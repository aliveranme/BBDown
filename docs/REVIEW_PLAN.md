# 代码审查修复排期（REVIEW_PLAN）

> 审查来源：R1/R2/R3/R4 四轮审查（安全/可读性/可靠性/韧性），2026-08 批次。
> 本文件跟踪**剩余未处理项**；已修复项见 git log（13766b0..2ab54d1 五连提交）与各代码注释。

## 状态总览

| 组 | 总数 | 已完成 | 剩余 |
|----|------|--------|------|
| A 安全 Infra | 7 | 7 | 0 |
| B 安全 Core | 3 | 2 | **1**（B3） |
| C 功能缺陷 | 3 | 3 | 0 |
| D 韧性 Infra | 10 | 10 | 0 |
| E 韧性 Core | 6 | 6 | 0 |
| F 测试 Infra | 12 | 6 | **6** |
| G 测试结构 | 10 | 4 | **6** |
| H 可读性 Infra | 13 | 1 | **12**（H11 同 C3 已修） |
| I 可读性 App/Core | 22 | 3 | **19**（I4 同 C1 已修、I21/I22 验证通过） |
| J CI/发布 | 4 | 2 | **2**（J1/J2 跟踪项） |
| **合计** | **90** | **44** | **46** |

---

## 第 1 轮：测试套件结构加固（防 CI 假绿/假失败，低风险高价值）

| 项 | 级别 | 位置 | 处理 |
|----|------|------|------|
| G1 | Critical | .github/workflows/pr.yml | ✅ 已落地（替代方案）：审查原建议 `xunit.runner.json 加 forbidOnly` 是 Playwright（JS `test.only`）概念，**xunit v2/v3 均无 forbidOnly/`[Only]`**（已查源码 ConfigReader_Json 与官方配置文档确认）。xunit 中“测试子集全绿”的等价残留是 `[Fact(Skip=...)]` 跳过不跑。已用 `xunit.runner.json` 的 `failSkips: true`（v2.5+，项目 v2.9.3 可用）把任何 Skip 当作硬失败；项目当前 0 个 Skip 零副作用，且已实测验证（注入临时 Skip 被报 FAIL） |
| G5 | Medium | DownloadPipelineTests.cs:848 | ✅ 墙钟断言改区间重叠断言（a 区间 ∩ b 区间必须重叠）——CI 调度抖动只影响总耗时不再误报 |
| G6 | Medium | RedirectHopValidationTests.cs:25,147 | ✅ 两处固定端口段（24000-26000/25000段）改 TestPort.Allocate() 动态端口 |
| G8 | Medium | HttpUtilRetryTests.cs 7 处 finally | ✅ 9 个测试全部改为捕获前值恢复（`var original = Config.Current` + finally 恢复）；补连接被拒用例（StatusCode=null 命中重试谓词，退避耗时下限证明重试发生） |
| G9 | Low | RedirectHopValidationTests.cs:120 | ✅ LocalRedirectServer 加 RequestCount 计数；断言请求数 ≤ maxHops+1（强证据：仅断言终值无法区分“截断返回”与“侥幸返回”） |
| G10 | Low | ServeApiHttpTests.cs:21,415 | ✅ BaseUrl 改动态端口（TestPort.Allocate 静态字段）；WaitForFinishedCountAsync 轮询耗尽抛带上下文 TimeoutException；Cancel 持久化断言带任务文件名/存在性上下文 |
| F9 | Medium | ServeApiSecurityTests | ✅ 新增 SanitizeUntrustedOptions_ClampsNumerics：上界（3/5000/120/30/64）+ 下界（RetryCount→1、MuxerTimeout→1、ThreadSegmentSize→1）共 10 断言 |
| F11 | Suggestion | ServeApiHttpTests.cs:1-40 | ✅ 补 [CollectionDefinition("ServeApiCollection")]；更新类注释（_taskFile 已实例字段注入，不再静态污染） |

## 第 2 轮：功能/韧性测试补齐

| 项 | 级别 | 位置 | 处理 |
|----|------|------|------|
| F6 | Medium | ExternalProcessRunnerTests.cs:50,68 | ✅ “KillsProcessTree” 两个测试升级为进程树哨兵验证：根进程派生持续写哨兵文件的子进程，超时/取消后验证哨兵文件停止增长（整棵进程树被杀）——替换此前只断言异常类型、杀根不杀子也通过的零证据断言；Unix 用 sh 后台子 shell / Windows 用 cmd+ping 重定向 |
| F7 | Medium | ExternalProcessRunnerTests.cs:160-201 | ✅ MergeFLV 假 runner 测试从“try/catch 吞异常”（抛/不抛都通过）改为确定性断言：Assert.ThrowsAsync<InvalidOperationException> + 消息含“保留源分段” + 假 runner 确实被调 + 源分段保留 |
| G7 | Medium | DownloadPipelineTests.cs:133-161 | ✅ 新增 3-clips SHA-256 用例：2.5MB 载荷/1MB 分片 → 服务端 Record RangeHeaders（3 段互补不重叠覆盖 [0,size)）+ 产物逐字节哈希一致 + 分片清理 + 锁释放 |
| F10 | Suggestion | LiveStreamUtil.cs | ✅ 补 2 个可稳定分支：非数字 roomId→ArgumentException（ResolveAsync 不发起网络请求）；零字节 EOF→删除空 seg + 退避重连续录（新 StreamMode.ZeroByte）。⚠️ LiveStreamWriteException 分支需要磁盘故障/只读文件系统，跨平台测试不可靠触发，保留人工验证 |
| F12 | Suggestion | 多处 | ✅ IsBlockedAddress CGNAT/ULA 经 IsSafeCallbackUrl 域名 DNS 分支直测（6 断言）；ProgressBar Dispose 结算新增 Test 文件（2 测试）；SubscriptionStore 幂等新增 5 测试（重复 Add/不存在 Remove/同 aid 去重/最近优先）。⚠️ LocalIntegration 缺 ffmpeg return 改 Skip 在 **xunit v2 无法实现**：`Assert.Skip` 动态跳过仅 v3 支持；静态 `[Fact(Skip)]` 编译期写死不能表达运行时缺 ffmpeg，且与 G1 的 `failSkips:true` 冲突（Skip 会变失败）——保留 return，待 v3 迁移时改为 Assert.Skip |

## 第 3 轮：B3 独立安全审查（Parser 签名/HTTP 层/AppHelper）

> ✅ 已完成（三路并行 reviewer 全产出，无子代理超时）。

### 结论
- **无 High、无当前可利用的 Medium**：签名链（WbiSign/GetSign/盐配对/时序）、HttpClient 池隔离（insecure↔校验池）、VerifiedAppHttpClient 不可降级、Cookie 携带边界、重定向逐跳校验、超时/取消分类、日志脱敏七面全部核实通过 ✅。
- 跨文件一致的根问题：**签名媒体 URL 脱敏在下载链路失效**（AppHelper/Parser 明确脱敏，下载器日志却明文落盘）。

### 已消纳修复（低风险高价值）
| 来源 | 处理 |
|------|------|
| Parser-M1 + AppHelper-F1 | ✅ SensitiveKeys 扩 `sign/x_sign/w_rid/deadline/marlin_token`；`BBDownDownloadUtil` 两处 Start downloading 日志改 SensitiveDataMasker.MaskUrl；+1 单测 |
| AppHelper-F4 | ✅ `item.StreamInfo?.Quality ?? 0` 防畸形帧 NRE（+S5 Size*8 checked 防 ulong 回绕） |
| Parser-Low-2 | ✅ WBI mixin key 日志改 MaskValue |

### 未消纳（记录，评估后决定）

> ✅ 已按后续加固批全部处理（见下“B3 补充加固已消纳”），下表保留为历史记录：
> - HTTP-L1/L2/S1、AppHelper-F2/F3/S1/S2、Parser-L3 已修复；
> - HTTP-S2/S3、AppHelper-S3/S4、Parser-L5 评估后维持现状（载荷非机密/已有更上游链路/改动风险>收益，见对应修改注释）。

| 来源 | 级别 | 位置 | 说明 |
|------|------|------|------|
| HTTP-L1 | Low | HTTPUtil.GetWebLocationAsync | ✅ 加使用约束 XML 注释（仅供硬编码可信 URL，禁用不可信输入） |
| HTTP-L2 | Low | HTTPUtil.CalibrateClock | ✅ 加 fromVerifiedPool 参数：--insecure 连接的 Date 头（中间人可控）不写全局时钟偏移，防跨流 WBI 扰动 |
| HTTP-S1 | Sugg. | GetWebSourceCoreAsync | ✅ Cookie 主机白名单纵深防御：sendCookie=true 前校验主机 ∈ 官方域 / 操作者配置的 Host / 回环，非可信主机拒绝外发凭据且不发任何网络请求 |
| HTTP-S2/S3 | Sugg. | webhook 客户端 | ⭕ 维持现状：CLI webhook 载荷非机密、自动跟随无害；serve 侧已有独立校验 client 收敛 |
| AppHelper-F2 | Low | GetPostResponseAsync | ✅ gRPC POST 改 NoRedirectClient + 3xx 显式拦截（禁止凭据/body 随 307/308 重放外发） |
| AppHelper-F3 | Low | BBDownDownloadUtil 媒体 Cookie | ✅ 新增 MediaDownloadClient（AllowAutoRedirect=false）替换 3 处媒体下载请求；--force-http 明文时 LogWarn 告警（无法强去 Cookie 否则 CDN 取流失败） |
| AppHelper-S1 | Sugg. | GzipDecompress | ✅ gzip 解压设 48MB 上限防解压炸弹（剔除输出超限内存占用） |
| AppHelper-S2 | Sugg. | ReadMessage 帧首字节 | ✅ gRPC 帧首字节显式校验（仅 0/1，其它抛 InvalidDataException 替代静默当未压缩） |
| AppHelper-S3 | Sugg. | token 日志首尾 | ⭕ 维持现状：保留首尾 4 字符是刻意设计（B1/B2 已验证通过，用于核对凭据身份） |
| AppHelper-S4 | Sugg. | 空 buvid 指纹 | ⭕ 维持现状：真实设备标识已由 BuvidProvider（buvid3）维护，AppHelper 空 buvid 是无关紧要历史字段；强行生成格式不符可能反触发风控 |
| Parser-L3 | Low | 异常消息回显 | ✅ SanitizeServerText 剥离控制字符再拼异常消息（防 ANSI/日志投毒注入面） |
| Parser-L5 | Low | tv/intl 时钟校准盲区 | ⭕ 维持现状：常规流程必有 web API 先行校准（审查结论“保持现状可接受”） |

### B3 补充加固新增测试
- ClockCalibrationTests：FromInsecurePool_DoesNotWriteGlobalOffset（L2）
- HttpUtilRetryTests：CookieNonTrustedHost_ThrowsBeforeNetwork / TrustedHost_WithCookie_Succeeds（S1）
- AppHelperMessageTests（新文件 5 测）：帧首字节非法/过短/gzip 往返/未压缩往返/解压上限（S1/S2）

## 第 4 轮：D8 HTTP 并发请求数上限

- ✅ 已落地：/get-tasks 族查询端点（`/`、`/running`、`/finished`、`/{id}`）经 MapGroup.AddEndpointFilter 加查询并发信号量（上限 8）——快照深拷贝是查询成本，槽位不足返回 429（与接受队列/认证限速同一语义，不排队堆积、客户端可重试）。新增 TryAcquireQuerySlot/AvailableQuerySlots 测试缝 + 端点测试（正常 200 → 占满 8 槽 → 列表与子路径均 429）

## 第 5 轮：H 组可读性重构（Infrastructure）

| 项 | 级别 | 位置 | 处理 |
|----|------|------|------|
| H1 | High | BBDownApiServer.cs 全文件 | God 类拆 ServeSecurityMiddleware / TaskRouteMapper / TaskFileStore / CallbackGuard；SetUpServer ~200 行 lambda 内联无法单测 |
| H2 | High | BBDownMuxer.cs:64,174 | MuxAV 20 参 / MuxByMp4box 15 参改 MuxRequest 参数对象 |
| H3 | High | BBDownDownloadUtil.cs:28 | RangeDownloadToTmpAsync 10 参 → RangeDownloadRequest + 拆两段 |
| H4 | High | BBDownDownloadUtil.cs:227,611 | Core 170/200 行嵌套 6-7 层：预检决策方法 + DownloadClipWithRetryAsync；6 个"检查 .tmp/.aria2"块收敛 |
| H5 | High | 多处 | 重复簇抽 6 个辅助方法（任务收尾四元组 ×4、IsLoopback 判定、SSRF 字面 IP ×2、DNS+逐地址校验 ×3、头块 ×3、权威大小复核 ×3、clip 路径推导 ×4） |
| H6 | Medium | LiveStreamUtil.cs:75,222,286 | 异常消息文本契约改 LiveRoomClosedException 专用异常 |
| H7 | Medium | 多处 | ✅ 死代码逐条删除（BBDownUtil.GetFiles、UrlResolver.MdRegex、GetAvIdAsync 无 token 重载、空 WriteLine ×2、NormalizeLockKey 上方孤立 doc 归位到 AcquireDownloadLock；CommandLineSplitter 保留——其位与为非短路是有意语义已加注释） |
| H8 | Medium | 多处 | 误导性命名：ReadLinesThrottled、_savePathLock、MyOptionBindingResult<T>、QualityName 档位映射顺序、nowId |
| H9 | Medium | 多处 | 魔法数字集中常量（关停 30s/回调 2min/1048576/复核 15s/分片并发 8/退避 3000*2^n/完整性 0.8/FLV 常量 13 个） |
| H10 | Medium | SubscriptionStore.cs:110-149,205 | 同一历史文件两套异常语义：抽 ReadHistoryLocked() 单入口 |
| H12 | Low | 多处 | ✅ recevied 拼写修正、bool & bool 加非短路注释、SetUpServer→SetupServer 改名（1 定义+4 调用） |
| H13 | Low | LiveStreamUtil.cs:57,235 | ✅ ResolveAsync 5 元组 → sealed record LiveStreamInfo；LiveCommand/LiveStreamUtil 内部/测试三处消费改按名访问 |

## 第 6 轮：I 组可读性重构（应用层 + Core 结构）

| 项 | 级别 | 位置 | 处理 |
|----|------|------|------|
| I1 | High | Download.cs:326-843 | DownloadPageAsync ~520 行 god 方法拆 4 helper + dash/flv 子方法；弹幕块 55 行重复、CoverOnly 重复、"已存在跳过"×3、"空 aid 目录删除"×5 收敛 |
| I2 | High | Parser.cs:106-540 | ExtractTracksAsync ~430 行：PickDataRoot/PickTrackBaseUrl 纯函数 + ApiMode 枚举；数据节点定位 3 份漂移变体收敛 |
| I3 | High | BBDownUtil.cs:167 vs Parser.cs:680 | GetSign MD5 盐 ×2、appkey ×2、GetTimeStamp(bool bflag) ×2 集中 BiliApiKeys 常量 + 单份实现 |
| I5 | Medium | Workflow.cs:15-16 起 | SetUpWork 10 元组 → DownloadContext record；4 层透传参数收敛 |
| I6 | Medium | BBDownLoginUtil.cs:69-316 | LoginWEB/LoginTV 复制收敛 2 helper + QrPollCode 常量组（86038/86101/86090/86039） |
| I7 | Medium | 6+ 处 | 异常过滤器 or-链逐字重复抽 IsRetryableDownloadException(Exception) |
| I8 | Medium | 4 个命令 | Task.Run(...).GetAwaiter().GetResult() async-over-sync 改 AsyncCommand + ExitCodeFor |
| I9 | Medium | SubCommand.cs:49-81 + WatchLaterCommand.cs:13-45 | 两个 Settings 类复制 8 个下载选项 + 两份 BuildOption 抽公共基类 |
| I10 | Medium | BBDownUtil.cs 全文件 | god 工具类按职责拆分（更新检查/文件/签名/TV 指纹/章节/WBI/SESSDATA） |
| I11 | Medium | Config.cs:61-84 | 门面双命名体系统一 PascalCase |
| I12 | Medium | UrlResolver.cs:15-180 | ResolveAsync 200 行 13 分支拆 ResolveHttpUrl/ResolveBareId + 改名 target |
| I13 | Medium | Entity.cs:37-93 | Page 阶梯构造器（8/9/10/12 参）改无参构造 + 初始化器 + 属性 |
| I14 | Medium | AppHelper.cs:448 vs Entity.cs:203 | 同名 AudioMaterial 冲突：DTO 改名 AppRoleAudioDto |
| I15 | Medium | Display.cs | XML 文档挂错方法归位；.Replace("[] ", "") hack ×4；带宽估算公式 ×6 抽 EstimatedBytes；bool video 参数 |
| I16 | Medium | BBDownConfigParser.cs:83-209 | MergeWithConfig 130 行 4 次手工扫参收敛 SkipOptionValue + 静态缓存 BuildAliasMap |
| I17 | Low | 5 处 | ✅ 死代码全部删除（BBDownLoginUtil 注释 Log、BBDownUtil.GetFiles、UrlResolver.MdRegex、GetAvIdAsync 无 token 重载、Pages.cs 末尾悬空 XML 注释） |
| I18 | Low | 多处 | ✅ 魔法数具名（日志 JSON 摘要 1024→LogJsonSummaryMaxChars ×3、Task.Delay(200)→FileHandleReleaseDelayMs、DrmTechType==2/QR GetGraphic(7)×2/86400 加注释或常量） |
| I19 | Low | 3 处 | ✅ 注释残余清理（外部编号引用改自描述、Parser 硬编码行号去掉、连续 ThrowIfCancellationRequested 去重） |
| I20 | Low | 多处 | ✅ 命名/文档歧义（aidOri 注释说明、--skip-ai 描述明确、Page.bvid fallback 加语义注释；--bandwith-ascending 拼写/语法兼容保留） |

## 第 7 轮：CI 跟踪项

| 项 | 级别 | 位置 | 处理 |
|----|------|------|------|
| J1 | Observation | release.yml | ubuntu:18.04 已 EOL（glibc 2.27 兼容是刻意选择），跟踪 apt 源长期可用性；必要时迁移容器基镜像 |
| J2 | Observation | 所有 workflow | Actions 固定 major 版本（@v4/@v5/@v6）非完整 SHA；有 Dependabot 周更兜底可接受，严格供应链可升级 SHA 固定 |

---

## 第 8 轮：续审（2026-08-29，MAINTENANCE_PLAN 批次验收 + 未深查区域扫查）

> 本轮为维护计划两批次落地后的续审。新发现登记于 REVIEW_FINDINGS.md（RF-5/RF-6/RF-7）。

| 项 | 结果 |
|----|------|
| 基线 | ✅ dotnet build Release 0 警告 0 错误；单测 634/634 全绿（PR gate 过滤器） |
| 批次二文档分支验收 | ✅ `docs/serve-security-options-sync`（e726c4c）对照 9 个改动点逐一核实：README 三处+矛盾表述修正、API.md 两条、wiki 参数表/忽略清单/反代提示全部就位；忽略清单与 `SanitizeUntrustedOptions`（BBDownApiServer.cs:697）17 字段逐一比对一致，文案与 ServeCommand.cs 源码描述一致；无反代的 Docker 示例未误加 `--trusted-proxy`。可提 PR。余项：CHANGELOG 条目随下次发版、合入后手动跑 `scripts/sync-wiki.ps1` |
| 批次一护栏代码审查 | ✅ FakeBilibiliApiServer（锁/Dispose/404 快速失败）、ParserFixtureTests（G8 卫生约定、15 用例断言真实消费节点）、Parser.cs `WithApiScheme` 开缝（默认配置行为逐字节不变）均无缺陷 |
| 新一轮深查 | SubUtil / DanmakuUtil / BBDownMuxer / ExternalProcessRunner / BBDownConfigParser / Program.cs：无 High/Medium；实证排除两个疑点（SubUtil:278 三字符 `\\/` 替换正确匹配 intl 双重转义；JSON 序列化 6 个 source-gen 上下文全覆盖无 AOT 缺口）；产出 3 个 Low（RF-5 culture 时间格式、RF-6 mp4box cover 转义一致性、RF-7 cliHasUrl 误报） |

## 第 9 轮：消纳挂起发现 RF-4/RF-5/RF-6/RF-7（2026-08-29）

> 将 REVIEW_FINDINGS.md 中四个 ⏳ 待议的 Low 级发现一次性落地（分支 `fix/review-r9-pending-findings`）。剩余挂起仅 RF-2（AsyncCommand 批量迁移，技术债待排期）；RF-3 经评估维持现状。

| 项 | 处理 |
|----|------|
| RF-5 | ✅ `BBDownMuxer` ffmpeg `creation_time` 格式化追加 `CultureInfo.InvariantCulture`（自定义格式的 `:` 是 culture 时间分隔符占位符，fi-FI 等区域设置下产出非 ISO-8601 串，`av_parse_time` 解析失败后发布时间元数据静默丢失） |
| RF-6 | ✅ `MuxByMp4box` itags `cover` 值补 `EscapeString(pic)`（Windows 路径天然含 `\`，与同函数其它 itags 值的转义规则对齐，杜比视界自动切 mp4box 时封面不再静默丢失） |
| RF-4 | ✅ 新增 `HTTPUtil.VerifiedNoRedirectClient`（始终校验证书 + `AllowAutoRedirect=false`，独立池不受 `--insecure` 降级）；`WidevineCdm` 许可证 POST 切换并在 3xx 显式拦截（与 gRPC POST B3-F2 收口同构，签名 challenge 不随 307/308 重放外发） |
| RF-7 | ✅ `BBDownConfigParser` 新增 `GetPositionalTokens`（与 `IsSubCommandInvocation` 同构跳值），`cliHasUrl` 只对位置参数应用 URL 启发式——`--aria2c-proxy http://...`、`--work-dir av123` 等 URL 形值选项不再压制配置文件里的 URL |
| 测试 | ✅ 新增 6 例（总计 640）：VerifiedNoRedirectClient 身份稳定/GET 307 不跟随（含自动跳转对照）/POST+body 307 不重放；ConfigMerge 两个 URL 形值选项回归 + 位置参数提取器语义 |
| 基线 | ✅ dotnet build Release 0 警告 0 错误；单测 640/640 全绿（PR gate 过滤器）；dotnet format --verify-no-changes 通过 |

---

## 已完成批次（git log 13766b0..2ab54d1，2026-08）

1. **13766b0** Core 韧性：字幕 TimeoutException 降级（E1）、Widevine 许可证有界重试+超时分类（B2/E2）、重定向 GET 重试（E3）、fetcher code 诊断（E4/E5）、Logger.LogStack（E6）
2. **2cff36c** serve 安全：ForceHttp 清零（A1）、trusted-proxy XFF（A2）、数值 Clamp 慢速 DoS（A3）、RetryCount [1,3]（A4/D5）、日志单行化（A7）、DnsSafeHost（C2）、token 环境变量（A6）、持久化/加载/webhook 日志升级与重启提示/关停枚举 JobId（D2/D3/D4/D7）
3. **df4eba9** 下载/直播/订阅韧性：VOD 读停滞看门狗（D1）、分片扩展名大小写统一（C3/H11）、直播短段退避（D6）、管道成功路径超时（D9）、订阅历史有界（D10）、itags CRLF 转义（A5）
4. **e59402c** 应用层：LATEST 全词匹配（C1/I4）、Download.cs 字幕 TimeoutException、Download.cs 662 处缩进对齐（format 门禁修复）
5. **2ab54d1** 测试：CSRF/认证限速/cancel 端点测试（F1/F2/F3/G2）、413 契约对齐（F8）、看门狗 per-call 注入（F5）、程序集级串行（G3/G4）、ExpandPageAliases 测试（C1）

另：B1 WidevineCrypto 亲验无问题；I21/I22 亲验无问题；J3 设计合理；J4 全部 CI/依赖验证通过。
