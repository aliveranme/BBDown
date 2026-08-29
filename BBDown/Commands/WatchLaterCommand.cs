using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using BBDown;
using BBDown.Core;
using BBDown.Core.Util;

namespace BBDown.Commands;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
public class WatchLaterSettings : CommandSettings
{
    [CommandOption("--limit")]
    [Description("最多下载前 N 个稍后再看视频(默认 0=全部)")]
    public int Limit { get; set; }

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
public class WatchLaterCommand : AsyncCommand<WatchLaterSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WatchLaterSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            // 稍后再看接口需要登录：先加载本地登录凭据（或用户传入的 cookie）。
            // 用统一会话初始化入口：不仅加载凭据，还做登录检查并提取 wbi——
            // 稍后再看列表与后续下载都用 WEB API，空 wbi 的 w_rid 会被 B 站拒绝。
            // 返回的完整会话在父流程自身异步流内应用（子方法内 AsyncLocal 写入不回流）。
            var bootstrap = new MyOption { Cookie = settings.Cookie, AccessToken = settings.AccessToken, UseTvApi = settings.UseTvApi, UseAppApi = settings.UseAppApi, UseIntlApi = settings.UseIntlApi };
            var session = await Program.InitializeRequestSessionAsync(bootstrap, cancellationToken);
            if (session is not null) Core.Config.Apply(session);

            Logger.Log("正在获取稍后再看列表...");
            var list = await FetchWatchLaterAsync(cancellationToken);
            if (list.Count == 0)
            {
                Logger.Log("稍后再看列表为空");
                return 0;
            }

            var targets = settings.Limit > 0 ? list.Take(settings.Limit).ToList() : list;
            Logger.Log($"共 {list.Count} 个稍后再看，开始下载 {targets.Count} 个...");
            int succeeded = 0;
            int failed = 0;
            foreach (var (aid, title) in targets)
            {
                Logger.Log($"--- 下载 av{aid} {title} ---");
                try
                {
                    var opt = BuildOption($"av{aid}", settings);
                    await Program.DoWorkAsync(opt, cancellationToken);
                    succeeded++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                            or IOException or ArgumentException or TimeoutException or TaskCanceledException)
                {
                    // 单个视频失败不应中止整批稍后再看，但必须计入失败数，
                    // 让调用方拿到非零退出码（此前静默继续并返回 0，
                    // 脚本/CI 无法区分"全部成功"与"部分失败"）
                    failed++;
                    Logger.LogWarn($"av{aid} 下载失败（继续下一个）: {ex.Message}");
                }
            }
            Logger.Log($"稍后再看下载完成：成功 {succeeded} 个，失败 {failed} 个");
            return failed == 0 ? 0 : 1;
        }
        catch (OperationCanceledException ex)
        {
            // 区分主动取消（Ctrl+C，token 已取消）与 HttpClient 超时（token 未取消）：
            // 超时是真实失败，必须返回非零退出码，不能以"已取消"+0 隐藏失败（脚本/CI 误判成功）
            if (cancellationToken.IsCancellationRequested)
            {
                Logger.LogWarn("已取消");
                return 0;
            }
            Logger.LogError($"稍后再看下载超时或被中断: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Logger.LogError($"稍后再看下载失败: {ex.Message}");
            return 1;
        }
    }

    private static async Task<List<(string Aid, string Title)>> FetchWatchLaterAsync(CancellationToken token)
    {
        const string api = "https://api.bilibili.com/x/v2/history/toview";
        string json = await HTTPUtil.GetWebSourceAsync(api, token: token);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int code = root.GetInt32Safe("code");
        if (code != 0)
            throw new InvalidOperationException($"获取稍后再看失败(code={code}): {root.GetValueAsStringSafe("message")}。该接口需要登录，请先运行 BBDown login 或传入 --cookie。");

        var list = new List<(string, string)>();
        var dataElem = root.TryGetPropertySafe("data");
        if (dataElem is not null)
        {
            foreach (var item in dataElem.Value.EnumerateArraySafe("list"))
            {
                var aid = item.GetValueAsStringSafe("aid");
                if (aid == "") continue;
                list.Add((aid, item.GetValueAsStringSafe("title")));
            }
        }
        return list;
    }

    private static MyOption BuildOption(string url, WatchLaterSettings s) => new()
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
