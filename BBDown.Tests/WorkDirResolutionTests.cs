using BBDown.Core;
using BBDown.Core.Util;

namespace BBDown.Tests;

/// <summary>
/// B1：消灭 serve 模式 CWD 全局写。下载管线的相对路径统一经
/// <see cref="PathUtil.ResolveWorkPath"/> 基于任务流配置的 WorkDir 解析；
/// serve 模式下 ChangeWorkingDir 不再写进程 CWD（并发任务各自的 --work-dir
/// 经 AsyncLocal 配置快照隔离），CLI 单任务仍写 CWD 以兼容子进程相对路径。
/// </summary>
public class WorkDirResolutionTests
{
    [Fact]
    public void ResolveWorkPath_Relative_ResolvesAgainstCwd_WhenNoWorkDir()
    {
        var original = Config.Current.WorkDir;
        try
        {
            Config.Apply(Config.Current with { WorkDir = "" });
            Assert.Equal(Path.GetFullPath("12345/a.mp4"), PathUtil.ResolveWorkPath("12345/a.mp4"));
        }
        finally
        {
            Config.Apply(Config.Current with { WorkDir = original });
        }
    }

    [Fact]
    public void ResolveWorkPath_Relative_ResolvesAgainstWorkDir_WhenSet()
    {
        var original = Config.Current.WorkDir;
        var workDir = Path.Combine(Path.GetTempPath(), "bbdown-wd-" + Guid.NewGuid().ToString("N"));
        try
        {
            Config.Apply(Config.Current with { WorkDir = workDir });
            Assert.Equal(Path.Combine(workDir, "12345/a.mp4"), PathUtil.ResolveWorkPath("12345/a.mp4"));
        }
        finally
        {
            Config.Apply(Config.Current with { WorkDir = original });
        }
    }

    [Fact]
    public void ResolveWorkPath_Absolute_PassesThroughRegardlessOfWorkDir()
    {
        var original = Config.Current.WorkDir;
        var workDir = Path.Combine(Path.GetTempPath(), "bbdown-wd-" + Guid.NewGuid().ToString("N"));
        var abs = Path.Combine(Path.GetTempPath(), "custom-out.mp4");
        try
        {
            Config.Apply(Config.Current with { WorkDir = workDir });
            // 自定义 --file-pattern 指向绝对目录时不受 WorkDir 影响
            Assert.Equal(abs, PathUtil.ResolveWorkPath(abs));
        }
        finally
        {
            Config.Apply(Config.Current with { WorkDir = original });
        }
    }

    [Fact]
    public void ResolveWorkPath_Empty_PassesThrough()
    {
        var original = Config.Current.WorkDir;
        try
        {
            Config.Apply(Config.Current with { WorkDir = Path.GetTempPath() });
            Assert.Equal("", PathUtil.ResolveWorkPath(""));
        }
        finally
        {
            Config.Apply(Config.Current with { WorkDir = original });
        }
    }

    [Fact]
    public async Task ResolveWorkPath_ConcurrentFlows_EachUsesOwnWorkDir()
    {
        // serve 并发任务：每个 /add-task 流各自 SetUpWork 写入自己的 WorkDir。
        // AsyncLocal 隔离要求两个并发流解析相对路径时各用各的目录，互不读对方的。
        var original = Config.Current.WorkDir;
        var wdA = Path.Combine(Path.GetTempPath(), "bbdown-wd-a-" + Guid.NewGuid().ToString("N"));
        var wdB = Path.Combine(Path.GetTempPath(), "bbdown-wd-b-" + Guid.NewGuid().ToString("N"));
        try
        {
            var t1 = Task.Run(() =>
            {
                Config.Apply(Config.Current with { WorkDir = wdA });
                Task.Delay(30).Wait();
                return PathUtil.ResolveWorkPath("12345/a.mp4");
            });
            var t2 = Task.Run(() =>
            {
                Config.Apply(Config.Current with { WorkDir = wdB });
                Task.Delay(30).Wait();
                return PathUtil.ResolveWorkPath("12345/a.mp4");
            });
            var results = await Task.WhenAll(t1, t2);
            Assert.Equal(Path.Combine(wdA, "12345/a.mp4"), results[0]);
            Assert.Equal(Path.Combine(wdB, "12345/a.mp4"), results[1]);
        }
        finally
        {
            Config.Apply(Config.Current with { WorkDir = original });
        }
    }

    [Fact]
    public void ChangeWorkingDir_ServeMode_DoesNotWriteProcessCwd()
    {
        var originalServeMode = Program.IsServeMode;
        var originalCwd = Environment.CurrentDirectory;
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-wd-" + Guid.NewGuid().ToString("N"));
        try
        {
            Program.IsServeMode = true;
            var option = new MyOption { WorkDir = dir };
            var resolved = Program.ChangeWorkingDir(option);
            Assert.Equal(Path.GetFullPath(dir), resolved);
            // serve 模式绝不写进程 CWD：并发任务各自的 --work-dir 不能互相覆盖进程级状态
            Assert.Equal(originalCwd, Environment.CurrentDirectory);
            Assert.True(Directory.Exists(resolved), "serve 下仍应创建目录（供 ResolveWorkPath 使用）");
        }
        finally
        {
            Program.IsServeMode = originalServeMode;
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ChangeWorkingDir_NoWorkDir_ReturnsEmpty_AndDoesNotTouchCwd()
    {
        var originalServeMode = Program.IsServeMode;
        var originalCwd = Environment.CurrentDirectory;
        try
        {
            Program.IsServeMode = true;
            Assert.Equal("", Program.ChangeWorkingDir(new MyOption()));
            Assert.Equal(originalCwd, Environment.CurrentDirectory);
        }
        finally
        {
            Program.IsServeMode = originalServeMode;
        }
    }

    [Fact]
    public void ChangeWorkingDir_CliMode_NoWorkDir_FallsBackToProgramDownloadDir()
    {
        // 未指定 --work-dir 时默认输出到 <程序目录>\Download（与 GUI 的默认落点一致），
        // 不再落到运行命令时的 CWD；CLI 单任务语义仍写进程 CWD（子进程相对路径依赖）。
        var originalServeMode = Program.IsServeMode;
        var originalCwd = Environment.CurrentDirectory;
        var expected = Path.Combine(Program.APP_DIR, "Download");
        try
        {
            Program.IsServeMode = false;
            var resolved = Program.ChangeWorkingDir(new MyOption());
            Assert.Equal(expected, resolved);
            Assert.True(Path.IsPathFullyQualified(resolved), "默认目录必须是绝对路径");
            Assert.True(Directory.Exists(resolved), "应自动创建默认下载目录");
            Assert.Equal(resolved, Environment.CurrentDirectory);
        }
        finally
        {
            Program.IsServeMode = originalServeMode;
            Environment.CurrentDirectory = originalCwd;
            // 仅在空目录时清理（不侵占真实数据；测试环境中该目录由本测试创建）
            try
            {
                if (Directory.Exists(expected) && !Directory.EnumerateFileSystemEntries(expected).Any())
                    Directory.Delete(expected);
            }
            catch (IOException) { }
        }
    }

    [Fact]
    public void SetUpWork_WorkDir_IsAppliedToTaskFlow_WithoutWritingCwd()
    {
        var originalServeMode = Program.IsServeMode;
        var originalConfig = Config.Current;
        var originalCwd = Environment.CurrentDirectory;
        var dir = Path.Combine(Path.GetTempPath(), "bbdown-wd-" + Guid.NewGuid().ToString("N"));
        try
        {
            Program.IsServeMode = true; // serve 语义：SetUpWork 不写进程 CWD
            // SkipMux=true 避免 FindBinaries 要求本机存在 ffmpeg
            var option = new MyOption { WorkDir = dir, SkipMux = true };
            Program.SetUpWork(option);

            // WorkDir 已写入当前任务流配置，供 PathUtil.ResolveWorkPath 解析相对路径
            Assert.Equal(Path.GetFullPath(dir), Config.Current.WorkDir);
            // serve 模式未改动进程 CWD
            Assert.Equal(originalCwd, Environment.CurrentDirectory);
            Assert.True(Directory.Exists(Path.GetFullPath(dir)));
        }
        finally
        {
            Program.IsServeMode = originalServeMode;
            Config.Apply(originalConfig);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ResolvePerSubBaseWorkDir_NoWorkDir_FallsBackToDefaultWorkDir_NotStartupCwd()
    {
        // sub check --per-sub-dir 无 -w 的基目录必须与 ChangeWorkingDir 同源（程序目录
        // Download），而不是启动时的 CWD——否则同一命令加/不加 --per-sub-dir 的默认根分裂
        // （不加时任务经 ChangeWorkingDir 落默认目录，加了却落启动目录）。
        var originalCwd = Environment.CurrentDirectory;
        var expected = Path.Combine(Program.APP_DIR, "Download");
        try
        {
            Environment.CurrentDirectory = Path.GetTempPath(); // 与 APP_DIR 不同，保证断言有区分度
            Assert.Equal(expected, Program.ResolvePerSubBaseWorkDir(""));
            // 显式 -w（已由 TryResolveWorkDir 绝对化）原样透传，不再解析
            Assert.Equal("wd-x", Program.ResolvePerSubBaseWorkDir("wd-x"));
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            // 仅在空目录时清理（不侵占真实数据；测试环境中该目录由本测试创建）
            try
            {
                if (Directory.Exists(expected) && !Directory.EnumerateFileSystemEntries(expected).Any())
                    Directory.Delete(expected);
            }
            catch (IOException) { }
        }
    }

    [Fact]
    public void ChangeWorkingDir_DefaultDirNotice_LoggedAtMostOnce()
    {
        // 默认目录提示只打印一次：watchlater / sub check 的逐任务循环会反复进入
        // ChangeWorkingDir，重复打印会刷屏。静态门控跨用例残留（同进程先前用例可能
        // 已触发过一次），故断言"第一次至多一条、第二次不再新增"，而非"第一次必然恰好一条"。
        var logPath = Path.Combine(Path.GetTempPath(), $"bbdown-wdlog-{Guid.NewGuid():N}.txt");
        var originalLogPath = Logger.LogFilePath;
        var originalServeMode = Program.IsServeMode;
        var originalCwd = Environment.CurrentDirectory;
        try
        {
            Logger.LogFilePath = logPath;
            Program.IsServeMode = false;
            Program.ChangeWorkingDir(new MyOption());
            var afterFirst = CountOccurrences(ReadLogOrEmpty(logPath), "使用默认下载目录");
            Program.ChangeWorkingDir(new MyOption());
            var afterSecond = CountOccurrences(ReadLogOrEmpty(logPath), "使用默认下载目录");
            Assert.InRange(afterFirst, 0, 1);
            Assert.Equal(afterFirst, afterSecond);
        }
        finally
        {
            Logger.LogFilePath = originalLogPath;
            Logger.CloseFile();
            Program.IsServeMode = originalServeMode;
            Environment.CurrentDirectory = originalCwd;
            try { if (File.Exists(logPath)) File.Delete(logPath); } catch (IOException) { }
        }
    }

    private static string ReadLogOrEmpty(string path)
    {
        if (!File.Exists(path)) return "";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    private static int CountOccurrences(string text, string substring)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(substring, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += substring.Length;
        }
        return count;
    }

    // ── RF-89/RF-90：多任务命令的 -w 只解析一次（防相对 -w 在 CWD 漂移后嵌套）──

    [Fact]
    public void TryResolveWorkDir_Relative_ReturnsAbsolutePath_SoReuseIsCwdStable()
    {
        // 多任务命令（sub check / watchlater）在逐任务循环前只解析一次 -w。绝对化是
        // "只解析一次"的前提：ChangeWorkingDir 会写进程 CWD，若把相对 -w 留给每个任务
        // 各自解析，第二个任务会基于上一个任务的下载目录再拼一层（<root>/<task1>/<task2>）。
        var originalCwd = Environment.CurrentDirectory;
        var root = Path.Combine(Path.GetTempPath(), "bbdown-wd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Environment.CurrentDirectory = root;
            var effectiveRoot = Environment.CurrentDirectory;
            Assert.True(Program.TryResolveWorkDir("out", out var resolved, out var error));
            Assert.Equal("", error);
            Assert.True(Path.IsPathFullyQualified(resolved), "-w 必须解析为绝对路径，否则无法跨任务复用");
            Assert.Equal(Path.Combine(effectiveRoot, "out"), resolved);

            // 缺陷复现（旧行为）：CWD 漂移后继续用相对 -w 解析会再拼一层
            Environment.CurrentDirectory = resolved; // 等价于 ChangeWorkingDir 的副作用
            Assert.Equal(Path.Combine(resolved, "out"), Path.GetFullPath("out"));

            // 新行为：绝对化后的值在 CWD 漂移后重新解析仍指向同一目录，不会嵌套
            Assert.Equal(resolved, Path.GetFullPath(resolved));
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TryResolveWorkDir_UnusablePath_ReturnsErrorWithoutThrowing()
    {
        // RF-89：不可用的 -w 必须经 error 返回，不能抛到命令级异常处理器
        // （那会被报成"请尝试升级到最新版本后重试!"并静默放弃其余全部任务）。
        // 用例取"已存在同名文件"——跨平台可靠且是真实可能发生的用户输入错误
        // （-w 指向文件而非目录）：Path.GetFullPath 通过，Directory.CreateDirectory 抛 IOException。
        var file = Path.Combine(Path.GetTempPath(), "bbdown-notdir-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "");
        try
        {
            Assert.False(Program.TryResolveWorkDir(file, out var resolved, out var error));
            Assert.Equal("", resolved);
            Assert.NotEqual("", error);

            // 子目录形态（-w 指向该文件下的子目录）同样失败
            Assert.False(Program.TryResolveWorkDir(Path.Combine(file, "sub"), out _, out _));
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    [Fact]
    public void TryResolveWorkDir_WindowsInvalidPathChars_ReturnsErrorWithoutThrowing()
    {
        // 与上一个用例互补：'|' 与纯空白是 Windows 下 Path.GetFullPath 直接拒绝的路径
        // （ArgumentException 分支），走的是 catch 白名单的另一条分支。
        // Linux 允许这类名称，故仅在 Windows 断言，避免跨平台误报。
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(Program.TryResolveWorkDir("a|b", out _, out var error));
        Assert.NotEqual("", error);
        Assert.False(Program.TryResolveWorkDir("   ", out _, out _));
    }

    [Fact]
    public void TryResolveWorkDir_Empty_KeepsExistingSemantics()
    {
        // 空 -w 保持原语义：解析成功且为空串（不写 WorkDir），由调用方回落到进程 CWD
        Assert.True(Program.TryResolveWorkDir("", out var resolved, out var error));
        Assert.Equal("", resolved);
        Assert.Equal("", error);
    }
}
