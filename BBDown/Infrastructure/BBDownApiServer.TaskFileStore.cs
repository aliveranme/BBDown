using System.Text;
using System.Text.Json;
using BBDown.Core;
using BBDown.Core.Util;
namespace BBDown;

/// <summary>
/// serve 任务持久化（H1 拆解：已完成任务的写入 / 溢出裁剪 / 启动加载）。
/// 任务列表本身由 <c>BBDownApiServer.Tasks.cs</c> 持有，这里只负责落盘与恢复。
/// </summary>
public partial class BBDownApiServer
{
    // 已完成任务持久化：serve 是长驻进程，任务记录只留在内存会在重启后丢失。
    // 默认写到进程当前目录；测试可通过构造函数注入临时路径，避免多实例互相污染
    private readonly string _taskFile;
    private readonly SemaphoreSlim _finishedTasksLoadLock = new(1, 1);
    private bool _finishedTasksLoaded;

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
                // tmp 名带 GUID（对齐 SubscriptionStore.AtomicWrite）：固定 .tmp 名会让同目录
                // 的多个 serve 实例并发写盘互相踩踏；代价是崩溃瞬间可能残留孤儿 tmp 文件（罕见且无害）。
                // Unix 上创建即 owner-only（对齐 BBDown.data）：任务记录含绝对路径/提交 URL，
                // 不应以 umask 默认的组/其他可读落盘；Windows 无此语义，保持原样。
                var tmpFile = _taskFile + $".tmp-{Guid.NewGuid():N}";
                var writeOpts = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows())
                    writeOpts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var fs = new FileStream(tmpFile, writeOpts))
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
        catch (Exception ex) when (ExceptionPolicies.IsTaskStoreFailure(ex))
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
    /// 文件读取或格式错误时记录警告并继续启动，不因历史记录恢复失败阻断服务。
    /// </summary>
    private async Task LoadFinishedTasksAsync(CancellationToken cancellationToken)
    {
        await _finishedTasksLoadLock.WaitAsync(cancellationToken);
        try
        {
            if (_finishedTasksLoaded) return;
            if (!File.Exists(_taskFile))
            {
                _finishedTasksLoaded = true;
                return;
            }

            var json = await File.ReadAllTextAsync(_taskFile, cancellationToken);
            var loaded = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListDownloadTask);
            if (loaded is null)
            {
                _finishedTasksLoaded = true;
                return;
            }

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
            _finishedTasksLoaded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ExceptionPolicies.IsTaskStoreFailure(ex))
        {
            // 升 Warn：加载失败意味着上次的全部任务记录无法恢复（损坏/权限/磁盘故障），
            // 此前仅 LogDebug（默认抑制）会让记录静默丢失。
            Logger.LogWarn($"加载历史任务记录失败（记录可能丢失）: {ex.Message}");
            _finishedTasksLoaded = true;
        }
        finally
        {
            _finishedTasksLoadLock.Release();
        }
    }
}
