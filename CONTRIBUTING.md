# 贡献指南

感谢你对 BBDown 项目的兴趣！在提交贡献之前，请阅读本指南。

## 开发环境

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)：`global.json` 固定 10.0.403 并允许最新补丁；当前 CI / AOT 锁文件验证版本为 10.0.403。
- 支持的操作系统：Windows / Linux / macOS

## 构建

```bash
# 还原依赖
dotnet restore BBDown.sln --locked-mode

# 编译（Debug）
dotnet build

# 编译（Release）
dotnet build -c Release

# 发布单文件（示例：win-x64）
dotnet publish BBDown -r win-x64 -c Release --no-restore
```

还原时不要传 `-r`：锁文件包含完整六 RID 图，单 RID 还原会触发 NU1004。发布时才选择 RID，并使用 `--no-restore`。有意修改依赖后，执行 `dotnet restore BBDown.sln --force-evaluate -p:RestoreLockedMode=false`，审查三个锁文件的差异，再恢复 locked mode 验证；SDK 补丁升级也需要检查隐式 ILCompiler / ILLink 依赖。

单元门禁使用 `dotnet test BBDown.sln -c Release --no-build --filter "Category!=Integration&Category!=NetworkIntegration&Category!=LocalIntegration"`。`Category=LocalIntegration` 需要 ffmpeg、aria2c、Bento4 的 mp4encrypt/mp4decrypt；CI 设置 `BBDOWN_REQUIRE_LOCAL_TOOLS=1` 禁止缺工具时空跑。Linux CI 使用 `scripts/install-local-test-tools.sh` 安装并核验固定归档 SHA256 的 Bento4。

AOT 发布保留逐条警告。使用英文输出保存 publish 日志后运行 `pwsh scripts/check-aot-warnings.ps1 -LogPath <log>`；只允许已审计的具体诊断，新增警告必须解释与验证后才能更新基线。保留的两项局部抑制分别对应 CLI 入口和类型注册器，均依赖显式命令/设置类型根。

## 代码风格

- 缩进：4 空格
- 编码：UTF-8
- 换行符：跨平台，由 Git 自动处理
- 遵循 `.editorconfig` 中的配置

## 分支策略（强制）

> ⚠️ **master 分支受保护，禁止直接推送。** 所有变更必须通过 Pull Request 合并。

### 分支命名规范

| 前缀 | 用途 | 示例 |
|------|------|------|
| `feature/` | 新功能开发 | `feature/drm-auto-fetch` |
| `fix/` | Bug 修复 | `fix/muxer-directory-creation` |
| `refactor/` | 代码重构 | `refactor/split-program-methods` |
| `docs/` | 仅文档更新 | `docs/api-server-guide` |
| `deps/` | 依赖升级 | `deps/protobuf-3-35` |

### 开发流程

```bash
# 1. 从最新 master 切分支
git checkout master
git pull origin master
git checkout -b feature/my-feature

# 2. 开发、提交（遵循 Commit 规范，见下文）
git add .
git commit -m "feat: add auto cookie refresh"

# 3. 推送到远端
git push origin feature/my-feature

# 4. 在 GitHub 提交 Pull Request，等待审查
# 5. CI 通过 + 至少 1 人 approve 后，由 maintainer 合并到 master
```

### Commit 规范

遵循 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/v1.0.0/)：

```
<type>(<scope>): <description>

[optional body]

[optional footer]
```

| type | 含义 |
|------|------|
| `feat` | 新功能 |
| `fix` | Bug 修复 |
| `refactor` | 代码重构 |
| `perf` | 性能优化 |
| `docs` | 文档更新 |
| `deps` | 依赖升级 |
| `test` | 测试相关 |
| `chore` | 构建/工具链改动 |

示例：
```
feat(drm): add auto key fetch from WVD device

Previously users had to manually provide --key and --kid.
Now the tool attempts to extract keys automatically when
a device.wvd file is present.

Closes #123
```

## 提交 Issue

- 提交前请先搜索，避免重复
- Bug 报告请使用 Bug Report 模板，并提供 `--debug` 日志
- 功能请求请使用 Feature Request 模板

## 提交 Pull Request

1. Fork 本仓库并创建你的分支：`git checkout -b feature/fooBar`
2. 修改代码，确保 `dotnet build` 通过
3. 如有必要，更新相关文档（README、CHANGELOG 等）
4. 提交并推送到你的 Fork：`git push origin feature/fooBar`
5. 在 GitHub 提交 Pull Request，并填写 PR 模板

## PR 审查原则

- **原子性**：一个 PR 只做一件事
- **向后兼容**：不破坏现有 CLI 参数和配置文件格式
- **文档同步**：代码变更需同步更新文档
- **构建通过**：`dotnet build` 必须在 CI 中通过（0 Error）
- **Review 要求**：非文档类 PR 需要至少 1 名 reviewer 批准
