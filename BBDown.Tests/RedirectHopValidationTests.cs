using System.Net;
using BBDown.Core;
using BBDown.Core.Util;

namespace BBDown.Tests;
/// <summary>
/// 逐跳重定向校验测试：验证 <see cref="HTTPUtil.GetWebLocationCheckedAsync"/> 在
/// 每一跳的 Location 被请求之前就用回调校验，非可信目标会被拦截（不会真正访问）。
/// </summary>
public class RedirectHopValidationTests
{
    /// <summary>起一个本地 HTTP 服务，按路径返回重定向或终态。Dispose 时停掉。</summary>
    private sealed class LocalRedirectServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public int Port { get; }

        // 服务端实际处理的请求总数（含初始请求与每一跳）：G9 用它对重定向环断言
        // “请求数 ≤ maxHops+1”，比只断言终值 ∈ {/a,/b} 强（后者无法证明环被截断）。
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        public LocalRedirectServer(
            Dictionary<string, (int Status, string Location)> routes,
            int terminalStatus = 200)
        {
            // 动态分配空闲回环端口：固定端口段（24000-26000）在 CI 并行/端口冲突时
            // HttpListener.Start 直接失败（G6），与其余测试类一致用 TestPort.Allocate()。
            var port = TestPort.Allocate();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Port = port;
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var ctx = await _listener.GetContextAsync();
                        try
                        {
                            Interlocked.Increment(ref _requestCount);
                            var path = ctx.Request.Url!.AbsolutePath;
                            if (routes.TryGetValue(path, out var route))
                            {
                                ctx.Response.StatusCode = route.Status;
                                ctx.Response.RedirectLocation = route.Location;
                            }
                            else
                            {
                                ctx.Response.StatusCode = terminalStatus;
                            }
                            ctx.Response.Close();
                        }
                        catch
                        {
                            // 客户端中止连接等：忽略
                        }
                    }
                }
                catch (HttpListenerException)
                {
                    // 服务停止
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task GetWebLocationCheckedAsync_UntrustedRedirect_StopsBeforeNextHop()
    {
        // 攻击链：可信入口 → 302 指向非可信主机。校验回调必须拦截，绝不访问 evil.com。
        using var server = new LocalRedirectServer(new()
        {
            { "/entry", (302, "http://evil.example.com/final") },
        });
        var baseUrl = $"http://127.0.0.1:{server.Port}";
        var result = await HTTPUtil.GetWebLocationCheckedAsync($"{baseUrl}/entry",
            uri => uri.Host == "127.0.0.1", token: CancellationToken.None);

        // 非可信跳转被拦截：返回原入口地址，而不是跟随到 evil.com
        Assert.Equal($"{baseUrl}/entry", result);
    }

    [Fact]
    public async Task GetWebLocationCheckedAsync_TrustedRedirect_FollowsToFinal()
    {
        // 可信入口 → 302 → 可信终点：应跟随到终点
        using var server = new LocalRedirectServer(new()
        {
            { "/entry", (302, "/final") },
        });
        var baseUrl = $"http://127.0.0.1:{server.Port}";
        var result = await HTTPUtil.GetWebLocationCheckedAsync($"{baseUrl}/entry",
            uri => uri.Host == "127.0.0.1", token: CancellationToken.None);

        Assert.Equal($"{baseUrl}/final", result);
    }

    [Fact]
    public async Task GetWebLocationCheckedAsync_RedirectChain_LimitedHops()
    {
        // 无限重定向环：必须被 maxHops 上限截断，而非无限跟随
        using var server = new LocalRedirectServer(new()
        {
            { "/a", (302, "/b") },
            { "/b", (302, "/a") },
        });
        var baseUrl = $"http://127.0.0.1:{server.Port}";
        var result = await HTTPUtil.GetWebLocationCheckedAsync($"{baseUrl}/a",
            uri => uri.Host == "127.0.0.1", maxHops: 5, token: CancellationToken.None);

        // 到达跳数上限后返回（不悬挂），结果落在环中的某一跳
        Assert.Contains(result, new[] { $"{baseUrl}/a", $"{baseUrl}/b" });
        // G9：服务端计数断言——环必须被 maxHops 截断，总请求数 ≤ 初始 1 次 + maxHops 跳。
        // 仅断言终值 ∈ {/a,/b} 无法区分“截断后返回”与“侥幸返回”，计数是唯一强证据。
        Assert.True(server.RequestCount <= 5 + 1,
            $"重定向环应被 maxHops=5 截断（请求数 ≤ 6），实际请求 {server.RequestCount} 次");
    }

    [Fact]
    public async Task GetWebLocationCheckedAsync_HeadRejected_FallsBackToGet()
    {
        // 回归修复：此前逐跳解析只发 HEAD，遇到不支持 HEAD 的服务器（405）直接放弃，
        // 导致 av 视频链接解析失败。必须回退到 GET 请求同一 URL。
        using var server = new LocalHeadRejectingServer(200);
        var baseUrl = $"http://127.0.0.1:{server.Port}";
        var result = await HTTPUtil.GetWebLocationCheckedAsync($"{baseUrl}/ok",
            uri => uri.Host == "127.0.0.1", token: CancellationToken.None);

        // GET 返回 200：成功解析，返回原 URL（未被重定向）
        Assert.Equal($"{baseUrl}/ok", result);
    }

    /// <summary>对 HEAD 一律返回 405、GET 返回指定状态码的本地服务（模拟不支持 HEAD 的服务器）。</summary>
    private sealed class LocalHeadRejectingServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly int _getStatus;
        private readonly Task _loop;
        public int Port { get; }

        public LocalHeadRejectingServer(int getStatus)
        {
            _getStatus = getStatus;
            // G6：固定端口段改动态分配（同 LocalRedirectServer）
            Port = TestPort.Allocate();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var ctx = await _listener.GetContextAsync();
                        try
                        {
                            if (ctx.Request.HttpMethod == "HEAD")
                            {
                                ctx.Response.StatusCode = 405; // Method Not Allowed
                            }
                            else
                            {
                                ctx.Response.StatusCode = _getStatus;
                            }
                            ctx.Response.Close();
                        }
                        catch { }
                    }
                }
                catch (HttpListenerException) { }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task GetWebSourceWithSetCookiesAsync_UntrustedRedirect_ThrowsBeforeNextHop()
    {
        // 登录轮询携带操作者 Cookie（且响应 Set-Cookie 是新凭证下发通道）：若自动跟随
        // 3xx，被攻破的入口或开放重定向可把带凭据的请求与响应引向任意主机。逐跳校验
        // 必须在发起下一跳前拦截非可信 Location，绝不访问 evil.com。
        using var server = new LocalRedirectServer(new()
        {
            { "/entry", (302, "http://evil.example.com/final") },
        });
        var baseUrl = $"http://127.0.0.1:{server.Port}";
        var original = Config.Current;
        try
        {
            Config.ApplyToCurrentAsyncFlow(original with { Cookie = "SESSDATA=secret" });
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HTTPUtil.GetWebSourceWithSetCookiesAsync($"{baseUrl}/entry", token: CancellationToken.None));
            Assert.Contains("可信主机", ex.Message);
            // 校验发生在发起下一跳网络请求之前：evil.com 绝不被访问（服务端只收到入口 1 次）
            Assert.Equal(1, server.RequestCount);
        }
        finally
        {
            Config.ApplyToCurrentAsyncFlow(original);
        }
    }

    [Fact]
    public async Task GetWebSourceWithSetCookiesAsync_TrustedRedirect_FollowsAndReturnsBody()
    {
        // 同一可信主机内的重定向（相对 Location 落在回环服务上）应正常跟随并返回
        // 终态 body：逐跳校验只拦截非可信目标，不破坏正常登录轮询链路。
        using var server = new LocalRedirectServer(new()
        {
            { "/entry", (302, "/final") },
        });
        var baseUrl = $"http://127.0.0.1:{server.Port}";
        var original = Config.Current;
        try
        {
            Config.ApplyToCurrentAsyncFlow(original with { Cookie = "SESSDATA=secret" });
            var (body, _) = await HTTPUtil.GetWebSourceWithSetCookiesAsync($"{baseUrl}/entry", token: CancellationToken.None);
            Assert.NotNull(body);
            // 入口 1 次 + 跟随 1 次
            Assert.Equal(2, server.RequestCount);
        }
        finally
        {
            Config.ApplyToCurrentAsyncFlow(original);
        }
    }
}
