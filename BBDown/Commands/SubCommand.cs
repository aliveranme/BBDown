using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using BBDown;
using BBDown.Core;
using BBDown.Core.Fetcher;

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
public class SubCheckSettings : SubSettings
{
    [CommandOption("-c|--cookie")]
    [Description("Cookie 字符串")]
    public string Cookie { get; set; } = "";

    [CommandOption("--access-token")]
    [Description("access token")]
    public string AccessToken { get; set; } = "";

    [CommandOption("-e|--encoding-priority")]
    [Description("视频编码优先级, 如 hevc,avc,av1")]
    public string? EncodingPriority { get; set; }

    [CommandOption("-q|--dfn-priority")]
    [Description("视频清晰度优先级, 如 8K 4K 1080P 高清 720P 高清")]
    public string? DfnPriority { get; set; }

    [CommandOption("-a|--use-app-api")]
    [Description("使用APP端解析模式")]
    public bool UseAppApi { get; set; }

    [CommandOption("-t|--use-tv-api")]
    [Description("使用TV端解析模式")]
    public bool UseTvApi { get; set; }

    [CommandOption("--use-intl-api")]
    [Description("使用国际版解析模式")]
    public bool UseIntlApi { get; set; }

    [CommandOption("-w|--work-dir")]
    [Description("设置工作目录(所有相对路径的根目录)")]
    public string WorkDir { get; set; } = "";
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
public class SubAddCommand : Command<SubAddSettings>
{
    protected override int Execute(CommandContext context, SubAddSettings settings, CancellationToken cancellationToken)
    {
        SubscriptionStore.Add(settings.Target, settings.Name);
        return 0;
    }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
public class SubListCommand : Command<SubListSettings>
{
    protected override int Execute(CommandContext context, SubListSettings settings, CancellationToken cancellationToken)
    {
        var subs = SubscriptionStore.Load();
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
public class SubRemoveCommand : Command<SubRemoveSettings>
{
    protected override int Execute(CommandContext context, SubRemoveSettings settings, CancellationToken cancellationToken)
    {
        SubscriptionStore.Remove(settings.Target);
        return 0;
    }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
public class SubCheckCommand : AsyncCommand<SubCheckSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, SubCheckSettings settings, CancellationToken cancellationToken)
    {
        // 批量检查期间 Ctrl+C 会取消当前下载；直接退出进程则等效于整体取消。
        var subs = SubscriptionStore.Load();
        if (subs.Count == 0)
        {
            Logger.LogWarn("当前没有订阅，请先用 BBDown sub add <目标> 添加");
            return 0;
        }

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
        var session = await Program.InitializeRequestSessionAsync(sessionOption, cancellationToken);
        if (session is not null) Core.Config.Apply(session);

        int failedSubs = 0;
        foreach (var sub in subs)
        {
            Logger.Log($"检查订阅: {sub.Name} ({sub.Target})");
            try
            {
                string resolved = await UrlResolver.ResolveAsync(sub.Target, cancellationToken);
                if (string.IsNullOrEmpty(resolved)) continue;

                var fetcher = FetcherFactory.CreateFetcher(resolved, settings.UseIntlApi);
                var vInfo = await fetcher.FetchAsync(resolved, cancellationToken);

                var allAids = vInfo.PagesInfo.Select(p => p.aid).Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList();
                var history = SubscriptionStore.LoadHistory(sub.Target);
                var newAids = allAids.Where(a => !history.Contains(a)).ToList();

                if (newAids.Count == 0)
                {
                    Logger.Log("  没有新增内容");
                    continue;
                }

                Logger.Log($"  发现 {newAids.Count} 个新内容: av{string.Join(", av", newAids)}");
                bool anyAidFailed = false;
                foreach (var aid in newAids)
                {
                    try
                    {
                        var opt = BuildOption($"av{aid}", settings);
                        await Program.DoWorkAsync(opt, cancellationToken);
                        SubscriptionStore.RecordDownloaded(sub.Target, aid);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException
                                                or InvalidOperationException or IOException or ArgumentException
                                                or TimeoutException or TaskCanceledException)
                    {
                        anyAidFailed = true;
                        Logger.LogWarn($"  av{aid} 下载失败（继续下一个）: {ex.Message}");
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
            catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException
                                        or InvalidOperationException or IOException or ArgumentException
                                        or TimeoutException or TaskCanceledException)
            {
                // 单个订阅失败不中止其余订阅，但必须计入失败数：
                // 全部失败仍返回 0 会让脚本/CI 无法区分"全部成功"与"全部失败"
                failedSubs++;
                Logger.LogWarn($"  订阅检查失败: {ex.Message}");
            }
        }
        if (failedSubs > 0)
        {
            Logger.LogWarn($"订阅检查完成，{failedSubs} 个订阅失败");
            return 1;
        }
        return 0;
    }

    private static MyOption BuildOption(string url, SubCheckSettings s) => new()
    {
        Url = url,
        Cookie = s.Cookie,
        AccessToken = s.AccessToken,
        EncodingPriority = s.EncodingPriority,
        DfnPriority = s.DfnPriority,
        UseAppApi = s.UseAppApi,
        UseTvApi = s.UseTvApi,
        UseIntlApi = s.UseIntlApi,
        WorkDir = s.WorkDir,
    };
}
