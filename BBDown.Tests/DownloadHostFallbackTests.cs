using System.Net;
using BBDown;
using BBDown.Core.Entity;
using static BBDown.Core.Entity.Entity;

namespace BBDown.Tests;

/// <summary>
/// HandlePcdn 替换 host 后的 404 回退：替换目标（备用镜像/手动 CDN）不承载某些内容时，
/// 下载必须改用替换前的原始地址重试一次，而不是让整页下载失败。生产实测：大会员影片的
/// APP 接口流被换成 upos-sz-mirrorcoso1.bilivideo.com 后整片 404，且页面级重试每轮都
/// 重新替换、永不恢复，最终"共 1 个分P下载失败"。
/// </summary>
public class DownloadHostFallbackTests
{
    private const string OriginalHost = "cn-bj-fx-01-01.bilivideo.com";
    private const string BackupHost = "upos-sz-mirrorcoso1.bilivideo.com";

    private static HttpRequestException NotFound() =>
        new("Response status code does not indicate success: 404 (Not Found).", null, HttpStatusCode.NotFound);

    // ---------- 回退判定真值矩阵 ----------

    [Fact]
    public void Fallback_NotFound_AndHostWasReplaced()
        => Assert.True(DownloadPageExecutor.ShouldFallbackToOriginalHost(
            $"https://{OriginalHost}/v.m4s", $"https://{BackupHost}/v.m4s", NotFound()));

    [Fact]
    public void Fallback_NotFound_ButHostUnchanged()
        => Assert.False(DownloadPageExecutor.ShouldFallbackToOriginalHost(
            $"https://{OriginalHost}/v.m4s", $"https://{OriginalHost}/v.m4s", NotFound()));

    [Fact]
    public void Fallback_NotFound_ButOriginalMissing()
        => Assert.False(DownloadPageExecutor.ShouldFallbackToOriginalHost(
            null, $"https://{BackupHost}/v.m4s", NotFound()));

    [Fact]
    public void Fallback_OtherStatus_NoFallback()
        => Assert.False(DownloadPageExecutor.ShouldFallbackToOriginalHost(
            $"https://{OriginalHost}/v.m4s", $"https://{BackupHost}/v.m4s",
            new HttpRequestException("server error", null, HttpStatusCode.InternalServerError)));

    [Fact]
    public void Fallback_NoStatusCode_NoFallback()
        => Assert.False(DownloadPageExecutor.ShouldFallbackToOriginalHost(
            $"https://{OriginalHost}/v.m4s", $"https://{BackupHost}/v.m4s",
            new HttpRequestException("connection reset")));

    [Fact]
    public void Fallback_NonHttpException_NoFallback()
        => Assert.False(DownloadPageExecutor.ShouldFallbackToOriginalHost(
            $"https://{OriginalHost}/v.m4s", $"https://{BackupHost}/v.m4s",
            new IOException("disk")));

    // ---------- 页面下载编排接线 ----------

    /// <summary>
    /// 视频/音频均被替换且替换目标 404：各自回退到原始地址并成功，下载顺序为
    /// [视频@替换, 视频@原始, 音频@替换, 音频@原始]，页面结果为成功。
    /// </summary>
    [Fact]
    public async Task ReplacedHost404_FallsBackToOriginal_PerTrack_AndSucceeds()
    {
        var (executor, context, urls) = BuildPage(url =>
            url.Contains(BackupHost, StringComparison.Ordinal) ? NotFound() : null);

        var result = await executor.DownloadDashPageAsync(context, 0, 0);

        Assert.True(result);
        Assert.Equal(new[]
        {
            $"https://{BackupHost}/v.m4s",
            $"https://{OriginalHost}/v.m4s",
            $"https://{BackupHost}/a.m4s",
            $"https://{OriginalHost}/a.m4s",
        }, urls);
    }

    /// <summary>
    /// 替换目标与原地址都 404：回退只发生一次，第二次失败按原路径向上传播
    ///（页面级重试接手），不再有第三次尝试、后续轨道也不开始。
    /// </summary>
    [Fact]
    public async Task ReplacedHost404_OriginalAlso404_PropagatesAfterSingleFallback()
    {
        var (executor, context, urls) = BuildPage(_ => NotFound());

        await Assert.ThrowsAsync<HttpRequestException>(() => executor.DownloadDashPageAsync(context, 0, 0));

        Assert.Equal(new[]
        {
            $"https://{BackupHost}/v.m4s",
            $"https://{OriginalHost}/v.m4s",
        }, urls);
    }

    /// <summary>未开启替换（地址未变）时 404 不得触发回退：行为与旧版一致，只尝试一次。</summary>
    [Fact]
    public async Task UnreplacedHost404_NoFallback()
    {
        var (executor, context, urls) = BuildPage(_ => NotFound(), forceReplace: false);

        await Assert.ThrowsAsync<HttpRequestException>(() => executor.DownloadDashPageAsync(context, 0, 0));

        Assert.Equal(new[] { $"https://{OriginalHost}/v.m4s" }, urls);
    }

    /// <summary>替换后遇到的非 404 失败（可能瞬时）不触发回退，交既有重试路径。</summary>
    [Fact]
    public async Task ReplacedHostNon404_NoFallback()
    {
        var (executor, context, urls) = BuildPage(_ =>
            new HttpRequestException("server error", null, HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<HttpRequestException>(() => executor.DownloadDashPageAsync(context, 0, 0));

        Assert.Equal(new[] { $"https://{BackupHost}/v.m4s" }, urls);
    }

    /// <summary>构造最小的 DASH 分P下载环境：单条视频轨 + 单条音频轨，SkipMux 使流程止于下载。</summary>
    private static (DownloadPageExecutor Executor, PageExecutionContext Context, List<string> Urls) BuildPage(
        Func<string, Exception?> failFor,
        bool forceReplace = true)
    {
        var video = new Video { id = "v", dfn = "1080P", baseUrl = $"https://{OriginalHost}/v.m4s", codecs = "HEVC" };
        var audio = new Audio
        {
            id = "a",
            dfn = "M4A",
            baseUrl = $"https://{OriginalHost}/a.m4s",
            codecs = "M4A",
            bandwidth = 100,
            dur = 10,
        };
        var parsed = new ParsedResult();
        parsed.VideoTracks.Add(video);
        parsed.AudioTracks.Add(audio);

        var options = new MyOption { SkipMux = true, ForceReplaceHost = forceReplace };
        var urls = new List<string>();

        var services = new DownloadPageExecutionServices
        {
            BackupHost = BackupHost,
            FormatSavePath = (_, _, _, _, _, _, _, _) => "out.mp4",
            DownloadDanmakuAsync = (_, _, _, _, _, _, _) => Task.FromResult<bool?>(null),
            DownloadCoverOnlyAsync = (_, _, _, _, _, _) => Task.FromResult(false),
            TrySkipExistingOutput = (_, _, _, _, _, _) => false,
            PrintSelectedTrackInfo = (_, _, _) => { },
            // 与生产同构：UposHost 非空即替换 host；回退所需的原地址由执行器在替换前自行捕获
            HandlePcdn = (opts, v, a) =>
            {
                if (opts.UposHost == "") return;
                if (v is not null) v.baseUrl = SwapHost(v.baseUrl, opts.UposHost);
                if (a is not null) a.baseUrl = SwapHost(a.baseUrl, opts.UposHost);
            },
            DownloadTrackAsync = (url, _, _, _) =>
            {
                urls.Add(url);
                var ex = failFor(url);
                return ex is null ? Task.CompletedTask : Task.FromException(ex);
            },
            DecryptDrmAsync = (_, _, _, _, _) => Task.CompletedTask,
        };

        var page = new Page { index = 1, aid = "1", cid = "2", epid = "", title = "t", dur = 10, res = "1080P", pubTime = 0 };
        var context = new PageExecutionContext
        {
            Page = page,
            Options = options,
            VideoInfo = new VInfo { Title = "t", Desc = "", Pic = "", PubTime = 0, PagesInfo = [page] },
            SelectedPagesInfo = [page],
            ParsedResult = parsed,
            Description = "",
            Title = "t",
            Pic = "",
            CoverPath = "cover.jpg",
            Lang = "",
            SubtitleInfo = [],
            AudioMaterial = [],
            DownloadConfig = new BBDownDownloadUtil.DownloadConfig(),
            Finalizer = new DownloadFinalizer((_, _, _) => Task.FromResult(0), p => p, _ => { }),
            DownloadDanmaku = false,
            DownloadDanmakuFormats = [],
            SavePathFormat = "out.mp4",
            PagesCount = 1,
            ApiType = "app",
            PubTime = 0,
            RelatedTask = null,
            CancellationToken = CancellationToken.None,
        };

        return (new DownloadPageExecutor(services), context, urls);
    }

    private static string SwapHost(string url, string host)
    {
        var uri = new Uri(url);
        return $"https://{host}{uri.AbsolutePath}";
    }
}
