using BBDown.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
namespace BBDown;

/// <summary>
/// serve 端点映射（H1 拆解：任务查询 / 入队 / 取消 / 清理四组）。处理器仍是内联 lambda
/// （与拆解前逐字相同）——它们读取任务注册表与闸门，抽成可单测的具名处理器需要外置状态，
/// 不在本次文件级拆分范围。
/// </summary>
public partial class BBDownApiServer
{
    /// <summary>任务查询端点族（/get-tasks 及子路径）。</summary>
    private void MapTaskQueryRoutes(WebApplication app)
    {
        var taskStatusApi = app.MapGroup("/get-tasks");
        // D8：查询端点并发上限。快照深拷贝是查询端点的真正成本（GetFinishedTasks 每请求
        // 深拷贝 running+finished 全部任务），无限并发会放大 CPU/GC。槽位不足返回 429。
        taskStatusApi.AddEndpointFilter(async (ctx, next) =>
        {
            if (!_queryLimiter.Wait(0))
            {
                // Retry-After 提示客户端何时可重试（限速语义与认证限速一致）
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
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
    }

    /// <summary>入队端点（/add-task）：绑定 → 净化 → 接受队列限流 → 入队并登记在途任务。</summary>
    private void MapAddTaskRoute(WebApplication app)
    {
        app.MapPost("/add-task", (RequestBodyBindingResult<ServeRequestOptions> bindingResult, HttpContext httpContext) =>
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
                // RF-83：与认证/查询限速一致，429 附 Retry-After 提示客户端何时可重试
                httpContext.Response.Headers.RetryAfter = "60";
                return Results.Problem("任务队列已满，请稍后再试",
                    statusCode: StatusCodes.Status429TooManyRequests, title: "Too Many Requests");
            }
            var task = EnqueueDownloadTask(req);
            // URL 解析可能在没有异步等待时直接失败并落盘；先登记任务，再异步放行执行，避免阻塞 HTTP 请求线程。
            var taskStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var bgTask = RunAcceptedTaskAsync(req, task, taskStart.Task);
            // 登记在途任务：服务关停时据此等待所有后台任务收尾
            lock (_inFlightLock) { _inFlightTasks.Add(bgTask); _inFlightJobIds.Add(task.JobId); }
            _ = bgTask.ContinueWith(t =>
            {
                lock (_inFlightLock) { _inFlightTasks.Remove(t); _inFlightJobIds.Remove(task.JobId); }
            }, TaskScheduler.Default);
            taskStart.SetResult();
            return Results.Json(new AddTaskAccepted(task.JobId), AppJsonSerializerContext.Default.AddTaskAccepted, statusCode: StatusCodes.Status202Accepted);
        });
    }

    /// <summary>取消端点（/cancel/{id}）：仅对 running/queued 生效。</summary>
    private void MapCancelRoute(WebApplication app)
    {
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
    }

    /// <summary>已完成任务清理端点族（/remove-finished）：全部 / 仅失败 / 按 id。</summary>
    private void MapFinishedRemovalRoutes(WebApplication app)
    {
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
}
