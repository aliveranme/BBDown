using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BBDown.Core;
using BBDown.Core.Util;

namespace BBDown;

public record Subscription(string Target, string Name, long AddedAt);

/// <summary>
/// 订阅持久化数据损坏（订阅清单或历史文件 JSON 解析失败）时抛出。
/// 与普通 I/O/网络失败不同：数据损坏意味着"哪些已下载过"的信息不可信，
/// 继续按空历史/空清单执行会触发大规模重复下载或覆盖，必须中止整个流程。
/// SubCheck 等调用方应捕获本异常并终止整批，而非按单订阅失败继续。
/// </summary>
public sealed class SubscriptionDataCorruptException : InvalidOperationException
{
    public SubscriptionDataCorruptException(string message, Exception inner) : base(message, inner) { }
}

[JsonSerializable(typeof(List<Subscription>))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class SubscriptionJsonContext : JsonSerializerContext
{
}

/// <summary>
/// 订阅清单与"已下载"历史的持久化。
/// 订阅文件与历史文件都放在程序目录（APP_DIR）下，与凭据文件同级。
/// </summary>
public static class SubscriptionStore
{
    /// <summary>存储根目录（程序目录）。internal 供测试注入临时目录，避免污染真实安装目录。</summary>
    internal static string StoreRoot = Program.APP_DIR;

    /// <summary>每个订阅保存在历史文件中的最大 avid 条数：超出后移除最旧的。
    /// 足够大（正常订阅不会触顶），只是约束无界增长。</summary>
    private const int MaxHistoryPerTarget = 5000;

    private static string SubFile => Path.Combine(StoreRoot, "BBDownSubscriptions.json");
    private static string HistoryFile => Path.Combine(StoreRoot, "BBDownSubscriptions.history.json");
    private static string DirectoryMapFile => Path.Combine(StoreRoot, "BBDownSubscriptions.directories.json");

    /// <summary>
    /// 把损坏的持久化文件隔离为 .corrupt-毫秒-GUID 并返回隔离路径；返回 null 表示隔离失败
    /// （文件被占用等，保留原位）。内部供测试验证"损坏数据不静默当空/重置"。
    /// 隔离名含毫秒 + GUID：同一时刻再次出现损坏不会覆盖上一份恢复副本（此前只精确到秒
    /// 且 overwrite:true，同秒第二次损坏会覆盖第一份）。
    /// </summary>
    internal static string? IsolateCorruptFile(string path)
    {
        string corrupt = $"{path}.corrupt-{DateTimeOffset.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        try
        {
            if (File.Exists(path)) File.Move(path, corrupt);
            return corrupt;
        }
        catch (IOException)
        {
            return null;
        }
    }

    // 缺失文件不一定是首次运行：隔离副本存在时要求用户先恢复原文件或显式移走副本，
    // 防止下次进程启动后把损坏数据当作空数据并覆盖历史。
    private static void ThrowIfIsolatedFileExists(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir)) return;
        string pattern = Path.GetFileName(path) + ".corrupt-*";
        string? isolated = Directory.EnumerateFiles(dir, pattern).FirstOrDefault();
        if (isolated is not null)
            throw new SubscriptionDataCorruptException(
                $"数据文件 {path} 缺失，发现隔离副本 {isolated}。请恢复文件，或确认放弃数据后移走隔离副本", new InvalidDataException("存在尚未恢复的隔离数据"));
    }

    // 走 JsonTypeInfo 的序列化：AOT 裁剪安全，且可自定义缩进与不转义非 ASCII
    private static string ToJson<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            JsonSerializer.Serialize(writer, value, typeInfo);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // 单进程内串行化存储访问：Add/Remove/RecordDownloaded 的读-改-写不可交错，
    // 同时避免读取者持有旧文件句柄时撞上 Windows 原子替换。
    private static readonly SemaphoreSlim _ioLock = new(1, 1);

    /// <summary>原子替换写入（temp + rename）：避免进程被杀/磁盘满留下截断 JSON，
    /// 否则下次 Load 会把损坏文件静默当作"无订阅"。
    /// 临时文件名带唯一后缀：固定 .tmp 名会让并发写者互相踩踏（FileShare.None 抛 IOException）。</summary>
    private static async Task AtomicWriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, content, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tmp, path, true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception ex) when (ExceptionPolicies.IsBestEffortFailure(ex)) { }
        }
    }

    private static Task WriteSubsAsync(List<Subscription> subs, CancellationToken cancellationToken)
    {
        // 写入失败必须向上传播：调用方据此返回非零退出码/失败状态。
        // 此前吞掉异常后调用方仍打印"已添加订阅"，用户以为成功但文件没写入。
        return AtomicWriteAsync(SubFile, ToJson(subs, SubscriptionJsonContext.Default.ListSubscription), cancellationToken);
    }

    public static async Task<List<Subscription>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private static async Task<List<Subscription>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(SubFile))
            {
                ThrowIfIsolatedFileExists(SubFile);
                return [];
            }
            return JsonSerializer.Deserialize(await File.ReadAllTextAsync(SubFile, cancellationToken),
                SubscriptionJsonContext.Default.ListSubscription) ?? [];
        }
        catch (Exception ex) when (ExceptionPolicies.IsJsonOrIoFailure(ex))
        {
            // 订阅清单损坏不能静默当空：否则 sub list/check 显示"没有订阅"并成功，
            // 下一次 sub add/remove 会用空列表覆盖原文件。隔离为 .corrupt-时间戳并抛
            // 专用异常，调用方据此中止流程。
            string? corrupt = IsolateCorruptFile(SubFile);
            Logger.LogError($"订阅清单损坏（{ex.Message}），已隔离为 {corrupt ?? SubFile}，中止操作以避免覆盖订阅");
            throw new SubscriptionDataCorruptException($"订阅清单损坏，已隔离为 {corrupt ?? SubFile}，请检查后恢复", ex);
        }
    }

    public static async Task AddAsync(string target, string? name, CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            var subs = await LoadCoreAsync(cancellationToken);
            if (subs.Any(s => s.Target == target))
            {
                Logger.LogWarn($"已存在订阅: {target}");
                return;
            }
            subs.Add(new Subscription(target, string.IsNullOrWhiteSpace(name) ? target : name!,
                DateTimeOffset.Now.ToUnixTimeSeconds()));
            await WriteSubsAsync(subs, cancellationToken);
            Logger.Log($"已添加订阅: {target}");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public static async Task RemoveAsync(string target, CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            var subs = await LoadCoreAsync(cancellationToken);
            var removed = subs.RemoveAll(s => s.Target == target);
            await WriteSubsAsync(subs, cancellationToken);
            Logger.Log(removed > 0 ? $"已移除订阅: {target}" : $"未找到订阅: {target}");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// 某个订阅已成功下载过的 avid 集合。历史文件损坏时隔离为 .corrupt-时间戳 并抛异常
    /// （调用方应中止该订阅，不能当空历史继续——否则已下载内容会被当作新增重新下载一遍）。
    /// 读取与"损坏判定"完全委托给 <see cref="ReadHistoryLockedAsync"/> 单入口，
    /// 与写路径（<see cref="RecordDownloadedAsync"/>）共用同一套异常语义。
    /// </summary>
    public static async Task<HashSet<string>> LoadHistoryAsync(string target, CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            var hist = await ReadHistoryLockedAsync(cancellationToken);
            // 该订阅尚无历史是合法场景（从未下载过）：返回空集合，不视为损坏
            return hist.TryGetValue(target, out var list) ? new HashSet<string>(list) : new HashSet<string>();
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// 历史文件的**唯一读取入口**（调用方必须已持有 <see cref="_ioLock"/>）：返回
    /// <c>{"target": [avid,...]}</c> 的可变副本；文件不存在返回空字典。
    /// **损坏语义唯一**——此前 <see cref="LoadHistoryAsync"/> 与 <see cref="RecordDownloadedAsync"/>
    /// 各写一套（前者按 `IsJsonOrIoFailure` 隔离并抛专用异常，后者只 catch `JsonException`，
    /// 于是同一份文件在"读取"与"写入"两条路径上对 IO 失败的反应不同）。现在统一为：
    /// JSON 结构不符（根非对象 / 值非字符串数组 / 元素非字符串，含字面量 `null`）或读取
    /// IO 失败 → 隔离为 .corrupt-时间戳 + 抛 <see cref="SubscriptionDataCorruptException"/>，
    /// 调用方必须中止流程。当空历史继续会让已下载内容被当作新增重下；静默重置会丢失全部历史。
    /// **严格校验整个文件**（不只调用方关心的那个 target）：任何一条损坏都意味着文件不可信，
    /// 而写路径会把整份文件重写回盘，不能"只看自己那条"。
    /// </summary>
    private static async Task<Dictionary<string, List<string>>> ReadHistoryLockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(HistoryFile))
        {
            ThrowIfIsolatedFileExists(HistoryFile);
            return [];
        }
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(HistoryFile, cancellationToken));
            // 根节点必须是对象：历史文件是 {"target": [avid,...]} 结构
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException($"历史文件根节点不是对象（实际 {doc.RootElement.ValueKind}）");
            var result = new Dictionary<string, List<string>>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // 目标字段存在但不是数组 → 结构损坏，不能当空历史（否则全部内容会被当新增重下）
                if (prop.Value.ValueKind != JsonValueKind.Array)
                    throw new JsonException($"订阅 {prop.Name} 的历史字段不是数组（实际 {prop.Value.ValueKind}）");
                var list = new List<string>();
                foreach (var e in prop.Value.EnumerateArray())
                {
                    // 数组元素不是字符串（数字/对象/null）→ 结构损坏
                    if (e.ValueKind != JsonValueKind.String)
                        throw new JsonException($"订阅 {prop.Name} 的历史数组包含非字符串元素（{e.ValueKind}）");
                    list.Add(e.GetString()!);
                }
                result[prop.Name] = list;
            }
            return result;
        }
        catch (Exception ex) when (ExceptionPolicies.IsJsonOrIoFailure(ex))
        {
            // 统一损坏语义：隔离现场供排查 + 专用异常中止。调用方必须捕获
            // SubscriptionDataCorruptException 终止整个 sub check——若按普通订阅失败继续，
            // 后续订阅会因历史文件已不存在而把全部内容当新增重新下载。
            string? corrupt = IsolateCorruptFile(HistoryFile);
            Logger.LogError($"订阅历史文件损坏（{ex.Message}），已隔离为 {corrupt ?? HistoryFile}，中止当前操作以避免重复下载或丢失历史");
            throw new SubscriptionDataCorruptException($"订阅历史文件损坏，已隔离为 {corrupt ?? HistoryFile}，请检查后恢复", ex);
        }
    }

    /// <summary>
    /// 持久化目录分配，包含已移除订阅的槽位：不能把旧目录再分配给其他订阅。
    /// 首次运行按订阅原顺序分配，与旧版目录命名兼容。
    /// </summary>
    internal static async Task<Dictionary<string, string>> ResolveDirectoriesAsync(
        IReadOnlyList<Subscription> subs, CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, string> mapping;
            if (!File.Exists(DirectoryMapFile))
            {
                ThrowIfIsolatedFileExists(DirectoryMapFile);
                mapping = [];
            }
            else
            {
                try
                {
                    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(DirectoryMapFile, cancellationToken));
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("目录映射根节点不是对象");
                    mapping = new Dictionary<string, string>();
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.String) throw new JsonException("目录映射值不是字符串");
                        string? name = prop.Value.GetString();
                        if (string.IsNullOrEmpty(name) || PathUtil.GetValidFileName(name, filterSlash: true, maxBaseNameLength: 255) != name || !names.Add(name) || !mapping.TryAdd(prop.Name, name))
                            throw new JsonException("目录映射包含无效或重复的目录名");
                    }
                }
                catch (Exception ex) when (ExceptionPolicies.IsJsonOrIoFailure(ex))
                {
                    string? isolated = IsolateCorruptFile(DirectoryMapFile);
                    throw new SubscriptionDataCorruptException($"订阅目录映射损坏，已隔离为 {isolated ?? DirectoryMapFile}，请检查后恢复", ex);
                }
            }

            var used = new HashSet<string>(mapping.Values, StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            foreach (var sub in subs)
            {
                if (mapping.ContainsKey(sub.Target)) continue;
                mapping[sub.Target] = Commands.SubCheckCommand.ResolveSubDirName(sub, used);
                changed = true;
            }
            if (changed)
                await AtomicWriteAsync(DirectoryMapFile, ToJson(mapping, SubscriptionJsonContext.Default.DictionaryStringString), cancellationToken);
            return mapping;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public static async Task RecordDownloadedAsync(string target, string aid, CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            var hist = await ReadHistoryLockedAsync(cancellationToken);
            if (!hist.TryGetValue(target, out var list)) { list = []; hist[target] = list; }
            // Remove + Add：重复下载同一 avid 时把它移到末尾（保持"最近"语义），键唯一。
            list.Remove(aid);
            list.Add(aid);
            // 历史只增不减：多年部署无界增长，每次 sub check 全量解析变慢。
            // 保留最近 N 条（超过的按时间顺序移除最旧的）。N 足够大，正常订阅不会触顶。
            if (list.Count > MaxHistoryPerTarget)
                list.RemoveRange(0, list.Count - MaxHistoryPerTarget);
            // 写入失败向上传播：调用方据此让 sub check 返回非零退出码，
            // 否则下次运行会因历史未记录而重复下载已下载内容。
            await AtomicWriteAsync(HistoryFile, ToJson(hist, SubscriptionJsonContext.Default.DictionaryStringListString), cancellationToken);
        }
        finally
        {
            _ioLock.Release();
        }
    }
}
