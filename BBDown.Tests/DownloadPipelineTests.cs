using System.Net;
using System.Security.Cryptography;
using BBDown;
using BBDown.Core;
using static BBDown.Core.Entity.Entity;

namespace BBDown.Tests;

/// <summary>计算字节数组的 SHA-256 十六进制摘要，用于下载产物内容一致性断言。</summary>
internal static class TestHash
{
    public static string ComputeSha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>动态申请一个空闲回环端口：避免多个测试服务器共用同一端口时，
/// HttpClient 连接池复用上一实例的陈旧连接，使请求计数/脚本响应错乱。
/// 进程内去重（Info 级观察）：bind-0 → 取端口 → Stop 之间存在 TOCTOU——
/// 另一个并行测试的 Allocate 可能拿到同一端口，随后 HttpListener.Start 抛
/// AddressAlreadyInUse 使用例偶发失败。用 HashSet 记录本进程已分配端口并跳过复用。</summary>
internal static class TestPort
{
    private static readonly object _gate = new();
    private static readonly HashSet<int> _allocated = new();

    public static int Allocate()
    {
        lock (_gate)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                listener.Start();
                int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                if (_allocated.Add(port)) return port;
            }
            throw new InvalidOperationException("动态端口分配重试 100 次仍重复，测试环境异常");
        }
    }
}

/// <summary>
/// 本类通过 <see cref="BBDownDownloadUtil.ActivePathLockCount"/> 断言全局路径锁字典
/// 会被清理（0 个空闲锁），而该字典是进程级静态状态——其它并行测试类若同时登记
/// 路径锁会让计数非 0，导致断言误失败。串行化本类与既有
/// <see cref="MuxerProcessRunnerCollection"/> 同一模式，保证计数断言稳定。
/// </summary>
[Collection("PathLockCollection")]
public class DownloadPipelineTests
{
    [Fact]
    public void GetAllClips_LastSegment_HasExplicitEndInsteadOfMinusOne()
    {
        var original = Config.Current.ThreadSegmentSizeMb;
        try
        {
            Config.Apply(Config.Current with { ThreadSegmentSizeMb = 1 }); // 1MB 分片
            long per = 1024L * 1024;
            var clips = BBDownDownloadUtil.GetAllClips("http://x", per + 500); // 完整一段 + 500 字节末段
            Assert.Equal(2, clips.Count);
            // 末段不再用 -1：指向文件真实末尾，断点续传的完整性检查（toPosition > 0）才能命中，
            // 否则完整末段会发 Range: bytes=<fileSize>- 触发 416 永久失败
            Assert.Equal(per + 500 - 1, clips[^1].to);
            Assert.All(clips, c => Assert.True(c.to >= c.from));
        }
        finally
        {
            Config.Apply(Config.Current with { ThreadSegmentSizeMb = original });
        }
    }

    [Fact]
    public void CleanStaleClips_RemovesOnlyMatchingPathClips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mineA = Path.Combine(dir, "00000_video.vclip");
            var mineB = Path.Combine(dir, "00001_video.vclip");
            var other = Path.Combine(dir, "00000_other.vclip");
            var audioClip = Path.Combine(dir, "00000_video.aclip"); // 同 stem 的音频轨分片
            var unrelated = Path.Combine(dir, "notes.txt");
            File.WriteAllText(mineA, "a");
            File.WriteAllText(mineB, "b");
            File.WriteAllText(other, "c");
            File.WriteAllText(audioClip, "e");
            File.WriteAllText(unrelated, "d");

            BBDownDownloadUtil.CleanStaleClipsFor(Path.Combine(dir, "video.mp4"));

            Assert.False(File.Exists(mineA));
            Assert.False(File.Exists(mineB));
            Assert.True(File.Exists(other));      // 其他任务的 clip 保留
            Assert.True(File.Exists(audioClip));  // 音频轨分片必须保留
            Assert.True(File.Exists(unrelated));  // 非 clip 文件保留
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CleanStaleClips_Audio_DoesNotDeleteVideoClips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var videoClip = Path.Combine(dir, "00000_video.vclip");
            var audioClip = Path.Combine(dir, "00000_video.aclip");
            File.WriteAllText(videoClip, "v");
            File.WriteAllText(audioClip, "a");

            BBDownDownloadUtil.CleanStaleClipsFor(Path.Combine(dir, "video.m4a"));

            Assert.True(File.Exists(videoClip)); // 视频轨分片保留
            Assert.False(File.Exists(audioClip));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(2, 3, 2)]
    [InlineData(5, 3, 2)]   // 越界 → 钳到末位
    [InlineData(-1, 3, 0)]
    [InlineData(3, 1, 0)]
    [InlineData(0, 0, -1)]  // 无音频 → 标记跳过
    public void ClampRoleAudioIndex_HandlesOutOfRange(int aIndex, int count, int expected)
        => Assert.Equal(expected, Program.ClampRoleAudioIndex(aIndex, count));

    [Fact]
    public void DeleteResidualChapterFiles_RemovesChapterPrefixedFiles()
    {
        // RF-11-D1：跳过/失败路径必须按前缀清理章节 meta 文件——muxer 写唯一名
        // chapters-{basename}（防并发混流互相覆盖），旧清理路径只删固定名 "chapters"，
        // 导致重跑已下载视频时残留 chapters-* 文件。本方法应按前缀匹配两者都清掉，
        // 且不能误删同名前缀之外的正常文件。
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-chapters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var fixedName = Path.Combine(dir, "chapters");
            var uniqueName = Path.Combine(dir, "chapters-abc");
            var otherName = Path.Combine(dir, "subtitle.zh.srt");
            File.WriteAllText(fixedName, "x");
            File.WriteAllText(uniqueName, "x");
            File.WriteAllText(otherName, "x");

            Program.DeleteResidualChapterFiles(dir);

            Assert.False(File.Exists(fixedName), "固定名 chapters 应被清理");
            Assert.False(File.Exists(uniqueName), "muxer 唯一名 chapters-* 应被清理");
            Assert.True(File.Exists(otherName), "非 chapters 前缀文件不得误删");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DeleteResidualChapterFiles_MissingOrEmptyDir_DoesNotThrow()
    {
        // 目录不存在 / 无匹配文件时静默返回：跳过路径兜底清理不能因 IO 异常掩盖主流程结果
        Program.DeleteResidualChapterFiles(Path.Combine(Path.GetTempPath(), "bbdown-no-such-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task MultiThreadDownloadAndMerge_MergesAndCleansClips_UnderLock()
    {
        // 用本地 HTTP 服务提供一段小文件，验证多线程下载在锁内完成"下载→合并→清理"：
        // 目标文件完整、分片全部清除、且路径锁已被释放（不泄漏）。
        using var server = new LocalByteServer(256 * 1024);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        try
        {
            var config = new BBDownDownloadUtil.DownloadConfig { MultiThread = true };
            var original = Config.Current.ThreadSegmentSizeMb;
            try
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = 1 }); // 1MB 分片 → 1 个分片
                await BBDownDownloadUtil.MultiThreadDownloadAndMergeAsync(
                    $"http://127.0.0.1:{server.Port}/file", target, config, CancellationToken.None);
            }
            finally
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = original });
            }

            // 目标文件已合并且内容完整（与服务端字节一致）
            Assert.True(File.Exists(target), "目标文件应已合并生成");
            Assert.Equal(256 * 1024, new FileInfo(target).Length);
            // 内容哈希一致：仅断言长度无法发现"长度正确但内容损坏"（错误内容也能通过）。
            // 必须与服务端的真实载荷逐字节一致，断点续传/分片拼接的任何错位都会反映在哈希上。
            Assert.Equal(server.PayloadHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            // 分片已清理：目录里不应残留 .vclip
            Assert.Empty(Directory.GetFiles(dir, "*.vclip"));
            // 锁已释放
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MultiThreadDownloadAndMerge_MultipleClips_AssembledInOrderByteExact()
    {
        // G7：此前所有 SHA-256 E2E 测试都只产生单 clip（256KB 载荷 / 1MB 分片），
        // 多 clip 的命名/排序/按序拼接（错位/乱序/漏段）无字节级验证。这里用
        // 3 个分片（2.5MB 载荷 + 1MB 分片 → 1MB+1MB+0.5MB），验证：
        //  1) 服务端实际收到 3 段互补、无重叠的 Range（覆盖 [0, size)）——分片正确；
        //  2) 合并产物与服务端载荷逐字节一致（SHA-256）——任意错位/乱序/漏段都会反映。
        using var server = new LocalByteServer(2_500_000);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        try
        {
            var config = new BBDownDownloadUtil.DownloadConfig { MultiThread = true };
            var original = Config.Current.ThreadSegmentSizeMb;
            try
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = 1 }); // 1MB 分片
                await BBDownDownloadUtil.MultiThreadDownloadAndMergeAsync(
                    $"http://127.0.0.1:{server.Port}/file", target, config, CancellationToken.None);
            }
            finally
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = original });
            }

            // 3 个分片：1MB + 1MB + 0.5MB（2 次整片 + 1 次末段）
            Assert.Equal(3, server.RangeHeaders.Count);
            // 3 段互补且不重叠地覆盖 [0, size)：按起始位置排序后，前导起点必须为 0，
            // 每段起点必须接上一段终点+1，末段必须覆盖到文件末尾。
            var ranges = server.RangeHeaders
                .Select(h =>
                {
                    var p = h.Split('=')[1].Split('-');
                    return (From: long.Parse(p[0]), To: long.Parse(p[1]));
                })
                .OrderBy(r => r.From)
                .ToList();
            Assert.Equal(0, ranges[0].From);
            for (int i = 1; i < ranges.Count; i++)
                Assert.Equal(ranges[i - 1].To + 1, ranges[i].From);
            Assert.Equal(2_500_000 - 1, ranges[^1].To);

            // 产物字节级一致：错位/乱序/漏段都会破坏哈希
            Assert.Equal(server.PayloadHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            Assert.Equal(2_500_000, new FileInfo(target).Length);
            // 分片已清理 + 锁已释放
            Assert.Empty(Directory.GetFiles(dir, "*.vclip"));
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MultiThreadDownloadAndMerge_OversizedStaleClip_IsTruncatedNotMerged()
    {
        // 回归：旧分片若比目标分片更长（上次中断留下的超长尾部），不能把截断后"恰好吻合"
        // 的长度当成内容可信——远端内容可能已变化但长度相同，会拼出损坏文件。
        // 正确行为是丢弃既有内容完整重下，产物必须与服务端载荷逐字节一致（哈希校验）。
        using var server = new LocalByteServer(256 * 1024);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        try
        {
            // 预置超长分片：stem=video、扩展名 .mp4 → 分片名 00000_video.vclip。
            // 用与服务端完全不同的随机内容（不是服务端载荷的前缀）——若实现错误地
            // 沿用旧分片尾部，哈希必然失配，直接暴露。
            var clipPath = Path.Combine(dir, "00000_video.vclip");
            var oversized = new byte[512 * 1024]; // 目标 256KB，旧分片 512KB
            new Random(1).NextBytes(oversized);
            await File.WriteAllBytesAsync(clipPath, oversized);

            var config = new BBDownDownloadUtil.DownloadConfig { MultiThread = true };
            var original = Config.Current.ThreadSegmentSizeMb;
            try
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = 1 }); // 1MB 分片 → 1 个分片
                await BBDownDownloadUtil.MultiThreadDownloadAndMergeAsync(
                    $"http://127.0.0.1:{server.Port}/file", target, config, CancellationToken.None);
            }
            finally
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = original });
            }

            // 产物长度等于服务器总长：超长旧分片尾部未被带入
            Assert.True(File.Exists(target), "目标文件应已合并生成");
            Assert.Equal(256 * 1024, new FileInfo(target).Length);
            // 内容哈希与服务端载荷一致：即便旧分片被截断到相同长度，也不允许把旧内容
            // 当成已下载内容（否则会拼出长度正确但内容损坏的文件）
            Assert.Equal(server.PayloadHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            // 分片已清理
            Assert.Empty(Directory.GetFiles(dir, "*.vclip"));
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// 回归：206 响应的 Content-Range 起始偏移与请求偏移不符时，必须丢弃本地内容并
    /// 抛可重试的 IOException，绝不能把错误区间的字节写到本地偏移 0（旧实现如此会
    /// 拼出"长度正确但内容损坏"的文件）。这里预置一个续传偏移，服务器却从错误的
    /// 起始偏移返回内容，验证下载以异常终止且不产生错误内容。
    /// </summary>
    [Fact]
    public async Task MultiThreadDownloadAndMerge_ContentRangeMismatch_ThrowsAndDoesNotProduceCorruptFile()
    {
        using var server = new MisleadingRangeServer(payloadSize: 128 * 1024, wrongOffset: 50 * 1024);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        try
        {
            var config = new BBDownDownloadUtil.DownloadConfig { MultiThread = true };
            var original = Config.Current.ThreadSegmentSizeMb;
            try
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = 1 }); // 1MB 分片 → 1 个分片
                // 服务器始终从错误偏移返回（即使请求从 0 开始）：下载必须失败，而非产出损坏文件
                await Assert.ThrowsAsync<IOException>(() =>
                    BBDownDownloadUtil.MultiThreadDownloadAndMergeAsync(
                        $"http://127.0.0.1:{server.Port}/file", target, config, CancellationToken.None));
            }
            finally
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = original });
            }

            // 失败后不得留下被当成成品的错误内容：目标文件要么不存在，要么是合法的空占位
            if (File.Exists(target))
                Assert.True(new FileInfo(target).Length == 0, "Content-Range 错位失败后不应产出错误内容文件");
            // 锁应已释放
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// 回归：多线程分片身份检查必须命中。预置资源 A 的旧分片（00000_video.vclip）及其
    /// manifest，再下载等长资源 B——若身份检查不命中（通配符错误/首分片缺失绕过），
    /// 旧分片会被按长度拼入新资源，最终哈希 ≠ B 的哈希。此测试用真实 LocalByteServer
    /// 走完整下载，验证产物哈希必须等于服务端 B 载荷。
    /// </summary>
    [Fact]
    public async Task MultiThreadDownloadAndMerge_StaleSegmentFromOtherResource_IsReplacedWithFreshContent()
    {
        using var server = new LocalByteServer(256 * 1024);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        try
        {
            // 预置资源 A 的旧分片（00000_video.vclip，与服务端 B 等长但内容不同）及其清单
            var clip = Path.Combine(dir, "00000_video.vclip");
            var staleBytes = new byte[256 * 1024];
            new Random(3).NextBytes(staleBytes);
            await File.WriteAllBytesAsync(clip, staleBytes);
            var staleManifest = new BBDownDownloadUtil.ResumeManifest(
                BBDownDownloadUtil.StableResourceIdentity("https://cdn.example.com/resourceA.mp4"), 256 * 1024, null, null);
            await File.WriteAllTextAsync(clip + ".manifest.json",
                System.Text.Json.JsonSerializer.Serialize(staleManifest, DownloadManifestJsonContext.Default.ResumeManifest));

            var config = new BBDownDownloadUtil.DownloadConfig { MultiThread = true };
            var original = Config.Current.ThreadSegmentSizeMb;
            try
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = 1 }); // 1MB 分片 → 1 个分片
                await BBDownDownloadUtil.MultiThreadDownloadAndMergeAsync(
                    $"http://127.0.0.1:{server.Port}/file", target, config, CancellationToken.None);
            }
            finally
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = original });
            }

            // 产物哈希必须等于服务端 B：若旧分片被复用（身份检查不命中），哈希会失配
            Assert.True(File.Exists(target));
            Assert.Equal(server.PayloadHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// 回归：视频与音频的临时文件必须隔离。旧实现用 GetFileNameWithoutExtension 生成
    /// .tmp，视频 xxx.mp4 与音频 xxx.m4a 共用 video.tmp——视频中断留下的数据会被音频
    /// 下载当成前缀续传（长度正确但内容损坏）。新实现保留扩展名（video.mp4.tmp /
    /// video.m4a.tmp）。此测试预置一个旧命名的共享 video.tmp 残留，下载音频并验证产物
    /// 与服务端字节一致：若音频误用共享残留作前缀，哈希必然失配。
    /// </summary>
    [Fact]
    public async Task SingleThreadDownload_AudioDoesNotReuseStaleSharedTempFromVideo()
    {
        using var server = new LocalByteServer(64 * 1024);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var videoPath = Path.Combine(dir, "video.mp4");
        var audioPath = Path.Combine(dir, "video.m4a"); // 同 stem，仅扩展名不同
        try
        {
            // 预置旧命名下的共享 .tmp 残留（模拟视频中断留下的数据）
            var staleShared = Path.Combine(dir, "video.tmp");
            var staleBytes = new byte[32 * 1024]; // 音频 64KB 的一半
            new Random(9).NextBytes(staleBytes);
            await File.WriteAllBytesAsync(staleShared, staleBytes);

            // 下载音频（单线程）：新实现用 video.m4a.tmp，忽略共享 video.tmp 残留
            var config = new BBDownDownloadUtil.DownloadConfig();
            await BBDownDownloadUtil.DownloadFileAsync(
                $"http://127.0.0.1:{server.Port}/file", audioPath, config, CancellationToken.None);

            // 音频产物必须与服务端字节一致：若旧实现把视频残留当音频前缀续传，
            // 输出 = 32KB 视频随机 + 32KB 服务端尾部，哈希必然失配
            Assert.True(File.Exists(audioPath));
            Assert.Equal(server.PayloadHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(audioPath)));
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// 回归：断点续传的 .tmp 必须通过资源身份清单校验。等长但 URL 不同的 .tmp
    /// （同一输出路径被另一清晰度/编码资源复用）必须被拒绝，不能仅凭长度采用。
    /// </summary>
    [Fact]
    public void CanResumeFrom_SameLengthButDifferentUrl_RejectsResume()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var tmp = Path.Combine(dir, "video.mp4.tmp");
            File.WriteAllText(tmp, "fake-prefix-content-of-any-length");
            // 写入清单：URL A 与总长，但当前请求是 URL B（同长度不同资源）
            var manifest = new BBDownDownloadUtil.ResumeManifest("https://cdn.example.com/1080p.mp4", 12345, null, null);
            File.WriteAllText(tmp + ".manifest.json",
                System.Text.Json.JsonSerializer.Serialize(manifest, DownloadManifestJsonContext.Default.ResumeManifest));

            // 清单 URL 与当前请求 URL 不同 → 拒绝续传
            Assert.False(BBDownDownloadUtil.CanResumeFrom(tmp, "https://cdn.example.com/720p.mp4", 12345, out var reason));
            Assert.NotNull(reason);
            Assert.Contains("不一致", reason);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// 稳定资源身份必须剥离会刷新的签名 query 参数（deadline/sign/w_rid/ts 等）：
    /// 媒体 URL 的签名每次请求刷新，若用完整 URL 相等，同一资源永远无法跨进程续传。
    /// </summary>
    [Fact]
    public void StableResourceIdentity_StripsSignatureParams_AndKeepsStableOnes()
    {
        var a = "https://upos.example.com/video.mp4?mid=1&deadline=1700000000&sign=abc&wts=1700000000&qn=80";
        var b = "https://upos.example.com/video.mp4?mid=1&deadline=1700000300&sign=def&wts=1700000300&qn=80";
        // 同一资源、刷新签名 → 稳定身份必须相同
        Assert.Equal(BBDownDownloadUtil.StableResourceIdentity(a), BBDownDownloadUtil.StableResourceIdentity(b));
        // 稳定参数（mid/qn）保留，签名参数剥离
        var stable = BBDownDownloadUtil.StableResourceIdentity(a);
        Assert.Contains("mid=1", stable);
        Assert.Contains("qn=80", stable);
        Assert.DoesNotContain("sign=", stable);
        Assert.DoesNotContain("deadline=", stable);
        // 不同资源（不同路径）→ 稳定身份不同
        Assert.NotEqual(
            BBDownDownloadUtil.StableResourceIdentity("https://upos.example.com/other.mp4?mid=1&deadline=1&sign=x"),
            stable);
    }

    /// <summary>
    /// 回归：单线程续传的 .tmp 清单在下载前就已写入（真正中断也带清单可续传）。
    /// 验证 CanResumeFrom 用稳定身份匹配——签名刷新后的同一资源仍可续传。
    /// </summary>
    [Fact]
    public void CanResumeFrom_RefreshedSignature_SameResourceStillResumable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var tmp = Path.Combine(dir, "video.mp4.tmp");
            File.WriteAllText(tmp, "prefix");
            // 清单记录旧签名的 URL（Identity 用稳定身份）
            var manifest = new BBDownDownloadUtil.ResumeManifest(
                BBDownDownloadUtil.StableResourceIdentity("https://cdn.example.com/1080p.mp4?deadline=100&sign=old&qn=80"),
                12345, null, null);
            File.WriteAllText(tmp + ".manifest.json",
                System.Text.Json.JsonSerializer.Serialize(manifest, DownloadManifestJsonContext.Default.ResumeManifest));

            // 当前请求是签名刷新的同一资源 → 稳定身份一致 → 可续传
            Assert.True(BBDownDownloadUtil.CanResumeFrom(tmp,
                "https://cdn.example.com/1080p.mp4?deadline=999&sign=new&qn=80", 12345, out _));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// 回归：完整 .tmp 即便身份与长度都匹配，若服务器 ETag 已变化（同路径内容变化但长度
    /// 不变），也必须拒绝——否则旧 .tmp 被直接采用，产出"长度正确但内容损坏"的文件。
    /// </summary>
    [Fact]
    public void CanResumeFrom_SameLengthAndIdentity_ButChangedETag_RejectsResume()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var tmp = Path.Combine(dir, "video.mp4.tmp");
            File.WriteAllText(tmp, "complete-content");
            // 清单记录旧 ETag：同一资源同一长度，但服务器已返回新 ETag（内容已变）。
            // 清单身份用稳定身份（与 URL 剥离签名参数后的结果一致）。
            var manifest = new BBDownDownloadUtil.ResumeManifest(
                BBDownDownloadUtil.StableResourceIdentity("https://cdn.example.com/video.mp4?deadline=1&sign=old"),
                12345, null, "W/old-etag");
            File.WriteAllText(tmp + ".manifest.json",
                System.Text.Json.JsonSerializer.Serialize(manifest, DownloadManifestJsonContext.Default.ResumeManifest));

            Assert.False(BBDownDownloadUtil.CanResumeFrom(tmp, "https://cdn.example.com/video.mp4?deadline=2&sign=new", 12345, out var reason,
                currentETag: "W/new-etag"));
            Assert.Contains("ETag", reason);
            // 校验器一致时仍可续传
            Assert.True(BBDownDownloadUtil.CanResumeFrom(tmp, "https://cdn.example.com/video.mp4?deadline=2&sign=new", 12345, out _,
                currentETag: "W/old-etag"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// 回归（aria2c --continue=true）：跨资源且长度恰相等的残留 partial 必须被删除重下，
    /// 不得被"已完整下载"跳过——否则残缺文件会直接作为成品进入混流。此前纯 length-only
    /// 跳过先于身份校验执行，等长跨资源残留被误跳过。
    /// </summary>
    [Fact]
    public void PrepareAria2cTarget_CrossResourceEqualLengthPartial_DeletesAndRedownloads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "video.mp4");
            // 残留 partial 恰好 10 字节，与"新资源"的 fileSize 相等（等长跨资源）
            File.WriteAllText(path, "1234567890");
            var control = path + ".aria2";
            File.WriteAllText(control, "ctrl");
            // 清单记录旧资源（1080P）身份；当前请求是另一资源（720P）
            var manifest = new BBDownDownloadUtil.ResumeManifest(
                BBDownDownloadUtil.StableResourceIdentity("https://cdn.example.com/1080p.mp4?qn=80"),
                100, null, null);
            File.WriteAllText(path + ".manifest.json",
                System.Text.Json.JsonSerializer.Serialize(manifest, DownloadManifestJsonContext.Default.ResumeManifest));

            bool skip = BBDownDownloadUtil.PrepareAria2cTarget(
                "https://cdn.example.com/720p.mp4?qn=64", path, fileSize: 10, headers: null, contentHeaders: null);

            Assert.False(skip, "等长跨资源残留不得跳过 aria2c");
            Assert.False(File.Exists(path), "跨资源残留 partial 应被删除");
            Assert.False(File.Exists(control), "残留 .aria2 控制文件应被删除");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>同资源中断（清单身份匹配）：partial 保留供 --continue=true 续传，返回 false。</summary>
    [Fact]
    public void PrepareAria2cTarget_SameResourceInterruptedPartial_KeptForResume()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "video.mp4");
            File.WriteAllText(path, "partial-prefix");
            var control = path + ".aria2";
            File.WriteAllText(control, "ctrl");
            var manifest = new BBDownDownloadUtil.ResumeManifest(
                BBDownDownloadUtil.StableResourceIdentity("https://cdn.example.com/1080p.mp4?deadline=1&sign=old&qn=80"),
                1000, null, null);
            File.WriteAllText(path + ".manifest.json",
                System.Text.Json.JsonSerializer.Serialize(manifest, DownloadManifestJsonContext.Default.ResumeManifest));

            // 签名刷新后的同一资源（稳定身份一致）→ 保留续传
            bool skip = BBDownDownloadUtil.PrepareAria2cTarget(
                "https://cdn.example.com/1080p.mp4?deadline=999&sign=new&qn=80", path, fileSize: 1000, headers: null, contentHeaders: null);

            Assert.False(skip);
            Assert.True(File.Exists(path), "同资源中断的 partial 应保留续传");
            Assert.True(File.Exists(control), "同资源 .aria2 控制文件应保留");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>全新下载（无既有文件）：写入本次身份清单并返回 false（需调 aria2c）。</summary>
    [Fact]
    public void PrepareAria2cTarget_NoPartial_WritesManifestAndReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "video.mp4");
            bool skip = BBDownDownloadUtil.PrepareAria2cTarget(
                "https://cdn.example.com/1080p.mp4?qn=80", path, fileSize: 1000, headers: null, contentHeaders: null);
            Assert.False(skip);
            Assert.True(File.Exists(path + ".manifest.json"), "首次下载前应写入身份清单");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>同资源完整文件：返回 true（跳过 aria2c），残留控制文件被清理、身份清单保留。</summary>
    [Fact]
    public void PrepareAria2cTarget_CompleteSameResource_SkipsAria2c()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "video.mp4");
            File.WriteAllText(path, "complete-10-byte"); // 16 字节
            var control = path + ".aria2";
            File.WriteAllText(control, "ctrl");
            var manifest = new BBDownDownloadUtil.ResumeManifest(
                BBDownDownloadUtil.StableResourceIdentity("https://cdn.example.com/1080p.mp4?qn=80"),
                16, null, null);
            File.WriteAllText(path + ".manifest.json",
                System.Text.Json.JsonSerializer.Serialize(manifest, DownloadManifestJsonContext.Default.ResumeManifest));

            bool skip = BBDownDownloadUtil.PrepareAria2cTarget(
                "https://cdn.example.com/1080p.mp4?qn=80", path, fileSize: 16, headers: null, contentHeaders: null);

            Assert.True(skip, "同资源完整文件应跳过 aria2c");
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(control), "残留 .aria2 控制文件应被清理");
            // 身份清单保留为"完成证书"，供下次重跑经 CanResumeFrom 确认身份后跳过
            Assert.True(File.Exists(path + ".manifest.json"), "完成下载后身份清单应保留");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// 回归：已完整下载但缺身份清单（旧版残留/清单丢失）的文件，不得被纯长度跳过——否则
    /// 无法确认其内容属于当前资源。保守删除重下（安全但浪费，与 CanResumeFrom 缺清单一致）。
    /// </summary>
    [Fact]
    public void PrepareAria2cTarget_CompleteButNoManifest_PurgesAndRedownloads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "video.mp4");
            File.WriteAllText(path, "complete-16-bytes"); // 长度与 fileSize 相等
            var control = path + ".aria2";
            File.WriteAllText(control, "ctrl");
            // 不写清单：模拟旧版下载完成/清单丢失

            bool skip = BBDownDownloadUtil.PrepareAria2cTarget(
                "https://cdn.example.com/1080p.mp4?qn=80", path, fileSize: 17, headers: null, contentHeaders: null);

            Assert.False(skip, "缺清单的完整文件不得跳过（无法确认身份）");
            Assert.False(File.Exists(path), "缺清单的既有文件应被删除重下");
            Assert.False(File.Exists(control), "残留控制文件应被删除");
            Assert.True(File.Exists(path + ".manifest.json"), "删除后应写入当前资源的新清单");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// 身份可信但超长的残留（长度超过远端总长）：内容不可信（越界尾部/资源变化），
    /// 必须删除重下——否则 aria2c --continue 从超出 EOF 的偏移续传可能 416 死循环。
    /// </summary>
    [Fact]
    public void PrepareAria2cTarget_OversizedTrustedPartial_PurgesAndRedownloads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "video.mp4");
            File.WriteAllText(path, "oversized-content-longer-than-remote");
            var control = path + ".aria2";
            File.WriteAllText(control, "ctrl");
            // 身份可信（同资源稳定身份 + 总长匹配），但本地文件长度 > fileSize
            var manifest = new BBDownDownloadUtil.ResumeManifest(
                BBDownDownloadUtil.StableResourceIdentity("https://cdn.example.com/1080p.mp4?qn=80"),
                10, null, null);
            File.WriteAllText(path + ".manifest.json",
                System.Text.Json.JsonSerializer.Serialize(manifest, DownloadManifestJsonContext.Default.ResumeManifest));

            bool skip = BBDownDownloadUtil.PrepareAria2cTarget(
                "https://cdn.example.com/1080p.mp4?qn=80", path, fileSize: 10, headers: null, contentHeaders: null);

            Assert.False(skip);
            Assert.False(File.Exists(path), "超长残留应被删除重下");
            Assert.False(File.Exists(control), "残留控制文件应被删除");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>返回错误 Content-Range 起始偏移的本地服务：验证下载必须拒绝而非接受错位区间。</summary>
    /// <summary>
    /// 模拟 CDN 黑洞/半死 TCP：HEAD 正常返回 Content-Length，GET 声明完整长度
    /// 但只写一小段后保持连接打开不再发数据——读停滞看门狗必须识破而不是永久挂起。
    /// </summary>
    private sealed class StallingServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly byte[] _payload;
        private readonly Task _loop;
        public int Port { get; }

        public StallingServer(int size)
        {
            _payload = new byte[size];
            new Random(11).NextBytes(_payload);
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
                            var resp = ctx.Response;
                            if (ctx.Request.HttpMethod == "HEAD")
                            {
                                resp.StatusCode = 200;
                                resp.ContentLength64 = _payload.Length;
                                resp.Close();
                                continue;
                            }
                            // GET：声明完整长度，只写 1KB 后停滞（连接保持打开不 EOF）——
                            // 模拟 CDN 发完响应头后停发数据。客户端看门狗超时后中止连接，
                            // 此处 WriteAsync/Close 抛异常被忽略。
                            resp.StatusCode = 200;
                            resp.ContentLength64 = _payload.Length;
                            await resp.OutputStream.WriteAsync(_payload.AsMemory(0, 1024), _cts.Token);
                            await resp.OutputStream.FlushAsync(_cts.Token);
                            try { await Task.Delay(Timeout.Infinite, _cts.Token); }
                            catch (OperationCanceledException) { /* 服务停止 */ }
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

    /// <summary>
    /// VOD 媒体流读停滞看门狗：ResponseHeadersRead 之后 HttpClient.Timeout 不约束响应体
    /// 读取，无看门狗时黑洞连接会让 ReadAsync 永久挂起（serve 模式钉死并发槽）。
    /// 看门狗超时抛可重试 IOException，重试预算耗尽后向上传播。
    /// </summary>
    [Fact]
    public async Task DownloadFile_StalledBody_WatchdogThrowsRetryableIOException()
    {
        var originalStall = BBDownDownloadUtil.MediaReadStallTimeout;
        var originalRetry = Config.Current.MaxRetryCount;
        var originalDelay = Config.Current.RetryDelayMs;
        BBDownDownloadUtil.MediaReadStallTimeout = TimeSpan.FromMilliseconds(300);
        // 缩短重试预算与退避：看门狗抛 IOException 走重试链，预算耗尽后异常向上传播——
        // 测试只关心看门狗触发，不必等待完整默认重试序列
        Config.Apply(Config.Current with { MaxRetryCount = 1, RetryDelayMs = 10 });
        try
        {
            using var server = new StallingServer(64 * 1024);
            var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "stalled.mp4");
            try
            {
                var ex = await Assert.ThrowsAsync<IOException>(() =>
                    BBDownDownloadUtil.DownloadFileAsync(
                        $"http://127.0.0.1:{server.Port}/file", target,
                        new BBDownDownloadUtil.DownloadConfig(), CancellationToken.None));
                Assert.Contains("停滞", ex.Message);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
        finally
        {
            BBDownDownloadUtil.MediaReadStallTimeout = originalStall;
            Config.Apply(Config.Current with { MaxRetryCount = originalRetry, RetryDelayMs = originalDelay });
        }
    }

    private sealed class MisleadingRangeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly byte[] _payload;
        private readonly long _wrongOffset;
        private readonly Task _loop;
        public int Port { get; }

        public MisleadingRangeServer(int payloadSize, long wrongOffset)
        {
            _payload = new byte[payloadSize];
            new Random(7).NextBytes(_payload);
            _wrongOffset = wrongOffset;
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
                            var resp = ctx.Response;
                            // HEAD：探测只取 Content-Length，不写响应体（HttpListener 对 HEAD
                            // 会抑制 body，这里显式分支使行为确定，也避免 256KB body 写向不读的客户端）
                            if (ctx.Request.HttpMethod == "HEAD")
                            {
                                resp.StatusCode = 200;
                                resp.ContentLength64 = _payload.Length;
                                resp.Close();
                                continue;
                            }
                            var rangeHeader = ctx.Request.Headers["Range"];
                            if (string.IsNullOrEmpty(rangeHeader))
                            {
                                resp.StatusCode = 200;
                                resp.ContentLength64 = _payload.Length;
                                await resp.OutputStream.WriteAsync(_payload, _cts.Token);
                            }
                            else
                            {
                                // 故意返回与请求不符的起始偏移：声明 bytes {wrongOffset}- ，
                                // 但实际写入的内容从错误位置开始——正常客户端必须拒绝此响应
                                long count = _payload.Length - _wrongOffset;
                                resp.StatusCode = 206;
                                resp.ContentLength64 = count;
                                resp.AddHeader("Content-Range", $"bytes {_wrongOffset}-{_payload.Length - 1}/{_payload.Length}");
                                await resp.OutputStream.WriteAsync(_payload.AsMemory((int)_wrongOffset, (int)count), _cts.Token);
                            }
                            resp.Close();
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

    /// <summary>本地 HTTP 服务，返回固定长度的字节流（用于多线程下载测试）。</summary>
    private sealed class LocalByteServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly byte[] _payload;
        private readonly Task _loop;
        public int Port { get; }

        /// <summary>服务端载荷的 SHA-256 十六进制串。测试用它校验下载产物内容一致（而非仅长度）。</summary>
        public string PayloadHash { get; }

        /// <summary>已服务的 Range 请求区间（G7 验证多分片覆盖时用）。锁保护。</summary>
        public List<string> RangeHeaders { get; } = [];

        public LocalByteServer(int size)
        {
            _payload = new byte[size];
            new Random(42).NextBytes(_payload);
            PayloadHash = TestHash.ComputeSha256Hex(_payload);
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
                            var resp = ctx.Response;
                            // HEAD：探测只取 Content-Length，不写响应体（见 MisleadingRangeServer 同款分支）
                            if (ctx.Request.HttpMethod == "HEAD")
                            {
                                resp.StatusCode = 200;
                                resp.ContentLength64 = _payload.Length;
                                resp.Close();
                                continue;
                            }
                            var rangeHeader = ctx.Request.Headers["Range"];
                            // 支持 Range 请求：响应 206 分段
                            if (ctx.Request.HttpMethod == "GET" && !string.IsNullOrEmpty(rangeHeader))
                            {
                                lock (RangeHeaders) RangeHeaders.Add(rangeHeader);
                            }
                            if (string.IsNullOrEmpty(rangeHeader))
                            {
                                resp.StatusCode = 200;
                                resp.ContentLength64 = _payload.Length;
                                await resp.OutputStream.WriteAsync(_payload, _cts.Token);
                            }
                            else
                            {
                                // 解析 "bytes=from-to"（-1 表示到末尾）
                                var range = rangeHeader.Replace("bytes=", "").Split('-');
                                var from = int.Parse(range[0]);
                                var to = range.Length > 1 && range[1] != "" ? int.Parse(range[1]) : _payload.Length - 1;
                                var count = to - from + 1;
                                resp.StatusCode = 206;
                                resp.ContentLength64 = count;
                                resp.AddHeader("Content-Range", $"bytes {from}-{to}/{_payload.Length}");
                                await resp.OutputStream.WriteAsync(_payload.AsMemory(from, count), _cts.Token);
                            }
                            resp.Close();
                        }
                        catch
                        {
                            // 客户端中止：忽略
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
    public async Task RunWithPathLockAsync_SamePath_SerializesProducers()
    {
        // 模拟 serve 下两个同标题任务写同一个最终路径：路径锁必须把"生产最终文件"
        // 串行化，避免后写者覆盖先写者。两个生产者各自写入后再整体校验文件内容。
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "same-title.mp4");
        try
        {
            // 两个生产者并发进入，但同一路径的锁会让它们串行执行
            var tasks = new[]
            {
                BBDownDownloadUtil.RunWithPathLockAsync(target, async () =>
                {
                    await Task.Delay(30);
                    await File.WriteAllTextAsync(target, "producer-A");
                    return true;
                }),
                BBDownDownloadUtil.RunWithPathLockAsync(target, async () =>
                {
                    await Task.Delay(10);
                    await File.WriteAllTextAsync(target, "producer-B");
                    return true;
                }),
            };
            await Task.WhenAll(tasks);
            // 最后写入者的内容完整保留（没有被并发截断/交错）
            var content = await File.ReadAllTextAsync(target);
            Assert.True(content is "producer-A" or "producer-B", $"最终内容应为某个生产者完整写入，实际: {content}");
            // 锁字典应已清理：serve 长驻进程不能因路径锁累积内存
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RunWithPathLockAsync_DifferentPaths_RunConcurrently()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 记录每个任务的实际执行区间，断言两区间重叠（并行）——替代墙钟上限断言
            // （elapsed < 180ms）：CI 调度抖动会放大总耗时造成间歇假失败（G5），
            // 而区间重叠只依赖两任务相对时序，与绝对耗时无关，稳定得多。
            var startedAt = new long[2];
            var finishedAt = new long[2];
            long now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var tasks = new[]
            {
                BBDownDownloadUtil.RunWithPathLockAsync(Path.Combine(dir, "a.mp4"), async () =>
                {
                    startedAt[0] = now();
                    await Task.Delay(100);
                    finishedAt[0] = now();
                    return true;
                }),
                BBDownDownloadUtil.RunWithPathLockAsync(Path.Combine(dir, "b.mp4"), async () =>
                {
                    startedAt[1] = now();
                    await Task.Delay(100);
                    finishedAt[1] = now();
                    return true;
                }),
            };
            await Task.WhenAll(tasks);
            // 不同路径不互斥：两任务的执行区间必须重叠（a 开始后 b 才结束，反之亦然）。
            // 若锁错误地互斥了两个路径，区间将严格串行、永不重叠。
            Assert.True(startedAt[0] <= finishedAt[1] && startedAt[1] <= finishedAt[0],
                $"不同路径应并行执行（区间重叠），实际 a=[{startedAt[0]}..{finishedAt[0]}] b=[{startedAt[1]}..{finishedAt[1]}]");
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// C3：探测合并。整个"下载→合并"链路只在顶层探测一次（HEAD 优先，不传输响应体、
    /// 连接可直接回池），结果下传各层，不再每层重复 GetFileSizeAndHeadersAsync。
    /// 旧实现 MultiThreadDownloadAndMergeAsync 与 Core 各探测一次，每个文件多出 2-3 次往返。
    /// 断言：一次 HEAD（探测），零 GET 探测，一次 Range 分片下载。
    /// </summary>
    [Fact]
    public async Task MultiThreadDownloadAndMerge_ProbesOnce_NotPerLayer()
    {
        using var server = new ProbeCountingServer(256 * 1024);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        try
        {
            var config = new BBDownDownloadUtil.DownloadConfig { MultiThread = true };
            var original = Config.Current.ThreadSegmentSizeMb;
            try
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = 1 }); // 1MB 分片 → 1 个分片
                await BBDownDownloadUtil.MultiThreadDownloadAndMergeAsync(
                    $"http://127.0.0.1:{server.Port}/file", target, config, CancellationToken.None);
            }
            finally
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = original });
            }

            Assert.True(File.Exists(target), "目标文件应已合并生成");
            Assert.Equal(server.PayloadHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            // 探测合并：整条链路只发 1 次 HEAD 探测、0 次 GET 探测（旧实现是 2+ 次 GET 探测）；
            // 256KB 文件在 1MB 分片下只产生 1 个 Range 分片请求。
            Assert.Equal(1, server.HeadCount);
            Assert.Equal(0, server.GetCount);
            Assert.Equal(1, server.RangeCount);
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// C3/F6 回归：HEAD 探测返回的 size 若与真实资源总长不符（CDN 对 HEAD 返回缓存/占位长度），
    /// 分片会按错误大小切割、静默产出截断文件——合并长度校验用的是同一个 HEAD 值，是
    /// 自引用比较拦不住。修复：每个分片的 206 响应 Content-Range 尾段（/total）交叉校验，
    /// total != 探测 size 即抛 RemoteSizeMismatchException（携带权威总长），按权威总长重新
    /// 切分下载，而不是用错误大小空转重试后失败。此测试断言 HEAD 声称 100 字节、真实
    /// 256KB 时下载**成功**且产物逐字节等于服务端载荷（而非"长度 100 但内容截断"的成品）。
    /// </summary>
    [Fact]
    public async Task MultiThreadDownloadAndMerge_HeadSizeMismatch_RepairedViaContentRangeTotal()
    {
        using var server = new MismatchedHeadSizeServer(payloadSize: 256 * 1024, headClaimedSize: 100);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        try
        {
            var config = new BBDownDownloadUtil.DownloadConfig { MultiThread = true };
            var original = Config.Current.ThreadSegmentSizeMb;
            var originalDelay = Config.Current.RetryDelayMs;
            try
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = 1, RetryDelayMs = 10 });
                await BBDownDownloadUtil.MultiThreadDownloadAndMergeAsync(
                    $"http://127.0.0.1:{server.Port}/file", target, config, CancellationToken.None);
            }
            finally
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = original, RetryDelayMs = originalDelay });
            }

            // 关键：不得产出被当作成品的截断文件（长度恰好等于错误的 HEAD size）；
            // 修复后必须得到与服务端载荷逐字节一致的完整产物。
            Assert.True(File.Exists(target), "HEAD 大小错位应被修复为正确产物而非失败");
            Assert.Equal(256 * 1024, new FileInfo(target).Length);
            Assert.Equal(server.PayloadHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// F6 单线程：HEAD 探测返回错误的（偏小）长度时，下载不应用错误大小空转重试——
    /// 首个 206 的 Content-Range 权威总长被捕获后修正，产物与服务端载荷逐字节一致。
    /// </summary>
    [Fact]
    public async Task SingleThreadDownload_HeadSizeMismatch_RepairedViaContentRangeTotal()
    {
        using var server = new MismatchedHeadSizeServer(payloadSize: 256 * 1024, headClaimedSize: 100);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        try
        {
            var config = new BBDownDownloadUtil.DownloadConfig();
            var originalDelay = Config.Current.RetryDelayMs;
            try
            {
                Config.Apply(Config.Current with { RetryDelayMs = 10 });
                await BBDownDownloadUtil.DownloadFileAsync(
                    $"http://127.0.0.1:{server.Port}/file", target, config, CancellationToken.None);
            }
            finally
            {
                Config.Apply(Config.Current with { RetryDelayMs = originalDelay });
            }

            Assert.True(File.Exists(target), "HEAD 大小错位应被修复为正确产物而非失败");
            Assert.Equal(server.PayloadHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// F1 单线程回归：HEAD 探测返回错误的（偏小）长度、且目标路径已存在等长陈旧文件时，
    /// "文件已下载过, 跳过下载"不得误跳过——否则陈旧/截断文件被报为下载成功。
    /// 修复：跳过前用 GET 权威总长复核，不符即删除重下，最终产物等于服务端载荷。
    /// </summary>
    [Fact]
    public async Task SingleThreadDownload_HeadSizeMismatchStaleFile_IsRedownloaded()
    {
        using var server = new MismatchedHeadSizeServer(payloadSize: 256 * 1024, headClaimedSize: 100);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        try
        {
            // 预置等长陈旧文件（长度恰好等于错误的 HEAD size）：此前会被纯长度跳过误报为成功
            File.WriteAllBytes(target, new byte[100]);
            var config = new BBDownDownloadUtil.DownloadConfig();
            var originalDelay = Config.Current.RetryDelayMs;
            try
            {
                Config.Apply(Config.Current with { RetryDelayMs = 10 });
                await BBDownDownloadUtil.DownloadFileAsync(
                    $"http://127.0.0.1:{server.Port}/file", target, config, CancellationToken.None);
            }
            finally
            {
                Config.Apply(Config.Current with { RetryDelayMs = originalDelay });
            }

            Assert.True(File.Exists(target), "陈旧文件应被删除并以正确内容重下");
            Assert.Equal(server.PayloadHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// F1 多线程回归：同 <see cref="SingleThreadDownload_HeadSizeMismatchStaleFile_IsRedownloaded"/>，
    /// 但走多线程下载+合并链路。
    /// </summary>
    [Fact]
    public async Task MultiThreadDownloadAndMerge_HeadSizeMismatchStaleFile_IsRedownloaded()
    {
        using var server = new MismatchedHeadSizeServer(payloadSize: 256 * 1024, headClaimedSize: 100);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        try
        {
            File.WriteAllBytes(target, new byte[100]);
            var config = new BBDownDownloadUtil.DownloadConfig { MultiThread = true };
            var original = Config.Current.ThreadSegmentSizeMb;
            var originalDelay = Config.Current.RetryDelayMs;
            try
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = 1, RetryDelayMs = 10 });
                await BBDownDownloadUtil.MultiThreadDownloadAndMergeAsync(
                    $"http://127.0.0.1:{server.Port}/file", target, config, CancellationToken.None);
            }
            finally
            {
                Config.Apply(Config.Current with { ThreadSegmentSizeMb = original, RetryDelayMs = originalDelay });
            }

            Assert.True(File.Exists(target), "陈旧文件应被删除并以正确内容重下");
            Assert.Equal(server.PayloadHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// F1 正向：目标路径已有与服务器同长（HEAD 探测正确）的完整文件时，跳过下载且不覆盖
    /// 既有内容。此前纯长度跳过已覆盖此场景；此处确认权威复核在 HEAD 正确时不误删重下。
    /// </summary>
    [Fact]
    public async Task SingleThreadDownload_ExistingCompleteFile_IsSkippedWithoutRedownload()
    {
        using var server = new LocalByteServer(256 * 1024);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        try
        {
            // 预置与服务端载荷同长的既有文件（内容不同）：用于断言跳过时不被覆盖
            var existing = new byte[256 * 1024];
            new Random(77).NextBytes(existing);
            await File.WriteAllBytesAsync(target, existing);
            var existingHash = TestHash.ComputeSha256Hex(existing);

            var config = new BBDownDownloadUtil.DownloadConfig();
            await BBDownDownloadUtil.DownloadFileAsync(
                $"http://127.0.0.1:{server.Port}/file", target, config, CancellationToken.None);

            // 已完整下载过 → 跳过，既有内容保持不变（未被覆盖）
            Assert.Equal(existingHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// F1 回归：权威总长复核遇到"无法确认总长"的响应（分块、无 Content-Length）时，
    /// 必须退回纯长度跳过、**不得删除**既有完整文件——否则每次重跑都会误删重下
    ///（曾因 ContentLength ?? 0 把有效文件误判为不符而删除）。
    /// </summary>
    [Fact]
    public async Task SingleThreadDownload_UnknownAuthoritativeSize_DoesNotDeleteExistingFile()
    {
        using var server = new ChunkedNoLengthServer(256 * 1024);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        try
        {
            var existing = new byte[256 * 1024];
            new Random(99).NextBytes(existing);
            await File.WriteAllBytesAsync(target, existing);
            var existingHash = TestHash.ComputeSha256Hex(existing);

            var config = new BBDownDownloadUtil.DownloadConfig();
            await BBDownDownloadUtil.DownloadFileAsync(
                $"http://127.0.0.1:{server.Port}/file", target, config, CancellationToken.None);

            // 复核无法确认总长 → 跳过，既有文件未被删除/覆盖
            Assert.Equal(existingHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// F1 回归：权威总长复核遇到 206 但 Content-Range 无尾段总长（bytes 0-0/*）时，
    /// 必须返回 null 退回纯长度跳过、不得删除既有文件——部分响应的 Content-Length（1 字节）
    /// 曾被视为权威总长而误删有效文件。
    /// </summary>
    [Fact]
    public async Task SingleThreadDownload_UnknownTotal206_DoesNotDeleteExistingFile()
    {
        using var server = new UnknownTotalRangeServer(256 * 1024);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        try
        {
            var existing = new byte[256 * 1024];
            new Random(98).NextBytes(existing);
            await File.WriteAllBytesAsync(target, existing);
            var existingHash = TestHash.ComputeSha256Hex(existing);

            var config = new BBDownDownloadUtil.DownloadConfig();
            await BBDownDownloadUtil.DownloadFileAsync(
                $"http://127.0.0.1:{server.Port}/file", target, config, CancellationToken.None);

            // 206 未知总长 → 无法确认 → 跳过，既有文件未被删除/覆盖
            Assert.Equal(existingHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// F1 回归：HEAD 返回占位（偏小）长度且服务器无视 Range（GET 一律 200 全量）时，
    /// 删除陈旧文件后必须按权威总长（200 Content-Length）继续下载，否则会用占位长度
    /// 一路带进 expectedTotalSize 空转并永久失败（written != fileSize）。
    /// </summary>
    [Fact]
    public async Task SingleThreadDownload_PlaceholderHead_DeletesStaleFile_AndRedownloadsCorrectSize()
    {
        using var server = new PlaceholderHeadServer(payloadSize: 256 * 1024, headClaimedSize: 100);
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "video.mp4");
        try
        {
            // 陈旧文件（长度=占位 HEAD 声称值）：复核发现权威大小不符后删除重下
            File.WriteAllBytes(target, new byte[100]);
            var config = new BBDownDownloadUtil.DownloadConfig();
            var originalDelay = Config.Current.RetryDelayMs;
            try
            {
                Config.Apply(Config.Current with { RetryDelayMs = 10 });
                await BBDownDownloadUtil.DownloadFileAsync(
                    $"http://127.0.0.1:{server.Port}/file", target, config, CancellationToken.None);
            }
            finally
            {
                Config.Apply(Config.Current with { RetryDelayMs = originalDelay });
            }

            Assert.True(File.Exists(target), "陈旧文件应被删除并以正确内容重下");
            Assert.Equal(server.PayloadHash, TestHash.ComputeSha256Hex(await File.ReadAllBytesAsync(target)));
            Assert.Equal(0, BBDownDownloadUtil.ActivePathLockCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>统计 HEAD/GET/Range 请求次数的本地服务：验证探测合并只探测一次。</summary>
    private sealed class ProbeCountingServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly byte[] _payload;
        private readonly Task _loop;
        public int Port { get; }
        public int HeadCount;
        public int GetCount;
        public int RangeCount;
        public string PayloadHash { get; }

        public ProbeCountingServer(int size)
        {
            _payload = new byte[size];
            new Random(42).NextBytes(_payload);
            PayloadHash = TestHash.ComputeSha256Hex(_payload);
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
                            var resp = ctx.Response;
                            if (ctx.Request.HttpMethod == "HEAD")
                            {
                                Interlocked.Increment(ref HeadCount);
                                resp.StatusCode = 200;
                                resp.ContentLength64 = _payload.Length;
                                resp.Close();
                                continue;
                            }
                            var rangeHeader = ctx.Request.Headers["Range"];
                            if (string.IsNullOrEmpty(rangeHeader))
                            {
                                Interlocked.Increment(ref GetCount);
                                resp.StatusCode = 200;
                                resp.ContentLength64 = _payload.Length;
                                await resp.OutputStream.WriteAsync(_payload, _cts.Token);
                            }
                            else
                            {
                                Interlocked.Increment(ref RangeCount);
                                var range = rangeHeader.Replace("bytes=", "").Split('-');
                                var from = int.Parse(range[0]);
                                var to = range.Length > 1 && range[1] != "" ? int.Parse(range[1]) : _payload.Length - 1;
                                var count = to - from + 1;
                                resp.StatusCode = 206;
                                resp.ContentLength64 = count;
                                resp.AddHeader("Content-Range", $"bytes {from}-{to}/{_payload.Length}");
                                await resp.OutputStream.WriteAsync(_payload.AsMemory(from, count), _cts.Token);
                            }
                            resp.Close();
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

    /// <summary>HEAD 声称错误（偏小）大小、GET/206 返回真实大小的本地服务：
    /// 模拟 CDN 对 HEAD 返回缓存/占位长度，验证 Content-Range 尾段交叉校验能拦下截断。</summary>
    private sealed class MismatchedHeadSizeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly byte[] _payload;
        private readonly long _headClaimedSize;
        private readonly Task _loop;
        public int Port { get; }

        /// <summary>服务端载荷的 SHA-256 十六进制串：用于断言修复后的产物内容一致。</summary>
        public string PayloadHash { get; }

        public MismatchedHeadSizeServer(int payloadSize, long headClaimedSize)
        {
            _payload = new byte[payloadSize];
            new Random(5).NextBytes(_payload);
            PayloadHash = TestHash.ComputeSha256Hex(_payload);
            _headClaimedSize = headClaimedSize;
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
                            var resp = ctx.Response;
                            if (ctx.Request.HttpMethod == "HEAD")
                            {
                                // 错误的（偏小）Content-Length：探测据此切分，若未交叉校验会静默截断
                                resp.StatusCode = 200;
                                resp.ContentLength64 = _headClaimedSize;
                                resp.Close();
                                continue;
                            }
                            var rangeHeader = ctx.Request.Headers["Range"];
                            if (string.IsNullOrEmpty(rangeHeader))
                            {
                                resp.StatusCode = 200;
                                resp.ContentLength64 = _payload.Length;
                                await resp.OutputStream.WriteAsync(_payload, _cts.Token);
                            }
                            else
                            {
                                var range = rangeHeader.Replace("bytes=", "").Split('-');
                                var from = int.Parse(range[0]);
                                var to = range.Length > 1 && range[1] != "" ? int.Parse(range[1]) : _payload.Length - 1;
                                var count = to - from + 1;
                                resp.StatusCode = 206;
                                resp.ContentLength64 = count;
                                // 关键：Content-Range 尾段用真实总长（≠ HEAD 声称的偏小值）
                                resp.AddHeader("Content-Range", $"bytes {from}-{to}/{_payload.Length}");
                                await resp.OutputStream.WriteAsync(_payload.AsMemory(from, count), _cts.Token);
                            }
                            resp.Close();
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

    /// <summary>HEAD 返回正常长度、GET 返回分块（无 Content-Length）的本地服务：
    /// 验证权威总长复核在"无法确认总长"时退回纯长度跳过、不删除既有文件。</summary>
    private sealed class ChunkedNoLengthServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly byte[] _payload;
        private readonly Task _loop;
        public int Port { get; }

        public ChunkedNoLengthServer(int size)
        {
            _payload = new byte[size];
            new Random(88).NextBytes(_payload);
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
                            var resp = ctx.Response;
                            if (ctx.Request.HttpMethod == "HEAD")
                            {
                                resp.StatusCode = 200;
                                resp.ContentLength64 = _payload.Length;
                                resp.Close();
                                continue;
                            }
                            // GET：分块响应（不设 Content-Length）→ 客户端无法确认权威总长
                            resp.StatusCode = 200;
                            resp.SendChunked = true;
                            await resp.OutputStream.WriteAsync(_payload, _cts.Token);
                            resp.Close();
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

    /// <summary>HEAD 返回正常长度、GET Range 返回 206 但 Content-Range 无尾段总长（bytes 0-0/*）的本地服务：
    /// 验证权威总长复核在"206 未知总长"时返回 null、不删除既有文件（部分响应的 Content-Length
    /// 只是分片大小，不能当权威总长）。</summary>
    private sealed class UnknownTotalRangeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly byte[] _payload;
        private readonly Task _loop;
        public int Port { get; }

        public UnknownTotalRangeServer(int size)
        {
            _payload = new byte[size];
            new Random(77).NextBytes(_payload);
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
                            var resp = ctx.Response;
                            if (ctx.Request.HttpMethod == "HEAD")
                            {
                                resp.StatusCode = 200;
                                resp.ContentLength64 = _payload.Length;
                                resp.Close();
                                continue;
                            }
                            var rangeHeader = ctx.Request.Headers["Range"];
                            if (string.IsNullOrEmpty(rangeHeader))
                            {
                                resp.StatusCode = 200;
                                resp.ContentLength64 = _payload.Length;
                                await resp.OutputStream.WriteAsync(_payload, _cts.Token);
                            }
                            else
                            {
                                // 206 但 Content-Range 无尾段总长（bytes 0-0/*）：权威总长未知
                                resp.StatusCode = 206;
                                resp.ContentLength64 = 1;
                                resp.AddHeader("Content-Range", "bytes 0-0/*");
                                await resp.OutputStream.WriteAsync(_payload.AsMemory(0, 1), _cts.Token);
                            }
                            resp.Close();
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

    /// <summary>HEAD 返回占位（偏小）长度、GET 无视 Range 一律返回 200 全量的本地服务：
    /// 验证 F1 删除陈旧文件后按权威总长（200 Content-Length）继续下载，而非用占位长度空转失败。</summary>
    private sealed class PlaceholderHeadServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly byte[] _payload;
        private readonly long _headClaimedSize;
        private readonly Task _loop;
        public int Port { get; }
        public string PayloadHash { get; }

        public PlaceholderHeadServer(int payloadSize, long headClaimedSize)
        {
            _payload = new byte[payloadSize];
            new Random(123).NextBytes(_payload);
            PayloadHash = TestHash.ComputeSha256Hex(_payload);
            _headClaimedSize = headClaimedSize;
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
                            var resp = ctx.Response;
                            if (ctx.Request.HttpMethod == "HEAD")
                            {
                                // 占位（错误、偏小）长度：探测据此会切错/带错
                                resp.StatusCode = 200;
                                resp.ContentLength64 = _headClaimedSize;
                                resp.Close();
                                continue;
                            }
                            // GET 一律 200 全量（无视 Range），Content-Length 是真实长度
                            resp.StatusCode = 200;
                            resp.ContentLength64 = _payload.Length;
                            await resp.OutputStream.WriteAsync(_payload, _cts.Token);
                            resp.Close();
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

[CollectionDefinition("PathLockCollection", DisableParallelization = true)]
public class PathLockCollection
{
}
