using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using BBDown;
using BBDown.Core;

namespace BBDown.Commands;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
public class LiveSettings : CommandSettings
{
    [CommandArgument(0, "<room_id>")]
    [Description("直播间 ID，如 12345")]
    public string RoomId { get; set; } = "";

    [CommandOption("-o|--output")]
    [Description("输出文件路径(默认: 直播间标题_直播录制_时间.flv)")]
    public string? Output { get; set; }

    [CommandOption("-c|--cookie")]
    [Description("设置字符串cookie(不设置则自动读取本地 BBDown.data 登录凭据)")]
    public string Cookie { get; set; } = "";

    [CommandOption("--access-token")]
    [Description("设置access_token")]
    public string AccessToken { get; set; } = "";
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
public class LiveCommand : Command<LiveSettings>
{
    protected override int Execute(CommandContext context, LiveSettings settings, CancellationToken cancellationToken)
    {
        // Task.Run avoids deadlock if called from a thread with a SynchronizationContext
        return Task.Run(async () =>
        {
            try
            {
                // 解析 ffmpeg：录制结束后的分段合成依赖 ffmpeg。直播命令不走主下载的
                // SetUpWork/FindBinaries 流程，这里显式探测——若 PATH/当前目录均无
                // ffmpeg，合成阶段会失败且只在收尾时暴露；提前报错让用户尽快安装。
                if (string.IsNullOrEmpty(BBDownMuxer.FFMPEG) || !File.Exists(BBDownMuxer.FFMPEG))
                {
                    var binPath = ExternalToolHelper.FindExecutable("ffmpeg");
                    if (string.IsNullOrEmpty(binPath))
                        throw new FileNotFoundException(
                            "找不到可执行的ffmpeg文件，直播分段合成需要 ffmpeg。请安装 ffmpeg 并确保其已加入 PATH 后重试。");
                    BBDownMuxer.FFMPEG = binPath;
                }

                // 加载登录凭据（--cookie 显式传入优先，否则读取本地 BBDown.data）：
                // 直播画质接口 getRoomPlayInfo 对未登录请求只返回游客画质（最高 720P），
                // 带 Cookie 才返回账号可看的最高画质（原画/杜比/4K 按账号权限）。
                // 此前 live 命令不加载凭据，已登录用户也只能录到 720P。
                var myOption = new MyOption { Cookie = settings.Cookie, AccessToken = settings.AccessToken };
                AppSettings? session = await Program.InitializeRequestSessionAsync(myOption, cancellationToken);
                if (session is not null) Config.Apply(session);

                Logger.Log($"正在解析直播间 {settings.RoomId}...");
                var (_, title, uname, _, quality) = await LiveStreamUtil.ResolveAsync(settings.RoomId, cancellationToken);
                Logger.Log($"直播间: {title} (UP: {uname})，画质: {LiveStreamUtil.QualityName(quality)} (qn={quality})");
                string path = settings.Output ?? $"{LiveStreamUtil.SanitizeFileName(title)}_直播录制_{DateTime.Now:yyyyMMdd_HHmmss}.flv";
                Logger.Log($"开始录制直播流: {path} (Ctrl+C 停止；断流/网络中断自动重连续录)");

                DateTime lastLog = DateTime.MinValue;
                // 传 roomId：断流/地址过期/网络中断时内部重新解析流地址续录
                var recordResult = await LiveStreamUtil.DownloadToFileAsync(settings.RoomId, path, total =>
                {
                    if (DateTime.Now - lastLog >= TimeSpan.FromSeconds(5))
                    {
                        lastLog = DateTime.Now;
                        Logger.Log($"已录制: {BBDownUtil.FormatFileSize(total)}");
                    }
                }, cancellationToken);

                if (recordResult == LiveStreamUtil.LiveRecordResult.NoData)
                {
                    // 未收到任何字节就结束：不生成空文件，也不报告"录制已保存"
                    Logger.LogWarn("录制未收到任何数据，未保存文件");
                    return 1;
                }
                else if (recordResult == LiveStreamUtil.LiveRecordResult.ConcatFailedWithSegmentsSaved)
                {
                    Logger.LogError("录制分段已保留，但未能自动合成最终文件。可手动使用 ffmpeg 进行 concat 合并。");
                    return 1;
                }
                Logger.Log($"录制已保存: {path}");
                return 0;
            }
            // 仅真正的用户取消返回 0；HttpClient 超时/重连耗尽抛出的
            // TaskCanceledException（token 未取消）必须落到下方失败分支返回 1，
            // 否则录制实际失败却以成功码退出，脚本/CI 拿不到失败信号。
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Logger.LogWarn("录制已取消");
                return 0;
            }
            catch (Exception ex)
            {
                Logger.LogError($"直播录制失败: {ex.Message}");
                return 1;
            }
        }).GetAwaiter().GetResult();
    }
}
