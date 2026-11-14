using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
namespace BBDown;

// serve API 的任务模型与请求绑定类型（H1 拆解：原先堆在 BBDownApiServer.cs 尾部）。
// 源生成上下文与这些类型同文件：迁移类型时不会漏改 [JsonSerializable]（AOT 纪律）。
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
    // 写者一律经 AddSavePath 走这把锁；_taskStateLock 不能是 primary constructor 属性。
    // H8：原名 _savePathLock 只提 SavePaths，实际同时保护 SavePaths / Status / IsSuccessful / Aid
    //（RF-86 要求 Snapshot 与 SetStatus 共用同一把锁），改名后名实一致。
    private readonly object _taskStateLock = new();

    /// <summary>受控写入口：与 Snapshot 的深拷贝在同一把锁下，避免枚举期间被并发修改。
    /// 去重（Info 级观察）：锁内 Skipped 分支与成功路径会对同一 savePath 各 Add 一次，
    /// 导致 API 快照中同一产物出现两条——产物列表语义是集合而非序列。</summary>
    public void AddSavePath(string path)
    {
        lock (_taskStateLock)
        {
            if (!SavePaths.Contains(path)) SavePaths.Add(path);
        }
    }

    /// <summary>线程安全地更新状态字段（下载线程写，查询端点读）。</summary>
    public void SetStatus(DownloadTaskStatus status)
    {
        lock (_taskStateLock)
        {
            Status = status;
            IsSuccessful = status == DownloadTaskStatus.Succeeded;
        }
    }

    /// <summary>
    /// 线程安全地更新 Aid（解析成功/失败后由下载线程写入；查询端点读）。
    /// 与 SetStatus 共用 _taskStateLock，避免 Snapshot 枚举期间读到半更新状态。
    /// </summary>
    public void SetAid(string aid)
    {
        lock (_taskStateLock) { Aid = aid; }
    }

    /// <summary>
    /// 深拷贝快照：下载线程会持续修改本对象的 Progress/SavePaths 等字段，
    /// /get-tasks 在锁外序列化共享对象时，SavePaths.Add 撞上枚举会抛
    /// InvalidOperationException。快照的 SavePaths 是独立副本，序列化即安全。
    /// </summary>
    public DownloadTask Snapshot()
    {
        // RF-86：Status/IsSuccessful 由 SetStatus 在 _taskStateLock 内成对写入——读取也须在同一把
        // 锁内，否则查询端点与任务完成赛跑时可返回 status=Succeeded 而 isSuccessful=false 的不一致快照
        // （与 SetAid 注释声称的"共用锁避免半更新状态"一致）。SavePaths 同锁复制副本。
        List<string> paths;
        DownloadTaskStatus status;
        bool isSuccessful;
        string aid;
        lock (_taskStateLock)
        {
            paths = new List<string>(SavePaths);
            status = Status;
            isSuccessful = IsSuccessful;
            aid = Aid;
        }
        return new(aid, Url, TaskCreateTime)
        {
            JobId = JobId,
            Title = Title,
            Pic = Pic,
            VideoPubTime = VideoPubTime,
            TaskFinishTime = TaskFinishTime,
            Progress = Progress,
            DownloadSpeed = DownloadSpeed,
            TotalDownloadedBytes = TotalDownloadedBytes,
            IsSuccessful = isSuccessful,
            ErrorMessage = ErrorMessage,
            SavePaths = paths,
            Status = status,
        };
    }
};
public record DownloadTaskCollection(List<DownloadTask> Running, List<DownloadTask> Finished);

/// <summary>/add-task 的 202 响应体：返回任务 JobId（GUID），客户端可据此查询或取消。</summary>
public record AddTaskAccepted(string TaskId);

record struct RequestBodyBindingResult<T>(T? Result, Exception? Exception)
{
    public bool IsValid => Exception is null;

    public static async ValueTask<RequestBodyBindingResult<T>> BindAsync(HttpContext httpContext)
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

/// <summary>请求体超过 <see cref="RequestBodyBindingResult{T}.MaxRequestBodyBytes"/> 的专用异常：
/// /add-task 处理器据此返回 413（与普通 JSON 语法错误的 400 区分）。
/// 定义在顶层：绑定器（RequestBodyBindingResult）与处理器（BBDownApiServer）都要引用它。</summary>
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
