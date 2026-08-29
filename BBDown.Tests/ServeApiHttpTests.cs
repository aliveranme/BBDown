using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace BBDown.Tests;

/// <summary>
/// 串行化 <see cref="ServeApiHttpTests"/> 的集合定义（F11）：此前只有
/// [Collection("ServeApiCollection")] 引用而无对应 CollectionDefinition，
/// 集合语义无法被框架验证。BBDownApiServer 实例是长驻资源且共享静态配置，
/// 集合串行化保证测试间不争用端口/任务文件。
/// </summary>
[CollectionDefinition("ServeApiCollection")]
public class ServeApiCollection
{
}

/// <summary>
/// serve HTTP 端点级测试：真正启动 BBDownApiServer 并用 HttpClient 走完整请求。
/// 与 ServeApiSecurityTests（只测静态纯函数）互补，覆盖 401 中间件、CORS、任务
/// 提交/查询/删除语义。
/// </summary>
/// <remarks>
/// BBDownApiServer 会把已完成任务持久化到任务文件（已改为实例字段 + 构造函数注入，
/// 每个实例用独立临时文件，不再有静态 _taskFile 污染）；运行中的 http server 是
/// 长驻资源——多个测试类并行各起一个实例会争用端口/资源。这里用集合串行化本类测试，
/// 避免与其它并行测试类竞争（F11：补 [CollectionDefinition] 使集合声明闭合；
/// 此前只有 [Collection] 引用而无定义，编译器/工具无法验证集合存在）。
/// </remarks>
[Collection("ServeApiCollection")]
public class ServeApiHttpTests
{
    // G10：固定端口 58681 改动态分配——固定端口在 CI 并行/端口冲突时 Kestrel 启动直接失败；
    // 静态字段只在进程内分配一次，集合内各实例共享同一 BaseUrl（与固定端口语义一致）。
    private static readonly string BaseUrl = $"http://127.0.0.1:{TestPort.Allocate()}";

    /// <summary>
    /// 每次启动一个干净的 server 实例（不携带 token），并保证它已停止监听后才释放。
    /// </summary>
    private sealed class RunningServer : IDisposable
    {
        public BBDownApiServer Server { get; }
        public HttpClient Client { get; }
        public string TaskFile => _taskFile;

        private readonly CancellationTokenSource _cts = new();
        private readonly string _taskFile;
        private readonly bool _ownsTaskFile;
        private readonly Task _runTask;

        public RunningServer(bool withToken = false, string? taskFilePath = null, int maxConcurrent = 3)
        {
            // 每个实例用独立的任务文件，避免 LoadFinishedTasks 把上个实例/上轮测试
            // 留下的记录加载进内存，导致任务跨测试累积、断言失准。
            // 外部传入 taskFilePath 时（如重启恢复测试），Dispose 不清理该文件。
            _ownsTaskFile = taskFilePath == null;
            _taskFile = taskFilePath ?? Path.Combine(Path.GetTempPath(), $"bbdown-tasks-{Guid.NewGuid():N}.json");
            Server = new BBDownApiServer(maxConcurrent: maxConcurrent, serveToken: withToken ? "test-token" : null, taskFilePath: _taskFile);
            Server.SetupServer();
            // RunAsync 迁移（RF-2）后本身就是异步任务，无需再 Task.Run 包一层
            _runTask = Server.RunAsync(BaseUrl, _cts.Token);
            // 等待服务器就绪（Kestrel 开始监听）后再发请求：WebApplication 启动在
            // CI 首次运行/慢环境下明显慢于本地，若不等待，所有请求都会撞上
            // Connection refused（{BaseUrl}）竞态。
            // 若服务器启动即失败（_runTask 已 faulted），立刻暴露根因而非干等超时。
            if (!Server.Ready.Task.Wait(TimeSpan.FromSeconds(10)))
            {
                if (_runTask.IsFaulted)
                    throw new InvalidOperationException($"测试服务器启动失败: {_runTask.Exception?.GetBaseException()}", _runTask.Exception);
                throw new TimeoutException($"测试服务器 {BaseUrl} 未在 10 秒内就绪");
            }
            Client = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _runTask.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* 服务器关停的取消异常可忽略 */ }
            Client.Dispose();
            _cts.Dispose();
            if (_ownsTaskFile)
            {
                try { if (File.Exists(_taskFile)) File.Delete(_taskFile); }
                catch (IOException) { /* 清理失败不影响测试结论 */ }
            }
        }
    }

    [Fact]
    public async Task GetTasks_ReturnsOkAndCollections()
    {
        using var server = new RunningServer();
        using var resp = await server.Client.GetAsync("/get-tasks/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        // 服务器通过 ConfigureHttpJsonOptions 序列化，属性名策略以实际返回为准
        Assert.True(doc.RootElement.TryGetProperty("running", out var running) || doc.RootElement.TryGetProperty("Running", out running),
            $"响应中没有任务集合属性，实际响应: {json}");
        Assert.True(doc.RootElement.TryGetProperty("finished", out var finished) || doc.RootElement.TryGetProperty("Finished", out finished),
            $"响应中没有完成集合属性，实际响应: {json}");
        Assert.Equal(JsonValueKind.Array, running.ValueKind);
        Assert.Equal(JsonValueKind.Array, finished.ValueKind);
    }

    [Fact]
    public async Task AddTask_InvalidInput_Returns400()
    {
        using var server = new RunningServer();
        // 空 body 无法反序列化为 ServeRequestOptions → 绑定层返回 400"输入有误"
        using var resp = await server.Client.PostAsync("/add-task", null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task AddTask_OversizedBody_Returns413()
    {
        using var server = new RunningServer();
        // 请求体超过 64KB 上限 → 返回 413（F8 契约对齐：超大负载与 JSON 语法错误语义不同，
        // 用 413 区分；此前注释承诺 413、实现与测试却是 400，三处不一致）
        var oversized = new { Url = "zz-not-a-real-url", Padding = new string('x', 128 * 1024) };
        using var content = JsonContent.Create(oversized);
        using var resp = await server.Client.PostAsync("/add-task", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
    }

    [Fact]
    public async Task AddTask_UnresolvableUrl_Returns202AndProducesFailedTask()
    {
        using var server = new RunningServer();
        // "zz-not-a-real-url" 在 UrlResolver.ResolveAsync 本地抛 ArgumentException，
        // 不会触发网络请求；ProcessDownloadTaskAsync 捕获后把已入队任务标记失败
        using var content = JsonContent.Create(new { Url = "zz-not-a-real-url" });
        using var resp = await server.Client.PostAsync("/add-task", content);
        // 202 Accepted + 任务 ID
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var accepted = JsonSerializer.Deserialize(body, AppJsonSerializerContext.Default.AddTaskAccepted);
        Assert.NotNull(accepted);
        // 现在 TaskId 是无业务含义的 JobId（GUID），不再是 URL 本身
        Assert.False(string.IsNullOrEmpty(accepted.TaskId));
        Assert.True(Guid.TryParse(accepted.TaskId, out _), $"TaskId 应为 GUID，实际: {accepted.TaskId}");
        Assert.NotEqual("zz-not-a-real-url", accepted.TaskId);

        // 轮询等待任务落盘完成（下载流程在后台 Task 中推进）
        var finished = await WaitForFinishedTasksAsync(server.Client);
        Assert.Single(finished);
        Assert.False(finished[0].IsSuccessful);
        Assert.Equal(DownloadTaskStatus.Failed, finished[0].Status);
        Assert.False(string.IsNullOrEmpty(finished[0].ErrorMessage));
        // JobId 已持久化且与响应体一致：查询/取消可按它命中
        Assert.Equal(accepted.TaskId, finished[0].JobId);
    }

    [Fact]
    public async Task AddTask_UnresolvableUrl_JobIdQueryableAndCancellable()
    {
        using var server = new RunningServer();
        // 提交一个必然解析失败的 URL：客户端应拿到 GUID JobId，
        // 且该 JobId 能命中 /get-tasks/{id}（查到失败任务）与 /cancel/{id} 的匹配逻辑
        using var content = JsonContent.Create(new { Url = "zz-not-a-real-url" });
        using var resp = await server.Client.PostAsync("/add-task", content);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var accepted = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(),
            AppJsonSerializerContext.Default.AddTaskAccepted);
        Assert.NotNull(accepted);

        // JobId 能查到失败任务（等待落盘后查）
        await WaitForFinishedTasksAsync(server.Client);
        using var detail = await server.Client.GetAsync($"/get-tasks/{accepted.TaskId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var task = JsonSerializer.Deserialize(await detail.Content.ReadAsStringAsync(),
            AppJsonSerializerContext.Default.DownloadTask);
        Assert.NotNull(task);
        Assert.Equal(DownloadTaskStatus.Failed, task.Status);
        Assert.Equal(accepted.TaskId, task.JobId);

        // 已完成的失败任务不可取消（返回 404）——但匹配逻辑按 JobId 命中，与 Aid 无关
        using var cancel = await server.Client.PostAsync($"/cancel/{accepted.TaskId}", null);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
    }

    [Fact]
    public async Task DeleteFinished_RemovesFinishedTasks()
    {
        using var server = new RunningServer();
        // 先制造一条失败任务
        using (var content = JsonContent.Create(new { Url = "zz-not-a-real-url" }))
        using (var resp = await server.Client.PostAsync("/add-task", content))
        {
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        }
        var before = await WaitForFinishedTasksAsync(server.Client);
        Assert.Single(before);

        using var del = await server.Client.DeleteAsync("/remove-finished/");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var after = await GetFinishedTasksAsync(server.Client);
        Assert.Empty(after);
    }

    [Fact]
    public async Task TokenAuth_RequiredOnApiPaths()
    {
        using var server = new RunningServer(withToken: true);

        // 不带 token 访问任务端点 → 401
        using (var unauthGet = await server.Client.GetAsync("/get-tasks/"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthGet.StatusCode);
        }
        using (var unauthPost = await server.Client.PostAsync("/add-task",
                   JsonContent.Create(new { Url = "av123" })))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthPost.StatusCode);
        }
        using (var unauthDelete = await server.Client.DeleteAsync("/remove-finished/"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthDelete.StatusCode);
        }

        // 带 token → 200
        server.Client.DefaultRequestHeaders.Remove("X-Serve-Token");
        server.Client.DefaultRequestHeaders.Add("X-Serve-Token", "test-token");
        using (var ok = await server.Client.GetAsync("/get-tasks/"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
    }

    [Fact]
    public async Task TokenAuth_Cancel_RequiresTokenAndProceedsToNotFound()
    {
        using var server = new RunningServer(withToken: true);

        // 不带 token 访问 /cancel/{id} → 401（取消端点也在 token 认证范围内）
        using (var unauthCancel = await server.Client.PostAsync("/cancel/some-id", null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthCancel.StatusCode);
        }

        // 带正确 token 后进入正常处理逻辑：不存在的任务 → 404（而非 401）
        server.Client.DefaultRequestHeaders.Remove("X-Serve-Token");
        server.Client.DefaultRequestHeaders.Add("X-Serve-Token", "test-token");
        using (var notFound = await server.Client.PostAsync("/cancel/does-not-exist", null))
        {
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        }
    }

    [Fact]
    public async Task TokenAuth_WrongToken_Returns401()
    {
        using var server = new RunningServer(withToken: true);

        // 错误 token → 401（常量时间比较路径：FixedTimeEquals 先哈希再比较，行为一致）
        server.Client.DefaultRequestHeaders.Add("X-Serve-Token", "wrong-token");
        using (var wrong = await server.Client.GetAsync("/get-tasks/"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // 前缀相同但整体不同的 token → 也 401（覆盖"正确 token 前缀 + 追加字符"）
        server.Client.DefaultRequestHeaders.Remove("X-Serve-Token");
        server.Client.DefaultRequestHeaders.Add("X-Serve-Token", "test-token-x");
        using (var prefix = await server.Client.GetAsync("/get-tasks/"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, prefix.StatusCode);
        }
    }

    [Fact]
    public async Task Cors_NoAllowAnyOrigin_NoCorsHeaders()
    {
        // 服务端已移除任意来源 CORS：浏览器跨域 POST 会被同源策略拦截，
        // 防止恶意网页控制本机服务。响应不应携带 Access-Control-Allow-Origin。
        using var server = new RunningServer();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/get-tasks/");
        req.Headers.TryAddWithoutValidation("Origin", "https://example.com");
        using var resp = await server.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(resp.Headers.Contains("Access-Control-Allow-Origin"),
            "不再启用任意来源 CORS，响应不应包含 Access-Control-Allow-Origin 头");
    }

    [Fact]
    public async Task Cors_NonLoopbackOrigin_WriteApi_403()
    {
        // CSRF 防护：浏览器跨源请求携带 Origin，非回环来源（攻击者网页/DNS rebinding）
        // 的写端点请求必须 403。删掉这条防线 501 个测试仍全绿，属于零覆盖契约。
        using var server = new RunningServer();

        // 非回环 Origin → 写端点全部 403（/add-task、/cancel、/remove-finished）
        using (var addReq = new HttpRequestMessage(HttpMethod.Post, "/add-task"))
        {
            addReq.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
            addReq.Content = JsonContent.Create(new { Url = "zz-not-a-real-url" });
            using var resp = await server.Client.SendAsync(addReq);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        using (var cancelReq = new HttpRequestMessage(HttpMethod.Post, "/cancel/whatever"))
        {
            cancelReq.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
            using var resp = await server.Client.SendAsync(cancelReq);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        using (var delReq = new HttpRequestMessage(HttpMethod.Delete, "/remove-finished/"))
        {
            delReq.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
            using var resp = await server.Client.SendAsync(delReq);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // 回环 Origin（本地页面/管理脚本）不受影响
        using (var okReq = new HttpRequestMessage(HttpMethod.Post, "/add-task"))
        {
            okReq.Headers.TryAddWithoutValidation("Origin", "http://127.0.0.1:23333");
            okReq.Content = JsonContent.Create(new { Url = "zz-not-a-real-url" });
            using var resp = await server.Client.SendAsync(okReq);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        }
    }

    [Fact]
    public async Task AddTask_TextPlainContentType_415()
    {
        // text/plain 是 CORS 简单请求的合法 Content-Type（不发预检），正是攻击者网页
        // 驱动本机 serve 提交任务的载体：/add-task 必须拒绝非 JSON Content-Type。
        using var server = new RunningServer();
        using var content = new StringContent("{\"Url\":\"zz-not-a-real-url\"}", System.Text.Encoding.UTF8, "text/plain");
        using var resp = await server.Client.PostAsync("/add-task", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode);

        // application/json 正常受理（415 只拦非 JSON 载体）
        using (var ok = await server.Client.PostAsync("/add-task", JsonContent.Create(new { Url = "zz-not-a-real-url" })))
        {
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }
    }

    [Fact]
    public async Task TokenAuth_RepeatedFailures_RateLimited429()
    {
        // 认证失败限速：1 分钟窗口内失败达到阈值后必须 429，令 X-Serve-Token
        // 暴力枚举失效。连续 6 次错误 token：前 5 次 401，第 6 次 429。
        using var server = new RunningServer(withToken: true);
        server.Client.DefaultRequestHeaders.Add("X-Serve-Token", "wrong-token");
        for (int i = 1; i <= 5; i++)
        {
            using var resp = await server.Client.GetAsync("/get-tasks/");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        using (var locked = await server.Client.GetAsync("/get-tasks/"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        }
    }

    [Fact]
    public void NonLoopbackListen_WithoutToken_Throws()
    {
        // 默认安全边界：非回环监听（0.0.0.0/具体网卡 IP）会把端点暴露到局域网/公网，
        // 未配置 --serve-token 时必须拒绝启动——删掉这条防线测试仍全绿。
        // RunAsync 是 async 方法，校验异常进入返回的 Task；同步前置校验独立在
        // ValidateListenUrl（RunAsync 启动前也调用它兜底），这里直接断言校验语义。
        var server = new BBDownApiServer();
        server.SetupServer();
        Assert.Throws<InvalidOperationException>(() => server.ValidateListenUrl("http://0.0.0.0:12345"));
        Assert.Throws<InvalidOperationException>(() => server.ValidateListenUrl("http://192.168.1.10:12345"));
        // 回环监听不带 token 是受信任本地边界，保持兼容（不抛）。
        // 实际启动路径由上方各 RunningServer 用例覆盖（真实 Kestrel 回环监听）。
        server.ValidateListenUrl($"http://127.0.0.1:{TestPort.Allocate()}");
    }

    [Fact]
    public async Task Cancel_RealQueuedTask_ProducesCancelledAndPersisted()
    {
        // /cancel 快乐路径：真实任务（排队等待并发闸门）被取消后必须标记 Cancelled、
        // 移入 finished 并持久化——此前该路径零覆盖。
        using var server = new RunningServer(maxConcurrent: 1);
        // 占用唯一并发执行槽：让后续任务停在"排队等待闸门"状态，
        // 无需真实慢任务/网络依赖即可验证排队取消路径。
        Assert.True(server.Server.TryAcquireConcurrencySlot(), "应能占用并发闸门");

        using var content = JsonContent.Create(new { Url = "zz-not-a-real-url" });
        using var resp = await server.Client.PostAsync("/add-task", content);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var accepted = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(),
            AppJsonSerializerContext.Default.AddTaskAccepted);
        Assert.NotNull(accepted);

        // 任务已入队（Queued，正在等待闸门），取消它
        using var cancel = await server.Client.PostAsync($"/cancel/{accepted.TaskId}", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        // 取消后应落盘为 Cancelled（轮询等待后台收尾）
        var finished = await WaitForFinishedTasksAsync(server.Client);
        var task = Assert.Single(finished);
        Assert.Equal(DownloadTaskStatus.Cancelled, task.Status);
        Assert.Equal(accepted.TaskId, task.JobId);

        // 持久化文件里也是 Cancelled（重启可恢复）。/get-tasks 可见任务早于
        // PersistFinishedTasks 写盘完成（二者之间存在短暂窗口），轮询等待落盘。
        DownloadTask? persisted = null;
        for (int i = 0; i < 40 && persisted is null; i++)
        {
            if (File.Exists(server.TaskFile))
            {
                var json = await File.ReadAllTextAsync(server.TaskFile);
                var loaded = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListDownloadTask);
                persisted = loaded?.FirstOrDefault(t => t.JobId == accepted.TaskId);
            }
            if (persisted is null) await Task.Delay(100);
        }
        // G10：轮询耗尽断言带上下文——此前 Assert.NotNull 失败时无任何信息，
        // 无法判断是“没写盘”还是“写盘内容不对”。
        Assert.True(persisted is not null,
            $"任务 {accepted.TaskId} 未在 4s 内持久化；任务文件 {(File.Exists(server.TaskFile) ? "存在" : "不存在")} " +
            $"({server.TaskFile})");
        Assert.Equal(DownloadTaskStatus.Cancelled, persisted.Status);
    }

    [Fact]
    public async Task Delete_RemovesSpecificFinishedTask()
    {
        using var server = new RunningServer();
        // 制造两条失败任务（不同 URL；去重已移除，同 URL 也可并存，但这里用不同 URL 更直观）
        using (var c1 = JsonContent.Create(new { Url = "zz-not-a-real-url" }))
        using (await server.Client.PostAsync("/add-task", c1))
        {
        }
        using (var c2 = JsonContent.Create(new { Url = "zz-another-bad-url" }))
        using (await server.Client.PostAsync("/add-task", c2))
        {
        }
        var list = await WaitForFinishedCountAsync(server.Client, 2);
        Assert.Equal(2, list.Count);
        var target = list[0];
        Assert.NotEqual(list[0].Aid, list[1].Aid);

        using (var del = await server.Client.DeleteAsync($"/remove-finished/{target.Aid}"))
        {
            Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        }

        var after = await GetFinishedTasksAsync(server.Client);
        Assert.DoesNotContain(after, t => t.Aid == target.Aid);
        Assert.Contains(after, t => t.Aid == list[1].Aid);
    }

    [Fact]
    public async Task Delete_NonExistentId_StillOk()
    {
        using var server = new RunningServer();
        using var del = await server.Client.DeleteAsync("/remove-finished/does-not-exist");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
    }

    [Fact]
    public async Task Cancel_UnknownId_Returns404()
    {
        using var server = new RunningServer();
        using var resp = await server.Client.PostAsync("/cancel/does-not-exist", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task AddTask_ReturnsJobId_AndQueryableByGetTasks()
    {
        using var server = new RunningServer();
        using var content = JsonContent.Create(new { Url = "zz-not-a-real-url" });
        using var resp = await server.Client.PostAsync("/add-task", content);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var respBody = await resp.Content.ReadAsStringAsync();
        // 探测实际响应体，确认序列化属性名
        var accepted = JsonSerializer.Deserialize(respBody,
            AppJsonSerializerContext.Default.AddTaskAccepted);
        Assert.NotNull(accepted);
        Assert.False(string.IsNullOrEmpty(accepted.TaskId), $"TaskId 反序列化为空。响应体: {respBody}");
        // TaskId 现在是 JobId（GUID），不再与 Aid/Url 绑定
        Assert.True(Guid.TryParse(accepted.TaskId, out _), $"TaskId 应为 GUID，实际: {accepted.TaskId}");

        // JobId 应能通过 /get-tasks/{id} 查到（查 finished 或 running）
        await WaitForFinishedTasksAsync(server.Client);
        using var detail = await server.Client.GetAsync($"/get-tasks/{accepted.TaskId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailBody = await detail.Content.ReadAsStringAsync();
        var task = JsonSerializer.Deserialize(detailBody,
            AppJsonSerializerContext.Default.DownloadTask);
        Assert.NotNull(task);
        Assert.True(task.Status == DownloadTaskStatus.Failed, $"任务状态应为 Failed，实际: {task.Status}。响应体: {detailBody}");
        Assert.Equal(accepted.TaskId, task.JobId);
    }

    [Fact]
    public void JobId_DefaultsToEmpty_ForLegacyRecords()
    {
        // 旧持久化记录没有 JobId 字段：新构造的 DownloadTask 默认空串，
        // 查询/取消端点会回退到 Aid/Url 匹配（见 MatchesTaskId）
        var task = new DownloadTask("12345", "av12345", 1700000000);
        Assert.Equal("", task.JobId);
    }

    [Fact]
    public async Task PersistFinishedTasks_AtomicWrite_LeavesValidJson()
    {
        using var server = new RunningServer();
        // 触发一条失败任务并等待其落盘
        using (var content = JsonContent.Create(new { Url = "zz-not-a-real-url" }))
        using (await server.Client.PostAsync("/add-task", content))
        {
        }
        await WaitForFinishedTasksAsync(server.Client);

        // 持久化文件应存在且是合法 JSON
        Assert.True(File.Exists(server.TaskFile), $"任务文件 {server.TaskFile} 应存在");
        var json = await File.ReadAllTextAsync(server.TaskFile);
        var loaded = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListDownloadTask);
        Assert.NotNull(loaded);
        Assert.Single(loaded);

        // 不应残留临时文件（原子写完成即清理）
        Assert.False(File.Exists(server.TaskFile + ".tmp"), "原子写完成后不应残留 .tmp 文件");
    }

    [Fact]
    public async Task AddTask_QueueFull_Returns429()
    {
        using var server = new RunningServer(maxConcurrent: 1);
        // accept cap = maxConcurrent * (1 + 8) = 9。手动占满全部槽位（模拟队列已满），
        // 下一个 /add-task 必须返回 429 而不是继续堆积后台任务。
        int cap = 9;
        for (int i = 0; i < cap; i++)
        {
            Assert.True(server.Server.TryAcquireAcceptSlot(), $"第 {i} 次占用接受槽位应成功");
        }
        Assert.Equal(0, server.Server.AvailableAcceptSlots);

        using var content = JsonContent.Create(new { Url = "zz-not-a-real-url" });
        using var resp = await server.Client.PostAsync("/add-task", content);
        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
    }

    [Fact]
    public async Task GetTasks_QuerySlotsExhausted_Returns429()
    {
        // D8：/get-tasks 族查询端点并发上限——快照深拷贝是查询成本，带 token 客户端
        // 无限并发查询会放大 CPU/GC。正常查询 200；占满 8 个查询槽（模拟并发中）后，
        // 下一个请求必须 429 而非继续深拷贝。
        using var server = new RunningServer();
        // 正常时可用（未限流）
        using (var normal = await server.Client.GetAsync("/get-tasks"))
            Assert.Equal(HttpStatusCode.OK, normal.StatusCode);

        // 占满全部查询槽
        for (int i = 0; i < 8; i++)
        {
            Assert.True(server.Server.TryAcquireQuerySlot(), $"第 {i} 次占用查询槽应成功");
        }
        Assert.Equal(0, server.Server.AvailableQuerySlots);

        // 槽满 → 429（列表 + 子路径都受限）
        using var blocked = await server.Client.GetAsync("/get-tasks");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await server.Client.GetAsync("/get-tasks/running")).StatusCode);
    }

    [Fact]
    public async Task LoadFinishedTasks_RecoversTasksOnRestart()
    {
        var taskFile = Path.Combine(Path.GetTempPath(), $"bbdown-tasks-{Guid.NewGuid():N}.json");
        try
        {
            // 第一个 server 写入一条失败任务
            using (var server = new RunningServer(taskFilePath: taskFile))
            {
                using (var content = JsonContent.Create(new { Url = "zz-not-a-real-url" }))
                using (await server.Client.PostAsync("/add-task", content))
                {
                }
                await WaitForFinishedTasksAsync(server.Client);
            }

            // 第二个 server 复用同一任务文件，应恢复该记录
            using var server2 = new RunningServer(taskFilePath: taskFile);
            var recovered = await GetFinishedTasksAsync(server2.Client);
            Assert.Single(recovered);
        }
        finally
        {
            try { if (File.Exists(taskFile)) File.Delete(taskFile); }
            catch (IOException) { }
        }
    }

    private static async Task<List<DownloadTask>> WaitForFinishedCountAsync(HttpClient client, int count)
    {
        for (int i = 0; i < 40; i++)
        {
            var list = await GetFinishedTasksAsync(client);
            if (list.Count >= count) return list;
            await Task.Delay(100);
        }
        // G10：4s 轮询耗尽后抛带上下文异常，替代静默返回不满 count 的列表——
        // 此前调用方断言失败（如 Assert.Single 报“序列为空”）无任何任务状态可查，
        // 无法区分“任务没完成”与“接口异常”。
        var final = await GetFinishedTasksAsync(client);
        throw new TimeoutException(
            $"等待完成任务超时（4s 轮询耗尽）：期望 ≥{count} 条，实际 {final.Count} 条；" +
            $"最近状态: {string.Join(", ", final.Take(10).Select(t => $"{t.JobId}:{t.Status}"))}");
    }

    private static Task<List<DownloadTask>> WaitForFinishedTasksAsync(HttpClient client)
        => WaitForFinishedCountAsync(client, 1);

    private static async Task<List<DownloadTask>> GetFinishedTasksAsync(HttpClient client)
    {
        using var resp = await client.GetAsync("/get-tasks/finished");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListDownloadTask) ?? [];
    }
}
