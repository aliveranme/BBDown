using BBDown;
using BBDown.Core;

namespace BBDown.Tests;

/// <summary>
/// 订阅持久化可靠性测试：损坏数据（清单/历史）必须隔离而非静默当空/重置。
/// 通过 <see cref="SubscriptionStore.StoreRoot"/> 注入独立临时目录，不污染真实安装目录。
/// </summary>
public class SubscriptionStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _origRoot;

    public SubscriptionStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "bbdown-sub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _origRoot = SubscriptionStore.StoreRoot;
        SubscriptionStore.StoreRoot = _tempRoot;
    }

    public void Dispose()
    {
        SubscriptionStore.StoreRoot = _origRoot;
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
    }

    private string HistoryFile => Path.Combine(_tempRoot, "BBDownSubscriptions.history.json");
    private string SubFile => Path.Combine(_tempRoot, "BBDownSubscriptions.json");

    /// <summary>
    /// 回归：损坏历史文件必须被隔离为 .corrupt-时间戳 并抛专用异常，而不是静默当空历史
    /// 或重置。此前 LoadHistory 损坏时返回空集合、RecordDownloaded 损坏时静默重置，
    /// 会让已下载内容被当作新增重新下载一遍，且丢失全部订阅历史。
    /// </summary>
    [Fact]
    public async Task LoadHistory_CorruptFile_IsolatesAndThrowsCorruptException()
    {
        // 写入损坏 JSON
        File.WriteAllText(HistoryFile, "{ this is not valid json !!!");

        // LoadHistory 必须抛专用异常（中止整个 sub check），而非返回空集合
        var ex = await Assert.ThrowsAsync<SubscriptionDataCorruptException>(
            () => SubscriptionStore.LoadHistoryAsync("mid:1"));

        // 损坏文件已被隔离（.corrupt-* 存在），原文件被移走
        Assert.Contains(".corrupt-", ex.Message);
        Assert.False(File.Exists(HistoryFile));
        Assert.Single(Directory.GetFiles(_tempRoot, "BBDownSubscriptions.history.json.corrupt-*"));
    }

    /// <summary>
    /// 回归：目标字段存在但不是数组（如 {"mid:1":"broken"}）必须按损坏处理并抛专用异常，
    /// 不能静默当空历史——否则该订阅全部内容会被当作新增重新下载一遍。
    /// </summary>
    [Fact]
    public async Task LoadHistory_TargetFieldNotArray_IsolatesAndThrowsCorruptException()
    {
        // 合法 JSON 但结构错误：目标字段是字符串而非数组
        File.WriteAllText(HistoryFile, """{"mid:1":"broken"}""");

        var ex = await Assert.ThrowsAsync<SubscriptionDataCorruptException>(
            () => SubscriptionStore.LoadHistoryAsync("mid:1"));
        Assert.Contains(".corrupt-", ex.Message);
        Assert.False(File.Exists(HistoryFile));
    }

    /// <summary>
    /// 回归：历史数组包含非字符串元素（数字/对象）也必须按损坏处理并抛专用异常。
    /// </summary>
    [Fact]
    public async Task LoadHistory_ArrayWithNonStringElement_IsolatesAndThrowsCorruptException()
    {
        // 数组内含数字元素：结构不符
        File.WriteAllText(HistoryFile, """{"mid:1":[12345, "67890"]}""");

        var ex = await Assert.ThrowsAsync<SubscriptionDataCorruptException>(
            () => SubscriptionStore.LoadHistoryAsync("mid:1"));
        Assert.Contains(".corrupt-", ex.Message);
        Assert.False(File.Exists(HistoryFile));
    }

    /// <summary>目标字段不存在（该订阅从未下载过）是合法场景，返回空集合并隔离历史文件损坏除外。</summary>
    [Fact]
    public async Task LoadHistory_TargetNotPresent_ReturnsEmpty()
    {
        File.WriteAllText(HistoryFile, """{"mid:2":["170001"]}""");
        Assert.Empty(await SubscriptionStore.LoadHistoryAsync("mid:1"));
        Assert.True(File.Exists(HistoryFile)); // 未损坏，不隔离
    }

    /// <summary>
    /// 回归：主订阅清单损坏必须隔离并抛专用异常，不能返回空列表。
    /// 此前 Load 损坏时返回空列表 → sub list/check 显示"没有订阅"并成功，
    /// 下一次 sub add/remove 会用空列表覆盖原文件（与历史文件问题同类）。
    /// </summary>
    [Fact]
    public async Task Load_CorruptSubFile_IsolatesAndThrowsCorruptException()
    {
        // 先写入合法订阅清单，再改成损坏 JSON
        await SubscriptionStore.AddAsync("mid:1", "UP主1");
        Assert.Single(await SubscriptionStore.LoadAsync());
        File.WriteAllText(SubFile, "{ broken !!!");

        // Load 必须抛专用异常而非返回空列表
        var ex = await Assert.ThrowsAsync<SubscriptionDataCorruptException>(() => SubscriptionStore.LoadAsync());
        Assert.Contains(".corrupt-", ex.Message);
        Assert.False(File.Exists(SubFile));
        Assert.Single(Directory.GetFiles(_tempRoot, "BBDownSubscriptions.json.corrupt-*"));
    }

    /// <summary>
    /// 变异验证（H10 收敛新增）：历史文件内容是字面量 `null`。旧实现里两条路径不一致——
    /// LoadHistory 视为损坏（根不是对象），RecordDownloaded 的 \`Deserialize(...) ?? new()\`
    /// 却静默当空历史并随后重写整份文件。统一后按"损坏不可信"处理（宁可中止也不静默当空，
    /// 静默当空会让已下载内容被当作新增重下）。
    /// </summary>
    [Fact]
    public async Task LoadHistory_NullJsonFile_IsolatesAndThrowsCorruptException()
    {
        File.WriteAllText(HistoryFile, "null");

        var ex = await Assert.ThrowsAsync<SubscriptionDataCorruptException>(
            () => SubscriptionStore.LoadHistoryAsync("mid:1"));
        Assert.Contains(".corrupt-", ex.Message);
        Assert.False(File.Exists(HistoryFile));
        Assert.Single(Directory.GetFiles(_tempRoot, "BBDownSubscriptions.history.json.corrupt-*"));
    }

    /// <summary>
    /// 同上，写路径：字面量 `null` 历史文件此前会被静默当空并重写（丢证据、丢历史），
    /// 统一后与读路径同语义（隔离 + 专用异常）。
    /// </summary>
    [Fact]
    public async Task RecordDownloaded_NullJsonFile_IsolatesAndThrowsCorruptException()
    {
        File.WriteAllText(HistoryFile, "null");

        var ex = await Assert.ThrowsAsync<SubscriptionDataCorruptException>(
            () => SubscriptionStore.RecordDownloadedAsync("mid:1", "170001"));
        Assert.Contains(".corrupt-", ex.Message);
        Assert.False(File.Exists(HistoryFile));
    }

    /// <summary>
    /// 变异验证（H10 收敛新增）：**别的**订阅条目结构损坏时，读取一个健康订阅也判损坏。
    /// 旧实现只校验被请求的那个 target，别的条目坏掉时照样返回结果；而写路径会把整份
    /// 文件重写回盘（坏条目会被原样写回或触发中断），读路径"只看自己那条"会让两条路径
    /// 对同一份文件给出不同结论。
    /// </summary>
    [Fact]
    public async Task LoadHistory_OtherTargetCorrupt_IsolatesAndThrowsCorruptException()
    {
        File.WriteAllText(HistoryFile, "{\"mid:2\":\"broken\"}");

        var ex = await Assert.ThrowsAsync<SubscriptionDataCorruptException>(
            () => SubscriptionStore.LoadHistoryAsync("mid:1"));
        Assert.Contains(".corrupt-", ex.Message);
    }

    /// <summary>
    /// 回归：RecordDownloaded 遇到损坏历史也必须抛专用异常（而非静默重置），
    /// 否则已下载内容会在下次检查时被当作新增重新下载，且丢失全部历史。
    /// </summary>
    [Fact]
    public async Task RecordDownloaded_CorruptHistory_IsolatesAndThrowsCorruptException()
    {
        File.WriteAllText(HistoryFile, "{ broken !!!");

        var ex = await Assert.ThrowsAsync<SubscriptionDataCorruptException>(
            () => SubscriptionStore.RecordDownloadedAsync("mid:1", "170001"));
        Assert.Contains(".corrupt-", ex.Message);
        Assert.False(File.Exists(HistoryFile));
    }

    [Fact]
    public async Task Add_DuplicateTarget_IsIdempotent()
    {
        // F12：重复 Add 同一 target 不得重复登记——否则 sub list 出现重复项、
        // remove 一次删不干净。
        await SubscriptionStore.AddAsync("mid:1", "UP");
        await SubscriptionStore.AddAsync("mid:1", "UP");
        await SubscriptionStore.AddAsync("mid:1", "UP");
        Assert.Single(await SubscriptionStore.LoadAsync());
    }

    [Fact]
    public async Task Remove_NonexistentTarget_DoesNotThrow()
    {
        // F12：Remove 不存在的订阅不得抛异常/留空残余（幂等）
        await SubscriptionStore.RemoveAsync("mid:404");
        Assert.Empty(await SubscriptionStore.LoadAsync());
    }

    [Fact]
    public async Task Remove_ExistentTarget_RemovesOnlyThat()
    {
        // F12：Remove 只删除目标，不影响其它订阅
        await SubscriptionStore.AddAsync("mid:1", "UP1");
        await SubscriptionStore.AddAsync("mid:2", "UP2");
        await SubscriptionStore.RemoveAsync("mid:1");
        var subs = await SubscriptionStore.LoadAsync();
        Assert.Single(subs);
        Assert.Equal("mid:2", subs[0].Target);
    }

    [Fact]
    public async Task RecordDownloaded_SameAid_IsIdempotent()
    {
        // F12：同 aid 重复 RecordDownloaded 不得历史膨胀/重复——否则每次 sub check
        // 都会把已下载的内容当作新增重新下载一遍。
        await SubscriptionStore.RecordDownloadedAsync("mid:1", "170001");
        await SubscriptionStore.RecordDownloadedAsync("mid:1", "170001");
        await SubscriptionStore.RecordDownloadedAsync("mid:1", "170001");
        Assert.Single(await SubscriptionStore.LoadHistoryAsync("mid:1"));
    }

    [Fact]
    public async Task RecordDownloaded_MovesExistingAid_KeepsSingleEntry()
    {
        // F12: RecordDownloaded 对已存在 aid 采用 Remove+Add（最近优先语义）：
        // active list 里同 aid 只能出现一次，且重复记录后仍为单条。
        await SubscriptionStore.RecordDownloadedAsync("mid:1", "a1");
        await SubscriptionStore.RecordDownloadedAsync("mid:1", "b2");
        await SubscriptionStore.RecordDownloadedAsync("mid:1", "a1"); // 同 aid 重复
        var hist = await SubscriptionStore.LoadHistoryAsync("mid:1");
        Assert.Equal(2, hist.Count); // a1 + b2，不重复
        Assert.Contains("a1", hist);
        Assert.Contains("b2", hist);
    }
    [Fact]
    public async Task IsolatedHistory_BlocksLaterReadsAndWritesUntilRestored()
    {
        await File.WriteAllTextAsync(HistoryFile, "{ broken");
        await Assert.ThrowsAsync<SubscriptionDataCorruptException>(() => SubscriptionStore.LoadHistoryAsync("mid:1"));
        await Assert.ThrowsAsync<SubscriptionDataCorruptException>(() => SubscriptionStore.LoadHistoryAsync("mid:1"));
        await Assert.ThrowsAsync<SubscriptionDataCorruptException>(() => SubscriptionStore.RecordDownloadedAsync("mid:1", "1"));
        Assert.False(File.Exists(HistoryFile));

        // 恢复文件后，即使隔离副本仍留作取证，正常历史也可使用。
        await File.WriteAllTextAsync(HistoryFile, "{\"mid:1\":[\"1\"]}");
        Assert.Contains("1", await SubscriptionStore.LoadHistoryAsync("mid:1"));
    }

    [Fact]
    public async Task IsolatedSubscriptions_BlockLaterAddUntilRestored()
    {
        await File.WriteAllTextAsync(SubFile, "{ broken");
        await Assert.ThrowsAsync<SubscriptionDataCorruptException>(() => SubscriptionStore.LoadAsync());
        await Assert.ThrowsAsync<SubscriptionDataCorruptException>(() => SubscriptionStore.AddAsync("mid:2", "new"));
        Assert.False(File.Exists(SubFile));

        await File.WriteAllTextAsync(SubFile, "[]");
        await SubscriptionStore.AddAsync("mid:2", "new");
        Assert.Single(await SubscriptionStore.LoadAsync());
    }

    [Fact]
    public async Task DirectoryAssignments_SurviveRemovalAndReserveOldSlots()
    {
        var first = new Subscription("mid:1", "same", 1);
        var second = new Subscription("mid:2", "same", 2);
        var initial = await SubscriptionStore.ResolveDirectoriesAsync([first, second]);
        Assert.Equal("same", initial[first.Target]);
        Assert.Equal("same-2", initial[second.Target]);

        var third = new Subscription("mid:3", "same", 3);
        var afterRemoval = await SubscriptionStore.ResolveDirectoriesAsync([second, third]);
        Assert.Equal("same-2", afterRemoval[second.Target]);
        Assert.Equal("same-3", afterRemoval[third.Target]);
        Assert.Equal("same", afterRemoval[first.Target]);
    }

    [Fact]
    public async Task CorruptDirectoryMapping_BlocksLaterAllocation()
    {
        var mapFile = Path.Combine(_tempRoot, "BBDownSubscriptions.directories.json");
        await File.WriteAllTextAsync(mapFile, "{ broken");
        var subscriptions = new[] { new Subscription("mid:1", "same", 1) };
        await Assert.ThrowsAsync<SubscriptionDataCorruptException>(() => SubscriptionStore.ResolveDirectoriesAsync(subscriptions));
        await Assert.ThrowsAsync<SubscriptionDataCorruptException>(() => SubscriptionStore.ResolveDirectoriesAsync(subscriptions));
        Assert.False(File.Exists(mapFile));
    }

}
