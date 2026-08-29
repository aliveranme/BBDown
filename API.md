# JSON API文档

## API

如果以服务器模式启动BBDown，BBDown会在本地启动一个http server，该服务器有以下API：

### 获取任务列表
**Endpoint:** `/get-tasks/`

**Method:** GET

**Description:** 获取所有任务的列表，包括正在运行的任务和已完成的任务。

**Response:** JSON格式的`DownloadTaskCollection`。

### 获取正在运行的任务列表
**Endpoint:** `/get-tasks/running`

**Method:** GET

**Description:** 获取所有正在运行的任务的列表。

**Response:** JSON格式的`List<DownloadTask>`, 正在运行的任务列表。

### 获取已完成的任务列表
**Endpoint:** `/get-tasks/finished`

**Method:** GET

**Description:** 获取所有已完成的任务的列表。

**Response:**  JSON格式的`List<DownloadTask>`, 已完成的任务列表。

### 获取特定任务
**Endpoint:** `/get-tasks/{id}`

**Method:** GET

**Description:** 获取特定任务的详细信息。`{id}` 优先匹配任务的 `JobId`（`/add-task` 返回的任务 ID），也支持按视频的 `Aid` 或提交时的 `Url` 匹配（兼容旧客户端与旧持久化记录）。

**Parameters:**
- `{id}` (路径参数): 任务的 JobId（或兼容的 Aid / 提交 Url）

**Response:** 如果找到匹配的任务，将返回JSON格式的`DownloadTask`。如果未找到匹配的任务，将返回404 Not Found。

### 添加任务
**Endpoint:** `/add-task`

**Method:** POST

**Description:** 向任务列表中添加新任务。

**Request Body:** JSON格式的任务信息，需要符合`MyOption`数据结构。并不要求带有MyOption中的每一个字段，只需要有`Url`字段就够了。

**Response:**
- 如果请求有效并成功添加任务，将返回 **202 Accepted**，响应体为 `{"TaskId":"<jobId>"}`，其中 `TaskId` 是无业务含义的 JobId（GUID 字符串），用于查询任务详情（`/get-tasks/{id}`）或取消任务（`/cancel/{id}`）。JobId 在任务入队时立即生成，与 URL 解析出的 Aid 无关——提交完整视频 URL 后同样可查询/取消。
- 如果请求无效，将返回400 Bad Request，并附带错误消息`"输入有误"`。
- 如果任务队列已满，将返回 **429 Too Many Requests**：执行中 + 排队等待的任务总数上限为 `--max-concurrent` × 9（每个并发槽位允许最多 8 个排队任务），达到上限后立即拒绝新任务，避免长驻进程被无限堆积的后台任务/配置对象/CTS 拖垮。

> 注：任务入队后，URL 解析与下载在后台异步进行；即使 URL 无法解析，客户端也能凭 JobId 查询到一条失败（`Failed`）任务及其错误原因。

### 取消任务
**Endpoint:** `/cancel/{id}`

**Method:** POST

**Description:** 取消一个正在运行或排队等待中的任务。`{id}` 优先匹配任务的 `JobId`，也支持按 `Aid` / 提交 `Url` 匹配（兼容旧客户端）。

**Parameters:**
- `{id}` (路径参数): 任务的 JobId（或兼容的 Aid / 提交 Url）

**Response:**
- 如果任务存在且可取消，返回 200 OK。
- 如果任务不存在（已完成或从未提交），返回 404 Not Found。

> 注意：已完成（succeeded/failed）的任务无法取消，只能通过 `/remove-finished` 清理。

### 移除已完成的任务
**Endpoint:** `/remove-finished`

**Method:** DELETE

**Description:** 移除所有已完成的任务

**Response:**
- 返回200 OK。

### 移除已完成的任务
**Endpoint:** `/remove-finished/failed`

**Method:** DELETE

**Description:** 移除所有已完成但是失败(`IsSuccessful == false`)的任务

**Response:**
- 返回200 OK。

### 移除特定已完成的任务
**Endpoint:** `/remove-finished/{id}`

**Method:** DELETE

**Description:** 移除特定已完成的任务。`{id}` 优先匹配任务的 `JobId`，也支持按 `Aid` / 提交 `Url` 匹配（兼容旧客户端）。

**Parameters:**
- `{id}` (路径参数): 任务的 JobId（或兼容的 Aid / 提交 Url）

**Response:**
- 无论是否能找到对应ID的任务，均返回200 OK。

> 注意：`FilePattern` / `MultiFilePattern` 在服务器模式下会被忽略（见上"安全边界"），因此 `/add-task` 无法用该字段自定义保存路径；请通过 `serve` 启动时的 `--work-dir` 指定默认工作目录。

## 数据结构

### `DownloadTask` 数据结构
`DownloadTask` 数据结构表示一个下载任务的信息。

**属性：**
- `JobId` `<string>`: 任务唯一标识（GUID 字符串），`/add-task` 返回的 TaskId 即 JobId，用于查询/取消任务。旧持久化记录（无该字段）默认为空串。
- `Aid` `<string>`: 视频解析出的 Aid（业务字段）。解析成功前为空/为提交 Url，不再作为任务唯一标识。
- `Url` `<string>`: 下载任务请求时的URL，不一定需要完整的URL，命令行支持的`av|bv|BV|ep|ss`都可以在这里使用。
- `TaskCreateTime` `<long>`: 任务创建时间，Unix时间戳，精确到秒，本机时区。
- `Title` `<string?>`: 视频的标题。
- `Pic` `<string?>`: 视频的封面图片链接。
- `VideoPubTime` `<long?>`: 视频发布时间，Unix时间戳，精确到秒。
- `TaskFinishTime` `<long?>`: 任务完成时间，Unix时间戳，精确到秒，本机时区。
- `Progress` `<double>`: 任务的下载进度，为0-1区间范围的小数。
- `DownloadSpeed` `<double>`: 下载速度, 单位为Byte/s。下载中时为最后一次更新的实时速度，下载完成后为平均速度。
- `TotalDownloadedBytes` `<double>`: 总下载字节(Byte)数，完成后的数字比实际文件偏小。
- `IsSuccessful` `<bool>`: 标识任务是否成功完成。
- `Status` `<string>`: 任务状态，取值 `Queued`（排队等待） / `Running`（下载中） / `Succeeded`（成功） / `Failed`（失败） / `Cancelled`（被取消）。

### `DownloadTaskCollection` 数据结构
`DownloadTaskCollection` 数据结构包含两个列表，分别表示正在运行的任务和已完成的任务。

**属性：**
- `Running` `<List<DownloadTask>>`: 包含正在运行的任务的列表，每个元素都是 `DownloadTask` 数据结构。
- `Finished` `<List<DownloadTask>>`: 包含已完成的任务的列表，每个元素都是 `DownloadTask` 数据结构。

### `MyOption` 数据结构

参考[BBDown/Configuration/MyOption.cs](./BBDown/Configuration/MyOption.cs)。属性和命令行参数几乎是一一对应的，相应的值填写使用命令行会使用的值即可。这个结构会随着版本变化，请参考对应版本时候的文件。

> 安全边界：`/add-task` 请求体中的 `host/epHost/tvHost/uposHost` 仅接受 B 站官方域名（其余回落默认值）；`aria2cArgs/aria2cPath/aria2cProxy/ffmpegPath/mp4boxPath/wvdPath/mp4decryptPath/workDir/notifyWebhook/callBackWebHook/userAgent/filePattern/multiFilePattern/insecure/forceHttp/drmKeyHex/drmKidHex` 一律被忽略（`filePattern`/`multiFilePattern` 会作为保存路径模板被拼进输出路径，可能被用于路径穿越；`insecure`/`forceHttp` 会分别关闭 TLS 证书校验、把携带凭据的媒体流量改写为明文 HTTP，可能导致请求被中间人截获；`drmKeyHex`/`drmKidHex` 是客户端可控的解密密钥注入点；回调字段 `callBackWebHook`/`notifyWebhook` 已改为服务端 allowlist，客户端请求体中的回调地址被忽略）。任务始终保存到默认目录模板。

### 注意事项
- 由于BBDown的下载进度回报频率所限，`TotalDownloadedBytes`会比实际下载的文件略低，大概会少等效于1秒下载速度的文件体积，如果文件本身就非常小那这个数字偏差会较大。
- 现在可通过 `POST /cancel/{id}` 取消排队中或运行中的任务（已完成任务不可取消，只能通过 `/remove-finished` 清理）。
- 服务器默认最多同时执行 3 个下载任务，可通过 serve 的 `--max-concurrent` 调整；超出部分排队等待。
- 配置了 `--serve-token` 后，`/get-tasks`、`/add-task`、`/cancel`、`/remove-finished` 所有端点都要求请求头 `X-Serve-Token` 匹配，否则返回 401。令牌优先使用环境变量 `BBDOWN_SERVE_TOKEN` 注入（避免出现在进程命令行；与 CLI 参数冲突时环境变量胜出并告警）。
- **非回环监听必须 `--serve-token`**：`-l http://0.0.0.0:<port>` 或 `-l http://<非回环IP>:<port>` 等非回环地址会把任务端点暴露到局域网/公网，未配置 `--serve-token` 时 serve 拒绝启动（提示必须配置 token）。
- **`--trusted-proxy` 仅在确有可信反向代理时启用**：认证失败限速默认按直连 IP 计键；启用后信任反代追加的 `X-Forwarded-For`，按客户真实 IP 计键。serve 前方无反代时切勿启用，否则客户端可伪造 XFF 头绕过限速。
- **不再启用任意来源 CORS**：serve 已移除 `AllowAnyOrigin`，响应不返回 `Access-Control-Allow-Origin`，浏览器跨域请求会被同源策略拦截。
- **写端点带 CSRF/跨源防护（无条件生效）**：`/add-task`、`/cancel`、`/remove-finished` 校验请求头 `Origin` 必须为回环来源（`127.0.0.1`/`localhost`/`[::1]`）或缺失（非浏览器客户端），否则返回 403；`/add-task` 的请求体必须为 `application/json`（`text/plain` 是 CORS 简单请求的合法载体，可直接跨源发出不触发预检），否则返回 415。浏览器管理页面请通过 `http://localhost` 或 `http://127.0.0.1` 托管（`file://` 页面跨源 POST 的 `Origin: null` 会被拒绝）。
- **任务完成回调由服务端配置**：serve 启动时通过 `--notify-webhook <url>` 指定任务完成回调地址（服务端固定 allowlist）；客户端请求体中的 `callBackWebHook` / `notifyWebhook` 字段被忽略。回调地址会拦截回环/链路本地/云元数据等敏感目标，并在每次回调连接时用已校验的 DNS 解析结果绑定连接（缓解 DNS 重绑定）。
- 默认仅监听 `http://127.0.0.1:23333`（仅本机可访问）。需要对外提供服务时请显式 `-l http://0.0.0.0:<port>`，并务必配合 `--serve-token` 与反向代理。
- API 服务器不支持 HTTPS 配置，如有需要请自行使用 nginx 等反向代理进行配置。
- 服务器模式下任务列表已加锁保护并发访问；`Config` 凭据在每个任务流内通过 AsyncLocal 隔离（每个任务的 SetUpWork 应用自己的 Cookie/Token/Host），并发任务不会互相读到对方的凭据。

### 使用例

#### 用BV号添加任务

```shell
curl -X POST -H 'Content-Type: application/json' -d '{ "Url": "BV1qt4y1X7TW" }' http://localhost:58682/add-task
```

#### 下载到指定目录

> 服务器模式下 `FilePattern` 字段会被忽略（见上"安全边界"），请改用 `serve` 的 `--work-dir` 指定默认工作目录，任务产物保存到该目录下的默认模板路径。示例仅供参考传统 CLI 用法：

Windows:
```shell
curl -X POST -H 'Content-Type: application/json' -d '{ "Url": "BV1qt4y1X7TW" }' http://localhost:58682/add-task
```

Unix-Like:
```shell
curl -X POST -H 'Content-Type: application/json' -d '{ "Url": "BV1qt4y1X7TW" }' http://localhost:58682/add-task
```
