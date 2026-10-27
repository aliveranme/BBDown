using System;
using BBDown.Core;
using System.Collections.Generic;
using System.IO;
using BBDown.Core.Util;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console.Cli;

namespace BBDown;

internal static partial class BBDownConfigParser
{
    /// <summary>命令行位置参数（下载 URL）的形态特征，用于识别 URL 冲突。</summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(https?://|av\d+|bv[0-9A-Za-z]+|av:|bv:|ep\d+|ep:|ss\d+|ss:|md\d+|md:|cheese[:/]|mid:|favId:|listBizId:|seriesBizId:)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex UrlLikeToken();

    /// <summary>已注册的子命令名：其 Settings 只声明各自少量选项，不能承受下载选项全集。</summary>
    private static readonly string[] SubCommandNames =
        { "login", "logintv", "serve", "live", "article", "watchlater", "sub" };

    /// <summary>
    /// 剥掉 "--opt=value" 的内联值，返回选项名；无内联值时原样返回。
    /// I16：此前三处扫参循环各自写一遍 <c>IndexOf('=')</c> 判定。
    /// </summary>
    private static string OptionName(string token)
    {
        var eq = token.IndexOf('=');
        return eq > 0 ? token[..eq] : token;
    }

    /// <summary>
    /// 返回"跳过当前 token（含其值，若有）"之后的下标。规则三处共用（I16）：
    /// "--opt=value" 的值已含在 token 内、bool 开关不取值——都只前进 1；
    /// 只有 <see cref="CliOptionIndex.TakesValue"/> 认可的取值选项才吞掉下一个 token。
    /// </summary>
    private static int SkipArgument(string[] args, int index)
    {
        var token = args[index];
        if (OptionName(token) != token) return index + 1; // "--opt=value"：值已含在 token 内
        return CliOptionIndex.TakesValue(token) ? index + 2 : index + 1;
    }

    /// <summary>
    /// 取命令行上某个选项的值（支持 "--name value" 与 "--name=value" 两种写法），
    /// 选项未出现（或写在末尾无值）时返回 null。
    /// </summary>
    private static string? GetOptionValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name) return i + 1 < args.Length ? args[i + 1] : null;
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal)) return args[i][(name.Length + 1)..];
        }
        return null;
    }

    /// <summary>
    /// 配置选项最多消耗一个值。布尔选项只接受可选的 true/false，其他选项可接受
    /// 以 '-' 开头的值，但不消耗下一个已知选项。
    /// </summary>
    private static int CountOptionValueTokens(List<string> args, int index, string name)
    {
        if (index >= args.Count || OptionName(name) != name) return 0;
        if (!CliOptionIndex.TakesValue(name))
            return bool.TryParse(args[index], out _) ? 1 : 0;
        return CliOptionIndex.IsKnownOption(OptionName(args[index])) ? 0 : 1;
    }

    /// <summary>
    /// 判断本次调用是否是子命令。子命令总是第一个位置参数；
    /// 需要值的选项会吞掉下一个 token，扫描时必须跳过，否则
    /// "--config-file <path> sub list" 的 path 会被误判为位置参数。
    /// </summary>
    internal static bool IsSubCommandInvocation(string[] args)
    {
        for (int i = 0; i < args.Length;)
        {
            var arg = args[i];
            if (!arg.StartsWith('-'))
                return SubCommandNames.Contains(arg, StringComparer.OrdinalIgnoreCase);
            i = SkipArgument(args, i);
        }
        return false;
    }

    /// <summary>
    /// 提取命令行中的位置参数（即下载 URL 的候选位置）。识别"已显式给出 URL"时
    /// 只扫位置参数、不扫全部 argv：选项的值可能恰好形似 URL（如
    /// --aria2c-proxy http://127.0.0.1:7890、--work-dir av123），把"URL 在配置文件 +
    /// 命令行有 URL 形值选项"误判成"命令行已给出 URL"，配置文件里的 URL 被丢弃后
    /// Spectre 报缺少必填参数，用户难以定位（RF-7）。
    /// 与 <see cref="IsSubCommandInvocation"/> 同构：需要值的选项吞掉下一 token，
    /// bool 开关与 "--opt=value" 写法不吞。
    /// </summary>
    internal static List<string> GetPositionalTokens(string[] args)
    {
        var positionals = new List<string>();
        for (int i = 0; i < args.Length;)
        {
            var arg = args[i];
            if (!arg.StartsWith('-'))
            {
                positionals.Add(arg);
                i++;
                continue;
            }
            i = SkipArgument(args, i);
        }
        return positionals;
    }

    public static async Task<List<string>> MergeWithConfigAsync(
        string[] cliArgs, CancellationToken cancellationToken = default)
    {
        var result = new List<string>(cliArgs);

        // 配置合并只服务默认下载命令：子命令的 Settings（SubListSettings、LiveSettings 等）
        // 只声明各自少量选项，把配置文件里的下载选项全集合并进去会被 Spectre
        // 以 unknown option 拒绝，导致"存在 BBDown.config 时 sub/live 等命令整体不可用"。
        if (IsSubCommandInvocation(cliArgs))
            return result;

        // 同时支持 "--config-file path" 与 "--config-file=path" 两种写法；
        // 旧实现只认空格写法，等号写法会被忽略而回落到默认配置路径。
        string? configPath = GetOptionValue(cliArgs, "--config-file");

        if (string.IsNullOrEmpty(configPath))
            configPath = Path.Combine(Program.APP_DIR, "BBDown.config");

        if (!File.Exists(configPath))
            return result;

        Logger.Log($"加载配置文件: {configPath}");

        // 加载发生在 Main 的异常处理器建立之前，裸异常会直接打印堆栈崩溃：
        // File.Exists 对目录也返回 true，ReadAllLinesAsync 会抛 UnauthorizedAccessException。
        List<string> configArgs;
        try
        {
            if (Directory.Exists(configPath))
            {
                Logger.LogWarn($"配置文件路径是一个目录，已忽略: {configPath}");
                return result;
            }
            configArgs = (await File.ReadAllLinesAsync(configPath, cancellationToken))
                .Where(s => !string.IsNullOrWhiteSpace(s) && !s.TrimStart().StartsWith('#'))
                .SelectMany(line =>
                {
                    var trim = line.Trim();
                    if (trim.StartsWith('-') && trim.Contains(' '))
                    {
                        var idx = trim.IndexOf(' ');
                        return new[] { trim[..idx], trim[idx..].Trim().Trim('"') };
                    }
                    return new[] { trim.Trim('"') };
                })
                .ToList();
        }
        catch (Exception ex) when (ExceptionPolicies.IsBestEffortFailure(ex))
        {
            Logger.LogWarn($"读取配置文件失败（已忽略）: {ex.Message}");
            return result;
        }

        // 命令行已显式给出 URL 时，配置文件里的位置参数（URL）不再合并，
        // 否则 MyOption 只声明一个 <URL> 位置参数，Spectre 会报 unexpected positional argument。
        // 与"命令行显式给出的选项必须压过配置文件"的合并原则保持一致。
        // 只对位置参数应用 URL 启发式（RF-7）：全量扫描会把选项值（--aria2c-proxy 的
        // 代理地址、--work-dir 的 av123 等）误判为 URL，丢弃配置文件里的真实 URL。
        bool cliHasUrl = GetPositionalTokens(cliArgs).Any(a => UrlLikeToken().IsMatch(a));

        var explicitOptions = new HashSet<string>();
        for (int i = 0; i < cliArgs.Length; i++)
        {
            if (!cliArgs[i].StartsWith('-')) continue;
            // 命令行可写成 "--opt value" 或 "--opt=value"，识别"已显式指定"时
            // 必须剥掉等号后缀，否则等号写法匹配不到别名，会被配置文件反向覆盖。
            // 注意：本循环刻意**不**跳过选项的值（与上面两个扫描不同）——多识别一个
            // "-" 开头的已知选项名只会让该选项更"显式"，漏识别才会被配置文件反向覆盖。
            if (CliOptionIndex.TryGetCanonical(OptionName(cliArgs[i]), out var canonical))
            {
                explicitOptions.Add(canonical);
            }
        }

        for (int i = 0; i < configArgs.Count;)
        {
            var name = configArgs[i];
            if (!name.StartsWith('-'))
            {
                if (!cliHasUrl) result.Add(name);
                i++;
                continue;
            }

            if (CliOptionIndex.TryGetCanonical(OptionName(name), out var canonical))
            {
                // 值和位置参数分开处理，命令行覆盖选项时不能同时丢掉后面的 URL。
                int valueCount = CountOptionValueTokens(configArgs, i + 1, name);
                if (!explicitOptions.Contains(canonical))
                {
                    result.Add(name);
                    for (int k = 0; k < valueCount; k++) result.Add(configArgs[i + 1 + k]);
                }
                i += 1 + valueCount;
            }
            else
            {
                result.Add(name);
                i++;
            }
        }

        Logger.LogDebug("新的命令行参数: " + string.Join(" ", result));
        return result;
    }
}
