using System;
using BBDown.Core;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BBDown;

public static class ExternalToolHelper
{
    /// <summary>
    /// 检测ffmpeg是否识别杜比视界
    /// </summary>
    /// <remarks>RF-22：改真异步——原 <c>WaitForExit(5000)</c> + <c>GetAwaiter().GetResult()</c>
    /// 在 async 下载链路中同步阻塞线程（杜比视界命中时每 P 最多 5 秒），且超时分支
    /// 直接 return 不观察 stdout/stderr 读取任务（UnobservedTaskException 面）。</remarks>
    public static async Task<bool> CheckFFmpegDOVIAsync()
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = BBDownMuxer.FFMPEG;
            process.StartInfo.Arguments = "-version";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            var outTask = process.StandardOutput.ReadToEndAsync();
            var errTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                // Kill 整树后仍须观察管道任务（Kill 后管道断裂可能以异常完成），
                // 与 ExternalProcessRunner 的收尾语义一致，防 UnobservedTaskException。
                try { process.Kill(true); } catch { }
                try { await Task.WhenAll(outTask, errTask); } catch { }
                return false;
            }
            string info = await outTask + Environment.NewLine + await errTask;
            var match = BBDownUtil.LibavutilRegex().Match(info);
            if (!match.Success) return false;
            int major = Convert.ToInt32(match.Groups[1].Value);
            int minor = Convert.ToInt32(match.Groups[2].Value);
            if (major > 57 || (major == 57 && minor >= 17))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or OverflowException)
        {
            Logger.LogDebug("检测ffmpeg版本失败: {0}", ex.Message);
        }
        return false;
    }

    public static string? FindExecutable(string name)
    {
        var fileExt = OperatingSystem.IsWindows() ? ".exe" : "";
        // 只在程序目录（APP_DIR）与 PATH 中查找，绝不搜索当前工作目录：
        // BBDown 是下载器，常被放在任意视频/下载目录运行，该目录里若有先前植入的
        // ffmpeg.exe/aria2c.exe/mp4box.exe（或误入的伪造二进制），会静默执行本地
        // 伪造文件（可执行文件劫持）。APP_DIR 与 PATH 是用户/包管理器建立的可信位置。
        // APP_DIR 优先于 PATH：程序自带/随包分发的工具版本受控，不应被 PATH 中同名
        // 旧版或注入的工具覆盖。
        var envPath = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        var searchPath = new[] { Program.APP_DIR }.Concat(envPath);
        return searchPath.Select(p => Path.Combine(p, name + fileExt)).FirstOrDefault(IsExecutableFile);
    }

    /// <summary>文件存在且可执行：Unix 上校验执行位（File.Exists 对无执行位的文件也返回 true，
    /// 若不校验会被选中后到 Process.Start 才失败）。</summary>
    private static bool IsExecutableFile(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
