using BBDown.Core;

namespace BBDown.Tests;

[Collection("PathLockCollection")]
public class LocalToolIntegrationTests
{
    [Fact]
    [Trait("Category", "LocalIntegration")]
    [Trait("Tool", "aria2c")]
    public async Task Aria2c_RealInterruptedPreallocation_ResumesAndMatchesSha256()
    {
        var aria2c = IntegrationTool("aria2c");
        if (aria2c is null) return;
        using var server = new DownloadPipelineTests.LocalByteServer(8 * 1024 * 1024);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-real-aria2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        var url = $"http://127.0.0.1:{server.Port}/file";
        var savedTool = BBDownAria2c.ARIA2C;
        var savedRunner = BBDownAria2c.ProcessRunner;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            BBDownAria2c.ARIA2C = aria2c;
            BBDownAria2c.ProcessRunner = new SystemProcessRunner();
            var config = new BBDownDownloadUtil.DownloadConfig
            {
                UseAria2c = true,
                Aria2cArgs = "--no-conf=true --file-allocation=trunc --enable-color=false --summary-interval=0 --max-download-limit=512K --stop=3 --auto-save-interval=1"
            };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => BBDownDownloadUtil.DownloadFileAsync(url, target, config, timeout.Token));
            Assert.Contains("退出码 7", ex.Message);
            Assert.Equal(server.Payload.Length, new FileInfo(target).Length);
            Assert.True(File.Exists(target + ".aria2"), "真实 aria2c 必须产生可恢复的控制文件");
            Assert.NotEqual(server.PayloadHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            int initialRequestCount = server.RangeHeaders.Count;

            config.Aria2cArgs = "--no-conf=true --file-allocation=trunc --enable-color=false --summary-interval=0";
            await BBDownDownloadUtil.DownloadFileAsync(url, target, config, timeout.Token);

            Assert.Equal(server.PayloadHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            Assert.Contains(server.RangeHeaders.Skip(initialRequestCount),
                range => range.StartsWith("bytes=", StringComparison.Ordinal) && !range.StartsWith("bytes=0-", StringComparison.Ordinal));
            Assert.False(File.Exists(target + ".aria2"));
            Assert.True(File.Exists(target + ".manifest.json"), "保留已完成资源身份，供后续运行校验");
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            BBDownAria2c.ARIA2C = savedTool;
            BBDownAria2c.ProcessRunner = savedRunner;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    [Trait("Tool", "mp4decrypt")]
    public async Task Drm_RealCencDecryption_PreservesVideoAndAudioPackets()
    {
        var ffmpeg = IntegrationTool("ffmpeg");
        var mp4decrypt = IntegrationTool("mp4decrypt");
        var mp4encrypt = IntegrationTool("mp4encrypt");
        if (ffmpeg is null || mp4decrypt is null || mp4encrypt is null) return;
        const string key = "00112233445566778899aabbccddeeff";
        const string kid = "ffeeddccbbaa99887766554433221100";
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-real-drm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var runner = new SystemProcessRunner();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        async Task RunFfmpegAsync(params string[] arguments)
        {
            int code = await runner.RunAsync(new ExternalProcessSpec
            {
                FileName = ffmpeg,
                Arguments = ["-hide_banner", "-loglevel", "error", "-y", .. arguments],
                TimeoutMs = 15000,
                OnStandardError = _ => { }
            }, timeout.Token);
            Assert.Equal(0, code);
        }
        try
        {
            var plain = Path.Combine(dir, "plain.mp4");
            var encrypted = Path.Combine(dir, "encrypted.mp4");
            await RunFfmpegAsync("-f", "lavfi", "-i", "testsrc=duration=1:size=128x96:rate=10",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac",
                "-shortest", "-movflags", "frag_keyframe+empty_moov", plain);
            int encryptionCode = await runner.RunAsync(new ExternalProcessSpec
            {
                FileName = mp4encrypt,
                Arguments = ["--method", "MPEG-CENC", "--strict", "--key", $"1:{key}:0000000000000001", "--property", $"1:KID:{kid}",
                    "--key", $"2:{key}:0000000000000002", "--property", $"2:KID:{kid}", plain, encrypted],
                TimeoutMs = 15000,
                OnStandardError = _ => { }
            }, timeout.Token);
            Assert.Equal(0, encryptionCode);
            Assert.NotEqual(TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(plain)),
                TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(encrypted)));

            await new DrmMediaDecryptor(runner).DecryptAsync(mp4decrypt, kid, key, encrypted, 15000, timeout.Token);

            // 去掉容器元数据差异，逐包比较解密后的两条基本流。
            foreach (var stream in new[] { "v:0", "a:0" })
            {
                var originalHash = Path.Combine(dir, stream[0] + "-plain.sha256");
                var decryptedHash = Path.Combine(dir, stream[0] + "-decrypted.sha256");
                await RunFfmpegAsync("-i", plain, "-map", "0:" + stream, "-c", "copy", "-f", "streamhash", "-hash", "sha256", originalHash);
                await RunFfmpegAsync("-i", encrypted, "-map", "0:" + stream, "-c", "copy", "-f", "streamhash", "-hash", "sha256", decryptedHash);
                Assert.Equal(await File.ReadAllTextAsync(originalHash), await File.ReadAllTextAsync(decryptedHash));
            }
            Assert.Empty(Directory.GetFiles(dir, "*.dec-*"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    [Trait("Tool", "ffmpeg")]
    public async Task LiveConcat_RealFfmpeg_PreservesAllMediaPackets()
    {
        var ffmpeg = IntegrationTool("ffmpeg");
        if (ffmpeg is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-real-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var originalTool = BBDownMuxer.FFMPEG;
        var originalRunner = BBDownMuxer.ProcessRunner;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            BBDownMuxer.FFMPEG = ffmpeg;
            BBDownMuxer.ProcessRunner = new SystemProcessRunner();
            var segments = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                var segment = Path.Combine(dir, $"seg-{i:000}.flv");
                int code = await BBDownMuxer.ProcessRunner.RunAsync(new ExternalProcessSpec
                {
                    FileName = ffmpeg,
                    Arguments = ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=64x64:rate=10:duration=1",
                        "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-f", "flv", segment],
                    TimeoutMs = 10000,
                    OnStandardError = _ => { }
                }, timeout.Token);
                Assert.Equal(0, code);
                segments.Add(segment);
            }
            var output = Path.Combine(dir, "merged.flv");

            bool ok = await LiveStreamUtil.ConcatSegmentsAsync(segments, output, timeout.Token);
            Assert.True(ok, "真实 FFmpeg 合并两个独立 FLV 段时必须保留所有音视频帧");
            Assert.True(new FileInfo(output).Length > 0);
            Assert.All(segments, segment => Assert.True(File.Exists(segment), "验证阶段应保留源段"));
        }
        finally
        {
            BBDownMuxer.FFMPEG = originalTool;
            BBDownMuxer.ProcessRunner = originalRunner;
            Directory.Delete(dir, true);
        }
    }

    internal static string? IntegrationTool(string name)
    {
        var path = ExternalToolHelper.FindExecutable(name);
        if (path is null && Environment.GetEnvironmentVariable("BBDOWN_REQUIRE_LOCAL_TOOLS") == "1")
            throw new InvalidOperationException($"集成测试必须安装 {name}；禁止静默空跑。");
        return path;
    }
}
