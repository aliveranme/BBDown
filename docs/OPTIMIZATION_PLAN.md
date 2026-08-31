# BBDown 优化方案（OPTIMIZATION_PLAN）

> 创建时间：2026-08-31
> 审查范围：`BBDown/`（CLI / Application / Infrastructure / Configuration / Utilities）+ `BBDown.Core/`（Parser / Fetcher / Util / DRM / Entity）+ `BBDown.Tests` + `.github/workflows` + `Directory.Build.props` / `global.json` / `.editorconfig`
> 审查方式：全量源码精读（`bin/obj` 除外）+ 构建/CI 配置核验 + 测试覆盖盲区扫描
> 基线：`dotnet build -c Release 0 警告 0 错误` / `dotnet test` PR 门禁过滤器全绿 / Native AOT `PublishAot=true` 生效

---

## 1. 总体结论

项目整体工程成熟度高，已具备多轮生产问题收敛痕迹（代码中 `RF-` 系列注释覆盖安全/韧性/正确性）。核心亮点：

- `BBDown.Core/Util/HTTPUtil.cs:124` 六池隔离（校验/不安全 × App/Media/Streaming + `VerifiedNoRedirectClient` / `NoRedirectClient`），`AllowAutoRedirect=false` 对媒体与 gRPC 凭据载荷的逐跳校验一致
- `BBDown/Infrastructure/BBDownDownloadUtil.cs:150` `ArrayPool<byte>` + 原子进度 + `Content-Range` 交叉校验 + `Range/If-Range` 续传清单
- `BBDown/Infrastructure/BBDownApiServer.cs:173` 认证限速（滑动窗口+裁剪）/ CSRF（Origin 回环校验）/ `Host` 回环白名单（`IsLoopbackHost`）/ 路径穿越收口
- `BBDown.Core/Config.cs:15` `AsyncLocal<AppSettings>` 隔离 `serve` 并发任务的凭据/Host 污染

**无致命缺陷。** 现存债务集中在三类：**单体过重导致的可维护性债、构建可重现性与依赖新鲜度、少量同步 IO 与重复逻辑**。本方案按 ROI 分级，所有条目均给出位置锚点、处置建议与工作量估算。

---

## 2. 分级优化清单

### P0 — 高收益 / 低风险，建议优先落地

#### P0-1 拆分 `Program` 局部类巨石

- **位置**：`BBDown/Application/Download.cs`（`DownloadPageAsync` 约 550 行、`Download.cs:548` 自注"此处代码简直灾难"）、`BBDown/Application/Workflow.cs` / `Options.cs` / `BBDown/Infrastructure/BBDownDownloadUtil.cs` 均为 `partial class Program` 扩散；`BBDown/Program.cs:27` `IsServeMode` 静态状态
- **问题**：单文件过长导致 review / 单测困难；`Program` 静态字段是并发污染根因（虽已靠 `AsyncLocal` 修补，但静态可变状态本身仍是风险源）；`Download.cs` 内 dash/flv 双分支、弹幕/封面/字幕/章节清理多处重复
- **建议**：
  1. 新建 `BBDown/Services/DownloadOrchestrator.cs` 接管 `DownloadPagesAsync` / `DownloadPageAsync` / `MuxAndFinalizeAsync` / `DeleteResidualChapterFiles`
  2. `BBDownDownloadUtil` 从 `internal static` 改为 `IDownloadService` 接口，构造函数注入 `IExternalProcessRunner` / `ILogger`，`BBDownApiServer` 与 CLI 共用实例而非静态方法
  3. `Options.cs` 的 `HandleDeprecatedOptions` / `ParseEncodingPriority` / `FindBinaries` 抽为 `OptionNormalizer` 纯静态工具，去掉对 `Program.SinglePageDefaultSavePath` / `MultiPageDefaultSavePath` 静态字段的读写
- **收益**：单测可 `new DownloadOrchestrator(fakeRunner, fakeHttp)` 直测，`InternalsVisibleTo` 白盒测试可逐步转为黑盒；`IsServeMode` 可收敛为 `AppSettings` 字段
- **工作量**：3–5 天（含回归用例补齐）

#### P0-2 同步 IO 阻塞异步路径

- **位置**：`BBDown/Application/Download.cs:445` `File.ReadAllText`、`BBDown/Configuration/BBDownConfigParser.cs:141` `File.ReadAllLines`、`BBDown/Application/Options.cs:327` `File.ReadAllText`（`BBDown.data` / `BBDownTV.data`）、`BBDown/Utilities/BBDownUtil.cs:106` `CopyToAsync` 已异步但外层仍有同步 `File.Exists` / `Directory.Exists` 紧邻
- **问题**：`async` 链上同步阻塞线程池；`serve` 并发下放大
- **建议**：对应改为 `ReadAllTextAsync` / `ReadAllLinesAsync` 并透传 `CancellationToken`；`File.Exists` 保留同步（无异步替代）但避免在热路径重复 `GetFileName` / `GetFullPath`
- **工作量**：半天

#### P0-3 依赖停滞与供应链可重现性

- **位置**：
  - `BBDown/BBDown.csproj:39` `SharpZipLib 1.4.2`（2023-01 后无发布，历史 4 CVE）
  - `BBDown/BBDown.csproj:18` `NoWarn` 一揽子抑制 `IL3050;IL3000;IL3001;IL3002;IL2067;IL2104`
  - 无 `packages.lock.json` / `Directory.Packages.props`（CPM），`pr.yml:26` / `build_latest.yml` 每次独立 `dotnet restore` 无缓存
- **建议**：
  1. 评估 `SharpZipLib` → `System.IO.Compression`（.NET 内置已支持 Zip，项目仅用于解压）或 `SharpCompress`；若仅解压可直接迁移，移除外部依赖
  2. 引入 `Directory.Packages.props` + `packages.lock.json`，`actions/setup-dotnet@v4` 开 `cache:true`，`pr.yml` 六个 job 可省 30–40s `restore`
  3. `NoWarn` 改为按文件局部 `#pragma warning disable IL3050` 或确认 `TrimmerRootAssembly` 已覆盖后移除全局抑制；CI 新增 `dotnet publish -p:TreatWarningsAsErrors=true` 定时审计
- **工作量**：1–2 天

#### P0-4 `Sdk="Microsoft.NET.Sdk.Web"` 收敛

- **位置**：`BBDown/BBDown.csproj:1`
- **问题**：主程序是 CLI + 嵌入式 Kestrel（`BBDownApiServer.cs` 仅需 Minimal API），Web SDK 隐式引入静态资源/Razor 等与 AOT 无关的裁剪分析；`Directory.Build.props:6` 已 `PublishAot=true`，Web SDK 的 AOT 兼容面比普通 SDK 窄；`EnableStaticWebAssets:false` 是为此打的补丁
- **建议**：改 `Microsoft.NET.Sdk`，显式 `<FrameworkReference Include="Microsoft.AspNetCore.App" />`，移除 `EnableStaticWebAssets` 补丁，裁剪更可控
- **工作量**：半天（含 `dotnet publish -r win-x64/linux-x64` 冒烟）

---

### P1 — 中收益，建议排期

#### P1-1 Fetcher 重复解析逻辑

- **位置**：`BBDown.Core/Fetcher/NormalInfoFetcher.cs:16` / `BangumiInfoFetcher.cs:16` / `CheeseInfoFetcher.cs` / `IntlBangumiInfoFetcher.cs` / `FavListFetcher.cs` / `MediaListFetcher.cs` / `SeriesListFetcher.cs` / `SpaceVideoFetcher.cs` 共 8 实现
- **问题**：共享模式 `HTTPUtil.GetWebSourceAsync` → `JsonDocument.Parse` → `code != 0` 抛错 → `EnumerateArraySafe` 组装 `VInfo` 重复；仅 `FetcherFactory` 有路由测试，8 个 Fetcher 零覆盖（`BBDown.Tests` 最大盲区，B 站接口变更最先断裂处）
- **建议**：抽 `FetcherBase` 提供 `FetchJsonAsync<T>(url, token)` + `EnsureSuccessCode(doc)` 模板，子类只实现 `ParseVInfo(JsonElement data)`；为每个 Fetcher 补 1 个离线 JSON 快照用例（仿 `ParserFixtureTests` 的 `Fixtures/parser/` 夹具回放，已有 `FakeBilibiliApiServer` 先例）
- **工作量**：2–3 天

#### P1-2 `Parser.cs` JsonDocument 生命周期与解析性能

- **位置**：`BBDown.Core/Parser.cs:251` Dash 分支 `try/catch + finally` 双重 `Dispose`、`intl` 分支 `code=0/1` 两次 `Parse`、全程 `GetInt32Safe` / `GetValueAsStringSafe` 反复 `TryGetProperty` 线性扫描
- **问题**：整体正确但易误改；高频 `TryGetProperty` 在超大 `playurl` 响应上为线性扫描
- **建议**：统一 `using var` 作用域；对固定结构引入 `JsonSerializerContext` 源生成反序列化（AOT 兼容，`Program.cs:63` 已有 `MyOptionJsonContext` 范例），减少 `JsonElement` 反复查找
- **工作量**：1–2 天

#### P1-3 CI 重复与 EOL 镜像

- **位置**：`pr.yml` 6 个 job 各自 `checkout + setup-dotnet + restore`；`build_latest.yml:91` 在 `ubuntu:18.04` 容器内 `wget` SDK 无缓存且 18.04 已 EOL；`pr.yml:102` `network-integration` `continue-on-error:true`
- **建议**：抽 `composite action` 复用 `setup`；`build_latest.yml` 的 glibc 兼容构建改 `ubuntu:20.04` 或 `dotnet-buildtools/prereqs:ubuntu-22.04` 并加 `actions/cache` 缓存 SDK tarball；`network-integration` 改为仅 `schedule` 或 `workflow_dispatch` 跑，避免 PR 噪音
- **工作量**：半天

#### P1-4 重试参数双轨收敛

- **位置**：CLI `BBDown/Application/Options.cs:240` `ValidateNumericOptions`（`RetryCount 1–100 / RetryDelay 0–600s`）与 serve `BBDown/Infrastructure/BBDownApiServer.cs:825` `SanitizeUntrustedOptions` 的 `Math.Clamp(1,3) / (0,5000)` 二次收敛分散
- **建议**：抽 `RetryPolicy.NormalizeForCli` / `NormalizeForServe` 统一入口，`SanitizeUntrustedOptions` 仅调后者；新增参数时不遗漏 clamp
- **工作量**：半天

---

### P2 — 低收益 / 长期项

| 编号 | 位置 | 说明 | 建议 |
|------|------|------|------|
| P2-1 | `BBDown.Core/Util/SubUtil.cs:30` / `BBDown/Utilities/BBDownUtil.cs:227` | `SubTagRegex` 的 BCP-47 规范化已充分，正则已全部 `GeneratedRegex` | 维持现状，无需优化 |
| P2-2 | `BBDown.Core/Util/PathUtil.cs:26` | `ReservedNames` + `TrimEnd('.',' ')` + `maxBaseNameLength=100` 已覆盖 Windows 保留名/尾点空格/超长标题 | 维持现状 |
| P2-3 | `BBDown.Core/DRM/WvdDevice.cs` / `WidevineCdm.cs` | 内存中私钥处理 | 确认 `CryptographicOperations.ZeroMemory` 覆盖；`CkcDecryptor` 零测试是盲区，补 1 个向量用例 |
| P2-4 | `BBDown/Infrastructure/ExternalProcessRunner.cs` | 进程执行边界已加 5s 管道兜底与 `Kill(entireProcessTree:true)` | 维持现状（RF-22 探针已修复为 `CheckFFmpegDOVIAsync`） |
| P2-5 | `BBDown/Configuration/BBDownConfigParser.cs` | `BuildAliasMap` 反射扫描 `CommandOptionAttribute` | 可加静态缓存（已在 P0-1 拆分时一并处理），单独优化收益低 |
| P2-6 | 日志 | `Logger.cs` 静态文本日志 + `SensitiveDataMasker` 已覆盖全面 | `serve` 长驻场景可选 `Microsoft.Extensions.Logging` + JSON 行，便于上游收集 |

---

## 3. 已确认无需优化项（避免重复评估）

- `HTTPUtil` 六池隔离（`_appHttpClient` / `_insecureAppHttpClient` / `_mediaHttpClient` / `_verifiedNoRedirectClient` 等 `Lazy<HttpClient>`）已正确隔离校验/不安全与重定向策略，不建议引入 `IHttpClientFactory`（会破坏现有 AOT 友好的静态池设计）。
- `Config.AsyncLocal<AppSettings>` 双写（`Config.cs:32` `Apply` 同时写 `_contextSettings` 与 `_settings`）是 `serve` 并发凭据隔离的正确实现，已有 `ConfigPropagationTests` 覆盖，不应重构为 `IConfigProvider`。
- `BBDownDownloadUtil` 的 `Interlocked` 原子进度 + `ArrayPool<byte>.Shared.Rent(256KB)` 已避免 LOH 与 `ConcurrentDictionary.Values` 快照开销，无需改为 `Channel`。

---

## 4. 路线图

| 阶段 | 工作 | 产出 | 依赖 |
|------|------|------|------|
| 第 1 周 | P0-3 依赖与供应链：SharpZipLib 评估 + `NoWarn` 收敛 + `lock` 文件 + CI 缓存 | PR 1 | 无 |
| 第 1 周 | P0-2 同步 IO 异步化 + P0-4 Sdk.Web 收敛 | PR 2 | 无 |
| 第 2–3 周 | P0-1 巨石拆分（`DownloadOrchestrator` + `IDownloadService`） | PR 3（大） | 需补回归用例 |
| 第 4 周 | P1-1 Fetcher 基类 + 快照用例 + P1-2 Parser 源生成 | PR 4 | PR 3 合入后 |
| 持续 | P1-3 CI 镜像升级 / P1-4 重试收敛 / P2-3 DRM 向量用例 | 小 PR | 按需 |

> 约束重申：`global.json`  pinned `10.0.300` / `PublishAot=true` / `dotnet format --verify-no-changes` 硬门禁 / `failSkips:true` / LF + UTF-8 + 4 空格（`.editorconfig`）在所有改动中保持。

---

## 5. 归档信息

- 归档位置：`docs/OPTIMIZATION_PLAN.md`（本文件）
- 关联文档：`docs/REVIEW_PLAN.md`（剩余修复排期）/ `docs/REVIEW_FINDINGS.md`（RF-1..RF-29 处置结论）/ `docs/MAINTENANCE_PLAN.md`（Parser 护栏与文档同步）
- 下一步：按路线图建分支（`refactor/` / `fix/` / `deps/` 前缀，Conventional Commits），每 PR 关联本文件对应 P 编号
