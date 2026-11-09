using BBDown.Core;
using BBDown.Core.Util;
namespace BBDown;

/// <summary>
/// serve 任务注册表与执行（H1 拆解：任务列表 / 并发与接受队列闸门 / 入队与下载执行）。
/// 与 <c>BBDownApiServer.TaskFileStore.cs</c> 的分工：本文件只管内存态与执行，
/// 持久化（写入 / 裁剪 / 加载）在 TaskFileStore。
/// </summary>
public partial class BBDownApiServer
{
    private readonly object _taskLock = new();
    private readonly List<DownloadTask> runningTasks = [];
    private readonly List<DownloadTask> finishedTasks = [];

    // 可注入完成通知以验证任务的终态路径，生产环境始终使用真实 webhook。
    internal Func<DownloadTask, string?, Task>? CompletionNotifier { get; set; }

    /// <summary>
    /// 在途后台任务集合（fire-and-forget 的 ProcessDownloadTaskAsync）。服务关停时
    /// 取消共享令牌后必须等待这些任务完成取消/终止外部进程/持久化，再退出进程——
    /// 否则 ffmpeg/aria2c 成为孤儿、直播 .part 未改名、已完成任务记录来不及落盘。
    /// </summary>
    private readonly HashSet<Task> _inFlightTasks = [];
    // 在途任务的 JobId 集合：关停排空超时后枚举具体任务，供运维定位孤儿外部进程
    private readonly HashSet<string> _inFlightJobIds = [];
    private readonly object _inFlightLock = new();

    /// <summary>
    /// 接受队列上限：正在处理 + 排队等待的任务总数上限。
    /// 每个 /add-task 请求都会创建一个后台 Task（即使还没开始下载），若不加限制，
    /// 攻击者或误操作可无限堆积后台 Task、配置对象与 CTS。达到上限后 /add-task
    /// 返回 429 Too Many Requests，而不是继续堆积。
    /// 队列长度 = maxConcurrent（并发执行） + 允许排队等待的额外数量。
    /// </summary>
    private readonly SemaphoreSlim _acceptLimiter;
    private const int MaxQueuedPerConcurrent = 8;

    /// <summary>
    /// HTTP 查询端点并发上限（D8）：/get-tasks 族每个请求都需在锁内对全部任务做
    /// 深拷贝快照（Snapshot()），带 token 客户端无限制并发查询会放大 CPU/GC。
    /// 信号量上限 8：允许本地管理脚本正常多次查询，同时掐住无限并发。
    /// 非阻塞获取：拿不到槽位立即 429（短事务，客户端可重试），不排队堆积——
    /// 与认证限速、接受队列的 429 语义一致，避免“慢客户端占满查询槽”。
    /// </summary>
    private readonly SemaphoreSlim _queryLimiter;
    private const int MaxConcurrentQueryHandlers = 8;

    /// <summary>当前空闲的 HTTP 查询槽位（供测试断言限流行为）。</summary>
    internal int AvailableQuerySlots => _queryLimiter.CurrentCount;

    /// <summary>尝试占用一个查询槽位。供测试直接占用槽位验证 429 限流路径。</summary>
    internal bool TryAcquireQuerySlot() => _queryLimiter.Wait(0);

    /// <summary>当前空闲的接受队列槽位（供测试断言限流行为）。</summary>
    internal int AvailableAcceptSlots => _acceptLimiter.CurrentCount;

    /// <summary>
    /// 尝试占用一个接受队列槽位。供测试直接占用槽位验证 429 限流路径。
    /// </summary>
    internal bool TryAcquireAcceptSlot() => _acceptLimiter.Wait(0);

    /// <summary>
    /// 尝试占用一个并发执行槽位（--max-concurrent 闸门）。供测试直接占用闸门
    /// 制造"排队等待"场景，验证 /cancel 对排队任务生效（无需真实慢任务）。
    /// </summary>
    internal bool TryAcquireConcurrencySlot() => _concurrencyLimiter.Wait(0);

    private readonly SemaphoreSlim _concurrencyLimiter;

    /// <summary>
    /// OperationCanceledException 归类：token 已请求取消 → 用户/服务关停主动取消（Cancelled）；
    /// token 未取消 → HttpClient 超时等内部中断（真实失败，Failed）。HttpClient 超时抛的
    /// TaskCanceledException 其 token 未取消，若一律按取消处理会把超时失败误标"已取消"，
    /// 掩盖真实失败原因。解析与下载阶段的 catch 复用此判定。
    /// </summary>
    internal static (DownloadTaskStatus Status, string Message) ClassifyCancellation(bool cancellationRequested, string failureMessage)
        => cancellationRequested
            ? (DownloadTaskStatus.Cancelled, "已取消")
            : (DownloadTaskStatus.Failed, failureMessage);

    /// <summary>
    /// 任务 ID 匹配：JobId 优先（/add-task 返回的 GUID，唯一且无业务含义）；
    /// 其次回退到 Aid / 提交 Url，兼容旧客户端与旧持久化记录（旧记录 JobId 为空串）。
    /// </summary>
    private static bool MatchesTaskId(DownloadTask task, string id)
    {
        if (!string.IsNullOrEmpty(task.JobId) && task.JobId == id) return true;
        return task.Aid == id || task.Url == id;
    }

    /// <summary>
    /// 在 <see cref="_taskLock"/> 持锁前提下按 ID 查找任务（JobId 优先，Aid/Url 回退）。
    /// finished 与 running 的查找顺序固定：先 finished 后 running，
    /// 避免同名任务在两个集合中各有副本时查询结果漂移。
    /// </summary>
    private DownloadTask? FindTaskByIdLocked(string id, bool includeFinished, bool includeRunning)
    {
        if (includeFinished)
        {
            var f = finishedTasks.FirstOrDefault(t => MatchesTaskId(t, id));
            if (f is not null) return f;
        }
        if (includeRunning)
        {
            var r = runningTasks.FirstOrDefault(t => MatchesTaskId(t, id));
            if (r is not null) return r;
        }
        return null;
    }

    /// <summary>
    /// /add-task 入队阶段：不 await 任何网络操作，立即生成 JobId 并把任务加入 runningTasks。
    /// 返回的任务已带 <see cref="DownloadTask.JobId"/>（GUID），客户端拿到 202 + JobId 后即可
    /// 通过 /get-tasks/{id} 或 /cancel/{id} 命中该任务。URL 解析、下载都在锁外的
    /// <see cref="ProcessDownloadTaskAsync"/> 中异步推进。
    /// 不按 Aid 去重：同一视频、不同参数可以并存为两个独立任务（各自有独立 JobId）。
    /// </summary>
    private DownloadTask EnqueueDownloadTask(MyOption option)
    {
        var task = new DownloadTask(option.Url, option.Url, DateTimeOffset.Now.ToUnixTimeSeconds())
        {
            JobId = Guid.NewGuid().ToString("N"),
            Status = DownloadTaskStatus.Queued,
        };
        // 仅入队，不做任何网络解析；锁内只操作内存集合
        lock (_taskLock) { runningTasks.Add(task); }
        return task;
    }

    /// <summary>
    /// 运行 /add-task 接受的后台任务：负责释放接受队列占位，并把任何漏网异常
    /// 收敛成日志（ProcessDownloadTaskAsync 内部已收敛所有异常，此处兜底避免遗漏
    /// 变成无人观察的 Task 异常）。占位释放必须在任务完成（含取消/失败）后，
    /// 否则队列上限会因已结束任务占位不释放而逐渐耗尽。
    /// </summary>
    private async Task RunAcceptedTaskAsync(MyOption option, DownloadTask task, Task startSignal)
    {
        await startSignal.ConfigureAwait(false);
        try
        {
            await ProcessDownloadTaskAsync(option, task, _notifyWebhook, _serverLifetimeCts.Token);
        }
        catch (Exception ex)
        {
            Logger.LogError($"任务异常终止: {ex.GetBaseException().Message}");
        }
        finally
        {
            _acceptLimiter.Release();
        }
    }

    /// <summary>
    /// 单个已接受任务的生命周期：等待并发闸门 → 解析 URL → 获取视频信息 → 下载。
    /// 信号量闸门覆盖"解析→下载"全程（此前只在解析后生效，解析阶段不受并发限制）。
    /// 任何异常都收敛到任务状态字段，使客户端拿到 JobId 后能查到失败原因。
    /// </summary>
    private async Task ProcessDownloadTaskAsync(MyOption option, DownloadTask task, string? notifyWebhook = null, CancellationToken cancellationToken = default)
    {
        // 解析 aid 前先把本任务的完整配置写入当前 async 流，用干净的 AppSettings 起步：
        // 1) 避免解析阶段读到全局 _settings 里上一个任务留下的 cookie（跨账号解析）；
        // 2) 避免 Host/EpHost/TvHost/Area 等字段继承上个任务的覆盖值（cookie 发往错误 host）。
        // 用 option 的显式值（MyOption 默认值即官方地址），accessToken 可能被 JSON null 置空。
        Config.Apply(new AppSettings
        {
            Cookie = option.Cookie ?? "",
            Token = (option.AccessToken ?? "").Replace("access_token=", ""),
            DebugLog = option.Debug,
            Host = option.Host,
            EpHost = option.EpHost,
            TvHost = option.TvHost,
            Area = option.Area ?? "",
            SkipSslCheck = option.Insecure,
            IsServeMode = true,
        });

        // 并发闸门：等待信号量放在 URL 解析之前。此前闸门只在解析完成后才生效，
        // 大量短链/SS/MD 提交会同时发起出站网络解析（不受 --max-concurrent 限制），
        // 攻击者或误操作可并发打满 B 站 API。现在信号量覆盖"解析→下载"完整生命周期。
        // 取消令牌也在这里创建：解析阶段（b23 跳转、SS/MD 查询、页面抓取）也能被
        // /cancel/{id} 与服务器关停中断（此前解析阶段的取消要等解析完成才生效）。
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, task.CancelCts.Token);
        bool slotAcquired = false;
        try
        {
            await _concurrencyLimiter.WaitAsync(linkedCts.Token);
            slotAcquired = true;
        }
        catch (OperationCanceledException)
        {
            // 排队等待期间被取消（客户端 /cancel/{id} 或服务器关停）：标记取消并落盘
            task.SetStatus(DownloadTaskStatus.Cancelled);
            task.TaskFinishTime = DateTimeOffset.Now.ToUnixTimeSeconds();
            await FinishTaskAndNotifyAsync(task, notifyWebhook);
            return;
        }

        string aid;
        try
        {
            aid = await BBDownUtil.GetAvIdAsync(option.Url, linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // 解析阶段被取消：分两种来源——用户/服务关停主动取消（linkedCts 已请求取消），
            // 与 HttpClient 超时抛的 TaskCanceledException（其 token 未取消，见 UrlResolver
            // FixAvidAsync 的同类判别）。ClassifyCancellation 区分两者，避免超时被误标
            // "已取消"掩盖解析失败。
            if (slotAcquired) _concurrencyLimiter.Release();
            task.SetAid(option.Url);
            task.TaskFinishTime = DateTimeOffset.Now.ToUnixTimeSeconds();
            var (cancelStatus, cancelMessage) = ClassifyCancellation(linkedCts.IsCancellationRequested, "解析请求超时或被中断");
            task.ErrorMessage = cancelMessage;
            task.SetStatus(cancelStatus);
            if (cancelStatus == DownloadTaskStatus.Failed)
                Logger.LogError($"解析链接失败: {SanitizeLogString(option.Url)} - {SanitizeLogString(cancelMessage)}");
            await FinishTaskAndNotifyAsync(task, notifyWebhook);
            return;
        }
        catch (Exception e)
        {
            // 链接无法解析时客户端已经收到 202 + JobId，必须把已入队的任务标记为失败
            // 并移入 finishedTasks，否则用户既等不到结果也查不到原因（查询/取消按 JobId 命中）。
            // Aid 没有可信值：保留原始 Url 便于用户在查询结果里辨认。
            if (slotAcquired) _concurrencyLimiter.Release();
            task.SetAid(option.Url);
            // 异常消息可能含绝对路径（IOException 等）：脱敏后再返回，避免经 /get-tasks
            // 泄露服务器文件系统布局（M2 路径泄露）。服务端日志保留原始消息便于排查。
            task.ErrorMessage = SanitizeErrorMessage(e.Message);
            task.TaskFinishTime = DateTimeOffset.Now.ToUnixTimeSeconds();
            task.SetStatus(DownloadTaskStatus.Failed);
            await FinishTaskAndNotifyAsync(task, notifyWebhook);
            Logger.LogError($"解析链接失败: {SanitizeLogString(option.Url)} - {SanitizeLogString(e.Message)}");
            return;
        }

        // 解析成功：任务命中的是 Aid（业务字段），JobId 保持不变
        task.SetAid(aid);
        try
        {
            task.SetStatus(DownloadTaskStatus.Running);
            var context = Program.SetUpWork(option);
            var (fetchedAid, vInfo, apiType, session) = await Program.GetVideoInfoAsync(option, context.AidOri, context.Input, linkedCts.Token);
            // GetVideoInfoAsync 在子异步流程中加载的凭据与提取的 wbi 不会自动回流父流程
            // （AsyncLocal 语义），这里在父流程内显式应用，确保后续 DownloadPagesAsync →
            // Parser.WbiSign 用上新密钥与本地凭据。
            if (session is not null) Core.Config.Apply(session);
            task.Title = vInfo.Title;
            task.Pic = vInfo.Pic;
            task.VideoPubTime = vInfo.PubTime;
            await Program.DownloadPagesAsync(option, vInfo, context with { AidOri = fetchedAid }, apiType, task, linkedCts.Token);
            task.SetStatus(DownloadTaskStatus.Succeeded);
        }
        catch (OperationCanceledException)
        {
            // 取消语义与解析阶段一致：主动取消标记 Cancelled；HttpClient 超时抛的
            // TaskCanceledException（token 未取消）是真实失败，标记 Failed 而非冒充取消。
            var (cancelStatus, cancelMessage) = ClassifyCancellation(linkedCts.IsCancellationRequested, "下载请求超时或被中断");
            task.ErrorMessage = cancelMessage;
            task.SetStatus(cancelStatus);
            if (cancelStatus == DownloadTaskStatus.Cancelled)
                Logger.LogDebug($"{aid} 任务被取消");
            else
                Logger.LogError($"{aid} 下载失败: {cancelMessage}");
        }
        // 捕获所有异常：任何漏网的异常类型都会跳过下方的收尾逻辑，
        // 使任务永久滞留在 runningTasks 中，之后再也无法重新下载。
        catch (Exception e)
        {
            bool debugMode = option.Debug || Config.Current.DebugLog;
            var displayMsg = debugMode ? e.ToString() : e.Message;
            // 异常消息可能含绝对路径：脱敏后再作为任务错误返回（服务端日志保留原文）。
            task.ErrorMessage = SanitizeErrorMessage(displayMsg);
            task.SetStatus(DownloadTaskStatus.Failed);
            Logger.LogError($"{aid} 下载失败: {e.Message}");
            Logger.LogDebug("异常详情: {0}", displayMsg);
        }
        finally
        {
            // 无论成功/取消/失败都释放并发占位
            if (slotAcquired) _concurrencyLimiter.Release();
        }
        task.TaskFinishTime = DateTimeOffset.Now.ToUnixTimeSeconds();
        if (task.IsSuccessful)
        {
            task.Progress = 1f;
            var elapsed = task.TaskFinishTime - task.TaskCreateTime;
            task.DownloadSpeed = elapsed > 0
                ? (double)(task.TotalDownloadedBytes / elapsed)
                : 0;
        }
        await FinishTaskAndNotifyAsync(task, notifyWebhook);
    }

    private async Task FinishTaskAndNotifyAsync(DownloadTask task, string? notifyWebhook)
    {
        FinishTask(task);
        await (CompletionNotifier ?? NotifyCompletionCallbackAsync)(task, notifyWebhook);
    }

    /// <summary>
    /// 任务终态收尾（四步一体）：状态/时间由调用方先行设置，这里统一做
    /// "释放取消源 → 从 runningTasks 移入 finishedTasks → 落盘"。
    /// 必须在 _taskLock 内 Dispose：/cancel 处理器在同一把锁内调用 Cancel()，
    /// 若在锁外 Dispose 会与取消路径竞争（对已释放 CTS 调 Cancel 抛 ObjectDisposedException）。
    /// 任何退出路径（排队取消 / 解析取消 / 解析失败 / 正常终态）漏掉它，都会让任务永久
    /// 滞留在 runningTasks 且不落盘，之后再无消费者——故四处共用这一份实现。
    /// </summary>
    private void FinishTask(DownloadTask task)
    {
        lock (_taskLock)
        {
            task.CancelCts.Dispose();
            runningTasks.Remove(task);
            finishedTasks.Add(task);
        }
        PersistFinishedTasks();
    }
}
