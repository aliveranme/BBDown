# 变更日志

本文件遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范，版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [未发布]

### 修复

- 直播分段合并不再仅凭产物大小判断成功：核对 FLV 音视频媒体帧数，漏掉短末段、坏段或单段并非 FLV 时保留原分段和旧输出；写入或进度回调异常时不清理已写入但尚未登记的分段。
- 分 P 选择中的前导零按整数规范化（如 `-p 01` 匹配 P1），单项范围到 `int.MaxValue` 时不再发生计数回绕。
- 选择不到分 P 或大量分 P 下载失败时，错误消息只展示前 20 项和总数，避免生成过大的异常与日志行。
- aria2c 预分配到远端总长的中断文件不再被误当成完整文件跳过；保留 `.aria2` 控制文件供恢复，避免残缺内容进入混流。
- 多线程续传清单增加分片布局校验，修改 `--thread-segment-size` 或使用缺少布局信息的旧清单时重新下载分片，避免不同偏移的旧数据被拼成长度正确但内容损坏的文件。
- 单个分片失败时取消同一下载中其余在途分片，及时释放停滞请求与路径锁；保留原始失败原因和用户取消语义。
- gRPC 响应同时检查 HTTP 状态与 headers/trailers 中的 `grpc-status`，避免把 HTTP 200 的权限拒绝误报成 protobuf 损坏。
- DRM 解密使用唯一临时文件，验证有效输出后覆盖移动原文件；失败、超时、取消或替换受阻时保留原媒体并清理本次临时输出。

### 工程与验证

- Parser、APP gRPC 和普通视频/番剧/课程 Fetcher 增加实例级 API 传输入口；公开方法及 CLI 配置保持兼容。
- 新增回环 HTTP/protobuf、gRPC 尾部状态、正文停滞取消、DRM 失败收尾，以及真实 aria2c 恢复和 Bento4 CENC 音视频解密集成测试。CI 显式要求工具可用，缺失工具不再静默空跑。
- PR、发布、每日构建和 Docker 先按完整六 RID 依赖图执行 locked restore，再以 `--no-restore` 发布指定平台；CI 与 Docker 使用 10.0.302 SDK 补丁以匹配现有锁文件，`global.json` 的 10.0.300/latestPatch 约束不变。
- 移除全局 AOT/裁剪警告屏蔽，展开第三方依赖诊断；新增具体警告基线检查与 CI 日志归档，发现未审计的新警告时失败。

## [1.7.3] - 2026-10-04

### 修复

- **playurl 触发 B 站人机验证（`data.v_voucher`）时不再只报不透明的"解析此分P失败"**：风控窗口内 B 站对 `x/player/wbi/playurl` 返回 HTTP 200 + `{"code":0,"data":{"v_voucher":"voucher_…"}}`——合法 JSON、`code=0`，但既无 `dash` 也无 `durl`。旧实现对这种响应既不报业务错误也不报风控页，静默解析出零轨道后落到"无可用轨道"分支，只打印一句 `解析此分P失败(建议--debug查看详细信息)`；且因为它不是异常，完全不参与页面级重试，`sub check` 中表现为风控窗口内整批投稿在毫秒级瞬间全部下载失败、日志无任何原因（实测单分P 56~66ms 内失败，逐个投稿重复）。现在在解析入口统一识别 `v_voucher` 响应（顶层 / `data` / `result`），抛出带原因与处置建议的可读错误，纳入既有页面级重试（`--retry-count` / `--retry-delay` 退避），与 `code=-412/-352` 等业务风控错误同待遇。
- **INTL 双轮请求中仅后一轮被风控时不再丢弃前一轮已解析的轨道**：INTL 播放接口要发两次请求（`prefer_code_type=0` 取 `video_info`/`stream_list`，`=1` 补充流），两轮共用同一个结果对象。原实现在两轮循环体内一律把 `v_voucher` 响应转成异常，于是第二轮被风控会连带丢掉第一轮已经拿到的可用轨道——而第二轮紧接第一轮发出，风控按请求量累积，其命中率反而高于第一轮（逼近阈值时是常态），重试时同样形状必然复现，等于永远下载不到本可下载的视频。现在改为：已有可用轨道时降级跳过该轮并打 Warn（与免二压 `qn=127` 重发同策略），只有两轮都没拿到轨道时才抛出、交由页面级重试。

## [1.7.2] - 2026-10-01

### 修复

- **DRM 解密调用 mp4decrypt 的参数形式错误（解密必然失败）**：旧实现把 `kid:key` 写进临时文件后用 `--key-file` 传给 mp4decrypt，而 Bento4 的 mp4decrypt **只有** `--key <id>:<key>` 这一个传密钥的选项——实际报 `ERROR: unexpected argument (<输入文件>)`（退出码 1），页面级重试 3 次后整页失败。现改为 `--key <kid>:<key>`，并顺带不再把密钥写入磁盘临时文件（旧路径既不可用又会在临时目录留下密钥）。本机实测：Bento4 `mp4encrypt --method MPEG-CENC` 加密后按新参数解密，解出的基本流与原文**逐字节一致**。

## [1.7.1] - 2026-10-01

### 新增

- **DRM 开箱即用**：发布包内置 `device.wvd`（Widevine L3 设备文件）**与 `mp4decrypt`（Bento4 解密工具）**，解压即具备完整的 Widevine 解密能力——无需自行提取设备文件，也无需另行安装解密工具；`--wvd-path` / `--mp4decrypt-path` 仍可用于替换内置文件。Bento4 版本与归档 SHA256 在构建时固定校验，许可证文本随包分发（见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)）。**例外**：`linux-arm64` 官方无 Bento4 二进制，该平台仍需自行安装 `mp4decrypt`；缺失时会在下载流之前报错并给出指引。
- **DRM 自动检测与自动解密（默认开启）**：解析默认携带 `drm_tech_type=2`，响应标记为 DRM 时自动取钥并解密，不再需要显式传 `--decrypt-drm`（该开关保留以兼容旧脚本）；新增 `--no-decrypt-drm` 关闭自动检测与解密（回到旧请求形态与旧行为）。

### 改进

- **解密前置检查**：受 DRM 保护的内容在下载流之前检查 `mp4decrypt` 与 `device.wvd`（或手动 `--key/--kid`）是否齐备，缺失时立即失败并打印可操作指引——此前要下完整个视频才会在解密阶段报错。

## [1.7.0] - 2026-10-01

### 修复

- **发布校验文件无法直接使用**：Release 附带的 `sha256sums.txt` 内路径带 `artifacts/` 前缀（CI 里由 `sha256sum artifacts/*.zip` 在工作目录下生成），用户下载后直接 `sha256sum -c sha256sums.txt` 必然报 `FAILED open or read`，须先自建一个同名目录才能校验——与“下载后验包”的初衷相悖。现改为在 `artifacts/` 内生成（文件名不带目录前缀），下载的 zip 与校验文件放同一目录即可直接校验；校验方法同时写入 README 与 wiki 快速上手（含 Windows / macOS 等价命令）。

- **单条目失败的异常过滤器三处漂移**：`sub check` 逐 aid/逐订阅、`watchlater` 逐视频、逐 P 下载编排与页面级重试各自维护一份"可跳过的异常类型"集合，历次扩充（RF-44 权限、RF-72 有界响应体）都漏过其中一两处——例如 `watchlater` 遇到 `KeyNotFoundException`、订阅检查遇到 `FormatException`/`OverflowException`（服务器可控 id 畸形）时会**中止整批**而不是记一次失败继续。现收敛为一份具名策略（"可跳过"与"页面级可重试"共用同一核心类型集），四处站点语义一致：单条目失败计入失败数后继续，用户取消仍按站点守卫立即上抛。
- **`<apiType>` 与实际接口不一致**：同时给出多个 `--use-*-api` 时，日志与 `<apiType>` 占位符按 TV > APP > INTL 展示，而实际请求按 INTL > APP > TV 分派——用 `<apiType>` 命名产物时可能与实际来源不符。现优先级只有一处定义（`Parser.ResolveApiMode`），展示与分派共用。

### 安全性

- **DRM 密钥材料清零**：`WvdDevice` 释放时清零 client_id 原始字节（此前只释放 RSA 句柄），私钥导入后立即清零源字节；Widevine 许可证处理不再保留未使用的客户端 MAC 密钥。

### 改进

- **`watchlater --help` 的选项顺序**：`--limit` 由首位移到末位。8 个下载选项（`-c/--cookie`、`-w/--work-dir` 等）在 `watchlater` 与 `sub check` 之间收敛到共用基类后，Spectre.Console.Cli 先枚举基类属性。选项名、描述与绑定行为均未变化，`sub add/list/remove` 的选项面同样不变。
- **维护性（用户不可见）**：H/I 组结构性重构计划（[REFACTOR_PLAN](docs/REFACTOR_PLAN.md)，批 1~7 + 收口批）执行完毕——异常过滤器收口为具名策略、`Parser`/下载管线/serve 按职责拆解、`watchlater` 与 `sub check` 的下载选项收敛到共用基类、`UrlResolver` 入口拆分、选项扫参与魔法数具名等；除本文件列出的条目外，用户可见行为与 CLI/配置契约保持不变。

## [1.6.22] - 2026-09-30

### 修复

- **选项值以 `-` 开头时无法用空格写法传入**：`BBDown sub add mid:19231317 --name "-尾野"` 此前报 `Option 'name' is defined but no value has been provided.`——Spectre.Console.Cli 的 tokenizer 把任何以 `-` 开头的 argv 一律判为选项，值以 `-` 开头的场景（订阅显示名、以 `-` 开头的路径/正则/文件模式等）只能用等号或冒号写法传。现在 argv 预处理会按 GNU getopt 语义把这类值并入前一个取值选项（`--name -尾野` → `--name=-尾野`），命令行与 `BBDown.config` 同样生效。两条护栏：bool 开关后面的 `-` 开头 token 不合并（`--skip-mux --skip-subtitle` 仍是合法写法），下一个 token 是已知选项名时不合并（漏写值的 `--name --cookie x` 仍报原错误）。

### 改进

- **`sub check` 增量扫描不再逐个展开投稿**：`mid:` 订阅此前每次检查都走 `IFetcher.FetchAsync`——投稿列表接口不返回 cid，因此要为**每个**投稿再发一次详情请求展开分P（实测一个 53 投稿的订阅，每次检查固定花约 11 秒在展开上，与是否有新内容无关；大 UP 主是分钟级请求量并显著加重风控）。现在改为新增的 `IAidLister`（`SpaceVideoFetcher` 实现）按投稿列表（`order=pubdate` 倒序、50 条/页）**轻量列举 aid**，每个新 aid 的下载本来就会各自重新解析，展开这一步是纯浪费。同一订阅实测 **11.75s → 1.00s**。
- **`sub check --full-scan`**：新增开关，禁用"遇到整页均已下载即停止翻页"的提前结束，重扫全部列表页。默认增量模式的代价是停止点之后的更旧页不再扫描——这些页里若有历史中没有的稿件（例如上次下载失败、或手动清理过历史），需要 `--full-scan` 才会重新发现（首次部署 / 历史文件重建 / 怀疑漏下旧稿件时建议先跑一次）；收藏夹/合集/番剧类订阅仍走原有全量解析路径（`order=mtime` 会与用户手动排序交互，判据不成立）。

## [1.6.21] - 2026-09-26

### 新增

- **`sub check --per-sub-dir`：按订阅分目录下载**。多个订阅（多 UP 主/账号）此前全部平铺在 `-w` 根目录，无法区分归属；开启后每个订阅下载到 `<work-dir>/<订阅名>/` 子目录，订阅名取 `sub add --name` 的显示名（缺省为 target），经 `PathUtil.SanitizePathSegment` 净化（剔除路径分隔符/保留名/纯点段），净化后同名的订阅追加 `-2/-3` 序号避免互相覆盖。默认关闭，单目录行为完全不变。

### 修复

- **`sub check` / `watchlater` 的 `-w` 解析**：两类多任务命令改为在进入逐任务循环前把 `-w` 解析为绝对路径且**只解析一次**（新增 `Program.TryResolveWorkDir`），修复相对 `-w` 时进程 CWD 漂移导致第二个任务产物落进 `<root>/<task1>/<task2>` 嵌套目录的问题（`watchlater` 此前存在同一缺陷）。非法 `-w`（Windows 保留字符、纯空白，或指向已存在的文件）现在报 `工作目录无效: …` 并返回退出码 1，不再穿到命令级处理器被报成"请尝试升级到最新版本后重试!"并静默放弃其余全部任务。
- **`sub check --per-sub-dir` 的订阅名回退**：显示名为空/空白时改为真正回退到订阅目标——原判据取的是净化后的值，而净化对空输入兜底为 `_`、永不返回空串，回退分支实际不可达（实测空名得到 `_` 而非 `mid_163637592`）。纯点名（如 `...`）仍净化为 `_`，不产生非法目录段。

### 改进

- **`sub add` 命名提示**：未指定 `--name` 时提示显示名将回退为订阅目标（也是 `sub check --per-sub-dir` 的目录名），建议显式命名；wiki 同步补充命名建议。

## [1.6.20] - 2026-09-24

### 修复

- **Fetcher 忽略非零接口错误码**：番剧、国际版番剧和收藏夹的部分路径只读取错误 `message` 却继续解析；现统一校验共享响应 envelope，并让系列及分页响应在 `code` 非零时终止解析。媒体列表在缺少 `data` 时仍保留系列回退。

### 改进

- **大量分P选择性能**：以 ordinal `HashSet` 筛选所选分P，保留原始页面顺序，避免选择项较多时反复线性查找。
- **异步文件处理与取消传播**：下载续传清单、归档、订阅历史、本地凭据和配置读取迁移到异步 I/O，并在相应操作中传递取消令牌。
- **下载流程维护性**：拆分页面编排、轨道执行和混流收尾逻辑，统一 DASH/FLV 的文件清理与最终产物登记边界。

### 构建

- 集中管理 NuGet 包版本并加入锁文件，移除未使用的 SharpZipLib 依赖；CI 启用 NuGet 缓存。

## [1.6.19] - 2026-09-18

### 修复

- **fetcher 错误响应诊断不可达（RF-65）**：`SpaceVideoFetcher`/`CheeseInfoFetcher`/`FavListFetcher`/`NormalInfoFetcher`/`IntlBangumiInfoFetcher` 的顶层 `GetPropertySafe` 未先查 `code`——错误响应（`code≠0` 且无 `data` 节点）时抛英文裸 `KeyNotFoundException`，掩盖精心编写的中文诊断。现 6 处顶层取节点统一为逐级判空，缺节点给可读提示。
- **服务器可控 `res`/`fps` 可穿越保存路径（RF-63）**：`--file-pattern` 含 `<res>`/`<fps>` 时，镜像站/`--insecure` 中间人下发含 `/` 或 `..` 的宽高/帧率即可写出预期目录之外。现与 `dfn`/codecs 同构过 `GetValidFileName`（RF-58 消纳缺口）。
- **评论抓取超时/本地权限错误误将成功页判为失败（RF-64）**：评论保存 catch 白名单窄于页面级过滤器，评论 API 超时（TimeoutException）等逃逸后被页面级过滤器记为失败——已成功下载并混流的页面报错、退出码非 0 且不入档。现 catch 补齐超时/聚合/权限异常。
- **PR CI 漏洞扫描门禁形同虚设（RF-67）**：`dotnet list package --vulnerable` 无论是否发现漏洞退出码恒为 0（NuGet/Home#11315），依赖出现已知漏洞时 PR 照常合并。现改用 `--format json` + `jq` 判定并 `exit 1`，使门禁具备真实失败语义。
- **日志注入旁支——`watchlater`/`live` 服务器可控标题未脱敏（RF-70）**：视频/直播间标题可含 CRLF 伪造日志行。现与 RF-54 同构过 `SanitizeLogString`。
- **服务器 message 未净化直拼异常消息（RF-80）**：接口 `message` 字段可含控制字符（ANSI/换行），经异常消息进入日志（B3-L3/RF-25 同族）。现 8 个 fetcher 共 12 处统一过 `JsonElementExtensions.SanitizeServerText`。
- **巨包/畸形 gRPC 帧令整批分 P 中止（RF-72）**：RF-28/RF-51 新增的 64MB 响应体上限与 gRPC 帧校验抛出的 `InvalidDataException`（继承 `SystemException` 而非 `IOException`）不在下载两级失败隔离过滤器内，一次命中即放弃剩余分 P、丢 webhook/failedPages。现两级过滤器与命令级过滤器（sub/watchlater）补齐该类型。
- **服务器可控 aid/cid 可穿越保存路径（RF-73）**：`Page` 的 aid/cid/epid 逐字来自 API 响应（无数字校验）且直接拼入工作区路径与 `<aid>`/`<cid>` 占位符；镜像站/`--insecure` 中间人下发含分隔符或 `..` 的值可令产物写出 `--work-dir` 之外。现 `Page` 属性 setter 经 `PathUtil.SanitizePathSegment` 单一收口（合法值恒等，不影响 bvid 回退）。
- **未认证客户端可刷爆 serve 日志盘（RF-74）**：401 认证失败日志把客户端可控的 `Request.Path`/XFF 未脱敏、未截断地写入无轮转的 `bbdown-api.log`，未认证客户端可无限刷盘。现 sink 改 `TruncateForLog`（单行化 + 截断），限速分支只记 IP。
- **serve 下 selectPage/danmakuFilter 无接受上限（RF-81）**：分P 选择展开仅按段限上限、弹幕过滤器无上限，客户端可构造内存/CPU 放大与 MB 级日志行。现 `ParsePageSelection` 增累计上限，日志行截断，serve 忽略装饰性弹幕过滤。
- **隐藏废弃开关绕过 serve 的 FilePattern 不变量（RF-82）**：`addDfnSubfix` 等在模板被清零后会重新填入默认模板。现 `SanitizeUntrustedOptions` 一并清零。
- **`DownloadTask.Snapshot()` 锁外读状态（RF-86）**：`Status`/`IsSuccessful` 由 `SetStatus` 在锁内成对写、读取却在锁外——查询与任务完成赛跑可返回 `status=Succeeded` 而 `isSuccessful=false` 的不一致快照。现读取一并入锁。
- **`/add-task` 队列满 429 缺 Retry-After（RF-83）**：与认证/查询限速不一致，客户端无法统一退避。现补齐。

### 改进

- **携 Cookie 请求的头阶段超时隐性减半（RF-66）**：`NoRedirectClient` 超时 1 分钟为超时矩阵唯一非 2 分钟项，RF-50 把 `GetWebSourceCoreAsync` 的 sendCookie 路径切到该池后，响应头阶段上限被 60s 截断。现对齐 `FromMinutes(2)`（与 `AppHttpClient`/`ApiTimeoutMs` 同不变量）。

### 测试

- **RF-60 的 tr-TR 回归测试假绿（RF-68）**：原输入 `"e-ac-3"` 不含小写 `'i'`，tr-TR 规则不触发、断言恒成立（把实现改回有缺陷的 `ToUpper()` 仍通过）。现改用含 `'i'` 的 `"avci"`，变异验证确认防线有效。
- **回环服务测试受本机系统代理干扰假红（RF-69）**：`ServeApiHttpTests` 的 `HttpClient` 未设 `UseProxy=false`，代理在线时回环请求被转发导致 `HostValidation_*` 断言失配。现固定 `SocketsHttpHandler { UseProxy = false }`。
- **新增 `res`/`fps` 净化回归测试**（RF-63，变异验证）。
- **AOT 绑定防线补齐 Settings 类型（RF-76）**：`AotCliBindingTests.SettingsTypes` 原只列 3 个类型，子命令参数类型改动不会失败。现补齐全部 10 个。
- **local-integration 门禁可静默空跑（RF-77）**：测试在找不到 ffmpeg 时 early-return 不产断言，job 仍报绿。现 CI 安装 ffmpeg 后显式断言 `command -v ffmpeg`。
- **零字节 .wvd 诊断退化（RF-78）**：空文件跑到索引空数组抛 `IndexOutOfRangeException`。现提前给可读提示（新增回归测试）。
- **DRM/登录响应体无大小上限（RF-79）**：`WidevineCdm`（2 处）与 `BBDownLoginUtil`（2 处）仍用 `ReadAs*Async`；`HTTPUtil.ReadContentBoundedAsync` 提为 public 后 4 处统一改经有界读取（64MB）。
- **DRM 测试名实不符/假绿（RF-88）**：`WvdDeviceKeyTests` 的 `ThrowsAny<Exception>` 会把意外 NRE 当通过，改精确类型；`WidevineCdmTests` 方法名含 `Logs` 却不断言日志，去名。
- **PR CI 不构建 Docker 镜像（RF-87）**：新增 `docker-build-smoke` job（构建 + serve 默认拒绝启动 + token 启动并 200/401）；`build_latest.yml` 加 `concurrency`。
- **消除两处假绿回归网（RF-75）**：入档粒度（`ArchiveGranularityTests`）与进度聚合（`DownloadProgressAggregationTests`）测试只驱动复刻副本；把生产逻辑改坏仍全绿。现抽为生产类型 `Program.ArchiveTracker`/`BBDownDownloadUtil.ProgressAggregator` 并由测试直接驱动（两个 helper 均经变异验证）。

### 文档

- **`API.md` 时间戳字段措辞修正（RF-71）**：`TaskCreateTime`/`TaskFinishTime` 标注"本机时区"，实际为 `ToUnixTimeSeconds()` 的 UTC 纪元秒（与时区无关）。
- **wiki 文档族 6 项（RF-84）**：Authentication 充电专属"退出码 2"（实为跳过/计失败）、Danmaku 格式 `protobuf`（实为 `xml,ass`）、Home 占位符计数 18→19、Subcommands 缺 live/article/watchlater 多个选项、API-Server 错误码缺 413/415、任务详情样例虚构 `TotalPages`/`Status:Finished`/`ErrorReason`（改为真实字段）。
- **Docker 配方挂载死路径（RF-85）**：文档挂载 `/app/downloads`、`/app/data`，但 serve 产物与凭据均在 `/app`（进程 CWD + 程序目录）——挂载无效、产物落容器可写层丢失。现改挂 `/app`，token 改经 `BBDOWN_SERVE_TOKEN` 环境变量。

> 注：第 15 轮登记的 RF-62（DRM `CryptographicException` 穿透两级过滤器）经消纳亲验**前提不成立**——`WidevineCdm.GetKeysAsync` 已有的 `catch (Exception) → return null` 已吞掉该类，异常不出取钥链。登记其逃逸链的记录失实，无需改动（已在 REVIEW_FINDINGS 标注）。

## [1.6.18] - 2026-09-15

### 修复

- **订阅历史文件损坏时 `sub check` 静默清零全部下载历史**：历史损坏被隔离后，逐 aid 循环的异常过滤器会吞掉专用的损坏异常继续跑完当前订阅——下一个 aid 记录时发现历史文件不存在，静默重建仅含自身的空历史并写回，下次 check 把所有订阅内容当作新增全量重下。现损坏异常立即终止整个检查（与存储层文档化的中止契约对齐）。
- **`sub check` 的 Ctrl+C 被记为订阅失败或退出码 130**：与文档"子命令取消返回 0"及 watchlater 的既有行为不一致。现与 watchlater 对齐——主动取消返回 0，token 未取消的中断按失败返回 1。
- **畸形清晰度 id 中止整批多 P 下载**：轨道排序对服务器可控的清晰度 id 裸 `Convert.ToInt32`，缺失/非数字/超 int32 时 FormatException/OverflowException 穿透页面级与批级两级失败隔离过滤器（与 v1.6.17 修复的 NotSupportedException 同族逃逸面），单个畸形节点即放弃剩余分 P。现 TryParse 降级，两级过滤器同步扩充。
- **`--skip-mux` 且无 ffmpeg 时下载杜比视界视频整批失败**：ffmpeg 版本探测的进程启动异常（Win32Exception）未被"探测失败"分支处理；且 SkipMux 下本就不需要探测。现过滤器兜底为走 mp4box + SkipMux 直接跳过探测。
- **专栏标题为 Windows 保留名时保存失败**：标题恰为 CON/NUL/COM1 等设备名时产物 `CON.md` 以设备名语义无法落盘，报误导性的"专栏获取失败"。专栏与直播文件名净化均接入保留名防护（与下载管线的 `GetValidFileName` 同一规则）。
- **serve 异常退出被记为退出码 0**：非用户取消的 OperationCanceledException（内部超时联动等）被取消分支吞掉，Docker restart 策略/systemd/CI 丢失崩溃信号。现取消分支补 token 守卫，未取消的异常落失败分支记日志并返回 1。
- **DRM 密钥临时文件"安全覆写"少覆写 1 字节**：固定写 64 个 NUL 覆写 65 字节的 `kid:key` 行，`FileMode.Create` 截断后最后一个字符仍留在盘上。现按实际载荷长度覆写。
- **外部程序"已解析但不可启动"中止整批多 P（RF-43）**：ffmpeg/mp4box/aria2c/mp4decrypt 的进程启动点对"路径存在但不可执行"（Unix 无执行位/Windows 损坏或错误架构二进制）抛出的 Win32Exception 不在两级失败隔离过滤器内，单个分 P 命中即放弃剩余分 P。现启动点统一规范化为 `InvalidOperationException`（消息带工具名）。
- **本地权限错误中止整批下载（RF-44）**：Windows 只读属性文件 `File.Delete`、受控文件夹访问、ACL 拒写抛出的 `UnauthorizedAccessException` 非 IOException 派生，穿透页面级与批级过滤器；多处清理子句只捕 IOException 与同文件双类型 catch 不一致。现两级过滤器与命令级过滤器（sub/watchlater）补齐该类型，7 处单类型清理 catch 对齐为双类型。
- **免二压重发降级后静默丢失杜比/Hi-Res 音轨（RF-45）**：pass 1 重发失败降级沿用旧文档时，音轨列表被无条件从旧文档重建（不含已追加的 dolby/flac），而追加守卫标记仍为 true——重试越忙越容易丢杜比且无任何日志。现列表重赋值仅在首轮执行，降级路径保持已含杜比/Hi-Res 的列表不动。
- **直播录制遇畸形响应整场终止（RF-46）**：live 接口返回 code=0 但缺 data/playurl_info 节点时抛 KeyNotFoundException，不在重连过滤器白名单——违背"不设重试上限、网络恢复自动续录"承诺。现逐级判空，缺节点按瞬态故障走既有退避重连。
- **`--use-app-api` 畸形响应中止整批（RF-47）**：APP 接口对非数字 id 抛 ArgumentException、对垃圾字节 protobuf 响应抛 InvalidProtocolBufferException，均不在两级过滤器内（姊妹接口 DmViewReply 已防）。现源头转译为 `InvalidOperationException`。
- **服务器可控 aid 越界中止整批（RF-48）**：收藏夹/合集条目 id 为 "0"/负数/超界大数时 `Page.bvid` getter 经 BV 编码抛 ArgumentOutOfRangeException 穿透过滤器。现编码失败回落原始 aid。
- **单稿件超时中止收藏夹/空间整批解析（RF-49）**：E1 超时类型统一为 TimeoutException 后，FavList/SpaceVideo/BuvidProvider 三处逐条降级过滤器未同步——一个稿件超时即放弃整批，装饰性 buvid3 超时竟能炸掉整个空间抓取。现三处过滤器补齐。
- **系列/合集错误响应诊断不可达（RF-52）**：先取 data 节点后查 code 的顺序让精心编写的中文错误诊断被英文裸 KeyNotFoundException 取代；合集误识别为系列的回退过滤器缺 KeyNotFoundException。现两处 fetcher（含分页）改为先查 code，回退过滤器补齐。
- **登录轮询 3xx 无 Location 被误报"重定向跳数超限"（RF-59）**：单跳无目标 ≠ 跳数超限，该确定性失败会中断扫码登录。现按终态读 body 返回（与 2xx 同路径）。
- **tr-TR 区域下选轨优先级查表静默退化（RF-60）**：`Audio.shortCodecs` 文化敏感 `ToUpper()` 在含 'i' 的编码串上产出 'İ'。现 `ToUpperInvariant()`。

### 改进

- **DRM 取钥支持取消**：`GetKeyWidevineAsync` 透传 CancellationToken 至许可证请求链路（原在薄封装处断链）——serve `/cancel` 与 Ctrl+C 在取钥窗口（2 分钟超时 ×3 次尝试，最长约 6 分钟）内不再不可中断。
- **评论 JSON / 专栏 Markdown 的时间戳固定 InvariantCulture**：自定义格式的 `:` 是时间分隔符占位符，fi-FI 等区域设置下产出 `12.00.00` 形态、产物跨机漂移；数据文件导出与控制台展示不同，必须文化无关。
- **TV 登录两个端点禁跟随重定向**：auth_code 获取与扫码轮询的 POST 体携带按 appsecret 签名的参数、轮询响应更是新下发 access_token 的通道，原走自动跟随重定向的共享客户端。现改禁跳转客户端 + 3xx 显式拦截（与 WEB 登录轮询、gRPC POST、Widevine 许可证的凭据收口同构），并顺带修复响应对象不释放的问题。这两个请求的客户端超时随之由 2 分钟收紧至 1 分钟（禁跳转客户端池的既定语义，与 WEB 登录轮询一致；单次小 POST 影响可忽略）。
- **携凭据的 API GET 收口为逐跳可信校验（RF-50）**：`GetWebSourceCoreAsync(sendCookie:true)`——全项目凭据最重的 GET 入口——仍自动跟随重定向，入口白名单只拦第一跳，3xx 可把完整 SESSDATA 引向任意主机。现改禁跳转客户端手动逐跳、每跳过 `IsTrustedCookieHost`（NoRedirect 收口族最后一名漏网成员）；匿名路径行为不变。
- **API 响应体读取统一 64MB 上限（RF-51，RF-28 消纳缺口接续）**：普通响应体（`GetWebSourceCoreAsync`/`GetWebSourceAnonymousCheckedAsync`）仍无界读取，被攻破端点或 `--insecure` 中间人可用分块慢发/巨包打满内存。现两处改有界读取 + charset 解码（含 BOM 剥离对齐），泛抓取路径顺带补 4xx/5xx 显式失败。
- **JSON 异常消息键名清单净化（RF-53）**：`GetPropertySafe` 把服务器可控的全部键名（可含控制字符）拼进异常消息落日志/终端。现剥离控制字符并截断保留前 8 个键名。
- **serve/CLI 日志注入收口到来源（RF-54）**：URL 拆解后的派生串（aidOri/fid/sid 等 query 值）以原始 CRLF 形态落日志，可伪造日志行。现 `ResolveAsync` 返回前统一单行化，含客户端原文的 LogError 一并套用。
- **webhook 域名零地址应答的误报（RF-55）**：校验侧空数组"空过放行"、连接侧 `addresses[0]` 越界把已成功任务打成"异常终止"。现两侧对齐（校验拒绝/连接跳过），回调过滤器放宽为 `catch (Exception)`（该 catch 目的只是"回调失败不影响任务"）。
- **DRM 工具搜索不再扫描当前工作目录（RF-57）**：`ToolFinder` 在 CWD 搜索 mp4decrypt/device.wvd，与 `FindExecutable` 建立的"绝不搜索 CWD"信任边界自相矛盾（可执行文件劫持面）。现仅搜索 PATH 与程序目录；显式路径选项不受影响。
- **输出文件名轨道元数据占位符净化（RF-58）**：`<dfn>/<videoCodecs>/<audioCodecs>` 是服务器透传值，镜像站/中间人可注入 `/` 或 `..` 穿越路径。现与 title 族一致统一过 `GetValidFileName`。

### 安全性

- **serve 请求体 `configFile` 字段防御性清零**：该字段是 DTO 从 MyOption 继承的死属性（实际由 argv 层处理、无消费点），若未来接通"按任务合并本地配置文件"会是指向服务器任意本地文件的注入点。提前清零。
- **serve 请求体 `area` 字段白名单（RF-56）**：Area 是唯一未收口的"拼进官方 API query"字段（任意文本注入 query 参数语义、跳过登录检测产生误导日志）。现仅接受 `hk`/`tw`/`th`（大小写不敏感），其余回落空值。

### 文档

- API.md：`DownloadTask` 字段清单补 `ErrorMessage`（失败原因，已单行化净化）与 `SavePaths`（服务器本地产物绝对路径）两个实际已序列化的字段；补 `/get-tasks*` 查询并发限速说明（429 + `Retry-After: 60`）。
- CLI-Reference：`--download-danmaku-formats` 示例 `xml,protobuf` 改为 `xml,ass`（枚举仅支持 xml/ass）；`--download-danmaku` 默认行为修正为同时保存 XML 与 ASS。
- README：`--show-all` 描述修正为"展示所有分 P 标题"（并非列出全部音视频流）；serve 子选项表补 `--notify-webhook`。
- 配置模板文档：占位符对照表补 `<videoDate>`（当前分 P 发布时间，接受与 `<publishDate>` 相同的格式后缀），计数 18→19。
- **RF-61 文档族**：`--save-archives-to-file` 产物说明修正为程序目录 `BBDown.archives`（CLI-Reference + Batch-and-Automation）；README 占位符表补 `<videoDate>`；API.md `/add-task` 补 413、忽略清单补 `configFile` 与 `area` 白名单、移除不存在的 serve `--work-dir` 表述、`ErrorMessage` 措辞与实现对齐。

### 测试增强

- 全库测试 700 例（第 14 轮消纳批新增 22 例，PR gate 过滤器）：`SanitizeLogString` 单行化契约（RF-54，此前零单测）；serve `area` 白名单（RF-56）；webhook 零地址 DNS 应答拒绝（RF-55）；`GetPropertySafe` 异常消息键名净化与截断（RF-53）；`Page.bvid` 越界 aid 回落（RF-48）；`Audio.shortCodecs` tr-TR 文化不变（RF-60）。
- RF-45（免二压降级丢杜比）的回归测试：`ExtractTracksAsync` 静态直连网络无注入缝，与 RF-30/RF-32 同批以代码走查 + 全量编译验证，测试缝待 DownloadOrchestrator 拆分（OPTIMIZATION_PLAN P0-1）后补。

## [1.6.17] - 2026-08-30

### 修复

- **mp4box 混流临时产物扩展名兼容性**：混流事务化的临时输出名 `.muxing-{guid}` 为未知扩展名，mp4box（GPAC）按扩展名推断输出封装格式，新版 filter-based MP4Box 上行为随版本而异（可能告警回退甚至失败）。现临时名以 `.mp4` 结尾，新旧版本全部确定性走 ISOM 封装（ffmpeg 分支本就用 `-f mp4` 强制格式，不受影响）。
- **多线程下载遇不支持 Range 的服务器时整批中止**：多线程分片路径在服务器以 200 响应 Range 请求时抛出的 `NotSupportedException`（及其它白名单外异常）会穿透页面级与批级两级 catch 过滤器，导致多 P 批量中一 P 命中即放弃剩余分 P、丢失完成通知与失败汇总。现抛出点规范化为 `InvalidOperationException` 并将过滤器补齐 `AggregateException`。
- **SubOnly 模式 ASS 字幕产物扩展名错误**：ASS 内容字幕被无条件改名为 `.srt`，播放器无法渲染。现按源字幕内容形态决定目标扩展名。
- **`<publishDate:...>`/`<videoDate:...>` 占位符在特定区域设置下产出非法文件名**：自定义日期格式的 `:` 是时间分隔符占位符，fi-FI 等区域下输出形如 `22.13`，en-US 下输出含 `:`（Windows 上可写入 NTFS 备用数据流，资源管理器不可见）。现固定 InvariantCulture 格式化并对替换值做文件名净化（与 ffmpeg `creation_time` 的收口同构）。
- **重跑已下载视频时封面/装饰文件残留**：混流锁内权威跳过分支漏删封面文件（每次残留一张且 aid 目录永远非空）；DASH 分支跳过路径的多处裸 `File.Delete`/`Directory.Delete` 在文件恰被杀软/索引器持有时抛 IOException，把"已下载成功"翻成整页失败重试。现已与 FLV 分支对齐统一包裹清理。
- **serve 交互式选项导致任务不可取消地占死并发槽**：客户端经 API 提交 `{"interactive":true}` 会让任务阻塞在 `Console.ReadLine`——不可被取消中断，少量任务即可占满 `--max-concurrent` 直至进程重启。现 serve 下强制关闭交互式模式。
- **特定区域设置下视频发布时间元数据丢失**：ffmpeg `creation_time` 时间戳的自定义格式未固定文化，`:` 在 fi-FI 等区域设置下被解析为时间分隔符占位符（产出形如 `19.30.00` 的非 ISO-8601 串），`av_parse_time` 解析失败后元数据静默丢失。现追加 `CultureInfo.InvariantCulture`。
- **杜比视界自动切 mp4box 时封面静默丢失**：`-itags` 的 `cover` 值（本地封面路径）未按 mp4box itags 值转义规则处理，Windows 路径中的 `\` 被当转义序列消费。现与同函数其它 itags 值一致走 `EscapeString`。
- **配置文件 URL 被 URL 形值的命令行选项误压制**：`BBDown.config` 中写了下载 URL、命令行又恰好携带值形似 URL 的选项（如 `--aria2c-proxy http://127.0.0.1:7890`、`--work-dir av123`）时，配置里的 URL 被误判"命令行已给出"而丢弃，Spectre 报缺少必填参数。现仅对位置参数应用 URL 启发式（选项值不再参与判定）。
- **FLV 跳过下载路径残留临时元数据文件**：FLV 跳过路径未清理封面、字幕与章节文件，且章节清理此前仅匹配固定名 `chapters` 而未覆盖 muxer 产出的唯一名 `chapters-{basename}`。现与 DASH 分支行为对齐并在跳过/失败路径按 `chapters*` 前缀兜底清理。
- **serve 已完成任务溢出裁剪按完成顺序误删**：`finishedTasks` 按完成时间追加，旧逻辑直接移除列表头部会误删"后创建但先完成"的任务。现改为按 `TaskCreateTime` 排序裁剪最旧创建的任务。
- **Parser 大会员回退硬编码域名与子串匹配脆弱**：番剧大会员回退网页源硬编码 `www.bilibili.com`（忽略 `EpHost` 镜像配置）；大会员错误判定依赖裸子串 `\"大会员专享限制\"` 易受文案漂移影响。现 host 跟随配置，判定优先解析 JSON 根 `message` 字段。
- **`BaseUrlRegex` 贪婪匹配误判 query 为端口**：原正则 `http.*:\d+` 会将 `http://host/path?x=1:2` 中 query 的 `:数字` 误判为端口。现收紧为 `^https?://[^/:]+:\d+`。

### 改进

- **serve API 响应与持久化健壮性**：`/get-tasks` 族响应补 `Cache-Control: no-store` 与 `X-Content-Type-Options: nosniff`（任务数据含服务器绝对路径，禁止缓存落盘），认证限速与查询限速的 429 响应补 `Retry-After: 60`；任务持久化临时文件名带 GUID（对齐订阅历史，同目录多实例不再互相踩踏写盘）；同一产物不再在任务快照中重复记录（`AddSavePath` 去重）。
- **wiki 同步脚本健壮化**：`scripts/sync-wiki.ps1` 检查 git 命令退出码（此前 commit 失败仍报成功）、自动清理 wiki 上源目录已删除的页面、异常时恢复调用目录。
- **进程与解析层健壮性**：直播杜比视界的 ffmpeg 版本探测改真异步（不再在下载链路同步阻塞最多 5 秒，超时后补观察管道任务）；用户取消（Ctrl+C/关停）不再被解析层"免二压重发"降级路径吞掉；免二压降级路径下杜比/Hi-Res 音轨不再重复追加；番剧 gRPC 移除与目标主机不符的硬编码 Host 头；收藏夹翻页遇空页提前结束（防畸形 media_count 触发请求洪泛）；国际版番剧接口删除多余的双重转义替换；登录轮询的重定向跳数上限改为确定性失败（不再误触发整流程重试）。
- **文档修正**：wiki 退出码表删除虚构的 `2`/`3` 退出码并修正 Ctrl+C 归属（主命令实际返回 130、子命令 0、工具缺失归 1）；参数总表补 `--host`/`--ep-host`/`--tv-host`/`--area`；README serve 配置注入说明补全选项列举。
- **CLI 命令层全量迁移 `AsyncCommand`**：`login`/`logintv`/`article`/`live`/`sub check`/`watchlater`/`serve` 7 个命令不再以 `Task.Run + GetAwaiter().GetResult()` 阻塞线程池线程等待异步操作（serve 长驻进程此前整个生命周期额外占用 1 个阻塞线程）；serve 链路改为真异步（`RunAsync`/`StartServerAsync`），监听地址前置校验独立为 `ValidateListenUrl`。各命令退出码与错误语义不变。

### 安全性

- **serve 无 token 模式 DNS rebinding 防线**：浏览器同源 GET 不携带 Origin，写端点的 Origin 校验保护不到读端点——攻击者网页经 rebinding（攻击者域名解析到 127.0.0.1）可读取 `/get-tasks*` 响应中的任务数据（含服务器绝对路径）。现无 token 时强制 Host 头为字面回环（localhost/127.0.0.0/8/::1，不做 DNS 解析），携带 token 的反代/自定义域名部署不受影响。
- **aria2c 输入换行注入面收口**：aria2c input-file 语法中换行即指令行分隔符，来自 API 响应的 URL 与用户配置的 Cookie 未剥离 CR/LF 时可注入任意 aria2c 指令行（新 URI/`all-proxy`/`dir` 等）。现写入 stdin 前剥离换行。
- **服务器可控文本不再直拼文件路径**：字幕语言代码（`lan`）、音频 ID（`audio_id`）来自接口响应（镜像站/`--insecure` 属项目威胁模型内的对抗源），含 `..\` 时可写出工作目录。现统一经 `GetValidFileName` 净化后再拼路径。
- **HTTP 响应体大小上限**：gzip 解压侧已有 48MB 上限，但压缩响应体本体与普通响应体无上限，被攻破端点或 `--insecure` 中间人可用巨包/分块慢发响应耗尽进程内存。现统一 64MB 上限（Content-Length 预检 + 逐块累计双拦截）。
- **serve 日志注入残留收口**：解析失败日志两处原始 `option.Url` 未单行化，客户端可借请求体 URL 中的 CR/LF 伪造日志行。现统一过 `SanitizeLogString`。
- **Widevine 许可证请求禁跟随重定向**：许可证 POST 的请求体是设备私钥签名的 challenge，原 `VerifiedAppHttpClient`（允许自动重定向）会在 307/308 上连同 body 重放到跨主机目标。新增 `VerifiedNoRedirectClient`（始终校验证书 + 禁自动重定向，独立连接池不受 `--insecure` 降级），3xx 显式报错不跟随（与 gRPC POST 的凭据收口同构）。
- **serve 认证失败限速字典有界裁剪**：防止攻击者使用大量独立 IP 或伪造 XFF 标头导致限速记录字典无界增长，在超过上限 `MaxTrackedAuthFailureIps` 时按最后失败时间自动裁剪，保留最近活跃记录。
- **登录轮询跟随重定向逐跳可信主机校验**：`GetWebSourceWithSetCookiesAsync` 切换为禁自动跳转客户端手动逐跳，每跳发起前校验 `IsTrustedCookieHost`，防止重定向将用户凭证及下发的 `Set-Cookie` 泄露至不可信主机。

### 测试增强

- 全库测试 666 例（新增 7 例）：serve Host 白名单端点行为（evil Host 403/回环放行/带 token 跳过校验）与纯函数判定、aria2c stdin 单行化、日期占位符文化无关与路径净化（fi-FI 区域下断言）、HTTP 响应体上限判定、serve Interactive 清零等。
- 此前批次（659 例，新增 25 例）：VerifiedNoRedirectClient 身份稳定性、GET/POST 307 不跟随、配置合并 URL 形值选项回归、章节前缀清理、认证字典限速有界裁剪、已完成任务溢出按创建时间排序、大会员 JSON message 字段解析、BaseUrlRegex 严格匹配、登录轮询逐跳重定向安全拦截与合法跟随等。

## [1.6.16] - 2026-08-19

### 修复

- **带字幕/副音轨视频混流必失败**：`-metadata:s:s:N`/`-metadata:s:a:N`/`-disposition` 等 ffmpeg 输出选项原紧跟在各自 `-i <输入>` 之后被当作输入选项——旧版 ffmpeg 直接报 `Option ... cannot be applied to input url ...srt` 导致带 CC 字幕/多音轨视频合并必失败，新版则静默丢失 stream 元数据（字幕标题等写不进输出流）。现统一收集到 `outputArgs` 在全部 `-i`/`-map` 完成后追加，并新增单元（输出选项必须位于最后 `-i` 之后）与真实 ffmpeg/ffprobe 集成（字幕 language 写入验证）回归测试。
- **`--comments` 评论永远保存失败**：评论在混流（MuxAV 才创建输出目录）之前保存，目标父目录不存在时 `DirectoryNotFoundException` 被降级吞掉。`SaveToJsonAsync` 保存前自动创建父目录（保存函数自包含），并补父目录缺失场景回归测试。

### 改进

- **`article`/`live` 子命令支持 `--work-dir`**：默认输出路径（专栏 Markdown / 直播录制与 `.segs` 分段）落到指定工作目录；用纯函数 `ResolveWorkDir` 解析（仅展开环境变量+建目录），不切进程 CWD、无全局副作用。
- **单选分 P 命名修正**：保存路径模板改按【实际下载分 P 数】决策——`-p` 单选 1 集时即使视频总 P>1 也走单 P 模板（`-F` 生效、产物不再带 `[P##]` 前缀）；番剧未完结仍固定按多 P 处理。
- **弹幕/评论文档与帮助文本**：`--download-danmaku-formats` 注明仅支持 `xml,ass`；`--danmaku-filter` 注明仅作用于 ASS 弹幕（XML 保留原始全量）；`--comments` 注明仅在第 1 个分 P 下载一次（视频级防重复）；README 同步补充。
- **审查收尾**：抽出 `Program.ResolveWorkDir` 纯函数消除 article/live 的进程 CWD 副作用，并澄清 Workflow 中仅保留 API 形状的冗余赋值。

### 测试增强

- 全库测试 629 例（新增 7 例）：路径模板决策（单/多 P、番剧、-F/-M 组合）、混流出选项位置全场景（主+2 副音轨+封面+2 字幕）、真实 ffmpeg 带字幕混流后字幕 language 经 ffprobe 验证写入、评论父目录缺失自动创建等。

## [1.6.15] - 2026-08-19

### 修复与安全性加固

- **serve 模式令牌优先级修复**：修复 `--serve-token` 与 `BBDOWN_SERVE_TOKEN` 优先级反转缺陷，确保环境变量优先于 CLI 参数，并在两者同时设置且值冲突时记录警告日志。
- **B 站官方域名白名单统一**：抽取 `HTTPUtil.OfficialHostSuffixes` 与 `IsOfficialBilibiliHost` 为唯一定义源，消除 `HTTPUtil.CookieTrustedHosts`、`UrlResolver.TrustedBilibiliHosts` 与 `BBDownApiServer.OfficialHostSuffixes` 之间潜在的白名单漂移风险。
- **serve 并发限流与测试隔离**：`/get-tasks` 族查询端点引入并发信号量限流（上限 8），防止高并发深拷贝任务快照耗尽 CPU/GC；将 `BBDownApiServer._persistFailures` 改为实例字段，杜绝并发测试实例间的状态串扰；新增 `--trusted-proxy` 选项以支持反代 XFF 客户端 IP 计键限速。
- **凭据外发与传输层纵深防御**：带 Cookie 的请求在发送前强制校验目标是否为官方或显式配置的信任域名，拦截非可信凭据外发；`--insecure` 不安全连接的 Date 响应头不再参与全局时钟校准（防跨流 WBI 扰动）；gRPC POST 拦截 3xx 重定向防凭据随跳转外发；媒体下载切换至禁用自动跳转的 `MediaDownloadClient`；gzip 解压设定 48MB 安全上限防解压炸弹；gRPC 帧首字节合法性校验；异常消息回显前剥离控制字符。
- **解析与格式化修复**：强制数字解析使用 `CultureInfo.InvariantCulture` 避免特定区域设置下解析异常；完善 gRPC 必填参数校验；拦截负数分 P 参数；修复国际站（biliintl/bilibili.tv）域名匹配与解析容错；修复弹幕 ASS 格式化及分 P 别名缺陷。
- **分片清理一致性**：统一轨道分片类型判定全部走 `IsVideoClipPath`（不区分大小写），修复 `DownloadClipsAsync` 返回分片路径处仍使用大小写敏感 `EndsWith(".mp4")` 判断的漏网，消除极端文件名下视频/音频分片 `.vclip`/`.aclip` 分类与清理规则错位的可能。

### 测试增强

- 全库测试扩充至 600+ 例：新增 serve token 环境变量优先级测试、B 站官方域名白名单覆盖测试、进程树哨兵验证、3-clips 分片哈希比对、查询并发信号量端点测试等。

## [1.6.14] - 2026-08-18

### 修复与安全性加固

- **serve 三层安全防护**：认证失败按来源 IP 滑动窗口限速（1 分钟 5 次后返回 429）并记录 401 失败日志，令 `X-Serve-Token` 暴力枚举失效；写端点（`/add-task`/`/cancel`/`/remove-finished`）新增 CSRF/跨源防护——校验 `Origin` 必须为回环来源或缺失（非浏览器客户端），`/add-task` 强制 JSON Content-Type（`text/plain` 是 CORS 简单请求载体，可直接跨源发出不触发预检）；任务错误消息经 `/get-tasks` 返回前脱敏绝对路径，防止泄露服务器文件系统布局。
- **日志与敏感信息脱敏**：`PlayViewReply` 调试日志不再全文落盘——1KB 截断并脱敏媒体 URL 中的 `sign`/`x_sign`/`w_rid` 签名参数（临时签名 CDN 地址落入日志等于外泄下载权）；敏感键新增 `DedeUserID`。
- **外部工具查找防劫持**：`FindExecutable` 不再搜索当前工作目录（BBDown 常在下载目录运行，目录中先前植入的 `ffmpeg.exe`/`aria2c.exe`/`mp4box.exe` 伪造二进制会被静默执行），改为程序目录优先 + PATH；Unix 上校验执行位。
- **时钟校准收窄**：仅对 WBI 签名权威主机 `api.bilibili.com`（含子域）校准服务器时钟，其它边缘服务器（番剧/国际版）的 Date 不再写入全局偏移（避免抖动签名基准）；偏移阈值从 ±24h 收紧到 ±1h。
- **HTTP 重试与超时语义**：登录轮询（GET）加入与常规请求一致的有界重试 + 指数退避（此前零重试，任一次瞬时 5xx/超时直接中断扫码流程）；POST 超时转可读 `TimeoutException`（调用方均为幂等查询，保留 5xx/超时有界重试）；风控 HTML 识别双剥 BOM，堵住裸 `JsonException` 回潮。
- **取消信号正确传播**：Widevine 许可证请求、混流、通知回调中的 `OperationCanceledException` 不再被 `catch (Exception)` 吞掉——取消后不再误报"解密失败"、不再打印"任务完成"。
- **混流轨道清理兜底**：已下载的音视频/字幕/封面轨道清理并入 `finally`（`CleanupDownloadedTracks`），混流失败/异常/取消路径不再残留 GB 级临时文件；`Decrypt` 终止进程后等待 stderr 任务，消除 `UnobservedTaskException`。
- **解析防御性加固**：bilidrm kid 仅接受 32 位 hex（畸形 URI 不再一路带到 mp4decrypt）；flv 最高清晰度重发失败（网络/超时/解析异常）沿用首次已校验响应降级；互动视频 `player.so` 解析降级为可读错误；WBI 密钥材料长度校验（短于 58 字符时降级保持原密钥而非越界崩溃）。
- **文件名与语言码修复**：文件名尾随点/空格裁剪（Windows 拒绝以点/空格结尾的名称，纯点串兜底产出合法基名）；字幕语言码 BCP-47 大小写规范化（`zh-tw`→`zh-TW`/`yue-hk`→`yue-HK`，修复小写语言码被错误标记为 und）。
- **混流与进程健壮性**：无主音轨时素材元数据下标起点修复（标题不再错位）；aria2c 进程级 6 小时兜底超时（防僵死永久占并发槽）；未闭合引号不再吞掉整段 `--aria2c-args` 配置；直播分段合成捕获 `Win32Exception`。

### 新增测试

- 全库 515 个测试全部通过：新增文件名尾随点/空格与纯点串净化 8 例、serve 错误消息脱敏 URL/相对路径 2 例；时钟校准（±1h 阈值、非权威主机不覆盖偏移）与 POST 超时重试语义测试更新。

## [1.6.13] - 2026-08-18

### 修复与安全性加固

- **直播录制加载登录凭据，自动录制账号可看最高画质**：`live` 命令此前不加载凭据，`getRoomPlayInfo` 对未登录请求只返回游客画质（最高 720P）；现在录制前统一走 `InitializeRequestSessionAsync`（`--cookie` 显式传入优先，否则读取本地 `BBDown.data`），并新增 `--cookie` / `--access-token` 选项；画质请求从 `qn=10000` 提升为 `qn=30000`（按账号权限回落，接口只列 ts/fmp4 时自动回退 `qn=10000` 再取一次），启动时打印实际解析到的画质；登录检测超时自动安全降级，防止离线启动时异常崩溃。
- **Ctrl+C 停止保留已录制内容**：取消发生在分段读取中时，已写字节此前会随"无数据"分支整段丢弃（录制几分钟后 Ctrl+C 会丢掉全部内容）；现在读/写阶段被取消都照常返回已写字节，当前分段计入已录内容并参与合成保存。
- **录制结束与终结态清理临时目录**：成功后或未录到任何分段便遇终结态异常退出时，不再残留空的 `path.flv.segs` 根目录（会话目录清空后自动删除空的 `.segs`；根下仍有其它会话的保留分段时不删）；合成失败仍保留分段供手动恢复。
- **断流/网络中断自动重连续录**：
  - 读停滞看门狗：连接既不复位也不 EOF、只是不再有数据（网络黑洞）时，无超时客户端上的 `ReadAsync` 会永久挂起、录制卡死——现在每收到数据重置 60 秒计时，超时按读中断自动重连；
  - 无限重连：旧实现重连 3 次即放弃，断网几分钟必然提前终止、网络恢复后不会自动继续——现在只要直播间仍在直播且用户未取消，就持续指数退避重试（3s→30s 封顶），网络恢复后自动续录；
  - 分段尾裁剪：网络中断/取消使段尾只剩半个 FLV 标签时，ffmpeg concat demuxer 会在截断处报错中止整个合成——合成/改名前先把每个分段裁到最后一个完整标签，且支持 Filter 标志掩码识别；
  - 终结态区分：直播间不提供 flv（仅 HLS/ts）与本地写盘失败（`LiveStreamWriteException` 异常过滤对齐）不再无限重试，直接报错并保留已录分段。

### 新增测试

- 直播录制集成测试新增至 499 个并全部通过：取消保留内容、断流重连合成、读停滞看门狗重连、下播 NoData、`qn=30000` 最高画质请求与回落、无 flv 终结态、FLV 截断尾裁剪（含 Filter 位）、终结态空目录清理与登录检测取消传播/超时降级等用例（本地假 B 站服务器覆盖完整录制循环）。

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

[Unreleased]: https://github.com/AliverAnme/BBDown/compare/v1.7.3...HEAD
[1.7.3]: https://github.com/AliverAnme/BBDown/compare/v1.7.2...v1.7.3
[1.7.2]: https://github.com/AliverAnme/BBDown/compare/v1.7.1...v1.7.2
[1.7.1]: https://github.com/AliverAnme/BBDown/compare/v1.7.0...v1.7.1
[1.7.0]: https://github.com/AliverAnme/BBDown/compare/v1.6.22...v1.7.0
[1.6.22]: https://github.com/AliverAnme/BBDown/compare/v1.6.21...v1.6.22
[1.6.21]: https://github.com/AliverAnme/BBDown/compare/v1.6.20...v1.6.21
[1.6.20]: https://github.com/AliverAnme/BBDown/compare/v1.6.19...v1.6.20
[1.6.19]: https://github.com/AliverAnme/BBDown/compare/v1.6.18...v1.6.19
[1.6.18]: https://github.com/AliverAnme/BBDown/compare/v1.6.17...v1.6.18
[1.6.17]: https://github.com/AliverAnme/BBDown/compare/v1.6.16...v1.6.17
[1.6.16]: https://github.com/AliverAnme/BBDown/compare/v1.6.15...v1.6.16
[1.6.15]: https://github.com/AliverAnme/BBDown/compare/v1.6.14...v1.6.15
[1.6.14]: https://github.com/AliverAnme/BBDown/compare/v1.6.13...v1.6.14
[1.6.13]: https://github.com/AliverAnme/BBDown/compare/v1.6.12...v1.6.13
[1.6.12]: https://github.com/AliverAnme/BBDown/compare/v1.6.11...v1.6.12
[1.6.11]: https://github.com/AliverAnme/BBDown/compare/v1.6.10...v1.6.11
[1.6.10]: https://github.com/AliverAnme/BBDown/compare/v1.6.9...v1.6.10
[1.6.9]: https://github.com/AliverAnme/BBDown/compare/v1.6.8...v1.6.9
[1.6.8]: https://github.com/AliverAnme/BBDown/compare/v1.6.7...v1.6.8
[1.6.7]: https://github.com/AliverAnme/BBDown/compare/v1.6.6...v1.6.7
[1.6.6]: https://github.com/AliverAnme/BBDown/compare/v1.6.5...v1.6.6
[1.6.5]: https://github.com/AliverAnme/BBDown/compare/v1.6.4...v1.6.5
[1.6.4]: https://github.com/AliverAnme/BBDown/compare/v1.6.3...v1.6.4
[1.6.3]: https://github.com/AliverAnme/BBDown/compare/v1.6.2...v1.6.3
[1.6.2]: https://github.com/AliverAnme/BBDown/compare/v1.6.1...v1.6.2
[1.6.1]: https://github.com/AliverAnme/BBDown/compare/v1.6.0...v1.6.1
[1.6.0]: https://github.com/AliverAnme/BBDown/releases/tag/v1.6.0
