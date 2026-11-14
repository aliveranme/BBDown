# 开发者指南与编译构建 (Developer Guide)

> 本文档为希望参与 BBDown 代码开发、修改源码或自行从源码构建独立 Native AOT 单文件二进制与轻量 Docker 镜像的开发者提供完整指引。

---

## 1. 开发环境准备

- **.NET SDK**：[.NET 10.0 SDK](https://dotnet.microsoft.com/download) 或更高版本
- **IDE 与工具**：Visual Studio 2022+ / JetBrains Rider / VS Code (配合 C# Dev Kit)
- **支持的操作系统**：Windows 10/11、macOS (Intel/Apple Silicon) 或 Linux (x64/arm64)
- **外部依赖**：[FFmpeg](https://ffmpeg.org/)、aria2c 与 Bento4 的 mp4encrypt/mp4decrypt（本地工具集成测试需要）。当前 CI SDK 为 10.0.403，与 `global.json` 的 `latestPatch` 约束一致。

---

## 2. 源码仓库结构

```
BBDown/
├── BBDown/                 # 主控制台应用程序 (CLI / Spectre.Console / API Server)
├── BBDown.Core/            # 核心业务类库 (解析、下载、混流、DRM、弹幕)
├── BBDown.Tests/           # xUnit 单元测试与集成测试工程
├── docs/                   # 项目文档与 Wiki 知识库
├── scripts/                # 自动化构建与 Wiki 同步脚本
├── Dockerfile              # Native AOT 多阶段 Docker 构建文件
└── BBDown.sln              # 解决方案入口
```

---

## 3. 本地编译与测试

### 3.1 还原依赖与常规构建
```bash
# 还原 NuGet 依赖
dotnet restore BBDown.sln --locked-mode

# Debug 编译构建
dotnet build

# Release 构建后运行单元门禁
dotnet build BBDown.sln -c Release --no-restore
dotnet test BBDown.sln -c Release --no-build --filter "Category!=Integration&Category!=NetworkIntegration&Category!=LocalIntegration"

# 工具已安装时运行离线、本地集成
dotnet test BBDown.sln -c Release --no-build --filter "Category=LocalIntegration"
```

### 3.2 本地运行与调试
```bash
# 查看帮助
dotnet run --project BBDown -- --help

# 调试运行视频解析
dotnet run --project BBDown -- -I "https://www.bilibili.com/video/BV1qt4y1X7TW"
```

---

## 4. Native AOT 独立发布构建

BBDown 深度适配了 .NET 的 **Native AOT (Ahead-Of-Time 提前编译)** 技术：
- **零运行时依赖**：编译直接生成目标平台原生机器码（ELF / PE / Mach-O），宿主机器**无需预装任何 .NET Runtime**。
- **极致冷启动速度**：省去 JIT 即时编译，实现毫秒级快速启动。

### 4.1 各平台发布命令

```bash
# 一次性还原完整六 RID 图；restore 不传 -r
dotnet restore BBDown/BBDown.csproj --locked-mode

# 1. Windows (x64)
dotnet publish BBDown -c Release -r win-x64 -o ./dist/win-x64 --no-restore

# 2. Linux (x64)
dotnet publish BBDown -c Release -r linux-x64 -o ./dist/linux-x64 --no-restore

# 3. Linux (ARM64 - 如树莓派 / 苹果 M 系列 Linux 容器)
dotnet publish BBDown -c Release -r linux-arm64 -o ./dist/linux-arm64 --no-restore

# 4. macOS (Apple Silicon M1/M2/M3/M4)
dotnet publish BBDown -c Release -r osx-arm64 -o ./dist/osx-arm64 --no-restore

# 5. macOS (Intel x64)
dotnet publish BBDown -c Release -r osx-x64 -o ./dist/osx-x64 --no-restore
```

这些命令分别在对应平台及其 Native AOT 工具链上运行。locked restore 失败时，应检查 SDK 补丁与锁文件中的隐式 AOT 工具版本；有意升级依赖使用 `--force-evaluate -p:RestoreLockedMode=false` 重新生成并审查锁文件。CI 的 Windows/Linux/macOS 还原矩阵还会验证六 RID 资产齐全，且锁文件未被改写。

AOT 警告全部展开。PR 发布保存英文日志，由 `scripts/check-aot-warnings.ps1` 与 `scripts/aot-warnings-baseline.txt` 比对；新诊断触发失败，具体证据随 CI artifact 保留。当前基线来自 Spectre.Console.Cli 0.55.0 的反射绑定路径，不能把基线通过当成该库已完全消除 AOT 风险。

> [!WARNING]
> **Native AOT 开发约束**：
> 由于 Native AOT 会在编译期执行静态代码裁剪（Trim）：
> 1. 请避免使用无法推断类型的动态反射（Dynamic Reflection）。
> 2. JSON 序列化请务必使用 `System.Text.Json` 源生成器（Source Generator）上下文以确保 AOT 强类型支持。

---

## 5. Docker 镜像构建

```bash
# 构建本地镜像
docker build -t bbdown:latest .

# 验证镜像运行
docker run --rm bbdown:latest --help
```

---

## 6. 贡献规范

欢迎提交 Issue 与 Pull Request！提交前请确保：
1. 本地执行 `dotnet test` 确保所有测试用例均通过。
2. 代码格式符合根目录 `.editorconfig` 规范。
3. 任何新增的 CLI 选项或功能改动请同步更新 [Wiki 文档](Home)。

---

### 🧭 快速跳转

| 上一篇 | 目录导航 | 下一篇 |
| :--- | :---: | ---: |
| ⬅️ [内部架构与设计原理](Architecture-and-Design) | 📑 [返回目录](Home) | [常见问题与故障排查](FAQ-and-Troubleshooting) ➡️ |
