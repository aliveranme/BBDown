using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using BBDown;
using BBDown.Core;
using BBDown.Core.Fetcher;
using BBDown.Core.Util;

namespace BBDown.Commands;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
public class SubSettings : CommandSettings
{
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
public class SubAddSettings : SubSettings
{
    [CommandArgument(0, "<target>")]
    [Description("订阅目标: 视频 URL / av / bv / ep: / ss: / mid: / 合集 / 收藏夹等")]
    public string Target { get; set; } = "";

    [CommandOption("--name")]
    [Description("订阅显示名称(默认使用目标字符串)")]
    public string? Name { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
public class SubListSettings : SubSettings
{
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
public class SubRemoveSettings : SubSettings
{
    [CommandArgument(0, "<target>")]
    [Description("要移除的订阅目标")]
    public string Target { get; set; } = "";
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
public class SubCheckSettings : DownloadOptionSettings
{
    [CommandOption("--per-sub-dir")]
    [Description("每个订阅下载到 <work-dir>/<订阅名>/ 子目录(订阅名取 sub add --name, 缺省为 target, 经路径净化)")]
    public bool PerSubDir { get; set; }

    [CommandOption("--full-scan")]
    [Description("禁用增量提前结束: 无视已下载历史翻完所有投稿列表页(默认遇到整页均已下载即停止翻页)")]
    public bool FullScan { get; set; }

    [CommandOption("--delay-per-item")]
    [Description("设置下载订阅新增内容之间的间隔时间(单位: 秒, 默认无间隔; 相邻订阅之间也停顿)")]
    public int DelayPerItem { get; set; } = 0;
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
public class SubAddCommand : AsyncCommand<SubAddSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, SubAddSettings settings, CancellationToken cancellationToken)
    {
        // RF-93：显示名同时是 sub check --per-sub-dir 的目录名；缺省回退 target 后净化结果
        // 形如 mid_163637592 / https___space_bilibili_com_...，多订阅时几乎不可辨认。
        // 未指定 --name 时提示一次（不改变既有行为）。
        if (string.IsNullOrWhiteSpace(settings.Name))
            Logger.LogWarn("未指定 --name，显示名将回退为订阅目标（也是 sub check --per-sub-dir 的目录名，建议用 --name 指定易读名称）");
        await SubscriptionStore.AddAsync(settings.Target, settings.Name, cancellationToken);
        return 0;
    }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
public class SubListCommand : AsyncCommand<SubListSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, SubListSettings settings, CancellationToken cancellationToken)
    {
        var subs = await SubscriptionStore.LoadAsync(cancellationToken);
        if (subs.Count == 0)
        {
            Logger.Log("当前没有订阅，请先用 BBDown sub add <目标> 添加");
            return 0;
        }
        Logger.Log($"共 {subs.Count} 个订阅:");
        foreach (var s in subs.OrderBy(s => s.AddedAt))
        {
            Logger.Log($"  {s.Target}  [{s.Name}]  (添加于 {DateTimeOffset.FromUnixTimeSeconds(s.AddedAt).LocalDateTime:yyyy-MM-dd HH:mm})");
        }
        return 0;
    }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
public class SubRemoveCommand : AsyncCommand<SubRemoveSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, SubRemoveSettings settings, CancellationToken cancellationToken)
    {
        await SubscriptionStore.RemoveAsync(settings.Target, cancellationToken);
        return 0;
    }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
public class SubCheckCommand : AsyncCommand<SubCheckSettings>
{
    /// <summary>
    /// <c>--delay-per-item</c> 的下界（秒）。0 = 不设间隔 = 加入该选项前的既有行为。
    /// </summary>
    internal const int MinDelayPerItemSeconds = 0;

    /// <summary>
    /// <c>--delay-per-item</c> 的上界（秒），与默认下载命令的 <c>--delay-per-page</c>（0~600）对齐。
    /// </summary>
    internal const int MaxDelayPerItemSeconds = 600;

    protected override async Task<int> ExecuteAsync(CommandContext context, SubCheckSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            // 批量检查期间 Ctrl+C 会取消当前下载；直接退出进程则等效于整体取消。
            var subs = await SubscriptionStore.LoadAsync(cancellationToken);
            if (subs.Count == 0)
            {
                Logger.LogWarn("当前没有订阅，请先用 BBDown sub add <目标> 添加");
                return 0;
            }

            // --delay-per-item 越界与 -w 同待遇：整批无效的输入错误 → 明确报错 + 退出码 1，
            // 且必须在 InitializeRequestSessionAsync（首次网络请求）之前判掉。
            // 不抛 ArgumentException：ExecuteAsync 只捕获 OperationCanceledException，抛出去会
            // 落到 Spectre 命令级处理器，被报成误导性的"请尝试升级到最新版本后重试!"。
            // 校验点排在 -w 解析**之前**（与默认下载命令一致：Workflow 先 ValidateNumericOptions
            // 再 ChangeWorkingDir）：TryResolveWorkDir 会经 ResolveWorkDir 执行
            // Directory.CreateDirectory，排在其后会让一次注定失败的越界调用仍建出 -w 目录。
            if (!TryValidateDelayPerItem(settings.DelayPerItem, out string? delayError))
            {
                Logger.LogError(delayError);
                return 1;
            }

            // -w 必须先绝对化且只解析一次（RF-89）：ChangeWorkingDir（CLI 非 serve）会把进程
            // CWD 切到上一个订阅的下载目录，相对 -w 到第二个订阅会基于该目录再拼一层，
            // 产出 <root>/<sub1>/<sub2> 嵌套。
            // 非法 -w 是整批无效的输入错误：明确报错 + 退出码 1。此前解析发生在
            // CheckSubscriptionsAsync 的逐订阅循环之前且不在任何 try 内，抛出的
            // ArgumentException 会逃到 Spectre 命令级处理器，被报成误导性的
            // "请尝试升级到最新版本后重试!" 并静默放弃其余全部订阅。
            if (!Program.TryResolveWorkDir(settings.WorkDir, out string resolvedWorkDir, out string workDirError))
            {
                Logger.LogError($"工作目录无效: {workDirError}");
                return 1;
            }
            settings.WorkDir = resolvedWorkDir;

            // 订阅解析与拉取（VIP/登录态内容）需要凭据：
            // LoadCredentials 会优先应用命令行 --cookie/--access-token，否则加载本地 BBDown.data。
            // 此前只处理显式传参，已登录但未传参时枚举阶段以匿名身份执行，VIP/区域订阅会被误判为空。
            var sessionOption = new MyOption
            {
                Cookie = settings.Cookie,
                AccessToken = settings.AccessToken,
                UseTvApi = settings.UseTvApi,
                UseAppApi = settings.UseAppApi,
                UseIntlApi = settings.UseIntlApi,
            };

            // 统一初始化请求会话：订阅枚举（mid: 空间/收藏夹/合集等）经 SpaceVideoFetcher →
            // Parser.WbiSign 签名，必须先取得 wbi，否则空 wbi 的 w_rid 会被 B 站拒绝。
            // 返回的完整会话（含本地凭据与新 wbi）在父流程（SubCheck 自身异步流）内显式应用——
            // 子方法内 AsyncLocal 写入不会回流，只应用 newWbi 会让本地凭据丢失。
            // 会话初始化纳入 try：Ctrl+C 落在此处按"已取消"处理，而非穿透全局
            // handler 返回 130（RF-32 路径②）。
            var session = await Program.InitializeRequestSessionAsync(sessionOption, cancellationToken);
            if (session is not null) Core.Config.Apply(session);

            int failedSubs = await CheckSubscriptionsAsync(subs, settings, cancellationToken);
            if (failedSubs > 0)
            {
                Logger.LogWarn($"订阅检查完成，{failedSubs} 个订阅失败");
                return 1;
            }
            return 0;
        }
        // 用户取消语义（RF-32）：与 watchlater 及文档契约（CLI-Reference 退出码表）对齐——
        // Ctrl+C / 关停属主动取消返回 0；token 未取消的 OCE（HttpClient 超时已在上层过滤器
        // 按 TCE 分类，到这里的多为内部联动 CTS）是真实失败，返回 1 而非以 0 掩盖。
        catch (OperationCanceledException ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Logger.LogWarn("已取消");
                return 0;
            }
            Logger.LogError($"订阅检查超时或被中断: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// <c>--delay-per-item</c> 的取值范围校验（<see cref="MinDelayPerItemSeconds"/> ~
    /// <see cref="MaxDelayPerItemSeconds"/> 秒）。返回 false 时 <paramref name="error"/> 是可直接打印的
    /// 错误文本。
    ///
    /// 抽成独立方法而非内联在 <see cref="ExecuteAsync"/>：后者一旦走到就会发起网络请求
    /// （会话初始化 + 订阅枚举），单测无法覆盖边界值；这里是无副作用纯函数，0/600 与
    /// -1/601 可直接断言。默认下载命令的同族校验（<c>ValidateNumericOptions</c>）抛
    /// ArgumentException，本命令不能照抄——见 <see cref="ExecuteAsync"/> 中的说明。
    /// </summary>
    internal static bool TryValidateDelayPerItem(int seconds, [NotNullWhen(false)] out string? error)
    {
        if (seconds < MinDelayPerItemSeconds || seconds > MaxDelayPerItemSeconds)
        {
            error = $"参数有误: --delay-per-item 需在 {MinDelayPerItemSeconds} ~ {MaxDelayPerItemSeconds} 秒之间, 当前为 {seconds}";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// 逐订阅检查并增量下载新内容，返回失败的订阅数。用户取消与订阅数据损坏异常原样上抛，
    /// 由 <see cref="SubCheckCommand.ExecuteAsync"/> 分类为退出码（RF-30/RF-32）。
    /// </summary>
    /// <param name="fetcherFactory">
    /// RF-97：fetcher 创建点改为可注入（默认仍为 <see cref="FetcherFactory.CreateFetcher"/>），
    /// 使"mid: 目标是否真的走 <see cref="IAidLister"/> 轻量路径"这一接线可被单测钉住。
    /// 此前 PR 新增的 10 例全部只测 <c>SpaceVideoFetcher.CollectNewAidsAsync</c>，把本方法的
    /// <c>fetcher is IAidLister</c> 分支强制失效后 746 例仍全绿——性能优化被静默旁路是这类
    /// 改动最常见的回归形态。
    /// </param>
    /// <param name="downloadAsync">
    /// 单个新增内容的下载执行点，默认 <see cref="Program.DoWorkAsync"/>（与加入该参数前逐字一致）。
    /// 与 <paramref name="fetcherFactory"/> 同因可注入：<c>--delay-per-item</c> 的"每个新增内容
    /// 下载之前停顿"这条接线，否则只能在真机上验证——单测里一旦有新增 aid，默认实现就会
    /// 发起真实网络下载。
    /// </param>
    /// <param name="delayAsync">
    /// 节流的实际等待点（秒, token），默认 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>。
    /// 可注入使"停顿了几次、每次几秒、相对列表/下载的先后顺序"成为可断言的纯逻辑（不真等）。
    /// </param>
    internal static async Task<int> CheckSubscriptionsAsync(
        List<Subscription> subs,
        SubCheckSettings settings,
        CancellationToken cancellationToken,
        Func<string, bool, IFetcher>? fetcherFactory = null,
        Func<MyOption, CancellationToken, Task>? downloadAsync = null,
        Func<int, CancellationToken, Task>? delayAsync = null)
    {
        var createFetcher = fetcherFactory ?? FetcherFactory.CreateFetcher;
        var download = downloadAsync ?? Program.DoWorkAsync;
        var delay = delayAsync ?? ((seconds, token) => Task.Delay(TimeSpan.FromSeconds(seconds), token));
        // -w 已由 ExecuteAsync 经 TryResolveWorkDir 绝对化且只解析一次（RF-89）：本方法内不得
        // 再解析——循环前的解析点不在任何 try 内，抛出的 ArgumentException 会逃出命令级
        // 异常过滤器（ExecuteAsync 只捕获 OperationCanceledException）。
        // --per-sub-dir 的基目录（见 Program.ResolvePerSubBaseWorkDir）：空 -w 时用与
        // ChangeWorkingDir 同源的默认下载目录（程序目录下的 Download），默认目录不可
        // 创建时回落检查启动时的 CWD；基目录必须先于逐订阅循环定下（下载过程中
        // ChangeWorkingDir 会写进程 CWD，相对路径到第二个订阅会漂移，须先捕获）。
        // 仅在开启 --per-sub-dir 时解析：关闭时 baseWorkDir 不参与任何路径，
        // 解析会无谓地创建默认目录、产生与本次运行无关的告警。
        string baseWorkDir = settings.PerSubDir
            ? Program.ResolvePerSubBaseWorkDir(settings.WorkDir)
            : settings.WorkDir;
        // 名称槽位每个订阅都占用（与是否有新增无关），冲突序号才能跨 run 稳定
        var usedSubDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // --delay-per-item：首个工作单元不等待，此后**每个**工作单元（开始检查一个订阅 /
        // 下载一个新增内容）之前先停顿 N 秒。动机：sub check 此前没有任何节流参数（--delay-per-page
        // 只声明在默认下载命令上，sub check 传给 Spectre 会被当 unknown option 拒绝），于是
        // "新增一个投稿很多的 UP"这种首次检查会连续下载全部新增稿件——每个 aid 一个独立任务
        // （解析 playurl + 分片下载），把账号推进风控窗口（列表 412 / -352，playurl v_voucher）。
        //
        // 单位必须是"工作单元"而不是"分 P"：单个新增 aid 就是一次独立任务，复用 --delay-per-page
        // 的语义在这里几乎永不触发（它只在多分 P 稿件内部生效：pagesInfo.Count > 1）。所以
        // 间隔只能插在 check 循环这一层。
        //
        // 列表翻页（4 个 fetcher 各自的分页循环、50 条/页、零间隔）刻意不含在内：翻页请求量
        // 比逐条下载小两个数量级，且要把间隔穿进 fetcher 会破坏 CollectNewAidsAsync 的委托
        // 注入测试形态，留待确有需要时再加。
        //
        // DelayPerItem = 0（默认）时既不等待也不打日志，行为与加入该选项前逐字一致。
        bool firstUnit = true;
        async Task PaceAsync()
        {
            if (firstUnit)
            {
                firstUnit = false;
                return;
            }
            if (settings.DelayPerItem <= 0) return;
            Logger.Log($"停顿{settings.DelayPerItem}秒...");
            await delay(settings.DelayPerItem, cancellationToken);
        }

        int failedSubs = 0;
        foreach (var sub in subs)
        {
            string subWorkDir = Path.Combine(baseWorkDir, ResolveSubDirName(sub, usedSubDirs));
            Logger.Log($"检查订阅: {sub.Name} ({sub.Target})"
                + (settings.PerSubDir ? $"  输出目录: {subWorkDir}" : ""));
            // 停顿在 try 之外：用户取消时 Task.Delay 抛出的 OCE 直接上抛给 ExecuteAsync
            // 归类为"已取消 / 退出码 0"，不会被下方"单订阅失败"的过滤器记成一次失败。
            await PaceAsync();
            try
            {
                string resolved = await UrlResolver.ResolveAsync(sub.Target, cancellationToken);
                if (string.IsNullOrEmpty(resolved)) continue;

                var fetcher = createFetcher(resolved, settings.UseIntlApi);

                var history = await SubscriptionStore.LoadHistoryAsync(sub.Target, cancellationToken);

                // 列表型目标（mid:，见 IAidLister）走轻量列举：IFetcher.FetchAsync 会为
                // **每个**投稿再发一次详情请求展开分P（千稿量级请求 + 120ms 间隔，单订阅即
                // 十秒到分钟级且显著加重风控），而这里只关心 aid 集合——每个新 aid 的下载
                // （DoWorkAsync → av{aid}）本来就会各自重新解析一次。
                // 未实现 IAidLister 的目标（收藏夹/合集/番剧等）保持原全量解析路径。
                List<string> newAids;
                if (fetcher is IAidLister lister)
                {
                    newAids = await lister.ListNewAidsAsync(resolved, history, settings.FullScan, cancellationToken);
                }
                else
                {
                    var vInfo = await fetcher.FetchAsync(resolved, cancellationToken);
                    var allAids = vInfo.PagesInfo.Select(p => p.aid).Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList();
                    newAids = allAids.Where(a => !history.Contains(a)).ToList();
                }

                if (newAids.Count == 0)
                {
                    Logger.Log("  没有新增内容");
                    continue;
                }

                // aid 来自 IAidLister 的服务器原文：过 SanitizeLogString 防 CRLF/ANSI 注入
                // 日志（RF-70 同族残留）。
                Logger.Log($"  发现 {newAids.Count} 个新内容: av{string.Join(", av", newAids.Select(BBDownApiServer.SanitizeLogString))}");
                bool anyAidFailed = false;
                foreach (var aid in newAids)
                {
                    try
                    {
                        await PaceAsync();
                        var opt = settings.ToMyOption($"av{aid}",
                            settings.PerSubDir ? subWorkDir : settings.WorkDir);
                        await download(opt, cancellationToken);
                        await SubscriptionStore.RecordDownloadedAsync(sub.Target, aid, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    // 订阅数据损坏（RecordDownloaded 隔离历史文件后抛出）必须终止整个
                    // sub check（RF-30）：SubscriptionDataCorruptException 继承自
                    // InvalidOperationException，会被下方含基类的过滤器吞掉——历史文件已被
                    // 隔离移走，下一个 aid 的 RecordDownloaded 会静默重建仅含自身的空历史
                    // 并原子写回，全部订阅下载历史就此清零、下次 check 全量重下。
                    catch (SubscriptionDataCorruptException)
                    {
                        throw;
                    }
                    // UnauthorizedAccessException（RF-44）：与下载页过滤器同步扩充——
                    // 只读属性文件/受控文件夹访问等本地权限错误按"单 aid 失败"继续。
                    // InvalidDataException（RF-72）：有界响应体/帧校验抛型，同族。
                    catch (Exception ex) when (ExceptionPolicies.IsSkippableItemFailure(ex))
                    {
                        anyAidFailed = true;
                        Logger.LogWarn($"  av{BBDownApiServer.SanitizeLogString(aid)} 下载失败（继续下一个）: {ex.Message}");
                    }
                }
                if (anyAidFailed) failedSubs++;
            }
            catch (SubscriptionDataCorruptException)
            {
                // 订阅持久化数据损坏（历史/清单损坏）：必须终止整个 sub check，不能按
                // 普通单订阅失败继续——否则后续订阅会因历史文件已不存在而把全部内容
                // 当作新增重新下载，并覆盖一份不完整的历史。
                throw;
            }
            // 用户取消（RF-32 路径①）：从 per-aid 重抛上来的取消不能被下方含
            // TaskCanceledException 的过滤器吞成"订阅检查失败"——否则后续每个订阅都在
            // 已取消的 token 上立刻失败，最终以"N 个订阅失败"+退出码 1 掩盖主动取消。
            // 上抛由 ExecuteAsync 分类为 0。
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // UnauthorizedAccessException（RF-44）：与下载页过滤器同步扩充。
            // InvalidDataException（RF-72）：有界响应体/帧校验抛型，同族。
            catch (Exception ex) when (ExceptionPolicies.IsSkippableItemFailure(ex))
            {
                // 单个订阅失败不中止其余订阅，但必须计入失败数：
                // 全部失败仍返回 0 会让脚本/CI 无法区分"全部成功"与"全部失败"
                failedSubs++;
                Logger.LogWarn($"  订阅检查失败: {ex.Message}");
            }
        }
        return failedSubs;
    }

    /// <summary>
    /// --per-sub-dir：把订阅显示名净化为安全的目录段（RF-18 同款净化——剔除路径分隔符/
    /// 控制字符/保留名/纯点段，超长截断），并与既有槽位去重（OrdinalIgnoreCase 对齐
    /// Windows 文件系统）：两个订阅净化后同名（如 "a:b"/"a?b" → "a_b"）或显示名重复时
    /// 追加 -2/-3 序号，避免互相覆盖。槽位在循环内逐订阅占用，序号跨 run 稳定。
    /// </summary>
    internal static string ResolveSubDirName(Subscription sub, HashSet<string> usedDirs)
    {
        // 显示名为空/空白时回退 target：判据必须用**净化前**的原始值——SanitizePathSegment
        // 对空/纯空白/纯点输入兜底为 "_"、永不返回空串（GetValidFileName 契约），拿净化结果
        // 判断会让回退永远走不到，文档与 PR 声称的"缺省为 target"形同虚设（RF-91）。
        // 判据与 SubscriptionStore.AddAsync 的 "IsNullOrWhiteSpace(name) ? target : name" 一致。
        string raw = string.IsNullOrWhiteSpace(sub.Name) ? sub.Target : sub.Name;
        string name = PathUtil.SanitizePathSegment(raw);
        string candidate = name;
        int seq = 2;
        while (!usedDirs.Add(candidate))
            candidate = $"{name}-{seq++}";
        return candidate;
    }
}
