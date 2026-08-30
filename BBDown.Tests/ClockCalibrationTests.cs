using System.Net;
using BBDown;
using BBDown.Core;
using BBDown.Core.Util;

namespace BBDown.Tests;

/// <summary>
/// 1.1：WBI 签名依赖本地系统时钟，时钟偏差超 ~60s 时效窗口会被 B 站拒绝。
/// 修复：HTTPUtil.CalibrateClock 从响应头 Date 校准偏移写入 Config，
/// 签名时间戳（GetTimeStamp/ServerClock）经偏移补偿。
/// </summary>
public class ClockCalibrationTests
{
    [Fact]
    public void CalibrateClock_ReadsDateHeader_WritesOffset()
    {
        var original = Config.Current;
        try
        {
            Config.Apply(Config.Current with { ServerClockOffsetSeconds = 0 });
            using var response = new HttpResponseMessage();
            var serverDate = DateTimeOffset.UtcNow.AddMinutes(5);
            response.Headers.Date = serverDate;

            // expected 基准必须在调用前取（Info 级观察）：若在 CalibrateClock 之后计算，
            // CI 卡顿 >3s 会把执行延迟算进容差造成假失败
            long expected = (long)Math.Round((serverDate - DateTimeOffset.UtcNow).TotalSeconds);

            HTTPUtil.CalibrateClock(response);

            Assert.InRange(Config.Current.ServerClockOffsetSeconds, expected - 3, expected + 3);
        }
        finally { Config.Apply(original); }
    }

    [Fact]
    public void CalibrateClock_MissingDate_DoesNotWrite()
    {
        var original = Config.Current;
        try
        {
            Config.Apply(Config.Current with { ServerClockOffsetSeconds = 0 });
            using var response = new HttpResponseMessage(); // 无 Date 头

            HTTPUtil.CalibrateClock(response);

            Assert.Equal(0, Config.Current.ServerClockOffsetSeconds);
        }
        finally { Config.Apply(original); }
    }

    [Fact]
    public void CalibrateClock_MalformedDate_Beyond1h_Rejected()
    {
        var original = Config.Current;
        try
        {
            Config.Apply(Config.Current with { ServerClockOffsetSeconds = 0 });
            using var response = new HttpResponseMessage();
            response.Headers.Date = DateTimeOffset.UtcNow.AddHours(3); // 超过 ±1h clamp

            HTTPUtil.CalibrateClock(response);

            Assert.Equal(0, Config.Current.ServerClockOffsetSeconds);
        }
        finally { Config.Apply(original); }
    }

    [Fact]
    public void CalibrateClock_ZeroOffset_DoesNotRewrite()
    {
        var original = Config.Current;
        try
        {
            Config.Apply(Config.Current with { ServerClockOffsetSeconds = 0 });
            using var response = new HttpResponseMessage();
            response.Headers.Date = DateTimeOffset.UtcNow.AddSeconds(2); // 几乎零偏差

            HTTPUtil.CalibrateClock(response);

            // 秒级偏差可能被截断成 0 或 ±1：只断言没有写入明显异常值
            Assert.InRange(Config.Current.ServerClockOffsetSeconds, -3, 3);
        }
        finally { Config.Apply(original); }
    }

    [Fact]
    public void CalibrateClock_FromInsecurePool_DoesNotWriteGlobalOffset()
    {
        // B3-L2：不安全池（--insecure）下 Date 头可被中间人伪造，若写入进程级全局偏移
        // 会扰动同一进程内其它已校验流的 WBI 签名基准。必须忽略不安全池的校准。
        var original = Config.Current;
        try
        {
            Config.Apply(Config.Current with { ServerClockOffsetSeconds = 0 });
            using var response = new HttpResponseMessage();
            response.Headers.Date = DateTimeOffset.UtcNow.AddMinutes(30); // 中间人伪造的大偏移

            HTTPUtil.CalibrateClock(response, fromVerifiedPool: false); // 模拟 --insecure 连接

            Assert.Equal(0, Config.Current.ServerClockOffsetSeconds);
        }
        finally { Config.Apply(original); }
    }

    [Fact]
    public void GetTimeStamp_RespectsServerClockOffset()
    {
        var original = Config.Current;
        try
        {
            Config.Apply(Config.Current with { ServerClockOffsetSeconds = 3600 });
            long ts = long.Parse(BBDownUtil.GetTimeStamp(true));
            long expected = DateTimeOffset.UtcNow.AddSeconds(3600).ToUnixTimeSeconds();
            Assert.InRange(ts, expected - 3, expected + 3);
        }
        finally { Config.Apply(original); }
    }

    [Fact]
    public void GetTimeStamp_ZeroOffset_MatchesUtc()
    {
        var original = Config.Current;
        try
        {
            Config.Apply(Config.Current with { ServerClockOffsetSeconds = 0 });
            long ts = long.Parse(BBDownUtil.GetTimeStamp(true));
            long expected = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Assert.InRange(ts, expected - 3, expected + 3);
        }
        finally { Config.Apply(original); }
    }

    [Fact]
    public async Task GetWebSource_NonAuthoritativeHost_DoesNotOverwriteClockOffset()
    {
        // 端到端：非权威主机（本机 HttpListener）的 Date 头不得覆盖已校准的偏移——
        // 防止边缘/本地服务器时钟抖动 WBI 签名基准（CalibrateClock 只对 api.bilibili.com 校准）。
        using var server = new ScriptedServer((200, """{"code":0}"""));
        var original = Config.Current;
        try
        {
            Config.Apply(Config.Current with { ServerClockOffsetSeconds = 3600 });
            await HTTPUtil.GetWebSourceAsync($"http://127.0.0.1:{server.Port}/api", token: CancellationToken.None);
            // 本机 127.0.0.1 非权威主机：偏移保持 3600 不被本机 Date 覆盖
            Assert.Equal(3600, Config.Current.ServerClockOffsetSeconds);
        }
        finally { Config.Apply(original); }
    }

    /// <summary>返回指定 (状态码, 响应体) 的本地服务。HttpListener 响应自动带 Date 头（本机 UTC）。</summary>
    private sealed class ScriptedServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        public int Port { get; }

        public ScriptedServer(params (int Status, string Body)[] responses)
        {
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
                            var (status, body) = responses.FirstOrDefault();
                            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                            ctx.Response.StatusCode = status;
                            ctx.Response.ContentLength64 = bytes.Length;
                            await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token);
                            ctx.Response.Close();
                        }
                        catch { /* 客户端中止：忽略 */ }
                    }
                }
                catch (HttpListenerException) { /* 服务停止 */ }
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
