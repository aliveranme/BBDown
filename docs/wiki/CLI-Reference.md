# 全命令行参数详解 (CLI Reference)

> 本文档提供 BBDown 主下载命令的所有参数选项、语法规范、分类详述以及进程退出码的完整参考字典。

---

## 1. 命令行基本语法

```bash
BBDown [选项] <URL或标识符>
```

### 1.1 支持的输入格式

| 目标类型 | 示例输入格式 |
| :--- | :--- |
| **普通投稿视频** | `https://www.bilibili.com/video/BV1qt4y1X7TW` 或 `BV1qt4y1X7TW` 或 `av170001` |
| **番剧 / 国创 / 电影 / 纪录片** | `https://www.bilibili.com/bangumi/play/ss33073` 或 `ss33073` 或 `ep12345` |
| **付费课程 / 课堂 (Cheese)** | `https://www.bilibili.com/cheese/play/ep12345` |
| **UP 主个人空间全部投稿** | `https://space.bilibili.com/163637592` |
| **播单 / 媒体列表** | `https://www.bilibili.com/medialist/play/ml123456` |
| **公开收藏夹** | `https://space.bilibili.com/163637592/favlist?fid=123456` |
| **合集与系列** | `https://space.bilibili.com/163637592/channel/seriesdetail?sid=12345` |

---

## 2. 完整参数速查总表

| 短选项 | 长选项 | 类型/默认值 | 功能说明 |
| :--- | :--- | :--- | :--- |
| `-t` | `--use-tv-api` | `bool (false)` | 使用 TV 端解析接口（通常无台标水印） |
| `-a` | `--use-app-api` | `bool (false)` | 使用移动端 APP 解析接口 |
| | `--use-intl-api` | `bool (false)` | 使用国际版（东南亚/泰国等）解析接口 |
| | `--host` | `string ("api.bilibili.com")` | 指定 BiliPlus 镜像站主机（UGC web 接口） |
| | `--ep-host` | `string ("api.bilibili.com")` | 指定 BiliPlus 镜像站 EP 主机（番剧 web 接口） |
| | `--tv-host` | `string ("api.snm0516.aisee.tv")` | 自定义 TV 端接口请求 Host |
| | `--area` | `string ("")` | 使用 BiliPlus 时必选，指定区域（`hk` / `tw` / `th`） |
| `-I` | `--only-show-info` | `bool (false)` | 仅解析并展示媒体流与分 P 信息，不下载 |
| `-i` | `--interactive` | `bool (false)` | 交互式命令行菜单挑选清晰度与音视频流 |
| | `--show-all` | `bool (false)` | 展示所有分 P 标题与元数据 |
| | `--hide-streams` | `bool (false)` | 控制台隐藏可用音视频流详情列表 |
| `-p` | `--select-page` | `string ("")` | 分 P 选择表达式（如 `1,3,5-10,ALL,LAST`） |
| `-q` | `--dfn-priority` | `string?` | 视频画质优先级列表（逗号分隔） |
| `-e` | `--encoding-priority` | `string?` | 视频与音频编码优先级列表（逗号分隔） |
| | `--video-ascending` | `bool (false)` | 视频流升序排列（最小文件体积优先） |
| | `--audio-ascending` | `bool (false)` | 音频流升序排列（最小文件体积优先） |
| | `--video-only` | `bool (false)` | 仅下载视频流（不下载音频且不混流） |
| | `--audio-only` | `bool (false)` | 仅下载音频流（不下载视频且不混流） |
| | `--danmaku-only` | `bool (false)` | 仅下载弹幕文件 |
| | `--cover-only` | `bool (false)` | 仅下载封面图片 |
| | `--sub-only` | `bool (false)` | 仅下载外挂字幕文件 |
| `-d` | `--download-danmaku` | `bool (false)` | 开启弹幕下载（默认保存为 XML） |
| | `--download-danmaku-formats` | `string?` | 弹幕格式列表（如 `xml,protobuf`） |
| | `--danmaku-filter` | `string?` | 弹幕关键词黑名单过滤（逗号分隔） |
| | `--danmaku-filter-user` | `string?` | 弹幕发送者 midHash 黑名单过滤（逗号分隔） |
| | `--comments` | `bool (false)` | 同时下载视频评论区，保存为 JSON |
| | `--allow-preview` | `bool (false)` | 允许下载充电专属视频的试看片段 |
| | `--decrypt-drm` | `bool (false)` | 启用原生 C# CDM 尝试解密 DRM 保护视频 |
| | `--key` | `string?` | 手动指定 DRM 解密 Key（16进制字符串） |
| | `--kid` | `string?` | 手动指定 DRM 密钥 ID（16进制字符串） |
| | `--wvd-path` | `string ("")` | 手动指定 `device.wvd` 文件路径 |
| | `--mp4decrypt-path` | `string ("")` | 手动指定 `mp4decrypt` 可执行文件路径 |
| | `--skip-mux` | `bool (false)` | 跳过混流步骤，保留单独的音视频源文件 |
| | `--simply-mux` | `bool (false)` | 精简混流（混流时不注入视频描述、UP主等元数据） |
| | `--skip-subtitle` | `bool (false)` | 跳过字幕下载 |
| | `--skip-cover` | `bool (false)` | 跳过封面下载 |
| | `--skip-ai` | `bool (true)` | 跳过 AI 生成字幕下载（默认跳过） |
| `-c` | `--cookie` | `string ("")` | 设置网页端 Cookie（含 SESSDATA 等） |
| | `--access-token` | `string ("")` | 设置 TV / APP 端 Access Token |
| `-F` | `--file-pattern` | `string ("")` | 单 P 自定义输出文件名模板 |
| `-M` | `--multi-file-pattern` | `string ("")` | 多 P 自定义输出文件名模板 |
| | `--work-dir` | `string ("")` | 设置下载产物输出的工作目录 |
| | `--config-file` | `string?` | 指定本地配置文件路径（默认读取 `BBDown.config`） |
| | `--multi-thread` | `bool (true)` | 开启多线程并发分片下载（传 `false` 可关闭） |
| | `--thread-segment-size` | `int (20)` | 多线程分片大小（单位：MB） |
| | `--retry-count` | `int (3)` | 网络请求失败最大重试次数 |
| | `--retry-delay` | `int (3000)` | 重试基础退避间隔（毫秒） |
| | `--delay-per-page` | `int (0)` | 多分 P 之间的请求间隔时间（秒） |
| | `--muxer-timeout` | `int (30)` | 混流工具最大执行超时时长（分钟） |
| | `--ffmpeg-path` | `string ("")` | 手动指定 `ffmpeg` 可执行文件路径 |
| | `--mp4box-path` | `string ("")` | 手动指定 `mp4box` 可执行文件路径 |
| | `--use-mp4box` | `bool (false)` | 使用 MP4Box 代替 FFmpeg 进行混流 |
| | `--use-aria2c` | `bool (false)` | 调用外部 aria2c 引擎进行下载 |
| | `--aria2c-path` | `string ("")` | 手动指定 `aria2c` 可执行文件路径 |
| | `--aria2c-args` | `string ("")` | 传给 aria2c 的额外命令行参数 |
| | `--force-http` | `bool (false)` | 媒体流强制使用 HTTP 协议替代 HTTPS |
| | `--insecure` | `bool (false)` | 跳过 SSL/TLS 证书有效性校验（抓包调试用） |
| | `--upos-host` | `string ("")` | 手动指定 CDN / UPOS 流媒体主机域名 |
| | `--force-replace-host` | `bool (true)` | 强制将边缘 PCDN 域名替换为骨干 CDN 域名 |
| | `--allow-pcdn` | `bool (false)` | 允许使用边缘 PCDN 节点（不自动替换） |
| | `--save-archives-to-file`| `bool (false)`| 在工作目录维护 `archives.txt` 记录已下载 aid |
| | `--notify-webhook` | `string?` | 下载完成后发送 HTTP POST 结果通知 |
| | `--language` | `string ("")` | 设置混流音频流的语言代码（如 `chi`、`jpn`、`eng`） |
| `-u` | `--user-agent` | `string ("")` | 指定自定义 User-Agent 请求头 |
| | `--debug` | `bool (false)` | 输出详细调试日志（含请求头与异常堆栈） |

---

## 3. 分类参数详解

### 3.1 画质与编码决策
- **`-q, --dfn-priority`**：画质字符串匹配从左至右进行。例如 `-q "8K 超高清, 1080P 高码率, HDR 真彩, 杜比视界, 1080P 高清"`。
- **`-e, --encoding-priority`**：编码优先级匹配。常用格式：`-e "hevc,av1,avc,flac,eac3,m4a"`。
- **`--video-ascending` / `--audio-ascending`**：强制使用码率升序决策（最小体积优先），适合移动热点或低配存储设备。

### 3.2 分 P 高级语法 (`-p, --select-page`)
- 单个序号：`-p 5`
- 离散多个：`-p 1,3,7`
- 范围区间：`-p 1-10`
- 混合组合：`-p 1-3,7,10-12`
- 全量剧集：`-p ALL`
- 最新一集：`-p LAST` 或 `-p LATEST`

---

## 4. 进程系统退出码 (Exit Codes)

BBDown 在执行退出时返回以下操作系统退出码：

| 退出码 | 状态描述 | 触发场景 |
| :---: | :--- | :--- |
| `0` | **Success (成功)** | 正常完成解析/下载/混流；`serve` / `live` 等子命令在用户取消或收到关停信号时也返回 `0` |
| `1` | **General Error (常规错误)** | URL 无效、解析异常、网络连续重试失败、接口返回 404/风控、找不到 FFmpeg / MP4Box 等外部工具 |
| `130` | **Interrupted (SIGINT 中断)** | 主下载命令（默认命令）运行中用户按 `Ctrl+C` 取消 |

---

### 🧭 快速跳转

| 上一篇 | 目录导航 | 下一篇 |
| :--- | :---: | ---: |
| ⬅️ [快速上手](Getting-Started) | 📑 [返回目录](Home) | [账号登录与鉴权](Authentication) ➡️ |
