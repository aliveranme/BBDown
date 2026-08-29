> 本项目仅供个人学习、研究和非商业性用途。用户在使用本工具时，需自行确保遵守相关法律法规，特别是与版权相关的法律条款。开发者不对因使用本工具而产生的任何版权纠纷或法律责任承担责任。请用户在使用时谨慎，确保其行为合法合规，并仅在有合法授权的情况下使用相关内容。

<p align="center">
  <img src="assets/readme/hero.svg" alt="BBDown - 命令行式哔哩哔哩下载器" width="100%">
</p>

# BBDown

![CI](https://github.com/aliveranme/BBDown/actions/workflows/build_latest.yml/badge.svg)
![CI PR](https://github.com/aliveranme/BBDown/actions/workflows/pr.yml/badge.svg)
![CodeQL](https://github.com/aliveranme/BBDown/actions/workflows/codeql.yml/badge.svg)
![Release](https://img.shields.io/github/v/release/aliveranme/BBDown)
![Downloads](https://img.shields.io/github/downloads/aliveranme/BBDown/total)
![License](https://img.shields.io/github/license/aliveranme/BBDown)

命令行式哔哩哔哩下载器 · Bilibili Downloader

一条命令完成链接解析、多线程下载与音视频混流，支持 8K / HDR / 杜比视界 / 杜比全景声，以及原生 C# 实现的 Widevine DRM 解密。

<a name="演示"></a>

![实际效果](assets/readme/section-demo.svg)

![BBDown 命令行下载演示动图](https://user-images.githubusercontent.com/20772925/88686407-a2001480-d129-11ea-8aac-97a0c71af115.gif)

下载完毕后在当前目录即可看到混流完成的 MP4 文件：

![下载结果：目录中生成的 MP4 文件截图](https://user-images.githubusercontent.com/20772925/88478901-5e1cdc00-cf7e-11ea-97c1-154b9226564e.png)

> 以上两张图来自上游 [nilaoda/BBDown](https://github.com/nilaoda/BBDown) 的早期版本，下载流程一致，但控制台样式与本分支当前基于 Spectre.Console 的输出有所不同。

![核心功能](assets/readme/section-features.svg)

- 普通视频、番剧、课程、合集 / 列表 / 收藏夹解析
- UP 主全部投稿批量下载（需登录）
- 多分 P 自动下载，支持自由选择清晰度、编码与分 P 范围
- 下载外挂字幕并转换为 `srt` / `ass` 格式
- 自动混流：合并音频 + 视频 + 字幕 + 章节信息（需 ffmpeg 或 mp4box）
- 最高支持 8K / HDR / 杜比视界 / 杜比全景声
- 原生 C# Widevine DRM 解密，无需 Python
- 二维码登录（WEB / TV / APP）
- API 服务器模式（`BBDown serve`），支持并发限制与文件日志
- 自定义文件名、配置文件（`BBDown.config`）、断点续传、Ctrl+C 中断
- 多线程下载，可选 aria2c

> **注意**：本软件混流时需要外部程序：
> - 普通视频：[ffmpeg](https://www.gyan.dev/ffmpeg/builds/)，或 [mp4box](https://gpac.wp.imt.fr/downloads/)
> - 杜比视界：ffmpeg 5.0 以上或新版 mp4box

![一条命令背后的故事](assets/readme/workflow.svg)

BBDown 把复杂的下载流程串成一条自动化流水线：粘贴链接 → 选择解析接口 → 多线程下载 → 自动混流输出。你只需要关注最终生成的 `.mp4` 文件。

![快速开始](assets/readme/section-getting-started.svg)

### 获取程序

- Release 版本：https://github.com/AliverAnme/BBDown/releases
- 自动构建的测试版本：https://github.com/AliverAnme/BBDown/actions

### 查看完整参数

```bash
BBDown --help
```

### 核心参数速查

| 短选项 | 长选项 | 说明 |
|--------|--------|------|
| `-t` | `--use-tv-api` | 使用 TV 端解析模式 |
| `-a` | `--use-app-api` | 使用 APP 端解析模式 |
| `-I` | `--only-show-info` | 仅解析而不下载 |
| `-i` | `--interactive` | 交互式选择清晰度 |
| `-d` | `--download-danmaku` | 下载弹幕 |
| `-e` | `--encoding-priority` | 编码优先级（如 `hevc,av1,avc`） |
| `-q` | `--dfn-priority` | 画质优先级 |
| `-p` | `--select-page` | 选择分 P（如 `-p 1,3,5-10`） |
| `-F` | `--file-pattern` | 自定义单 P 文件名格式 |
| `-M` | `--multi-file-pattern` | 自定义多 P 文件名格式 |
| `-c` | `--cookie` | 设置 Cookie |
| | `--muxer-timeout` | 混流超时时长（分钟，默认 30） |
| | `--retry-count` | 网络请求失败重试次数（默认 3） |
| | `--retry-delay` | 重试间隔基础毫秒数（默认 3000） |
| | `--thread-segment-size` | 多线程下载分片大小（MB，默认 20） |
| | `--config-file` | 指定配置文件路径 |

### 更多常用选项

下载内容控制：

| 长选项 | 说明 |
|--------|------|
| `--video-only` / `--audio-only` | 仅下载视频 / 音频轨 |
| `--sub-only` | 仅下载字幕 |
| `--cover-only` | 仅下载封面 |
| `--show-all` | 显示全部可用音视频流 |
| `--save-archives-to-file` | 记录已下载 aid，重复运行时自动跳过 |
| `--allow-preview` | 允许下载充电专属视频的试看片段（见下） |

外部工具与网络：

| 长选项 | 说明 |
|--------|------|
| `--multi-thread` | 多线程下载（默认开启，`--multi-thread false` 关闭） |
| `--use-aria2c` | 改用 aria2c 下载 |
| `--aria2c-path` / `--aria2c-args` | 指定 aria2c 路径 / 额外参数 |
| `--ffmpeg-path` / `--mp4box-path` | 指定混流工具路径 |
| `--use-mp4box` | 使用 mp4box 混流 |
| `--work-dir` | 指定下载工作目录 |
| `--insecure` | 跳过 SSL 证书校验 |

跳过与排障：

| 长选项 | 说明 |
|--------|------|
| `--skip-mux` | 跳过混流，保留原始音视频文件 |
| `--skip-subtitle` / `--skip-cover` / `--skip-ai` | 跳过字幕 / 封面 / AI 字幕 |
| `--debug` | 输出调试日志（排障时附上） |

> DRM 解密相关选项（`--decrypt-drm` / `--key` / `--kid` / `--wvd-path` 等）见下方 [Widevine DRM 解密](#widevine-drm-解密)。完整选项请执行 `BBDown --help`。

### 充电专属视频

UP 主的充电专属稿件，在当前账号没有充电权限时，B 站接口**不会返回错误**——它照常返回 `code=0`，并且在时长字段里声称完整长度，实际只下发几分钟的试看片段。

BBDown 会在开始下载前识别这种情况并中止，避免产出一个被报告为"下载成功"的残片：

```
[警告] 充电专属视频
当前账号没有该UP主的充电权限，接口只返回了 00:06:29 的试看片段（完整视频 02:23:48）
已跳过。如需下载试看片段，请加 --allow-preview
```

此时退出码非 0，便于脚本判断。若确实需要保留试看片段，加 `--allow-preview`，产出文件名会带 `[试看]` 前缀以便与完整视频区分。

登录一个已为该 UP 主充电的账号（`BBDown login`）即可正常下载完整视频，不需要额外参数。

### 子命令

| 命令 | 说明 |
|------|------|
| `login` | APP 扫码登录 WEB 账号 |
| `logintv` | APP 扫码登录 TV 账号 |
| `serve` | 以 API 服务器模式运行 |
| `live` | 录制 B 站直播流（断流自动重连，录制内容写入独立分段，结束后用 FFmpeg concat 合成最终文件；取消/断连时已录分段保留在 `.segs/session-*` 目录，可手动恢复）。支持 `--work-dir` 指定输出/分段目录（默认当前目录） |
| `article` | 下载 B 站专栏文章为 Markdown。支持 `--work-dir` 指定输出目录（默认当前目录） |
| `watchlater` | 批量下载稍后再看列表（需登录） |
| `sub` | 订阅管理：`sub add/list/remove/check`，检查并增量下载新内容（以稿件 aid 为粒度；首次 check 无下载历史时，订阅视频的所有分P会被视为新内容全量下载，下载成功后才记录历史跳过） |

`serve` 子选项：

| 短选项 | 长选项 | 说明 |
|--------|--------|------|
| `-l` | `--listen` | 监听地址（默认 `http://127.0.0.1:23333`，仅本机可访问） |
| | `--max-concurrent` | 最大并发下载数（默认 3） |
| | `--serve-token` | 可选认证令牌，配置后所有任务/查询端点要求 `X-Serve-Token` 请求头，否则 401。优先使用环境变量 `BBDOWN_SERVE_TOKEN` 注入（避免令牌出现在进程命令行；两者冲突时环境变量胜出并告警） |
| | `--trusted-proxy` | 信任直连反代追加的 X-Forwarded-For（认证失败限速按客户真实 IP 计键）。仅在 serve 前方确有可信反代时启用，否则客户端可伪造 XFF 绕过限速 |

> 安全提示：CLI 默认仅监听回环地址且无认证（安全默认），需要对外提供时请显式指定 `-l http://0.0.0.0:<port>` 并务必配置 `--serve-token`。多用户环境下建议用环境变量 `BBDOWN_SERVE_TOKEN` 注入令牌，避免 `ps` 等进程列表暴露。`/add-task` 请求体中可能引发命令执行、凭据外泄或路径穿越的字段会被一律忽略（如 `ffmpegPath`、`aria2cArgs`、`host` 白名单、`filePattern`、`insecure` 等），详见 [API.md](./API.md)。

### 常用命令

下载普通视频：
```bash
BBDown "https://www.bilibili.com/video/BV1qt4y1X7TW"
```

使用 TV 接口下载（粉丝量大的 UP 主片源通常无水印）：
```bash
BBDown -t "https://www.bilibili.com/video/BV1qt4y1X7TW"
```

显示所有分 P 信息：
```bash
BBDown --show-all "https://www.bilibili.com/video/BV1Y7411d7Ys"
```

选择分 P 下载：
```bash
# 单个分 P
BBDown -p 10 "https://www.bilibili.com/video/BV1Y7411d7Ys"

# 多个分 P
BBDown -p 1,2,10 "https://www.bilibili.com/video/BV1Y7411d7Ys"

# 范围分 P
BBDown -p 1-10 "https://www.bilibili.com/video/BV1Y7411d7Ys"

# 范围与单个混用
BBDown -p 1-3,7,9-11 "https://www.bilibili.com/video/BV1Y7411d7Ys"

# 番剧全集
BBDown -p ALL "https://www.bilibili.com/bangumi/play/ss33073"
```

下载 UP 主的全部投稿（需登录）：
```bash
# 先登录，该接口不接受未登录请求
BBDown login

# 解析整个空间，每个投稿的每个分 P 会依次编号
BBDown "https://space.bilibili.com/21241234"

# 配合 -p 只取其中一部分
BBDown -p 1-20 "https://space.bilibili.com/21241234"
```

> 投稿列表接口不返回 cid，程序需逐个请求视频详情来展开分 P，
> 投稿较多时解析阶段会花费一些时间。

### 登录与鉴权

<details>
<summary>WEB / TV 鉴权</summary>

---

扫码登录网页账号：
```bash
BBDown login
```

扫码登录云视听小电视账号：
```bash
BBDown logintv
```

*PS: 如果登录报错 `The type initializer for 'Gdip' threw an exception`，请参考 [#37](https://github.com/aliveranme/BBDown/issues/37) 解决。*

手动加载网页 Cookie：
```bash
BBDown -c "SESSDATA=******" "https://www.bilibili.com/video/BV1qt4y1X7TW"
```

手动加载云视听小电视 Token：
```bash
BBDown -t --access-token "******" "https://www.bilibili.com/video/BV1qt4y1X7TW"
```
</details>

<details>
<summary>APP 鉴权</summary>

---

> 根据 [#123](https://github.com/aliveranme/BBDown/issues/123#issuecomment-877583825)，可以填写 TV 登录产生的 `access_token` 来给 APP 接口使用。可复制 `BBDownTV.data` 到 `BBDownApp.data` 使程序自动读取。

目前程序无法自动获取鉴权信息，推荐通过抓包来获取。在请求 Header 中寻找键为 `authorization` 的项，其值形如 `identify_v1 5227************1`，其中的 `5227************1` 就是 token（access_key）。

获取后手动通过 `--access-token` 选项加载，或写入 `BBDownApp.data` 使程序自动读取：

```bash
BBDown -a --access-token "******" "https://www.bilibili.com/video/BV1qt4y1X7TW"
```
</details>

![配置与 API](assets/readme/section-config-api.svg)

### 配置文件

在 1.4.9 或更高版本中，BBDown 支持读取本地配置文件。若未指定 `--config-file`，默认读取程序同目录下的 `BBDown.config`。

<details>
<summary>典型配置文件示例</summary>

---

```config
# 本文件是 BBDown 程序的配置文件
# 以 # 开头的行会被程序忽略
# 对于一个选项，其参数应当在下一行出现

# 设置输出文件名格式
--file-pattern
<videoTitle>[<dfn>]

--multi-file-pattern
<videoTitle>/[P<pageNumberWithZero>]<pageTitle>[<dfn>]

# 设置下载多个分 P 时的间隔
--delay-per-page
2

# 开启弹幕下载
--download-danmaku
```
</details>

> **弹幕说明**：`--download-danmaku-formats` 目前仅支持 `xml` 与 `ass` 两种格式（默认可写 `xml,ass`；B 站原生 protobuf 弹幕尚未实现）。`--danmaku-filter` / `--danmaku-filter-user` 仅在生成 ASS 弹幕时过滤关键词 / 发送者，XML 弹幕始终保留原始全量（XML 作存档、ASS 作过滤展示）。

### 自定义输出文件名格式

| 代码 | 含义 |
|------|------|
| `<videoTitle>` | 视频主标题 |
| `<pageNumber>` | 视频分 P 序号 |
| `<pageNumberWithZero>` | 视频分 P 序号（前缀补零） |
| `<pageTitle>` | 视频分 P 标题 |
| `<bvid>` | 视频 BV 号 |
| `<aid>` | 视频 aid |
| `<cid>` | 视频 cid |
| `<dfn>` | 视频清晰度 |
| `<res>` | 视频分辨率 |
| `<fps>` | 视频帧率 |
| `<videoCodecs>` | 视频编码 |
| `<videoBandwidth>` | 视频码率 |
| `<audioCodecs>` | 音频编码 |
| `<audioBandwidth>` | 音频码率 |
| `<ownerName>` | 上传者名称（下载番剧时为空） |
| `<ownerMid>` | 上传者 mid（下载番剧时为空） |
| `<publishDate>` | 发布时间（yyyy-MM-dd_HH-mm-ss） |
| `<apiType>` | API 类型（TV / APP / INTL / WEB） |

> **命名模板选择**：单分P视频用单P模板（`<videoTitle>` 或 `-F`），多分P视频用多P模板（`<videoTitle>/[P<pageNumberWithZero>]<pageTitle>` 或 `-M`）。多P判定基于**实际下载的分P数**——`-p 3` 只单选 1 集时即使视频有多个分P也走单P模板（`-F` 生效，产物不带 `[P##]` 前缀）；番剧未完结时固定按多P处理。

### API 服务器

启动服务器（默认监听 `http://127.0.0.1:23333`，仅本机可访问）：

```bash
BBDown serve
```

自定义监听地址和端口（需要对外网开放时显式指定 `0.0.0.0`）：

```bash
BBDown serve -l http://0.0.0.0:12450 --serve-token <token>
```

> 安全提示：CLI 默认仅监听回环地址且无认证，仅建议在可信网络内使用。对外网开放时必须显式 `-l http://0.0.0.0:<port>` 并配合 `--serve-token`（所有 API 请求需携带 `X-Serve-Token` 请求头，否则 401）。**注意：API 服务器仅支持 HTTP，`X-Serve-Token` 在局域/公网链路上是明文传输的，token 只提供访问控制、不提供传输加密**——跨不可信网络使用时请务必前置 HTTPS 反向代理（如 nginx/caddy），并避免把 token 写在进程列表可见的明文命令行（优先用环境变量 `BBDOWN_SERVE_TOKEN` 注入）。若 serve 前方确有可信反向代理，可加 `--trusted-proxy`，使认证失败限速按 X-Forwarded-For 中的客户真实 IP 计键（无反代时切勿启用，客户端可伪造 XFF 绕过限速）。`/add-task` 提交的 `host/epHost/tvHost` 仅接受 B 站官方域名、执行路径/代理/工作目录字段一律忽略、请求体上限 64KB。API 服务器不支持 HTTPS。

> 配置注入说明：`serve` 为子命令，其选项（`-l` / `--max-concurrent` / `--serve-token`）**不支持**从配置文件 `BBDown.config` 读取，只能通过命令行或环境变量传入——其中 `--serve-token` 支持经环境变量 `BBDOWN_SERVE_TOKEN` 注入（优先于 CLI 参数），其余选项仅支持命令行。配置合并（`BBDownConfigParser.MergeWithConfig`）会跳过所有子命令调用，因此 `BBDown.config` 中的选项对 `serve` 不生效。

#### Docker 部署

镜像以 **Native AOT** 构建，运行阶段直接执行原生可执行文件（不依赖 .NET 运行时），默认以 `serve` 启动并监听 `http://0.0.0.0:23333`（容器内必须监听 `0.0.0.0`，否则 `-p` 端口映射从宿主机访问不到），宿主机通过 `-p 23333:23333` 即可访问。

```bash
docker build -t bbdown .
```

> **安全边界**：serve 在非回环监听（`0.0.0.0`）且未配置 `--serve-token` 时会**拒绝启动**（防止局域网任意客户端提交下载任务）。因此直接 `docker run -d -p 23333:23333 <image>` 会以非零码退出——这是预期行为，不是故障。对外暴露必须显式传入 token：

```bash
docker run -d --name bbdown -p 23333:23333 <image> serve -l http://0.0.0.0:23333 --serve-token <token>
```

容器对外暴露时**务必**配置 `--serve-token`（否则任何能连到该端口的人都可以提交下载任务）。由于镜像 `ENTRYPOINT` 是 AOT 原生可执行文件，追加参数会作为 `serve` 的命令行参数整体拼接。

API 详细说明请参考 [API.md](./API.md)。

### Widevine DRM 解密

BBDown 目前以**原生 C#** 实现了 Widevine CDM，可自动获取解密密钥并解密 B 站 DRM 保护内容，**无需 Python / pywidevine**。

**准备**
1. 获取一个 `device.wvd` 文件（Widevine 设备文件，需自行提取或从可信来源获取）
2. 将 `device.wvd` 放在以下任一位置：
   - 程序所在目录
   - 环境变量 `PATH` 中的目录
   - macOS: `/opt/homebrew/bin` / Linux: `/usr/local/bin` / Windows: 程序目录

**使用**
```bash
# 下载 DRM 保护的视频（自动解密）
BBDown --decrypt-drm "https://www.bilibili.com/cheese/play/ep1243104"
```

**原理说明**
- 使用 `drm_tech_type=2` 请求标准 Widevine 流
- 从 B 站许可证服务器获取密钥（兼容性取决于 `device.wvd` 的 `security_level`）
- 解密后与普通视频一样进行混流输出

## 开发构建

```bash
# 克隆仓库
git clone https://github.com/AliverAnme/BBDown.git
cd BBDown

# 还原依赖并编译
dotnet restore
dotnet build

# 运行
BBDown/bin/Debug/net10.0/BBDown --help
```

详细贡献指南请参考 [CONTRIBUTING.md](./CONTRIBUTING.md)。

## 更新日志

查看 [CHANGELOG.md](./CHANGELOG.md) 了解版本历史。

## 许可证

本项目基于 [MIT 许可证](./LICENSE) 开源。

## 安全

安全漏洞报告请参考 [SECURITY.md](./SECURITY.md)。请勿通过公开 Issue 报告安全问题。

## 社区

- [贡献指南](./CONTRIBUTING.md)
- [行为准则](./CODE_OF_CONDUCT.md)
- [Discussions](https://github.com/AliverAnme/BBDown/discussions)

## 致谢

本项目继承自 [nilaoda/BBDown](https://github.com/nilaoda/BBDown)，在此感谢原作者的开创性工作。

### 本分支额外致谢
- https://github.com/spectreconsole/spectre.console

### 原作者致谢
- https://github.com/codebude/QRCoder
- https://github.com/icsharpcode/SharpZipLib
- https://github.com/protocolbuffers/protobuf
- https://github.com/grpc/grpc
- https://github.com/SocialSisterYi/bilibili-API-collect
- https://github.com/SeeFlowerX/bilibili-grpc-api
- https://github.com/FFmpeg/FFmpeg
- https://github.com/gpac/gpac
- https://github.com/aria2/aria2
