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
| H1 | High | BBDownApiServer.cs 全文件 | God 类拆 ServeSecurityMiddleware / TaskRouteMapper / TaskFileStore / CallbackGuard；SetUpServer ~200 行 lambda 内联无法单测。⚠️ **第 12 轮勘误**：四个拆分文件在 git 全历史中从未存在（零提交记录），功能实际仍集中在本文件（现 1543 行）——本条记录失实，文件结构上 god 类未拆分；功能层面（中间件/持久化/回调防护）已实现并经审查通过 |
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

## 第 10 轮：RF-2 一次性批量重构（AsyncCommand 迁移，2026-08-29）

> 消纳 REVIEW_FINDINGS 最后一个 ⏳ 挂起项 RF-2（分支 `refactor/rf2-async-command-migration`）。至此 FINDINGS 全部条目结转完毕（其余为"已修复"或"维持现状"）。

| 项 | 处理 |
|----|------|
| RF-2 | ✅ 7 个命令迁移 `AsyncCommand<TSettings>`（Login/LoginTV/Article/Live/SubCheck/WatchLater/Serve；原清单含 DefaultCommand，核实已迁移过，漂移修正）；serve 链路 `Run` → `ValidateListenUrl` + `RunAsync`、`StartServer` → `StartServerAsync`，serve 全程 await 不再占用线程池线程阻塞等待；各命令 catch/退出码语义逐字保留，`ExitCodeFor` 评估后不抽取（各命令取消/超时/部分失败语义不同，见 FINDINGS RF-2） |
| 测试 | ✅ ServeApiHttpTests 适配：RunningServer 直用 `RunAsync`、NonLoopbackListen 改断 `ValidateListenUrl` 同步异常语义；单测 640/640 全绿（PR gate 过滤器）+ LocalIntegration 3/3 |
| 基线 | ✅ dotnet build Release 0 警告 0 错误；dotnet format --verify-no-changes 通过；serve 冒烟：`--help` 退出码 0、serve 监听回环、`/get-tasks/running` 200 |

## 第 11 轮：RF-2 迁移后全库续审 + 新发现消纳（2026-08-30）

> 本轮为 RF-2 合入后的续审：核实迁移质量 + 四路并行深查（下载管线 / serve 服务 / Core 解析网络层 / 测试套件），产出 8 个新发现（3 Medium + 5 Low，登记 REVIEW_FINDINGS RF-8..RF-13）并**全部修复**（含回归测试）。

| 项 | 处理 |
|----|------|
| RF-2 迁移后核实 | ✅ 8 命令 AsyncCommand 语义逐字保留、serve `ValidateListenUrl`+`RunAsync` 拆分合理、无残留 async-over-sync 阻塞（仅 ExternalToolHelper 短进程探针例外，文档已说明）；基线 build 0 警告 0 错误、单测 640/640、format 通过 |
| serve 冒烟 | ✅ 回环启动 `/get-tasks/running`+`/finished` 200；非回环无 token 拒绝启动且退出码 1（遗留 serve 进程 31152 锁 DLL，已确认来源并终止） |
| RF-8 | ✅ Download.cs FLV 跳过路径清理与 DASH 分支对齐（封面/字幕/章节），fastSkipChecked 路径补章节清理；新增 `DeleteResidualChapterFiles` 按前缀兜底清理（muxer 写 `chapters-{basename}` 唯一名，旧清理只删固定名 `chapters` 是预存不一致）；+2 测试 |
| RF-9 | ✅ BBDownApiServer 认证失败限速字典超过 `MaxTrackedAuthFailureIps` 时按最后失败时间裁剪（仅删过期条目约束不住新 IP 轰炸）；+1 测试（反射验证字典有界） |
| RF-10 | ✅ `TrimFinishedTasksLocked` 溢出裁剪按 `TaskCreateTime` 保留最新（旧 `RemoveRange(0,...)` 按完成顺序误删"后创建先完成"任务）；+1 测试 |
| RF-11 | ✅ Parser 大会员回退 host 改用 `Config.Current.EpHost`（镜像站可用）；回退判定改解析 JSON `message` 字段（子串匹配仅作非 JSON 兜底，防 B 站改文案失效）；+2 测试 |
| RF-12 | ✅ `BaseUrlRegex` 收紧为 `^https?://[^/:]+:\d+`（query 中 `:数字` 不再误判为端口）；+1 测试 |
| RF-13 | ✅ `GetWebSourceWithSetCookiesAsync`（登录轮询）改 `NoRedirectClient` 手动逐跳 + 每跳 `IsTrustedCookieHost` 校验（与 gRPC/Widevine 收口同构，凭据与响应 Set-Cookie 不外发非可信主机），上限 `MaxRedirectHops=10`；+2 测试 |
| 基线 | ✅ dotnet build Release 0 警告 0 错误；单测 659/659 全绿（PR gate 过滤器，新增 19 例）；dotnet format --verify-no-changes 通过 |

---

## 第 12 轮：全库完整续审（2026-08-30）

> 四路并行深查（下载管线 / serve 服务 / Core 解析网络层 / 测试与 CI 合规）+ Medium 级发现逐项人工核实。新发现 3 Medium + 16 Low + 若干 Info，**仅登记评估未修复**，全部登记 REVIEW_FINDINGS（RF-14..RF-29）；Info 级观察不登记 RF，见下表末行。

| 项 | 结果 |
|----|------|
| 基线 | ✅ dotnet build Release 0 警告 0 错误；单测 659/659 全绿（PR gate 过滤器）；无 Skip 残留（failSkips 在位）；CI 门禁与 AGENTS.md 逐字一致；AOT source-gen JSON 四上下文覆盖完整；RF-1~RF-13 修复质量抽查通过 |
| RF-14 (M) | 下载管线两级 catch 过滤器缺口：`NotSupportedException`（CDN 忽略 Range，BBDownDownloadUtil.cs:797 刻意抛出）/`ArgumentException`/`AggregateException` 逃逸 Download.cs:94/:1067 白名单 → 整批中止、丢 webhook/failedPages。建议 :797 改抛 InvalidOperationException + 过滤器补 AggregateException |
| RF-15 (M) | serve 读端点无 Host 校验：默认无 token 部署下 DNS rebinding 可读 `/get-tasks*`（响应含 SavePaths 绝对路径）；写端点因 POST 必带 Origin 已受保护，读端点同源 GET 不发 Origin，须 Host 白名单收口 |
| RF-16 (M) | 文档正确性族：wiki 退出码表虚构 2/3 且 Ctrl+C 归 0 与实现不符（实况：充电专属跳过→0、工具缺失→1、默认命令 Ctrl+C→130）；CLI-Reference 缺 --host/--ep-host/--tv-host/--area 4 项；README:333 serve 选项列举不完整 |
| RF-17 | Parser 免二压重发两处 catch（:324/:526）吞用户取消——TaskCanceledException 无 `!token.IsCancellationRequested` 守卫，:518 注释前提错误（SendAsync 用户取消抛的正是 TaskCanceledException） |
| RF-18 | 服务器可控 lan/audio_id 未净化直拼文件路径（SubUtil 5 处 + Parser:502 + Download:444，ResolveWorkPath 只 Combine 不过滤）；附带 SubOnly 无条件改名 .srt（ASS 内容产物扩展名错误） |
| RF-19 | publishDate/videoDate 占位符 CurrentCulture 格式化（`:` 占位符）且替换值未过 GetValidFileName（Windows `:` ADS 陷阱/跨机路径漂移）——Program.cs:48 + PathHelper.cs:67-68 |
| RF-20 | 跳过路径清理一致性残留：锁内权威 Skipped 分支（Download.cs:208-225）漏 coverPath；dash 跳过路径多处裸 File.Delete/Directory.Delete 与 flv 包裹版不一致（RF-8 修复族漏网） |
| RF-21 | aria2c stdin input-file 的 URL/Cookie 未滤 `\r\n`，可注入 `all-proxy`/`dir`/新 URI 指令行（BBDownAria2c.cs:45-50） |
| RF-22 | 进程执行边界：CheckFFmpegDOVI 探针同步阻塞 + 超时分支未观察 outTask/errTask（ExternalToolHelper.cs:26-33）；ExternalProcessRunner 成功路径 5s 管道兜底可把退出码 0 翻转为 TimeoutException（:90-92，先确认是否有意） |
| RF-23 | 混流事务化 `.muxing-{guid}` 未知扩展名直接作为 mp4box `-new` 输出参数，GPAC 按扩展名推断容器，新版行为需实测（Download.cs:236 + BBDownMuxer.cs:184） |
| RF-24 | SanitizeUntrustedOptions 漏 `interactive` → serve 任务阻塞 Console.ReadLine 占死并发槽且 /cancel 无法中断（一行清零修复） |
| RF-25 | 解析失败日志两处 option.Url 未过 SanitizeLogString（BBDownApiServer.cs:1091/:1120），客户端可控 CRLF 日志注入残留 |
| RF-26 | Core 低危族 5 小项：免二压降级音频重复追加（缺去重）、PGC gRPC Host 头与目标不符、FavList 翻页无空页保护、IntlBangumi 多余 `\/` Replace、`x/player/wbi/v2` 两处未签名（未记录行为依赖） |
| RF-27 | FindBinaries 写进程级静态工具路径——核实 serve 下 SanitizeUntrustedOptions:737-740 已清零路径字段，覆盖场景实际不可达，倾向维持现状（待议） |
| RF-28 | HTTP 响应体无大小上限（gRPC POST 二进制与普通响应字符串层；gzip 侧 48MB 上限未覆盖本体） |
| RF-29 | .editorconfig 存量违规 4 文件（两个 csproj BOM、Tests csproj 与两个 github yml 缺末尾换行；format 门禁不检查 BOM/insert_final_newline）——已逐字节验证 |
| 勘误 R-1 | 第 5 轮 H1 记录失实：ServeSecurityMiddleware/TaskRouteMapper/TaskFileStore/CallbackGuard 四文件在 git 全历史中从未存在，功能仍集中于 BBDownApiServer.cs（1543 行）；已在 H1 行加注 |
| Info 级观察（不登记 RF） | webhook payload 页数用全量分 P 数非选中数；AddSavePath 无去重（Download.cs:211+1063 重复记录）；serve 响应缺 Cache-Control: no-store / 429 缺 Retry-After；任务持久化 tmp 固定名多实例互踩（SubscriptionStore 已用 GUID tmp 未对齐）；番剧 pub_time 无时区串按本机时区解析；HTTPUtil 重定向超限抛 HttpRequestException（StatusCode=null）命中重试谓词整流程重试；WbiSign 未按规范排序/过滤保留字符（当前可用）；TestPort.Allocate 理论 TOCTOU；Windows 哨兵 0.6s 采样窗口可能漏报进程树未杀；ClockCalibrationTests expected 基准应在调用前取；.dockerignore 未排除 .git/Tests/docs（context 偏大）；sync-wiki.ps1 未查 $LASTEXITCODE、不清理 wiki 旧页面、无 try/finally；根目录 .tmp 已被 gitignore 且未跟踪（无需处理） |
| 无新发现面 | HttpClient 池隔离（verified/insecure×redirect/no-redirect×media 全独立）、WBI 密钥链、JsonDocument 生命周期、protobuf 帧边界、路径锁机制、LiveStreamUtil 录制循环、webhook SSRF 三重防护、持久化 tmp+flush+rename 原子性、锁序一致性、测试共享状态恢复（try/finally 快照）、Collection 序列化声明闭合、Dockerfile AOT、secrets、proto 生成纪律、serve 参数文档与代码一致性、SECURITY/CONTRIBUTING 完备性 |

---

## 第 12 轮附：消纳批 RF-14~RF-29（2026-08-30）

> 消纳第 12 轮登记的发现（分支 `fix/review-r12-findings`）。15 项修复落地（含补遗 RF-23，临时名补 `.mp4` 后缀无需 GPAC 实测即定案）；RF-27 维持现状定案。

| 项 | 处理 |
|----|------|
| RF-14 | ✅ `MultiThreadDownloadCoreAsync` 抛出点 `NotSupportedException`→`InvalidOperationException`（消息不变），Download.cs 两级 catch 过滤器补 `AggregateException`——"CDN 忽略 Range"不再中止整批/丢 webhook |
| RF-15 | ✅ 无 token 时 `isApi` 强制 Host 为字面回环（新增 `internal IsLoopbackHost`：localhost/127/8/::1，刻意不做 DNS 解析防 rebinding 绕过）；有 token 时跳过校验保反代/自定义域名部署；+3 端点测试（evil Host 读写 403 / 回环放行 / 带 token 跳过）+1 纯函数测试 |
| RF-16 | ✅ 文档修正：wiki 退出码表删虚构 2/3 行、Ctrl+C 改"主命令 130 / 子命令 0"、工具缺失归入 1；总表补 `--host`/`--ep-host`/`--tv-host`/`--area`（语义取自 MyOption Description）；README:333 serve 选项列举补 `--trusted-proxy`/`--notify-webhook` |
| RF-17 | ✅ Parser 免二压两处 catch 前补 `catch (OperationCanceledException) when (token.IsCancellationRequested) throw;`（SendAsync 用户取消抛的正是 TaskCanceledException），并修正 :518 错误注释 |
| RF-18 | ✅ SubUtil 新增 `BuildSubtitlePath` 统一 5 处 lan 走 `GetValidFileName`；Parser `audio_id` 同款净化；SubOnly 目标扩展名按源内容形态（.ass 保留）+ lan 净化 |
| RF-19 | ✅ `FormatTimeStamp` 追加 `CultureInfo.InvariantCulture`（与 RF-5 同构）；PathHelper publishDate/videoDate 替换值过 `GetValidFileName`；`FormatSavePath` 改 internal 可测；+1 测试（fi-FI 区域下断言文化无关与 `:` 净化） |
| RF-20 | ✅ 锁内权威 Skipped 分支补 coverPath 清理；dash 分支裸删全部包裹（封面 1 处、弹幕 XML 3 处、aid 目录 3 处、flv 弹幕 2 处对齐），统一 `catch (IOException or UnauthorizedAccessException)` |
| RF-21 | ✅ aria2c stdin 的 URL/Cookie 写入前剥离 CR/LF（注入的指令行语义消除，畸形 URI 由退出码校验兜底）；+1 测试（注入 cookie/双 URI 场景断言单行化） |
| RF-22 | ✅ `CheckFFmpegDOVI` 改真异步 `CheckFFmpegDOVIAsync`（WaitForExitAsync+WaitAsync 5s；超时分支补观察管道任务防 UnobservedTaskException），调用点改 await；ExternalProcessRunner 成功路径 5s 兜底 ⭕ 维持现状（有意设计，注释在位） |
| RF-23 | ✅ （补遗）混流临时名改为 `.muxing-{guid:N}.mp4`——不再依赖 GPAC 对未知扩展名的容忍度（新版 filter-based MP4Box 按扩展名推断输出封装），新旧版本全部确定性走 ISOM；ffmpeg 分支 `-f mp4` 强制格式不受影响；`muxingPath` 仅精确路径引用，改动零波及 |
| RF-24 | ✅ `SanitizeUntrustedOptions` 补 `req.Interactive = false`（阻塞占死并发槽面消除）；既有 ClearsExecutionFields 测试补断言 |
| RF-25 | ✅ 解析失败日志两处 `option.Url`（及异常消息）过 `SanitizeLogString` |
| RF-26 | ✅ 5 小项全落地：① 免二压降级 dolby/flac 重复追加——applied 标志守卫 + 新文档接管时重置；② AppHelper GetHeader 移除硬编码 `Host: grpc.biliapi.net`（由 HttpClient 按 URI 生成，番剧 gRPC SNI/Host 不再错位）；③ FavListFetcher 翻页空页 break（对齐其余 fetcher 停滞语义）；④ IntlBangumiInfoFetcher 删除多余 `.Replace("\\/","/")`；⑤ `x/player/wbi/v2` 两处（SubUtil/BBDownUtil）登录态补 WbiSign（aid/cid/wts 升序），未登录保持无签名 |
| RF-27 | ⭕ 维持现状定案（详见 FINDINGS） |
| RF-28 | ✅ HTTPUtil 新增 `MaxResponseBodyBytes`（64MB）+ `ReadContentBoundedAsync`（Content-Length 预检 + 逐块累计双拦截）替换 `ReadAsStringAsync`/`ReadAsByteArrayAsync`，`DecodeBodyBytes` 按 charset 解码；+1 判定函数测试（64MB 上限无法廉价构造真实响应） |
| RF-29 | ✅ 4 文件去 BOM/补末尾换行（BBDown.Core.csproj、BBDown.Tests.csproj、codeql.yml、dependabot.yml） |
| Info 级观察 | ✅ 消纳 8 项：AddSavePath 去重（Skipped+成功双记导致 API 快照重复）；serve API 响应补 `Cache-Control: no-store`/`X-Content-Type-Options`、429 补 `Retry-After: 60`；任务持久化 tmp 名带 GUID（对齐 SubscriptionStore，多实例同目录不再互踩）；HTTPUtil 重定向超限改抛 InvalidOperationException（HttpRequestException StatusCode=null 误命中重试谓词）；TestPort.Allocate 进程内去重（TOCTOU 偶发 AddressAlreadyInUse）；ClockCalibrationTests expected 基准移到调用前；.dockerignore 补 .git/Tests/docs 等排除（context 瘦身）；sync-wiki.ps1 健壮化（$LASTEXITCODE 检查、废弃页面清理、try/finally 回目录）。⭕ 维持观察 4 项：webhook 页数语义（需产品决策）、番剧 pub_time 本机时区解析、WbiSign 未排序（当前可用）、Windows 哨兵采样窗口（漏报仅影响测试证据强度） |
| 基线 | ✅ dotnet build Release 0 警告 0 错误；单测 666/666 全绿（+7）；LocalIntegration 3/3；serve Host 校验不破坏既有回环用例 |

---

## 已完成批次（git log 13766b0..2ab54d1，2026-08）

1. **13766b0** Core 韧性：字幕 TimeoutException 降级（E1）、Widevine 许可证有界重试+超时分类（B2/E2）、重定向 GET 重试（E3）、fetcher code 诊断（E4/E5）、Logger.LogStack（E6）
2. **2cff36c** serve 安全：ForceHttp 清零（A1）、trusted-proxy XFF（A2）、数值 Clamp 慢速 DoS（A3）、RetryCount [1,3]（A4/D5）、日志单行化（A7）、DnsSafeHost（C2）、token 环境变量（A6）、持久化/加载/webhook 日志升级与重启提示/关停枚举 JobId（D2/D3/D4/D7）
3. **df4eba9** 下载/直播/订阅韧性：VOD 读停滞看门狗（D1）、分片扩展名大小写统一（C3/H11）、直播短段退避（D6）、管道成功路径超时（D9）、订阅历史有界（D10）、itags CRLF 转义（A5）
4. **e59402c** 应用层：LATEST 全词匹配（C1/I4）、Download.cs 字幕 TimeoutException、Download.cs 662 处缩进对齐（format 门禁修复）
5. **2ab54d1** 测试：CSRF/认证限速/cancel 端点测试（F1/F2/F3/G2）、413 契约对齐（F8）、看门狗 per-call 注入（F5）、程序集级串行（G3/G4）、ExpandPageAliases 测试（C1）

另：B1 WidevineCrypto 亲验无问题；I21/I22 亲验无问题；J3 设计合理；J4 全部 CI/依赖验证通过。
