using System.Net;
using BBDown.Core;
using BBDown.Core.Util;

namespace BBDown.Tests;

/// <summary>
/// RF-4：Widevine 许可证请求的传输通道。VerifiedNoRedirectClient 必须同时满足
/// ① 始终校验证书（不随 --insecure 降级——响应携带内容密钥）；② 禁自动跟随重定向
/// （请求体是设备私钥签名的 challenge，307/308 连同 body 重放即签名外发）。
/// </summary>
public class VerifiedNoRedirectClientTests
{
    [Fact]
    public void VerifiedNoRedirectClient_AlwaysVerified_RegardlessOfFlowConfig()
    {
        var original = Config.Current.SkipSslCheck;
        try
        {
            // 即使当前流跳过了校验，VerifiedNoRedirectClient 仍指向始终校验的池
            //（不能由用户选项降级，同 VerifiedAppHttpClient 的安全前提）
            Config.ApplyToCurrentAsyncFlow(Config.Current with { SkipSslCheck = true });
            var verified = HTTPUtil.VerifiedNoRedirectClient;
            Config.ApplyToCurrentAsyncFlow(Config.Current with { SkipSslCheck = false });
            Assert.Same(verified, HTTPUtil.VerifiedNoRedirectClient);
        }
        finally
        {
            Config.ApplyToCurrentAsyncFlow(Config.Current with { SkipSslCheck = original });
        }
    }

    [Fact]
    public async Task VerifiedNoRedirectClient_DoesNotFollowRedirect_WhileAutoRedirectClientDoes()
    {
        using var server = new SingleRedirectServer();
        var url = $"http://127.0.0.1:{server.Port}/start";

        // 禁自动跳转：3xx 作为响应直接返回，Location 指向的目标不会被请求
        using (var resp = await HTTPUtil.VerifiedNoRedirectClient.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.TemporaryRedirect, resp.StatusCode);
        }
        Assert.Equal(1, server.RequestCount);

        // 对照组：自动跳转客户端（VerifiedAppHttpClient）会跟随到终态——
        // 正是 RF-4 要在许可证链路上排除的行为。
        // 计数包含上一阶段的 1 次：/start（307）+ /final（200）共再增 2 次，总计 3
        using (var resp = await HTTPUtil.VerifiedAppHttpClient.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        Assert.Equal(3, server.RequestCount);
    }

    [Fact]
    public async Task VerifiedNoRedirectClient_PostWithBody_RedirectIsReturnedNotFollowed()
    {
        // 镜像许可证请求形态（POST + 签名 body）：307 不被跟随，body 只到达服务端一次，
        // 由调用方拿到 3xx 显式处置（WidevineCdm 拦截报错），不存在重放外发
        using var server = new SingleRedirectServer();
        var url = $"http://127.0.0.1:{server.Port}/start";

        using var content = new StringContent("signed-challenge-body");
        using var resp = await HTTPUtil.VerifiedNoRedirectClient.PostAsync(url, content);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, resp.StatusCode);
        Assert.Equal(1, server.RequestCount);
    }

    /// <summary>起一个本地 HTTP 服务：/start 返回 307 重定向到 /final（终态 200），统计实际收到的请求数。</summary>
    private sealed class SingleRedirectServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public int Port { get; }

        // 服务端实际处理的请求总数：区分"3xx 被直接返回"（1 次）与"被跟随"（≥2 次）
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        public SingleRedirectServer()
        {
            // 动态分配空闲回环端口：固定端口段在 CI 并行/端口冲突时 HttpListener.Start 直接失败
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
                            if (ctx.Request.Url!.AbsolutePath == "/final")
                            {
                                ctx.Response.StatusCode = 200;
                            }
                            else
                            {
                                ctx.Response.StatusCode = 307;
                                ctx.Response.RedirectLocation = "/final";
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
}
