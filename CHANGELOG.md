# 变更日志

本文件遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范，版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased] - 2026-08-17

### 修复

- **直播录制加载登录凭据，自动录制账号可看最高画质**：`live` 命令此前不加载凭据，`getRoomPlayInfo` 对未登录请求只返回游客画质（最高 720P）；现在录制前统一走 `InitializeRequestSessionAsync`（`--cookie` 显式传入优先，否则读取本地 `BBDown.data`），并新增 `--cookie` / `--access-token` 选项；画质请求从 `qn=10000` 提升为 `qn=30000`（按账号权限回落，接口只列 ts/fmp4 时自动回退 `qn=10000` 再取一次），启动时打印实际解析到的画质；登录检测超时自动安全降级，防止离线启动时异常崩溃。
- **Ctrl+C 停止保留已录制内容**：取消发生在分段读取中时，已写字节此前会随"无数据"分支整段丢弃（录制几分钟后 Ctrl+C 会丢掉全部内容）；现在读/写阶段被取消都照常返回已写字节，当前分段计入已录内容并参与合成保存。
- **录制结束与终结态清理临时目录**：成功后或未录到任何分段便遇终结态异常退出时，不再残留空的 `path.flv.segs` 根目录（会话目录清空后自动删除空的 `.segs`；根下仍有其它会话的保留分段时不删）；合成失败仍保留分段供手动恢复。
- **断流/网络中断自动重连续录**：
  - 读停滞看门狗：连接既不复位也不 EOF、只是不再有数据（网络黑洞）时，无超时客户端上的 `ReadAsync` 会永久挂起、录制卡死——现在每收到数据重置 60 秒计时，超时按读中断自动重连；
  - 无限重连：旧实现重连 3 次即放弃，断网几分钟必然提前终止、网络恢复后不会自动继续——现在只要直播间仍在直播且用户未取消，就持续指数退避重试（3s→30s 封顶），网络恢复后自动续录；
  - 分段尾裁剪：网络中断/取消使段尾只剩半个 FLV 标签时，ffmpeg concat demuxer 会在截断处报错中止整个合成——合成/改名前先把每个分段裁到最后一个完整标签，且支持 Filter 标志掩码识别；
  - 终结态区分：直播间不提供 flv（仅 HLS/ts）与本地写盘失败（`LiveStreamWriteException` 异常过滤对齐）不再无限重试，直接报错并保留已录分段。

### 新增测试

- 直播录制集成测试新增至 498 个并全部通过：取消保留内容、断流重连合成、读停滞看门狗重连、下播 NoData、`qn=30000` 最高画质请求与回落、无 flv 终结态、FLV 截断尾裁剪（含 Filter 位）、终结态空目录清理与登录检测超时降级等用例（本地假 B 站服务器覆盖完整录制循环）。

## [1.6.12] - 2026-08-17

### 修复与安全性加固

- **直播录制完整性校验**：`LiveStreamUtil` 合并直播分段改用临时 staging 文件并在合并后校验产物大小（`outLen >= totalInputBytes * 0.8`），杜绝因 ffmpeg 遇坏段截断退出而静默删除原始分段丢失数据；引入 `LiveRecordResult` 明确区分完全成功、合并失败保留分段及无数据状态。
- **HTTP 超时与取消语义规范化**：`HTTPUtil` 内部超时 CTS 耗尽后转为 `TimeoutException` 抛出，避免被 CLI 顶层误判为主观取消（退出码 130）；为 `GetWebSourceWithSetCookiesAsync` / `GetWebSourceAnonymousCheckedAsync` 补齐 `ApiTimeoutMs` 整体超时；修复 `using` 声明在 `EnsureSuccessStatusCode()` 之前导致的 4xx/5xx 连接未 Dispose 泄露。
- **下载编排与并发控制**：`Download` 路径独占锁命中跳过时清理败者任务已下载的临时音视频/字幕文件；为 FLV 分支补齐弹幕下载、`--danmaku-only` 与 `--cover-only` 支持；多线程分片下载遇确定性不支持 Range 时立即抛错，不再做无意义退避重试。
- **Core 模块与各 Fetcher 健壮性**：`BangumiInfoFetcher` / `IntlBangumiInfoFetcher` 修正指定 epid 试看分P被意外过滤的问题，未匹配 ep 时抛出明确的 `KeyNotFoundException`；`FavListFetcher` 收藏夹翻页遇到风控或非零 code 时显式中断并报错；免二压第二轮请求增加异常捕获平滑降级与播放受限校验；所有 `backup_url` 改用 `EnumerateArraySafe` 杜绝畸形非数组崩溃。
- **混流与分段合并**：`BBDownMuxer.MergeFLV` 单分片路径免除 ffmpeg 依赖直接移动；多段 FLV 合并中间 `.ts` 文件使用 `try-finally` 保证在异常或取消时必被清理；`CheckFFmpegDOVI` 增加 5 秒异步超时保护。
- **DRM、字幕与弹幕安全**：`WidevineCdm` 全链路传递 `CancellationToken` 并在密钥解析完毕后通过 `CryptographicOperations.ZeroMemory` 清理内存中的密钥材料；`WvdDevice.ParseWvd` 补充 Span 边界检查防越界；SRT 字幕正文 `-->` 转义防时间轴错位；ASS 弹幕正文反斜杠 `\` 转义防恶意排版标签注入；弹幕时间解析失败时跳过生成该条 Dialogue。
- **Serve API 与 CLI 命令安全**：`BBDownApiServer` 净化客户端传入的 `RetryCount` / `RetryDelay`、清除 `Debug` 堆栈暴露标志并规范化 Host 剔除协议前缀；`SubCommand` / `WatchLaterCommand` 订阅与稍后再看单视频捕获 `TimeoutException` / `TaskCanceledException`，防止单项网络抖动中断整批任务；扫码登录严格校验接口返回码 `code == 0` 以及 `access_token` 有效性。
- **UI 与并发线程安全**：`ProgressBar` 控制台刷新加入 `Logger.ConsoleLock`，消除多任务并发下进度条与日志字符交织乱码。

### 新增测试

- 全量单元测试扩充至 487 个并通过，覆盖直播截断拦截、HTTP 超时转换、Host 规范化、截断 WVD 格式校验、弹幕字符转义等用例。

## [1.6.11] - 2026-08-13

### 修复

- **适配 B 站新版扫码登录协议**：B 站已将扫码登录凭证（`SESSDATA`/`bili_jct`/`DedeUserID` 等）从 poll 响应的 `data.url` 参数迁移到 **Set-Cookie 响应头**（HttpOnly）下发，`data.url` 仅剩 crossDomain 跳转参数。旧实现只读响应 body，导致登录“成功”却写入不含 `SESSDATA` 的无效 cookie，表现为始终提示“Cookie 已过期/账号未登录”。
  - `HTTPUtil` 新增 `GetWebSourceWithSetCookiesAsync`，透出 Set-Cookie 响应头；
  - 登录成功时合并 url query（旧协议）与 Set-Cookie（新协议）凭证，自动去重、过滤 cookie 属性（Path/Domain/Expires 等）、转义逗号；
  - 写入前防御性校验 `SESSDATA` 存在，缺失（中间层剥离/风控拦截）时拒绝落盘并报错；
  - 新增 13 个凭证合并/提取专项单元测试，全量 394 个测试通过。

## [1.6.10] - 2026-08-04

### 安全

- **webhook 回调 SSRF 重定向封堵**：任务完成回调改用专用 `AllowAutoRedirect=false` 的 HttpClient，不再经共享客户端跟随攻击者可控的 `Location` 重定向到内网/云元数据地址。
- **回调全零地址拦截**：`IsSafeCallbackUrl` 字面 IP 分支补拦 `0.0.0.0` 与 `[::]`（连接时绑定回环）。
- **局域网回调边界说明**：字面 IP 的 RFC1918 内网地址仍放行（局域网回调用法）；攻击者构造的域名回调走 DNS 解析分支、内网段一律拒绝。需进一步收紧的局域网用户应配置 `--serve-token` 或前置反向代理。

### 维护

- 修复 2 个编译警告：`ServeApiSecurityTests` 空 host 用例的 null 赋值、`IsSafeCallbackUrl` 的 `literalIp` 可能为 null 解引用。

## [1.6.9] - 2026-08-04

### 修复（发布前回归审查）

- **mp4box 字幕参数分裂**：字幕 `:name=` 内嵌含空格语言名（如 "Aymar aru"）时，在 Unix 上被 .NET 拆成两个 argv 导致混流失败。改为把名称/语言代码放在外层引号内（EscapeString 后保持单 token）。
- **mp4box 音频 lang 引号结构**：音频轨 `:lang=` 的不平衡内层引号在 Windows CRT 下会破坏含引号的 lang 值，改为去掉内层引号、值留在外层引号内。
- **直播录制失败退出码**：重连耗尽抛出的 HTTP 超时 OCE 不再被 `LiveCommand` 误判为"用户取消"返回 0，改为落到失败分支返回 1（`catch` 加 `when (cancellationToken.IsCancellationRequested)`）。
- **serve host 白名单斜杠绕过**：`IsOfficialHost` 不再对含路径/斜杠/用户信息/非默认端口的串做纯后缀匹配（此前 `evil.com/.bilibili.com` 可被放行，使携带 SESSDATA 的请求发往攻击者主机），改为只接受规范化的纯主机名。
- **serve host 空值回落**：`host`/`epHost`/`tvHost` 为空或显式 null 时不再原样保留（否则番剧/TV/intl URL 拼成 `https:///...` 抛 `UriFormatException`），统一回落官方默认。
- **serve 回调 RFC1918 内网封堵**：域名解析出的 RFC1918/ULA/CGNAT 地址一律拒绝（DNS 重绑定打内网）；字面 IP 的内网地址仍放行（局域网回调用法）。
- **serve 密钥注入封堵**：`/add-task` 请求体的 `drmKeyHex`/`drmKidHex` 字段被忽略（此前可控制 mp4decrypt 的 key-file 内容）。
- **免二压取最高画质回归**：`Parser` 重请求接管守卫不再要求新响应同时含 `video`+`audio` 数组（杜比/FLAC-only 片源会被整体降级到低画质），只要求 `video` 存在即接管，音轨缺失时从新 dash 的 dolby/flac 节点补出。
- **TV 登录凭据文件权限**：`BBDownTV.data` 与 `BBDown.data` 一致，创建时即以 600 权限打开，消除 umask 两步窗口。

### 安全

- **serve 混流命令注入**：`BBDownMuxer` 混流时对 `lang`/`author`/音轨/字幕语言等外部来源值统一转义并加引号，消除通过 `/add-task` 请求体 `language` 字段远程注入 ffmpeg/mp4box 命令行、实现宿主任意文件读写的漏洞。
- **serve 强制 TLS 校验**：`/add-task` 请求体中的 `insecure` 字段不再被接受，serve 下无法再关闭证书校验（此前可借此让携带操作者 SESSDATA 的请求被中间人截获）。
- **serve 路径穿越封堵**：`filePattern`/`multiFilePattern` 请求字段被忽略（可作为保存路径模板做任意目录创建/文件写入），任务一律使用默认保存模板。
- **回调 DNS 重绑定加固**：任务完成回调在建立连接前会再次校验回调地址（`IsSafeCallbackUrl`），把 add 时校验与连接时刻之间的 DNS 重绑定窗口压缩到最小。

### 修复

- **直播录制超时误判**：`HttpClient` 2 分钟超时抛出的 `TaskCanceledException` 不再被当成"用户取消"静默结束录制，而是按瞬态故障进入重连；重连等待期间取消也不再留下孤儿 `.part` 文件。
- **直播录制重连语义**：主播下播（`当前未在直播`）作为终结态正常结束并改名为最终文件，不再被当可恢复故障重试 3 次后抛错；WAF/风控返回非 JSON 的 `JsonException` 也纳入重连。
- **ProgressBar 计时器竞态**：`Dispose` 与 `speedTimer` 回调改用同一把锁同步，消除对已释放 `Timer` 调用 `Change()` 导致的进程崩溃（serve 长驻场景）。
- **订阅文件丢失更新**：`SubscriptionStore` 的读-改-写完整序列纳入 `_ioLock`，并发写者不再互相覆盖。
- **批量下载超时中止**：单个分P 的 HTTP 超时（`TaskCanceledException`）不再让整批下载中止，进入"记录失败后继续"分支；封面下载不再吞掉用户取消信号。
- **空收藏夹误导报错**：无 `-p` 时选中分P为空（空收藏夹等）改为抛可读的 `InvalidOperationException`，而非 `ArgumentNullException`。
- **Parser 免二压丢音轨**：重请求响应带 `dash` 但缺 `audio`/`video` 数组时沿用第一轮完整轨道，不再静默丢弃。
- **直播录制退出码**：重连耗尽保留 `.part` 并抛错，`live` 命令退出码为 1（此前对超时误判为取消会返回 0）。

### 测试

- 新增 `DownloadTaskSnapshotTests.AddSavePath_IsVisibleToSnapshot`；`ServeApiSecurityTests` 断言 `SanitizeUntrustedOptions` 清空 `insecure`/`filePattern`/`multiFilePattern`；`WidevineCdmTests` 非法 PSSH 用例先验证 wvd 可加载，防止静默退化为 Load 失败路径。
- CI 两个 workflow 的 `dotnet test` 增加 `--filter "Category!=Integration"`，与 `UrlResolverTests` 的 `[Trait]` 对齐，避免每次 push/tag 触发真实网络请求阻塞发布。

### 维护

- `BBDownConfigParser` 的 URL 形态正则补充 `cheese/` 斜杠形式，配置合并不再把裸 `cheese/ep123` 误判。
- `SensitiveDataMasker` 补充 `x-bili-exps-bin` 设备标识头脱敏。
- 登录凭据文件（`BBDown.data`）创建时即按 600 权限打开，消除 umask 权限窗口。
- `API.md`/`README.md` 更新 serve 安全边界说明与直播 `.part` 行为。

## [1.6.8] - 2026-08-01

### 安全

- **DRM 密钥进程列表暴露**：`mp4decrypt` 解密密钥改为通过临时文件传递（`--key-file`），避免命令行对同主机 `ps aux` 可见。临时文件使用后覆写并删除。
- **Debug 日志密钥脱敏**：Widevine 解密密钥在 debug 日志中仅显示前 8 字符。
- **SSL 跳过诊断增强**：`--insecure` 模式下记录被跳过的证书错误类型到 debug 日志，便于排查。

### 修复

- **WidevineCdm 异常吞噬**：protobuf 解析失败和 RSA 解密失败不再静默吞错，改为记录诊断日志并降级返回 null。
- **异步死锁防护**：`LoginCommand`、`LoginTVCommand`、`BBDownApiServer` 中 `.GetAwaiter().GetResult()` 调用改为 `Task.Run(...).GetAwaiter().GetResult()`，防止被 GUI 宿主复用时死锁。
- **调试日志文件清理**：仓库根目录遗留的 20 个 `debug_*.json` 已删除，并加入 `.gitignore`。

### 测试

- 新增 `WidevineCdmTests`：PSSH 解析边界、非法输入降级。
- 新增 `ParserTests`：`ThrowIfPlayLimited` 全覆盖、WbiSign、Codec 映射。
- `UrlResolverTests` 添加 `[Trait("Category", "Integration")]` 标记，CI 可通过 `--filter` 排除。

### 维护

- `.remember/`、`.pi-subagents/`、`debug_*.json`、`artifact/` 加入 `.gitignore`。
- `Parser.cs` Dispose 所有权说明注释。
- `WidevineCdm.cs` RSA OAEP fallback 从裸 `catch` 改为 `catch (CryptographicException)`。

## [1.6.7] - 2026-07-27

### 修复

- **TV 端解析健壮性**：处理 TV API 返回的 `result` 节点不是 JSON 对象（如 `null`/数组）时的解析错误。
- **登录错误提示增强**：WEB/TV 登录失败时返回非零退出码；提示信息区分“网络失败”与“二维码过期”。
- **取消 token 贯通** (`cancellationToken`)：
  - `CheckUpdateAsync` 可被取消，避免退出后仍访问 GitHub。
  - `BBDown serve` 收到 `Ctrl+C` 时优雅停止，正在处理的 HTTP 请求可被正常关闭。
  - `BBDown login` / `BBDown logintv` 的二维码轮询可被取消，不再每秒请求一次 B站登录接口。
- **silent-failure 补遗**：
  - 更新检查失败从 debug 日志升级到 warn 提示。
  - `ss:` 输入的番剧→课程 fallback 不再裸 `catch`，会打印 fallback 原因。
  - 章节信息、DRM license 元数据提取失败时改为 warn 级别。
  - 下载重试日志带上异常类型名，3 次失败后给出明确“已重试 N 次”提示。

### 变更

- `CheckUpdateAsync` 版本比较统一为 `vX.Y.Z` 格式，不再对同一版本误报“发现新版本”。
- 未登录提示增加 TV 登录用法说明：若已执行 `BBDown logintv`，请在下载命令中加上 `--use-tv-api`。

## [1.6.6] - 2026-07-27

### 新增

- **充电专属（试看）视频检测**：解析 UPower 接口时识别充电专属预览片段，默认跳过并提示；新增 `--allow-preview` 选项允许保存试看内容。
- 支持下载 UP 主全部投稿列表（`space` 下载模式）。
- 测试覆盖扩展：新增 DRM 私钥解析、选项绑定等回归测试。

### 安全

- 日志与控制台输出中对 Cookie、Token 等凭据进行脱敏处理。
- APP API gRPC 请求中的 Authorization 头在日志中脱敏。
- Widevine 解密私钥导入支持 PKCS#1 / PKCS#8 DER 格式；`mp4decrypt` 失败时显式抛异常，避免静默失败。

### 变更

- Release CI 流程：所有平台构建任务前必须先通过测试套件。
- Docker 构建目标切换到 .NET 10，并仅还原应用项目以加速构建。

### 修复

- 路径解析：从 `AppContext.BaseDirectory` 解析 `APP_DIR`，修复不同启动方式下的工作目录错误。
- 登录状态：区分“从未登录”与“Cookie 已过期”。
- 下载链路：消除静默失败、校验数值选项、正确传播单 P 失败状态。
- 分段下载：`MergeFLV` 仅合并本分段的片段并校验 ffmpeg 退出码。
- Fetcher：修复分页死锁、悬空 `JsonElement` 与循环中的静默中断。
- 配置解析：正确识别 `--opt=value` 形式的 `--config-file` 与命令行选项。
- aria2c：通过 `stdin input-file` 而非命令行传递 Cookie，避免特殊字符被 Shell 截断。
- 交互式选集：使用 `>=` 正确限制 track/quality 索引。
- Archive：仅当某 aid 的所有分 P 都成功后才记录该 aid。
- 字幕：保留超过 24 小时的时长，并防止空白行错误拆分 cue。
- 弹幕：对 ASS 输出中的控制字符进行转义。
- 选项默认值：让 `default-on` 标记真正默认为 `true`。
- 分 P 选择：支持混合 `-p` 语法，并拒绝完全匹配不到任何页面的选择。
- API 服务器：HTTP 响应完成后仍保持下载任务存活。

### 文档

- README 增加“更多常用选项”参考表。
- README 添加充电专属视频处理说明。
- 修复 CLI 示例与 README 资源链接。

## [1.6.5] - 2026-07-25

### 修复

- 修复 Native AOT 产物启动时 Spectre.Console.Cli 无法获取默认命令 settings 类型导致崩溃的问题。
- 兼容 `-help`、`-?`、`-version` 等单横线常见参数写法，避免 `-help` 被解析为短选项簇并误报 `encoding-priority` 缺值。
- 修复 `Av` 大小写视频 URL 解析问题。
- 优化区域限制等播放限制的错误提示，明确展示 `limit_play_reason` 与 `play_detail`。
- 修正 BV 转换与 SS URL 解析相关测试样例。
- 统一 GitHub issues 链接为小写仓库路径。

## [1.6.4] - 2026-05-29

### 新增

- **原生 C# Widevine DRM 解密**（完全替代 Python/pywidevine 依赖）
  - 实现 `WidevineCrypto.AesCmac` + `derive_keys` / `derive_context` 密钥派生
  - 完整的 HMAC-SHA256 签名校验 + AES 内容密钥解密
  - V2 WVD 格式支持 + B站服务证书 PKCS#1 公钥兼容
- GitHub Release 自动化工作流（推送 `v*` tag 自动构建 6 平台并创建 Release）
- API 服务器并发数自定义：`BBDown serve --max-concurrent <n>`
- CLI 自定义参数：
  - `--muxer-timeout <分钟>` — 混流超时（默认 30）
  - `--retry-count <n>` — 网络请求重试次数（默认 3）
  - `--retry-delay <毫秒>` — 重试间隔基数（默认 3000）
  - `--thread-segment-size <MB>` — 多线程下载分片大小（默认 20）
- Cookie 过期检测与明确提示（区分"未登录"vs"Cookie 已过期"）
- 下载链路 `CancellationToken` 贯通（CLI Ctrl+C / API 请求取消）
- `.tmp` 文件断点续传支持（完整临时文件自动移动，写入增量校验修复）
- API 服务器文件日志（`bbdown-api.log`）
- `JsonElementExtensions` 安全 JSON 访问器（10 个扩展方法）
- 单元测试骨架：`BBDown.Tests`（`BilibiliBvConverterTests` / `UrlResolverTests` / `FormatHelperTests`）
- 核心方法拆分：`UrlResolver.cs` / `ExternalToolHelper.cs`

### 变更

- **目标框架升级：.NET 9 → .NET 10**
- 升级依赖：QRCoder 1.6.0 → 1.8.0
- 升级依赖：Google.Protobuf 3.28.3 → 3.34.1
- 升级依赖：Grpc.Tools 2.67.0 → 2.80.0
- 迁移 CLI 框架：System.CommandLine（已归档）→ Spectre.Console.Cli 0.55.0
- `Config` 全局状态重构：`AppSettings` record + 线程安全读写锁
- `HttpClient` 连接池刷新：`SocketsHttpHandler.PooledConnectionLifetime = 5min`
- 规范化 API 文档文件名：`json-api-doc.md` → `API.md`
- 重试策略精细化：指数退避 + 不可重试异常短路（`ArgumentException` / `InvalidOperationException` / `NotSupportedException`）
- 清理冗余 NuGet 引用：`Microsoft.Extensions.DependencyInjection`（已由 `Microsoft.NET.Sdk.Web` 隐式提供）

### 修复

- **API server `dotnet run` 端口劫持**：移除 `launchSettings.json`，`serve --listen` 现在正确绑定自定义地址
- **Widevine proto 协议合规**：字段编号与 Google 标准对齐（`pssh_data=1`、`RequestType` 枚举、`key_control_nonce=uint32`）
- **Native AOT 运行时崩溃**：`MyOption` / `CommandSettings` / `Command` 类添加 `[DynamicallyAccessedMembers]` + `<TrimmerRootAssembly Include="BBDown" />`
- Windows 下 FFmpeg/MP4Box 混流时弹出命令行窗口（`CreateNoWindow = true`）
- 跨平台目录创建逻辑（`Path.GetDirectoryName` 替代 `Contains('/')`）
- 下载重试时的异常信息丢失问题（增加 `LogDebug`）
- API 服务器 Webhook 回调的未观察异常风险
- `Parser.GetMaxQn` 中 `int.Parse` 未处理非数字输入 → `int.TryParse`
- `BBDownMuxer.EscapeString` 双引号转义逻辑错误
- 多处 `First()` 调用在空序列时抛 `InvalidOperationException`
- `Page.bvid` getter 中 `long.Parse(aid)` 未处理非数字 aid
- `MergeFLV` 空数组保护
- `SpaceVideoFetcher` 中 `GetValidFileName` 与 `BBDownUtil` 的重复实现合并到 `BBDown.Core.Util.PathUtil`
- `Path.GetDirectoryName` 返回 null 时的安全防护
- `AppHelper.DoReqAsync` 参数未校验直接 `Convert.ToInt64`
- 文化敏感字符串操作（`ToLower()` → `ToLowerInvariant()`）防止土耳其 locale bug
- 多处 `JsonDocument` / `HttpResponseMessage` 资源泄漏
- `BBDownDownloadUtil` 进度回调中除零风险防护
- FFmpeg/MP4Box 混流死锁（消费 stdout 防止缓冲区满）
- 并发下载目标文件碰撞（按路径 `SemaphoreSlim` 排他锁）
- API 服务器错误信息泄露（默认隐藏 `ErrorMessage`，仅 debug 模式暴露详情）

## [1.6.3] - 2025-05-06

### 修复

- `DelayPerPage` 选项在 System.CommandLine beta4 下错误地要求必填

## [1.6.2] - 2025-03-16

### 修复

- Dockerfile 构建流程优化
- 多处 `JsonDocument` 未正确释放的问题
- `NormalInfoFetcher` 中 `TryGetProperty` 安全性

## [1.6.1] - 2025-02-08

### 新增

- 支持 ASS 弹幕格式输出
- 合集/系列链接新格式兼容（space.bilibili.com/*/lists/*）

### 修复

- 修正 `GetWebLocationAsync` HEAD 请求兼容性

## [1.6.0] - 2024-12-15

### 新增

- Widevine DRM 原生 C# 解密支持（无需 Python）
- API 服务器模式（`BBDown serve`）
- 配置文件支持（`BBDown.config`）

### 变更

- 重构 gRPC APP 接口请求体
- 增加对多音频轨（背景音频、配音）的支持

---

[Unreleased]: https://github.com/AliverAnme/BBDown/compare/v1.6.8...HEAD
[1.6.8]: https://github.com/AliverAnme/BBDown/compare/v1.6.7...v1.6.8
[1.6.7]: https://github.com/AliverAnme/BBDown/compare/v1.6.6...v1.6.7
[1.6.6]: https://github.com/AliverAnme/BBDown/compare/v1.6.5...v1.6.6
[1.6.5]: https://github.com/AliverAnme/BBDown/compare/v1.6.4...v1.6.5
[1.6.4]: https://github.com/AliverAnme/BBDown/compare/v1.6.3...v1.6.4
[1.6.3]: https://github.com/AliverAnme/BBDown/compare/v1.6.2...v1.6.3
[1.6.2]: https://github.com/AliverAnme/BBDown/compare/v1.6.1...v1.6.2
[1.6.1]: https://github.com/AliverAnme/BBDown/compare/v1.6.0...v1.6.1
[1.6.0]: https://github.com/AliverAnme/BBDown/releases/tag/v1.6.0
