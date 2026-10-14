using BBDown;
using BBDown.Commands;
using BBDown.Core;
using BBDown.Core.Entity;
using Spectre.Console.Cli;
using static BBDown.Core.Entity.Entity;

namespace BBDown.Tests;

/// <summary>
/// <c>--delay-per-item</c>：sub check 此前没有任何节流参数，于是"新增一个投稿很多的 UP"这种
/// 首次检查会把全部新增稿件连续下载（每个 aid 一个独立任务：解析 playurl + 分片下载），
/// 很容易把账号推进风控窗口（列表 HTTP 412 / <c>code=-352</c>、playurl <c>v_voucher</c>）。
///
/// 本类钉住四件事：
/// ① 停顿的**粒度与时机**：首个工作单元不等待，此后每个单元（订阅检查 / 新增内容下载）
///    之前停顿，且停顿发生在该动作**之前**——按真实发生顺序断言事件流，而不是"调用过"；
/// ② 默认（未设置）与显式 <c>0</c> 都不产生任何等待：既有行为逐字不变；
/// ③ 取值 0~600 的边界与可读错误文本（越界值在任何网络请求之前被拒）；
/// ④ CLI 面：真实 <see cref="CommandApp"/> 能把 <c>--delay-per-item</c> 绑定到位。
///
/// 不触网：目标用 <c>mid:N</c>（<see cref="UrlResolver"/> 对无 scheme 输入原样返回），
/// 订阅历史用临时 StoreRoot，下载与等待都走注入委托——真下载/真等待在本类中不会发生。
/// </summary>
public class SubCheckDelayPacingTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _origRoot;

    public SubCheckDelayPacingTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "bbdown-subdelay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _origRoot = SubscriptionStore.StoreRoot;
        SubscriptionStore.StoreRoot = _tempRoot;
    }

    public void Dispose()
    {
        SubscriptionStore.StoreRoot = _origRoot;
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
    }

    /// <summary>
    /// 一次假运行：每个订阅的"列表请求 / 停顿 / 下载"按真实发生顺序记进同一条事件流。
    /// 事件流是断言的主体——只数调用次数无法区分"停顿发生在下载之前还是之后"。
    /// </summary>
    private sealed class FakeRun
    {
        private readonly Dictionary<string, string[]> _newAidsByTarget;

        internal FakeRun(params (string Target, string[] NewAids)[] plan)
        {
            Plan = plan.Select(p => p.Target).ToList();
            _newAidsByTarget = plan.ToDictionary(p => p.Target, p => p.NewAids);
        }

        internal List<string> Plan { get; }
        internal List<string> Events { get; } = [];

        /// <summary>设置后接管等待：用于模拟"停顿期间用户取消"。</summary>
        internal Func<int, CancellationToken, Task>? OnDelay { get; set; }

        internal IFetcher Fetcher(string resolved, bool useIntl) => new RecordingLister(this, resolved);

        internal Task Download(MyOption option, CancellationToken token)
        {
            Events.Add($"download:{option.Url}");
            return Task.CompletedTask;
        }

        internal Task Delay(int seconds, CancellationToken token)
        {
            Events.Add($"delay:{seconds}");
            return OnDelay?.Invoke(seconds, token) ?? Task.CompletedTask;
        }

        /// <summary>同时实现 IFetcher 与 IAidLister（等价于 mid: 命中的 SpaceVideoFetcher）。</summary>
        private sealed class RecordingLister(FakeRun run, string target) : IFetcher, IAidLister
        {
            public Task<VInfo> FetchAsync(string id, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("mid: 目标不应走全量解析路径（逐稿展开详情）");

            public Task<List<string>> ListNewAidsAsync(string id, IReadOnlySet<string> known, bool fullScan, CancellationToken cancellationToken = default)
            {
                run.Events.Add($"list:{id}");
                return Task.FromResult(new List<string>(run._newAidsByTarget[target]));
            }
        }
    }

    /// <summary><paramref name="delayPerItem"/> 为 null 表示"未设置"，走属性初始化器默认值。</summary>
    private static SubCheckSettings Settings(int? delayPerItem = null)
    {
        var settings = new SubCheckSettings { WorkDir = Path.GetTempPath() };
        if (delayPerItem is not null) settings.DelayPerItem = delayPerItem.Value;
        return settings;
    }

    private static async Task<int> CheckAsync(FakeRun run, SubCheckSettings settings, CancellationToken token = default)
        => await SubCheckCommand.CheckSubscriptionsAsync(
            [.. run.Plan.Select(t => new Subscription(t, t, 0))],
            settings, token, run.Fetcher, run.Download, run.Delay);

    [Theory]
    [InlineData(false)]   // 未设置：默认值
    [InlineData(true)]    // 显式 --delay-per-item 0
    public async Task ZeroDelay_NoPause_AndActionOrderUnchanged(bool explicitZero)
    {
        // 默认 0 = 既有行为：不等待（无 delay 事件），列表与下载的先后顺序也逐字不变
        var run = new FakeRun(("mid:1", ["11", "12"]), ("mid:2", ["21"]));

        var failed = await CheckAsync(run, Settings(explicitZero ? 0 : null));

        Assert.Equal(0, failed);
        Assert.Equal(
            ["list:mid:1", "download:av11", "download:av12", "list:mid:2", "download:av21"],
            run.Events);
    }

    [Fact]
    public async Task DelayPerItem_PausesBeforeEveryActionAfterTheFirst()
    {
        // 首个工作单元（第一个订阅的列表）不等待；此后每个单元前停顿 N 秒——
        // 单元 = 开始检查一个订阅 / 下载一个新增内容，因此请求被均匀摊开：
        // 列表 → 停顿 → 下载 → 停顿 → 下载 → 停顿 → 列表 → 停顿 → 下载
        var run = new FakeRun(("mid:1", ["11", "12"]), ("mid:2", ["21"]));

        var failed = await CheckAsync(run, Settings(5));

        Assert.Equal(0, failed);
        Assert.Equal(
            [
                "list:mid:1",
                "delay:5", "download:av11",
                "delay:5", "download:av12",
                "delay:5", "list:mid:2",
                "delay:5", "download:av21",
            ],
            run.Events);
    }

    [Fact]
    public async Task DelayPerItem_AppliesBetweenSubscriptions_EvenWhenNothingNew()
    {
        // 没有新增内容也照样停顿：列表请求本身就是一次请求突发，
        // 多订阅场景下"每个订阅各发 1~2 个列表请求"同样会被连续打出去
        var run = new FakeRun(("mid:1", []), ("mid:2", []));

        var failed = await CheckAsync(run, Settings(7));

        Assert.Equal(0, failed);
        Assert.Equal(["list:mid:1", "delay:7", "list:mid:2"], run.Events);
    }

    [Fact]
    public async Task DelayPerItem_UsesTheConfiguredSeconds_NotAConstant()
    {
        // 间隔秒数必须来自选项（写死常量时这条会失败）
        var run = new FakeRun(("mid:1", ["11"]));

        await CheckAsync(run, Settings(123));

        Assert.Equal(["list:mid:1", "delay:123", "download:av11"], run.Events);
    }

    [Fact]
    public async Task CancelWhileWaiting_Propagates_InsteadOfCountingASubFailure()
    {
        // 用户在"停顿 N 秒"期间 Ctrl+C：取消必须原样上抛（由 ExecuteAsync 分类为
        // 已取消/退出码 0），不能被 per-aid / per-sub 的失败过滤器吞成"订阅检查失败"
        using var cts = new CancellationTokenSource();
        var run = new FakeRun(("mid:1", ["11"]), ("mid:2", ["21"]));
        run.OnDelay = (_, token) => { cts.Cancel(); throw new OperationCanceledException(token); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CheckAsync(run, Settings(30), cts.Token));

        // 取消点在 sub1 的第一个下载之前：sub2 既没被列举也没被下载
        Assert.Equal(["list:mid:1", "delay:30"], run.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(SubCheckCommand.MaxDelayPerItemSeconds)]
    public void DelayPerItem_InRange_IsAccepted(int seconds)
    {
        Assert.True(SubCheckCommand.TryValidateDelayPerItem(seconds, out string? error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(SubCheckCommand.MaxDelayPerItemSeconds + 1)]
    [InlineData(int.MaxValue)]
    public void DelayPerItem_OutOfRange_IsRejected_WithReadableMessage(int seconds)
    {
        Assert.False(SubCheckCommand.TryValidateDelayPerItem(seconds, out string? error));
        Assert.NotNull(error);
        Assert.Contains("--delay-per-item", error);
        Assert.Contains(seconds.ToString(), error);
        // 范围文本本身也是契约：改上界必须同步改这条断言
        Assert.Contains(
            $"{SubCheckCommand.MinDelayPerItemSeconds} ~ {SubCheckCommand.MaxDelayPerItemSeconds} 秒",
            error);
    }

    [Theory]
    [InlineData(new[] { "--delay-per-item", "7" }, 7)]
    [InlineData(new[] { "--delay-per-item=9" }, 9)]
    [InlineData(new string[0], 0)]
    public void DelayPerItem_BindsFromCommandLine(string[] args, int expected)
    {
        // CLI 面：真实 CommandApp（含 sub 分支约束）能绑定该选项，缺省为 0
        CaptureCheckCommand.Captured = null;
        var app = new CommandApp();
        app.Configure(config => config.AddBranch<SubSettings>("sub",
            sub => sub.AddCommand<CaptureCheckCommand>("check")));

        var exit = app.Run([.. new[] { "sub", "check" }, .. args]);

        Assert.Equal(0, exit);
        Assert.Equal(expected, CaptureCheckCommand.Captured!.DelayPerItem);
    }

    /// <summary>捕获绑定结果的假命令（不执行任何下载）——只替代 SubCheckCommand 的入口。</summary>
    private sealed class CaptureCheckCommand : AsyncCommand<SubCheckSettings>
    {
        public static SubCheckSettings? Captured;

        protected override Task<int> ExecuteAsync(CommandContext context, SubCheckSettings settings, CancellationToken cancellationToken)
        {
            Captured = settings;
            return Task.FromResult(0);
        }
    }
}
