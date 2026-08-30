using BBDown;

namespace BBDown.Tests;

/// <summary>
/// aria2c 外部调用测试：退出码必须被校验（此前被完全忽略，非零退出但产物存在的
/// 场景会被当作成功进入混流），且须启用 --continue=true 断点续传（此前中断后整文件重下）。
/// 替换静态 <see cref="BBDownAria2c.ProcessRunner"/>，测试结束恢复。
/// </summary>
public class BBDownAria2cTests
{
    /// <summary>捕获调用参数并返回预设退出码的假执行器。</summary>
    private sealed class FakeAria2cRunner : IExternalProcessRunner
    {
        private readonly int _exitCode;
        public List<ExternalProcessSpec> Specs { get; } = [];

        public FakeAria2cRunner(int exitCode) => _exitCode = exitCode;

        public Task<int> RunAsync(ExternalProcessSpec spec, CancellationToken cancellationToken = default)
        {
            Specs.Add(spec);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_exitCode);
        }
    }

    [Fact]
    public async Task DownloadFileByAria2cAsync_NonZeroExit_Throws()
    {
        var fake = new FakeAria2cRunner(exitCode: 4); // 达到最大重试次数
        var original = BBDownAria2c.ProcessRunner;
        try
        {
            BBDownAria2c.ProcessRunner = fake;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                BBDownAria2c.DownloadFileByAria2cAsync("http://example.com/a.mp4", "out/a.mp4", ""));

            // 错误信息应包含可读的退出码说明
            Assert.Contains("4", ex.Message);
            Assert.Contains("达到最大重试次数", ex.Message);
        }
        finally
        {
            BBDownAria2c.ProcessRunner = original;
        }
    }

    [Fact]
    public async Task DownloadFileByAria2cAsync_ZeroExit_DoesNotThrow_AndEnablesResume()
    {
        var fake = new FakeAria2cRunner(exitCode: 0);
        var original = BBDownAria2c.ProcessRunner;
        try
        {
            BBDownAria2c.ProcessRunner = fake;

            await BBDownAria2c.DownloadFileByAria2cAsync("http://example.com/a.mp4", "out/a.mp4", "");

            var spec = fake.Specs.Single();
            // 断点续传已启用：中断留下的 .aria2 控制文件可续传，而非整文件重下
            Assert.Contains(spec.Arguments, a => a.Contains("--continue=true"));
            // 凭据走 stdin 而非命令行参数
            Assert.NotNull(spec.StandardInput);
        }
        finally
        {
            BBDownAria2c.ProcessRunner = original;
        }
    }

    /// <summary>
    /// 取消必须原样传播为 OperationCanceledException，不能被转成"aria2c 下载失败"的
    /// InvalidOperationException——否则用户 Ctrl+C 会被误报为下载失败。
    /// </summary>
    [Fact]
    public async Task DownloadFileByAria2cAsync_UserCancellation_PropagatesOperationCanceled()
    {
        var fake = new FakeAria2cRunner(exitCode: 0);
        var original = BBDownAria2c.ProcessRunner;
        try
        {
            BBDownAria2c.ProcessRunner = fake;

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BBDownAria2c.DownloadFileByAria2cAsync("http://example.com/a.mp4", "out/a.mp4", "", cts.Token));
        }
        finally
        {
            BBDownAria2c.ProcessRunner = original;
        }
    }

    /// <summary>
    /// RF-21：URL 与 Cookie 是外部输入（API 响应/用户配置），aria2c input-file 的
    /// 换行即指令行分隔符——含 CR/LF 的值必须剥离后再写 stdin，否则可注入任意
    /// aria2c 指令行（新 URI/all-proxy/dir 等）。
    /// </summary>
    [Fact]
    public async Task DownloadFileByAria2cAsync_StripsNewlinesFromUrlAndCookie()
    {
        var fake = new FakeAria2cRunner(exitCode: 0);
        var original = BBDownAria2c.ProcessRunner;
        var originalConfig = Core.Config.Current;
        try
        {
            BBDownAria2c.ProcessRunner = fake;
            Core.Config.ApplyToCurrentAsyncFlow(originalConfig with
            {
                Cookie = "SESSDATA=abc\r\n  all-proxy=http://evil:8080"
            });

            await BBDownAria2c.DownloadFileByAria2cAsync(
                "http://cdn.example/a.mp4\nhttp://evil.example/b.mp4", "out/a.mp4", "");

            var stdin = fake.Specs.Single().StandardInput;
            // 换行剥离后 cookie 值保持单行：注入的 "  all-proxy=..." 不再构成独立指令行
            //（前面没有换行分隔符，aria2c 只把整行当作 Cookie 头的值）
            Assert.DoesNotContain("\n  all-proxy", stdin);
            Assert.DoesNotContain("\r", stdin);
            // 注入的第二个 URI 不得成为独立指令行：与原 URI 合并成一行畸形 URI
            //（aria2c 会因非法 URI 报非零退出，由退出码校验兜底）
            Assert.DoesNotContain("\nhttp://evil.example/b.mp4", stdin);
            // URI 单独一行（拼接后的畸形值不影响行结构）
            Assert.Contains("http://cdn.example/a.mp4http://evil.example/b.mp4\n", stdin);
            // cookie 头单行完整
            Assert.Contains("  header=Cookie: SESSDATA=abc  all-proxy=http://evil:8080\n", stdin);
        }
        finally
        {
            Core.Config.ApplyToCurrentAsyncFlow(originalConfig);
            BBDownAria2c.ProcessRunner = original;
        }
    }
}
