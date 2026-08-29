using System;
using BBDown.Core;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
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

    /// <summary>不消耗值的选项（bool 开关）的规范属性名。</summary>
    private static readonly HashSet<string> FlagOptionCanonicals = BuildFlagCanonicals();

    private static HashSet<string> BuildFlagCanonicals()
    {
        var flags = new HashSet<string>(StringComparer.Ordinal);
        void ScanType([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] Type type)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetCustomAttribute<CommandOptionAttribute>() != null && prop.PropertyType == typeof(bool))
                    flags.Add(prop.Name);
            }
        }
        ScanType(typeof(MyOption));
        ScanType(typeof(Commands.ServeSettings));
        return flags;
    }

    /// <summary>
    /// 判断本次调用是否是子命令。子命令总是第一个位置参数；
    /// 需要值的选项会吞掉下一个 token，扫描时必须跳过，否则
    /// "--config-file <path> sub list" 的 path 会被误判为位置参数。
    /// </summary>
    internal static bool IsSubCommandInvocation(string[] args)
    {
        var aliasMap = BuildAliasMap();
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith('-'))
                return SubCommandNames.Contains(arg, StringComparer.OrdinalIgnoreCase);

            var token = arg;
            var eq = token.IndexOf('=');
            if (eq > 0) continue; // "--opt=value"：值已含在 token 内，不消耗下一项
            if (aliasMap.TryGetValue(token, out var canonical) && !FlagOptionCanonicals.Contains(canonical))
                i++; // 该选项需要值：下一 token 是它的值，跳过
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
        var aliasMap = BuildAliasMap();
        var positionals = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith('-'))
            {
                positionals.Add(arg);
                continue;
            }
            var token = arg;
            var eq = token.IndexOf('=');
            if (eq > 0) continue; // "--opt=value"：值已含在 token 内，不消耗下一项
            if (aliasMap.TryGetValue(token, out var canonical) && !FlagOptionCanonicals.Contains(canonical))
                i++; // 该选项需要值：下一 token 是它的值，跳过
        }
        return positionals;
    }

    public static List<string> MergeWithConfig(string[] cliArgs)
    {
        var result = new List<string>(cliArgs);

        // 配置合并只服务默认下载命令：子命令的 Settings（SubListSettings、LiveSettings 等）
        // 只声明各自少量选项，把配置文件里的下载选项全集合并进去会被 Spectre
        // 以 unknown option 拒绝，导致"存在 BBDown.config 时 sub/live 等命令整体不可用"。
        if (IsSubCommandInvocation(cliArgs))
            return result;

        // 同时支持 "--config-file path" 与 "--config-file=path" 两种写法；
        // 旧实现只认空格写法，等号写法会被忽略而回落到默认配置路径。
        string? configPath = null;
        for (int i = 0; i < cliArgs.Length; i++)
        {
            if (cliArgs[i] == "--config-file")
            {
                configPath = cliArgs.ElementAtOrDefault(i + 1);
                break;
            }
            if (cliArgs[i].StartsWith("--config-file=", StringComparison.Ordinal))
            {
                configPath = cliArgs[i]["--config-file=".Length..];
                break;
            }
        }

        if (string.IsNullOrEmpty(configPath))
            configPath = Path.Combine(Program.APP_DIR, "BBDown.config");

        if (!File.Exists(configPath))
            return result;

        Logger.Log($"加载配置文件: {configPath}");

        // 加载发生在 Main 的异常处理器建立之前，裸异常会直接打印堆栈崩溃：
        // File.Exists 对目录也返回 true，ReadAllLines 会抛 UnauthorizedAccessException。
        List<string> configArgs;
        try
        {
            if (Directory.Exists(configPath))
            {
                Logger.LogWarn($"配置文件路径是一个目录，已忽略: {configPath}");
                return result;
            }
            configArgs = File.ReadAllLines(configPath)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarn($"读取配置文件失败（已忽略）: {ex.Message}");
            return result;
        }

        var aliasMap = BuildAliasMap();

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
            var token = cliArgs[i];
            var eq = token.IndexOf('=');
            if (eq > 0) token = token[..eq];
            if (aliasMap.TryGetValue(token, out var canonical))
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

            if (aliasMap.TryGetValue(name, out var canonical))
            {
                if (!explicitOptions.Contains(canonical))
                {
                    result.Add(name);
                    i++;
                    // 收集该选项的值。仅当"以 - 开头且是已知选项名"时才视为下一个选项终止收集：
                    // 否则配置文件里值本身以 - 开头（如 --access-token -abc、负数参数）会被误当选项丢弃。
                    while (i < configArgs.Count && (!configArgs[i].StartsWith('-') || !aliasMap.ContainsKey(configArgs[i])))
                    {
                        result.Add(configArgs[i]);
                        i++;
                    }
                }
                else
                {
                    i++;
                    // 命令行已显式指定该选项：跳过配置文件里的值，判定规则同上
                    while (i < configArgs.Count && (!configArgs[i].StartsWith('-') || !aliasMap.ContainsKey(configArgs[i]))) i++;
                }
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

    private static Dictionary<string, string> BuildAliasMap()
    {
        var map = new Dictionary<string, string>();

        void ScanType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = prop.GetCustomAttribute<CommandOptionAttribute>();
                if (attr != null)
                {
                    var canonical = prop.Name;
                    foreach (var name in attr.LongNames)
                    {
                        map["--" + name] = canonical;
                    }
                    foreach (var name in attr.ShortNames)
                    {
                        map["-" + name] = canonical;
                    }
                }
            }
        }

        ScanType(typeof(MyOption));
        ScanType(typeof(Commands.ServeSettings));
        return map;
    }
}
