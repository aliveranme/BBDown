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
| RF-14 | 下载管线两级 catch 过滤器缺口（NotSupportedException/AggregateException 逃逸） | Medium | 采纳（改抛 InvalidOperationException + 过滤器补 AggregateException） | ✅ 已修复（第 12 轮） |
| RF-15 | serve 读端点无 Host 校验（DNS rebinding 读面） | Medium | 采纳（isApi 加 Host 回环白名单） | ✅ 已修复（第 12 轮） |
| RF-16 | 文档正确性族（wiki 退出码表虚构 2/3、CLI-Reference 缺 4 选项、README serve 列举不完整） | Medium（文档） | 采纳（修正文档） | ✅ 已修复（第 12 轮） |
| RF-17 | Parser 免二压重发吞用户取消（两处 catch 缺取消守卫） | Low | 采纳（补 OperationCanceledException 重抛守卫） | ✅ 已修复（第 12 轮） |
| RF-18 | 服务器可控 lan/audio_id 未净化直拼文件路径 + SubOnly ASS 改名 .srt | Low | 采纳（GetValidFileName 净化 + 按源扩展名改名） | ✅ 已修复（第 12 轮） |
| RF-19 | publishDate/videoDate 占位符 culture 敏感且替换值未净化 | Low | 采纳（InvariantCulture + GetValidFileName） | ✅ 已修复（第 12 轮） |
| RF-20 | 跳过路径清理一致性残留（锁内 Skipped 漏 coverPath、dash 跳过路径裸删） | Low | 采纳（与 flv 分支对齐） | ✅ 已修复（第 12 轮） |
| RF-21 | aria2c stdin input-file 换行注入面 | Low | 采纳（写前剔除 \r\n） | ✅ 已修复（第 12 轮） |
| RF-22 | 进程执行边界（探针未观察管道任务；成功路径 5s 兜底翻转成功） | Low | 部分采纳（探针改异步执行器；成功路径语义先确认） | ✅ 探针已修复（第 12 轮）；成功路径 ⭕ 维持现状 |
| RF-23 | mp4box 输出 `.muxing-{guid}` 未知扩展名兼容性 | Low | 待议（需 GPAC 实测后定） | ✅ 已修复（第 12 轮补遗：临时名补 .mp4 后缀） |
| RF-24 | SanitizeUntrustedOptions 漏 interactive | Low | 采纳（一行清零） | ✅ 已修复（第 12 轮） |
| RF-25 | 解析失败日志 option.Url 未单行化（两处） | Low | 采纳（补 SanitizeLogString） | ✅ 已修复（第 12 轮） |
| RF-26 | Core 解析/网络健壮性低危族（5 小项） | Low | 采纳（随批次逐项落地） | ✅ 已修复（第 12 轮） |
| RF-27 | FindBinaries 进程级静态工具路径（serve 并发理论面） | Low | 待议（倾向维持现状：Sanitize 已清零路径字段） | ⭕ 维持现状（第 12 轮定案） |
| RF-28 | HTTP 响应体无大小上限 | Low | 采纳（逐块读取设总量上限） | ✅ 已修复（第 12 轮） |
| RF-29 | .editorconfig 存量违规 4 文件（BOM/末尾换行） | Low | 采纳（重存文件；可补轻量检查） | ✅ 已修复（第 12 轮） |

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

## RF-14：下载管线两级 catch 过滤器缺口（NotSupportedException/AggregateException 逃逸）

- **位置**：`BBDown/Infrastructure/BBDownDownloadUtil.cs:797-801`（抛出点）、`BBDown/Application/Download.cs:1067`（页面级重试过滤器）、`Download.cs:94`（批级失败上报过滤器）。
- **发现**：多线程下载路径在"服务器以 200 响应多线程 Range 请求"时**刻意抛出** `NotSupportedException`（真实可发生的运行时条件：某 CDN 忽略 Range），`:799-801` 还原样重抛 `ArgumentException`；但页面级与批级两级过滤器白名单均只有 `HttpRequestException or JsonException or IOException or InvalidOperationException or TimeoutException (or TaskCanceledException)`。`Parallel.ForEachAsync` 多分片同时失败聚合出的 `AggregateException` 同样不在内（`IsSizeArtifactFailure` 在 :914-916 专门处理过它，证明作者知晓其存在）。异常经 `:830/:837` `ExceptionDispatchInfo` 原样重抛，途中不会被包装。
- **影响**：多 P 批量中一 P 命中即穿透 `DownloadPagesAsync` 的 foreach，剩余分 P 全弃、`NotifyWebhook` 与 `failedPages` 汇总全丢、退出码语义与单 P 失败不一致——与管线精心建立的"单 P 失败隔离"设计矛盾。`EnsureToolAvailable` 的注释（BBDownMuxer.cs:56-59）记录了同构问题（FileNotFoundException 当时正是为此改抛 InvalidOperationException），`NotSupportedException` 是漏网的同族。
- **结论**：采纳修复，三选一（按侵入度递增）——(a) :797 改抛 `InvalidOperationException`（与 EnsureToolAvailable 同一先例，最小改动）；(b) 扩充两级过滤器加入 `NotSupportedException or ArgumentException or AggregateException`（AggregateException 拆包取首个 InnerException 判断）；(c) 在 MultiThreadDownloadCoreAsync 抛出点统一规范化为页面级白名单类型。建议 (a) + 过滤器补 AggregateException。
- **状态**：✅ 已修复（2026-08-30，第 12 轮消纳批）：采纳 (a)——BBDownDownloadUtil 抛出点改 `InvalidOperationException`（消息不变），两级过滤器补 `AggregateException`；既有测试无 NotSupportedException 断言（grep 零命中）无需适配。
---

## RF-15：serve 读端点无 Host 校验（DNS rebinding 读面）

- **位置**：`BBDown/Infrastructure/BBDownApiServer.cs:171-216`（中间件：只对写端点校验 Origin，:172 注释明确豁免读端点）、`:238-279`（读端点返回完整快照）、`BBDown/Application/Download.cs:211`（`AddSavePath(savePath)` 存入 `PathUtil.ResolveWorkPath` 解析后的**绝对路径**）。
- **发现**：默认部署（回环监听、无 token）下，`/get-tasks*` 响应含 SavePaths（服务器绝对文件路径）、标题、URL、错误消息，但无任何 Host 头校验（全仓库无 AllowedHosts 过滤，Kestrel 默认 `AllowedHosts=*`）。攻击者网页经 DNS rebinding（攻击者域名 → 127.0.0.1）后，页面源即 `http://evil.com:23333`，对 `/get-tasks` 的 GET 是"同源"请求——不携带 Origin（fetch 同源 GET 不发 Origin），写端点的 Origin 校验对读端点不生效，且单加 Origin 校验也堵不住。写端点本身无恙：POST 必带 Origin（Fetch 规范非 GET/HEAD 必发），被 `IsLoopbackOrigin` 拦截。
- **定性**：Medium（信息泄露面真实；前提是用户浏览器访问攻击者页面 + 本机 serve 正在运行，泄露的是文件系统布局与任务元数据，非凭据）。
- **结论**：在 isApi 分支增加 **Host 头白名单**（`context.Request.Host.Host` 必须解析为回环地址或 localhost，否则 403/404）——curl/脚本直连 127.0.0.1/localhost 不受影响，同时封死 rebinding 的读写两条路；作为纵深也可给读端点加"非回环 Origin 即 403"，或将 `SavePaths` 从无 token 模式的响应中脱敏。
- **状态**：✅ 已修复（2026-08-30，第 12 轮消纳批）：无 token 时 `isApi` 强制 Host 为字面回环（新增 `IsLoopbackHost`，localhost/127/8/::1，刻意不做 DNS 解析），有 token 时跳过校验保反代部署；+3 端点测试（evil Host 403 读写两路 / 回环放行 / 带 token 跳过校验）+1 纯函数测试。
---

## RF-16：文档正确性族（wiki 退出码表 / CLI-Reference 缺项 / README 列举不完整）

- **位置**：`docs/wiki/CLI-Reference.md:112-121`（退出码表）、`:1,:27`（自称"完整参数详解/完整参数速查总表"）、`README.md:333`。
- **发现**（三处）：
  - 退出码表声称 `2 = Permission Denied（充电专属视频）`、`3 = Tool Missing`，并把"用户主动 Ctrl+C"归入 `0`。但全仓库 `return 2/3;`、`Environment.Exit(2/3)` **零命中**：充电专属无 `--allow-preview` 时走 `Download.cs:489-493` 的 `return false`（跳过分 P，进程正常 0 退出）；工具缺失（FindBinaries 的 FileNotFoundException）走全局 handler 退出 **1**；默认命令 Ctrl+C 返回 **130**（`Program.cs:159-164`），仅 serve/live 等子命令 catch OCE 返回 0。依赖退出码做自动化判断（CI/脚本包装器）的用户会误判。
  - CLI-Reference 对照 MyOption 65 个选项，反引号精确匹配差集为 `--host`、`--ep-host`、`--tv-host`、`--area`（INTL/TV 端点覆盖选项）4 项缺失；README"核心参数速查"同样缺。
  - README:333 serve 配置注入说明括号内仅列 `-l`/`--max-concurrent`/`--serve-token`，而 `ServeSettings` 还有 `--trusted-proxy`、`--notify-webhook`（同页 :157-158 表格已列出，前后不一致）。
- **结论**：采纳修正——(a) 退出码表删除虚构的 2/3 行并改写 Ctrl+C 归属（或实现退出码 2/3，需先定语义）；(b) 补 4 选项行或把标题/总表口径改为"常用参数"；(c) README:333 补全或改"等选项"。
- **状态**：✅ 已修复（2026-08-30，第 12 轮消纳批，文档修正）：退出码表删除 2/3 行、Ctrl+C 改为主命令 130 / 子命令 0、工具缺失归入 1；补 `--host`/`--ep-host`/`--tv-host`/`--area` 4 行（语义取自 MyOption Description）；README serve 选项列举补全。

---

## RF-17：Parser 免二压重发吞用户取消（两处 catch 缺取消守卫）

- **位置**：`BBDown.Core/Parser.cs:324`、`:526`（免二压重新请求的 catch 过滤器）。
- **发现**：两处 `catch (Exception ex) when (ex is ... or TaskCanceledException)` 无 `!token.IsCancellationRequested` 守卫。`:518-519` 注释断言"真正的用户取消（OperationCanceledException，非 TaskCanceledException）不被过滤器捕获"——**前提是错的**：`HttpClient.SendAsync` 在用户 token 取消时抛的正是 `TaskCanceledException`（OperationCanceledException 的子类）。Ctrl+C / serve 关停落在重发请求窗口会被吞掉、记为"降级沿用第一轮结果"，继续走完解析并产生误导性 Warn。项目既有正确模式（`Download.cs:1159`、`UrlResolver.cs:239`、`FavListFetcher.cs:108`）都是先 `catch (OperationCanceledException) when (token.IsCancellationRequested) throw;` 或在过滤器补 `!token.IsCancellationRequested`，唯独这两处漏了。
- **结论**：采纳一行修复——两个 catch 前插入取消重抛守卫（或在 TaskCanceledException 分支加 `&& !token.IsCancellationRequested` 语义）。
- **状态**：✅ 已修复（2026-08-30，第 12 轮消纳批：代码修复 + 回归测试 + CHANGELOG 未发布条目）。

---

## RF-18：服务器可控 lan/audio_id 未净化直拼文件路径 + SubOnly ASS 改名 .srt

- **位置**：`BBDown.Core/Util/SubUtil.cs:247,286,319,357,407`、`BBDown.Core/Parser.cs:502`、`BBDown/Application/Download.cs:444-445`。
- **发现**（两处）：
  - `lan`（`lang_key`）、`audio_id` 全部来自响应体（B 站接口 / 镜像站 EpHost / `--insecure` 下的中间人——后两者正是本项目在其他防御中明确采纳的对抗源），未净化直接进 `PathUtil.ResolveWorkPath($"{aid}/{aid}.{cid}.{lan}...")`；`ResolveWorkPath` 只做 Combine 不过滤（含 `..` 与分隔符原样保留），标题类文本都走了 `GetValidFileName`（InvalidChars 含 `/`、`\`），此处是缺口——恶意 `lang_key` 含 `..\` 可把字幕写出 workDir 之外。
  - SubOnly 分支无条件 `Path.ChangeExtension(_outSubPath, $".{s.lan}.srt")`：ASS 内容字幕（按 URL 形态落盘为 `.ass`）被改名为 `.srt`，播放器无法渲染。`Download.cs:444` 处 `s.lan` 同样未净化进最终产物路径。
- **结论**：采纳——对 `lan`/`audio_id` 应用 `PathUtil.GetValidFileName`（或白名单 `[A-Za-z0-9_-]`）后再拼路径；SubOnly 按源文件扩展名决定目标扩展名（`.ass` → `.{lan}.ass`）。
- **状态**：✅ 已修复（2026-08-30，第 12 轮消纳批：代码修复 + 回归测试 + CHANGELOG 未发布条目）。

---

## RF-19：publishDate/videoDate 占位符 culture 敏感且替换值未净化

- **位置**：`BBDown/Program.cs:48`（`ToString(format)` 未指定 culture）、`BBDown/Application/PathHelper.cs:67-68`（替换值未过 `GetValidFileName`，对照 :51/:54/:58 的 title/pageTitle/ownerName 都过了）。
- **发现**：`<publishDate:...>`/`<videoDate:...>` 的自定义格式串是用户输入，格式化用 CurrentCulture（`:` 是"时间分隔符"占位符而非字面字符）：en-US 下 `<publishDate:yyyy-MM-dd HH:mm:ss>` 产出含 `:` 的串直接进 savePath——Windows 上可写入 NTFS 备用数据流（`File.Exists` 为真但资源管理器不可见）；fi-FI 等区域设置下 `:` 被替换为本地分隔符导致跨机器产物路径漂移。格式非法串有 FormatException 兜底，但 `:` 产出的是合法格式化结果，兜不住。
- **结论**：采纳——`FormatTimeStamp` 加 `CultureInfo.InvariantCulture`（与 RF-5 的 creation_time 收口同构）；对 publishDate/videoDate 的替换值再过一次 `GetValidFileName`。
- **状态**：✅ 已修复（2026-08-30，第 12 轮消纳批：代码修复 + 回归测试 + CHANGELOG 未发布条目）。

---

## RF-20：跳过路径清理一致性残留（锁内 Skipped 漏 coverPath、dash 跳过路径裸删）

- **位置**：`BBDown/Application/Download.cs:208-225`（锁内权威 Skipped 分支）、`:709`（dash 快速跳过删封面）及 `:613,618,637,459,688`（dash 分支其余裸删）。
- **发现**（两处）：
  - 锁内 Skipped 分支清理了 videoPath/audioPath/字幕/音频素材/章节并删空 aid 目录，但**漏了 `coverPath`**——两条锁外快速跳过路径（:709、:976）都删封面，唯独这条锁内权威跳过不删 → 每次走此路径 aid 目录残留一张封面、永远非空删不掉。
  - dash 分支跳过/提前返回路径的多处裸 `File.Delete`/`Directory.Delete` 无 try/catch，而 flv 分支对应位置（:976-977、:939、:962、:988）全部包了 `catch (IOException or UnauthorizedAccessException)`——封面恰被杀软/索引器持有时裸删抛 IOException → 进入页面级重试（过滤器含 IOException）→ 整页（含已存在产物判定）重跑，占用持续则重试耗尽、已完成的分 P 被记为失败。dash 分支是 RF-8 修复族里漏网的一致性问题。
- **结论**：采纳——锁内 Skipped 分支补 coverPath 清理；dash 分支裸删统一改用 flv 分支同款包裹（或收敛到统一清理函数）。
- **状态**：✅ 已修复（2026-08-30，第 12 轮消纳批：代码修复 + 回归测试 + CHANGELOG 未发布条目）。

---

## RF-21：aria2c stdin input-file 换行注入面

- **位置**：`BBDown/Infrastructure/BBDownAria2c.cs:45-50`。
- **发现**：aria2c `--input-file=-` 语法是"URI 行 + 缩进行为选项行"，而写入 stdin 的 URL（来自 API 响应的 CDN 地址）与 Cookie（操作者配置）未剔除 `\r`/`\n`——包含换行的 URL 可注入任意新指令行（新 URI + `  dir=`/`  all-proxy=` 等）。合法的 `  dir=`/`  out=` 行在注入点之后会对后续 URI 重新生效，实际危害以行为扰动/自 DoS 为主，但注入面真实存在，与项目"参数一律走 ArgumentList/stdin 防注入"的总体思路不符。
- **结论**：采纳——URL 与 Cookie 写入前剔除 `\r`/`\n`（一行防御，正常输入零影响）。
- **状态**：✅ 已修复（2026-08-30，第 12 轮消纳批：代码修复 + 回归测试 + CHANGELOG 未发布条目）。

---

## RF-22：进程执行边界（探针未观察管道任务；成功路径 5s 兜底翻转成功）

- **位置**：`BBDown/Utilities/ExternalToolHelper.cs:26-33`、`BBDown/Infrastructure/ExternalProcessRunner.cs:90-92`。
- **发现**（两处）：
  - `CheckFFmpegDOVI` 用 `WaitForExit(5000)` + `outTask.GetAwaiter().GetResult()` 同步探针（RF-2 迁移时的"短进程探针例外"，但杜比视界命中时每 P 最多卡 5 秒）；超时分支 `process.Kill(true); return false;` 直接返回，`outTask`/`errTask` 未被观察——Kill 后管道断裂可能成为 UnobservedTaskException（ExternalProcessRunner/Decrypt 同类问题都做了观察兜底，此处没有）。
  - `ExternalProcessRunner` 进程成功退出后 `await Task.WhenAll(pipeTasks).WaitAsync(5s)`——子进程若（罕见地）派生继承 stdout 句柄的孙进程，管道 EOF 超过 5 秒即抛 TimeoutException 进 catch 重抛，混流退出码 0 的成功结果被误报为失败。注释表明作者知晓此权衡，但成功路径与失败路径共用同一超时，语义上把"输出句柄未关"等同于"执行失败"。
- **结论**：采纳（探针部分）——`CheckFFmpegDOVI` 改真异步（`WaitForExitAsync` + `WaitAsync` 5s 超时，改名 `CheckFFmpegDOVIAsync`），超时分支 Kill 整树后补观察 stdout/stderr 管道任务（防 UnobservedTaskException），调用点 `Download.cs` 同步改 await。成功路径 5s 管道兜底**维持现状**：代码注释已说明"管道任务异常应向上传播"的权衡，正常场景 ffmpeg 不派生继承句柄的孙进程，避免为不存在的场景加分支。
- **状态**：✅ 探针部分已修复（2026-08-30，第 12 轮消纳批）；ExternalProcessRunner 成功路径 ⭕ 维持现状（有意设计，注释在位）。

---

## RF-23：mp4box 输出 `.muxing-{guid}` 未知扩展名兼容性（需实测）

- **位置**：`BBDown/Application/Download.cs:236,240`（混流事务化临时名）、`BBDown/Infrastructure/BBDownMuxer.cs:184`（mp4box 分支把该路径直接作为 `-new` 输出参数）。
- **发现**：混流事务化把输出统一改为 `savePath + ".muxing-{guid:N}"`。ffmpeg 用 `-f mp4` 强制格式不受影响；但 GPAC 按扩展名推断输出封装格式，`.muxing-xxx` 属未知扩展名——旧版 GPAC（gf_isom_open 直写）无碍，较新的 filter-based MP4Box 行为随版本而异（可能告警回退 mp4，也可能直接失败）。仓库内无针对此的测试或注释；若目标 GPAC 版本严格，则所有 mp4box 路径（`--use-mp4box` 与杜比视界自动切换）都会失败。
- **结论**：待议——先在装有 GPAC 的环境实测确认；若不兼容，让 mp4box 分支输出到 `Path.ChangeExtension(muxingPath, ".mp4")` 的临时名（保持唯一性）。
- **状态**：✅ 已修复（2026-08-30，第 12 轮补遗）：临时名改为 `.muxing-{guid:N}.mp4`——不再依赖 GPAC 对未知扩展名的容忍度，新旧版本全部确定性走 ISOM 封装（ffmpeg 分支本就用 `-f mp4` 强制格式，不受影响）；`muxingPath` 仅被精确路径引用（无模式清理、无测试断言格式名），改动零波及。无需 GPAC 实测即可定案。

---

## RF-24：SanitizeUntrustedOptions 漏 interactive（serve 任务阻塞占死并发槽）

- **位置**：`BBDown/Infrastructure/BBDownApiServer.cs:734-805`（SanitizeUntrustedOptions 未清除 `Interactive`）、`BBDown/Application/Display.cs:88`（`Console.ReadLine()`）、`Download.cs:588-591,851-854`（serve 任务流中触发）。
- **发现**：客户端 POST `/add-task` 携带 `{"interactive":true}`，任务进入下载阶段后 `SelectTrackManually` → `Console.ReadLine` 同步阻塞。该调用不在 await 点、不可被 CancellationToken 中断，`/cancel/{id}` 无法释放它占用的并发槽；`--max-concurrent`（默认 3）个此类任务即可把并发闸门占满直至进程重启。若操作者在终端前台运行 serve，任务还会直接消费操作者的键盘输入。仅持 API 权限的本地客户端可触发（写端点 CSRF 已防护），故 Low。
- **结论**：采纳一行修复——`SanitizeUntrustedOptions` 中加 `req.Interactive = false;`（与 `req.Debug = false` 同类处理）。
- **状态**：✅ 已修复（2026-08-30，第 12 轮消纳批：代码修复 + 回归测试 + CHANGELOG 未发布条目）。

---

## RF-25：解析失败日志 option.Url 未单行化（日志注入残留）

- **位置**：`BBDown/Infrastructure/BBDownApiServer.cs:1091`、`:1120`。
- **发现**：日志单行化机制已建（`SanitizeLogString`），但仅用于 :312 队列满路径；解析异常路径把客户端完全可控的原始 URL 直接拼进日志（`$"...{option.Url}..."`）。请求体 URL 可含 CR/LF（64KB 限额内），可伪造 `bbdown-api.log` 日志行。
- **结论**：采纳——两处包一层 `SanitizeLogString(option.Url)`。
- **状态**：✅ 已修复（2026-08-30，第 12 轮消纳批：代码修复 + 回归测试 + CHANGELOG 未发布条目）。

---

## RF-26：Core 解析/网络健壮性低危族（5 小项）

- **位置与发现**：
  1. `BBDown.Core/Parser.cs:342-372` —— 免二压重发**失败降级**路径下，dash 无 `audio` 键 + 杜比/Hi-Res 存在时，音频在两次 pass 各追加一次且无去重（intl 分支 :229 有 Contains 去重）→ `AudioTracks` 重复条目、下游重复下载/混流。修复：pass 1 降级分支跳过重追加（`reparsePass == 0` 守卫）或补去重。
  2. `BBDown.Core/AppHelper.cs:284` —— PGC gRPC 请求 `Host` 头硬编码 `grpc.biliapi.net`，实际目标 `app.bilibili.com`（API2），TLS SNI 与 Host 头不一致，依赖基础设施忽略 Host 路由。修复：Host 取目标 URI Authority 或移除让 HttpClient 自动生成。
  3. `BBDown.Core/Fetcher/FavListFetcher.cs:141-157` —— pn 制收藏夹翻页无"空页/停滞"保护（MediaList/SeriesList/SpaceVideo 都有），受控响应源每页返回 code=0 且 medias 空时把 totalPage 数量的分页请求全部打完（media_count 畸形大时请求洪泛）。修复：页 medias 为空即 break。
  4. `BBDown.Core/Fetcher/IntlBangumiInfoFetcher.cs:19` —— `.Replace("\\/", "/")` 多余（`\/` 本是合法 JSON 转义，Parse 会正确解码）且可损坏数据（原文 `\\`+`/` 被错误归并）。修复：直接删除。
  5. `BBDown.Core/Util/SubUtil.cs:343`、`BBDown/Utilities/BBDownUtil.cs:227` —— `x/player/wbi/v2` 端点名带 wbi 却无 `w_rid/wts` 签名，当前 B 站容忍（未记录行为依赖），一旦收紧即静默降级。修复：登录路径下按现有模式补 WbiSign。
- **结论**：采纳，随批次逐项落地（各项独立、互不依赖）。
- **状态**：✅ 已修复（2026-08-30，第 12 轮消纳批：代码修复 + 回归测试 + CHANGELOG 未发布条目）。

---

## RF-27：FindBinaries 写进程级静态工具路径（serve 并发理论面）

- **位置**：`BBDown/Application/Options.cs:151-204`（经 `Workflow.cs:29` serve 下每任务调用）。
- **发现**：`FindBinaries` 直接写进程级静态 `BBDownMuxer.FFMPEG`/`MP4BOX`/`BBDownAria2c.ARIA2C`，两个并发任务理论上可互相覆盖/静默继承。**但核实实际触发面接近零**：serve 下 `SanitizeUntrustedOptions`（BBDownApiServer.cs:737-740）已清零 `Aria2cPath`/`FFmpegPath`/`Mp4boxPath`，任务无法携带不同路径，并发探测写入的是同一 PATH 探测结果（同值无冲突）；CLI 单进程单 URL 无并发。残余仅"任务 B 静默继承任务 A 探测的路径（同机同 PATH，语义等价）"与冗余探测跳过。
- **结论**：待议，倾向维持现状；若后续把工具路径纳入 `AppSettings`（与 Config 的 AsyncLocal 方案对齐）可顺路收编，不做预防性单独重构。
- **状态**：⭕ 维持现状（2026-08-30 第 12 轮定案：serve 下 `SanitizeUntrustedOptions:737-740` 已清零路径字段，覆盖场景实际不可达；CLI 单进程单 URL 无并发）。

---

## RF-28：HTTP 响应体无大小上限

- **位置**：`BBDown.Core/Util/HTTPUtil.cs:758`（gRPC `GetPostResponseAsync` 的 `ReadAsByteArrayAsync`）、`:467`（普通响应 `ReadAsStringAsync`）。
- **发现**：gzip 解压侧已有 48MB 上限（AppHelper.GzipDecompress，B3-S1），但**压缩响应体本身**与普通 GET 响应体（自动解压后）均无上限。被攻破端点或 `--insecure` 中间人可用分块慢发/gzip 炸弹直接打满进程内存——与 B3 已认可的威胁模型一致，只是解压上限没有覆盖到这一层。
- **结论**：采纳——读取前检查 `Content-Length`/逐块读取并设总量上限（如 64MB），超限抛 `InvalidDataException`。
- **状态**：✅ 已修复（2026-08-30，第 12 轮消纳批：代码修复 + 回归测试 + CHANGELOG 未发布条目）。

---

## RF-29：.editorconfig 存量违规 4 文件（BOM/末尾换行，format 门禁不覆盖）

- **位置**：`BBDown.Core/BBDown.Core.csproj`（UTF-8 BOM）、`BBDown.Tests/BBDown.Tests.csproj`（UTF-8 BOM + 无末尾换行）、`.github/workflows/codeql.yml`（无末尾换行）、`.github/dependabot.yml`（无末尾换行）。均已逐字节验证。
- **发现**：`dotnet format` 不检查 charset BOM 与 `insert_final_newline`，故 pr.yml 的 format 硬门禁拦不住。RF-8~13 涉及的源/测试文件全部干净，此为存量。
- **结论**：采纳——按 .editorconfig 重存 4 个文件；可选补一个轻量检查（挂 format job 前置步骤）。
- **状态**：✅ 已修复（2026-08-30，第 12 轮消纳批：代码修复 + 回归测试 + CHANGELOG 未发布条目）。

---

## 处置规则说明

- ✅ 已修复：本轮已落地并有测试/验证。
- ⏳ 待排期 / 待议：技术债或改进提议，登记跟踪，由后续批次按优先级处理。
- ⭕ 维持现状：经评估不实施（含前提不成立的建议），保留理由以便后续不重复评估。
