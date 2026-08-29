using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using BBDown;
using BBDown.Core;

namespace BBDown.Commands;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
public class ArticleSettings : CommandSettings
{
    [CommandArgument(0, "<cv_id>")]
    [Description("专栏 ID 或链接，如 cv123 或 https://www.bilibili.com/read/cv123")]
    public string CvId { get; set; } = "";

    [CommandOption("-o|--output")]
    [Description("输出 Markdown 文件路径(默认: 专栏标题.md，输出到 --work-dir 目录下)")]
    public string? Output { get; set; }

    [CommandOption("-w|--work-dir")]
    [Description("设置工作目录(默认: 当前目录；未指定--output时专栏 Markdown 将输出到该目录)")]
    public string WorkDir { get; set; } = "";
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
public class ArticleCommand : AsyncCommand<ArticleSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ArticleSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            string cvId = ArticleUtil.ExtractCvId(settings.CvId);
            Logger.Log($"正在获取专栏 cv{cvId}...");
            var article = await ArticleUtil.FetchAsync(cvId, cancellationToken);
            // 专栏默认输出目录尊重 --work-dir（与主下载命令一致）。
            // 相对 --output 同样相对 --work-dir 解析（Path.Combine 对已根化第二参数
            // 直接透传，绝对路径不受影响）；workDir 为空串时保持 CWD 语义。
            // 用纯函数 ResolveWorkDir：仅解析/建目录，不切进程 CWD（无全局副作用）。
            string workDir = Program.ResolveWorkDir(settings.WorkDir);
            string path = settings.Output is null
                ? Path.Combine(workDir, $"{LiveStreamUtil.SanitizeFileName(article.Title)}.md")
                : Path.Combine(workDir, settings.Output);
            await ArticleUtil.SaveAsMarkdownAsync(article, path);
            Logger.Log($"专栏已保存: {path}");
            return 0;
        }
        catch (OperationCanceledException ex)
        {
            // 区分主动取消（Ctrl+C，token 已取消）与 HttpClient 超时（token 未取消）：
            // 超时是真实失败，返回非零退出码而非以"已取消"+0 隐藏失败
            if (cancellationToken.IsCancellationRequested)
            {
                Logger.LogWarn("已取消");
                return 0;
            }
            Logger.LogError($"专栏获取超时或被中断: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Logger.LogError($"专栏获取失败: {ex.Message}");
            return 1;
        }
    }
}
