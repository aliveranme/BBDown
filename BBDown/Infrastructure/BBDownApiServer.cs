using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using BBDown.Core;
using BBDown.Core.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
namespace BBDown;

public partial class BBDownApiServer
{
    private WebApplication? app;
    private readonly object _taskLock = new();
    private readonly List<DownloadTask> runningTasks = [];
    private readonly List<DownloadTask> finishedTasks = [];
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly string? _serveToken;

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
    /// 认证失败限速状态：来源 IP → 最近失败时间戳。serve 是长驻进程，token 校验无
    /// 任何限速/锁定会让攻击者（同机进程、或非回环部署时的局域网/公网客户端）无限次
    /// 猜测 X-Serve-Token 而不触发任何告警或冷却。按来源 IP 在 1 分钟窗口内失败超过
    /// 阈值后返回 429，令暴力枚举失效。
    /// </summary>
    private readonly Dictionary<string, List<DateTime>> _authFailures = [];
    private readonly object _authFailuresLock = new();
    private const int MaxAuthFailuresPerMinute = 5;
    /// <summary>认证失败跟踪的 IP 上限：超过后清理过期条目，防一次性 IP 轰炸导致字典无限增长。</summary>
    private const int MaxTrackedAuthFailureIps = 1024;

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

    /// <summary>
    /// 是否信任直连反代追加的 X-Forwarded-For（--trusted-proxy）：启用后认证失败限速
    /// 按 XFF 末段计键，反代部署下多个客户端不再共享同一反代 IP。默认 false：不读 XFF，
    /// 否则任意客户端可直接伪造 XFF 更换限速计键绕过限速。
    /// </summary>
    private readonly bool _trustProxy;

    /// <summary>
    /// 服务端固定的任务完成回调地址（serve 启动时经 --notify-webhook 配置）。
    /// 只接受管理员在启动参数里显式配置的地址；客户端请求体中的回调字段一律忽略，
    /// 防止任意客户端让本机服务器向攻击者指定的地址 POST 任务数据（SSRF 横向面）。
    /// </summary>
    private readonly string? _notifyWebhook;

    /// <summary>
    /// 服务器就绪信号：Kestrel 开始监听后触发。测试用它等待服务器真正可连，
    /// 避免 WebApplication 启动慢（如 CI 首次运行）时测试立即发请求撞上
    /// Connection refused 竞态。生产代码不依赖此信号，仅作同步点。
    /// </summary>
    internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 下载任务的生命周期 token。必须独立于 HTTP 请求：
    /// Minimal API 注入的 CancellationToken 是 HttpContext.RequestAborted，
    /// 它在响应写完后即失效，会让后台下载在客户端拿到 200 的瞬间被取消。
    /// 该 token 只在服务器关停时触发。
    /// </summary>
    private readonly CancellationTokenSource _serverLifetimeCts = new();
    // 已完成任务持久化：serve 是长驻进程，任务记录只留在内存会在重启后丢失。
    // 默认写到进程当前目录；测试可通过构造函数注入临时路径，避免多实例互相污染
    private readonly string _taskFile;

    public BBDownApiServer(int maxConcurrent = 3, string? serveToken = null, string? taskFilePath = null, string? notifyWebhook = null, bool trustProxy = false)
    {
        // 防御：maxConcurrent <= 0 会让 SemaphoreSlim 构造抛 ArgumentOutOfRangeException，
        // serve 作为长驻进程应以可读错误退出而非崩溃
        _concurrencyLimiter = new SemaphoreSlim(Math.Max(1, maxConcurrent), Math.Max(1, maxConcurrent));
        _queryLimiter = new SemaphoreSlim(MaxConcurrentQueryHandlers, MaxConcurrentQueryHandlers);
        // 接受队列：并发执行 + 排队等待，上限 = maxConcurrent + maxConcurrent*8（可排队等待的数量）。
        // 上限不随请求数增长，serve 长驻进程的任务/CTS 堆积被限制在一个固定数量级内。
        int acceptCap = Math.Max(1, maxConcurrent) * (1 + MaxQueuedPerConcurrent);
        _acceptLimiter = new SemaphoreSlim(acceptCap, acceptCap);
        _serveToken = serveToken;
        _notifyWebhook = notifyWebhook;
        _trustProxy = trustProxy;
        _taskFile = taskFilePath ?? Path.Combine(Environment.CurrentDirectory, "bbdown-tasks.json");
    }

    public void SetupServer()
    {
        if (app is not null) return;
        LoadFinishedTasks();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.ConfigureHttpJsonOptions((options) =>
        {
            options.SerializerOptions.TypeInfoResolver = JsonTypeInfoResolver.Combine(options.SerializerOptions.TypeInfoResolver, AppJsonSerializerContext.Default);
        });
        app = builder.Build();
        // 服务器关停时取消仍在进行的下载，避免进程挂在未完成的任务上
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            if (!_serverLifetimeCts.IsCancellationRequested) _serverLifetimeCts.Cancel();
        });
        // 服务器就绪信号：Kestrel 实际开始监听后触发，测试据此等待可连接
        app.Lifetime.ApplicationStarted.Register(() => Ready.TrySetResult());
        // 认证与 CSRF 防护中间件（无条件安装）：
        // 1) token 认证（可选）：serve 配置了 --serve-token 后所有任务/查询端点要求
        //    X-Serve-Token 匹配，否则 401。非回环监听（0.0.0.0/具体网卡 IP）在 Run 阶段
        //    强制要求 token（见 Run 内的回环检查），本地回环监听未配置 token 时保持
        //    向后兼容（仅本机信任环境）。
        // 2) CSRF/跨源防护（无条件，写端点）：浏览器对 http://127.0.0.1:23333 的跨源
        //    POST 若用 text/plain（CORS 简单请求，不发预检）即可直接到达 /add-task——
        //    攻击者网页可借此借用操作者本地 B 站凭据提交下载任务、刷满接受队列（DoS）
        //    或取消任务；DNS rebinding（攻击者域名解析到 127.0.0.1）时 Origin 仍是攻击者
        //    域名，同样被拒。这里对写端点（/add-task、/cancel、/remove-finished）校验
        //    Origin 必须为回环来源，否则 403；/add-task 的请求体必须是 JSON Content-Type
        //    （text/plain 正是"跨源可发但不触发预检"的载体，直接拒绝）。只读端点
        //    （/get-tasks）不校验 Origin，本地管理页面/脚本可正常查询。
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            bool isApi = path.StartsWithSegments("/get-tasks")
                || path.StartsWithSegments("/add-task")
                || path.StartsWithSegments("/cancel")
                || path.StartsWithSegments("/remove-finished");
            if (isApi)
            {
                if (!string.IsNullOrEmpty(_serveToken) && !FixedTimeEquals(context.Request.Headers["X-Serve-Token"], _serveToken!))
                {
                    var clientIp = GetClientIp(context);
                    // 每次失败都记录日志：401 此前完全静默，暴力尝试对运维/用户不可见。
                    Logger.LogWarn($"serve 认证失败（401）: {clientIp} {context.Request.Path}");
                    if (IsAuthLockedOut(clientIp))
                    {
                        // 1 分钟窗口内失败超阈值：限速拒绝，令 X-Serve-Token 暴力枚举失效。
                        Logger.LogWarn($"serve 认证失败过于频繁，已限速: {clientIp}");
                        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                        return;
                    }
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                bool isWriteApi = path.StartsWithSegments("/add-task")
                    || path.StartsWithSegments("/cancel")
                    || path.StartsWithSegments("/remove-finished");
                if (isWriteApi)
                {
                    var origin = context.Request.Headers.Origin.ToString();
                    if (!string.IsNullOrEmpty(origin) && !IsLoopbackOrigin(origin))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }
                    if (path.StartsWithSegments("/add-task") && !IsJsonContentType(context.Request.ContentType))
                    {
                        context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                        return;
                    }
                }
            }
            await next();
        });
        var taskStatusApi = app.MapGroup("/get-tasks");
        // D8：查询端点并发上限。快照深拷贝是查询端点的真正成本（GetFinishedTasks 每请求
        // 深拷贝 running+finished 全部任务），无限并发会放大 CPU/GC。槽位不足返回 429。
        taskStatusApi.AddEndpointFilter(async (ctx, next) =>
        {
            if (!_queryLimiter.Wait(0))
            {
                return Results.Problem(
                    "查询过于频繁，请稍后再试",
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Too Many Queries");
            }
            try
            {
                return await next(ctx);
            }
            finally
            {
                _queryLimiter.Release();
            }
        });
        taskStatusApi.MapGet("/", handler: () =>
        {
            // Results.Json 的序列化发生在锁外（响应流式化阶段），必须在此快照集合，
            // 否则锁外遍历 runningTasks/finishedTasks 与并发 Add/RemoveAll 竞争，
            // 轻则读到不完整状态，重则抛集合修改异常。
            // 元素也需深拷贝：DownloadTask 被下载线程持续修改（SavePaths.Add、进度字段），
            // 共享对象在锁外序列化时仍会与写者竞争。
            List<DownloadTask> running, finished;
            lock (_taskLock)
            {
                running = runningTasks.Select(t => t.Snapshot()).ToList();
                finished = finishedTasks.Select(t => t.Snapshot()).ToList();
            }
            return Results.Json(new DownloadTaskCollection(running, finished), AppJsonSerializerContext.Default.DownloadTaskCollection);
        });
        taskStatusApi.MapGet("/running", handler: () =>
        {
            List<DownloadTask> snapshot;
            lock (_taskLock) { snapshot = runningTasks.Select(t => t.Snapshot()).ToList(); }
            return Results.Json(snapshot, AppJsonSerializerContext.Default.ListDownloadTask);
        });
        taskStatusApi.MapGet("/finished", handler: () =>
        {
            List<DownloadTask> snapshot;
            lock (_taskLock) { snapshot = finishedTasks.Select(t => t.Snapshot()).ToList(); }
            return Results.Json(snapshot, AppJsonSerializerContext.Default.ListDownloadTask);
        });
        taskStatusApi.MapGet("/{id}", (string id, CancellationToken token) =>
        {
            DownloadTask? task;
            lock (_taskLock)
            {
                // 匹配顺序：JobId 优先（/add-task 现在返回的是 JobId GUID，旧持久化记录
                // 无 JobId 时为空串）；其次回退到 Aid / 提交 Url，兼容旧客户端与旧记录。
                task = FindTaskByIdLocked(id, includeFinished: true, includeRunning: true);
            }
            if (task is null)
            {
                return Results.NotFound();
            }
            return Results.Json(task.Snapshot(), AppJsonSerializerContext.Default.DownloadTask);
        });
        app.MapPost("/add-task", (MyOptionBindingResult<ServeRequestOptions> bindingResult) =>
        {
            if (bindingResult.Exception is RequestBodyTooLargeException)
            {
                // 请求体超过 64KB 上限：与绑定层注释/实现对齐（此前注释承诺 413、实现与
                // 测试却是 400，三处不一致）。超大负载与 JSON 语法错误语义不同，用 413 区分。
                return Results.Problem("请求体过大", statusCode: StatusCodes.Status413PayloadTooLarge, title: "Payload Too Large");
            }
            if (!bindingResult.IsValid)
            {
                return Results.BadRequest("输入有误");
            }
            var req = bindingResult.Result!;
            // 安全边界：网络传入的执行路径/参数/代理一律忽略。
            // Aria2cArgs 会被拼入 aria2c 命令行、Aria2cPath 会覆盖静态进程路径，
            // 允许客户端控制这些字段等价于任意命令/程序执行（RCE）。
            SanitizeUntrustedOptions(req);
            // 任务完成回调只由服务端启动配置（--notify-webhook）决定，客户端请求体
            // 中的 CallBackWebHook 已被 SanitizeUntrustedOptions 清零，这里不再读取。
            // 使用服务器生命周期 token 而非请求 token，否则下载会随响应结束一同被取消。
            // ProcessDownloadTaskAsync 内部已收敛所有异常，此处兜底避免遗漏变成无人观察的 Task 异常。
            // 返回 JobId（GUID）：客户端可据此查询 /get-tasks/{id} 或取消 /cancel/{id}。
            // JobId 在任务入队时即生成，与 URL 解析出的 Aid 无关——完整 URL 提交后仍可查询/取消。
            // 用源生成上下文 + 202：Results.Accepted 走 Web 默认 camelCase 序列化，
            // 与 API 其余端点（PascalCase 源生成）不一致，且无法用 AOT 上下文类型化。
            // 先入队拿到 JobId：任何解析/下载都在锁外的后台任务中异步推进，
            // 客户端拿到 202 + JobId 后即可通过 /get-tasks/{id} 或 /cancel/{id} 命中。
            // 接受队列限流：任务总数（执行中 + 排队等待）达到上限后立即拒绝新请求，
            // 防止长驻进程被无限堆积的后台任务/配置对象/CTS 拖垮。
            if (!_acceptLimiter.Wait(0))
            {
                // URL 是客户端可控输入（可含 CRLF）：单行化后再进日志，避免日志注入面
                Logger.LogWarn($"任务队列已满，拒绝新任务: {SanitizeLogString(req.Url)}");
                return Results.Problem("任务队列已满，请稍后再试",
                    statusCode: StatusCodes.Status429TooManyRequests, title: "Too Many Requests");
            }
            var task = EnqueueDownloadTask(req);
            var bgTask = RunAcceptedTaskAsync(req, task);
            // 登记在途任务：服务关停时据此等待所有后台任务收尾
            lock (_inFlightLock) { _inFlightTasks.Add(bgTask); _inFlightJobIds.Add(task.JobId); }
            _ = bgTask.ContinueWith(t =>
            {
                lock (_inFlightLock) { _inFlightTasks.Remove(t); _inFlightJobIds.Remove(task.JobId); }
            }, TaskScheduler.Default);
            return Results.Json(new AddTaskAccepted(task.JobId), AppJsonSerializerContext.Default.AddTaskAccepted, statusCode: StatusCodes.Status202Accepted);
        });
        // 取消任务：仅对 running/queued 生效（finished 任务不可取消）。
        // 单独 Cts 触发后，正在下载的分片会经 CancellationToken 中止，队列中等待的
        // 任务会在占位释放后直接标记 Cancelled。
        // Cancel() 必须在 _taskLock 内调用：后台完成路径在同一把锁内 Dispose 该 CTS，
        // 若在锁外 Cancel 可能撞上已释放的令牌源抛 ObjectDisposedException。
        app.MapPost("/cancel/{id}", (string id) =>
        {
            DownloadTask? task;
            lock (_taskLock)
            {
                // 匹配顺序与 /get-tasks/{id} 一致：JobId 优先，其次 Aid / Url 回退。
                // Cancel() 必须在 _taskLock 内调用：后台完成路径在同一把锁内 Dispose 该 CTS，
                // 若在锁外 Cancel 可能撞上已释放的令牌源抛 ObjectDisposedException。
                task = FindTaskByIdLocked(id, includeFinished: false, includeRunning: true);
                if (task is null) return Results.NotFound();
                task.CancelCts.Cancel();
            }
            return Results.Ok();
        });
        var finishedRemovalApi = app.MapGroup("remove-finished");
        finishedRemovalApi.MapDelete("/", () =>
        {
            lock (_taskLock) { finishedTasks.RemoveAll(t => true); }
            PersistFinishedTasks();
            return Results.Ok();
        });
        finishedRemovalApi.MapDelete("/failed", () =>
        {
            lock (_taskLock) { finishedTasks.RemoveAll(t => !t.IsSuccessful); }
            PersistFinishedTasks();
            return Results.Ok();
        });
        finishedRemovalApi.MapDelete("/{id}", (string id) =>
        {
            lock (_taskLock) { finishedTasks.RemoveAll(t => MatchesTaskId(t, id)); }
            PersistFinishedTasks();
            return Results.Ok();
        });
    }

    /// <summary>
    /// 监听地址前置校验（同步）：URL 合法性与"非回环必须带 token"安全边界。
    /// 独立成同步方法：RunAsync 是 async 方法，校验异常会进入返回的 Task 而非
    /// 同步抛出，测试无法用 Assert.Throws 语义直接断言；ServeCommand 的同步
    /// 预检失败路径也依赖同步异常。RunAsync 启动前仍调用本方法兜底。
    /// </summary>
    public void ValidateListenUrl(string url)
    {
        if (app is null) return;
        bool result = Uri.TryCreate(url, UriKind.Absolute, out Uri? uriResult)
            && uriResult.Scheme == Uri.UriSchemeHttp;
        if (!result)
        {
            Logger.LogError($"{url} 不是合法的 http URL，url 示例：http://0.0.0.0:5000");
            Logger.LogWarn("如果您需要 https，请额外配置反向代理");
            // 抛异常而非仅设置 ExitCode：Environment.ExitCode 会被 Main 的返回值覆盖，
            // 导致监听地址无效时进程仍以 0 退出。ServeCommand 捕获后返回非零退出码。
            throw new ArgumentException($"{url} 不是合法的 http URL");
        }
        // 默认安全边界：非回环监听（0.0.0.0 / :: / 具体网卡 IP 等）会把任务提交/查询/取消
        // 端点暴露到局域网甚至公网，未配置 --serve-token 时任意来源都能提交下载任务并
        // 触碰本机磁盘。这里在启动前强制要求 token，拒绝以不安全配置启动。
        // 回环（127.0.0.1 / localhost / [::1] / ::1）仍是受信任的本地边界，保持向后兼容。
        if (!IsLoopbackListenAddress(uriResult!) && string.IsNullOrEmpty(_serveToken))
        {
            throw new InvalidOperationException(
                $"监听地址 {url} 不是回环地址（0.0.0.0 / :: / 具体网卡 IP 等），必须配置 --serve-token 才能启动，否则任意客户端都能提交任务并访问本机文件。");
        }
    }

    public async Task RunAsync(string url, CancellationToken cancellationToken = default)
    {
        ValidateListenUrl(url);
        if (app is null) return;
        app.Urls.Add(url);
        try
        {
            // RF-2：命令层已迁移 AsyncCommand，serve 全程 await——不再用
            // Task.Run + GetAwaiter().GetResult() 让一个线程池线程阻塞整个服务生命周期。
            await app.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 收到取消信号（如 Ctrl+C），正常退出
        }
        // 关停收尾：Kestrel 已停止接收新请求。取消共享令牌已在 ApplicationStopping
        // 触发（见 SetupServer 的注册）。这里限时等待所有在途后台任务完成
        // "取消 → 终止外部进程 → 持久化"，避免退出进程时遗留孤儿 ffmpeg/aria2c
        // 或丢失未落盘的任务记录。等待超时（例如下载器在取消后仍卡在外部进程上）
        // 则放弃等待，不无限阻塞退出。
        // Task.WaitAll 是有界同步等待且仅发生在进程退出路径（≤30s），与 RF-2 消除的
        // "整个服务生命周期占线程阻塞"不同，保留同步形式以避免 WhenAll+WaitAsync
        // 的异常类型变化（TimeoutException/首个业务异常）改变退出语义。
        Task[] inflight;
        lock (_inFlightLock) { inflight = [.. _inFlightTasks]; }
        if (inflight.Length > 0)
        {
            Logger.LogWarn($"正在等待 {inflight.Length} 个在途任务取消并收尾...");
            try
            {
                if (!Task.WaitAll(inflight, TimeSpan.FromSeconds(30)))
                {
                    // 升 Error 并枚举具体 JobId：此前仅 Warn 无明细，孤儿 ffmpeg/aria2c 无法事后定位
                    string[] stuckJobIds;
                    lock (_inFlightLock) { stuckJobIds = [.. _inFlightJobIds]; }
                    Logger.LogError($"部分在途任务未在 30 秒内完成收尾，已强制退出（可能遗留孤儿外部进程）: {string.Join(", ", stuckJobIds)}");
                }
            }
            catch (AggregateException)
            {
                // 个别任务取消路径抛出的异常不影响整体退出
            }
        }
        // 最后一次持久化：确保取消前已完成的任务记录落盘
        PersistFinishedTasks();
    }

    /// <summary>
    /// 令牌的常量时间比较：先 SHA-256 定长化（规避长度泄漏），再 FixedTimeEquals。
    /// 字符串 != 按序比较会在首个差异字符短路，理论上允许对高熵 serve token 做
    /// 时序侧信道逐位探测；哈希输出定长后每次比较耗时与输入内容无关。
    /// </summary>
    private static bool FixedTimeEquals(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(provided)),
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected)));
    }

    /// <summary>
    /// 错误消息路径脱敏：IOException 等异常消息常含绝对路径（如
    /// "Could not find file 'D:\data\video\xxx.mp4'"），经 /get-tasks 返回会泄露服务器
    /// 文件系统布局。把 Windows/Unix 绝对路径替换为相对名（仅保留文件名/末段），
    /// 供 <see cref="DownloadTask.ErrorMessage"/> 落盘/返回前调用。
    /// </summary>
    internal static string SanitizeErrorMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return message ?? "";
        return _absolutePathRegex().Replace(message, m => m.Groups["file"].Value);
    }

    /// <summary>
    /// 客户端可控字符串进日志前的单行化：URL/请求体等可含 CR/LF，
    /// 直接拼日志会破坏日志结构（日志注入面）。只替换控制字符，不改变可见内容。
    /// </summary>
    internal static string SanitizeLogString(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\r", "\\r").Replace("\n", "\\n");
    }

    /// <summary>匹配盘符/UNC/Unix 根绝对路径（含尾随文件名或目录段），保留路径最后一段。
    /// 形如 D:\a\b\c.mp4、\\server\share\x、/home/u/x.mp4；交替三种根前缀，
    /// 中间目录段任意、末尾段捕获为 file（含中文/空格等非分隔符字符）。
    /// Unix 根分支用 (?&lt;![/:\w]) 负向后顾：URL（http://x/y，/ 前是 : 或 /）与
    /// 相对路径（a/b/c，/ 前是路径字符）不被当作绝对路径替换，避免破坏消息里的 URL。</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(?:[A-Za-z]:\\|(?<![/:\w])/|\\\\)(?:[^\\/\r\n]+[\\/])*(?<file>[^\\/\r\n]+)")]
    private static partial System.Text.RegularExpressions.Regex _absolutePathRegex();

    /// <summary>
    /// 认证失败限速的计键：默认用 TCP 远端 IP；配置 --trusted-proxy 后信任
    /// X-Forwarded-For 的最后一个条目（由直连的可信反代追加，用它代表真实客户端），
    /// 反代部署下多个客户端不再共享同一反代 IP 而互相拖累限速。
    /// </summary>
    private string GetClientIp(Microsoft.AspNetCore.Http.HttpContext context)
    {
        if (_trustProxy)
        {
            var xff = context.Request.Headers["X-Forwarded-For"].ToString();
            var last = xff.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (!string.IsNullOrEmpty(last)) return last;
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    /// <summary>
    /// 认证失败限速判定：1 分钟滑动窗口内失败次数达到阈值返回 true（拒绝服务）。
    /// 每次失败都调用本方法登记；限速状态按来源 IP 隔离，同机合法客户端不受影响。
    /// 窗口过期的空条目会被移除，防止攻击者用大量一次性 IP 轰炸时字典无限增长
    /// （每 IP 一条永不清除的空 list）。攻击者持续用新 IP/XFF 值时每条都是"最近失败"
    /// 不过期，仅删过期条目约束不住字典大小——字典超过上限时按最后失败时间裁剪，
    /// 只保留最近活跃的 <see cref="MaxTrackedAuthFailureIps"/> 条。
    /// internal 供测试直接验证滑动窗口阈值。
    /// </summary>
    internal bool IsAuthLockedOut(string clientIp)
    {
        lock (_authFailuresLock)
        {
            var now = DateTime.UtcNow;
            // 攻击者换 IP 轰炸时字典条目会累积：超过上限则先全局清理过期条目
            //（O(n) 只发生在异常规模下，正常路径零开销）。
            if (_authFailures.Count > MaxTrackedAuthFailureIps)
            {
                foreach (var stale in _authFailures.Where(kv => kv.Value.Count == 0 || now - kv.Value[^1] > TimeSpan.FromMinutes(1)).ToList())
                    _authFailures.Remove(stale.Key);
                // 新 IP 持续轰炸时过期清理后仍超上限：按最后失败时间裁剪，保留最近
                // 活跃的上限条，其余移除，保证字典有界（O(n log n) 仅在异常规模触发）。
                if (_authFailures.Count > MaxTrackedAuthFailureIps)
                {
                    var overflow = _authFailures
                        .OrderBy(kv => kv.Value[^1]) // 最后失败时间最旧的最先裁剪
                        .Take(_authFailures.Count - MaxTrackedAuthFailureIps)
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (var key in overflow) _authFailures.Remove(key);
                }
            }
            if (!_authFailures.TryGetValue(clientIp, out var list))
            {
                _authFailures[clientIp] = [now];
                return false;
            }
            list.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
            if (list.Count == 0)
            {
                // 窗口内条目全部过期：移除键重建，避免保留空 list 条目
                _authFailures.Remove(clientIp);
                _authFailures[clientIp] = [now];
                return false;
            }
            if (list.Count >= MaxAuthFailuresPerMinute) return true;
            list.Add(now);
            return false;
        }
    }

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
    /// 写端点的跨源防护：Origin 头必须解析为回环来源（127.0.0.1/localhost/[::1]）。
    /// 浏览器跨源请求一定携带 Origin；DNS rebinding（攻击者域名解析到 127.0.0.1）下
    /// Origin 仍是攻击者域名而非回环来源，在此被拒。非浏览器客户端（curl/HttpClient）
    /// 不携带 Origin 头，字符串为空即放行，保持向后兼容。
    /// internal 供测试直接验证判定逻辑。
    /// </summary>
    internal static bool IsLoopbackOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        var host = uri.DnsSafeHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(host, out var ip)) return IPAddress.IsLoopback(ip);
        return false;
    }

    /// <summary>
    /// /add-task 请求体必须为 JSON：text/plain 是 CORS 简单请求的合法 Content-Type，
    /// 浏览器跨源可"不发预检"直接发出，正是攻击者网页驱动本机 serve 提交任务的载体。
    /// 仅接受 application/json 或 application/*+json；无 Content-Type（空 body 或旧客户端）
    /// 放行交由绑定层返回 400/422。
    /// </summary>
    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return true;
        var mediaType = contentType.Split(';')[0].Trim();
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 监听地址是否属于本机回环：127.0.0.1、localhost、[::1]、::1。
    /// 通配监听 0.0.0.0 / :: 与具体网卡 IP 一律视为非回环。
    /// 用 DnsSafeHost（IPv6 字面量不带方括号）以便 IPAddress.TryParse 解析 [::1]。
    /// </summary>
    private static bool IsLoopbackListenAddress(Uri listenUri)
    {
        var host = listenUri.DnsSafeHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(host, out var ip)) return IPAddress.IsLoopback(ip);
        return false;
    }

    // 串行化任务文件的写入：多任务并发完成时若直接 File.WriteAllText，
    // 后写者会因 FileShare.None 抛 IOException 被吞成日志，丢失刚完成任务的记录。
    // 快照生成也在该锁内进行（见 PersistFinishedTasks），锁顺序固定为 _persistLock → _taskLock。
    private static readonly object _persistLock = new();

    // 连续持久化失败计数：磁盘满等持续性故障升级为 LogError（单次瞬时失败 LogWarn 即可）。
    // 写盘成功时清零。磁盘满时任务记录会静默消失，必须留下可观测痕迹。
    private int _persistFailures;

    // 保留策略：已完成任务列表最多保留条数 / 最大保留天数。
    // serve 是长驻进程，任务记录无限累积会让 bbdown-tasks.json 无限膨胀。
    private const int MaxFinishedTasks = 1000;
    private static readonly TimeSpan FinishedTaskRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// 把已完成任务快照写入磁盘，serve 重启后可恢复。
    /// 原子写：先写临时文件、flush 到磁盘，再 File.Move 覆盖正式文件，
    /// 中途进程崩溃/断电不会留下半截 JSON 覆盖掉上一份有效状态。
    /// 写失败只降级为日志，不影响下载流程。
    /// 锁顺序：快照生成（Trim + 拷贝）与写盘都在 _persistLock 内完成，先取 _persistLock
    /// 再取 _taskLock。所有 PersistFinishedTasks() 调用点都发生在 _taskLock 释放之后，
    /// 因此与调用方持有的 _taskLock 不构成循环等待，不会死锁。
    /// </summary>
    private void PersistFinishedTasks()
    {
        try
        {
            lock (_persistLock)
            {
                List<DownloadTask> snapshot;
                lock (_taskLock)
                {
                    // 写盘前先按保留策略截断，避免列表膨胀
                    TrimFinishedTasksLocked();
                    snapshot = finishedTasks.Select(t => t.Snapshot()).ToList();
                }
                var json = JsonSerializer.Serialize(snapshot, AppJsonSerializerContext.Default.ListDownloadTask);
                var tmpFile = _taskFile + ".tmp";
                using (var fs = new FileStream(tmpFile, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmpFile, _taskFile, overwrite: true);
            }
            // 写盘成功：清零连续失败计数
            Interlocked.Exchange(ref _persistFailures, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 升 Warn：任务记录落盘失败意味着重启后记录丢失，此前仅 LogDebug（默认抑制）
            // 会让磁盘满等故障完全无痕。连续失败升级 Error（持续性故障）。
            int failures = Interlocked.Increment(ref _persistFailures);
            if (failures >= 3)
                Logger.LogError($"持久化任务记录连续失败 {failures} 次: {ex.Message}");
            else
                Logger.LogWarn($"持久化任务记录失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 在 <see cref="_taskLock"/> 持锁前提下，按保留策略截断已完成任务列表：
    /// 超龄记录与超出 <see cref="MaxFinishedTasks"/> 的溢出记录被移除。
    /// 列表按完成顺序追加，与创建顺序无关——裁剪溢出时必须按创建时间排序，
    /// 移除最旧创建的，保留最新的（此前直接 RemoveRange 头部会误删"后创建但先完成"的任务）。
    /// </summary>
    private void TrimFinishedTasksLocked()
    {
        if (finishedTasks.Count == 0) return;
        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        // 超龄优先移除；保留下来的仍超过上限则按创建时间保留最新的
        var cutoff = now - (long)FinishedTaskRetention.TotalSeconds;
        finishedTasks.RemoveAll(t => t.TaskCreateTime < cutoff);
        if (finishedTasks.Count > MaxFinishedTasks)
        {
            // 只移除最旧创建的溢出条目，保留其余任务的原顺序（API 按完成顺序展示）
            var toRemove = finishedTasks
                .OrderBy(t => t.TaskCreateTime)
                .Take(finishedTasks.Count - MaxFinishedTasks)
                .ToHashSet();
            finishedTasks.RemoveAll(t => toRemove.Contains(t));
        }
    }

    /// <summary>
    /// serve 启动时恢复上次运行留下的已完成任务记录。
    /// 文件损坏时静默忽略，且不因恢复出的记录破坏启动。
    /// </summary>
    private void LoadFinishedTasks()
    {
        try
        {
            if (!File.Exists(_taskFile)) return;
            var json = File.ReadAllText(_taskFile);
            var loaded = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListDownloadTask);
            if (loaded is null) return;
            lock (_taskLock)
            {
                finishedTasks.AddRange(loaded);
                TrimFinishedTasksLocked();
            }
            Logger.LogDebug("已恢复 {0} 条历史任务记录", loaded.Count);
            // 在途任务不持久化：正常关停会在退出前等待在途任务收尾并落盘，
            // 但进程被杀/断电等异常退出会让上次的排队/下载中任务永久丢失且无提示。
            // 启动时给出提示，让运维知道哪些记录可能缺失。
            Logger.LogWarn($"serve 重启：已恢复 {loaded.Count} 条历史任务记录；排队/下载中的在途任务不持久化，异常退出后不会恢复");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 升 Warn：加载失败意味着上次的全部任务记录无法恢复（损坏/权限/磁盘故障），
            // 此前仅 LogDebug（默认抑制）会让记录静默丢失。
            Logger.LogWarn($"加载历史任务记录失败（记录可能丢失）: {ex.Message}");
        }
    }

    /// <summary>
    /// 清除网络请求体中可能导致任意命令/程序执行或凭据外泄的字段。
    /// Aria2cArgs 会拼入 aria2c 命令行、Aria2cPath 会覆盖静态进程路径、
    /// Aria2cProxy 会追加进 Aria2cArgs —— 三者都不允许客户端控制。
    /// FFmpegPath/Mp4boxPath/WvdPath/Mp4decryptPath 同属"让服务器执行指定程序"的字段，
    /// 允许客户端控制等价于选择任意已存在的可执行文件。
    /// WorkDir 虽已不写进程 CWD（ChangeWorkingDir 在 serve 下改为 AsyncLocal 按任务隔离，
    /// 见 AppSettings.WorkDir），但它与 FilePattern 同属"客户端控制服务端输出位置"的
    /// 任意写面（可把下载写到服务器任意可写目录）——一并忽略，serve 任务固定输出到服务端 CWD。
    /// </summary>
    internal static void SanitizeUntrustedOptions(ServeRequestOptions req)
    {
        req.Aria2cArgs = "";
        req.Aria2cPath = "";
        req.Aria2cProxy = "";
        req.FFmpegPath = "";
        req.Mp4boxPath = "";
        req.WvdPath = "";
        req.Mp4decryptPath = "";
        req.WorkDir = "";
        // Insecure 会全局关闭 TLS 证书校验：serve 默认无 token，任意客户端 POST /add-task
        // 携带 {"insecure":true} 即可让携带操作者 SESSDATA 的请求跳过 TLS 校验被中间人截获。
        // serve 强制启用 TLS 校验，忽略该字段。
        req.Insecure = false;
        // ForceHttp 会把媒体 CDN 的 https 改写为明文 http（ReplaceUrl），而下载请求仍携带
        // 操作者的 SESSDATA Cookie（BBDownDownloadUtil 的 Range/HEAD/GET 均带 Cookie）。
        // 与 Insecure 同类威胁模型：任意客户端 POST /add-task 携带 {"forceHttp":true}
        // 即可让携带凭据的媒体流量走明文，被同一链路上的中间人截获整账号凭据。
        // serve 强制 https，忽略该字段。
        req.ForceHttp = false;
        // UserAgent 现按异步流隔离（Config.Current.UserAgent，经 SetUpWork 写入）；
        // serve 下仍统一清零：客户端不能控制服务端出站请求的 UA 指纹。
        req.UserAgent = "";
        // NotifyWebhook 是 CLI 功能：serve 的 /add-task 请求体若携带它，会绕过
        // CallBackWebHook 的 SSRF 校验，让服务器向攻击者指定的任意地址 POST 任务数据。
        req.NotifyWebhook = "";
        // 任务完成回调改为服务端 allowlist：只接受 serve 启动时 --notify-webhook 配置的
        // 固定地址，客户端请求体中的 CallBackWebHook 一律清零（此前仅靠 IsSafeCallbackUrlAsync
        // 校验后仍接受客户端传值——现在完全忽略客户端回调，杜绝任意客户端驱动本机 POST）。
        // FilePattern/MultiFilePattern 会被 SetUpWork 当作 savePathFormat 拼进保存路径，
        // FormatSavePath 只替换占位符、字面量里的 ".." 段原样保留，BBDownMuxer 会按 savePath
        // 建目录——攻击者可借此任意创建目录/写入文件（路径穿越面）。serve 任务一律回落默认模板。
        req.FilePattern = "";
        req.MultiFilePattern = "";
        // DrmKeyHex/DrmKidHex 会经 DecryptDrmAsync 写入 mp4decrypt 的 key-file 参与解密，
        // 是客户端可控的密钥注入点。serve 任务一律回落 device.wvd 自动取钥；
        // 需要手动 --key/--kid 的操作者应使用 CLI 而非 API。
        req.DrmKeyHex = "";
        req.DrmKidHex = "";
        // 任务完成回调只由服务端启动配置（--notify-webhook）决定，客户端请求体中的
        // 回调字段被完全忽略（服务端 allowlist，不接受客户端指定）。
        req.CallBackWebHook = "";

        // 限制重试参数：客户端传入失控的重试次数或超长延迟会在共享并发槽内阻塞长达数十天，
        // 占满并发槽并使服务瘫痪。将 API 任务重试次数限制在 [1, 3]，延迟限制在 [0, 5000]ms 内。
        // 下限 1：显式传 0 会被 ValidateNumericOptions 的 --retry-count ≥ 1 判为非法任务 Failed，
        // 与这里 Clamp(0,3) 矛盾（A4）——统一为 [1,3]，0 自动升为 1。
        req.RetryCount = Math.Clamp(req.RetryCount, 1, 3);
        req.RetryDelay = Math.Clamp(req.RetryDelay, 0, 5000);

        // 慢速 DoS 面：ValidateNumericOptions 的合法上界（MuxerTimeout 35000 分钟≈583h、
        // DelayPerPage 600s/分P、ThreadSegmentSize 1024MB）允许 API 客户端用合法值占满
        // 并发槽数小时、填满 accept 队列。serve 的受控环境收紧到实际使用范围：
        // 混流最长 2 小时、分P 间隔最长 30s、分片最大 64MB。CLI 仍走完整 ValidateNumericOptions。
        req.MuxerTimeout = Math.Clamp(req.MuxerTimeout, 1, 120);
        req.DelayPerPage = Math.Clamp(req.DelayPerPage, 0, 30);
        req.ThreadSegmentSize = Math.Clamp(req.ThreadSegmentSize, 1, 64);

        // Debug 字段若受客户端控制，任务失败时会将服务端完整异常堆栈暴露在 /get-tasks API 中。
        req.Debug = false;

        // host 字段决定凭据（Cookie/access_token）的发送目标：serve 默认无认证，
        // 若不校验，任意客户端可把请求指向自己的服务器，骗取操作者保存在
        // BBDown.data 的 B 站 Cookie（LoadCredentials 会在任务流中加载）。
        // 仅允许 B 站官方域名；空值/非官方一律回落官方默认值，并规范化剥离 scheme 前缀。
        req.Host = NormalizeHost(req.Host, "api.bilibili.com");
        req.EpHost = NormalizeHost(req.EpHost, "api.bilibili.com");
        req.TvHost = NormalizeHost(req.TvHost, "api.snm0516.aisee.tv");
        req.UposHost = string.IsNullOrWhiteSpace(req.UposHost) || !IsOfficialHost(req.UposHost)
            ? ""
            : (Uri.TryCreate(req.UposHost, UriKind.Absolute, out var uposUri) ? uposUri.Host : req.UposHost.Trim());
    }

    private static string NormalizeHost(string? host, string defaultHost)
    {
        if (string.IsNullOrWhiteSpace(host) || !IsOfficialHost(host)) return defaultHost;
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri))
            return uri.Host;
        return host.Trim();
    }

    /// <summary>
    /// host 字段是否指向 B 站官方域名。空值视为合法（回落默认）；
    /// 支持 "api.bilibili.com" 与 "https://api.bilibili.com" 两种写法。
    /// 只接受规范化的纯主机名：带路径/斜杠/用户信息/非默认端口等形态一律拒绝，
    /// 防止攻击者用 "evil.com/.bilibili.com" 这类斜杠混淆串在纯后缀匹配下被放行，
    /// 使携带操作者 SESSDATA 的请求发往攻击者主机。
    /// </summary>
    internal static bool IsOfficialHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;

        string hostname = host;
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri))
        {
            // 带 scheme 的写法：必须 http/https、无 userinfo、无路径/query/fragment、默认端口
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            if (!string.IsNullOrEmpty(uri.UserInfo)) return false; // userinfo 可伪装信任域
            if (uri.AbsolutePath.Length > 1) return false;         // 非空路径（含 "/" 以外的路径段）
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
            if (!uri.IsDefaultPort) return false;
            hostname = uri.Host;
        }
        else if (host.Contains('/') || host.Contains('\\') || host.Contains('@') ||
                 host.Contains('?') || host.Contains('#') || host.Contains(':') || host.Contains('['))
        {
            // 非绝对 URL 却含协议/路径/用户信息/端口/IPv6 字面量等分隔符：不是合法纯主机名，拒绝
            return false;
        }

        return HTTPUtil.IsOfficialBilibiliHost(hostname);
    }

    /// <summary>
    /// 回调地址 SSRF 防护：仅允许 http/https 绝对地址，
    /// 且禁止指向回环（127.0.0.1/::1/localhost）、链路本地（169.254.x / fe80::）与
    /// 云元数据面（169.254.0.0/16）。RFC1918 私网段仅在回调 URL 是**字面 IP** 时放行
    /// （操作者直接配置的局域网回调是 serve 正常用法）；经域名解析出的内网地址一律拒绝，
    /// 防止攻击者用解析到内网的域名（DNS 重绑定）把回调打向内网。
    /// dnsResolver 供测试注入（生产用系统 DNS）。
    /// 注意：域名在"配置时校验"与"回调时刻连接"之间存在 DNS 重绑定窗口（短 TTL 域名
    /// 可先解析为公网通过校验、回调时改指 169.254.169.254/内网）。因此每次回调前
    /// （NotifyCompletionCallbackAsync）都会再次调用本方法复查，并在 SendCallbackAsync 中
    /// 用 ConnectCallback 把连接绑定到本次校验解析出的 IP，把窗口压缩到连接前瞬间。
    /// 异步解析（GetHostAddressesAsync）：回调路径在 async 流程中，同步 DNS 会阻塞线程池线程。
    /// </summary>
    internal static async Task<bool> IsSafeCallbackUrlAsync(string? url, Func<string, Task<IPAddress[]>>? dnsResolver = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return true; // 未配置回调视为合法
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        bool hostIsLiteralIp = IPAddress.TryParse(uri.DnsSafeHost, out var literalIp) && literalIp is not null;

        if (hostIsLiteralIp)
        {
            // IPv4-mapped IPv6（如 [::ffff:169.254.169.254]）会把下方的 169.254 检查绕过
            // （其 AddressFamily 是 InterNetworkV6），统一映射回 IPv4 后再做检查。
            if (literalIp!.IsIPv4MappedToIPv6) literalIp = literalIp.MapToIPv4();
            // 字面 IP 是操作者显式配置的地址：仅拦回环/链路本地/云元数据。
            // RFC1918 内网字面 IP 放行（局域网回调是 serve 正常用法），
            // 因为字面 IP 不涉及 DNS 重绑定，攻击者无法借它打内网——攻击者构造的
            // "域名回调"永远走下方 DNS 解析分支（RFC1918 已拒绝）。若需进一步收紧，
            // 局域网回调用户应配置 --serve-token 或前置反向代理。
            if (IPAddress.IsLoopback(literalIp)) return false;
            if (literalIp.IsIPv6LinkLocal) return false;
            if (literalIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = literalIp.GetAddressBytes();
                if (b.Length == 4 && b[0] == 169 && b[1] == 254) return false; // 169.254.0.0/16 云元数据面
                if (b.All(x => x == 0)) return false; // 0.0.0.0：连接时绑定到回环
            }
            else if (literalIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                var b6 = literalIp.GetAddressBytes();
                if (b6.Length == 16 && b6.All(x => x == 0)) return false; // [::]
            }
            return true;
        }

        // DnsSafeHost：IPv6 字面量不带方括号（uri.Host 对 [::1] 会带方括号，IPAddress.TryParse
        // 必然失败 → 落到下方 DNS 分支被当成主机名解析，字面 IP 检查从未真正执行
        //（测试假阳性）；且 Dns.GetHostAddressesAsync("[::1]") 解析失败会把管理员配置的
        // 公网 IPv6 webhook 永久判为不合法，与 SendCallbackAsync（已用 DnsSafeHost）矛盾。
        var host = uri.DnsSafeHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        // 域名回调的 DNS 重绑定缺口：攻击者注册解析到 169.254.169.254 / 内网地址的域名
        // （如 metadata.google.internal），仅比对字符串会放行，任务完成时 HttpClient 才解析 DNS。
        // 这里提前解析并校验全部地址；域名解析出的任一地址命中回环/链路本地/169.254/RFC1918/ULA
        // 即拒绝——内网地址只允许"字面 IP"形式的显式配置，域名一律要求公网可达。
        try
        {
            var addresses = dnsResolver is not null ? await dnsResolver(host) : await Dns.GetHostAddressesAsync(host);
            foreach (var addr in addresses)
            {
                var resolvedIp = addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr;
                if (IsBlockedAddress(resolvedIp)) return false;
            }
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            // 域名无法解析：回调必然失败，按不安全处理
            return false;
        }
    }

    /// <summary>
    /// 判定地址是否属于应拒绝的敏感/内网段：回环、链路本地、云元数据（169.254/16）、
    /// RFC1918 私网段（10/8、172.16/12、192.168/16）、CGNAT（100.64/10）与 IPv6 ULA（fc00::/7）。
    /// </summary>
    private static bool IsBlockedAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b.Length != 4) return false;
            // 169.254.0.0/16 云元数据面
            if (b[0] == 169 && b[1] == 254) return true;
            // RFC1918：10.0.0.0/8、172.16.0.0/12、192.168.0.0/16
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            // CGNAT 100.64.0.0/10
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            return false;
        }
        // IPv6 ULA fc00::/7
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return b.Length == 16 && (b[0] & 0xfe) == 0xfc;
        }
        return false;
    }

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
    private async Task RunAcceptedTaskAsync(MyOption option, DownloadTask task)
    {
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
            lock (_taskLock)
            {
                task.CancelCts.Dispose();
                runningTasks.Remove(task);
                finishedTasks.Add(task);
            }
            PersistFinishedTasks();
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
                Logger.LogError($"解析链接失败: {option.Url} - {cancelMessage}");
            lock (_taskLock)
            {
                task.CancelCts.Dispose();
                runningTasks.Remove(task);
                finishedTasks.Add(task);
            }
            PersistFinishedTasks();
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
            lock (_taskLock)
            {
                task.CancelCts.Dispose();
                runningTasks.Remove(task);
                finishedTasks.Add(task);
            }
            PersistFinishedTasks();
            Logger.LogError($"解析链接失败: {option.Url} - {e.Message}");
            return;
        }

        // 解析成功：任务命中的是 Aid（业务字段），JobId 保持不变
        task.SetAid(aid);
        try
        {
            task.SetStatus(DownloadTaskStatus.Running);
            var (encodingPriority, dfnPriority, firstEncoding, downloadDanmaku, downloadDanmakuFormats, input, savePathFormat, lang, aidOri, delay) = Program.SetUpWork(option);
            var (fetchedAid, vInfo, apiType, session) = await Program.GetVideoInfoAsync(option, aidOri, input, linkedCts.Token);
            // GetVideoInfoAsync 在子异步流程中加载的凭据与提取的 wbi 不会自动回流父流程
            // （AsyncLocal 语义），这里在父流程内显式应用，确保后续 DownloadPagesAsync →
            // Parser.WbiSign 用上新密钥与本地凭据。
            if (session is not null) Core.Config.Apply(session);
            task.Title = vInfo.Title;
            task.Pic = vInfo.Pic;
            task.VideoPubTime = vInfo.PubTime;
            await Program.DownloadPagesAsync(option, vInfo, encodingPriority, dfnPriority, firstEncoding, downloadDanmaku, downloadDanmakuFormats,
                        input, savePathFormat, lang, fetchedAid, delay, apiType, task, linkedCts.Token);
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
        // 任务结束后释放它的取消令牌源，避免长驻进程里每个任务都残留一个 CTS。
        // 必须在 _taskLock 内 Dispose：/cancel 处理器在同一把锁内调用 Cancel()，
        // 若在锁外 Dispose 会与取消路径竞争（对已释放 CTS 调 Cancel 抛 ObjectDisposedException）。
        lock (_taskLock)
        {
            task.CancelCts.Dispose();
            runningTasks.Remove(task);
            finishedTasks.Add(task);
        }
        PersistFinishedTasks();

        await NotifyCompletionCallbackAsync(task, notifyWebhook);
    }

    /// <summary>
    /// 按服务端启动配置（--notify-webhook）发送任务完成回调。客户端请求体中的回调
    /// 字段已被 SanitizeUntrustedOptions 清零，这里只使用管理员显式配置的固定地址。
    /// </summary>
    private async Task NotifyCompletionCallbackAsync(DownloadTask task, string? notifyWebhook)
    {
        if (string.IsNullOrEmpty(notifyWebhook)) return;
        // 回调连接前复查 SSRF：--notify-webhook 是管理员显式配置的固定地址（服务端
        // allowlist），但仍需拦截回环/链路本地/云元数据等敏感目标。DNS 重绑定风险说明：
        // 域名回调在"启动时校验"与"回调连接时刻"之间可能存在解析结果变化（短 TTL 域名），
        // 因此每次回调前都重新校验；并在 SendCallbackAsync 中用 ConnectCallback 把连接
        // 绑定到本次校验解析出的 IP，避免 HttpClient 再次做 DNS 解析（消除重绑定窗口）。
        if (!await IsSafeCallbackUrlAsync(notifyWebhook))
        {
            Logger.LogWarn($"回调地址不合法，已跳过本次回调: {notifyWebhook}");
            return;
        }
        // 序列化共享对象前用 Snapshot() 深拷贝：与 /get-tasks 查询端点一致，
        // 避免未来任何并发写者在序列化期间修改 SavePaths 引发竞态。
        string jsonContent = JsonSerializer.Serialize(task.Snapshot(), AppJsonSerializerContext.Default.DownloadTask);
        await SendCallbackAsync(notifyWebhook, jsonContent);
    }

    /// <summary>
    /// 向固定回调地址 POST 任务快照。用 SocketsHttpHandler.ConnectCallback 把 TCP 连接
    /// 绑定到 IsSafeCallbackUrlAsync 本次校验解析出的 IP：HTTP 请求的 Host（SNI）仍是原域名，
    /// 但实际连到的地址来自已校验的解析结果，杜绝"校验用一套 DNS、连接用另一套 DNS"
    /// 的 TOCTOU 重绑定窗口。每个回调请求独立创建 handler（连接不复用），代价最小。
    /// 校验语义与 <see cref="IsSafeCallbackUrlAsync"/> 保持一致：字面 IP（管理员配置的局域网
    /// 回调）仅拦回环/链路本地/云元数据，域名解析出的内网地址一律拒绝。
    /// </summary>
    private static async Task SendCallbackAsync(string webhook, string jsonContent)
    {
        try
        {
            var uri = new Uri(webhook);
            // DnsSafeHost：IPv6 字面量不带方括号（uri.Host 对 [::1] 会带方括号，无法解析）
            var host = uri.DnsSafeHost;
            bool hostIsLiteralIp = IPAddress.TryParse(host, out var literalIp) && literalIp is not null;
            IPAddress target;
            if (hostIsLiteralIp)
            {
                // 字面 IP 是管理员显式配置的地址：直接绑定到该 IP（与 IsSafeCallbackUrlAsync 的
                // 字面 IP 分支一致，RFC1918 局域网回调放行）。此处不再解析 DNS。
                literalIp = literalIp!.IsIPv4MappedToIPv6 ? literalIp.MapToIPv4() : literalIp;
                if (IsUnsafeLiteralIpAddress(literalIp))
                {
                    Logger.LogWarn($"回调地址是敏感字面 IP，已跳过本次回调: {webhook}");
                    return;
                }
                target = literalIp;
            }
            else
            {
                // 域名回调：解析一次并校验全部地址；任一地址命中敏感/内网段即跳过。
                // 与 IsSafeCallbackUrlAsync 使用同一套 DNS 解析逻辑（域名重绑定校验在此完成）。
                IPAddress[] addresses;
                try
                {
                    addresses = await Dns.GetHostAddressesAsync(host);
                }
                catch (SocketException)
                {
                    Logger.LogWarn($"回调地址 DNS 解析失败，已跳过本次回调: {webhook}");
                    return;
                }
                foreach (var addr in addresses)
                {
                    var resolvedIp = addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr;
                    if (IsBlockedAddress(resolvedIp))
                    {
                        Logger.LogWarn($"回调地址解析到敏感地址，已跳过本次回调: {webhook}");
                        return;
                    }
                }
                // 解析结果可能含多个地址（已全部校验通过），取第一个连接
                target = addresses[0].IsIPv4MappedToIPv6 ? addresses[0].MapToIPv4() : addresses[0];
            }

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(target, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
            // ConnectCallback 已绑定目标 IP；SNI 由 HttpClient 依据请求 URI 的原 host 设置，
            // 因此请求 URI 保留原 webhook 而不是替换成 IP
            using var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync(webhook, content);
            if (!resp.IsSuccessStatusCode)
            {
                // 升 Warn：回调失败意味着通知静默丢失，此前仅 LogDebug（默认抑制）无痕。
                Logger.LogWarn($"回调返回 HTTP {(int)resp.StatusCode}: {webhook}");
            }
        }
        // TaskCanceledException 也要接住：回调 HttpClient.Timeout（2 分钟）触发时抛它且
        // token 未取消，若不进此过滤器会一路冒泡到 RunAcceptedTaskAsync 的 catch(Exception)，
        // 对一个已成功且已持久化的任务打印误导性的"任务异常终止"。
        catch (Exception e) when (e is HttpRequestException or UriFormatException or InvalidOperationException or SocketException or TaskCanceledException)
        {
            // 升 Warn：回调失败意味着通知静默丢失（任务本身已成功），需可观测。
            Logger.LogWarn($"回调失败: {e.Message}");
        }
    }

    /// <summary>
    /// 字面 IP 回调的敏感地址判定：与 <see cref="IsSafeCallbackUrlAsync"/> 的字面 IP 分支一致，
    /// 仅拦回环、链路本地、云元数据面（169.254/16）与全零地址；RFC1918 私网放行
    /// （管理员显式配置的局域网回调是 serve 正常用法）。
    /// </summary>
    private static bool IsUnsafeLiteralIpAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b.Length != 4) return false;
            if (b[0] == 169 && b[1] == 254) return true; // 169.254.0.0/16 云元数据面
            if (b.All(x => x == 0)) return true;         // 0.0.0.0：连接时绑定到回环
            return false;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b6 = ip.GetAddressBytes();
            return b6.Length == 16 && b6.All(x => x == 0); // [::]
        }
        return false;
    }
}

public enum DownloadTaskStatus
{
    /// <summary>已接受，等待进入执行（受并发限制器约束）。</summary>
    Queued,
    /// <summary>正在下载/混流中。</summary>
    Running,
    /// <summary>执行完毕且成功。</summary>
    Succeeded,
    /// <summary>执行完毕但失败。</summary>
    Failed,
    /// <summary>被客户端取消。</summary>
    Cancelled,
}

public record DownloadTask(string Aid, string Url, long TaskCreateTime)
{
    /// <summary>
    /// 任务唯一标识（GUID 字符串）。新任务在入队（EnqueueDownloadTask）时立即生成并随
    /// 202 响应返回，客户端据此查询 /get-tasks/{id} 或取消 /cancel/{id}——与 URL 解析出的
    /// Aid 无关，完整视频 URL 提交后仍可命中。默认空串：旧持久化记录反序列化时无该字段
    /// 即为空，查询/取消端点会回退到 Aid / Url 匹配（见 MatchesTaskId）。
    /// </summary>
    [JsonInclude]
    public string JobId { get; set; } = "";

    /// <summary>
    /// 视频解析出的 Aid（业务字段，不再作为任务唯一标识）。
    /// 显式声明以遮蔽 record 主构造参数自动生成的 init-only 属性：入队时 Aid 是提交 Url，
    /// 解析成功/失败后经 <see cref="SetAid"/> 更新为真实 Aid（或失败时的回退值）。
    /// </summary>
    [JsonInclude]
    public string Aid { get; set; } = Aid;

    [JsonInclude]
    public string? Title = null;
    [JsonInclude]
    public string? Pic = null;
    [JsonInclude]
    public long? VideoPubTime = null;
    [JsonInclude]
    public long? TaskFinishTime = null;
    [JsonInclude]
    public double Progress = 0f;
    [JsonInclude]
    public double DownloadSpeed = 0f;
    [JsonInclude]
    public double TotalDownloadedBytes = 0f;
    [JsonInclude]
    public bool IsSuccessful = false;
    [JsonInclude]
    public string? ErrorMessage = null;
    [JsonInclude]
    public DownloadTaskStatus Status = DownloadTaskStatus.Queued;

    /// <summary>
    /// 每个任务独立的取消令牌源：serve 是长驻进程，任务在后台队列中执行，
    /// 全局 _serverLifetimeCts 只负责关停时全量取消；客户端可通过 /cancel/{id}
    /// 单独取消某个任务。等待进入队列的任务也可以被取消（取消前先释放信号量占位）。
    /// </summary>
    [JsonIgnore]
    public CancellationTokenSource CancelCts { get; } = new();

    [JsonInclude]
    public List<string> SavePaths = new();

    // 保护 SavePaths 的读写锁：下载线程持续 Add，而 /get-tasks 的 Snapshot 深拷贝会枚举
    // SavePaths，若撞上并发 Add 抛 InvalidOperationException（List 版本变更）。
    // 写者一律经 AddSavePath 走这把锁；_savePathLock 不能是 primary constructor 属性。
    private readonly object _savePathLock = new();

    /// <summary>受控写入口：与 Snapshot 的深拷贝在同一把锁下，避免枚举期间被并发修改。</summary>
    public void AddSavePath(string path)
    {
        lock (_savePathLock) { SavePaths.Add(path); }
    }

    /// <summary>线程安全地更新状态字段（下载线程写，查询端点读）。</summary>
    public void SetStatus(DownloadTaskStatus status)
    {
        lock (_savePathLock)
        {
            Status = status;
            IsSuccessful = status == DownloadTaskStatus.Succeeded;
        }
    }

    /// <summary>
    /// 线程安全地更新 Aid（解析成功/失败后由下载线程写入；查询端点读）。
    /// 与 SetStatus 共用 _savePathLock，避免 Snapshot 枚举期间读到半更新状态。
    /// </summary>
    public void SetAid(string aid)
    {
        lock (_savePathLock) { Aid = aid; }
    }

    /// <summary>
    /// 深拷贝快照：下载线程会持续修改本对象的 Progress/SavePaths 等字段，
    /// /get-tasks 在锁外序列化共享对象时，SavePaths.Add 撞上枚举会抛
    /// InvalidOperationException。快照的 SavePaths 是独立副本，序列化即安全。
    /// </summary>
    public DownloadTask Snapshot()
    {
        List<string> paths;
        lock (_savePathLock) { paths = new List<string>(SavePaths); }
        return new(Aid, Url, TaskCreateTime)
        {
            JobId = JobId,
            Title = Title,
            Pic = Pic,
            VideoPubTime = VideoPubTime,
            TaskFinishTime = TaskFinishTime,
            Progress = Progress,
            DownloadSpeed = DownloadSpeed,
            TotalDownloadedBytes = TotalDownloadedBytes,
            IsSuccessful = IsSuccessful,
            ErrorMessage = ErrorMessage,
            SavePaths = paths,
            Status = Status,
        };
    }
};
public record DownloadTaskCollection(List<DownloadTask> Running, List<DownloadTask> Finished);

/// <summary>/add-task 的 202 响应体：返回任务 JobId（GUID），客户端可据此查询或取消。</summary>
public record AddTaskAccepted(string TaskId);

record struct MyOptionBindingResult<T>(T? Result, Exception? Exception)
{
    public bool IsValid => Exception is null;

    public static async ValueTask<MyOptionBindingResult<T>> BindAsync(HttpContext httpContext)
    {
        try
        {
            // 大小写不敏感：客户端可能用 {url:...} 或 {Url:...}，两者都应绑定成功。
            // 用带该选项的 context 生成 TypeInfo（源生成，AOT 安全）。
            var context = new SourceGenerationContext(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            JsonTypeInfo? jsonTypeInfo = context.GetTypeInfo(typeof(T));
            if (jsonTypeInfo is not JsonTypeInfo<T> typedInfo)
            {
                return new(default, new InvalidOperationException($"Cannot find TypeInfo for type {typeof(T)}"));
            }
            // 请求体大小限制：/add-task 的合法负载很小（Url + 少量选项）。
            // 不设上限会让攻击者用超大 body 耗尽内存/带宽（长驻 serve 进程）。
            // Content-Length 超限直接 413；无 Content-Length（chunked）时读满上限即止。
            if (httpContext.Request.ContentLength is > MaxRequestBodyBytes)
            {
                return new(default, new RequestBodyTooLargeException());
            }
            using var ms = new MemoryStream();
            var buffer = new byte[4096];
            long total = 0;
            while (true)
            {
                int read = await httpContext.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), httpContext.RequestAborted);
                if (read == 0) break;
                total += read;
                if (total > MaxRequestBodyBytes)
                {
                    return new(default, new RequestBodyTooLargeException());
                }
                ms.Write(buffer, 0, read);
            }
            var json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            var item = JsonSerializer.Deserialize<T>(json, typedInfo);

            if (item is null) return new(default, new NoNullAllowedException());

            return new((T)item, null);
        }
        catch (Exception ex) when (ex is RequestBodyTooLargeException or JsonException or NotSupportedException or InvalidOperationException)
        {
            return new(default, ex);
        }
    }

    /// <summary>/add-task 请求体大小上限：合法负载远小于此值，超限即拒。</summary>
    private const long MaxRequestBodyBytes = 64 * 1024;
}

/// <summary>请求体超过 <see cref="MyOptionBindingResult{T}.MaxRequestBodyBytes"/> 的专用异常：
/// /add-task 处理器据此返回 413（与普通 JSON 语法错误的 400 区分）。
/// 定义在顶层：绑定器（MyOptionBindingResult）与处理器（BBDownApiServer）都要引用它。</summary>
internal sealed class RequestBodyTooLargeException : InvalidOperationException
{
    public RequestBodyTooLargeException() : base("请求体过大") { }
}

[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(ValidationProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
[JsonSerializable(typeof(DownloadTask))]
[JsonSerializable(typeof(List<DownloadTask>))]
[JsonSerializable(typeof(DownloadTaskCollection))]
[JsonSerializable(typeof(AddTaskAccepted))]
public partial class AppJsonSerializerContext : JsonSerializerContext
{

}

[JsonSerializable(typeof(MyOption))]
[JsonSerializable(typeof(ServeRequestOptions))]
internal partial class SourceGenerationContext : JsonSerializerContext
{

}
