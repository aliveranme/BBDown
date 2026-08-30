using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using static BBDown.Core.Entity.Entity;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BBDown.Core;
using BBDown.Core.Util;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
using BBDown.Core.Entity;
using BBDown.Core.DRM;
using System.Diagnostics;
using Spectre.Console.Cli;
using BBDown.Commands;

namespace BBDown;

/// <summary>下载完成通知的载荷（--notify-webhook）。</summary>
public record NotifyPayload(string Title, int PageCount, string Message, long CompletedAt);

partial class Program
{
    private static readonly string BACKUP_HOST = "upos-sz-mirrorcoso1.bilivideo.com";
    public static string SinglePageDefaultSavePath { get; set; } = "<videoTitle>";
    public static string MultiPageDefaultSavePath { get; set; } = "<videoTitle>/[P<pageNumberWithZero>]<pageTitle>";

    /// <summary>当前进程是否运行在 serve 模式。serve 下 <see cref="Options.ChangeWorkingDir"/>
    /// 不写进程 CWD（并发任务各自的 --work-dir 经 AsyncLocal 配置快照隔离），
    /// 相对路径由 PathUtil.ResolveWorkPath 基于 Config.Current.WorkDir 解析。</summary>
    internal static bool IsServeMode;

    // 用 AppContext.BaseDirectory 而非 Environment.ProcessPath：
    // 以 `dotnet BBDown.dll` / `dotnet run` 启动时，进程可执行文件是 dotnet 宿主本身，
    // ProcessPath 会把 APP_DIR 指到 .NET 安装目录，导致 BBDown.data 等凭据
    // 被写入/读取自错误位置——表现为刚登录完却仍提示"尚未登录"。
    // BaseDirectory 在 apphost、dotnet 宿主与 NativeAOT 单文件下都指向程序集所在目录。
    public static readonly string APP_DIR = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    private static string FormatTimeStamp(long ts, string format)
    {
        try
        {
            // InvariantCulture（RF-19）：自定义格式串的 `:` 是"时间分隔符"占位符而非字面字符，
            // CurrentCulture 在 fi-FI 等区域设置下会替换为本地分隔符（与 RF-5 creation_time
            // 同构收口）。产物文件名合法性由 PathHelper 的 GetValidFileName 负责。
            return ts == 0 ? "null" : DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or FormatException)
        {
            Logger.LogError($"格式化日期出错: {ex.Message}");
            return ts.ToString();
        }
    }

    [JsonSerializable(typeof(MyOption))]
    [JsonSerializable(typeof(ServeRequestOptions))]
    [JsonSerializable(typeof(NotifyPayload))]
    partial class MyOptionJsonContext : JsonSerializerContext { }

    private static void Console_CancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // 首次 Ctrl+C：恢复终端状态后只取消根 CTS 并允许进程走正常退出路径。
        // 此前这里直接 Environment.Exit(0)：下载/ffmpeg/DRM 的 finally 不保证执行、
        // 直播 .part 不会改名保存、serve 任务状态来不及持久化，且即使下载未完成
        // 也以成功码 0 退出。现在把取消交给命令执行流：
        //  - 各子命令（live/article/watchlater/serve/login）自行 catch OperationCanceledException 返回 0；
        //  - 默认下载命令不接 OCE，由 SetExceptionHandler 识别取消并返回 130（见 Main），
        //    所有清理逻辑得以执行。

        // 必须设置 e.Cancel = true：否则 Ctrl+C 处理结束后 OS 会立即终止进程，
        // 优雅退出路径同样被绕过。
        try
        {
            Console.ResetColor();
            Console.CursorVisible = true;
            if (!OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("stty", "echo");
        }
        catch { /* 尽力恢复终端状态，失败无需上报 */ }

        if (_firstCancelHandled)
        {
            // 二次 Ctrl+C：命令清理可能卡住（如外部进程等待超时），强制终止。
            // 用标准 130（128+SIGINT）退出码，比 0 更能反映"被中断"。
            Logger.LogWarn("强制退出...");
            Environment.Exit(130);
        }
        _firstCancelHandled = true;
        Logger.LogWarn("正在取消并等待清理完成（再次 Ctrl+C 强制退出）...");
        e.Cancel = true;
        try { _rootCts.Cancel(); } catch { /* 取消令牌源可能已释放，忽略 */ }
    }

    /// <summary>根取消令牌源：Ctrl+C 首次触发时取消它，让命令执行流走优雅退出路径。</summary>
    private static readonly CancellationTokenSource _rootCts = new();

    /// <summary>是否已经处理过首次 Ctrl+C（二次则强制退出）。</summary>
    private static volatile bool _firstCancelHandled;

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DefaultCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LoginCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LoginTVCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ServeCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LiveCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(MyOption))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LoginSettings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ServeSettings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LiveSettings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ArticleSettings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ArticleCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(WatchLaterSettings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(WatchLaterCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SubSettings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SubAddSettings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SubListSettings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SubRemoveSettings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SubCheckSettings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SubAddCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SubListCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SubRemoveCommand))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SubCheckCommand))]
    public static async Task<int> Main(params string[] args)
    {
        Console.CancelKeyPress += Console_CancelKeyPress;

        Console.BackgroundColor = ConsoleColor.DarkBlue;
        Console.ForegroundColor = ConsoleColor.White;
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!;
        Console.Write($"BBDown version {ver.Major}.{ver.Minor}.{ver.Build}, Bilibili Downloader.\r\n");
        Console.ResetColor();
        Console.Write("遇到问题请首先到以下地址查阅有无相关信息：\r\nhttps://github.com/aliveranme/BBDown/issues\r\n");
        Console.WriteLine();

        var normalizedArgs = NormalizeCliArgs(args);
        var mergedArgs = BBDownConfigParser.MergeWithConfig(normalizedArgs).ToArray();

        if (mergedArgs.Contains("--debug"))
        {
            Config.Apply(Config.Current with { DebugLog = true });
        }

        var services = new ServiceCollection();
        var registrar = new TypeRegistrar(services);
        var app = new CommandApp<DefaultCommand>(registrar);
        app.Configure(config =>
        {
            config.SetApplicationName("BBDown");
            config.SetApplicationVersion($"{ver.Major}.{ver.Minor}.{ver.Build}");
            config.SetExceptionHandler((ex, resolver) =>
            {
                // 用户主动取消（首次 Ctrl+C 触发 _rootCts.Cancel()）：Spectre 只有在
                // 未注册 ExceptionHandler 时才会把 OperationCanceledException 转成
                // CancellationExitCode=130；注册 handler 后所有异常（含取消）都会先
                // 落到这里，取消会被误报成"请尝试升级到最新版本后重试!"并以 1 退出。
                // 这里显式识别取消：清理路径的 finally 已执行完才轮到 handler 被调用，
                // 直接返回与 Spectre 一致的 130（128+SIGINT），不打印误导性错误。
                if (_rootCts.IsCancellationRequested ||
                    ex is OperationCanceledException oce && oce.CancellationToken.IsCancellationRequested)
                {
                    try { Console.ResetColor(); Console.CursorVisible = true; } catch { }
                    Logger.LogWarn("已取消");
                    return 130;
                }
                Console.BackgroundColor = ConsoleColor.Red;
                Console.ForegroundColor = ConsoleColor.White;
                // 完整异常细节（堆栈/InnerException）写日志文件：即使非 --debug 模式也要落盘，
                // 否则用户只看到被裁剪的 Message/AOT 资源键，无法定位根因。
                Logger.LogStack(ex);
                // 非取消的 TaskCanceledException 是 HttpClient 超时（token 未取消）：
                // 默认 Message 在裁剪/AOT 发布下只显示资源键"TaskCanceledException_ctor_DefaultMessage"，
                // 对用户无意义且"升级后重试"解决不了超时，给出可读提示。
                var msg = Config.Current.DebugLog ? ex.ToString()
                    : ex is TaskCanceledException ? "请求超时或网络连接中断，请检查网络后重试"
                    : ex.Message;
                Console.Error.WriteLine(msg);
                Console.Error.WriteLine("请尝试升级到最新版本后重试!");
                Console.ResetColor();
                try { Console.CursorVisible = true; } catch { }
                return 1;
            });

            config.AddCommand<LoginCommand>("login")
                  .WithDescription("通过APP扫描二维码以登录您的WEB账号");
            config.AddCommand<LoginTVCommand>("logintv")
                  .WithDescription("通过APP扫描二维码以登录您的TV账号");
            config.AddCommand<ServeCommand>("serve")
                  .WithDescription("以服务器模式运行");
            config.AddCommand<LiveCommand>("live")
                  .WithDescription("录制B站直播流");
            config.AddCommand<ArticleCommand>("article")
                  .WithDescription("下载B站专栏文章为 Markdown");
            config.AddCommand<WatchLaterCommand>("watchlater")
                  .WithDescription("批量下载稍后再看列表(需登录)");
            config.AddBranch<SubSettings>("sub", sub =>
            {
                sub.SetDescription("订阅管理: 添加/列出/移除订阅，检查并增量下载新内容");
                sub.AddCommand<SubAddCommand>("add").WithDescription("添加订阅");
                sub.AddCommand<SubListCommand>("list").WithDescription("列出订阅");
                sub.AddCommand<SubRemoveCommand>("remove").WithDescription("移除订阅");
                sub.AddCommand<SubCheckCommand>("check").WithDescription("检查订阅并增量下载新内容");
            });
        });

        return await app.RunAsync(mergedArgs, _rootCts.Token);
    }

    internal static string[] NormalizeCliArgs(string[] args)
    {
        return args.Select(arg => arg switch
        {
            "-help" => "--help",
            "-?" => "--help",
            "-version" => "--version",
            _ => arg
        }).ToArray();
    }

    internal static async Task StartServerAsync(string? listenUrl, int maxConcurrent = 3, string? serveToken = null, string? notifyWebhook = null, CancellationToken cancellationToken = default, bool trustProxy = false)
    {
        var defaultListenUrl = "http://127.0.0.1:23333";
        // serve 为长驻进程：标记模式，此后各任务的 --work-dir 不再写进程 CWD
        IsServeMode = true;
        Logger.LogFilePath = Path.Combine(Directory.GetCurrentDirectory(), "bbdown-api.log");
        var server = new BBDownApiServer(maxConcurrent, serveToken, notifyWebhook: notifyWebhook, trustProxy: trustProxy);
        server.SetupServer();
        try
        {
            await server.RunAsync(string.IsNullOrEmpty(listenUrl) ? defaultListenUrl : listenUrl, cancellationToken);
        }
        finally
        {
            // 关停后释放持久日志 writer 的文件句柄（RunAsync 可能抛异常，finally 保证释放）
            Logger.CloseFile();
        }
    }

    internal static async Task DoWorkAsync(MyOption myOption, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (encodingPriority, dfnPriority, firstEncoding, downloadDanmaku, downloadDanmakuFormats,
            input, savePathFormat, lang, aidOri, delay) = SetUpWork(myOption);
        var (fetchedAid, vInfo, apiType, session) = await GetVideoInfoAsync(myOption, aidOri, input, cancellationToken);
        // GetVideoInfoAsync 在子异步流程中加载的凭据与提取的 wbi 不会自动回流父流程
        // （AsyncLocal 语义），这里在父流程内显式应用，确保后续 DownloadPagesAsync →
        // Parser.WbiSign 用上新密钥与本地凭据。GetVideoInfoAsync 内部已对自身流程应用。
        if (session is not null) Core.Config.Apply(session);
        await DownloadPagesAsync(myOption, vInfo, encodingPriority, dfnPriority, firstEncoding, downloadDanmaku, downloadDanmakuFormats,
            input, savePathFormat, lang, fetchedAid, delay, apiType, cancellationToken: cancellationToken);
    }

}
