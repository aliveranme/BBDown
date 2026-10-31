using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BBDown.Core;
using BBDown.Core.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
namespace BBDown;

/// <summary>
/// serve 安全面（H1 拆解：原 BBDownApiServer.cs 的鉴权 / 限速 / 回环与 Host 判定 / 日志净化 /
/// 请求体净化）。全部成员仍是同一个 <see cref="BBDownApiServer"/> 实例（partial），
/// 共享 token、认证失败限速表等状态。
/// </summary>
public partial class BBDownApiServer
{
    /// <summary>
    /// 认证与 CSRF/跨源防护中间件（无条件安装）。判定顺序：API 路径识别 → 安全响应头 →
    /// token 校验（含失败限速）→ 无 token 时的回环 Host 校验（DNS rebinding 防线）→
    /// 写端点 Origin / Content-Type 闸。逐条语义见方法内注释。
    /// </summary>
    private void UseServeSecurityMiddleware(WebApplication app)
    {
        // 认证与 CSRF 防护中间件（无条件安装）：
        // 1) token 认证（可选）：serve 配置了 --serve-token 后所有任务/查询端点要求
        //    X-Serve-Token 匹配，否则 401。非回环监听（0.0.0.0/具体网卡 IP）在 Run 阶段
        //    强制要求 token（见 Run 内的回环检查），本地回环监听未配置 token 时保持
        //    向后兼容（仅本机信任环境）。
        // 2) CSRF/跨源防护（无条件，写端点）：浏览器对 http://127.0.0.1:23333 的跨源
        //    POST 若用 text/plain（CORS 简单请求，不发预检）即可直接到达 /add-task——
        //    攻击者网页可借此借用操作者本地 B 站凭据提交下载任务、刷满接受队列（DoS）
        //    或取消任务；DNS rebinding（攻击者域名解析到 127.0.0.1）时 Origin 仍是攻击者
        //    域名，同样被拒。这里对写端点（/add-task、/cancel、/remove-finished）校验
        //    Origin 必须与当前请求同源且为回环来源，否则 403；/add-task 的请求体必须是 JSON Content-Type
        //    （text/plain 正是"跨源可发但不触发预检"的载体，直接拒绝）。只读端点
        //    （/get-tasks）不校验 Origin，本地管理页面/脚本可正常查询。
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            bool isApi = path.StartsWithSegments("/get-tasks")
                || path.StartsWithSegments("/add-task")
                || path.StartsWithSegments("/cancel")
                || path.StartsWithSegments("/remove-finished");
            if (isApi)
            {
                // 安全响应头（Info 级观察）：API 响应含任务数据/绝对路径，禁止缓存落盘；
                // nosniff 防 MIME 嗅探。429 的 Retry-After 在各自拒绝点单独附加。
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (!string.IsNullOrEmpty(_serveToken) && !FixedTimeEquals(context.Request.Headers["X-Serve-Token"], _serveToken!))
                {
                    var clientIp = GetClientIp(context);
                    if (IsAuthLockedOut(clientIp))
                    {
                        // 1 分钟窗口内失败超阈值：限速拒绝，令 X-Serve-Token 暴力枚举失效。
                        // 仅记客户端 IP，不回显攻击者可控的路径/XFF（日志体积与注入面同时收口）。
                        Logger.LogWarn($"serve 认证失败过于频繁，已限速: {clientIp}");
                        context.Response.Headers.RetryAfter = "60";
                        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                        return;
                    }
                    // RF-74：认证失败日志必须单行化（RF-25/RF-54 同族：Request.Path/XFF 可含 CRLF）
                    // 并截断——未认证客户端可无限刷此 sink（Logger 无轮转），不截断可灌盘。
                    Logger.LogWarn($"serve 认证失败（401）: {clientIp} {TruncateForLog(context.Request.Path)}");
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                // 无 token 模式（默认回环部署）的 DNS rebinding 防线（RF-15）：浏览器同源
                // GET 不携带 Origin，写端点的 Origin 校验对读端点不生效——攻击者网页经
                // rebinding（攻击者域名 → 127.0.0.1）后即可读取 /get-tasks 响应中的
                // SavePaths 绝对路径等任务数据。仅接受字面回环 Host（localhost/回环 IP），
                // 不做 DNS 解析（rebinding 正是靠 DNS 答案翻转绕过解析校验）。有 token 时
                // 认证已全面把关，且非回环合法部署（反代/自定义域名）Host 不在白名单内，
                // 不强校验。
                else if (string.IsNullOrEmpty(_serveToken) && !IsLoopbackHost(context.Request.Host.Host))
                {
                    Logger.LogWarn($"serve 拒绝非回环 Host 请求（疑似 DNS rebinding）: {SanitizeLogString(context.Request.Host.Value)}");
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                bool isWriteApi = path.StartsWithSegments("/add-task")
                    || path.StartsWithSegments("/cancel")
                    || path.StartsWithSegments("/remove-finished");
                if (isWriteApi)
                {
                    var origin = context.Request.Headers.Origin.ToString();
                    if (!string.IsNullOrEmpty(origin)
                        && (!IsLoopbackOrigin(origin) || !IsSameOrigin(origin, context.Request)))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }
                    if (path.StartsWithSegments("/add-task") && !IsJsonContentType(context.Request.ContentType))
                    {
                        context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                        return;
                    }
                }
            }
            await next();
        });
    }

    private readonly string? _serveToken;

    /// <summary>
    /// 认证失败限速状态：来源 IP → 最近失败时间戳。serve 是长驻进程，token 校验无
    /// 任何限速/锁定会让攻击者（同机进程、或非回环部署时的局域网/公网客户端）无限次
    /// 猜测 X-Serve-Token 而不触发任何告警或冷却。按来源 IP 在 1 分钟窗口内失败超过
    /// 阈值后返回 429，令暴力枚举失效。
    /// </summary>
    private readonly Dictionary<string, List<DateTime>> _authFailures = [];
    private readonly object _authFailuresLock = new();
    private const int MaxAuthFailuresPerMinute = 5;
    /// <summary>认证失败跟踪的 IP 上限：超过后清理过期条目，防一次性 IP 轰炸导致字典无限增长。</summary>
    private const int MaxTrackedAuthFailureIps = 1024;

    /// <summary>
    /// 是否信任直连反代追加的 X-Forwarded-For（--trusted-proxy）：启用后认证失败限速
    /// 按 XFF 末段计键，反代部署下多个客户端不再共享同一反代 IP。默认 false：不读 XFF，
    /// 否则任意客户端可直接伪造 XFF 更换限速计键绕过限速。
    /// </summary>
    private readonly bool _trustProxy;

    /// <summary>
    /// 令牌的常量时间比较：先 SHA-256 定长化（规避长度泄漏），再 FixedTimeEquals。
    /// 字符串 != 按序比较会在首个差异字符短路，理论上允许对高熵 serve token 做
    /// 时序侧信道逐位探测；哈希输出定长后每次比较耗时与输入内容无关。
    /// </summary>
    private static bool FixedTimeEquals(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(provided)),
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected)));
    }

    /// <summary>
    /// 错误消息路径脱敏：IOException 等异常消息常含绝对路径（如
    /// "Could not find file 'D:\data\video\xxx.mp4'"），经 /get-tasks 返回会泄露服务器
    /// 文件系统布局。把 Windows/Unix 绝对路径替换为相对名（仅保留文件名/末段），
    /// 供 <see cref="DownloadTask.ErrorMessage"/> 落盘/返回前调用。
    /// </summary>
    internal static string SanitizeErrorMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return message ?? "";
        return _absolutePathRegex().Replace(message, m => m.Groups["file"].Value);
    }

    /// <summary>
    /// 客户端可控字符串进日志前的单行化：URL/请求体等可含 CR/LF，
    /// 直接拼日志会破坏日志结构（日志注入面）。只替换控制字符，不改变可见内容。
    /// </summary>
    internal static string SanitizeLogString(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\r", "\\r").Replace("\n", "\\n");
    }

    /// <summary>客户端可控字符串进日志前的单行化 + 截断（RF-74）。
    /// Logger 写持久文件且无轮转：未认证客户端可无限刷 401 日志 sink，
    /// 不限制单条长度（受 Kestrel 请求行上限约束但仍可达数 KB）时可灌满磁盘。
    /// 截断到固定长度，保留首段便于排查。</summary>
    internal static string TruncateForLog(string? s, int maxLength = 200)
    {
        var sanitized = SanitizeLogString(s);
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength] + "…";
    }

    /// <summary>匹配盘符/UNC/Unix 根绝对路径（含尾随文件名或目录段），保留路径最后一段。
    /// 形如 D:\a\b\c.mp4、\\server\share\x、/home/u/x.mp4；交替三种根前缀，
    /// 中间目录段任意、末尾段捕获为 file（含中文/空格等非分隔符字符）。
    /// Unix 根分支用 (?&lt;![/:\w]) 负向后顾：URL（http://x/y，/ 前是 : 或 /）与
    /// 相对路径（a/b/c，/ 前是路径字符）不被当作绝对路径替换，避免破坏消息里的 URL。</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(?:[A-Za-z]:\\|(?<![/:\w])/|\\\\)(?:[^\\/\r\n]+[\\/])*(?<file>[^\\/\r\n]+)")]
    private static partial System.Text.RegularExpressions.Regex _absolutePathRegex();

    /// <summary>
    /// 认证失败限速的计键：默认用 TCP 远端 IP；配置 --trusted-proxy 后信任
    /// X-Forwarded-For 的最后一个条目（由直连的可信反代追加，用它代表真实客户端），
    /// 反代部署下多个客户端不再共享同一反代 IP 而互相拖累限速。
    /// </summary>
    private string GetClientIp(Microsoft.AspNetCore.Http.HttpContext context)
    {
        if (_trustProxy)
        {
            var xff = context.Request.Headers["X-Forwarded-For"].ToString();
            var last = xff.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (!string.IsNullOrEmpty(last)) return last;
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    /// <summary>
    /// 认证失败限速判定：1 分钟滑动窗口内失败次数达到阈值返回 true（拒绝服务）。
    /// 每次失败都调用本方法登记；限速状态按来源 IP 隔离，同机合法客户端不受影响。
    /// 窗口过期的空条目会被移除，防止攻击者用大量一次性 IP 轰炸时字典无限增长
    /// （每 IP 一条永不清除的空 list）。攻击者持续用新 IP/XFF 值时每条都是"最近失败"
    /// 不过期，仅删过期条目约束不住字典大小——字典超过上限时按最后失败时间裁剪，
    /// 只保留最近活跃的 <see cref="MaxTrackedAuthFailureIps"/> 条。
    /// internal 供测试直接验证滑动窗口阈值。
    /// </summary>
    internal bool IsAuthLockedOut(string clientIp)
    {
        lock (_authFailuresLock)
        {
            var now = DateTime.UtcNow;
            // 攻击者换 IP 轰炸时字典条目会累积：超过上限则先全局清理过期条目
            //（O(n) 只发生在异常规模下，正常路径零开销）。
            if (_authFailures.Count > MaxTrackedAuthFailureIps)
            {
                foreach (var stale in _authFailures.Where(kv => kv.Value.Count == 0 || now - kv.Value[^1] > TimeSpan.FromMinutes(1)).ToList())
                    _authFailures.Remove(stale.Key);
                // 新 IP 持续轰炸时过期清理后仍超上限：按最后失败时间裁剪，保留最近
                // 活跃的上限条，其余移除，保证字典有界（O(n log n) 仅在异常规模触发）。
                if (_authFailures.Count > MaxTrackedAuthFailureIps)
                {
                    var overflow = _authFailures
                        .OrderBy(kv => kv.Value[^1]) // 最后失败时间最旧的最先裁剪
                        .Take(_authFailures.Count - MaxTrackedAuthFailureIps)
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (var key in overflow) _authFailures.Remove(key);
                }
            }
            if (!_authFailures.TryGetValue(clientIp, out var list))
            {
                _authFailures[clientIp] = [now];
                return false;
            }
            list.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
            if (list.Count == 0)
            {
                // 窗口内条目全部过期：移除键重建，避免保留空 list 条目
                _authFailures.Remove(clientIp);
                _authFailures[clientIp] = [now];
                return false;
            }
            if (list.Count >= MaxAuthFailuresPerMinute) return true;
            list.Add(now);
            return false;
        }
    }

    /// <summary>
    /// 写端点的跨源防护：Origin 头必须解析为回环来源（127.0.0.1/localhost/[::1]）。
    /// 浏览器跨源请求一定携带 Origin；DNS rebinding（攻击者域名解析到 127.0.0.1）下
    /// Origin 仍是攻击者域名而非回环来源，在此被拒。非浏览器客户端（curl/HttpClient）
    /// 不携带 Origin 头，字符串为空即放行，保持向后兼容。
    /// internal 供测试直接验证判定逻辑。
    /// </summary>
    internal static bool IsLoopbackOrigin(string origin)
        // 无法解析为绝对 URI 的 Origin 一律拒绝；其余判定委托给唯一实现
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IsLoopbackHost(uri.DnsSafeHost);

    /// <summary>
    /// 写端点的浏览器 Origin 必须与请求的 scheme、host、port 完全一致。
    /// 只检查 localhost/回环 IP 会把不同端口上的网页也当成可信来源；/cancel 是简单 POST，
    /// 可被这些网页用 HTML 表单跨源触发，即使浏览器不允许读取响应。
    /// </summary>
    private static bool IsSameOrigin(string origin, HttpRequest request)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        int requestPort = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        return uri.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase)
            && uri.DnsSafeHost.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == requestPort;
    }

    /// <summary>
    /// 回环主机判定（**全库唯一实现**）：serve 的 Host 白名单（RF-15）、写端点的 Origin
    /// 跨源防护、监听地址判定（<see cref="IsLoopbackListenAddress"/> 与 CLI 的 `-l` 提示）
    /// 全部委托到这里——此前四处各写一份 `localhost / TryParse / IsLoopback` 判定，
    /// 任一处漂移都会让"是否需要 --serve-token"两侧判断错位。
    /// 仅接受字面回环 Host（localhost / 127.0.0.0/8 / ::1），DNS rebinding 下 Host 是
    /// 攻击者域名即被拒。刻意不做 DNS 解析——rebinding 靠 DNS 答案翻转绕过解析校验，
    /// 字面量比对才可靠。空 Host（极老 HTTP/1.0 客户端）一律拒绝：浏览器/HTTP 库均发送 Host。
    /// internal 供测试直接验证判定逻辑。
    /// </summary>
    internal static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(host, out var ip)) return IPAddress.IsLoopback(ip);
        return false;
    }

    /// <summary>
    /// /add-task 请求体必须为 JSON：text/plain 是 CORS 简单请求的合法 Content-Type，
    /// 浏览器跨源可"不发预检"直接发出，正是攻击者网页驱动本机 serve 提交任务的载体。
    /// 仅接受 application/json 或 application/*+json；无 Content-Type（空 body 或旧客户端）
    /// 放行交由绑定层返回 400/422。
    /// </summary>
    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return true;
        var mediaType = contentType.Split(';')[0].Trim();
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 监听地址是否属于本机回环：127.0.0.1、localhost、[::1]、::1。
    /// 通配监听 0.0.0.0 / :: 与具体网卡 IP 一律视为非回环。
    /// 用 DnsSafeHost（IPv6 字面量不带方括号）以便 IPAddress.TryParse 解析 [::1]。
    /// </summary>
    private static bool IsLoopbackListenAddress(Uri listenUri)
        => IsLoopbackHost(listenUri.DnsSafeHost);

    /// <summary>
    /// 清除网络请求体中可能导致任意命令/程序执行或凭据外泄的字段。
    /// Aria2cArgs 会拼入 aria2c 命令行、Aria2cPath 会覆盖静态进程路径、
    /// Aria2cProxy 会追加进 Aria2cArgs —— 三者都不允许客户端控制。
    /// FFmpegPath/Mp4boxPath/WvdPath/Mp4decryptPath 同属"让服务器执行指定程序"的字段，
    /// 允许客户端控制等价于选择任意已存在的可执行文件。
    /// WorkDir 虽已不写进程 CWD（ChangeWorkingDir 在 serve 下改为 AsyncLocal 按任务隔离，
    /// 见 AppSettings.WorkDir），但它与 FilePattern 同属"客户端控制服务端输出位置"的
    /// 任意写面（可把下载写到服务器任意可写目录）——一并忽略，serve 任务固定输出到服务端 CWD。
    /// </summary>
    internal static void SanitizeUntrustedOptions(ServeRequestOptions req)
    {
        req.Aria2cArgs = "";
        req.Aria2cPath = "";
        req.Aria2cProxy = "";
        req.FFmpegPath = "";
        req.Mp4boxPath = "";
        req.WvdPath = "";
        req.Mp4decryptPath = "";
        req.WorkDir = "";
        // ConfigFile 是 DTO 从 MyOption 继承的死属性（--config-file 实际在 argv 层由
        // BBDownConfigParser 处理，全库无消费点）：当前无害，但若未来接通"按任务合并
        // 本地配置文件"，会是指向服务器任意本地文件的注入点。提前清零（第 13 轮 Info③）。
        req.ConfigFile = null;
        // Insecure 会全局关闭 TLS 证书校验：serve 默认无 token，任意客户端 POST /add-task
        // 携带 {"insecure":true} 即可让携带操作者 SESSDATA 的请求跳过 TLS 校验被中间人截获。
        // serve 强制启用 TLS 校验，忽略该字段。
        req.Insecure = false;
        // ForceHttp 会把媒体 CDN 的 https 改写为明文 http（ReplaceUrl），而下载请求仍携带
        // 操作者的 SESSDATA Cookie（BBDownDownloadUtil 的 Range/HEAD/GET 均带 Cookie）。
        // 与 Insecure 同类威胁模型：任意客户端 POST /add-task 携带 {"forceHttp":true}
        // 即可让携带凭据的媒体流量走明文，被同一链路上的中间人截获整账号凭据。
        // serve 强制 https，忽略该字段。
        req.ForceHttp = false;
        // UserAgent 现按异步流隔离（Config.Current.UserAgent，经 SetUpWork 写入）；
        // serve 下仍统一清零：客户端不能控制服务端出站请求的 UA 指纹。
        req.UserAgent = "";
        // NotifyWebhook 是 CLI 功能：serve 的 /add-task 请求体若携带它，会绕过
        // CallBackWebHook 的 SSRF 校验，让服务器向攻击者指定的任意地址 POST 任务数据。
        req.NotifyWebhook = "";
        // 任务完成回调改为服务端 allowlist：只接受 serve 启动时 --notify-webhook 配置的
        // 固定地址，客户端请求体中的 CallBackWebHook 一律清零（此前仅靠 IsSafeCallbackUrlAsync
        // 校验后仍接受客户端传值——现在完全忽略客户端回调，杜绝任意客户端驱动本机 POST）。
        // FilePattern/MultiFilePattern 会被 SetUpWork 当作 savePathFormat 拼进保存路径，
        // FormatSavePath 只替换占位符、字面量里的 ".." 段原样保留，BBDownMuxer 会按 savePath
        // 建目录——攻击者可借此任意创建目录/写入文件（路径穿越面）。serve 任务一律回落默认模板。
        // RF-82：隐藏的废弃兼容开关（AddDfnSuffix/NoPaddingPageNum 等）在 FilePattern/MultiFilePattern
        // 被清零后会重新填入默认模板（Options.HandleDeprecatedOptions），使"serve 任务固定用默认模板"
        // 的不变量可被客户端 JSON 绕过。这些开关在 API 语义上无意义，一律清零。
        req.AddDfnSuffix = false;
        req.NoPaddingPageNum = false;
        req.BandwidthAscending = false;
        req.OnlyHevc = false;
        req.OnlyAvc = false;
        req.OnlyAv1 = false;
        req.FilePattern = "";
        req.MultiFilePattern = "";
        // RF-81：SelectPage 与 DanmakuFilter* 是客户端可控的"无上限"输入——ParsePageSelection
        // 展开上限已改为累计（Pages.cs），但仍可构造大量分P；弹幕过滤器则是纯装饰性功能，
        // serve 下无必要且是"关键词×弹幕数"的 CPU 放大面。serve 任务一律忽略弹幕过滤器。
        req.DanmakuFilter = null;
        req.DanmakuFilterUser = null;
        // DrmKeyHex/DrmKidHex 会经 DecryptDrmAsync 写入 mp4decrypt 的 key-file 参与解密，
        // 是客户端可控的密钥注入点。serve 任务一律回落 device.wvd 自动取钥；
        // 需要手动 --key/--kid 的操作者应使用 CLI 而非 API。
        req.DrmKeyHex = "";
        req.DrmKidHex = "";
        // 任务完成回调只由服务端启动配置（--notify-webhook）决定，客户端请求体中的
        // 回调字段被完全忽略（服务端 allowlist，不接受客户端指定）。
        req.CallBackWebHook = "";

        // RF-56：Area 是唯一未收口的"拼进官方 API query"字段（Parser 的 area={Area} 裸拼、
        // Workflow 按 Area != "" 跳过登录检测）。与 --area 文档语义（hk|tw|th 枚举）对齐：
        // 仅接受白名单值（大小写不敏感），否则回落 ""——任意文本注入 query 参数语义/
        // 产生"已检测登录"误导日志的通道一并关闭。
        req.Area = req.Area?.Trim().ToLowerInvariant() is "hk" or "tw" or "th" ? req.Area.Trim().ToLowerInvariant() : "";

        // 限制重试参数：客户端传入失控的重试次数或超长延迟会在共享并发槽内阻塞长达数十天，
        // 占满并发槽并使服务瘫痪。将 API 任务重试次数限制在 [1, 3]，延迟限制在 [0, 5000]ms 内。
        // 下限 1：显式传 0 会被 ValidateNumericOptions 的 --retry-count ≥ 1 判为非法任务 Failed，
        // 与这里 Clamp(0,3) 矛盾（A4）——统一为 [1,3]，0 自动升为 1。
        var retryPolicy = RetryPolicy.NormalizeForServe(req.RetryCount, req.RetryDelay);
        req.RetryCount = retryPolicy.RetryCount;
        req.RetryDelay = retryPolicy.RetryDelayMs;

        // 慢速 DoS 面：ValidateNumericOptions 的合法上界（MuxerTimeout 35000 分钟≈583h、
        // DelayPerPage 600s/分P、ThreadSegmentSize 1024MB）允许 API 客户端用合法值占满
        // 并发槽数小时、填满 accept 队列。serve 的受控环境收紧到实际使用范围：
        // 混流最长 2 小时、分P 间隔最长 30s、分片最大 64MB。CLI 仍走完整 ValidateNumericOptions。
        req.MuxerTimeout = Math.Clamp(req.MuxerTimeout, 1, 120);
        req.DelayPerPage = Math.Clamp(req.DelayPerPage, 0, 30);
        req.ThreadSegmentSize = Math.Clamp(req.ThreadSegmentSize, 1, 64);

        // Debug 字段若受客户端控制，任务失败时会将服务端完整异常堆栈暴露在 /get-tasks API 中。
        req.Debug = false;

        // Interactive 会让任务阻塞在 Console.ReadLine（SelectTrackManually）：该调用不在
        // await 点、不可被 CancellationToken 中断，/cancel 无法释放它占用的并发槽——
        // 少量此类任务即可占满 --max-concurrent 并发闸门直至进程重启；前台运行还会
        // 直接消费操作者的键盘输入。serve 下强制关闭（与 Debug 同类处理，RF-24）。
        req.Interactive = false;

        // host 字段决定凭据（Cookie/access_token）的发送目标：serve 默认无认证，
        // 若不校验，任意客户端可把请求指向自己的服务器，骗取操作者保存在
        // BBDown.data 的 B 站 Cookie（LoadCredentials 会在任务流中加载）。
        // 仅允许 B 站官方域名；空值/非官方一律回落官方默认值，并规范化剥离 scheme 前缀。
        req.Host = NormalizeHost(req.Host, "api.bilibili.com");
        req.EpHost = NormalizeHost(req.EpHost, "api.bilibili.com");
        req.TvHost = NormalizeHost(req.TvHost, "api.snm0516.aisee.tv");
        req.UposHost = string.IsNullOrWhiteSpace(req.UposHost) || !IsOfficialHost(req.UposHost)
            ? ""
            : (Uri.TryCreate(req.UposHost, UriKind.Absolute, out var uposUri) ? uposUri.Host : req.UposHost.Trim());
    }

    private static string NormalizeHost(string? host, string defaultHost)
    {
        if (string.IsNullOrWhiteSpace(host) || !IsOfficialHost(host)) return defaultHost;
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri))
            return uri.Host;
        return host.Trim();
    }

    /// <summary>
    /// host 字段是否指向 B 站官方域名。空值视为合法（回落默认）；
    /// 支持 "api.bilibili.com" 与 "https://api.bilibili.com" 两种写法。
    /// 只接受规范化的纯主机名：带路径/斜杠/用户信息/非默认端口等形态一律拒绝，
    /// 防止攻击者用 "evil.com/.bilibili.com" 这类斜杠混淆串在纯后缀匹配下被放行，
    /// 使携带操作者 SESSDATA 的请求发往攻击者主机。
    /// </summary>
    internal static bool IsOfficialHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;

        string hostname = host;
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri))
        {
            // 带 scheme 的写法：必须 http/https、无 userinfo、无路径/query/fragment、默认端口
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            if (!string.IsNullOrEmpty(uri.UserInfo)) return false; // userinfo 可伪装信任域
            if (uri.AbsolutePath.Length > 1) return false;         // 非空路径（含 "/" 以外的路径段）
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
            if (!uri.IsDefaultPort) return false;
            hostname = uri.Host;
        }
        else if (host.Contains('/') || host.Contains('\\') || host.Contains('@') ||
                 host.Contains('?') || host.Contains('#') || host.Contains(':') || host.Contains('['))
        {
            // 非绝对 URL 却含协议/路径/用户信息/端口/IPv6 字面量等分隔符：不是合法纯主机名，拒绝
            return false;
        }

        return HTTPUtil.IsOfficialBilibiliHost(hostname);
    }
}
