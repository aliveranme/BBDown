using BBDown;
using BBDown.Commands;
using BBDown.Core;
using BBDown.Core.Entity;
using static BBDown.Core.Entity.Entity;

namespace BBDown.Tests;

/// <summary>
/// RF-97：<c>sub check</c> 的**路径选择**此前零覆盖——PR #55 新增的 10 例全部只测
/// <c>SpaceVideoFetcher.CollectNewAidsAsync</c>，而变异实验（把 <c>CheckSubscriptionsAsync</c>
/// 里的 <c>fetcher is IAidLister</c> 分支强制失效，等价于"轻量路径从未被启用"）显示 746 例仍全绿。
/// 性能优化被静默旁路是这类改动最常见的回归形态，因此把 fetcher 创建点改为可注入，
/// 用假 fetcher 钉住"mid: 目标走轻量列举、非列表目标回退全量解析"。
///
/// 本类不触网：目标用 <c>mid:1</c>（<c>UrlResolver</c> 对无 scheme 输入原样返回，不发请求），
/// 历史用临时 StoreRoot 注入，假 fetcher 不产生真实 IO。
/// </summary>
public class SubCheckPathSelectionTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _origRoot;

    public SubCheckPathSelectionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "bbdown-subpath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _origRoot = SubscriptionStore.StoreRoot;
        SubscriptionStore.StoreRoot = _tempRoot;
    }

    public void Dispose()
    {
        SubscriptionStore.StoreRoot = _origRoot;
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
    }

    /// <summary>同时实现 IFetcher 与 IAidLister（等价于 mid: 命中的 SpaceVideoFetcher）。</summary>
    private sealed class AidListerFetcher : IFetcher, IAidLister
    {
        private readonly List<string> _newAids;

        internal AidListerFetcher(params string[] newAids) => _newAids = [.. newAids];

        internal int FetchCalls { get; private set; }
        internal int ListCalls { get; private set; }
        internal string? LastId { get; private set; }
        internal bool? LastFullScan { get; private set; }
        internal IReadOnlySet<string>? LastKnown { get; private set; }

        public Task<VInfo> FetchAsync(string id, CancellationToken cancellationToken = default)
        {
            FetchCalls++;
            throw new InvalidOperationException("mid: 目标不应走全量解析路径（逐稿展开详情）");
        }

        public Task<List<string>> ListNewAidsAsync(string id, IReadOnlySet<string> known, bool fullScan, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            LastId = id;
            LastKnown = known;
            LastFullScan = fullScan;
            return Task.FromResult(new List<string>(_newAids));
        }
    }

    /// <summary>只实现 IFetcher（等价于收藏夹/合集/番剧目标）。</summary>
    private sealed class FullFetchFetcher : IFetcher
    {
        private readonly VInfo _info;

        internal FullFetchFetcher(params string[] aids) => _info = new VInfo
        {
            Title = "t",
            Desc = "d",
            Pic = "",
            PubTime = 0,
            PagesInfo = [.. aids.Select((aid, i) => new Page { index = i + 1, aid = aid, cid = "1", epid = "", title = "p", dur = 0, res = "", pubTime = 0 })],
        };

        internal int FetchCalls { get; private set; }

        public Task<VInfo> FetchAsync(string id, CancellationToken cancellationToken = default)
        {
            FetchCalls++;
            return Task.FromResult(_info);
        }
    }

    private static SubCheckSettings Settings(bool fullScan = false) => new()
    {
        WorkDir = Path.GetTempPath(),
        PerSubDir = false,
        FullScan = fullScan,
    };

    private static List<Subscription> Subs(params string[] targets) =>
        [.. targets.Select(t => new Subscription(t, t, 0))];

    [Fact]
    public async Task MidTarget_UsesAidListerPath_NotFullExpand()
    {
        // mid: 目标命中 IAidLister：必须走轻量列举，绝不调用 FetchAsync（逐稿展开详情）
        var fetcher = new AidListerFetcher();

        var failed = await SubCheckCommand.CheckSubscriptionsAsync(
            Subs("mid:1"), Settings(), CancellationToken.None, (_, _) => fetcher);

        Assert.Equal(0, failed);
        Assert.Equal(1, fetcher.ListCalls);
        Assert.Equal(0, fetcher.FetchCalls);
        Assert.Equal("mid:1", fetcher.LastId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullScanFlag_IsForwardedToLister(bool fullScan)
    {
        // --full-scan 的接线（此前同样零覆盖：传错/传常量都不会被发现）
        var fetcher = new AidListerFetcher();

        await SubCheckCommand.CheckSubscriptionsAsync(
            Subs("mid:1"), Settings(fullScan), CancellationToken.None, (_, _) => fetcher);

        Assert.Equal(fullScan, fetcher.LastFullScan);
    }

    [Fact]
    public async Task ListerPath_ReceivesDownloadHistory()
    {
        // 历史必须先加载再交给列举器（比对基准），否则每个稿件都会被当成新增
        await SubscriptionStore.RecordDownloadedAsync("mid:1", "111", CancellationToken.None);
        var fetcher = new AidListerFetcher();

        await SubCheckCommand.CheckSubscriptionsAsync(
            Subs("mid:1"), Settings(), CancellationToken.None, (_, _) => fetcher);

        Assert.NotNull(fetcher.LastKnown);
        Assert.Contains("111", fetcher.LastKnown!);
    }

    [Fact]
    public async Task NonListerTarget_FallsBackToFullFetchPath()
    {
        // 收藏夹/合集/番剧等未实现 IAidLister 的目标必须保持原全量解析路径
        var fetcher = new FullFetchFetcher();

        var failed = await SubCheckCommand.CheckSubscriptionsAsync(
            Subs("favId:1:2"), Settings(), CancellationToken.None, (_, _) => fetcher);

        Assert.Equal(0, failed);
        Assert.Equal(1, fetcher.FetchCalls);
    }
    [Fact]
    public async Task PerSubDir_CheckPersistsAssignmentsBeforeAnyDownloads()
    {
        var first = new Subscription("mid:1", "same", 1);
        var second = new Subscription("mid:2", "same", 2);
        var settings = Settings();
        settings.PerSubDir = true;
        var fetcher = new AidListerFetcher();

        await SubCheckCommand.CheckSubscriptionsAsync([first, second], settings, CancellationToken.None, (_, _) => fetcher);
        await SubCheckCommand.CheckSubscriptionsAsync([second], settings, CancellationToken.None, (_, _) => fetcher);

        var assignments = await SubscriptionStore.ResolveDirectoriesAsync([second]);
        Assert.Equal("same-2", assignments[second.Target]);
        Assert.Equal("same", assignments[first.Target]);
    }

}
