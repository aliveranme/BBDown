using System.Net;
using System.Text;
using System.Text.Json;
using BBDown;
using BBDown.Core.Util;

namespace BBDown.Tests;

[Collection("MuxerProcessRunnerCollection")]
public class LiveStreamUtilTests
{
    [Theory]
    [InlineData("正常标题", "正常标题")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("", "直播")]
    [InlineData("   ", "直播")]
    public void SanitizeFileName_StripsInvalidChars(string input, string expected)
        => Assert.Equal(expected, LiveStreamUtil.SanitizeFileName(input));

    /// <summary>
    /// concat 合成必须使用 BBDownMuxer.FFMPEG（用户 --ffmpeg-path / PATH 探测的路径），
    /// 而非硬编码 "ffmpeg"。此前硬编码会让用户的显式指定失效，且 PATH 未配置时
    /// 静默失败。
    /// </summary>
    [Fact]
    public async Task ConcatSegments_UsesBBDownMuxerFfmpegPath()
    {
        var fake = new FakeProcessRunner(exitCode: 0);
        var original = BBDownMuxer.ProcessRunner;
        var originalFfmpeg = BBDownMuxer.FFMPEG;
        var dir = Path.Combine(Path.GetTempPath(), "live-segs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            BBDownMuxer.ProcessRunner = fake;
            // 模拟用户显式指定 ffmpeg：FindBinaries 会把这个路径写入 BBDownMuxer.FFMPEG
            BBDownMuxer.FFMPEG = "/opt/custom/ffmpeg";

            var seg1 = Path.Combine(dir, "seg-000.flv");
            var seg2 = Path.Combine(dir, "seg-001.flv");
            File.WriteAllText(seg1, "a");
            File.WriteAllText(seg2, "b");
            var outPath = Path.Combine(dir, "out.flv");

            var ok = await LiveStreamUtil.ConcatSegmentsAsync([seg1, seg2], outPath, CancellationToken.None);

            Assert.True(ok);
            var spec = fake.Specs.Single();
            Assert.Equal("/opt/custom/ffmpeg", spec.FileName); // 不是硬编码 "ffmpeg"
            Assert.Contains("-f", spec.Arguments);
            Assert.Contains("concat", spec.Arguments);
        }
        finally
        {
            BBDownMuxer.ProcessRunner = original;
            BBDownMuxer.FFMPEG = originalFfmpeg;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// concat 列表文件与输出路径必须用绝对路径：自定义 --output 目录时若 CWD 与
    /// 目标目录不同，相对路径的 file '...' 条目会在 concat demuxer 读取时解析失败。
    /// </summary>
    [Fact]
    public async Task ConcatSegments_UsesAbsolutePathsForListAndOutput()
    {
        var fake = new FakeProcessRunner(exitCode: 0);
        var original = BBDownMuxer.ProcessRunner;
        var originalFfmpeg = BBDownMuxer.FFMPEG;
        var dir = Path.Combine(Path.GetTempPath(), "live-segs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            BBDownMuxer.ProcessRunner = fake;
            BBDownMuxer.FFMPEG = "ffmpeg";

            var outPath = Path.Combine(dir, "out.flv");
            var seg1 = Path.Combine(dir, "seg-000.flv");
            File.WriteAllText(seg1, "a");

            await LiveStreamUtil.ConcatSegmentsAsync([seg1], outPath, CancellationToken.None);

            var spec = fake.Specs.Single();
            var args = spec.Arguments;
            // -i 后紧跟的 concat 列表路径必须是绝对路径
            int iIdx = args.IndexOf("-i");
            Assert.True(iIdx >= 0, $"应包含 -i，args={string.Join(" ", args)}");
            Assert.True(Path.IsPathRooted(args[iIdx + 1]),
                $"concat 列表路径应为绝对路径，实际: {args[iIdx + 1]}");
            // 最后一个参数是输出路径，也必须是绝对路径
            Assert.True(Path.IsPathRooted(args[^1]),
                $"输出路径应为绝对路径，实际: {args[^1]}");
            // concat 列表内容必须包含绝对分段路径：假执行器在 finally 删除列表前
            // 捕获其内容（否则方法返回后列表已被清理，无从校验）
            Assert.NotNull(fake.CapturedInput);
            Assert.Contains(Path.GetFullPath(seg1), fake.CapturedInput);
        }
        finally
        {
            BBDownMuxer.ProcessRunner = original;
            BBDownMuxer.FFMPEG = originalFfmpeg;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// 上次录制合并失败保留的分段会话目录，在下次录制启动时**不得被删除**。
    /// 旧实现启动时递归删除整个 .segs 目录，把可恢复资产丢掉（可恢复数据丢失）。
    /// ReportStaleSessions 只提示保留位置，不删除任何非空会话。
    /// </summary>
    [Fact]
    public void ReportStaleSessions_PreservesNonEmptySessionDirectories()
    {
        var dir = Path.Combine(Path.GetTempPath(), "live-segs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 模拟上次失败保留的会话：根目录下有一个带分段的会话子目录
            var segRoot = Path.Combine(dir, "output.flv.segs");
            var staleSession = Path.Combine(segRoot, "session-20260101_000000");
            Directory.CreateDirectory(staleSession);
            File.WriteAllText(Path.Combine(staleSession, "seg-000.flv"), "recoverable-data");

            LiveStreamUtil.ReportStaleSessions(segRoot);

            // 非空旧会话必须原样保留（文件仍存在）
            Assert.True(File.Exists(Path.Combine(staleSession, "seg-000.flv")),
                "上次录制保留的分段不应在下次启动时被删除");
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// 当分段合并输出文件大小显著小于全部输入分段总和（如坏分段导致 ffmpeg concat demuxer 提前退出并生成截断产物），
    /// ConcatSegmentsAsync 必须判定为失败并返回 false，防止误删可恢复的分段。
    /// </summary>
    [Fact]
    public async Task ConcatSegments_TruncatedOutput_ReturnsFalse()
    {
        var fake = new FakeProcessRunner(exitCode: 0, outputContent: "tiny");
        var original = BBDownMuxer.ProcessRunner;
        var dir = Path.Combine(Path.GetTempPath(), "live-segs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            BBDownMuxer.ProcessRunner = fake;
            var seg1 = Path.Combine(dir, "seg-000.flv");
            var seg2 = Path.Combine(dir, "seg-001.flv");
            // 创建两个较大的分段（各 100KB）
            File.WriteAllBytes(seg1, new byte[100 * 1024]);
            File.WriteAllBytes(seg2, new byte[100 * 1024]);
            var outPath = Path.Combine(dir, "out.flv");

            // fake 只写了 "tiny" (4 字节)，远小于 200KB 的输入总长
            var ok = await LiveStreamUtil.ConcatSegmentsAsync([seg1, seg2], outPath, CancellationToken.None);

            Assert.False(ok);
        }
        finally
        {
            BBDownMuxer.ProcessRunner = original;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// SelectFlvUrl 的选流纯函数测试（内联 JSON，无网络）：FLV 被优先选中，
    /// availableFormats 收集接口实际提供的全部格式（含被跳过的 ts/fmp4），
    /// quality 返回选中 codec 的 current_qn。
    /// </summary>
    [Fact]
    public void SelectFlvUrl_PicksFlv_AndReportsAllFormats()
    {
        const string playUrl = """
        {
          "stream": [
            {
              "protocol_name": "http_stream",
              "format": [
                { "format_name": "flv", "codec": [
                  { "codec_name": "avc", "current_qn": 10000, "base_url": "/live/room/1.flv", "url_info": [
                    { "host": "https://example.com", "extra": "?token=1" } ] }
                ] }
              ]
            },
            {
              "protocol_name": "http_hls",
              "format": [
                { "format_name": "ts", "codec": [
                  { "codec_name": "avc", "base_url": "/live/room/1.m3u8", "url_info": [
                    { "host": "https://hls.example.com", "extra": "?token=2" } ] }
                ] }
              ]
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(playUrl);

        var url = LiveStreamUtil.SelectFlvUrl(doc.RootElement, out var formats, out var quality);

        Assert.Equal("https://example.com/live/room/1.flv?token=1", url);
        Assert.Equal(10000, quality);
        Assert.Contains("flv", formats);
        Assert.Contains("ts", formats);
    }

    [Fact]
    public void SelectFlvUrl_OnlyHls_ReturnsNull_AndReportsTs()
    {
        const string playUrl = """
        {
          "stream": [
            {
              "protocol_name": "http_hls",
              "format": [
                { "format_name": "ts", "codec": [
                  { "codec_name": "avc", "base_url": "/live/room/2.m3u8", "url_info": [
                    { "host": "https://hls.example.com", "extra": "?token=2" } ] }
                ] }
              ]
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(playUrl);

        var url = LiveStreamUtil.SelectFlvUrl(doc.RootElement, out var formats, out _);

        Assert.Null(url); // HLS 暂不支持：无 FLV 时返回 null，由调用方报可操作错误
        Assert.Equal(new[] { "ts" }, formats);
    }

    [Fact]
    public void SelectFlvUrl_EmptyPlayUrl_ReturnsNull_AndNoFormats()
    {
        using var doc = JsonDocument.Parse("""{"stream": []}""");

        var url = LiveStreamUtil.SelectFlvUrl(doc.RootElement, out var formats, out _);

        Assert.Null(url);
        Assert.Empty(formats);
    }

    // ==================== 完整录制循环集成测试（本地假 B 站服务器） ====================

    /// <summary>
    /// Ctrl+C 停止时必须把当前分段已写入的内容保留并保存（此前取消发生在分段读取中时，
    /// 该分段的全部内容会随"无数据"分支被丢弃——录制几分钟后 Ctrl+C 会丢掉全部内容）。
    /// </summary>
    [Fact]
    public async Task DownloadToFile_CancelMidStream_PreservesRecordedContent()
    {
        using var server = new FakeLiveServer();
        server.StreamModes.Enqueue(FakeLiveServer.StreamMode.Stall); // 写 1 块后挂起，模拟录制进行中
        var progressTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (result, outPath, dir) = await RunWithServerAsync(server, progressTcs, async (cts, task) =>
        {
            // 等客户端确实读到了数据（已落盘）再取消——此时录制的确有内容可保存
            await progressTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
            return await task;
        });

        Assert.Equal(LiveStreamUtil.LiveRecordResult.Success, result);
        var saved = await File.ReadAllBytesAsync(outPath);
        Assert.NotEmpty(saved); // 取消前已录内容必须被保存
        Assert.False(Directory.Exists(outPath + ".segs"), "录制结束后不应残留 .segs 临时目录");
        try { Directory.Delete(dir, true); } catch { }
    }

    /// <summary>
    /// 网络断开（连接中途被服务器掐断）后必须自动重新解析并续录到新分段，
    /// 全部结束后合成最终文件；主播下播则正常结束。
    /// </summary>
    [Fact]
    public async Task DownloadToFile_DisconnectMidStream_ReconnectsAndConcats()
    {
        using var server = new FakeLiveServer();
        server.StreamModes.Enqueue(FakeLiveServer.StreamMode.AbortMidStream); // 读一半被掐断
        server.StreamModes.Enqueue(FakeLiveServer.StreamMode.Normal);         // 续录段正常到 EOF
        server.OfflineAfterStreams = 2;                                       // 两段之后主播下播

        var (result, outPath, dir) = await RunWithServerAsync(server, null, (cts, task) => task);

        Assert.Equal(LiveStreamUtil.LiveRecordResult.Success, result);
        Assert.Equal(2, server.StreamRequestCount); // 断流后自动重连了一次
        var saved = await File.ReadAllBytesAsync(outPath);
        // 两段内容都应合入最终文件（每段 ChunkCount 块 × ChunkBytes）
        long expected = 2L * server.StreamChunkCount * server.StreamChunkBytes;
        Assert.Equal(expected, saved.Length);
        Assert.False(Directory.Exists(outPath + ".segs"), "录制结束后不应残留 .segs 临时目录");
        try { Directory.Delete(dir, true); } catch { }
    }

    /// <summary>
    /// 网络黑洞（连接既不 RST 也不 EOF、只是不再有数据）必须被读停滞看门狗发现，
    /// 而不是永久卡死；看门狗触发后自动重连续录。
    /// </summary>
    [Fact]
    public async Task DownloadToFile_StalledConnection_WatchdogReconnects()
    {
        var originalStall = LiveStreamUtil.ReadStallTimeout;
        LiveStreamUtil.ReadStallTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            using var server = new FakeLiveServer();
            server.StreamModes.Enqueue(FakeLiveServer.StreamMode.Stall); // 写 1 块后静默停滞
            server.StreamModes.Enqueue(FakeLiveServer.StreamMode.Normal);
            server.OfflineAfterStreams = 2;

            var (result, outPath, dir) = await RunWithServerAsync(server, null, (cts, task) => task);

            Assert.Equal(LiveStreamUtil.LiveRecordResult.Success, result);
            Assert.Equal(2, server.StreamRequestCount); // 停滞被看门狗识破并重连
            var saved = await File.ReadAllBytesAsync(outPath);
            long expected = (1L + server.StreamChunkCount) * server.StreamChunkBytes;
            Assert.Equal(expected, saved.Length);
            try { Directory.Delete(dir, true); } catch { }
        }
        finally
        {
            LiveStreamUtil.ReadStallTimeout = originalStall;
        }
    }

    /// <summary>
    /// 直播间已下播时录制应立即正常结束（NoData，不生成空文件、不残留目录）。
    /// </summary>
    [Fact]
    public async Task DownloadToFile_RoomOffline_ReturnsNoData_NoResidue()
    {
        using var server = new FakeLiveServer();
        server.IsLive = false;

        var (result, outPath, dir) = await RunWithServerAsync(server, null, (cts, task) => task);

        Assert.Equal(LiveStreamUtil.LiveRecordResult.NoData, result);
        Assert.False(File.Exists(outPath));
        Assert.False(Directory.Exists(outPath + ".segs"), "NoData 也不应残留 .segs 目录");
        try { Directory.Delete(dir, true); } catch { }
    }

    /// <summary>
    /// 画质请求必须带 qn=30000（最高档，按账号权限回落），而不是低档位——
    /// 配合登录凭据（BBDown.data）才能拿到账号可看的最高画质。
    /// </summary>
    [Fact]
    public async Task ResolveAsync_RequestsHighestQualityQn()
    {
        using var server = new FakeLiveServer();
        server.IsLive = true;

        var originalHost = LiveStreamUtil.LiveApiHost;
        LiveStreamUtil.LiveApiHost = $"http://127.0.0.1:{server.Port}";
        try
        {
            var (url, title, uname, roomId, quality) = await LiveStreamUtil.ResolveAsync("12345", CancellationToken.None);

            Assert.True(server.PlayRequests.Count >= 1);
            Assert.Contains("qn=30000", server.PlayRequests[0]); // 首选最高画质
            Assert.NotNull(url);
            Assert.Equal("测试直播", title);
            Assert.Equal("tester", uname);
            Assert.Equal("12345", roomId);
            Assert.Equal(10000, quality);
        }
        finally
        {
            LiveStreamUtil.LiveApiHost = originalHost;
        }
    }

    /// <summary>
    /// 最高档请求若只返回 ts/fmp4（无 flv），应回落 qn=10000（原画）再取一次；
    /// 仍取不到 flv 时抛 LiveStreamUnavailableException（终结态），而不是无限重试。
    /// </summary>
    [Fact]
    public async Task ResolveAsync_FallsBackToLowerQn_WhenTopHasNoFlv()
    {
        using var server = new FakeLiveServer();
        server.IsLive = true;
        // 第一次 play 响应（qn=30000）：只有 ts；第二次（qn=10000）：有 flv
        server.PlayBodies.Enqueue(server.PlayBody(flv: false, qn: 30000));
        server.PlayBodies.Enqueue(server.PlayBody(flv: true, qn: 10000));

        var originalHost = LiveStreamUtil.LiveApiHost;
        LiveStreamUtil.LiveApiHost = $"http://127.0.0.1:{server.Port}";
        try
        {
            var (url, _, _, _, quality) = await LiveStreamUtil.ResolveAsync("12345", CancellationToken.None);

            Assert.Equal(2, server.PlayRequests.Count);
            Assert.Contains("qn=30000", server.PlayRequests[0]);
            Assert.Contains("qn=10000", server.PlayRequests[1]);
            Assert.NotNull(url);
            Assert.Equal(10000, quality); // 回落后的原画
        }
        finally
        {
            LiveStreamUtil.LiveApiHost = originalHost;
        }
    }

    [Fact]
    public async Task ResolveAsync_NoFlvAtAll_ThrowsLiveStreamUnavailable()
    {
        using var server = new FakeLiveServer();
        server.IsLive = true;
        server.PlayBodies.Enqueue(server.PlayBody(flv: false, qn: 30000));
        server.PlayBodies.Enqueue(server.PlayBody(flv: false, qn: 10000));

        var originalHost = LiveStreamUtil.LiveApiHost;
        LiveStreamUtil.LiveApiHost = $"http://127.0.0.1:{server.Port}";
        try
        {
            var ex = await Assert.ThrowsAsync<LiveStreamUtil.LiveStreamUnavailableException>(
                () => LiveStreamUtil.ResolveAsync("12345", CancellationToken.None));
            Assert.Contains("ts", ex.Message);
        }
        finally
        {
            LiveStreamUtil.LiveApiHost = originalHost;
        }
    }

    /// <summary>
    /// 在假服务器上跑一次完整 DownloadToFileAsync：替换 ProcessRunner 为假 concat
    /// 执行器（真实拼接分段字节），LiveApiHost 指向本地服务器。
    /// <paramref name="progressTcs"/> 非空时在客户端首次落盘数据时完成——供"取消"类
    /// 测试等待录制真正进行中。返回 (结果, 输出路径, 临时目录)。
    /// </summary>
    private static async Task<(LiveStreamUtil.LiveRecordResult Result, string OutPath, string Dir)> RunWithServerAsync(
        FakeLiveServer server, TaskCompletionSource? progressTcs,
        Func<CancellationTokenSource, Task<LiveStreamUtil.LiveRecordResult>, Task<LiveStreamUtil.LiveRecordResult>> body)
    {
        var originalHost = LiveStreamUtil.LiveApiHost;
        var originalRunner = BBDownMuxer.ProcessRunner;
        var originalFfmpeg = BBDownMuxer.FFMPEG;
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, "out.flv");
        try
        {
            LiveStreamUtil.LiveApiHost = $"http://127.0.0.1:{server.Port}";
            BBDownMuxer.ProcessRunner = new ConcatProcessRunner();
            BBDownMuxer.FFMPEG = "ffmpeg";

            using var cts = new CancellationTokenSource();
            var task = LiveStreamUtil.DownloadToFileAsync("12345", outPath, _ => progressTcs?.TrySetResult(), cts.Token);
            var result = await body(cts, task).WaitAsync(TimeSpan.FromSeconds(30));
            return (result, outPath, dir);
        }
        finally
        {
            LiveStreamUtil.LiveApiHost = originalHost;
            BBDownMuxer.ProcessRunner = originalRunner;
            BBDownMuxer.FFMPEG = originalFfmpeg;
        }
    }

    /// <summary>
    /// 段尾截断（网络中断/取消）会留下半个 FLV 标签：ffmpeg concat demuxer 在截断标签处
    /// 报错并中止整个合成。TrimFlvTail 必须把文件裁到最后一个完整标签——否则断流重连的
    /// 录制几乎必然合成失败。完整标签必须原样保留，只裁截断尾。
    /// </summary>
    [Fact]
    public void TrimFlvTail_RemovesTruncatedTag_KeepsCompleteTags()
    {
        var dir = Path.Combine(Path.GetTempPath(), "live-trim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "seg.flv");
        try
        {
            var complete = new byte[0];
            complete = Concat(complete, new byte[] { 0x46, 0x4C, 0x56, 0x01, 0x05, 0x00, 0x00, 0x00, 0x09 }); // FLV 头
            complete = Concat(complete, new byte[] { 0, 0, 0, 0 }); // PreviousTagSize0
            complete = Concat(complete, BuildFlvTag(0x12, new byte[] { 0x02, 0x00, 0x0A }, "onMetaData"u8.ToArray())); // 元数据
            complete = Concat(complete, BuildFlvTag(0x09, new byte[100], Array.Empty<byte>()));                  // 完整视频标签
            long goodEnd = complete.Length;
            // 截断尾：声明 500 字节负载但只写 100 字节
            complete = Concat(complete, BuildFlvTag(0x09, new byte[100], Array.Empty<byte>(), declaredPayload: 500));
            File.WriteAllBytes(path, complete);

            bool trimmed = LiveStreamUtil.TrimFlvTail(path);

            Assert.True(trimmed);
            Assert.Equal(goodEnd, new FileInfo(path).Length);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>尾部完整（正常 EOF）时 TrimFlvTail 不应改动文件。</summary>
    [Fact]
    public void TrimFlvTail_CompleteFile_Unchanged()
    {
        var dir = Path.Combine(Path.GetTempPath(), "live-trim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "seg.flv");
        try
        {
            var data = new byte[0];
            data = Concat(data, new byte[] { 0x46, 0x4C, 0x56, 0x01, 0x05, 0x00, 0x00, 0x00, 0x09 });
            data = Concat(data, new byte[] { 0, 0, 0, 0 });
            data = Concat(data, BuildFlvTag(0x09, new byte[64], Array.Empty<byte>()));
            File.WriteAllBytes(path, data);

            bool trimmed = LiveStreamUtil.TrimFlvTail(path);

            Assert.False(trimmed);
            Assert.Equal(data.Length, new FileInfo(path).Length);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// 当 FLV 标签首字节包含 Filter 位（Bit 5，如 0x29 对应带 Filter 的视频标签）时，
    /// TrimFlvTail 掩码过滤后仍应正确识别合法标签并裁剪截断尾。
    /// </summary>
    [Fact]
    public void TrimFlvTail_WithFilterBit_RemovesTruncatedTag()
    {
        var dir = Path.Combine(Path.GetTempPath(), "live-trim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "seg.flv");
        try
        {
            var complete = new byte[0];
            complete = Concat(complete, new byte[] { 0x46, 0x4C, 0x56, 0x01, 0x05, 0x00, 0x00, 0x00, 0x09 });
            complete = Concat(complete, new byte[] { 0, 0, 0, 0 });
            // 带 Filter 标志的视频标签：0x20 | 0x09 = 0x29
            complete = Concat(complete, BuildFlvTag(0x29, new byte[64], Array.Empty<byte>()));
            long goodEnd = complete.Length;
            // 截断尾
            complete = Concat(complete, BuildFlvTag(0x29, new byte[64], Array.Empty<byte>(), declaredPayload: 200));
            File.WriteAllBytes(path, complete);

            bool trimmed = LiveStreamUtil.TrimFlvTail(path);

            Assert.True(trimmed);
            Assert.Equal(goodEnd, new FileInfo(path).Length);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// 当发生终结态异常（如直播间无 FLV）时，DownloadToFileAsync 退出时不应残留空的 .segs 会话目录。
    /// </summary>
    [Fact]
    public async Task DownloadToFile_UnavailableException_CleansUpEmptySegsDir()
    {
        using var server = new FakeLiveServer();
        server.IsLive = true;
        server.PlayBodies.Enqueue(server.PlayBody(flv: false, qn: 30000));
        server.PlayBodies.Enqueue(server.PlayBody(flv: false, qn: 10000));

        var dir = Path.Combine(Path.GetTempPath(), "live-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, "out.flv");
        var originalHost = LiveStreamUtil.LiveApiHost;
        try
        {
            LiveStreamUtil.LiveApiHost = $"http://127.0.0.1:{server.Port}";
            await Assert.ThrowsAsync<LiveStreamUtil.LiveStreamUnavailableException>(
                () => LiveStreamUtil.DownloadToFileAsync("12345", outPath, null, CancellationToken.None));

            Assert.False(Directory.Exists(outPath + ".segs"), "抛出终结态异常后不应残留 .segs 空目录");
        }
        finally
        {
            LiveStreamUtil.LiveApiHost = originalHost;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    private static byte[] BuildFlvTag(int type, byte[] payload, byte[] extra, int? declaredPayload = null)
    {
        int declared = declaredPayload ?? payload.Length + extra.Length;
        var tag = new List<byte> { (byte)type };
        tag.Add((byte)(declared >> 16));
        tag.Add((byte)(declared >> 8));
        tag.Add((byte)declared);
        tag.AddRange(new byte[4]); // timestamp + ext
        tag.AddRange(new byte[3]); // stream id
        tag.AddRange(payload);
        tag.AddRange(extra);
        tag.AddRange(new byte[] { 0, 0, 0, 0 }); // prevTagSize（值不校验）
        return tag.ToArray();
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    /// <summary>记录收到的调用与取消令牌的假执行器。能捕获外部进程的 stdin 输入。
    /// 模拟真实 concat 产物：在 args 最后一个参数（输出路径）生成非空文件，使
    /// ConcatSegmentsAsync 的"产物存在且非空"校验通过。</summary>
    private sealed class FakeProcessRunner : IExternalProcessRunner
    {
        private readonly int _exitCode;
        private readonly string _outputContent;
        public List<ExternalProcessSpec> Specs { get; } = [];
        public string? CapturedInput { get; private set; }

        public FakeProcessRunner(int exitCode, string outputContent = "merged")
        {
            _exitCode = exitCode;
            _outputContent = outputContent;
        }

        public Task<int> RunAsync(ExternalProcessSpec spec, CancellationToken cancellationToken = default)
        {
            Specs.Add(spec);
            // concat 列表通过文件传给 ffmpeg，而非 stdin——但假执行器不真正启动
            // 进程，列表文件在方法 finally 里被删除。这里在删除前读取列表内容，
            // 供断言验证 file '...' 条目使用绝对路径。
            var listArg = spec.Arguments[spec.Arguments.IndexOf("-i") + 1];
            if (File.Exists(listArg))
                CapturedInput = File.ReadAllText(listArg);
            // 模拟 concat 产出：写出非空产物，满足"产物存在且非空"校验
            var outArg = spec.Arguments[^1];
            File.WriteAllText(outArg, _outputContent);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_exitCode);
        }
    }

    /// <summary>把 concat 列表里的分段真实拼接为产物：让多分段录制循环测试的
    /// 大小校验（产物 ≥ 输入 80%）通过。</summary>
    private sealed class ConcatProcessRunner : IExternalProcessRunner
    {
        public Task<int> RunAsync(ExternalProcessSpec spec, CancellationToken cancellationToken = default)
        {
            var listArg = spec.Arguments[spec.Arguments.IndexOf("-i") + 1];
            var outArg = spec.Arguments[^1];
            using var outFs = new FileStream(outArg, FileMode.Create, FileAccess.Write, FileShare.None);
            foreach (var line in File.ReadAllLines(listArg))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 列表行: file '/abs/path.flv'
                var path = line.Trim();
                if (!path.StartsWith("file '", StringComparison.Ordinal) || !path.EndsWith("'", StringComparison.Ordinal))
                    continue;
                var seg = path["file '".Length..^1].Replace("'\\''", "'");
                if (!File.Exists(seg)) continue;
                using var segFs = File.OpenRead(seg);
                segFs.CopyTo(outFs);
            }
            return Task.FromResult(0);
        }
    }

    /// <summary>
    /// 模拟 B 站直播 API + 流服务器的本地假服务器。
    /// get_info 返回直播间状态；getRoomPlayInfo 返回可脚本化的 playurl（flv 指向本服务器
    /// 的 /stream.flv）；流按连接顺序消费 <see cref="StreamModes"/> 决定行为
    /// （正常 EOF / 中途掐断 / 静默停滞）。记录收到的 play 请求（断言 qn 参数）。
    /// </summary>
    private sealed class FakeLiveServer : IDisposable
    {
        public enum StreamMode
        {
            /// <summary>写 ChunkCount 块数据后正常结束（EOF）。</summary>
            Normal,
            /// <summary>写 ChunkCount 块数据后中途掐断连接（模拟网络断开）。</summary>
            AbortMidStream,
            /// <summary>写 1 块数据后静默挂起（模拟网络黑洞/读停滞）。</summary>
            Stall,
        }

        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly object _sync = new();
        public int Port { get; }

        public Queue<StreamMode> StreamModes { get; } = new();
        public Queue<string> PlayBodies { get; } = new();
        public List<string> PlayRequests { get; } = [];
        public int StreamRequestCount { get; private set; }
        public int StreamBytesWritten { get; private set; }
        public bool IsLive { get; set; } = true;
        /// <summary>已服务多少个流连接后把直播间标记为下播（正常结束录制）。</summary>
        public int OfflineAfterStreams { get; set; } = int.MaxValue;
        public int StreamChunkCount { get; set; } = 3;
        public int StreamChunkBytes { get; set; } = 4096;

        public FakeLiveServer()
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
                        _ = Task.Run(() => HandleAsync(ctx));
                    }
                }
                catch (HttpListenerException) { /* 服务停止 */ }
            });
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url!.AbsolutePath;
                if (path == "/room/v1/Room/get_info")
                {
                    string body = InfoBodyTemplate.Replace("@LIVE@", IsLive ? "1" : "2");
                    await RespondAsync(ctx, body);
                }
                else if (path == "/xlive/web-room/v2/index/getRoomPlayInfo")
                {
                    lock (_sync) PlayRequests.Add(ctx.Request.Url!.Query);
                    string body = PlayBodies.Count > 0
                        ? PlayBodies.Dequeue()
                        : PlayBody(flv: true, qn: 10000);
                    await RespondAsync(ctx, body);
                }
                else if (path == "/stream.flv")
                {
                    int streamNo;
                    lock (_sync)
                    {
                        streamNo = StreamRequestCount + 1;
                        StreamRequestCount = streamNo;
                    }
                    var mode = StreamModes.Count > 0 ? StreamModes.Dequeue() : StreamMode.Normal;
                    var resp = ctx.Response;
                    resp.StatusCode = 200;
                    resp.SendChunked = true;
                    switch (mode)
                    {
                        case StreamMode.Normal:
                            for (int i = 0; i < StreamChunkCount; i++)
                            {
                                await WriteChunkAsync(resp, StreamChunkBytes);
                                await Task.Delay(10, _cts.Token);
                            }
                            resp.Close();
                            break;
                        case StreamMode.AbortMidStream:
                            for (int i = 0; i < StreamChunkCount; i++)
                            {
                                await WriteChunkAsync(resp, StreamChunkBytes);
                                await Task.Delay(10, _cts.Token);
                            }
                            resp.Abort(); // 中途掐断：客户端读流报连接中断
                            break;
                        case StreamMode.Stall:
                            await WriteChunkAsync(resp, StreamChunkBytes);
                            await Task.Delay(Timeout.Infinite, _cts.Token); // 静默挂起
                            break;
                    }
                    if (streamNo >= OfflineAfterStreams) IsLive = false;
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }
            catch { /* 客户端中止/服务停止：忽略 */ }
        }

        private async Task WriteChunkAsync(HttpListenerResponse resp, int bytes)
        {
            var chunk = new byte[bytes];
            await resp.OutputStream.WriteAsync(chunk, _cts.Token);
            lock (_sync) StreamBytesWritten += bytes;
        }

        private static async Task RespondAsync(HttpListenerContext ctx, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }

        /// <summary>等待流服务器已写出至少 1 字节（录制真正开始），超时抛异常。</summary>
        public async Task WaitForStreamBytesAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_sync) { if (StreamBytesWritten > 0) return; }
                await Task.Delay(20);
            }
            throw new TimeoutException("等待假服务器写出流数据超时");
        }

        /// <summary>构造 playurl 响应体：flv 指向本服务器的 /stream.flv（用服务器自身端口）。
        /// 纯 raw string + 占位符替换：JSON 结尾的连续花括号与插值转义冲突，不用插值。</summary>
        public string PlayBody(bool flv, int qn) => flv
            ? PlayUrlBodyTemplate.Replace("@QN@", qn.ToString()).Replace("@PORT@", Port.ToString())
            : HlsOnlyBodyTemplate.Replace("@PORT@", Port.ToString());

        private const string InfoBodyTemplate = """
            {"code":0,"message":"OK","data":{"title":"测试直播","uname":"tester","live_status":@LIVE@}}
            """;

        private const string PlayUrlBodyTemplate = """
            {"code":0,"message":"OK","data":{"playurl_info":{"playurl":{"stream":[
              {"protocol_name":"http_stream","format":[
                {"format_name":"flv","codec":[
                  {"codec_name":"avc","current_qn":@QN@,"base_url":"/stream.flv","url_info":[
                    {"host":"http://127.0.0.1:@PORT@","extra":"","stream_ttl":600}]}]},
                {"format_name":"ts","codec":[
                  {"codec_name":"avc","base_url":"/x.ts","url_info":[
                    {"host":"http://127.0.0.1:@PORT@","extra":""}]}]}
              ]}]}}}}
            """;

        private const string HlsOnlyBodyTemplate = """
            {"code":0,"message":"OK","data":{"playurl_info":{"playurl":{"stream":[
              {"protocol_name":"http_hls","format":[
                {"format_name":"ts","codec":[
                  {"codec_name":"avc","base_url":"/x.ts","url_info":[
                    {"host":"http://127.0.0.1:@PORT@","extra":""}]}]}
              ]}]}}}}
            """;

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
