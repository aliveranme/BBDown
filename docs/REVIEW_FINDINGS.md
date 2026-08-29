# 审查发现与建议跟踪（REVIEW_FINDINGS）

> **用途**：记录代码审查轮次中**经评估后需要决策 / 持续跟踪**的发现与建议（含已判定"不实施"的条目，避免后续重复评估）。
> **与 REVIEW_PLAN.md 互补**：`docs/REVIEW_PLAN.md` 跟踪"剩余未处理修复项"的排期；本文件跟踪"已分析、留有处置结论"的条目（采纳 / 技术债 / 维持现状 / 待议）。
> **创建时间**：2026-08-19（自 v1.6.14 起的审查轮次结项时建立）。

## 状态总览

| 编号 | 主题 | 评估级别 | 判定 | 状态 |
|------|------|---------|------|------|
| RF-1 | 分片扩展名判定一致性（`IsVideoClipPath`） | Low | 采纳（1 行修复） | ✅ 已修复（本轮） |
| RF-2 | CLI 命令层 Async-over-Sync 迁移 `AsyncCommand` | Medium（对应 REVIEW_PLAN I8） | 技术债，一次性批量重构 | ✅ 已修复（第 10 轮） |
| RF-3 | serve 已完成任务历史"无界堆积" | —（建议前提不成立） | 不实施（现有防护已覆盖） | ⭕ 维持现状 |
| RF-4 | Widevine 许可证请求跟随重定向 | Low（一致性） | 改进提议 | ✅ 已修复（第 9 轮） |
| RF-5 | ffmpeg `creation_time` 元数据格式文化敏感 | Low | 一行修复（InvariantCulture） | ✅ 已修复（第 9 轮） |
| RF-6 | mp4box `-itags` cover 值未走 EscapeString | Low（一致性） | 一行修复（补 EscapeString） | ✅ 已修复（第 9 轮） |
| RF-7 | 配置合并 cliHasUrl 把选项值误判为 URL | Low | 启发式收紧（仅扫描位置参数） | ✅ 已修复（第 9 轮） |
| RF-8 | FLV 跳过路径不清理封面/字幕/章节（残留累积） | Medium | 与 DASH 分支清理对齐 | ✅ 已修复（第 11 轮） |
| RF-9 | serve 认证失败限速字典无界增长 | Medium | 超上限按最后失败时间裁剪 | ✅ 已修复（第 11 轮） |
| RF-10 | serve 已完成任务溢出裁剪按完成顺序误删 | Low | 按 TaskCreateTime 保留最新 | ✅ 已修复（第 11 轮） |
| RF-11 | Parser 大会员回退硬编码域名 + 子串判定脆弱 | Medium | EpHost 配置化 + JSON message 解析 | ✅ 已修复（第 11 轮） |
| RF-12 | `BaseUrlRegex` 贪婪匹配误判 query 为端口 | Low | 正则收紧 | ✅ 已修复（第 11 轮） |
| RF-13 | 登录轮询跟随重定向缺逐跳校验（凭据外发面） | Low | NoRedirect + 每跳可信校验 | ✅ 已修复（第 11 轮） |

---

## RF-1：分片扩展名判定一致性（`IsVideoClipPath`）

- **位置**：`BBDown/Infrastructure/BBDownDownloadUtil.cs` — 轨道分片类型判定（`.vclip`/`.aclip`），收敛后统一入口 `IsVideoClipPath(path)`。
- **来源建议**：仅匹配 `.mp4` 导致非 DASH 容器（FLV/MKV/TS 等）下视频分片被误判为音频 `.aclip`，建议扩展后缀表或改用显式轨道类型参数。
- **分析**：
  - 实际触发面接近零：`IsVideoClipPath` 的输入是**轨道最终产物路径**，BBDown 输出固定为视频 `xxx.mp4`、音频 `xxx.m4a`（`Download.cs:365-366`）；非 DASH 的 FLV 分段也命名为 `{i}.mp4`（`Download.cs:962`），分类正确；项目不存在非 mp4 视频输出选项（无 `--mkv`）。
  - **但核实发现第 6 处漏网**：`DownloadClipsAsync` 返回值构造处（原 844 行）仍用区分大小写的 `Path.GetExtension(path).EndsWith(".mp4")`，与 `IsVideoClipPath`（`OrdinalIgnoreCase`）行为不一致——是"统一 5 处大小写"重构时遗漏的一处。当前产品不会产出大写 `.MP4` 文件名故无运行时错位，属代码一致性问题。
- **结论**：采纳一行修复（走 `IsVideoClipPath`）；来源建议的"扩展后缀表"不采纳（对不存在的场景过度设计），未来若引入多容器输出，按建议后半句**改用显式轨道类型参数**而非格式猜测。
- **状态**：✅ 已修复（2026-08-19，844 行改为 `IsVideoClipPath(path)`，全库 5 处判定统一，无残留大小写敏感判断）。

---

## RF-2：CLI 命令层 Async-over-Sync 迁移 `AsyncCommand`

- **位置**：8 处命令 `Task.Run(...).GetAwaiter().GetResult()` — `LoginCommand`、`LoginTVCommand`、`ArticleCommand`、`LiveCommand`、`SubCommand`、`WatchLaterCommand`、`DefaultCommand`、`ServeCommand`；对应 REVIEW_PLAN I8。
- **来源建议**：迁移至 `Spectre.Console.Cli` 的 `AsyncCommand<TSettings>`（`ExecuteAsync`），消除线程池线程同步阻塞与潜在死锁/饥饿。
- **分析**：
  - **死锁面不存在**：BBDown 为纯控制台进程，无 `SynchronizationContext`，`GetResult()` 无死锁条件。
  - **饥饿面极低**：CLI 单次执行，每命令生命周期仅额外占用 1 个线程池线程；唯一长驻点是 serve 的 `StartServer`（serve 全程占 1 线程），线程池可伸缩无实际危害；serve 的并发请求处理本身为 async，不经过此路径。
- **结论**：方向正确（Spectre 官方推荐写法、占用更少线程），但定性应降为"高质量重构"而非隐患；建议作为**一次性批量重构**（8 命令 + 注册 + 保持 ExitCode 语义），不零散进行；不列入当前发版。
- **状态**：✅ 已修复（2026-08-29，第 10 轮，一次性批量落地）：
  - 迁移 **7** 个命令至 `AsyncCommand<TSettings>`（Login/LoginTV/Article/Live/SubCheck/WatchLater/Serve）。原清单列 8 处，核实 `DefaultCommand` 已是 `AsyncCommand`（清单漂移修正，实际迁移面 7 处）。
  - serve 链路：`BBDownApiServer.Run` 拆为同步前置校验 `ValidateListenUrl`（测试可用 `Assert.Throws` 同步断言、ServeCommand 快速失败路径保留同步异常语义）+ 真异步 `RunAsync`（`await app.RunAsync`，不再 `Task.Run` + `GetResult()` 让一个线程池线程阻塞整个服务生命周期）；`Program.StartServer` → `StartServerAsync`。关停段的 30s `Task.WaitAll` 有界同步等待**保留**：仅发生在进程退出路径，与生命周期阻塞不同，且避免 `WhenAll`+`WaitAsync` 改变超时/异常类型语义（代码注释说明）。
  - 各命令原有 catch/退出码语义**逐字保留**（cancel→0、超时→1、批量失败计数→1）。原建议中的 `ExitCodeFor` 评估后**不抽取**：四个命令的取消/超时/部分失败分支消息与过滤条件各不相同，强行共享 helper 会掩盖差异。
  - 计划外残留：`ExternalToolHelper` 一处 `GetAwaiter().GetResult()` 为短进程探针的 stdout/stderr 同步读取，非命令生命周期阻塞，维持现状。
  - 测试适配：`ServeApiHttpTests.RunningServer` 直用 `RunAsync`（去 `Task.Run` 包装）；`NonLoopbackListen_WithoutToken_Throws` 改断言 `ValidateListenUrl` 同步异常语义（真实回环启动路径由各 RunningServer 用例继续覆盖）。

---

## RF-3：serve 已完成任务历史"无界堆积"

- **位置**：`BBDown/Infrastructure/BBDownApiServer.cs` — `finishedTasks` 内存 + `bbdown-tasks.json` 落盘。
- **来源建议**：类比 `SubscriptionStore`（保留上限 5000）为已完成任务设定上限，阻止内存/快照/落盘线性增长。
- **分析**：**建议前提不成立**，现有防护已覆盖：
  - `MaxFinishedTasks = 1000`（:588）+ `FinishedTaskRetention = 30 天`（:589）。
  - `PersistFinishedTasks()` 每次序列化前在 `_persistLock` 内先执行 `TrimFinishedTasksLocked()`（清超龄 + `RemoveRange` 至 1000）（:610, :643-653）。
  - 三个副作用断言（内存增长 / 快照耗时 / 磁盘线性增加）均被 bound 在 ≤1000 条 + 30 天，不随运行时长无界增长。
- **真实残留（有界、可选优化）**：`PersistFinishedTasks` 有 10 个调用点，每次任务完成/删除/关停**全量序列化 ≤1000 条写 tmp + 原子替换**；高频批量任务下属固定 O(1000)/次的非必要 IO。优化方向为**写盘节流**（dirty 标记 + 延时合并，如 30s 窗口多次变更只落盘一次）。
- **结论**：按原文重复"加上限"不采纳；写盘节流列为可选低优先级优化，待有实测压力数据或用户反馈后再评估，不做预防性优化。
- **状态**：⭕ 维持现状（现有上限有效；节流优化列为可选后续项）。

---

## RF-4：Widevine 许可证请求跟随重定向（一致性提议）

- **位置**：`BBDown.Core/DRM/WidevineCdm.cs` — `SendRequestAsync` 使用 `HTTPUtil.VerifiedAppHttpClient`（`AllowAutoRedirect = true`）。
- **发现**：携带设备签名 `challenge` 的许可证 POST 会随 3xx 重放 body；与本轮 B3-F2"凭据载荷禁跟随重定向"原则（gRPC POST 已改用 `NoRedirectClient` 显式拦 3xx）不一致。
- **定性**：预存问题（非本批引入）；实际可利用性极低 —— 恒校验 TLS 排除 MITM、`LicenseUrl` 为硬编码可信端点、服务器若被攻破可直接伪造响应无需重定向（无增量攻击面）。
- **结论**：为原则一致性建议改为禁重定向客户端并显式处理 3xx（与 gRPC POST 同构收口）。副作用极小，可随任意后续安全批次一并落地。
- **状态**：✅ 已修复（2026-08-29，第 9 轮）：新增 `HTTPUtil.VerifiedNoRedirectClient`（始终校验证书 + `AllowAutoRedirect=false`，独立池不受 `--insecure` 降级），`WidevineCdm.SendRequestAsync` 切换并在 3xx 显式拦截报错（状态码 <500 不满足重试谓词，按确定性失败立即抛出）。新增 `VerifiedNoRedirectClientTests` 3 例：身份稳定性（不随 SkipSslCheck 路由）、GET 307 不跟随（对照自动跳转客户端跟随）、POST+body 307 不重放。

---

## RF-5：ffmpeg `creation_time` 元数据格式文化敏感

- **位置**：`BBDown/Infrastructure/BBDownMuxer.cs:375` — `$"creation_time={DateTimeOffset.FromUnixTimeSeconds(pubTime):yyyy-MM-ddTHH:mm:ss.ffffffZ}"`。
- **发现**：字符串插值的自定义日期格式默认按 CurrentCulture 解析，其中 `:` 是"时间分隔符"占位符而非字面字符。在时间分隔符非 `:` 的区域设置（如 fi-FI 用 `.`）下，产出的 ISO-8601 时间戳形如 `2026-08-29T19.30.00.000000Z`，ffmpeg 的 `av_parse_time` 无法按 ISO-8601 解析，发布时间元数据静默丢失/告警。
- **定性**：预存问题（上游继承）；影响面窄（仅 `--sub-only` 之外的正常混流且区域设置特殊的用户），仅元数据丢失不损坏流。
- **核实排他性**：全库扫描其余 `yyyy-MM-dd HH:mm` 用法均为日志/控制台展示或文件名场景，文化敏感可接受（或无分隔符安全）；仅此一处喂给机器可读协议。
- **结论**：一行修复——格式化追加 `CultureInfo.InvariantCulture`（与 277b138 批次的"文化不变解析"原则同构收口）。
- **状态**：✅ 已修复（2026-08-29，第 9 轮）：`DateTimeOffset.FromUnixTimeSeconds(pubTime).ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture)`，全库唯一喂给机器可读协议的时间戳收口。

---

## RF-6：mp4box `-itags` cover 值未走 EscapeString

- **位置**：`BBDown/Infrastructure/BBDownMuxer.cs:146` — `metaArg.Append($":cover=\"{pic}\"")`。
- **发现**：`MuxByMp4box` 顶部对 desc/title/episodeId/author/lang 统一 `EscapeString`（依据其注释：mp4box itags 值内 `"` 与 `\` 必须转义），但同为 itags 值的 `pic`（封面图本地路径）未转义。Windows 路径天然含 `\`（如 `C:\Users\...\cover.jpg`），按代码自述规则会被 mp4box 当转义序列消费，杜比视界自动切 mp4box 的场景下封面可能静默丢失。
- **定性**：预存问题（上游继承）；mp4box 对裸 `\` 的实际容忍度未实测（GPAC 解析器可能宽松），故定 Low/一致性而非确认缺陷。
- **结论**：为与同函数其它 itags 值的转义规则保持一致，补 `EscapeString(pic)` 即可（`EscapeString` 对正常路径无副作用——仅翻倍 `\` 与 `"`）。
- **状态**：✅ 已修复（2026-08-29，第 9 轮）：`metaArg.Append($":cover=\"{EscapeString(pic)}\"")`，与同函数顶部 desc/title/episodeId/author/lang 的转义规则一致。

---

## RF-7：配置合并 cliHasUrl 把选项值误判为 URL

- **位置**：`BBDown/Configuration/BBDownConfigParser.cs:136` — `bool cliHasUrl = cliArgs.Any(a => UrlLikeToken().IsMatch(a))`。
- **发现**：判定"命令行是否已显式给出 URL"时扫描**全部** argv（含选项的值）。误报场景：URL 写在 `BBDown.config` 中、命令行携带值形似 URL 的选项（如 `--aria2c-proxy http://127.0.0.1:7890`、`--work-dir av123`）→ `cliHasUrl` 误判为 true → 配置文件里的 URL 位置参数被丢弃 → Spectre 报缺少必填参数，用户难以定位。
- **定性**：预存启发式的精度问题；触发需要"URL 在配置文件 + 命令行恰好有 URL 形值选项"的组合，真实概率低。
- **结论**：收紧方向——只对"首个非选项 token"（Spectre 位置参数的位置）应用 UrlLikeToken，或先按 aliasMap 跳过带值选项再扫描（`IsSubCommandInvocation` 已有同构跳过逻辑可复用）。改动属行为微调，建议带回归用例（CLI 传 `--aria2c-proxy http://...` + 配置含 URL）单独落地。
- **状态**：✅ 已修复（2026-08-29，第 9 轮）：新增 `GetPositionalTokens`（与 `IsSubCommandInvocation` 同构：带值选项吞下一 token、bool 开关与 `--opt=value` 不吞），`cliHasUrl` 只对位置参数应用 UrlLikeToken。`ConfigMergeTests` 新增 3 例回归：`--aria2c-proxy` URL 形值/`--work-dir` av123 形值不再压制配置文件 URL（配置 URL 与选项值均正确合并）、位置参数提取器跳值语义。

---

## RF-8：FLV 跳过路径不清理封面/字幕/章节（残留累积）

- **位置**：`BBDown/Application/Download.cs` — FLV 分支"文件已存在跳过"路径（约 :947-958）。
- **发现**：DASH 分支的跳过清理（:683-704）会清理本次已下载的封面/字幕/章节并删除空 aid 目录；FLV 分支的跳过路径**只**尝试删空目录，不清理封面/字幕/章节。用户重跑已下载视频时，封面/字幕/章节文件在 aid 工作目录反复累积，且目录非空导致空目录删除逻辑永远不触发。
- **定性**：真实残留累积（每重跑一次多一套文件）；非安全/数据损坏问题。
- **核实附带发现**：清理章节用固定名 `chapters`（`Path.Combine(dir, "chapters")`），而 muxer 写入的是**唯一名** `chapters-{basename}`（`BBDownMuxer.cs:136,324`，防并发混流互相覆盖）——旧清理路径根本删不到实际写入的文件，属预存不一致（`BBDownMuxer` 自身 finally 会清理自己的产物，但跳过路径不经过 muxer）。
- **结论**：收敛为 `Program.DeleteResidualChapterFiles(dir)`：按 `chapters*` 前缀匹配两种命名兜底清理；单文件清理失败静默（IO/句柄异常不掩盖主流程结果）。FLV 跳过路径补封面/字幕清理，与 DASH 分支对齐；fastSkipChecked 跳过路径（:208-223）补章节清理。
- **状态**：✅ 已修复（2026-08-30，第 11 轮）+2 测试（前缀清理含固定名/唯一名/不误删其它文件；目录缺失不抛）。

---

## RF-9：serve 认证失败限速字典无界增长

- **位置**：`BBDown/Infrastructure/BBDownApiServer.cs` — `IsAuthLockedOut` / `_authFailures`（:49, :511）。
- **发现**：`IsAuthLockedOut` 的字典清理只移除**窗口过期**条目（`now - last > 1min`）。攻击者用大量一次性 IP/XFF 值轰炸时，每条都是"最近失败"永不过期——仅删过期条目约束不住字典大小，字典随攻击 IP 数线性增长（内存 DoS）。
- **定性**：Medium（攻击者可控输入面的无界增长；需要持续伪造新 XFF 值，但 serve 已放行 `--trusted-proxy` 场景下 XFF 由代理注入，攻击者不可直接控制——真实触发需攻击者能控制直连来源 IP 或代理透传，触发面中低，但修复成本极低）。
- **结论**：超过 `MaxTrackedAuthFailureIps` 时先清过期，仍超限则按最后失败时间裁剪回上限（保留最近活跃的 N 条）。O(n log n) 仅在异常规模触发，正常路径零开销。
- **状态**：✅ 已修复（2026-08-30，第 11 轮）+1 测试（反射验证 1.2 倍上限独立 IP 轰炸后字典 ≤ 上限+1）。

---

## RF-10：serve 已完成任务溢出裁剪按完成顺序误删

- **位置**：`BBDown/Infrastructure/BBDownApiServer.cs` — `TrimFinishedTasksLocked`（:675）。
- **发现**：`finishedTasks` 列表按**完成顺序**追加（`finishedTasks.Add`），与 `TaskCreateTime`（创建顺序）无关。旧实现溢出时 `RemoveRange(0, count - MaxFinishedTasks)` 删除列表头部——若某任务"后创建但先完成"排在头部，会被误删，而更旧的尾部任务被保留，与"保留最新任务"的意图相反。
- **定性**：Low（需任务完成顺序与创建顺序显著倒挂才可见；真实场景批量并发任务下确有概率）。
- **结论**：溢出裁剪改为按 `TaskCreateTime` 排序，仅移除最旧创建的溢出条目，保留其余任务原顺序（API 按完成顺序展示）。
- **状态**：✅ 已修复（2026-08-30，第 11 轮）+1 测试（构造"后创建先完成"在头部的列表，验证保留最新创建、裁剪最旧创建）。

---

## RF-11：Parser 大会员回退硬编码域名 + 子串判定脆弱

- **位置**：`BBDown.Core/Parser.cs` — 大会员回退（:87-101）。
- **发现**（两处）：
  - 回退抓取网页源硬编码 `https://www.bilibili.com/bangumi/play/ep{epId}`，忽略 `Config.Current.EpHost`（镜像站/BiliPlus 配置）——镜像站用户该回退必然失败（被重定向回可能不可达的官方域名）。
  - 大会员判定用裸子串 `webJson.Contains("\"大会员专享限制\"")`——B 站改文案即失效（历史上文案从"大会员专享限制"演进来过）。
- **定性**：Medium（镜像站用户的真实功能缺陷 + 文案漂移脆弱性）。
- **结论**：回退 host 跟随配置（默认配置行为逐字节不变：`EpHost == "api.bilibili.com"` 时用 `www.bilibili.com`，否则用配置的镜像主机）；判定改解析 JSON 根 `message` 字段（`code:-10403, message:大会员专享限制`），非 JSON 响应（风控 HTML）才回退子串兜底。
- **状态**：✅ 已修复（2026-08-30，第 11 轮）+2 测试（`IsVipRestrictedResponse` JSON message 5 例 + 非 JSON 兜底 2 例）。

---

## RF-12：`BaseUrlRegex` 贪婪匹配误判 query 为端口

- **位置**：`BBDown.Core/Parser.cs` — `BaseUrlRegex`（:751）。
- **发现**：原正则 `http.*:\d+` 未锚定 scheme 后立即匹配主机:端口，会把 `http://host/path?x=1:2` 这类 URL 的 query 中 `:数字` 误判为端口——若该 URL 实际无端口，基址推导错误（虽然实际使用中 CDN URL 通常带端口，但 query 参数含时间戳/签名时可能误判）。
- **定性**：Low（当前使用点 `PickTrackBaseUrl` 的输入为合法 CDN URL，query 带 `:数字` 的场景罕见；正则语义与"提取主机:端口"意图不符是确定性代码缺陷）。
- **结论**：收紧为 `^https?://[^/:]+:\d+`（锚定起点、主机段不含 `/`/`:`、冒号后必须数字）。
- **状态**：✅ 已修复（2026-08-30，第 11 轮）+1 测试（6 例：带端口匹配 / query `:数字` 不误判 / 无端口不匹配 / 缺 scheme 不匹配）。

---

## RF-13：登录轮询跟随重定向缺逐跳校验（凭据外发面）

- **位置**：`BBDown.Core/Util/HTTPUtil.cs` — `GetWebSourceWithSetCookiesAsync`（:217）。
- **发现**：登录轮询（扫码后轮询二维码状态）携带操作者 Cookie 且响应 `Set-Cookie` 是新凭证下发通道，却使用自动跟随重定向的 `AppHttpClient`——被攻破的 passport 域名或开放重定向可把带凭据的请求与响应 `Set-Cookie` 凭证引向任意主机。与同文件 `GetWebSourceAnonymousCheckedAsync`（匿名逐跳校验）、B3-F2 gRPC POST、RF-4 Widevine 的收口原则不一致。
- **定性**：Low（纵深防御缺口；入口 URL 为硬编码可信 passport 端点，无当前可利用面；原则一致性修复）。
- **结论**：改用 `NoRedirectClient` 手动逐跳，每跳 Location 在发起下一跳前必须通过 `IsTrustedCookieHost`，上限 `MaxRedirectHops=10`；与现有 `GetWebSourceCoreAsync` 的 5xx 重试/时钟校准语义保持一致。
- **状态**：✅ 已修复（2026-08-30，第 11 轮）+2 测试（非可信重定向抛错且不访问下一跳 / 可信同主机重定向跟随成功返回 body）。

---

## 处置规则说明

- ✅ 已修复：本轮已落地并有测试/验证。
- ⏳ 待排期 / 待议：技术债或改进提议，登记跟踪，由后续批次按优先级处理。
- ⭕ 维持现状：经评估不实施（含前提不成立的建议），保留理由以便后续不重复评估。
