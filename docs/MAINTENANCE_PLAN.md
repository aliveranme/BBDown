# 维护计划（MAINTENANCE_PLAN）：Parser 护栏测试 + serve 安全选项文档同步

> 创建时间：2026-08-27。依据：六维度实证评审（安全 9 / 测试 8.5 / 可靠性 8.5 / 流程 8 / 架构 7 / 代码质量 7）+ 三路取证。
> 项目语境：**个人维护的继承项目**（原作 nilaoda/BBDown）。REVIEW_PLAN 中 H/I 组约 31 项纯结构重构（god 方法拆分等）不作为目标；本计划只落两件对维护者自身有直接回报的事：
>
> 1. **Parser 解析层是全库最大测试盲区**——`ExtractTracksAsync` 约 500 行主干零覆盖（`BBDown.Tests/ParserTests.cs:6-10` 自注"需要真实/模拟响应，暂不覆盖"），恰是 B 站改接口形状时最先断裂、届时只能人肉对线上验证的模块。
> 2. **两个安全选项发布 8 天不在任何用户文档**——`--trusted-proxy` 与 `BBDOWN_SERVE_TOKEN`（均 v1.6.15 引入）仅存在于源码与 CHANGELOG；且 wiki 忽略字段清单与 `API.md` 权威口径分叉（缺 8 字段、多 1 个不实条目）。

## 非目标（明确不做）

- 不拆 `DownloadPageAsync` / `ExtractTracksAsync` / `BBDownApiServer` god 方法（H 组/I 组纯结构债）。
- 不做覆盖率度量基建、不动 HTTPUtil 连接池注入化。
- 第一版**不覆盖 appApi（gRPC）分支**与"大会员限制→网页源码正则回退"路径（前者需伪造 gRPC，成本不成比例；后者列为后续第二档）。
- 不引入 Kestrel TestServer / HttpMessageHandler 注入（全仓无先例，侵入刚加固过的连接池逻辑）。

---

## 批次一：Parser 解析层护栏测试

### 方案选型（取证结论，不再重议）

**本地假 B 站 API 服务器（HttpListener 回环监听）+ JSON 夹具回放。**

理由：

1. 这是本仓库唯一被反复验证的 HTTP 伪造手段——`HttpUtilRetryTests.ScriptedServer`（HttpUtilRetryTests.cs:344-393）、`LiveStreamUtilTests.FakeLiveServer`（LiveStreamUtilTests.cs:768-964）、`DownloadPipelineTests` 内 8 处本地服务器全部是 `HttpListener + TestPort.Allocate` 同构模式，照抄即可。
2. `HTTPUtil` 的 HttpClient 是 private static Lazy 固化池、零 handler 注入缝——handler 注入路线成本与风险最高。
3. 夹具即文档：下次 B 站改接口返回形状，红掉的用例直接指明断在哪个节点。

### 前置生产代码开缝（全计划唯一 Core 改动）

`BBDown.Core/Parser.cs:39`——UGC web 接口前缀硬编码 `"api.bilibili.com/x/player/wbi/playurl"`，改为引用 `Config.Current.Host`。

- 默认值本就是 `api.bilibili.com`（AppSettings.cs:11,13），**行为零变化**；
- 开缝后 UGC 可经 `Config.Apply(current with { Host = $"http://127.0.0.1:{port}" })` 定向到本地假服务器；
- 番剧/TV/INTL 三族本来就走 `Host/TvHost`，无需改动。

### 实施步骤

1. **FakeBilibiliApiServer**（测试项目内，仿 ScriptedServer）：按请求 path 分发登记的夹具内容、记录收到的 query 供断言 WBI 参数存在性、Dispose 收口。端口一律 `TestPort.Allocate()` 动态分配（防连接池串扰，DownloadPipelineTests.cs:19-31 先例）。
2. **夹具目录**：`BBDown.Tests/Fixtures/parser/*.json`，csproj 加 `<Content Include="Fixtures\**\*.json" CopyToOutputDirectory="PreserveNewest" />`（xunit.runner.json 有同款先例）。仓库当前无任何夹具 json，这是首套。
3. **测试类 `ParserFixtureTests`**：无 Category Trait（离线可重复，随 PR 单测 job 跑）。**严禁**标 `NetworkIntegration`（那是 CI 真网兜底通道）。
4. 卫生约定：每用例 `try/finally` 恢复 `Config.Current` 快照（G8 约定）；WBI 用例临时设 `Config.WBI` 并恢复（ParserTests.cs:63-78 先例）。

### 夹具集（锁住 ExtractTracksAsync 全部主干行为的 14 个场景）

| # | 夹具 | 模拟场景 | 关键断言 |
|---|------|---------|---------|
| F01 | ugc-web-dash.json | UGC data 根 DASH（fnval=4048 标准形状） | 轨道数量、id/qn、bandwidth/1000、codecid→AV1/HEVC/AVC 映射、res/fps 读入 |
| F02 | bangumi-web-dash.json | 番剧 result 根（`result.video_info` 变体各一份，F02a/F02b） | 数据根三段定位链正确、URL 走 `{Host}/pgc/player/web/v2/playurl` 且不带 w_rid |
| F03 | tv-dash.json | TV DASH（res/fps 应跳过） | `!tvApi` 分支下 res/fps 缺省不入轨 |
| F04/F05 | intl-code0/-code1.json | INTL 双次请求（code=0 视频 + code=1 补充流） | stream_list 合并、dash_audio 音轨产出 |
| F06 | flv-durl.json | FLV durl + accept_quality | Clips 逐段 url/size、Dfns 来自 accept_quality |
| F07 | tv-durl-qnxtras.json | TV durl + qn_extras | Dfns 改从 qn_extras 取 |
| F08 | drm-dash.json | DRM dash（bilidrm_uri 32hex + widevine_pssh） | KidHex/PsshBase64 正确提取；非法 kid 仅告警保持空 |
| F09 | dash-reparse-higher-qn.json | 二压重取第二轮接管 | 免二压 pass1 以 qn=127 重发且新文档接管 respJson/root |
| F10 | durl-replay-empty.json | durl 重发被拒/空数组退化 | 回退沿用首次响应、旧文档正确 Dispose |
| F11 | dolby-flac-audio.json | dash.dolby.audio[] + dash.flac.audio | 杜比多轨与 Hi-Res 单轨追加进音轨列表 |
| F12 | biz-error.json | 顶层 code≠0 | ThrowIfBizError 消息含服务端 message 且经 SanitizeServerText |
| F13 | play-limited.json | play_check.limit_play_reason = VIP_LIMIT | 抛出携带可读 reason 映射 |
| F14 | missing-nodes-tolerant.json | 缺 backup_url/dolby/flac 键 | EnumerateArraySafe 空序列容错、KeyNotFoundException 吞掉路径 |

录制来源：手工构造即可（`GetValueAsStringSafe` 对数字/字符串数字等价，夹具写法自由）；F13 已有真实报文样板（ParserPlayLimitTests.cs:11-21）。

### 验收标准

- [ ] 14 组夹具全部转绿并纳入 PR 单测 job；
- [ ] 生产 diff 仅 `Parser.cs:39` 一处（appApi/intl 相对接线不变）；
- [ ] **护栏自证**：临时把某夹具的一个消费字段改名（如 `codecid`→`codec_id`），至少 F01 红——证明断言真实消费了节点而非仅测"不抛异常"；
- [ ] `dotnet format BBDown.sln --verify-no-changes` 通过；新文件 LF/UTF-8（Windows 下注意，AGENTS.md 硬约束）。

---

## 批次二：serve 安全选项文档同步

### 改动点清单（均已核实到行号锚点）

**README.md（4 处）：**

1. `:157` 表尾（`--serve-token` 行后）新增一行：`--trusted-proxy`，描述沿用源码官方文案（ServeCommand.cs:26）："信任直连反代追加的 X-Forwarded-For（认证失败限速按客户真实 IP 计键）。仅在确有可信反代时启用，否则客户端可伪造 XFF 绕过限速"。
2. `:157` `--serve-token` 说明列扩写：优先使用环境变量 `BBDOWN_SERVE_TOKEN`（避免令牌出现在进程命令行；两者冲突时环境变量胜出并告警）。
3. `:159` 与 `:330` 两处安全提示 blockquote 各补一句 token 环境变量注入建议 / 反代场景提示。
4. **必须连带修正** `:332`——现文声称 serve 选项"不支持从环境变量读取"，与 1.6.15 引入 `BBDOWN_SERVE_TOKEN` 的实现（ServeCommand.cs:48-50）**直接矛盾**。改写为"`--serve-token` 支持经 `BBDOWN_SERVE_TOKEN` 注入（优先于 CLI 参数），其余选项仍仅支持命令行"。

**API.md（2 处）：**

5. `:150` `--serve-token` 注意事项条目尾部并入 `BBDOWN_SERVE_TOKEN` 一句。
6. `:151` 之后新增一条 `--trusted-proxy` 要点（含伪造 XFF 反面警告）；`:155-156` 反代相关两条附近可互引。

**docs/wiki/API-Server-and-Docker.md（3 处）：**

7. `:17-21` 参数表追加两行（`--trusted-proxy`、`--notify-webhook` 服务端用途）；`--serve-token` 行说明补环境变量。
8. `:34` 忽略清单整体替换为 `API.md:144` 权威口径：删除不实的 `ConfigFile`（SanitizeUntrustedOptions 从不清零它，BBDownApiServer.cs:697-738 逐赋值核实），补齐缺的 8 个（Aria2cProxy/WvdPath/Mp4decryptPath/NotifyWebhook/CallBackWebHook/UserAgent/FilePattern/MultiFilePattern），Host 家族改为 host/epHost/tvHost/uposHost 四元组白名单域表述。
9. 反代部署示例（`:25`/:144-165/:172-182 三处命令块）仅在带反向代理的示例中演示 `--trusted-proxy`（默认关闭）。

**流程：** CHANGELOG 条目随下次发版走（写入当版本"### 文档"小节，仿 ：240-244 纯文档句式）；wiki 合入主干后**手动跑 `scripts/sync-wiki.ps1`**（CI 无自动同步；脚本克隆 wiki 仓库 Copy-Item 覆盖后 push）。

### 验收标准

- [ ] `grep -r "trusted-proxy\|BBDOWN_SERVE_TOKEN" README.md API.md docs/wiki/` 在三处文档均有命中；
- [ ] wiki `:34` 清单与 `API.md:144` 字段级一致（17 清零字段 + host 四元组白名单）；
- [ ] README `:332` 矛盾表述已消除；
- [ ] CHANGELOG 记忆点已挂（或明确排入下次发版）。

---

## 提交与分支策略

遵循 CONTRIBUTING.md（master 受保护，Conventional Commits）：

```
test/parser-fixture-guardrails
  ├── refactor(core): UGC web playurl 主机引用 Config.Host（默认值不变，为本地夹具服务器开缝）
  └── test(parser): 新增 ExtractTracksAsync 夹具回放护栏（FakeBilibiliApiServer + 14 场景）

docs/serve-security-options-sync
  ├── docs: 补 --trusted-proxy 与 BBDOWN_SERVE_TOKEN 用户文档并修正环境变量表述
  └── docs(wiki): 忽略字段清单对齐 API.md 权威口径
```

## 工作量与顺序

| 批次 | 规模 | 建议 |
|------|------|------|
| 二（文档） | 9 个小改动点，约 1 小时（含 wiki 推送） | **先做**——纯低风险收割，顺手消掉一处用户可感知的错误声明 |
| 一（护栏） | FakeServer ~100 行 + 14 夹具 + ~15 用例，约半天 | 随后做；护栏自证一步别跳 |

两批次相互独立，各自独立 PR。
