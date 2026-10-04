# 子命令使用指南 (Subcommands)

> 本文档详细介绍 BBDown 除了基础下载功能之外的高级子命令生态，涵盖直播录制、专栏保存、稍后再看同步以及 UP 主订阅管理。

---

## 1. 直播录制 (`live`)

BBDown 支持对哔哩哔哩直播间进行无损分段实时录制与自动合成。

### 1.1 命令语法
```bash
BBDown live <room_id> [选项]
```

### 1.2 参数列表

| 短选项 | 长选项 | 类型/默认值 | 说明 |
| :--- | :--- | :--- | :--- |
| *(参数0)*| `<room_id>` | `string` | 直播间短号或长号房间 ID（如 `6` 或 `12345`） |
| `-o` | `--output` | `string?` | 最终输出文件路径（默认：`直播间标题_直播录制_时间.flv`） |
| `-w` | `--work-dir` | `string ("")` | 设置工作目录（默认当前目录；未指定 `--output` 时录制文件输出到该目录） |
| `-c` | `--cookie` | `string ("")` | 手动指定 Cookie（若省略则自动加载 `BBDown.data`） |
| | `--access-token` | `string ("")` | 手动指定 Access Token |

### 1.3 核心技术特性
- **画质权限自动提升**：未登录录制仅返回 720P 游客画质；BBDown 会自动读取本地 `BBDown.data` 凭据，解析获取原画、4K、杜比视界等账号可用最高规格。
- **断流重连与分段暂存**：直播卡顿或主播重启推流时，程序会自动进行重试，将已接收数据保存在 `.segs/session-*` 目录中。
- **FFmpeg Concat 安全合成**：录制结束时（按 `Ctrl+C` 或主播下播），程序自动调用 FFmpeg 合成分段，并核对输入与输出的 FLV 音视频媒体帧数。若坏段、漏段或中断写入导致无法确认合成完整性，会保留当前会话的 `.segs/session-*` 分段供手动恢复，不覆盖已有录制文件。

---

## 2. 专栏文章下载 (`article`)

将 B 站专栏 / 动态图文（Opus）下载并完整转换为 Markdown 格式文件。

### 2.1 命令语法
```bash
BBDown article <cv_id或链接> [选项]
```

### 2.2 参数列表

| 参数/选项 | 类型/默认值 | 说明 |
| :--- | :--- | :--- |
| `<cv_id>` | `string` | 专栏 ID（如 `cv12345`）或完整文章链接 |
| `-o, --output` | `string?` | 输出 Markdown 路径（默认：`<专栏标题>.md`） |
| `-w, --work-dir` | `string ("")` | 设置工作目录（默认当前目录；未指定 `--output` 时输出到该目录） |

---

## 3. 稍后再看批量下载 (`watchlater`)

一键批量同步并下载当前登录账号「稍后再看」列表中的所有视频。

### 3.1 命令语法
```bash
BBDown watchlater [选项]
```

### 3.2 参数列表

| 选项 | 类型/默认值 | 说明 |
| :--- | :--- | :--- |
| `--limit <N>` | `int (0)` | 最大下载视频数量（默认 0 代表下载全部） |
| `-w, --work-dir` | `string ("")` | 设置下载输出目录 |
| `-c, --cookie` | `string ("")` | 手动指定 Cookie（若省略则自动加载 `BBDown.data`） |
| `--access-token` | `string ("")` | 手动指定 Access Token |
| `-q, --dfn-priority` | `string?` | 画质优先级 |
| `-e, --encoding-priority` | `string?` | 编码优先级 |
| `-t` / `-a` / `--use-intl-api` | `bool` | 使用 TV 端 / APP 端 / 国际版解析模式 |

---

## 4. 订阅增量管理 (`sub`)

BBDown 支持对指定的 UP 主、番剧或合集进行订阅追踪，通过增量检查一键下载最新更新。

### 4.1 常用子操作

| 命令 | 功能说明 | 示例 |
| :--- | :--- | :--- |
| `BBDown sub add <target> [--name <name>]` | 添加新订阅源 | `BBDown sub add "mid:163637592" --name "何同学"` |
| `BBDown sub list` | 查看当前所有订阅列表 | `BBDown sub list` |
| `BBDown sub remove <target>` | 删除指定订阅源 | `BBDown sub remove "mid:163637592"` |
| `BBDown sub check [选项]` | 增量拉取所有订阅的新视频 | `BBDown sub check -q "1080P 高码率"` |
| `BBDown sub check --per-sub-dir` | 每个订阅下载到独立子目录 | `BBDown sub check -w "E:\\Download\\bbdown" --per-sub-dir` |
| `BBDown sub check --full-scan` | 禁用增量提前结束，重扫全部投稿列表页 | `BBDown sub check --full-scan` |

> **增量扫描**：`mid:` 订阅的检查按投稿列表（`order=pubdate` 倒序、50 条/页）**轻量列举 aid**，不再为每个投稿逐个请求分P详情（此前每检查一次都要展开全部投稿，实测一个 53 投稿的订阅固定花 ~11 秒，与是否有新内容无关）。某页的稿件全部已下载过即停止翻页，因此常规检查通常只发 1~2 个请求。代价：停止点更旧的页不再扫描，这些页里若存在历史中没有的稿件（例如上次下载失败、或手动清理过历史），默认增量不会发现，用 `--full-scan` 强制重扫。收藏夹 / 合集 / 番剧类订阅仍走原有全量解析路径。
>
> **已知限制与建议**：增量以「稿件发布时间倒序 + 整页均已下载」为停止判据，因此**只保证不遗漏新投稿，不保证重扫全部历史**——以下情形请先跑一次 `BBDown sub check --full-scan`：① 首次部署该功能；② `BBDownSubscriptions.history.json` 被重建或手动清理过；③ 你手动删除过已下载的稿件/历史、或怀疑某次下载失败后历史未记录。每天定时跑的常规检查用默认增量即可。

> **多订阅分目录**：添加了多个订阅时，默认所有新视频都会平铺在 `-w` 指定的根目录里。加 `--per-sub-dir` 后每个订阅下载到 `<work-dir>/<订阅名>/` 子目录——订阅名取 `sub add --name` 的显示名（未指定则用 target），经路径净化（非法字符替换、保留名/纯点段处理），净化后同名的订阅自动追加 `-2`/`-3` 序号。该选项默认关闭，不加时保持原有平铺行为。

> **建议显式命名**：未指定 `--name` 时目录名回退为订阅目标，净化后形如 `mid_163637592`（target 为 URL 时更长），多订阅场景下不易辨认。建议用 `BBDown sub add "mid:163637592" --name "某UP主"` 显式命名；`sub add` 在未指定 `--name` 时也会给出提示。

### 4.2 支持的订阅源标识格式
- **UP 主 UID**：`mid:163637592`
- **个人空间链接**：`https://space.bilibili.com/163637592`
- **合集 / 系列链接**：`https://space.bilibili.com/163637592/channel/seriesdetail?sid=12345`
- **番剧季度**：`ss:33073`

---

### 🧭 快速跳转

| 上一篇 | 目录导航 | 下一篇 |
| :--- | :---: | ---: |
| ⬅️ [配置文件与命名规则](Configuration-and-Templates) | 📑 [返回目录](Home) | [弹幕与评论区抓取](Danmaku-and-Comments) ➡️ |
