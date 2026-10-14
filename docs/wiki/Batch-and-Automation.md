# 批量下载与自动化脚本实战 (Batch & Automation)

> 本文档介绍如何使用 BBDown 批量抓取 UP 主空间、合集播单、公开收藏夹，并配合历史防重机制、Webhook 回调与定时任务构建全自动下载流水线。

---

## 1. 批量下载场景

### 1.1 UP 主空间全部投稿
传入 UP 主的个人空间链接即可批量下载该 UP 主的所有投稿稿件：
```bash
# 1. 先登录账号（空间投稿接口必须携带登录态）
BBDown login

# 2. 下载该 UP 主的所有投稿视频
BBDown "https://space.bilibili.com/163637592"

# 3. 结合 -p 参数限制下载范围（例如仅下载前 20 个稿件）
BBDown -p 1-20 "https://space.bilibili.com/163637592"
```

> [!NOTE]
> 由于 B 站空间列表接口仅返回稿件基本摘要信息，程序需要逐个请求稿件详情以展开具体的多分 P 结构。投稿数量庞大时（数百上千条），初始解析阶段耗时数分钟属于正常现象。

### 1.2 播单 / 媒体列表 / 收藏夹
BBDown 支持直接传入各类合集与播单链接：
- **媒体列表 / 播单**：`BBDown "https://www.bilibili.com/medialist/play/ml123456"`
- **个人公开收藏夹**：`BBDown "https://space.bilibili.com/123456/favlist?fid=123456"`
- **合集与系列**：`BBDown "https://space.bilibili.com/123456/channel/seriesdetail?sid=12345"`

---

## 2. 自动化与防重机制

### 2.1 历史防重归档 (`--save-archives-to-file`)
在长期批量或定时增量下载时，开启 `--save-archives-to-file` 会在程序目录下自动生成并维护 `BBDown.archives` 记录文件：
- **自动记录**：成功下载的视频 `aid`。
- **自动跳过**：后续再次运行同类命令时，只要 `aid` 已存在于记录文件中，程序会直接跳过下载，避免重复拉取。

```bash
BBDown --save-archives-to-file -p 1-50 "https://space.bilibili.com/163637592"
```

### 2.2 防风控请求延迟 (`--delay-per-page`)
批量下载合集或大批量分 P 时，频繁发起高画质流请求可能触发 B 站的反爬风控（返回 412 或临时封禁 IP）。建议使用 `--delay-per-page` 设置分 P 之间的等待秒数：
```bash
BBDown --delay-per-page 3 -p ALL "https://www.bilibili.com/bangumi/play/ss33073"
```

订阅场景另有一个开关：`BBDown sub check --delay-per-item <秒>`。它给**每个新增内容（以及每个订阅）之间**插入间隔——刚从订阅里加入一个投稿很多的 UP 主时，首次检查会连续下载全部新增稿件，用它把请求摊开可显著降低触发风控的概率（例如 `--delay-per-item 5`）：
```bash
BBDown sub check --per-sub-dir --delay-per-item 5
```
注意 `--delay-per-page` 对 `sub check` 几乎不起作用（它只在多分 P 稿件内部生效），两者不可互相替代，详见 [子命令](Subcommands)。

---

## 3. Webhook 回调通知 (`--notify-webhook`)

当配合 NAS、服务器或消息机器人使用时，可以通过 `--notify-webhook` 指定一个接收 HTTP POST 通知的 URL：

```bash
BBDown --notify-webhook "https://api.example.com/notify/bbdown" <URL>
```

### 3.1 Webhook Payload 规范

```json
{
  "Title": "测试视频标题",
  "Aid": "170001",
  "Bvid": "BV1qt4y1X7TW",
  "Cid": "22334455",
  "FilePath": "/data/downloads/测试视频标题.mp4",
  "IsSuccessful": true,
  "Duration": "00:15:32",
  "Timestamp": 1715000000
}
```

---

## 4. 自动化定时同步实战

### 示例 A：Linux Crontab 每日增量同步订阅
```bash
# 编辑 crontab
crontab -e

# 添加定时任务（每天凌晨 03:00 执行）
0 3 * * * cd /data/bbdown && ./BBDown sub check --save-archives-to-file >> /var/log/bbdown_sub.log 2>&1
```

### 示例 B：PowerShell 批量处理链接文本
```powershell
# run_batch.ps1
$urls = Get-Content -Path "urls.txt"
foreach ($url in $urls) {
    if (-not [string]::IsNullOrWhiteSpace($url)) {
        Write-Host ">>> 正在处理: $url" -ForegroundColor Cyan
        .\BBDown.exe --save-archives-to-file --delay-per-page 2 $url
    }
}
```

---

### 🧭 快速跳转

| 上一篇 | 目录导航 | 下一篇 |
| :--- | :---: | ---: |
| ⬅️ [弹幕与评论区抓取](Danmaku-and-Comments) | 📑 [返回目录](Home) | [Widevine DRM 原生解密](DRM-Decryption) ➡️ |
