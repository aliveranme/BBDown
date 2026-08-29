# API 服务器与 Docker 部署 (API Server & Docker)

> 本文档介绍 BBDown 内置的轻量级 HTTP API 服务器模式（`BBDown serve`），包括任务调度、RESTful API 端点规范、安全鉴权以及 Docker / Docker Compose 容器化部署方案。

---

## 1. 启动 API 服务器

### 1.1 基本启动
```bash
BBDown serve
```
默认在本地回环地址 `http://127.0.0.1:23333` 启动，最大并发槽位为 3。

### 1.2 命令行选项

| 短选项 | 长选项 | 默认值 | 说明 |
| :--- | :--- | :--- | :--- |
| `-l` | `--listen` | `http://127.0.0.1:23333` | 监听的主机与端口 |
| | `--max-concurrent` | `3` | 最大同时执行下载的任务数量 |
| | `--serve-token` | *(无)* | API 访问安全鉴权令牌。优先使用环境变量 `BBDOWN_SERVE_TOKEN` 注入（避免令牌出现在进程命令行；两者冲突时环境变量胜出并告警） |
| | `--trusted-proxy` | *(关闭)* | 信任直连反代追加的 `X-Forwarded-For`（认证失败限速按客户真实 IP 计键）。仅在 serve 前方确有可信反代时启用，否则客户端可伪造 XFF 绕过限速 |
| | `--notify-webhook` | *(无)* | 任务完成时向该固定地址发送 HTTP POST 回调（服务端配置；`/add-task` 请求体中的回调字段一律被忽略） |

### 1.3 生产环境启动示例
```bash
BBDown serve -l http://0.0.0.0:23333 --max-concurrent 5 --serve-token "secret_token_123"
```

> 生产环境建议以 HTTPS 反向代理（nginx/caddy）对外暴露；仅当前方确有可信反代时，再在命令中追加 `--trusted-proxy`（默认关闭），使认证失败限速按 `X-Forwarded-For` 中的客户真实 IP 计键。

---

## 2. 安全设计与防护规范

1. **非回环监听强制 Token**：监听地址设为非 `127.0.0.1`（如 `0.0.0.0`）时，**必须显式提供 `--serve-token`**，否则服务将直接拒绝启动退出。
2. **鉴权请求头**：启用 `--serve-token` 后，所有客户端请求均需携带请求头 `X-Serve-Token: <token>`，否则返回 `401 Unauthorized`。令牌优先经环境变量 `BBDOWN_SERVE_TOKEN` 注入，避免 `ps` 等进程列表暴露。
3. **入参安全性过滤**：为防止远程代码执行、凭据外泄或路径穿越，API 提交的配置中以下危险字段一律被强制忽略：`aria2cArgs`、`aria2cPath`、`aria2cProxy`、`ffmpegPath`、`mp4boxPath`、`wvdPath`、`mp4decryptPath`、`workDir`、`insecure`、`forceHttp`、`userAgent`、`notifyWebhook`、`filePattern`、`multiFilePattern`、`drmKeyHex`、`drmKidHex`、`callBackWebHook`（任务固定输出到服务端默认目录模板；重试/超时等数值参数还会被钳制到受控范围）。`host/epHost/tvHost/uposHost` 四个 Host 字段仅接受 B 站官方域名白名单，非官方值一律回落官方默认。
4. **反代与加密**：由于内置 HTTP 服务器不包含 HTTPS 传输加密，公网开放时强烈建议配置 Nginx / Caddy 反向代理。若前方确有可信反代，可加 `--trusted-proxy` 使认证失败限速按 `X-Forwarded-For` 的客户真实 IP 计键；无反代时切勿启用（客户端可伪造 XFF 绕过限速）。

---

## 3. REST API 接口规范

### 3.1 接口概览

| 接口 | 方法 | 说明 | 成功状态码 |
| :--- | :--- | :--- | :--- |
| `/add-task` | `POST` | 提交新下载任务到执行/排队队列 | `202 Accepted` |
| `/get-tasks/` | `GET` | 获取所有任务列表（运行中 + 已完成） | `200 OK` |
| `/get-tasks/running` | `GET` | 获取当前正在下载中的任务列表 | `200 OK` |
| `/get-tasks/finished` | `GET` | 获取所有已完成（成功或失败）的任务列表 | `200 OK` |
| `/get-tasks/{id}` | `GET` | 根据 TaskId (JobId) 或 Aid 查询特定任务详情 | `200 OK` / `404` |
| `/cancel/{id}` | `POST` | 取消正在排队或执行中的任务 | `200 OK` / `404` |
| `/remove-finished` | `DELETE` | 清理移除所有已完成的任务记录 | `200 OK` |
| `/remove-finished/failed` | `DELETE` | 仅清理移除失败的任务记录 | `200 OK` |
| `/remove-finished/{id}` | `DELETE` | 清理指定的已完成任务记录 | `200 OK` |

---

### 3.2 接口详细请求与返回

#### 添加任务 (`POST /add-task`)
- **Request Headers**:
  - `Content-Type: application/json`
  - `X-Serve-Token: secret_token_123` (若服务端已配置)
- **Request Body**:
  ```json
  {
    "Url": "https://www.bilibili.com/video/BV1qt4y1X7TW",
    "UseTvApi": true,
    "DownloadDanmaku": true,
    "EncodingPriority": "hevc,av1,avc",
    "DfnPriority": "1080P 高码率, 1080P 高清",
    "SelectPage": "1"
  }
  ```
- **Response (`202 Accepted`)**:
  ```json
  {
    "TaskId": "c7a8b9e0-1234-5678-90ab-cdef12345678"
  }
  ```
- **错误状态码**：
  - `400 Bad Request`：请求体不是合法 JSON 或缺少 `Url`。
  - `401 Unauthorized`：缺少或错误的 `X-Serve-Token`。
  - `429 Too Many Requests`：排队队列已满（排队队列上限为 `--max-concurrent` × 9）。

#### 查询任务详情 (`GET /get-tasks/{id}`)
- **Response (`200 OK`)**:
  ```json
  {
    "JobId": "c7a8b9e0-1234-5678-90ab-cdef12345678",
    "Aid": "170001",
    "Title": "测试视频",
    "Pic": "http://i0.hdslb.com/bfs/archive/xxx.jpg",
    "TotalPages": 1,
    "Progress": 100.0,
    "Status": "Finished",
    "IsSuccessful": true,
    "ErrorReason": ""
  }
  ```

---

## 4. 客户端调用代码示例

### 4.1 cURL 示例
```bash
# 提交任务
curl -X POST http://127.0.0.1:23333/add-task \
     -H "Content-Type: application/json" \
     -H "X-Serve-Token: secret_token_123" \
     -d '{"Url": "BV1qt4y1X7TW", "UseTvApi": true}'

# 查询状态
curl -H "X-Serve-Token: secret_token_123" http://127.0.0.1:23333/get-tasks/running
```

### 4.2 Python (requests) 示例
```python
import requests

SERVER_URL = "http://127.0.0.1:23333"
HEADERS = {"X-Serve-Token": "secret_token_123"}

# 1. 提交下载任务
payload = {
    "Url": "https://www.bilibili.com/video/BV1qt4y1X7TW",
    "DownloadDanmaku": True
}
resp = requests.post(f"{SERVER_URL}/add-task", json=payload, headers=HEADERS)
task_id = resp.json().get("TaskId")
print(f"Task submitted with ID: {task_id}")

# 2. 轮询进度
status_resp = requests.get(f"{SERVER_URL}/get-tasks/{task_id}", headers=HEADERS)
print(status_resp.json())
```

---

## 5. Docker 与 Docker Compose 部署

### 5.1 Docker Compose 一键启动 (`docker-compose.yml`)

```yaml
version: '3.8'

services:
  bbdown-server:
    image: aliveranme/bbdown:latest
    container_name: bbdown-service
    restart: unless-stopped
    ports:
      - "23333:23333"
    volumes:
      - /mnt/storage/downloads:/app/downloads   # 挂载下载产物目录
      - /mnt/storage/bbdown_config:/app/data    # 挂载 BBDown.data / device.wvd
    command:
      - "serve"
      - "-l"
      - "http://0.0.0.0:23333"
      - "--max-concurrent"
      - "5"
      - "--serve-token"
      - "your_super_secret_token"
```

启动服务：
```bash
docker compose up -d
```

### 5.2 宿主机 Docker CLI 启动
```bash
docker run -d \
  --name bbdown \
  --restart unless-stopped \
  -p 23333:23333 \
  -v $(pwd)/downloads:/app/downloads \
  -v $(pwd)/config:/app/data \
  aliveranme/bbdown:latest \
  serve -l http://0.0.0.0:23333 --serve-token "your_super_secret_token"
```

---

### 🧭 快速跳转

| 上一篇 | 目录导航 | 下一篇 |
| :--- | :---: | ---: |
| ⬅️ [Widevine DRM 原生解密](DRM-Decryption) | 📑 [返回目录](Home) | [内部架构与设计原理](Architecture-and-Design) ➡️ |
