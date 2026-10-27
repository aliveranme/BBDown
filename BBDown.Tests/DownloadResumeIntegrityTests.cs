using System.Net;
using BBDown.Core;

namespace BBDown.Tests;

[Collection("PathLockCollection")]
public class DownloadResumeIntegrityTests
{
    [Fact]
    public async Task SingleThreadRetry_ChangedResource_DiscardsOldPrefix()
    {
        using var server = new ChangingResourceServer(interruptFirst: true);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var original = Config.Current;
        try
        {
            Config.Apply(original with { RetryDelayMs = 0, MaxRetryCount = 3 });
            var target = Path.Combine(dir, "video.mp4");
            await BBDownDownloadUtil.DownloadFileAsync(server.Url, target, new());
            Assert.Equal(server.NewPayload, await File.ReadAllBytesAsync(target));
            Assert.True(server.IfRangeHeaders.Count >= 2);
            Assert.All(server.IfRangeHeaders, value => Assert.Equal("\"old\"", value));
        }
        finally
        {
            Config.Apply(original);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MultiThreadDownload_ChangedResource_DiscardsAllSegments()
    {
        using var server = new ChangingResourceServer(interruptFirst: false);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var original = Config.Current;
        try
        {
            Config.Apply(original with { ThreadSegmentSizeMb = 1, RetryDelayMs = 0, MaxRetryCount = 3 });
            var target = Path.Combine(dir, "video.mp4");
            await Assert.ThrowsAsync<RemoteResourceChangedException>(() =>
                BBDownDownloadUtil.MultiThreadDownloadAndMergeAsync(server.Url, target, new() { MultiThread = true }));
            Assert.False(File.Exists(target));
            Assert.Empty(Directory.GetFiles(dir, "*.vclip"));
            Assert.Empty(Directory.GetFiles(dir, "*.manifest.json"));
            Assert.True(server.IfRangeHeaders.Count >= 2);
            Assert.All(server.IfRangeHeaders, value => Assert.Equal("\"old\"", value));
        }
        finally
        {
            Config.Apply(original);
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aria2c_PreallocatedInterruptedFile_ResumesThroughDownloader(bool multiThread)
    {
        using var server = new DownloadPipelineTests.LocalByteServer(64 * 1024);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        var url = $"http://127.0.0.1:{server.Port}/file";
        var originalRunner = BBDownAria2c.ProcessRunner;
        try
        {
            // aria2c 预分配到远端总长，但只有部分字节已下载；控制文件记录哪些块尚缺。
            await File.WriteAllBytesAsync(target, new byte[server.Payload.Length]);
            await File.WriteAllTextAsync(target + ".aria2", "incomplete-block-map");
            await WriteManifestAsync(target, url, server.Payload.Length);
            var runner = new CompletingAria2cRunner(async token =>
            {
                Assert.True(File.Exists(target + ".aria2"), "不能在 aria2c 恢复前删除控制文件");
                Assert.Equal(server.Payload.Length, new FileInfo(target).Length);
                await File.WriteAllBytesAsync(target, server.Payload.ToArray(), token);
                File.Delete(target + ".aria2");
            });
            BBDownAria2c.ProcessRunner = runner;
            var config = new BBDownDownloadUtil.DownloadConfig { UseAria2c = true, MultiThread = multiThread };

            if (multiThread)
                await BBDownDownloadUtil.MultiThreadDownloadAndMergeAsync(url, target, config);
            else
                await BBDownDownloadUtil.DownloadFileAsync(url, target, config);

            Assert.Equal(1, runner.CallCount);
            Assert.Equal(server.PayloadHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            Assert.False(File.Exists(target + ".aria2"));
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            BBDownAria2c.ProcessRunner = originalRunner;
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(0)] // 旧清单没有分片布局，不能确认后续分片的偏移。
    [InlineData(1)] // 布局一致，保留已有前缀并从中断处续传。
    [InlineData(2)] // 原 2MB 分片改成 1MB：第二分片的起点发生变化。
    public async Task MultiThreadResume_ValidatesSegmentLayout_AndProducesExactContent(int previousSegmentMb)
    {
        const int mb = 1024 * 1024;
        const int prefixSize = mb / 4;
        using var server = new DownloadPipelineTests.LocalByteServer(3 * mb);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        var url = $"http://127.0.0.1:{server.Port}/file";
        var original = Config.Current;
        try
        {
            // 只留下第二分片；首分片不存在时也必须校验轨道清单。
            var clip = Path.Combine(dir, "00001_video.vclip");
            int previousOffset = (previousSegmentMb == 1 ? 1 : 2) * mb;
            await File.WriteAllBytesAsync(clip, server.Payload.Slice(previousOffset, prefixSize).ToArray());
            await WriteManifestAsync(Path.Combine(dir, "00000_video.vclip"), url,
                server.Payload.Length, previousSegmentMb * (long)mb);
            Config.Apply(original with { ThreadSegmentSizeMb = 1 });

            await BBDownDownloadUtil.MultiThreadDownloadAndMergeAsync(url, target,
                new BBDownDownloadUtil.DownloadConfig { MultiThread = true });

            Assert.Equal(server.PayloadHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            string expectedRange = previousSegmentMb == 1
                ? $"bytes={mb + prefixSize}-{2 * mb - 1}"
                : $"bytes={mb}-{2 * mb - 1}";
            Assert.Contains(expectedRange, server.RangeHeaders);
            Assert.Empty(Directory.GetFiles(dir, "*.vclip"));
            Assert.Empty(Directory.GetFiles(dir, "*.manifest.json"));
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Config.Apply(original);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MultiThreadDownload_FailedClip_CancelsStalledSiblingAndReleasesLock()
    {
        // 单核宿主只会调度一路分片，无法制造两个在途请求。
        if (Environment.ProcessorCount < 2) return;
        using var server = new ConcurrentFailureServer();
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        var original = Config.Current;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task? download = null;
        try
        {
            Config.Apply(original with { ThreadSegmentSizeMb = 1 });
            download = BBDownDownloadUtil.MultiThreadDownloadAndMergeAsync(server.Url, target,
                new BBDownDownloadUtil.DownloadConfig { MultiThread = true }, cts.Token);
            await server.SiblingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // 服务端明确等第二请求开始后才拒绝首分片；失败应主动打断另一响应的读取，
            // 不依赖 60 秒停滞看门狗或用户取消。此处时间上限只是死锁保护。
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => download.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("不支持多线程", ex.Message);
            Assert.False(cts.IsCancellationRequested);
            Assert.False(File.Exists(target));
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            cts.Cancel();
            if (download is not null)
            {
                try { await download; }
                catch (Exception) { /* 回收失败/取消任务后再删除分片。 */ }
            }
            Config.Apply(original);
            Directory.Delete(dir, true);
        }
    }

    private static Task WriteManifestAsync(string path, string url, long size, long segmentSize = 0)
    {
        // 手写旧/新格式，验证真实磁盘兼容性；url 是测试服务器生成的固定回环地址。
        string layout = segmentSize > 0 ? $"\"SegmentSizeBytes\": {segmentSize}," : "";
        return File.WriteAllTextAsync(path + ".manifest.json", $$"""
            {
                "Identity": "{{url}}",
                {{layout}}
                "TotalLength": {{size}},
                "LastModified": null,
                "ETag": null
            }
            """);
    }

    private sealed class CompletingAria2cRunner(Func<CancellationToken, Task> complete) : IExternalProcessRunner
    {
        public int CallCount { get; private set; }

        public async Task<int> RunAsync(ExternalProcessSpec spec, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Assert.Contains("--continue=true", spec.Arguments);
            await complete(cancellationToken);
            return 0;
        }
    }

    /// <summary>首次请求提供旧对象，接下来同长度的新对象按 If-Range 规范返回完整 200。</summary>
    private sealed class ChangingResourceServer : IDisposable
    {
        private const int Mb = 1024 * 1024;
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly bool _interruptFirst;
        private int _requests;
        public string Url { get; }
        public byte[] NewPayload { get; } = Enumerable.Repeat((byte)0x42, 2 * Mb).ToArray();
        public List<string?> IfRangeHeaders { get; } = [];

        public ChangingResourceServer(bool interruptFirst)
        {
            _interruptFirst = interruptFirst;
            string prefix = $"http://127.0.0.1:{TestPort.Allocate()}/";
            Url = prefix + "file";
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var context = await _listener.GetContextAsync();
                        try { await RespondAsync(context); }
                        catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
                    }
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
            });
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            var response = context.Response;
            if (context.Request.HttpMethod == "HEAD")
            {
                response.AddHeader("ETag", "\"old\"");
                response.ContentLength64 = NewPayload.Length;
                response.Close();
                return;
            }
            IfRangeHeaders.Add(context.Request.Headers["If-Range"]);
            if (Interlocked.Increment(ref _requests) == 1)
            {
                response.AddHeader("ETag", "\"old\"");
                var range = context.Request.Headers["Range"]![6..].Split('-');
                int from = int.Parse(range[0]);
                int to = string.IsNullOrEmpty(range[1]) ? NewPayload.Length - 1 : int.Parse(range[1]);
                response.StatusCode = 206;
                response.AddHeader("Content-Range", $"bytes {from}-{to}/{NewPayload.Length}");
                response.ContentLength64 = to - from + 1;
                int length = _interruptFirst ? 32 * 1024 : to - from + 1;
                await response.OutputStream.WriteAsync(Enumerable.Repeat((byte)0x11, length).ToArray(), _cts.Token);
                if (_interruptFirst)
                {
                    await response.OutputStream.FlushAsync(_cts.Token);
                    response.Abort();
                    return;
                }
            }
            else
            {
                response.AddHeader("ETag", "\"new\"");
                response.StatusCode = 200;
                response.ContentLength64 = NewPayload.Length;
                await response.OutputStream.WriteAsync(NewPayload, _cts.Token);
            }
            response.Close();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
            _loop.GetAwaiter().GetResult();
            _cts.Dispose();
        }
    }

    private sealed class ConcurrentFailureServer : IDisposable
    {
        private const int Mb = 1024 * 1024;
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _handlers = [];
        private readonly Task _loop;
        public string Url { get; }
        public TaskCompletionSource SiblingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentFailureServer()
        {
            string prefix = $"http://127.0.0.1:{TestPort.Allocate()}/";
            Url = prefix + "file";
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var context = await _listener.GetContextAsync();
                        _handlers.Add(RespondAsync(context));
                    }
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
            });
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            try
            {
                var response = context.Response;
                if (context.Request.HttpMethod == "HEAD")
                {
                    response.ContentLength64 = 2 * Mb;
                }
                else if (context.Request.Headers["Range"] == $"bytes={Mb}-{2 * Mb - 1}")
                {
                    response.StatusCode = 206;
                    response.ContentLength64 = Mb;
                    response.AddHeader("Content-Range", $"bytes {Mb}-{2 * Mb - 1}/{2 * Mb}");
                    await response.OutputStream.WriteAsync(new byte[1], _cts.Token);
                    await response.OutputStream.FlushAsync(_cts.Token);
                    SiblingStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, _cts.Token);
                }
                else
                {
                    await SiblingStarted.Task.WaitAsync(_cts.Token);
                    response.StatusCode = 200; // 首分片不支持 Range，立即失败。
                    response.ContentLength64 = 0;
                }
                response.Close();
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
            _loop.GetAwaiter().GetResult();
            Task.WhenAll(_handlers).GetAwaiter().GetResult();
            _cts.Dispose();
        }
    }
}
