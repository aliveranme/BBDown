using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using BBDown;
using BBDown.Core;

namespace BBDown.Commands;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
public class ServeSettings : CommandSettings
{
    [CommandOption("-l|--listen")]
    [Description("服务器监听url")]
    public string ListenUrl { get; set; } = "http://127.0.0.1:23333";

    [CommandOption("--max-concurrent")]
    [Description("最大并发下载数(默认3)")]
    public int MaxConcurrent { get; set; } = 3;

    [CommandOption("--serve-token")]
    [Description("可选访问令牌，设置后所有 API 请求需携带 X-Serve-Token 请求头，否则返回 401。优先使用环境变量 BBDOWN_SERVE_TOKEN（避免令牌出现在进程命令行/ps 中）")]
    public string? ServeToken { get; set; }

    [CommandOption("--trusted-proxy")]
    [Description("信任直连反代追加的 X-Forwarded-For（认证失败限速按客户真实 IP 计键）。仅在 serve 前方确有可信反代时启用，否则客户端可伪造 XFF 绕过限速")]
    public bool TrustedProxy { get; set; }

    [CommandOption("--notify-webhook")]
    [Description("任务完成时向该固定地址发送 HTTP POST 回调(服务端配置, 不接受客户端指定)")]
    public string? NotifyWebhook { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
public class ServeCommand : AsyncCommand<ServeSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ServeSettings settings, CancellationToken cancellationToken)
    {
        _ = BBDownUtil.CheckUpdateAsync(cancellationToken);
        try
        {
            if (settings.MaxConcurrent < 1)
            {
                Logger.LogError($"--max-concurrent 至少为 1，当前为 {settings.MaxConcurrent}");
                return 1;
            }
            // --serve-token 经 CLI 参数传入时，多用户系统的 ps//proc/*/cmdline 会暴露令牌。
            // 环境变量 BBDOWN_SERVE_TOKEN 优先：令牌不出现在进程命令行，且 CI/脚本部署更安全。
            // 显式 CLI 参数仍保留（向后兼容），但环境变量已设置时以环境变量为准。
            var serveToken = ResolveServeToken(settings.ServeToken, Environment.GetEnvironmentVariable("BBDOWN_SERVE_TOKEN"));
            // 默认安全边界的前置校验：非回环监听（0.0.0.0 / :: / 具体网卡 IP）会把任务
            // 端点暴露到局域网/公网，必须显式配置 --serve-token 才能启动。
            // 这里给出可读错误；BBDownApiServer.RunAsync 内还有兜底防御（InvalidOperationException）。
            if (!IsLoopbackListenUrl(settings.ListenUrl) && string.IsNullOrEmpty(serveToken))
            {
                Logger.LogError(
                    $"监听地址 {settings.ListenUrl} 不是回环地址（127.0.0.1/localhost），" +
                    $"非回环监听必须配置 --serve-token 才能启动，否则任意客户端都能提交任务并访问本机文件。");
                return 1;
            }
            await Program.StartServerAsync(settings.ListenUrl, settings.MaxConcurrent, serveToken, settings.NotifyWebhook, cancellationToken, settings.TrustedProxy);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception e)
        {
            Logger.LogError($"服务器启动失败: {e.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 解析 serve 访问令牌。环境变量 BBDOWN_SERVE_TOKEN 优先于 CLI --serve-token 选项。
    /// 当两者均设置且值不同时记录警告日志。
    /// </summary>
    internal static string? ResolveServeToken(string? cliToken, string? envToken)
    {
        var serveToken = !string.IsNullOrEmpty(envToken) ? envToken : cliToken;
        if (!string.IsNullOrEmpty(cliToken) && !string.IsNullOrEmpty(envToken) && envToken != cliToken)
        {
            // 两者都存在且不同：以环境变量优先是安全决策，但差异值得提示避免运维困惑
            Logger.LogWarn("--serve-token 与 BBDOWN_SERVE_TOKEN 均已设置，使用环境变量值（更高优先级）");
        }
        return serveToken;
    }

    /// <summary>监听 URL 是否属于本机回环（127.0.0.1 / localhost / [::1] / ::1）。</summary>
    private static bool IsLoopbackListenUrl(string listenUrl)
    {
        // 空值在 Program.StartServer 会回落到默认回环地址，视为回环
        if (string.IsNullOrWhiteSpace(listenUrl)) return true;
        if (Uri.TryCreate(listenUrl, UriKind.Absolute, out var uri))
        {
            // DnsSafeHost 去掉 IPv6 字面量的方括号，IPAddress.TryParse 才能解析 [::1]
            var host = uri.DnsSafeHost;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (System.Net.IPAddress.TryParse(host, out var ip)) return System.Net.IPAddress.IsLoopback(ip);
        }
        return false;
    }
}
