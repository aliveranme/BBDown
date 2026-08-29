using System.Net;
using System.Net.Http.Headers;

namespace BBDown.Core.Util;

public static partial class HTTPUtil
{

    /// <summary>
    /// 构造按策略固化的共享客户端。SSL 校验策略在**构造时**根据 <paramref name="skipSslCheck"/>
    /// 定死进 RemoteCertificateValidationCallback 闭包，之后不再读取 Config.Current。
    /// 校验池与不安全池各自持有独立的 SocketsHttpHandler 连接池：此前回调在每次握手时
    /// 读 Config.Current.SkipSslCheck（AsyncLocal + 全局双写），并发/长驻场景下
    /// --insecure 任务建立的未验证连接会被共享池复用给其它未开 --insecure 的任务
    /// （连接 5 分钟寿命内），安全边界被污染。现在不安全请求只从独立池取连接，
    /// 校验请求与不安全请求互不复用连接。
    /// </summary>
    private static HttpClient CreateClient(bool allowRedirect, TimeSpan timeout, bool skipSslCheck)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowRedirect,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = CreateSslOptions(skipSslCheck),
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    private static System.Net.Security.SslClientAuthenticationOptions CreateSslOptions(bool skipSslCheck)
    {
        if (skipSslCheck)
            Logger.LogDebug("SSL 证书验证已禁用");
        return new System.Net.Security.SslClientAuthenticationOptions
        {
            // skipSslCheck 在构造时捕获并固化：同一池内所有连接共用同一策略，
            // 与请求所属任务的 Config.Current 无关（连接池已按策略隔离）。
            RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
            {
                if (skipSslCheck)
                {
                    if (errors != System.Net.Security.SslPolicyErrors.None)
                        Logger.LogDebug("SSL 证书验证被跳过，证书错误: {0}", errors);
                    return true;
                }
                return errors == System.Net.Security.SslPolicyErrors.None;
            },
        };
    }

    // 并发下载与 serve 模式会从多个线程首次访问，Lazy 保证每个策略池只创建一个 HttpClient，
    // 否则多余实例各自持有一份 SocketsHttpHandler 连接池，造成 socket 泄漏。
    // AppHttpClient 按当前异步流配置路由到校验池/不安全池；不安全池仅在 --insecure
    // 流程首次访问时才被创建，不会为从未使用的策略白白建连接池。
    private static readonly Lazy<HttpClient> _appHttpClient =
        new(() => CreateClient(allowRedirect: true, TimeSpan.FromMinutes(2), skipSslCheck: false), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<HttpClient> _insecureAppHttpClient =
        new(() => CreateClient(allowRedirect: true, TimeSpan.FromMinutes(2), skipSslCheck: true), LazyThreadSafetyMode.ExecutionAndPublication);

    // 媒体下载专用池：AllowAutoRedirect=false（B3-F3，下载请求携带登录 Cookie，
    // 不跟随任何重定向以免凭据随 3xx 引向非官方主机）。其余与 AppHttpClient 同构。
    private static readonly Lazy<HttpClient> _mediaHttpClient =
        new(() => CreateClient(allowRedirect: false, TimeSpan.FromMinutes(2), skipSslCheck: false), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<HttpClient> _insecureMediaHttpClient =
        new(() => CreateClient(allowRedirect: false, TimeSpan.FromMinutes(2), skipSslCheck: true), LazyThreadSafetyMode.ExecutionAndPublication);

    public static HttpClient AppHttpClient =>
        Config.Current.SkipSslCheck ? _insecureAppHttpClient.Value : _appHttpClient.Value;

    /// <summary>
    /// 始终校验证书的共享客户端：WidevineCdm 许可证请求（响应携带内容密钥）必须走此池，
    /// 不受 --insecure 影响——跳过校验会让中间人直接窃取解密密钥，不能由用户选项降级。
    /// </summary>
    public static HttpClient VerifiedAppHttpClient => _appHttpClient.Value;

    // 始终校验 + 禁自动跳转的专用池：与 _appHttpClient 同超时、同校验策略，仅重定向策略不同。
    private static readonly Lazy<HttpClient> _verifiedNoRedirectClient =
        new(() => CreateClient(allowRedirect: false, TimeSpan.FromMinutes(2), skipSslCheck: false), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 始终校验证书且禁自动跟随重定向的共享客户端：供携带签名载荷的非幂等请求使用
    /// （WidevineCdm 许可证 POST）。VerifiedAppHttpClient（AllowAutoRedirect=true）会把
    /// 307/308 连同 body 重放到跨主机目标——许可证请求体是设备私钥签名的 challenge，
    /// 随重定向重放即签名外发；此处 3xx 由调用方显式拦截（与 gRPC POST 的 B3-F2 收口同构）。
    /// 与 VerifiedAppHttpClient 同样不受 --insecure 影响（独立于不安全池，不能由用户选项降级）。
    /// </summary>
    public static HttpClient VerifiedNoRedirectClient => _verifiedNoRedirectClient.Value;

    /// <summary>
    /// 媒体下载专用客户端：禁用自动跟随重定向（B3-F3）。下载请求携带登录 Cookie，
    /// 且 URL 由 B 站播放接口（受信任源）返回——若开启自动跳转，一个被篡改/被攻破的
    /// CDN 或上游响应的 3xx 会把带 Cookie 的下载流量引向任意主机（凭据外泄）。
    /// 禁用后 3xx 由调用方按非 2xx 处理（EnsureSuccessStatusCode 抛错），不跟随。
    /// 与 AppHttpClient 相同的 insecure 策略隔离（独立池，互不复用连接）。
    /// </summary>
    public static HttpClient MediaDownloadClient =>
        Config.Current.SkipSslCheck ? _insecureMediaHttpClient.Value : _mediaHttpClient.Value;

    /// <summary>
    /// 从响应头 Date 校准服务器时钟偏移（秒），写入 Config（流内 + 全局双写，
    /// 见 <see cref="Config.SET_CLOCK_OFFSET"/>）。RFC 7231 要求所有 HTTP 响应携带
    /// Date（GMT），读不到时直接跳过。仅当偏移在 ±1h 内才写入：畸形/恶意 Date 头
    /// 不污染时钟。只对 WBI 签名权威主机 api.bilibili.com 校准：其它主机（番剧/国际版
    /// 等边缘服务器）的 Date 是各自时钟，写入全局偏移会抖动签名基准。偏移为 0 时不写。
    /// 必须在 EnsureSuccessStatusCode 之前调用：4xx/5xx 错误响应同样带 Date——本地时钟
    /// 偏差导致签名被拒的请求，其错误响应恰好能校准下一次重试的 wts/ts。
    /// internal 供测试直接调用验证校准逻辑。
    /// </summary>
    /// <param name="response">企业响应。Date 头将被校准。</param>
    /// <param name="fromVerifiedPool"><c>true</c> 表示响应来自始终校验证书的连接池。
    /// 不安全池（--insecure）下的 Date 头可被链上中间人伪造：写入全局时钟偏移会扰动
    /// 同一进程内其它已校验流的 WBI 签名基准（B3-L2）。偏移是进程级全局物理量，
    /// 不应由不可信传输的响应改写。</param>
    internal static void CalibrateClock(HttpResponseMessage response, bool fromVerifiedPool = true)
    {
        // --insecure 连接的 Date 头不可信（中间人可控）：不参与全局时钟校准，
        // 避免不安全流污染已验证流的 WBI 时间基准。
        if (!fromVerifiedPool) return;
        if (response.Headers.Date is not { } serverDate) return;
        // 只对 WBI 签名权威主机 api.bilibili.com（含子域）校准：WBI 签名经 Parser.WbiSign
        // 只发给该主机，其它主机（bangumi.bilibili.com、api.bilibili.tv、api.biliintl.com
        // 或 serve 本地回调）的 Date 是各自边缘服务器时钟，写入全局偏移会抖动 WBI 签名
        // 基准。直接构造的 HttpResponseMessage（单元测试）无 RequestMessage，放行。
        if (response.RequestMessage?.RequestUri?.Host is { } host
            && !host.Equals("api.bilibili.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".api.bilibili.com", StringComparison.OrdinalIgnoreCase))
            return;
        long offset = serverDate.ToUnixTimeSeconds() - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // 阈值收紧到 ±1 小时：WBI 签名 wts 时效窗口约 60s，正常偏差远小于 1h；
        // 超过 1h 的 Date 头只可能是畸形/恶意值或系统时钟被大幅改动，写入反而有害。
        if (Math.Abs(offset) > 3600) return;
        if (offset != Config.Current.ServerClockOffsetSeconds)
            Config.SET_CLOCK_OFFSET(offset);
    }

    /// <summary>
    /// 直播录制专用客户端：无限流持续读取，不套用全局 2 分钟超时。
    /// 实测 .NET 的 HttpClient.Timeout 对 ResponseHeadersRead 之后的流式读取并不生效，
    /// 但无限连接在语义上不应携带任何客户端超时——一旦未来改动（如换 handler 或
    /// ResponseContentRead）触达该超时，会直接掐断整场录制。独立客户端也避免长期
    /// 占用共享连接池里的一条连接。与 AppHttpClient 相同的策略隔离。
    /// </summary>
    private static readonly Lazy<HttpClient> _streamingHttpClient =
        new(() => CreateClient(allowRedirect: true, Timeout.InfiniteTimeSpan, skipSslCheck: false), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<HttpClient> _insecureStreamingHttpClient =
        new(() => CreateClient(allowRedirect: true, Timeout.InfiniteTimeSpan, skipSslCheck: true), LazyThreadSafetyMode.ExecutionAndPublication);

    public static HttpClient StreamingHttpClient =>
        Config.Current.SkipSslCheck ? _insecureStreamingHttpClient.Value : _streamingHttpClient.Value;

    /// <summary>
    /// 禁自动跳转的共享客户端：供逐跳校验重定向（GetWebLocationCheckedAsync /
    /// GetWebSourceAnonymousCheckedAsync）复用。手动逐跳跟随需要 AllowAutoRedirect=false，
    /// 每次跳转新建 HttpClient 会重复建连接池；共享实例避免 socket 泄漏与握手开销。
    /// 与 AppHttpClient 相同的策略隔离（校验/不安全两个池）。
    /// </summary>
    private static readonly Lazy<HttpClient> _noRedirectClient =
        new(() => CreateClient(allowRedirect: false, TimeSpan.FromMinutes(1), skipSslCheck: false), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<HttpClient> _insecureNoRedirectClient =
        new(() => CreateClient(allowRedirect: false, TimeSpan.FromMinutes(1), skipSslCheck: true), LazyThreadSafetyMode.ExecutionAndPublication);

    private static HttpClient NoRedirectClient =>
        Config.Current.SkipSslCheck ? _insecureNoRedirectClient.Value : _noRedirectClient.Value;

    /// <summary>手动逐跳重定向校验的最大跳数（防开放重定向无限循环）。</summary>
    private const int MaxRedirectHops = 10;

    private static readonly string[] platforms = { "Windows NT 10.0; Win64", "Macintosh; Intel Mac OS X 10_15", "X11; Linux x86_64" };

    private static string RandomVersion(int minInclusive, int maxInclusive)
    {
        // 真实浏览器主版本是整数（如 139），比浮点小数更接近真实指纹；闭区间含 maxInclusive
        return Random.Shared.Next(minInclusive, maxInclusive + 1).ToString();
    }

    private static string GetRandomUserAgent()
    {
        // 2026 年主流 Chrome/Firefox 版本段：80-110 的陈旧指纹本身就是风控信号
        string[] browsers = { $"AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{RandomVersion(130, 150)}.0.0.0 Safari/537.36", $"Gecko/20100101 Firefox/{RandomVersion(130, 150)}.0" };
        return $"Mozilla/5.0 ({platforms[Random.Shared.Next(platforms.Length)]}) {browsers[Random.Shared.Next(browsers.Length)]}";
    }

    /// <summary>进程级默认 UA：没有任务级 UA 时使用，保持整个进程一个默认值（与原静态属性行为一致）。</summary>
    private static readonly Lazy<string> _defaultUserAgent = new(GetRandomUserAgent, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 解析实际使用的 User-Agent：显式参数优先，其次当前异步流配置的
    /// <see cref="Config.Current"/> UserAgent（CLI --user-agent / serve 任务统一经
    /// SetUpWork 写入），都没有时用进程级随机默认值。原先的静态可变属性
    /// HTTPUtil.UserAgent 会被并发任务互相覆盖（一个任务带自定义 UA 污染所有后续任务），
    /// 这里收敛为按流隔离，serve 下各任务互不干扰。
    /// </summary>
    public static string GetUserAgent(string? userAgent)
        => !string.IsNullOrEmpty(userAgent)
            ? userAgent
            : !string.IsNullOrEmpty(Config.Current.UserAgent)
                ? Config.Current.UserAgent!
                : _defaultUserAgent.Value;

    /// <summary>从 User-Agent 提取 Chrome 主版本（构造 sec-ch-ua 用；非 Chrome UA 不匹配）。</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"Chrome/(\d+)")]
    private static partial System.Text.RegularExpressions.Regex ChromeVersionRegex();

    /// <summary>
    /// 抓取 B 站 JSON API 的网页内容（携带登录 Cookie）。默认拒绝 HTML 响应：
    /// B 站风控/错误页常以 200 返回 HTML，此时抛 <see cref="RiskControlResponseException"/>
    /// 给出可读诊断，替代下游 JsonDocument.Parse 的裸 JsonException。
    /// 少数确实抓取 HTML/XML 的调用点（番剧页、player.so）传 <paramref name="rejectHtml"/> = false。
    /// 5xx/传输层失败/超时按 MaxRetryCount 指数退避重试（4xx 风控不重试）。
    /// </summary>
    public static async Task<string> GetWebSourceAsync(string url, string? userAgent = null, CancellationToken token = default, bool rejectHtml = true)
        => await GetWebSourceCoreAsync(url, sendCookie: true, userAgent: userAgent, token: token, rejectHtml: rejectHtml);

    /// <summary>
    /// 获取网页内容并透出 Set-Cookie 响应头。仅用于需要登录凭据的接口：
    /// B 站新版扫码登录（2026）将 SESSDATA/bili_jct/DedeUserID 等凭证经
    /// poll 响应的 Set-Cookie（HttpOnly）下发，data.url 仅剩 crossDomain 跳转参数，
    /// 只返回 body 会永久丢失登录凭证。
    /// </summary>
    public static async Task<(string Body, List<string> SetCookies)> GetWebSourceWithSetCookiesAsync(
        string url, string? userAgent = null, CancellationToken token = default)
    {
        // B3-S1 纵深防御与 GetWebSourceCoreAsync(sendCookie:true) 同源收口：本方法携带
        // 操作者 Cookie（且响应 Set-Cookie 是最敏感的新凭证下发通道），URL 不可信时
        // 必须在发起任何网络请求前拒绝，防止未来调用方把用户可控 URL 传入导致凭据外发。
        if (!IsTrustedCookieHost(url))
            throw new InvalidOperationException($"拒绝向非可信主机发送登录 Cookie: {SensitiveDataMasker.MaskUrl(url)}");
        // 登录轮询（扫码后轮询二维码状态）是 GET，幂等，可安全参与与 GetWebSourceCoreAsync
        // 一致的有界重试：此前零重试会让任一次瞬时 5xx/超时直接中止整个扫码登录流程，
        // 用户必须重新扫码。有界次数 min(MaxRetryCount,3) + 指数退避，见 GetWebSourceCoreAsync。
        int maxRetry = Math.Max(1, Math.Min(Config.Current.MaxRetryCount, 3));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                // 重定向逐跳校验：登录轮询携带操作者 Cookie，若自动跟随 3xx，被攻破的
                // passport 域名或开放重定向可把带凭据的请求与响应 Set-Cookie 凭证引向任意
                // 主机（B3-S1 纵深防御）。改用禁自动跳转客户端手动逐跳，每一跳的 Location
                // 在发起下一跳前必须通过 IsTrustedCookieHost，与 GetWebSourceAnonymousCheckedAsync 同构。
                string current = url;
                for (int hop = 0; hop < MaxRedirectHops; hop++)
                {
                    using var webRequest = new HttpRequestMessage(HttpMethod.Get, current);
                    webRequest.Headers.TryAddWithoutValidation("User-Agent", GetUserAgent(userAgent));
                    webRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
                    if (!string.IsNullOrEmpty(Config.Current.Cookie))
                        webRequest.Headers.TryAddWithoutValidation("Cookie", Config.Current.Cookie);
                    webRequest.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
                    webRequest.Headers.Connection.Clear();

                    Logger.LogDebug("获取网页内容: Url: {0}", SensitiveDataMasker.MaskUrl(current));
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Config.Current.ApiTimeoutMs));
                    using var webResponse = await NoRedirectClient.SendAsync(webRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                    // 登录接口同样参与服务器时钟校准（响应头 Date）：与其它 API 调用保持一致，
                    // 否则依赖签名时间戳的后续请求在校准缺失下可能因时钟偏差被拒。
                    // 用 fromVerifiedPool 区分：--insecure 连接的 Date 头由中间人可控，不写全局偏移
                    CalibrateClock(webResponse, fromVerifiedPool: !Config.Current.SkipSslCheck);
                    if ((int)webResponse.StatusCode is >= 300 and < 400)
                    {
                        var location = webResponse.Headers.Location;
                        if (location is null) break;
                        var next = location.IsAbsoluteUri ? location : new Uri(new Uri(current), location);
                        if (!IsTrustedCookieHost(next.ToString()))
                            throw new InvalidOperationException($"重定向目标未通过可信主机校验: {SensitiveDataMasker.MaskUrl(next.ToString())}");
                        current = next.ToString();
                        continue;
                    }
                    // 5xx 显式抛错走下方退避；4xx 交给 EnsureSuccessStatusCode 立即抛出（不重试）
                    if (!webResponse.IsSuccessStatusCode && (int)webResponse.StatusCode >= 500)
                        throw new HttpRequestException($"服务器返回 {(int)webResponse.StatusCode} {webResponse.ReasonPhrase}", null, webResponse.StatusCode);
                    webResponse.EnsureSuccessStatusCode();

                    string htmlCode = await webResponse.Content.ReadAsStringAsync(timeoutCts.Token);
                    // 截断实参含 `htmlCode[..1024]` 的子串分配：DebugLog 关闭时跳过求值，
                    // 避免为每次元数据响应（可达数 MB）白白分配 1KB 截断串
                    if (Config.Current.DebugLog)
                        Logger.LogDebug("Response: {0}", htmlCode.Length > 1024 ? htmlCode[..1024] + $"…[截断, 共 {htmlCode.Length} 字符]" : htmlCode);
                    List<string> setCookies = webResponse.Headers.TryGetValues("Set-Cookie", out var vals) ? vals.ToList() : [];
                    return (htmlCode, setCookies);
                }
                throw new HttpRequestException($"重定向跳数超过上限 ({MaxRedirectHops})");
            }
            catch (HttpRequestException ex) when (attempt < maxRetry && (ex.StatusCode is null || (int)ex.StatusCode >= 500))
            {
                int backoffMs = ExponentialBackoffMs(attempt);
                Logger.LogDebug("登录轮询失败(第{0}次重试, {1}ms后): {2}", attempt, backoffMs, SensitiveDataMasker.MaskUrl(url));
                await Task.Delay(backoffMs, token);
            }
            catch (OperationCanceledException) when (attempt < maxRetry && !token.IsCancellationRequested)
            {
                // 超时（timeoutCts 触发）：瞬时传输层故障，参与有界重试。
                // 真正的用户取消（token 已取消）不重试，直接向上传播。
                int backoffMs = ExponentialBackoffMs(attempt);
                Logger.LogDebug("登录轮询超时(第{0}次重试, {1}ms后): {2}", attempt, backoffMs, SensitiveDataMasker.MaskUrl(url));
                await Task.Delay(backoffMs, token);
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"登录轮询超时 ({Config.Current.ApiTimeoutMs}ms): {SensitiveDataMasker.MaskUrl(url)}", ex);
            }
        }
    }

    /// <summary>
    /// 匿名抓取网页内容：不携带登录 Cookie。仅用于解析阶段抓取尚未确认可信的
    /// 用户输入 URL（如 ResolveAsync 的泛抓取分支）——此时目标域名可能由攻击者控制，
    /// 附带操作者的 B 站凭据会把它外发到攻击者服务器（SSRF + 凭据泄露）。
    /// 已验证可信的 B 站 API 调用仍走 <see cref="GetWebSourceAsync"/>（携带 Cookie）。
    /// 目标可能是任意网页，默认不拒绝 HTML。
    /// </summary>
    public static async Task<string> GetWebSourceAnonymousAsync(string url, string? userAgent = null, CancellationToken token = default, bool rejectHtml = false)
        => await GetWebSourceCoreAsync(url, sendCookie: false, userAgent: userAgent, token: token, rejectHtml: rejectHtml);

    /// <summary>
    /// 逐跳校验重定向的匿名抓取：不携带 Cookie，且每一跳的 Location 都在发起下一跳
    /// 请求前交给 <paramref name="validateNextHop"/> 校验。与
    /// <see cref="GetWebSourceAnonymousAsync"/>（走自动跳转的共享客户端）不同，
    /// 初始域名可信不代表重定向目标可信——可信域名的开放重定向仍可把请求导向内网。
    /// </summary>
    public static async Task<string> GetWebSourceAnonymousCheckedAsync(string url,
        Func<Uri, bool> validateNextHop, string? userAgent = null, int maxHops = 10, CancellationToken token = default)
    {
        string current = url;
        for (int hop = 0; hop < maxHops; hop++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Config.Current.ApiTimeoutMs));
            using var webRequest = new HttpRequestMessage(HttpMethod.Get, current);
            webRequest.Headers.TryAddWithoutValidation("User-Agent", GetUserAgent(userAgent));
            webRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
            webRequest.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
            webRequest.Headers.Connection.Clear();

            using var response = await NoRedirectClient.SendAsync(webRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location is null) return current;
                var next = location.IsAbsoluteUri ? location : new Uri(new Uri(current), location);
                // 发起下一跳前校验目标：非可信主机 / 私网地址在此被拦截，不会真正访问
                if (!validateNextHop(next))
                {
                    Logger.LogWarn($"重定向目标未通过校验，已中止: {SensitiveDataMasker.MaskUrl(next.ToString())}");
                    return current;
                }
                current = next.ToString();
                continue;
            }
            string htmlCode = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (Config.Current.DebugLog)
                Logger.LogDebug("Response: {0}", htmlCode.Length > 1024 ? htmlCode[..1024] + $"…[截断, 共 {htmlCode.Length} 字符]" : htmlCode);
            return htmlCode;
        }
        return current;
    }

    /// <summary>
    /// 带 Cookie（登录凭据）外发前的目标主机白名单（B3-S1 纵深防御）。
    /// 仅允许向两类主机发送登录 Cookie：1) B 站官方域名（含子域）；2) 操作者经
    /// --host/--ep-host/--tv-host 显式配置的后台主机（BiliPlus/自建镜像站是操作者自选，
    /// 与 serve 侧的 IsOfficialHost 校验同源：显式配置即信任）。其余任何主机一律拒绝，
    /// 防未来调用方把用户可控 URL 传入 sendCookie:true 的外泄面。
    /// </summary>
    private static bool IsTrustedCookieHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.DnsSafeHost;
        // 回环/本机地址无条件放行：本地调试/测试服务器（HttpListener 假服务）不涉及
        // 跨网络凭据外泄，且 serve 的回调校验已在 BBDownApiServer 层单独收口。
        if (host == "localhost" || host == "127.0.0.1" || host == "::1")
            return true;
        // 官方域名（含子域）放行
        if (IsOfficialBilibiliHost(host))
            return true;
        // 操作者显式配置的后台主机放行（BiliPlus/镜像站；位于 URL 的 Host 字段映射）
        if (IsConfiguredHost(host, Config.Current.Host)
            || IsConfiguredHost(host, Config.Current.EpHost)
            || IsConfiguredHost(host, Config.Current.TvHost))
            return true;
        return false;
    }

    private static bool IsConfiguredHost(string host, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return false;
        if (!Uri.TryCreate("https://" + configured.Trim(), UriKind.Absolute, out var uri)) return false;
        return host.Equals(uri.DnsSafeHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// B 站官方及其关联业务域名后缀白名单（含子域）。
    /// 作为全项目（HTTP 凭据外发校验、UrlResolver 泛解析重定向校验、serve API host 净化等）的唯一定义源。
    /// </summary>
    public static readonly string[] OfficialHostSuffixes =
        { "bilibili.com", "b23.tv", "bilivideo.com", "hdslb.com", "biliapi.net", "biliapi.com", "bilibili.tv", "biliintl.com", "aisee.tv" };

    /// <summary>
    /// 检查指定主机名是否匹配 B 站官方域名白名单（精确匹配或为其子域）。
    /// </summary>
    public static bool IsOfficialBilibiliHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        return OfficialHostSuffixes.Any(s =>
            host.Equals(s, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> GetWebSourceCoreAsync(string url, bool sendCookie, string? userAgent, CancellationToken token, bool rejectHtml = false)
    {
        // API 层统一重试：仅对 5xx、传输层失败（HttpRequestException.StatusCode 为 null）
        // 与超时（用户 token 未取消的 OperationCanceledException，见下方 catch）按有界次数
        //（最多 3，min(MaxRetryCount,3)）指数退避重试。有界是刻意的：
        // 页面级已有 while(retryCount<maxRetry) 重试，若 API 层也用全量 MaxRetryCount 会
        // 乘积放大请求数与总等待；且总退避 ~9s 远小于 WBI 签名 wts 的时效窗口，重试不会
        // 因签名过期被误判。4xx 是风控/参数/鉴权错误，重试只会加重风控状态，直接抛出。
        // 风控页（200+HTML）由 rejectHtml 抛 RiskControlResponseException，不重试。
        int maxRetry = Math.Max(1, Math.Min(Config.Current.MaxRetryCount, 3));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var webRequest = new HttpRequestMessage(HttpMethod.Get, url);
                var effectiveUa = GetUserAgent(userAgent);
                webRequest.Headers.TryAddWithoutValidation("User-Agent", effectiveUa);
                webRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
                // B3-S1 纵深防御：带 Cookie 的请求必须先确认目标在可信 B 站域白名单内。
                // 否则一旦未来某调用方把用户可控 URL 传入本方法，登录凭据（SESSDATA）会
                // 随 HttpClient 直接外发（凭据泄露）。当前全部 sendCookie:true 调用方都传
                // B 站 API/CDN 域名（受信任源），此校验是防回退闸而非改动现有行为。
                if (sendCookie && !IsTrustedCookieHost(url))
                    throw new InvalidOperationException($"拒绝向非可信主机发送登录 Cookie: {SensitiveDataMasker.MaskUrl(url)}");
                if (sendCookie)
                    webRequest.Headers.TryAddWithoutValidation("Cookie", (url.Contains("/ep") || url.Contains("/ss")) ? Config.Current.Cookie + ";CURRENT_FNVAL=4048;" : Config.Current.Cookie);
                if (url.Contains("api.bilibili.com"))
                    webRequest.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
                if (url.Contains("api.bilibili.tv"))
                {
                    // sec-ch-ua 与 UA 自洽：只有 Chrome UA 才发送（真实 Firefox 不发送 Chrome 品牌
                    // sec-ch-ua），版本取自已解析的 UA——避免"UA 145 + sec-ch-ua 131"这类可被识别
                    // 的指纹不一致（此前硬编码 131，与升级后的 UA 池同样不匹配）。
                    var chromeVersion = ChromeVersionRegex().Match(effectiveUa).Groups[1].Value;
                    if (chromeVersion.Length > 0)
                        webRequest.Headers.TryAddWithoutValidation("sec-ch-ua",
                            $"\"Google Chrome\";v=\"{chromeVersion}\", \"Chromium\";v=\"{chromeVersion}\", \"Not_A Brand\";v=\"99\"");
                }
                webRequest.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
                webRequest.Headers.Connection.Clear();

                Logger.LogDebug("获取网页内容: Url: {0}, Headers: {1}",
                    SensitiveDataMasker.MaskUrl(url), SensitiveDataMasker.MaskHeaders(webRequest.Headers));
                // ResponseHeadersRead 之后 HttpClient.Timeout 不再约束响应体读取（实测，见
                // StreamingHttpClient 注释），用 CancelAfter 重建整体超时（默认 2 分钟）。
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Config.Current.ApiTimeoutMs));
                using var webResponse = await AppHttpClient.SendAsync(webRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                // 服务器时钟校准：在所有状态码分支前执行（错误响应也带 Date，见 CalibrateClock）
                CalibrateClock(webResponse, fromVerifiedPool: !Config.Current.SkipSslCheck);
                // 5xx：显式抛 HttpRequestException（带状态码）走下方退避；
                // 4xx 交给 EnsureSuccessStatusCode 立即抛出（HttpRequestException.StatusCode < 500，
                // 不满足重试过滤条件，不会被重试）。
                if (!webResponse.IsSuccessStatusCode && (int)webResponse.StatusCode >= 500)
                    throw new HttpRequestException($"服务器返回 {(int)webResponse.StatusCode} {webResponse.ReasonPhrase}", null, webResponse.StatusCode);
                webResponse.EnsureSuccessStatusCode();

                string htmlCode = await webResponse.Content.ReadAsStringAsync(timeoutCts.Token);
                // 200+HTML 风控页识别：JSON 响应不可能以 '<' 开头，因此这一定是 HTML 页面。
                // 给出可读的"疑似风控页"异常，替代下游 JsonDocument.Parse 的裸 JsonException
                //（难定位、看似偶发）。TrimStart 处理前导空白——部分 WAF/风控页在 '<html' 前
                // 带换行或空白，直接 StartsWith('<') 会漏检并让裸 JsonException 回潮；并防御性
                // 剥离 UTF-8 BOM（char.IsWhiteSpace 不认 U+FEFF，ReadAsStringAsync 通常已剥离，
                // 此处兜底）。用 AsSpan 避免为每条响应分配截断串。此为业务性拦截，不参与上面的
                // 5xx 重试。
                if (rejectHtml)
                {
                    // 200+HTML 风控页识别：JSON 响应不可能以 '<' 开头，因此这一定是 HTML 页面。
                    // 给出可读的"疑似风控页"异常，替代下游 JsonDocument.Parse 的裸 JsonException
                    //（难定位、看似偶发）。先剥前导空白，再剥 BOM，再剥一次空白：
                    // char.IsWhiteSpace 不认 U+FEFF，部分 WAF/风控页在 BOM 前还带换行/空白
                    //（"\n\uFEFF<html"），只剥 BOM 或只剥空白都会漏检而让裸 JsonException 回潮。
                    // 用 AsSpan 避免为每条响应分配截断串。此为业务性拦截，不参与上面的 5xx 重试。
                    var htmlSpan = htmlCode.AsSpan().TrimStart().TrimStart('\uFEFF').TrimStart();
                    if (htmlSpan.StartsWith("<"))
                        throw new RiskControlResponseException(url);
                }
                // 响应体可达数 MB（如 intl 回退抓取的整张 HTML 页面），翻页类 fetcher 会放大几十倍，
                // 全部落盘会把日志文件灌满；截断到前 1KB 即可排查问题。
                // 截断实参含子串分配，DebugLog 关闭时跳过求值（见 GetWebSourceWithSetCookiesAsync）。
                if (Config.Current.DebugLog)
                    Logger.LogDebug("Response: {0}", htmlCode.Length > 1024 ? htmlCode[..1024] + $"…[截断, 共 {htmlCode.Length} 字符]" : htmlCode);
                return htmlCode;
            }
            catch (HttpRequestException ex) when (attempt < maxRetry && (ex.StatusCode is null || (int)ex.StatusCode >= 500))
            {
                int backoffMs = ExponentialBackoffMs(attempt);
                Logger.LogDebug("API 请求失败(第{0}次重试, {1}ms后): {2}", attempt, backoffMs, SensitiveDataMasker.MaskUrl(url));
                await Task.Delay(backoffMs, token);
            }
            catch (OperationCanceledException) when (attempt < maxRetry && !token.IsCancellationRequested)
            {
                // HttpClient.Timeout / 响应体读取超时抛的 TaskCanceledException 其用户 token 未取消：
                // 超时是最常见的瞬时传输层故障，与 5xx 同权参与有界重试。
                // 真正的用户取消（token 已取消）不重试，直接向上传播。
                int backoffMs = ExponentialBackoffMs(attempt);
                Logger.LogDebug("API 请求超时(第{0}次重试, {1}ms后): {2}", attempt, backoffMs, SensitiveDataMasker.MaskUrl(url));
                await Task.Delay(backoffMs, token);
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            {
                // 重试预算耗尽后的超时异常：如果不转换，抛出的 OCE 携带已取消的 timeoutCts token，
                // 会被 CLI 顶层 (Program.cs / SetExceptionHandler) 误识别为用户主动取消 (Ctrl+C / 退出码 130)。
                // 此时向外部抛出真实的 TimeoutException，准确表达超时故障。
                throw new TimeoutException($"API 请求超时 ({Config.Current.ApiTimeoutMs}ms): {SensitiveDataMasker.MaskUrl(url)}", ex);
            }
        }
    }

    /// <summary>指数退避毫秒数：RetryDelayMs(封顶 3s) × 2^(attempt-1)，单次封顶 12s。
    /// API 层重试是有界的快速重试：总尝试数 clamp 到最多 3（见 GetWebSourceCoreAsync），
    /// 总等待默认 ~9s，远小于 WBI 签名 wts 的时效窗口（~60s），也不会与页面级重试叠加成
    /// 失控的总时长（--retry-count 可到 100，若每层都用全量配置会乘积放大）。</summary>
    private static int ExponentialBackoffMs(int attempt)
    {
        long baseMs = Math.Min(Config.Current.RetryDelayMs, 3000);
        long raw = baseMs * (1L << Math.Min(attempt - 1, 10));
        return (int)Math.Min(raw, 12_000);
    }

    // 重写重定向处理, 自动跟随多次重定向。
    /// <summary>
    /// 跟随重定向到最终地址（使用启用自动跳转的共享客户端，无逐跳校验、无跳数上限）。
    /// <b>仅供硬编码的可信 URL（当前唯一调用方：Github releases/latest 更新检查）使用</b>——
    /// 不得把用户输入/不可信 URL 传入本方法：它会在访问前就跟随任何中间重定向，
    /// 未做主机校验，若复用到不可信输入会形成开放重定向/SSRF 盲发请求面（B3-L1）。
    /// 不可信地址请用 <see cref="GetWebLocationCheckedAsync"/>（逐跳校验）。
    /// </summary>
    public static async Task<string> GetWebLocationAsync(string url, CancellationToken token = default)
    {
        // 先尝试 HEAD，部分服务器不支持则 fallback 到 GET
        foreach (var method in new[] { HttpMethod.Head, HttpMethod.Get })
        {
            try
            {
                using var webRequest = new HttpRequestMessage(method, url);
                webRequest.Headers.TryAddWithoutValidation("User-Agent", GetUserAgent(null));
                webRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
                webRequest.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
                webRequest.Headers.Connection.Clear();

                Logger.LogDebug("获取网页重定向地址(method={1}): Url: {0}", SensitiveDataMasker.MaskUrl(url), method);
                using var webResponse = await AppHttpClient.SendAsync(webRequest, HttpCompletionOption.ResponseHeadersRead, token);
                webResponse.EnsureSuccessStatusCode();
                string location = webResponse.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
                Logger.LogDebug("Location: {0}", SensitiveDataMasker.MaskUrl(location));
                return location;
            }
            catch (HttpRequestException) when (method == HttpMethod.Head)
            {
                // HEAD 不被支持，回退到 GET
                Logger.LogDebug("HEAD 请求失败，尝试 GET");
            }
        }
        return url; // fallback: return original URL
    }

    /// <summary>
    /// 逐跳校验重定向的地址解析。共享 <see cref="AppHttpClient"/> 启用了自动跳转
    /// （AllowAutoRedirect=true），请求会先跟随到最终主机才返回——若可信域名存在开放
    /// 重定向，内部地址或非可信目标仍会被先访问到。此方法用禁用自动跳转的专用客户端
    /// 手动逐跳跟随：每一跳的 Location 都在**发起下一跳请求之前**交给
    /// <paramref name="validateNextHop"/> 校验，拒绝即中止（不访问非可信/私网目标）。
    /// 最多跟随 <paramref name="maxHops"/> 次，防重定向环无限跳转。
    /// 每跳先 HEAD，不支持 HEAD 的服务器（405/HttpRequestException）回退到 GET 请求同一 URL。
    /// 返回最终 URL；未被重定向时返回原 URL。
    /// </summary>
    public static async Task<string> GetWebLocationCheckedAsync(string url,
        Func<Uri, bool> validateNextHop, int maxHops = 10, CancellationToken token = default)
    {
        // 复用共享的禁自动跳转客户端：每次跳转新建 HttpClient 会重复建连接池
        string current = url;
        for (int hop = 0; hop < maxHops; hop++)
        {
            var method = HttpMethod.Head;
            bool retryAsGet = false;
            try
            {
                using var webRequest = new HttpRequestMessage(method, current);
                webRequest.Headers.TryAddWithoutValidation("User-Agent", GetUserAgent(null));
                webRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
                webRequest.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
                webRequest.Headers.Connection.Clear();

                using var response = await NoRedirectClient.SendAsync(webRequest, HttpCompletionOption.ResponseHeadersRead, token);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    if (location is null)
                    {
                        // 3xx 无 Location：无法继续，按原地址返回
                        return current;
                    }
                    var next = location.IsAbsoluteUri ? location : new Uri(new Uri(current), location);
                    // 发起下一跳前校验目标：非可信主机 / 私网地址在此被拦截，不会真正访问
                    if (!validateNextHop(next))
                    {
                        Logger.LogWarn($"重定向目标未通过校验，已中止: {SensitiveDataMasker.MaskUrl(next.ToString())}");
                        return current;
                    }
                    current = next.ToString();
                    continue;
                }
                if (response.IsSuccessStatusCode)
                {
                    return current;
                }
                // 非 3xx 且非成功（如 HEAD 返回 405/404）：部分服务器不支持 HEAD，回退 GET
                retryAsGet = true;
            }
            catch (HttpRequestException) when (method == HttpMethod.Head)
            {
                // HEAD 不被支持，回退到 GET
                retryAsGet = true;
            }

            if (retryAsGet)
            {
                // GET 兜底有界重试：HEAD 不被支持/失败后回退 GET，网络瞬断时若只发一次会
                // 误报"短链解析失败（目标可能已失效）"。重试一次（指数退避）并记录 Warn——
                // 此前 catch { return current; } 无任何日志，瞬断被静默当成内容性失败。
                var getResult = await GetOnceAsync(current);
                if (getResult.Next is not null)
                {
                    current = getResult.Next;
                    continue;
                }
                return getResult.Current;
            }
            return current;
        }
        // 到达跳数上限仍未结束：返回当前地址（调用方据此判断）
        return current;

        // 局部函数：GET 一次指定地址。返回 { Current = 未重定向的最终地址（含成功/校验中止/无 Location），
        //  Next = 有待跟随的下一跳 }。网络失败抛 HttpRequestException。
        async Task<(string Current, string? Next)> GetOnceAsync(string target)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var getRequest = new HttpRequestMessage(HttpMethod.Get, target);
                    getRequest.Headers.TryAddWithoutValidation("User-Agent", GetUserAgent(null));
                    getRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
                    getRequest.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
                    getRequest.Headers.Connection.Clear();

                    using var getResponse = await NoRedirectClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, token);
                    if ((int)getResponse.StatusCode is >= 300 and < 400)
                    {
                        var location = getResponse.Headers.Location;
                        if (location is null)
                        {
                            return (target, null);
                        }
                        var next = location.IsAbsoluteUri ? location : new Uri(new Uri(target), location);
                        if (!validateNextHop(next))
                        {
                            Logger.LogWarn($"重定向目标未通过校验，已中止: {SensitiveDataMasker.MaskUrl(next.ToString())}");
                            return (target, null);
                        }
                        return (target, next.ToString());
                    }
                    return (target, null);
                }
                catch (HttpRequestException)
                {
                    if (attempt == 1)
                    {
                        Logger.LogWarn($"重定向 GET 请求失败，{RedirectGetRetryDelayMs}ms 后重试一次: {SensitiveDataMasker.MaskUrl(target)}");
                        await Task.Delay(RedirectGetRetryDelayMs, token);
                        continue;
                    }
                    Logger.LogWarn($"重定向 GET 请求重试仍失败: {SensitiveDataMasker.MaskUrl(target)}");
                    throw;
                }
            }
        }
    }

    /// <summary>短链 GET 兜底重试的退避常量（与 API 层退避基数一致）。</summary>
    private const int RedirectGetRetryDelayMs = 500;

    public static async Task<byte[]> GetPostResponseAsync(string Url, byte[] postData, Dictionary<string, string>? headers = null, CancellationToken token = default)
    {
        // 与 GetWebSourceCoreAsync 相同的 API 层统一重试：仅对 5xx、传输层失败
        //（HttpRequestException.StatusCode 为 null）与超时（用户 token 未取消的
        // OperationCanceledException）按有界次数（最多 3）指数退避重试。
        // grpc 帧首字节是压缩标志（0/1），不可能为 '<'；若首字节是 '<'，说明拿到的是
        // HTML 错误/风控页，抛 RiskControlResponseException 替代下游反序列化的乱码错误。
        int maxRetry = Math.Max(1, Math.Min(Config.Current.MaxRetryCount, 3));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                // postData 是 grpc 请求体，可能携带 access_key，只记录长度而非内容
                Logger.LogDebug("Post to: {0}, data: {1} bytes", SensitiveDataMasker.MaskUrl(Url), postData.Length);

                using ByteArrayContent content = new(postData);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/grpc");

                using HttpRequestMessage request = new()
                {
                    RequestUri = new Uri(Url),
                    Method = HttpMethod.Post,
                    Content = content,
                    //Version = HttpVersion.Version20
                };

                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> header in headers)
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation("User-Agent", "Dalvik/2.1.0 (Linux; U; Android 6.0.1; oneplus a5010 Build/V417IR) 6.10.0 os/android model/oneplus a5010 mobi_app/android build/6100500 channel/bili innerVer/6100500 osVer/6.0.1 network/2");
                    request.Headers.TryAddWithoutValidation("grpc-encoding", "gzip");
                }

                // 不检查状态码会把 4xx/5xx 的错误页当成 grpc 响应体返回，
                // 下游只能报出难以定位的反序列化错误。
                // ResponseHeadersRead：先拿到状态码再单独 await 响应体，使 EnsureSuccessStatusCode
                // 不必等整包缓冲完就能抛错，且响应体读取阶段仍受调用方 token 控制（可取消）。
                // 注意：ResponseHeadersRead 会让 HttpClient.Timeout 不再约束响应体读取（实测见
                // StreamingHttpClient 注释），服务端发完响应头后响应体停滞会无限挂起；因此这里
                // 用 CancelAfter 重建与 HttpClient.Timeout 等价的整体超时（ApiTimeoutMs，默认 2 分钟）。
                // 先把 response 绑定到 using 再检查状态码：若在初始化表达式内抛错，response 未绑定、
                // using 不会 Dispose，连接会被未消费的响应体占用无法归还连接池；绑定后再
                // EnsureSuccessStatusCode，抛错时 using 会 Dispose → SocketsHttpHandler 自动排空
                // 小响应体归还连接。
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Config.Current.ApiTimeoutMs));
                // B3-F2：凭据最重的 gRPC POST 禁用自动跟随重定向。
                // AppHttpClient（AllowAutoRedirect=true）会在 307/308 上连同 body 与
                // authorization/x-bili-metadata-bin（含 access_token）重放到跨主机目标——
                // gRPC 端点本不应重定向，跟随只会给凭据外泄留面。用 NoRedirectClient：
                // 3xx 在此显式拦下报错，不跟随、不外发凭据（与 GET 逐跳校验同思想）。
                using var response = await NoRedirectClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                // 3xx：gRPC 端点不应重定向，显式拦截（不允许凭据/body 随 307/308 重放）
                if ((int)response.StatusCode is >= 300 and < 400)
                    throw new HttpRequestException($"gRPC 端点返回意外重定向 (HTTP {(int)response.StatusCode})，已拒绝跟随以避免凭据外发", null, response.StatusCode);
                // 服务器时钟校准：在所有状态码分支前执行（错误响应也带 Date，见 CalibrateClock）
                CalibrateClock(response, fromVerifiedPool: !Config.Current.SkipSslCheck);
                if (!response.IsSuccessStatusCode && (int)response.StatusCode >= 500)
                    throw new HttpRequestException($"服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
                response.EnsureSuccessStatusCode();
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token);
                // HTML 风控/错误页首字节是 '<'（0x3C），不可能是合法 grpc 帧头 → 明确报错
                if (bytes.Length > 0 && bytes[0] == (byte)'<')
                    throw new RiskControlResponseException(Url);
                return bytes;
            }
            catch (HttpRequestException ex) when (attempt < maxRetry && (ex.StatusCode is null || (int)ex.StatusCode >= 500))
            {
                // 本函数仅服务幂等查询（PlayView gRPC 播放地址、字幕 gRPC）：重发不改变
                // 服务端状态，连接级失败（StatusCode null）与 5xx 均可安全有界重试。
                // 真正的非幂等请求（Widevine 许可证）走 VerifiedNoRedirectClient 直连
                // （WidevineCdm.cs），不经过本函数的重试逻辑。
                int backoffMs = ExponentialBackoffMs(attempt);
                Logger.LogDebug("API POST 失败(第{0}次重试, {1}ms后): {2}", attempt, backoffMs, SensitiveDataMasker.MaskUrl(Url));
                await Task.Delay(backoffMs, token);
            }
            catch (OperationCanceledException) when (attempt < maxRetry && !token.IsCancellationRequested)
            {
                // timeoutCts 超时抛的 OperationCanceledException 其用户 token 未取消：
                // 超时是最常见的瞬时传输层故障，与 5xx 同权参与有界重试（幂等查询）。
                // 真正的用户取消（token 已取消）不重试，直接向上传播。
                int backoffMs = ExponentialBackoffMs(attempt);
                Logger.LogDebug("API POST 超时(第{0}次重试, {1}ms后): {2}", attempt, backoffMs, SensitiveDataMasker.MaskUrl(Url));
                await Task.Delay(backoffMs, token);
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            {
                // 所有重试用尽后仍超时（timeoutCts 触发）：以可读超时错误抛出
                // （token 已取消的真实取消不进入此处，直接向上传播）。
                throw new TimeoutException($"API POST 超时 ({Config.Current.ApiTimeoutMs}ms): {SensitiveDataMasker.MaskUrl(Url)}", ex);
            }
        }
    }
}
