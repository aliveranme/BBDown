using System.Text;
using BBDown.Core.Util;

namespace BBDown.Core;

public static class Logger
{
    private static string? _logFilePath;
    /// <summary>
    /// 日志文件路径。设置时关闭旧持久 writer（路径变化场景下句柄不残留）。
    /// serve 启动时设为 CWD/bbdown-api.log；未设置时只写控制台。
    /// </summary>
    public static string? LogFilePath
    {
        get => _logFilePath;
        set
        {
            lock (_fileLock)
            {
                if (_logFilePath == value) return;
                _logFilePath = value;
                // 换路径时重置失败闭锁：新路径可能可写，不能让旧路径的失败状态永久禁用文件日志
                _fileWriteDisabled = false;
                _fileWriteFailures = 0;
                _fileWriteDisabledAt = 0;
                CloseFileWriterLocked();
            }
        }
    }

    // Console.ForegroundColor 是进程级全局状态：并发下载时多线程交错"设色-写入-复位"
    // 会互相插入颜色区间，导致日志颜色错乱、行内容交错。单条日志的写入需整体加锁。
    private static readonly object _consoleLock = new();
    public static object ConsoleLock => _consoleLock;

    // 文件写入串行化：与 Console 写入的先后顺序由锁保证（Log/LogColor/LogWarn 先取
    // _consoleLock、释放后再 AppendToFile 取 _fileLock，不构成嵌套锁，不会死锁）。
    private static readonly object _fileLock = new();
    private static StreamWriter? _fileWriter;

    /// <summary>连续创建/写入日志文件失败达到该阈值后禁用文件日志：否则路径不可写时
    /// 每一行日志都会重试打开文件并抛一次异常（CreateFileWriter 返回 null），
    /// 高频日志下产生无意义的异常噪音与开销。</summary>
    private const int MaxFileWriteFailures = 5;
    /// <summary>闭锁后的恢复冷却：每这么久尝试一次重建 writer，使瞬时故障（磁盘满/临时占用）
    /// 缓解后能自动恢复文件日志，而不是闭锁到进程结束（serve 的日志路径是固定的，不会重设）。</summary>
    private const long RetryCooldownMs = 30_000;
    private static int _fileWriteFailures;
    private static bool _fileWriteDisabled;
    private static long _fileWriteDisabledAt;

    private static void WriteLine(string line)
    {
        Console.WriteLine(line);
        AppendToFile(line);
    }

    private static StreamWriter? CreateFileWriter()
    {
        if (string.IsNullOrEmpty(_logFilePath)) return null;
        try
        {
            // 持久句柄 + AutoFlush：替代每行 File.AppendAllText 的开合文件（高频日志下最大的
            // 同步 IO 开销）。AutoFlush 保证每行立即落盘（崩溃/断电不丢行）。
            // FileShare.ReadWrite|Delete：允许 tail/Get-Content（ReadWrite 共享）外部读取，
            // 也允许 Windows 上外部删除/轮转（File.Delete 需要 FILE_SHARE_DELETE）。
            // 注意：默认 FileShare.Read 的读取器（File.ReadAllText）在 Windows 上仍会因不授予
            // 写共享而失败——这是"持有一个写句柄"的固有属性，读日志请用 tail/Get-Content。
            // Unix 上创建即 owner-only（与 BBDown.data 凭据文件同约定）：serve 日志含任务路径、
            // URL 等按仓库约定属敏感的操作者活动，不应以 umask 默认的组/其他可读落盘。
            var opts = new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.ReadWrite | FileShare.Delete,
            };
            if (!OperatingSystem.IsWindows())
            {
                // UnixCreateMode 只作用于新文件。升级时也收紧已有日志，避免旧版 umask
                // 创建的 0644 文件继续暴露任务路径、URL 等操作者数据。
                if (File.Exists(_logFilePath))
                    File.SetUnixFileMode(_logFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            var fs = new FileStream(_logFilePath, opts);
            return new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex) when (ExceptionPolicies.IsBestEffortFailure(ex))
        {
            // 路径不可写：降级为仅控制台
            return null;
        }
    }

    private static void AppendToFile(string line)
    {
        if (string.IsNullOrEmpty(_logFilePath)) return;
        // 闭锁期间周期性尝试恢复：瞬时故障（磁盘满/临时占用）缓解后能自动恢复文件日志，
        // 而非闭锁到进程结束（serve 的日志路径固定，不会重设）。恢复前只按 RetryCooldownMs
        // 频率重试一次，不产生每行重试的异常噪音。闭锁状态在锁内写、这里锁外读，是
        // best-effort 短路：偶发读到过期值最多让一次调用进锁内多试一次，无害。
        if (_fileWriteDisabled && Environment.TickCount64 - _fileWriteDisabledAt < RetryCooldownMs)
            return;
        // 一次性提示（禁用/恢复）在锁外打印：不经 _consoleLock 的纯文本，避免 _fileLock
        // 内再取 _consoleLock 破坏"所有控制台写入经 _consoleLock 串行化"的既有锁序。
        // 单行、频率极低，与颜色化日志的交错是外观级的，可接受。
        string? notice = null;
        try
        {
            lock (_fileLock)
            {
                // 轮转自愈：外部 mv/删除后文件被替换时重开 writer，否则日志会写入已重命名的
                // 旧 inode（Unix 上 FileShare 无效，mv 后新文件永远收不到日志；用长度对比检测）。
                if (_fileWriter is not null && LogFileReplacedLocked())
                    CloseFileWriterLocked();
                _fileWriter ??= CreateFileWriter();
                if (_fileWriter is null)
                {
                    // 路径不可写（CreateFileWriter 返回 null）。连续失败达到阈值即闭锁，
                    // 否则每行日志都会重试打开文件并抛一次异常。阈值内的偶发失败允许自愈。
                    if (++_fileWriteFailures >= MaxFileWriteFailures)
                    {
                        bool firstLatch = !_fileWriteDisabled;
                        _fileWriteDisabled = true;
                        if (firstLatch)
                            notice = $"[Logger] 日志文件写入连续失败 {MaxFileWriteFailures} 次，已禁用文件日志: {_logFilePath}";
                    }
                    // 失败的重试刷新冷却计时：恢复前保持低频（每 RetryCooldownMs 一次），
                    // 避免冷却期过后每个调用都重试一次 CreateFileWriter
                    _fileWriteDisabledAt = Environment.TickCount64;
                    return;
                }
                // 写入成功才清零连续失败计数：不能放在 WriteLine 之前——若 WriteLine 持续
                // 抛异常（磁盘满/句柄失效），提前清零会让计数在 0↔1 间振荡、永远到不了阈值，
                // 写入期闭锁变成死代码（每行仍重试并吞一次异常）。
                _fileWriter.WriteLine(line);
                _fileWriteFailures = 0;
                if (_fileWriteDisabled)
                {
                    // 恢复成功：解除闭锁（周期重试命中可写路径）
                    _fileWriteDisabled = false;
                    notice = $"[Logger] 日志文件已恢复写入: {_logFilePath}";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // 写入期异常（磁盘满/句柄失效等）同样计数：持续故障应闭锁而非每行重试。
            // 计数与禁用都在 _fileLock 内：与 LogFilePath setter 的重置串行化。
            lock (_fileLock)
            {
                if (++_fileWriteFailures >= MaxFileWriteFailures)
                {
                    if (!_fileWriteDisabled)
                    {
                        _fileWriteDisabled = true;
                        notice = $"[Logger] 日志文件写入连续失败 {MaxFileWriteFailures} 次，已禁用文件日志: {_logFilePath}";
                    }
                    CloseFileWriterLocked();
                }
                _fileWriteDisabledAt = Environment.TickCount64;
            }
        }
        if (notice is not null)
            Console.WriteLine(notice);
    }

    /// <summary>检测日志文件是否已被外部替换/截断（轮转自愈用，仅在 <see cref="_fileLock"/> 内调用）。</summary>
    private static bool LogFileReplacedLocked()
    {
        try
        {
            var fi = new FileInfo(_logFilePath!);
            // 文件被删除，或长度小于已写字节数（mv+新建/截断替换）→ 持久句柄已指向旧文件
            return !fi.Exists || fi.Length < _fileWriter!.BaseStream.Position;
        }
        catch (Exception ex) when (ExceptionPolicies.IsBestEffortFailure(ex))
        {
            return false;
        }
    }

    /// <summary>关闭并释放日志 writer（serve 关停时调用，释放文件句柄）。</summary>
    public static void CloseFile()
    {
        lock (_fileLock) CloseFileWriterLocked();
    }

    private static void CloseFileWriterLocked()
    {
        if (_fileWriter is null) return;
        try { _fileWriter.Dispose(); } catch { }
        _fileWriter = null;
    }

    /// <summary>以指定颜色原子地输出一行（设色-写入-复位在锁内完成）。</summary>
    private static void WriteColored(string text, ConsoleColor color)
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = color;
            try
            {
                Console.Write(text);
            }
            finally
            {
                Console.ResetColor();
            }
        }
    }

    public static void Log(object text, bool enter = true)
    {
        var line = DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff]") + " - " + text;
        // 普通日志同样要加锁：serve 并发下载时多线程交错写会插行/截断，
        // 与 LogColor/LogError 的锁保持一致（这些方法都把 Console 写入锁内）。
        lock (_consoleLock)
        {
            Console.Write(line);
            if (enter) Console.WriteLine();
        }
        AppendToFile(line);
        if (enter) AppendToFile(string.Empty);
    }

    public static void LogError(object text)
    {
        var line = DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff]") + " - " + text;
        lock (_consoleLock)
        {
            Console.Write(DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff]") + " - ");
            WriteColored(text.ToString() ?? "", ConsoleColor.Red);
            Console.WriteLine();
        }
        AppendToFile(line);
    }

    public static void LogColor(object text, bool time = true)
    {
        string line;
        if (time)
        {
            line = DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff]") + " - " + text;
        }
        else
        {
            line = "                             " + text;
        }
        lock (_consoleLock)
        {
            if (time)
                Console.Write(DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff]") + " - ");
            else
                Console.Write("                            ");
            WriteColored(text.ToString() ?? "", ConsoleColor.Cyan);
            Console.WriteLine();
        }
        AppendToFile(line);
    }

    public static void LogWarn(object text, bool time = true)
    {
        string line;
        if (time)
        {
            line = DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff]") + " - " + text;
        }
        else
        {
            line = "                             " + text;
        }
        lock (_consoleLock)
        {
            if (time)
                Console.Write(DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff]") + " - ");
            else
                Console.Write("                            ");
            WriteColored(text.ToString() ?? "", ConsoleColor.DarkYellow);
            Console.WriteLine();
        }
        AppendToFile(line);
    }

    public static void LogDebug(string toFormat, params object[] args)
    {
        if (!Config.Current.DebugLog) return;
        string message = args.Length > 0 ? string.Format(toFormat, args).Trim() : toFormat;
        var line = DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff]") + " - " + message;
        lock (_consoleLock)
        {
            WriteColored(line, ConsoleColor.DarkGray);
            Console.WriteLine();
        }
        AppendToFile(line);
    }

    /// <summary>
    /// 把异常完整信息（含堆栈/InnerException）写入日志文件，不受 DebugLog 门控。
    /// 只调 AppendToFile，不打印控制台——最终用户只需要一行可读摘要（handler 会输出），
    /// 完整堆栈供事后排查。serve 模式下任务白名单外异常仅靠 Message 难以定位根因
    /// （裁剪/AOT 发布时 Message 可能是资源键），完整 ex.ToString() 落盘是唯一诊断证据。
    /// </summary>
    public static void LogStack(Exception ex)
    {
        var line = DateTime.Now.ToString("[yyyy-MM-dd HH:mm:ss.fff]") + " - " + ex;
        AppendToFile(line);
    }
}
