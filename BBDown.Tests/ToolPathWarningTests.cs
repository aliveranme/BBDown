using BBDown.Core;

namespace BBDown.Tests;

/// <summary>
/// 显式指定的外部工具路径不存在时必须明确告警且不得被采纳：
/// 此前 <c>--ffmpeg-path/--mp4box-path/--aria2c-path</c> 指向不存在的文件时静默回退
/// 到程序目录/PATH 的同名程序——GUI 预填的路径实际不存在时用户无感知地用了别的版本；
/// 且两个来源都失败时报错仍建议"使用 --ffmpeg-path 指定路径"，而用户明明指定了。
/// SkipMux=true 使本测试不依赖本机是否安装 ffmpeg/mp4box（不触发回退查找）。
/// </summary>
public class ToolPathWarningTests
{
    [Fact]
    public void FindBinaries_ExplicitMissingPaths_WarnsAndDoesNotAdoptThem()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"bbdown-toolpath-{Guid.NewGuid():N}.txt");
        var originalLogPath = Logger.LogFilePath;
        var originalFfmpeg = BBDownMuxer.FFMPEG;
        var originalMp4box = BBDownMuxer.MP4BOX;
        var originalAria2c = BBDownAria2c.ARIA2C;
        var missingDir = Path.Combine(Path.GetTempPath(), $"bbdown-missing-{Guid.NewGuid():N}");
        var missingFfmpeg = Path.Combine(missingDir, "ffmpeg.exe");
        var missingMp4box = Path.Combine(missingDir, "mp4box.exe");
        var missingAria2c = Path.Combine(missingDir, "aria2c.exe");
        try
        {
            Logger.LogFilePath = logPath;
            Program.FindBinariesForTest(new MyOption
            {
                SkipMux = true,
                UseAria2c = false,
                FFmpegPath = missingFfmpeg,
                Mp4boxPath = missingMp4box,
                Aria2cPath = missingAria2c,
            });

            var log = ReadAllTextShared(logPath);
            Assert.Contains("--ffmpeg-path 指定的文件不存在", log);
            Assert.Contains(missingFfmpeg, log);
            Assert.Contains("--mp4box-path 指定的文件不存在", log);
            Assert.Contains(missingMp4box, log);
            Assert.Contains("--aria2c-path 指定的文件不存在", log);
            Assert.Contains(missingAria2c, log);
            // 不存在的显式路径不得被采纳为工具路径（静态值保持原样，回退查找未启用）
            Assert.NotEqual(missingFfmpeg, BBDownMuxer.FFMPEG);
            Assert.NotEqual(missingMp4box, BBDownMuxer.MP4BOX);
            Assert.NotEqual(missingAria2c, BBDownAria2c.ARIA2C);
        }
        finally
        {
            Logger.LogFilePath = originalLogPath;
            Logger.CloseFile();
            BBDownMuxer.FFMPEG = originalFfmpeg;
            BBDownMuxer.MP4BOX = originalMp4box;
            BBDownAria2c.ARIA2C = originalAria2c;
            try { if (File.Exists(logPath)) File.Delete(logPath); } catch (IOException) { }
        }
    }

    [Fact]
    public void FindBinaries_ExplicitExistingPath_IsAdoptedWithoutWarning()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"bbdown-toolpath-{Guid.NewGuid():N}.txt");
        var originalLogPath = Logger.LogFilePath;
        var originalFfmpeg = BBDownMuxer.FFMPEG;
        var fakeFfmpeg = Path.Combine(Path.GetTempPath(), $"bbdown-fake-ffmpeg-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllText(fakeFfmpeg, "");
            Logger.LogFilePath = logPath;
            Program.FindBinariesForTest(new MyOption { SkipMux = true, FFmpegPath = fakeFfmpeg });

            Assert.Equal(fakeFfmpeg, BBDownMuxer.FFMPEG);
            // 未产生任何日志时文件不会被创建（本用例预期无告警）：存在才读，不存在视为空
            var log = File.Exists(logPath) ? ReadAllTextShared(logPath) : "";
            Assert.DoesNotContain("指定的文件不存在", log);
        }
        finally
        {
            Logger.LogFilePath = originalLogPath;
            Logger.CloseFile();
            BBDownMuxer.FFMPEG = originalFfmpeg;
            try { if (File.Exists(logPath)) File.Delete(logPath); } catch (IOException) { }
            try { File.Delete(fakeFfmpeg); } catch (IOException) { }
        }
    }

    [Fact]
    public void FindBinaries_ExistingRelativePath_IsAdoptedAsAbsolutePath()
    {
        // 相对路径按"启动时目录"解析成绝对路径再采纳：CLI 无 -w 时 ChangeWorkingDir
        // 会把进程 CWD 切到默认下载目录，若原样保存相对串，混流/下载/解密阶段会按
        // 新 CWD 解析而找不到同一个文件（此前无 -w 不切 CWD，本可正常工作）。
        var originalCwd = Environment.CurrentDirectory;
        var originalFfmpeg = BBDownMuxer.FFMPEG;
        var originalMp4box = BBDownMuxer.MP4BOX;
        var originalAria2c = BBDownAria2c.ARIA2C;
        var dir = Path.Combine(Path.GetTempPath(), $"bbdown-reltool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            const string toolRel = "relative-ffmpeg.exe";
            Environment.CurrentDirectory = dir;
            var toolAbs = Path.Combine(Environment.CurrentDirectory, toolRel);
            File.WriteAllText(toolAbs, "");
            var option = new MyOption
            {
                SkipMux = true,
                FFmpegPath = toolRel,
                Mp4boxPath = toolRel,
                Aria2cPath = toolRel,
                WvdPath = toolRel,
                Mp4decryptPath = toolRel,
            };
            Program.FindBinariesForTest(option);

            Assert.Equal(toolAbs, BBDownMuxer.FFMPEG);
            Assert.Equal(toolAbs, BBDownMuxer.MP4BOX);
            Assert.Equal(toolAbs, BBDownAria2c.ARIA2C);
            Assert.Equal(toolAbs, option.WvdPath);
            Assert.Equal(toolAbs, option.Mp4decryptPath);
            Assert.True(Path.IsPathFullyQualified(BBDownMuxer.FFMPEG), "采纳的路径必须是绝对路径");
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            BBDownMuxer.FFMPEG = originalFfmpeg;
            BBDownMuxer.MP4BOX = originalMp4box;
            BBDownAria2c.ARIA2C = originalAria2c;
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>以 FileShare.ReadWrite 读取：兼容 Logger writer 持有的文件（见 LoggerFileTests）。</summary>
    private static string ReadAllTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }
}
