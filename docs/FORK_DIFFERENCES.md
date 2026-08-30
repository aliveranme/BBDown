# 本 fork 与上游差异（FORK_DIFFERENCES）

> **用途**：记录 aliveranme/BBDown（本仓库）相对上游 [nilaoda/BBDown](https://github.com/nilaoda/BBDown) 的实质性差异，供同步上游、发布说明与协作者快速对齐认知。
> **快照基准**：本 fork `master` @ `11bc7fc`；上游 `master` @ `259a555`（适配新版空间合集/系列链接，#1077）。merge-base 即上游顶端——本 fork 领先上游 **313 个提交**，上游无未合入提交。总量：221 文件，+30,446 / −5,121 行。
> **更新时机**：每次合入上游新提交或本 fork 发版后，更新"快照基准"与受影响条目（见文末维护说明）。

---

## 一、上游已具备、本 fork 继承并大幅演进的能力

避免"把上游既有能力误记为本 fork 新增"，先列这一节：

| 能力 | 上游状态（259a555） | 本 fork 演进 |
|------|--------------------|--------------|
| `serve` / API 服务 | `BBDownApiServer.cs` 约 210 行极简任务 API | 约 1,460 行：任务队列/JobId 契约、认证限速、CSRF/Origin 校验、webhook 回调 SSRF 防护、任务持久化、订阅任务接入（详见 2.4） |
| Native AOT | `net9.0` + `PublishAot=true`（`Directory.Build.props` 已有） | 升级 `net10.0`；修复系列 AOT 运行时崩溃（Spectre CommandSettings 元数据保留等）；新增 `AotCliBindingTests` 防 CLI 选项绑定回归 |
| Dockerfile | 存在 | 按 AOT SDK 镜像重建（`sdk:10.0-aot` 构建 + `runtime-deps:10.0` 运行）、非 root 用户、默认拒绝非回环无 token 启动 |
| 8 个 Fetcher / Parser / HTTPUtil / SubUtil | 存在 | 深度重构与加固（防御性解析、重试、脱敏），差异见三、四节 |

## 二、用户可见的功能差异

### 2.1 CLI 命令框架与子命令

- 命令框架：System.CommandLine（上游）→ **Spectre.Console.Cli**（本 fork），8 个命令全部 `AsyncCommand`（无线程池阻塞）。
- 上游仅 3 个子命令：`login` / `logintv` / `serve`。
- 本 fork 新增 4 个子命令：

| 子命令 | 功能 | 关键实现 |
|--------|------|---------|
| `live` | 直播录制 | `LiveStreamUtil`（FLV 分段、读停滞看门狗、短段退避重连、TrimFlvTail） |
| `sub` | 订阅下载 | `SubscriptionStore`（持久化、幂等、有界历史 5000 + 损坏隔离） |
| `article` | 评论/文章保存 | `CommentUtil` / `ArticleUtil` |
| `watchlater` | 稍后再看批量下载 | 复用批量管线 |

### 2.2 DRM / Widevine 解密（全新，上游无 `BBDown.Core/DRM/`）

- 课程 CKC 协议（`CkcDecryptor`）+ 番剧 `drm_tech_type=2` Widevine（`WidevineCdm` / `WidevineCrypto` / `WvdDevice`）。
- 仓库打包 `device.wvd`（一键解密）；配套 Chrome 扩展提取密钥。
- 新选项：`--decrypt-drm`、`--key`、`--kid`、`--wvd-path`、`--mp4decrypt-path`。

### 2.3 serve / API 服务（上游 210 行 → 本 fork 约 1,460 行）

- 任务生命周期：`/add-task` 返回 JobId、`/cancel` 全程可取消、`/remove-finished`、`/get-tasks` 族查询（并发信号量上限 8，超限 429）。
- 安全边界：`X-Serve-Token` 常量时间比较 + 失败限速有界裁剪；写端点 Origin/CSRF 校验；webhook 回调三重 SSRF 防护（启动 allowlist + 每次复查 + 禁重定向）；`SanitizeUntrustedOptions` 清零客户端危险字段（路径/UA/ForceHttp/DRM 密钥等 17+ 字段）+ 数值 Clamp 防慢速 DoS。
- 持久化：任务记录 tmp+flush+rename 原子落盘、已完成任务上限 1000 + 30 天保留。
- 新选项：`--max-concurrent`、`--serve-token`（环境变量 `BBDOWN_SERVE_TOKEN` 优先）、`--trusted-proxy`、`--notify-webhook`。
- 用户文档：根目录 `API.md`（取代上游 `json-api-doc.md`，后者已删除）。

### 2.4 新增 CLI 选项（15 个，上游 57 个全部保留、零删除）

| 主题 | 选项 |
|------|------|
| DRM | `--decrypt-drm`、`--key`、`--kid`、`--wvd-path`、`--mp4decrypt-path` |
| 韧性 | `--retry-count`、`--retry-delay`、`--muxer-timeout`、`--thread-segment-size` |
| 行为 | `--allow-preview`（充电专属预览检测，不静默保存）、`--insecure`（跳过 TLS 校验，serve 下强制清零） |
| 弹幕/评论 | `--comments`、`--danmaku-filter`、`--danmaku-filter-user` |
| serve | `--notify-webhook` |

### 2.5 下载与解析韧性（上游基本裸奔的行为面）

- `.tmp` 分片断点续传（含 Content-Range 身份校验防串台）、多线程分片尺寸错位修复、跳过路径假成功复核。
- `CancellationToken` 贯通网络/下载/登录轮询/更新检查全链路（上游多处不可取消）。
- 风控页识别（200+HTML）参与页面级重试；Cookie 过期显式检测报告；签名 URL 日志脱敏。

## 三、安全差异（相对上游的系统性加固）

| 层 | 差异 |
|----|------|
| HTTP | 连接池按策略隔离（verified/insecure × 自动/禁重定向 × media 下载），`--insecure` 不可降级 verified 池；重定向逐跳可信主机校验（登录轮询/gRPC/Widevine）；Cookie 外发主机白名单；gzip 解压 48MB 上限；gRPC 帧首字节校验 |
| 时钟/WBI | `ServerClock` 服务端时钟校准（不可信源隔离防 WBI 扰动）；`BuvidProvider` 设备指纹；WBI 签名流内传播 |
| 凭据/日志 | `SensitiveDataMasker`（签名 URL/Cookie/WBI key 脱敏）、`SanitizeServerText` 控制字符剥离、异常消息路径脱敏、serve 日志单行化 |
| 本地执行 | 外部工具搜索仅 `APP_DIR`/`PATH`、绝不搜 CWD（防可执行劫持）；`RiskControlResponseException` / `UpowerGuard` 防御性解析 |
| serve 面 | 见 2.3（认证/CSRF/限速/SSRF/Clamp 全家桶） |

## 四、工程与流程差异（上游均无）

- **测试**：`BBDown.Tests`（xUnit，659 例全绿，上游无任何测试项目）——`failSkips: true` 防静默跳过、`FakeBilibiliApiServer` + 14 场景夹具护栏锁 `ExtractTracksAsync` 主干、Local/NetworkIntegration 分层（ffmpeg 在位检测）。
- **CI/CD**：`pr.yml`（单测 gate + `dotnet format --verify-no-changes` 硬门禁 + 本地集成）、`codeql.yml`、`release.yml` / `build_latest.yml`（多平台 AOT 产物 + Docker 冒烟）、dependabot、issue/PR 模板、CODEOWNERS。
- **约束**：`global.json`（SDK 10.0.300 pin）、`.editorconfig` / `.gitattributes`（UTF-8、LF、4 空格、末尾换行）、`AGENTS.md`（代理协作规范）、`CONTRIBUTING.md` / `SECURITY.md` / `CODE_OF_CONDUCT.md`。
- **文档**：README 全面重写、`API.md`、`CHANGELOG.md`（Keep a Changelog）、`docs/wiki/` 15 页 + `scripts/sync-wiki.ps1`、`docs/REVIEW_PLAN.md` / `REVIEW_FINDINGS.md` / `MAINTENANCE_PLAN.md`（12 轮审查体系）。
- **结构重组**：`BBDown/` 平铺 → `Commands/` `Infrastructure/` `Application/` `Configuration/` `Utilities/` 分层；`BBDown.Core` 新增 `DRM/`、`Util/` 扩充（`PathUtil`、`JsonElementExtensions`、`SensitiveDataMasker` 等）；删除 `launchSettings.json`。

## 五、向后兼容承诺

- 上游 57 个 CLI 选项**全部保留**（含 `--bandwith-ascending` 历史拼写兼容），零删除。
- `BBDown.config` 键格式不破坏。
- `login` / `logintv` / `serve` 语义保持（serve 行为强化，默认无 token 回环监听仍可用）。

## 六、同步上游注意事项

1. 上游推进后先 `git fetch upstream`，`git merge upstream/master`（本 fork 未 rebase 改写历史，merge 保留双方脉络）。
2. 冲突热点（本 fork 深度重构文件）：`Parser.cs`、`HTTPUtil.cs`、`BBDownApiServer.cs`、`MyOption.cs`、`SubUtil.cs`、各 Fetcher。
3. 合并后必跑基线三件套：`dotnet build BBDown.sln -c Release` → PR gate 过滤器单测 → `dotnet format BBDown.sln --verify-no-changes`（见 AGENTS.md）。
4. 上游侧差异对齐检查：上游若新增 CLI 选项/子命令，对照本文 2.1/2.4 评估是否采纳并更新；上游若改 `serve`，注意本 fork 安全边界（SanitizeUntrustedOptions 等）不可被合并回退。
5. 上游的 `json-api-doc.md` 已在本 fork 删除（被 `API.md` 取代）；上游若恢复或修改该文件，冲突时以 `API.md` 为准。

## 七、维护本文档

- **更新时机**：合入上游提交后、每个 fork 版本发布后。
- **更新内容**：头部快照基准（commit、领先/落后计数）；受影响章节（新子命令/新选项/新能力进二节；安全批次进三节；工程变化进四节）。
- **生成方式**：`git fetch upstream && git merge-base master upstream/master` 取基准；`git rev-list --count` 取领先/落后；`git diff --stat upstream/master..master` 取规模；CLI 选项对比可从上游 `CommandLineInvoker.cs` 与本 fork `BBDown/Configuration/MyOption.cs` 提取 `--xxx` 记号比对。
