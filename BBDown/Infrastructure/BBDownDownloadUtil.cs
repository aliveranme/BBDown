using System;
using System.Buffers;
using BBDown.Core.Util;
using BBDown.Core;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using static BBDown.Core.Entity.Entity;

namespace BBDown;

internal static class BBDownDownloadUtil
{
    /// <summary>下载读取缓冲区大小（256KB；原为内联的 1048576 / 4）。</summary>
    private const int BlockSizeBytes = 1024 * 1024 / 4;
    /// <summary>
    /// 多线程下载的跨分片进度聚合（RF-75）：回调传入的是「该分片已下载的累计字节数」而非增量，
    /// 因此必须用「新值 - 上次值」推进总量；分片重试时该值回退到较小值，总量也必须随之回退，
    /// 否则进度条会越过 100%。抽为生产类型供下载管线与单测共用（此前测试复刻副本，
    /// 生产逻辑变异仍全绿）。
    /// </summary>
    internal sealed class ProgressAggregator
    {
        private readonly long[] _perClip;
        private long _total;

        public ProgressAggregator(int clipCount, long fileSize)
        {
            _perClip = new long[clipCount];
            FileSize = fileSize;
        }

        public long FileSize { get; }

        /// <summary>上报某分片当前的累计下载字节数，返回跨分片累计总量（永不倒退超过重试回退）。</summary>
        public long Report(int index, long cumulativeForClip)
        {
            var previous = Interlocked.Exchange(ref _perClip[index], cumulativeForClip);
            return Interlocked.Add(ref _total, cumulativeForClip - previous);
        }

        public long Total => Interlocked.Read(ref _total);
    }
    public class DownloadConfig
    {
        public bool UseAria2c { get; set; } = false;
        public string Aria2cArgs { get; set; } = string.Empty;
        public bool ForceHttp { get; set; } = false;
        public bool MultiThread { get; set; } = false;
        public DownloadTask? RelatedTask { get; set; } = null;
    }

    /// <summary>
    /// VOD 媒体流读停滞看门狗阈值：CDN 发完响应头后停发数据（黑洞/半死 TCP）时，
    /// ResponseHeadersRead 之后 HttpClient.Timeout 不约束响应体读取（见 HTTPUtil 注释），
    /// ReadAsync 会永久挂起——CLI 需手动 Ctrl+C；serve 模式下每个挂起的分片钉死
    /// 一个并发槽（默认 --max-concurrent 3 即 1/3），3 个即服务停摆且无任何日志。
    /// 每收到一块数据重置计时；超时未收到数据抛可重试 IOException 走既有退避链。
    /// 与直播侧（LiveStreamUtil.ReadStallTimeout）同一模式，VOD 侧此前没有防护。
    /// internal 可注入：测试缩短该阈值验证看门狗行为。
    /// </summary>
    internal static TimeSpan MediaReadStallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 依据最终扩展名判定轨道类型：视频轨分片用 .vclip、音频轨用 .aclip。
    /// 统一不区分大小写：CDN 回传大写的 .MP4 时，区分大小写的 EndsWith(".mp4") 会把
    /// 视频分片错判为音频（.aclip），与清理规则错位——清理时误删视频分片或把音频分片
    /// 当视频保留。此前 5 处判定中 4 处区分大小写、1 处 OrdinalIgnoreCase，行为不一致。
    /// </summary>
    private static bool IsVideoClipPath(string path)
        => Path.GetExtension(path).EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 媒体下载/探测请求的统一请求头（Referer / User-Agent / Cookie）。
    /// TV 版接口（platform=android_tv_yst、platform=android）不能带 B 站网页 Referer，
    /// 否则被 CDN 拒绝，故按 URL 判定是否加 Referer；User-Agent 与配置 Cookie 一律加上。
    /// 此前三处（分片下载 / 权威大小复核 / 探测）各写一份，逐字重复。
    /// </summary>
    private static void ApplyMediaRequestHeaders(HttpRequestMessage request, string url)
    {
        if (!url.Contains("platform=android_tv_yst") && !url.Contains("platform=android"))
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
        request.Headers.TryAddWithoutValidation("User-Agent", HTTPUtil.GetUserAgent(null));
        request.Headers.TryAddWithoutValidation("Cookie", Core.Config.Current.Cookie);
    }

    /// <summary>
    /// 单次分片/整段下载的请求参数（H3：原 10 参方法的参数对象）。
    /// 字段与原形参一一对应；取消令牌仍作为独立参数（控制流不属数据）。
    /// </summary>
    private readonly record struct RangeDownloadRequest(
        int ClipIndex,
        string Url,
        string TmpName,
        long FromPosition,
        long? ToPosition,
        Action<int, long, long> OnProgress,
        bool FailOnRangeNotSupported = false,
        string? IfRange = null,
        long ExpectedTotalSize = -1);

    private static async Task<long> RangeDownloadToTmpAsync(RangeDownloadRequest request, CancellationToken token = default)
    {
        using var fileStream = new FileStream(request.TmpName, FileMode.OpenOrCreate);
        long clipLength = request.ToPosition is > 0 ? request.ToPosition.Value - request.FromPosition + 1 : long.MaxValue;

        // 超长旧分片：上次中断可能留下超出目标分片范围的尾部（内容异常变大）。
        // 此时既有字节不可信——它可能是另一版本/另一资源在相同偏移留下的残留。
        // 此前把尾部截断后仅凭长度判完成，若远端内容已变化但长度恰好相同，
        // 会拼出"长度正确但内容损坏"的文件，且下次重试仍看到同一超长分片。
        // 正确做法是清空并完整重下本分片，不信任截断后"恰好吻合"的长度。
        if (fileStream.Length > clipLength)
        {
            fileStream.SetLength(0);
            fileStream.Seek(0, SeekOrigin.Begin);
        }
        else
        {
            fileStream.Seek(0, SeekOrigin.End);
        }

        if (request.ToPosition > 0 && fileStream.Length == clipLength)
        {
            // 已下载完成 直接汇报进度并跳过下载
            request.OnProgress(request.ClipIndex, clipLength, clipLength);
            return fileStream.Length;
        }
        var downloadedBytes = request.FromPosition + fileStream.Position;

        using var httpRequestMessage = new HttpRequestMessage();
        ApplyMediaRequestHeaders(httpRequestMessage, request.Url);
        // Range 指定续传偏移；校验器只用服务器的 ETag/Last-Modified，不能把本地
        // 临时文件的 LastWriteTimeUtc 当作 If-Range（两者没有资源版本对应关系）。
        httpRequestMessage.Headers.Range = new(downloadedBytes, request.ToPosition);
        // 续传时带 If-Range：有 ETag/Last-Modified 时让服务器校验本地前缀仍属于当前对象。
        // 若对象已变化（ETag 不符），服务器返回完整 200，下方 200 分支清空重下，
        // 不会把旧前缀与新后缀拼成损坏文件。
        if (!string.IsNullOrEmpty(request.IfRange))
            httpRequestMessage.Headers.TryAddWithoutValidation("If-Range", request.IfRange);
        httpRequestMessage.RequestUri = new(request.Url);

        using var response = (await HTTPUtil.MediaDownloadClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead, token)).EnsureSuccessStatusCode();

        if (response.StatusCode == HttpStatusCode.OK) // server doesn't response a partial content
        {
            // If-Range 不匹配时服务端会返回完整对象。分片模式不能把这个响应当作单片：
            // 其它分片可能已属于旧版本，必须由调用方清除整条轨道并重新探测。
            if (request.FailOnRangeNotSupported && !string.IsNullOrEmpty(request.IfRange))
                throw new RemoteResourceChangedException();
            if (request.FailOnRangeNotSupported && (downloadedBytes > 0 || request.ToPosition != null)) throw new NotSupportedException("Range request is not supported.");
            downloadedBytes = 0;
            // 完整重下必须清空旧内容：只 Seek(0) 而没 SetLength(0) 会留下旧文件尾部，
            // 与新的短内容拼接成损坏文件。
            fileStream.SetLength(0);
            fileStream.Seek(0, SeekOrigin.Begin);
        }
        else if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            // 严格校验 Content-Range 的起始偏移：服务器必须以我们请求的字节偏移响应。
            // 若返回的起始字节与请求不符（远端资源变化导致偏移语义错位、或 CDN 行为异常），
            // 或 206 响应缺失 Content-Range（协议异常），**当前响应体对应的区间不可信**——
            // 它可能是错误偏移的字节，也可能与本地既有前缀不连续。
            // 关键：不能清空本地文件后继续把这段错误区间的字节写到偏移 0（旧实现正是如此，
            // 把 Content-Range: bytes 50-999 的内容写到本地偏移 0，随后因长度恰好匹配而被
            // 当成完整下载）。必须丢弃本地内容并立即抛可重试的 IOException，
            // 让上层重试从正确起点（偏移 0）重新发起完整请求。
            var contentRange = response.Content.Headers.ContentRange;
            if (contentRange is not { HasRange: true } || contentRange.From != downloadedBytes)
            {
                // 直接抛错，由 using 释放连接；不读取响应体——若服务器忽略 Range
                // 返回整文件 206，读入缓冲会浪费巨量内存。
                fileStream.SetLength(0);
                fileStream.Seek(0, SeekOrigin.Begin);
                throw new IOException(
                    $"Content-Range 起始偏移({contentRange?.From?.ToString() ?? "none"})与请求偏移({downloadedBytes})不符，" +
                    "远端内容已变化或服务器行为异常，已丢弃本地内容并重新下载");
            }
            // 206 的 Content-Range 尾段（/total）揭示资源真实总长。HEAD 探测返回的 size
            // 若与真实总长不符（CDN 对 HEAD 返回缓存/占位长度、或探测后内容已变化），
            // 分片划分会按错误大小切割，静默产出截断文件——合并长度校验用的是同一个
            // HEAD 值，是自引用比较，拦不住。这里用每个分片的 206 响应交叉校验：
            // total != 探测 size 即丢弃本地内容并抛 RemoteSizeMismatchException（携带权威
            // 总长），上层据此按权威总长重新切分/重试，而不是产出"长度正确但内容截断"
            // 的成品，也不是用错误大小空转重试。
            if (request.ExpectedTotalSize > 0 && contentRange.Length is { } total && total != request.ExpectedTotalSize)
            {
                fileStream.SetLength(0);
                fileStream.Seek(0, SeekOrigin.Begin);
                throw new RemoteSizeMismatchException(request.ExpectedTotalSize, total);
            }
        }

        using var stream = await response.Content.ReadAsStreamAsync(token);
        long? declaredLength = response.Content.Headers.ContentLength;
        // 服务器声明了 Content-Length 时按声明校验完整性；未声明时读到 EOF 即为结束
        var totalBytes = downloadedBytes + (declaredLength ?? long.MaxValue - downloadedBytes);
        long writeStartPosition = fileStream.Position;

        const int blockSize = BlockSizeBytes;
        // 256KB 超过 85000 字节的大对象堆阈值，直接 new 会让每个分片、每次重试
        // 都在 LOH 上留下一块并触发 Gen2 回收（Gen2 会暂停全部线程）。
        // Rent 返回的数组可能大于请求值，因此读写都必须显式限定长度。
        var buffer = ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            // 读停滞看门狗：网络波动时连接可能既不 RST 也不 EOF，只是不再有数据到达
            //（黑洞/半死 TCP）。ResponseHeadersRead 之后 HttpClient.Timeout 不约束响应体
            // 读取，无看门狗时 ReadAsync 永久挂起。与直播侧（LiveStreamUtil.StreamToFileAsync）
            // 同一模式：每收到一段数据重置计时；超时未收到数据视为连接死亡，抛可重试的
            // IOException（上层 catch 链已含 IOException，见 IsSizeArtifactFailure 同源处理）
            // 走既有退避重试。真正的用户取消（token 已取消）原样向上传播。
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            stallCts.CancelAfter(MediaReadStallTimeout);
            while (downloadedBytes < totalBytes)
            {
                int received;
                try
                {
                    received = await stream.ReadAsync(buffer.AsMemory(0, blockSize), stallCts.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new IOException($"下载停滞：{MediaReadStallTimeout.TotalSeconds:0} 秒内未收到数据，已触发重试");
                }
                if (received == 0)
                {
                    // 提前 EOF：仅当服务器声明了 Content-Length 且未读够时是截断——
                    // 说明连接中断，必须抛错触发重试，不能把截断当成功。
                    // 未声明 Content-Length 时读到 EOF 是正常结束，静默跳出。
                    if (declaredLength is not null)
                        throw new IOException("下载中断：响应提前结束，已触发重试");
                    break;
                }
                stallCts.CancelAfter(MediaReadStallTimeout); // 收到数据：重置停滞计时
                // 依赖 FileStream 自身缓冲，不逐块 FlushAsync：每 256KB 一次刷盘会
                // 把异步写放大成同步 syscall，大文件下载产生海量无必要的刷新调用。
                // 分片结束时统一 flush 一次，保证数据落盘后调用方（合并）可读到完整内容。
                await fileStream.WriteAsync(buffer.AsMemory(0, received), token);
                downloadedBytes += received;
                request.OnProgress(request.ClipIndex, downloadedBytes - request.FromPosition, totalBytes);
            }
            // 分片写完后落盘：合并/删除等后续操作依赖磁盘上已有完整数据
            await fileStream.FlushAsync(token);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (declaredLength != null)
        {
            long written = fileStream.Position - writeStartPosition;
            if (written != declaredLength.Value)
                throw new InvalidOperationException("写入大小与HTTP响应声明不符，触发重试");
        }
        return fileStream.Length;
    }

    private sealed class PathLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Waiters;
    }

    private static readonly Dictionary<string, PathLock> _downloadLocks = new();
    private static readonly object _lockFactory = new();

    /// <summary>当前登记的路径锁数量。空闲时应为 0，用于验证锁不会累积。</summary>
    internal static int ActivePathLockCount
    {
        get { lock (_lockFactory) { return _downloadLocks.Count; } }
    }

    /// <summary>供测试使用：以路径锁执行一段逻辑。</summary>
    internal static Task RunWithPathLockAsync(string path, Func<Task> action, CancellationToken token = default)
        => WithPathLockAsync(path, action, token);

    /// <summary>
    /// 带返回值的路径锁版本：以目标路径的独占锁执行 <paramref name="action"/> 并返回其结果。
    /// 用于"判定目标文件是否已存在 → 生产（如混流写最终路径）→ 清理"这类必须原子化的临界区：
    /// 若不持有锁，serve 下两个同标题任务可能同时通过判定、同时写同一个最终路径，后写者覆盖先写者。
    /// </summary>
    internal static Task<T> RunWithPathLockAsync<T>(string path, Func<Task<T>> action, CancellationToken token = default)
        => WithPathLockAsync(path, action, token);

    /// <summary>
    /// 规范化为锁键：Path.GetFullPath 展开相对路径/“..”段，Windows 下统一小写
    /// （NTFS 不区分大小写，大小写不同的同一路径应共享一把锁）。
    /// 规范化必须同时用于 Acquire 与 Unregister，否则字典键不匹配导致锁泄漏。
    /// </summary>
    private static string NormalizeLockKey(string path)
    {
        // 空/空白路径在 GetFullPath 下会抛异常；用固定占位键避免锁机制自身抛错
        if (string.IsNullOrWhiteSpace(path)) return "<empty>";
        string full = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    /// <summary>
    /// 取得某个目标路径的独占锁并登记一个使用者。
    /// 必须与 <see cref="UnregisterDownloadLock"/> 成对使用，否则字典会持续膨胀 ——
    /// serve 模式是长驻进程，每个下载过的路径都留下一个 SemaphoreSlim 就是内存泄漏。
    /// </summary>
    private static PathLock AcquireDownloadLock(string path)
    {
        var key = NormalizeLockKey(path);
        lock (_lockFactory)
        {
            if (!_downloadLocks.TryGetValue(key, out var pathLock))
            {
                pathLock = new PathLock();
                _downloadLocks[key] = pathLock;
            }
            pathLock.Waiters++;
            return pathLock;
        }
    }

    private static void UnregisterDownloadLock(string path, PathLock pathLock)
    {
        var key = NormalizeLockKey(path);
        lock (_lockFactory)
        {
            // 仅在没有其他使用者时移除，避免正在等待的线程拿到已被弃用的信号量
            if (--pathLock.Waiters == 0 && _downloadLocks.TryGetValue(key, out var current) && ReferenceEquals(current, pathLock))
            {
                _downloadLocks.Remove(key);
                pathLock.Semaphore.Dispose();
            }
        }
    }

    /// <summary>
    /// 以目标路径的独占锁执行 <paramref name="action"/>。
    /// 单独记录 acquired 状态：等待被取消时既不能漏减引用计数，
    /// 也不能对一个从未获取到的信号量调用 Release。
    /// </summary>
    private static async Task WithPathLockAsync(string path, Func<Task> action, CancellationToken token)
    {
        var pathLock = AcquireDownloadLock(path);
        var acquired = false;
        try
        {
            await pathLock.Semaphore.WaitAsync(token);
            acquired = true;
            await action();
        }
        finally
        {
            if (acquired) pathLock.Semaphore.Release();
            UnregisterDownloadLock(path, pathLock);
        }
    }

    private static async Task<T> WithPathLockAsync<T>(string path, Func<Task<T>> action, CancellationToken token)
    {
        var pathLock = AcquireDownloadLock(path);
        var acquired = false;
        try
        {
            await pathLock.Semaphore.WaitAsync(token);
            acquired = true;
            return await action();
        }
        finally
        {
            if (acquired) pathLock.Semaphore.Release();
            UnregisterDownloadLock(path, pathLock);
        }
    }

    public static async Task DownloadFileAsync(string url, string path, DownloadConfig config, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(url)) return;
        await WithPathLockAsync(path, () => DownloadFileCoreAsync(url, path, config, token: token), token);
    }

    /// <summary>
    /// 单线程下载的实际逻辑。不获取 <see cref="AcquireDownloadLock"/>，
    /// 以便多线程模式降级时可以在已持锁的状态下复用（SemaphoreSlim 不可重入）。
    /// </summary>
    private static async Task DownloadFileCoreAsync(string url, string path, DownloadConfig config, (long size, HttpResponseHeaders? headers, HttpContentHeaders? contentHeaders)? probe = null, CancellationToken token = default)
    {
        if (config.ForceHttp) url = ReplaceUrl(url);
        // 下载 URL 含签名/时效参数（sign/deadline）：日志脱敏后才落盘，避免 PID/日志文件
        // 携带用户可用的 CDN 临时下载权（与 AppHelper 对 PlayViewReply 的脱敏承诺一致）。
        Logger.LogDebug("Start downloading: {0}", SensitiveDataMasker.MaskUrl(url));
        string desDir = Path.GetDirectoryName(path)!;
        if (!string.IsNullOrEmpty(desDir) && !Directory.Exists(desDir)) Directory.CreateDirectory(desDir);
        // 探测合并：多线程降级复用 MultiThreadDownloadAndMergeAsync 已探测的结果（probe 非空），
        // 直接单线程调用（DownloadFileAsync）时在此探测一次。每个文件整个下载链路只探测一次，
        // 不再"aria2 分支探测一次 + 下方再探测一次"。
        var (fileSize, probeHeaders, probeContentHeaders) = probe ?? await GetFileSizeAndHeadersAsync(url, token);
        if (config.UseAria2c)
        {
            // --continue=true 断点续传前先校验既有 partial/.aria2 的资源身份：残留的旧资源
            //（如中断的 1080P 下载 + 控制文件）会被 aria2c 无身份校验地续传，新资源字节
            // 追加到旧前缀上可能拼出损坏文件。用与非 aria2 路径相同的 ResumeManifest
            // 身份校验，身份不可信则删除 partial + 控制文件完整重下。已完整下载则跳过。
            if (await PrepareAria2cTargetAsync(url, path, fileSize, probeHeaders, probeContentHeaders, token))
            {
                Logger.LogDebug("文件已下载过, 跳过下载");
                CleanStaleWorkArtifactsFor(path);
                return;
            }
            await BBDownAria2c.DownloadFileByAria2cAsync(url, path, config.Aria2cArgs, token);
            if (File.Exists(path + ".aria2") || !File.Exists(path))
                throw new InvalidOperationException("aria2下载可能存在错误");
            // 不清除身份清单：它作为"完成证书"保留，下次重跑时 PrepareAria2cTarget 可经
            // CanResumeFrom 确认身份后跳过，而不是把完整文件误判为"身份不可信"删除重下。
            // 其它下载模式遗留的工作残留（.tmp/分片/.merging）已无消费者，顺手回收。
            CleanStaleWorkArtifactsFor(path);
            return;
        }
        int retry = 0;
        // 预检决策（是否已完整 / 可否续传 / 陈旧文件处置）收口在 PrepareSingleThreadTargetAsync；
        // 目标长度可能被权威总长修正、续传点可能携带 If-Range，均以返回的决策为准。
        var precheck = await PrepareSingleThreadTargetAsync(
            url, path, fileSize, probeHeaders?.ETag?.ToString(), probeContentHeaders?.LastModified?.ToString("R"), token);
        if (precheck.AlreadyComplete)
        {
            // 成品已就位：回收其它下载模式遗留的工作残留（.tmp/.aria2/分片/.merging），
            // 否则 GB 级临时文件与工作目录会永久残留（见 CleanStaleWorkArtifactsFor）。
            CleanStaleWorkArtifactsFor(path);
            return;
        }
        string tmpName = precheck.TmpName;
        fileSize = precheck.FileSize;
        // 首次请求也带探测得到的校验器：若连接中断，重试沿用同一版本，
        // 不能仅在跨进程续传时才设置 If-Range。
        string? resumeIfRange = precheck.ResumeIfRange ?? SelectIfRange(probeHeaders?.ETag?.ToString(), probeContentHeaders?.LastModified?.ToString("R"));
        int maxRetry = Config.Current.MaxRetryCount;
        // 下载首字节前就写入续传清单：真正中断（进程被杀/Ctrl+C）留下的 .tmp 必须带清单，
        // 下次运行才能确认其资源身份而续传。若等下载完成才写，中断的 .tmp 无清单，
        // 下次一定被删除——跨进程续传实际不可用。
        await WriteResumeManifestAsync(tmpName, url, fileSize, probeHeaders, probeContentHeaders, token);
        // 尺寸修正不消耗 --retry-count（见下方 RemoteSizeMismatchException catch）：
        // 否则 --retry-count 1 时修正后没有剩余下载机会。
        bool sizeRepaired = false;
        while (retry < maxRetry)
        {
            try
            {
                using var progress = new ProgressBar(config.RelatedTask);
                long written = await RangeDownloadToTmpAsync(new RangeDownloadRequest(
                    0, url, tmpName, 0, null,
                    (_, downloaded, total) => progress.Report((double)downloaded / total, downloaded),
                    IfRange: resumeIfRange, ExpectedTotalSize: fileSize), token);
                // 移动最终路径前验证总长度：探测到的远端大小 > 0 时，临时文件必须与之一致。
                // 若响应 Content-Range 错位被上方拒绝重试后仍拿到错误内容，长度校验能拦住
                // 假成功——此前直接 File.Move 把未校验内容当作成品。
                if (fileSize > 0 && written != fileSize)
                    throw new IOException($"下载产物长度({written})与服务器声明({fileSize})不符，触发重试");
                File.Move(tmpName, path, true);
                DeleteResumeManifest(tmpName);
                break;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
            {
                throw; // non-retryable: bad input, unsupported feature, logic error
            }
            catch (RemoteSizeMismatchException ex)
            {
                // HEAD 探测大小与 206 Content-Range 声明总长不符（CDN 对 HEAD 返回缓存/占位长度）：
                // 用权威总长修正后继续下载，不再用错误大小空转（每次重试必然再次失败）。
                // 修正不计入 --retry-count 消耗；同一下载最多修正一次，仍不符视为真实异常抛出。
                if (sizeRepaired) throw;
                sizeRepaired = true;
                fileSize = ex.ActualTotal;
                await WriteResumeManifestAsync(tmpName, url, fileSize, probeHeaders, probeContentHeaders, token);
                Logger.LogDebug("Content-Range 总长与探测大小不符，已按权威总长({0})继续下载", fileSize);
            }
            catch (Exception ex) when (ExceptionPolicies.IsTransportFailure(ex))
            {
                // 退避基数用 retry + 1：否则首次重试的等待时间为 0，
                // 面对限流的服务器会立刻再打一次
                int backoffMs = (retry + 1) * Config.Current.RetryDelayMs;
                Logger.LogDebug("下载失败(第{0}次重试, {1}ms后): {2}", retry + 1, backoffMs, ex.Message);
                await Task.Delay(backoffMs, token);
                if (++retry == maxRetry) throw;
            }
        }
    }

    /// <summary>单线程下载的「目标/临时文件预检」决策结果（纯本地文件状态 + 资源身份校验）。</summary>
    /// <param name="TmpName">本次下载使用的临时文件路径（<c>path + ".tmp"</c>）。</param>
    /// <param name="FileSize">预检后应使用的远端总长（权威总长复核可能修正它；0 表示未知）。</param>
    /// <param name="ResumeIfRange">可续传时携带的 If-Range 值（ETag/Last-Modified）；null 表示从头下载。</param>
    /// <param name="AlreadyComplete">true 表示产物已就位，调用方应直接返回。</param>
    private readonly record struct SingleThreadPrecheck(string TmpName, long FileSize, string? ResumeIfRange, bool AlreadyComplete);


    /// <summary>
    /// 目标文件已存在且长度与探测大小一致时，用 GET 权威总长复核"探测大小是否可信"
    ///（HEAD 可能被 CDN 返回缓存/占位长度——与 <see cref="RangeDownloadToTmpAsync"/> 的
    /// Content-Range 交叉校验同源的问题），并就地处置不可信的既有文件。
    /// 返回 <c>Trustworthy = true</c> 表示可以跳过下载：复核失败/未知总长一律退回纯长度跳过，
    /// 避免误删有效文件。<c>false</c> 表示探测大小不可信，返回的 <c>KnownSize</c> 为权威总长，
    /// 既有文件与其续传清单已被删除，调用方必须改用权威总长继续下载——占位 HEAD 会一路带进
    /// expectedTotalSize，若服务器又无视 Range（GET 回 200 全量，无 Content-Range 可交叉校验），
    /// 长度校验会空转重试直至永久失败。
    /// </summary>
    private static async Task<(bool Trustworthy, long KnownSize)> VerifyExistingTargetAsync(
        string url, string path, long fileSize, CancellationToken token)
    {
        long? authoritativeSize = await GetAuthoritativeSizeAsync(url, token);
        if (authoritativeSize is not { } known || known <= 0 || known == fileSize)
            return (true, fileSize);
        Logger.LogDebug("探测大小({0})与权威大小({1})不符，既有文件不可信，删除后完整重下", fileSize, known);
        File.Delete(path);
        DeleteResumeManifest(path);
        return (false, known);
    }

    /// <summary>
    /// 单线程下载前的目标/临时文件预检：判定「产物已就位可直接跳过 / 从现有临时文件续传 /
    /// 完整重下」，并删除身份不可信或长度错位的陈旧文件（资源身份经 <see cref="CanResumeFromAsync"/> 校验）。
    /// 探测大小可能被 CDN 的 HEAD 占位长度欺骗，此时按权威总长修正——修正值随决策返回，
    /// 调用方必须用返回的 <see cref="SingleThreadPrecheck.FileSize"/> 而非原探测值继续下载。
    /// </summary>
    private static async Task<SingleThreadPrecheck> PrepareSingleThreadTargetAsync(string url, string path,
        long fileSize, string? etag, string? lastModified, CancellationToken token)
    {
        // 临时文件保留目标扩展名（path + ".tmp"）：视频 xxx.mp4 与音频 xxx.m4a 路径只差
        // 扩展名，此前用 GetFileNameWithoutExtension 会让两者共用同一 .tmp——视频中断
        // 留下的 1MB 视频数据会被下次音频下载当成音频前缀续传（长度正确但内容损坏）。
        // 保留扩展名即隔离音视频的临时文件，且与多线程分片（.vclip/.aclip）的隔离一致。
        string tmpName = path + ".tmp";
        // 必须要求 fileSize > 0：服务器未返回 Content-Length 时 fileSize 为 0，
        // 此时若 path 恰好是上次失败留下的空文件，会被误判成"已下载完成"
        if (fileSize > 0 && File.Exists(path) && new FileInfo(path).Length == fileSize)
        {
            var (trustworthy, known) = await VerifyExistingTargetAsync(url, path, fileSize, token);
            if (trustworthy)
            {
                Logger.LogDebug("文件已下载过, 跳过下载");
                return new SingleThreadPrecheck(tmpName, fileSize, null, true);
            }
            fileSize = known;
        }
        if (fileSize > 0 && File.Exists(tmpName) && new FileInfo(tmpName).Length == fileSize)
        {
            // 长度相等不等于内容可信：同一输出路径可能被 1080P→720P / AVC→HEVC 的
            // 另一个资源复用（长度恰好相同）。只有续传清单确认资源身份一致时才直接采用，
            // 否则删除完整重下——杜绝"长度正确但内容损坏"的假成功。
            var (canResume, resumeReason) = await CanResumeFromAsync(
                tmpName, url, fileSize, etag, lastModified, token);
            if (canResume)
            {
                Logger.LogDebug("断点续传: 检测到已完整下载的临时文件且资源身份一致, 直接移动");
                File.Move(tmpName, path, true);
                DeleteResumeManifest(tmpName);
                return new SingleThreadPrecheck(tmpName, fileSize, null, true);
            }
            Logger.LogDebug("断点续传: 临时文件长度与远端一致但资源身份不可信（{0}），删除后完整重下", resumeReason ?? "未知原因");
            File.Delete(tmpName);
            DeleteResumeManifest(tmpName);
        }
        // 部分下载的临时文件直接续传：RangeDownloadToTmpAsync 会从现有长度处
        // 发起 Range: bytes=N- 续传。此前这里删除不完整临时文件导致大文件/弱网
        // 每次中断都从头重下，实际无法断点续传。
        string? resumeIfRange = null;
        if (File.Exists(tmpName) && new FileInfo(tmpName).Length > 0)
        {
            // 续传同样要求资源身份一致：清单缺失/不符时删除 .tmp 完整重下，
            // 否则旧前缀可能与新响应拼接（长度仍正确但内容损坏）。
            var (canResume, reason) = await CanResumeFromAsync(
                tmpName, url, fileSize, etag, lastModified, token);
            if (canResume)
            {
                Logger.LogDebug("断点续传: 从现有临时文件 {0} 字节处继续（资源身份一致）", new FileInfo(tmpName).Length);
                // 续传时带 If-Range（ETag/Last-Modified）：让服务器校验本地前缀仍属于当前对象
                resumeIfRange = await ReadManifestIfRangeAsync(tmpName, token);
            }
            else
            {
                Logger.LogDebug("断点续传: 现有临时文件资源身份不可信（{0}），删除后完整重下", reason ?? "未知原因");
                File.Delete(tmpName);
                DeleteResumeManifest(tmpName);
            }
        }
        // 临时文件比远端更大：既非"完整匹配"也非"可续传的前缀"（续传只会追加，
        // 不可能让已有内容变短）。它只可能是远端资源变化（同长度语义错位）或上次
        // 中断写入的越界尾部。若继续从现有长度处发 Range: bytes=N-，服务器要么 416、
        // 要么返回偏移错位的片段，拼出损坏文件。这里删除让单线程下载完整重下。
        if (fileSize > 0 && File.Exists(tmpName) && new FileInfo(tmpName).Length > fileSize)
        {
            Logger.LogDebug("断点续传: 临时文件({0} 字节)大于远端文件({1} 字节)，内容不可信，删除后完整重下",
                new FileInfo(tmpName).Length, fileSize);
            File.Delete(tmpName);
            DeleteResumeManifest(tmpName);
        }
        return new SingleThreadPrecheck(tmpName, fileSize, resumeIfRange, false);
    }
    /// <summary>
    /// aria2c 断点续传目标校验（纯文件决策，探测结果由调用方注入）。返回 true 表示
    /// 文件已完整下载，调用方应跳过 aria2c。对存在的 partial/.aria2 控制文件先做
    /// ResumeManifest 身份校验——身份不可信（跨资源/缺清单/等长异内容）时删除 partial
    /// + 控制文件完整重下，杜绝跨资源续传把新资源字节追加到旧前缀上拼出损坏文件；
    /// 身份可信、长度与远端一致且没有控制文件时才可跳过。校验后写入本次资源身份清单，
    /// 供同一资源的中断续传（页面级重试）复用。
    /// internal 供测试注入探测结果验证决策。
    /// </summary>
    internal static async Task<bool> PrepareAria2cTargetAsync(string url, string path, long fileSize,
        HttpResponseHeaders? headers, HttpContentHeaders? contentHeaders, CancellationToken token = default)
    {
        string controlFile = path + ".aria2";
        bool partialExists = File.Exists(path) || File.Exists(controlFile);
        if (partialExists)
        {
            long partialLength = File.Exists(path) ? new FileInfo(path).Length : 0;
            // 既有 partial/.aria2：先做身份校验。身份可信（同资源中断，清单存在且匹配）
            // 才允许保留续传或判定完整跳过；身份不可信（跨资源/缺清单/等长异内容）→
            // 删除完整重下。等长也必须过身份校验：长度恰好相同的跨资源残留若被当作
            // "已完整"跳过，残缺文件会直接作为成品进入混流。
            var (canResume, reason) = await CanResumeFromAsync(
                path, url, fileSize, headers?.ETag?.ToString(), contentHeaders?.LastModified?.ToString("R"), token);
            if (!canResume)
            {
                Logger.LogDebug("aria2c: 既有文件资源身份不可信（{0}），删除后完整重下", reason ?? "未知原因");
                // 用位与 &（非短路）：两个文件都必须尝试删除，&& 会因第一个成功而漏删第二个
                bool purged = TryDeleteStale(path) & TryDeleteStale(controlFile);
                DeleteResumeManifest(path);
                if (!purged)
                    throw new InvalidOperationException(
                        $"aria2c 无法清理身份不可信的残留文件: {path}，已中止下载（请手动删除后重试）");
            }
            else if (fileSize > 0 && partialLength == fileSize && !File.Exists(controlFile))
            {
                // aria2c 默认会预分配到远端总长；长度相等不能证明所有块已下载。
                // 控制文件仍在时交给 aria2c 恢复，不能删除块进度后把残缺内容当成成品。
                return true;
            }
            else if (fileSize > 0 && partialLength > fileSize)
            {
                // 身份可信但超长（长度超过远端总长）：内容不可信（越界尾部/资源变化），
                // 删除完整重下。否则 aria2c --continue 从超出 EOF 的偏移续传可能 416 死循环。
                Logger.LogDebug("aria2c: 既有文件({0}字节)大于远端({1}字节)，内容不可信，删除后完整重下", partialLength, fileSize);
                // 用位与 &（非短路）：两个文件都必须尝试删除，&& 会因第一个成功而漏删第二个
                bool purged = TryDeleteStale(path) & TryDeleteStale(controlFile);
                DeleteResumeManifest(path);
                if (!purged)
                    throw new InvalidOperationException(
                        $"aria2c 无法清理超长残留文件: {path}，已中止下载（请手动删除后重试）");
            }
            // 身份可信且未确认完成：保留数据及控制文件，交给 aria2c --continue=true 恢复。
        }
        await WriteResumeManifestAsync(path, url, fileSize, headers, contentHeaders, token);
        return false;
    }

    /// <summary>尽力删除残留文件；返回是否成功（占用/权限问题只降级为日志并返回 false）。</summary>
    private static bool TryDeleteStale(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ExceptionPolicies.IsBestEffortFailure(ex))
        {
            Logger.LogDebug("清理残留文件失败: {0}: {1}", path, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 多线程下载。返回本次实际产生的分片文件列表（按 index 升序，与
    /// <see cref="GetAllClips"/> 的切片一一对应）。调用方应只合并/清理该列表：
    /// 扫描目录里全部 *.?clip 会把上一次取消、其它分P、其它轨道留下的分片混进来，
    /// 造成拼串味文件与误删。
    /// </summary>
    public static async Task<List<string>> MultiThreadDownloadFileAsync(string url, string path, DownloadConfig config, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(url)) return [];
        List<string>? clips = null;
        await WithPathLockAsync(path, async () =>
        {
            // 与 Core 一致：--force-http 先替换 URL 再探测，探测与下载用同一资源地址
            //（否则探测 https、下载 http，两端的 Content-Length/校验器可能不一致）
            if (config.ForceHttp) url = ReplaceUrl(url);
            // 探测合并：链路顶层探测一次，结果下传 Core，不再在 Core 内重复探测
            var probe = await GetFileSizeAndHeadersAsync(url, token);
            var (coreClips, _) = await MultiThreadDownloadCoreAsync(url, path, config, probe, token);
            clips = coreClips;
        }, token);
        return clips ?? [];
    }

    /// <summary>
    /// 多线程下载 + 合并 + 清理，在目标路径的独占锁内完成整个分片生命周期。
    /// 调用方（Display）不再在锁外合并/删除分片：相同目标路径的第二个任务会等第一个
    /// 任务"下载→合并→清理"全部结束后再进入，要么看到文件已完整而跳过，要么正常下载，
    /// 不会复用/误删第一个任务的分片。
    /// 合并结果先写到临时文件再原子替换到目标路径：中途失败不会留下半截成品。
    /// </summary>
    public static async Task MultiThreadDownloadAndMergeAsync(string url, string path, DownloadConfig config, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(url)) return;
        await WithPathLockAsync(path, async () =>
        {
            // 与 Core 一致：--force-http 先替换 URL 再探测，探测与下载用同一资源地址
            //（否则探测 https、下载 http，两端的 Content-Length/校验器可能不一致）
            if (config.ForceHttp) url = ReplaceUrl(url);
            // 探测合并：整个"下载→合并"链路只在顶层探测一次（HEAD 优先，连接可直接回池），
            // 结果沿 Core → 单线程降级 → aria2 判断下传。此前每层重复 GetFileSizeAndHeadersAsync
            // 让每个文件多出 2-3 次往返，且 GET 探测不消费响应体使连接无法复用。
            var (fileSize, probeHeaders, probeContentHeaders) = await GetFileSizeAndHeadersAsync(url, token);
            // 已完整下载过：直接跳过（不再产生分片，也不做任何合并/清理）。
            // aria2 路径不走此纯长度跳过：等长跨资源残留会被误当"完整"跳过，身份校验
            // 交给 MultiThreadDownloadCoreAsync 内的 PrepareAria2cTarget。
            // 非 aria2 路径同样不能只凭 HEAD 长度跳过——HEAD 可能被 CDN 返回缓存/占位长度
            //（与 Content-Range 交叉校验同源的问题），等长陈旧文件会被误报为"已完整下载"。
            // 删除只发生在权威总长**已知**且与探测不符时；复核失败/未知总长退回纯长度跳过。
            if (!config.UseAria2c && fileSize > 0 && File.Exists(path) && new FileInfo(path).Length == fileSize)
            {
                // 复核"探测大小是否可信"与两个 Core 路径共用同一实现（VerifyExistingTargetAsync）
                var (trustworthy, known) = await VerifyExistingTargetAsync(url, path, fileSize, token);
                if (trustworthy)
                {
                    Logger.LogDebug("文件已下载过, 跳过下载");
                    DeleteResumeManifest(path);
                    // 成品已完整时，历史中断遗留的分片、合并临时文件（GB 级）以及其它
                    // 下载模式遗留的 .tmp/.aria2 不再有任何消费者——不显式清理就永久
                    // 泄漏磁盘，并让工作目录因非空永不回收（见 CleanStaleWorkArtifactsFor）。
                    CleanStaleWorkArtifactsFor(path);
                    return;
                }
                // 用权威总长下传 Core：占位 HEAD 若一路带进 expectedTotalSize，对无视 Range
                //（GET 回 200 全量）的服务器会以 NotSupportedException 失败，而非按正确大小下载。
                fileSize = known;
            }
            // 分片下载内部可能按 206 Content-Range 的权威总长修复 HEAD 探测错误的大小，
            // 返回实际使用的总长，合并长度校验用它对账（而不是外层可能错误的探测值）。
            var (clips, actualFileSize) = await MultiThreadDownloadCoreAsync(url, path, config, (fileSize, probeHeaders, probeContentHeaders), token);
            if (clips.Count == 0) return; // 单线程降级或 aria2 路径：成品已直接写到目标路径
            // 在锁内合并：合并到临时文件后原子替换，避免锁内写目标路径时被读取方读到半截
            string tmpMerged = path + ".merging";
            await BBDownUtil.CombineMultipleFilesIntoSingleFileAsync(clips.ToArray(), tmpMerged, token);
            // 完整性闭环：合并产物必须与服务器声明的总长度一致，否则删除半截成品并抛错，
            // 触发上层重试。合并时若任一来源分片不完整/缺失，产物长度会小于预期。
            if (actualFileSize > 0)
            {
                long mergedLength = File.Exists(tmpMerged) ? new FileInfo(tmpMerged).Length : 0;
                if (mergedLength != actualFileSize)
                {
                    try { File.Delete(tmpMerged); } catch (IOException) { }
                    throw new InvalidOperationException(
                        $"分片合并产物长度 ({mergedLength} 字节) 与服务器声明总长 ({actualFileSize} 字节) 不符，已触发重试");
                }
            }
            File.Move(tmpMerged, path, true);
            // 清理分片、轨道清单（清单随分片一起移除，下次干净开始）与其它下载模式
            // 遗留的工作残留（.tmp/.aria2/.merging）：无消费者，留着会让工作目录永不回收。
            CleanStaleWorkArtifactsFor(path);
        }, token);
    }

    /// <summary>本次多线程下载实际产生的分片文件列表（无分片时返回空列表）及实际使用的文件总长。
    /// 总长可能在下载中经 206 Content-Range 交叉校验修正（HEAD 探测被 CDN 返回缓存/占位长度）。</summary>
    private static async Task<(List<string> clips, long fileSize)> MultiThreadDownloadCoreAsync(string url, string path, DownloadConfig config,
        (long size, HttpResponseHeaders? headers, HttpContentHeaders? contentHeaders) probe, CancellationToken token)
    {
        if (config.ForceHttp) url = ReplaceUrl(url);
        // 同上：多线程路径同样对带签名 URL 脱敏再进日志
        Logger.LogDebug("Start downloading: {0}", SensitiveDataMasker.MaskUrl(url));
        var (fileSize, probeHeaders, probeContentHeaders) = probe;
        if (config.UseAria2c)
        {
            // 与单线程 aria2 分支一致：先校验续传目标身份（防跨资源续传拼损坏文件），
            // 已完整则跳过（MultiThreadDownloadAndMergeAsync 已做过一次长度跳过，此处兜底）
            if (await PrepareAria2cTargetAsync(url, path, fileSize, probeHeaders, probeContentHeaders, token))
            {
                CleanStaleWorkArtifactsFor(path);
                return ([], fileSize);
            }
            await BBDownAria2c.DownloadFileByAria2cAsync(url, path, config.Aria2cArgs, token);
            if (File.Exists(path + ".aria2") || !File.Exists(path))
                throw new InvalidOperationException("aria2下载可能存在错误");
            // 同单线程：保留身份清单作为完成证书，供重跑跳过完整文件；其它下载模式
            // 遗留的工作残留（.tmp/分片/.merging）已无消费者，顺手回收。
            CleanStaleWorkArtifactsFor(path);
            return ([], fileSize);
        }
        Logger.LogDebug("文件大小：{0} bytes", fileSize);
        // 分片必须依赖已知的文件大小：拿不到 Content-Length 时 GetAllClips 会返回空列表，
        // 于是既不下载也不报错，最终在混流阶段才以"找不到文件"的形式暴露出来。
        // 单线程读到 EOF 为止，不依赖文件大小，因此降级而非失败。
        if (fileSize <= 0)
        {
            Logger.LogWarn("服务器未返回文件大小, 已降级为单线程下载");
            // 复用本链路已探测的结果：不重新探测（探测合并）
            await DownloadFileCoreAsync(url, path, config, probe, token);
            return ([], fileSize);
        }
        //已下载过, 跳过下载
        if (File.Exists(path) && new FileInfo(path).Length == fileSize)
        {
            // 与单线程路径共用同一"探测大小权威性"复核；此处额外注意 fileSize 不得被置 0，
            // 否则下方切分崩溃（复核失败/未知总长一律退回纯长度跳过）。
            var (trustworthy, known) = await VerifyExistingTargetAsync(url, path, fileSize, token);
            if (trustworthy)
            {
                Logger.LogDebug("文件已下载过, 跳过下载");
                // 目标文件已完整：清理上一次中断遗留的该路径分片（否则调用方（Display）
                // 在下载返回后仍会无条件重合并目录里的 .vclip，用残缺分片截断覆盖这份完整
                // 成品），以及其它下载模式遗留的 .tmp/.aria2/.merging 等工作残留。
                CleanStaleWorkArtifactsFor(path);
                return ([], fileSize);
            }
            fileSize = known;
        }
        // 探测大小可能被 CDN 的 HEAD 缓存/占位长度欺骗（见 MismatchedHeadSizeServer）：
        // 分片切分按错误大小进行，每个分片的 206 Content-Range 交叉校验会抛
        // RemoteSizeMismatchException。这里按声明的权威总长最多重新切分一次，
        // 而不是用错误大小空转重试（每次必然再次失败）。
        long mismatchTotal = 0;
        string? ifRange = SelectIfRange(probeHeaders?.ETag?.ToString(), probeContentHeaders?.LastModified?.ToString("R"));
        List<Clip>? allClips = null;
        for (int sizeAttempt = 0; ; sizeAttempt++)
        {
            allClips = GetAllClips(url, fileSize);
            int total = allClips.Count;
            Logger.LogDebug("分段数量：{0}", total);
            // 轨道级资源身份清单：多线程分片（.vclip/.aclip）由同一 URL 切出，轨道清单记录
            // 整条轨道的资源身份。身份不符（1080P→720P / AVC→HEVC）时一次性删除该轨道
            // 全部分片，而不是逐片按长度判断——长度相同的前缀会被复用拼入新资源，内容混合。
            // 该轨道的全部预期分片路径（与 allClips 一一对应）：用于检测"是否存在任意分片"，
            // 不要求首分片存在——中断可能只留下后续分片，只要任一存在就必须做身份校验。
            List<string> expectedClips = allClips
                .Select(c => ClipPathFor(path, c.index))
                .ToList();
            // 后续分片的绝对偏移由分片大小决定；只比 URL/总长会在调整分片大小后
            // 把旧分片前缀拼到新偏移。记录实际首分片大小（单分片时即总长）。
            long segmentSizeBytes = allClips[0].to - allClips[0].from + 1;
            string manifestClip = expectedClips[0]; // 轨道清单挂在首分片名下（00000_<stem>.vclip.manifest.json）
            // 存在任意旧分片 → 校验轨道 manifest；缺失/损坏/不匹配 → 清理全部分片和旧 manifest
            bool anyExistingSegment = expectedClips.Any(File.Exists);
            if (anyExistingSegment)
            {
                var (canResumeTrack, trackReason) = await CanResumeFromAsync(
                    manifestClip, url, fileSize, probeHeaders?.ETag?.ToString(), probeContentHeaders?.LastModified?.ToString("R"), token, segmentSizeBytes);
                if (!canResumeTrack)
                {
                    Logger.LogDebug("多线程: 轨道分片资源身份不可信（{0}），删除全部分片后完整重下", trackReason ?? "未知原因");
                    CleanStaleClipsFor(path);
                    DeleteResumeManifest(manifestClip);
                }
            }
            // 下载分片前写入轨道清单（真正中断也会留下清单，下次可确认身份续传）
            await WriteResumeManifestAsync(manifestClip, url, fileSize, probeHeaders, probeContentHeaders, token, segmentSizeBytes);
            // 分片进度按下标存放并维护一个原子累计值。
            // 此前每次回调都要对 ConcurrentDictionary.Values 求两次和，
            // 而 Values 每次访问都会复制出一份快照 —— 回调频率是每分片每 256KB 一次，
            // 10GB 的下载会触发约 4 万次 O(分片数) 的遍历。
            var progressAggregator = new ProgressAggregator(total, fileSize);

            using var progress = new ProgressBar(config.RelatedTask);
            progress.Report(0);
            int maxRetry = Config.Current.MaxRetryCount;
            // 显式限制单文件分片并发：不设上限时 Parallel.ForEachAsync 用 CPU 核数，
            // 高核数机器 × serve 并发任务会把出站连接数放大到远超需要的量级。
            // 每文件封顶 8 路并发分片，下载带宽通常先于并发数饱和，足够。
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = Math.Min(8, Math.Max(1, Environment.ProcessorCount)),
            };
            mismatchTotal = 0;
            int resourceChanged = 0;
            Exception? clipFailure = null;
            try
            {
                await Parallel.ForEachAsync(allClips, parallelOptions, async (clip, clipToken) =>
                {
                    // 分片级重试与异常归一化收口在 DownloadClipWithRetryAsync；尺寸错位以权威总长返回，
                    // 由下方统一决定是否按权威总长重切分。
                    long? clipTotal;
                    try
                    {
                        clipTotal = await DownloadClipWithRetryAsync(
                            url, path, clip, fileSize, maxRetry, progressAggregator, progress, ifRange, clipToken);
                    }
                    catch (RemoteResourceChangedException)
                    {
                        Interlocked.Exchange(ref resourceChanged, 1);
                        throw;
                    }
                    if (clipTotal is { } actualTotal)
                        Interlocked.CompareExchange(ref mismatchTotal, actualTotal, 0);
                });
            }
            catch (Exception ex)
            {
                // 分片下载失败：可能是尺寸错位（待下方修复），也可能是真实网络/协议错误。
                // 尺寸错位优先修复；无错位则保留原异常原样重抛。
                clipFailure = ex;
            }

            if (resourceChanged != 0)
            {
                // 等所有分片任务结束后清理，避免并发写者在清理后留下旧版本分片。
                CleanStaleClipsFor(path);
                DeleteResumeManifest(manifestClip);
                throw new RemoteResourceChangedException();
            }
            if (mismatchTotal > 0)
            {
                // 尺寸错位：按权威总长重新切分下载（最多修复一次；仍不符则抛出）。
                // 越界分片会因请求超出真实资源范围而报 416/网络错（IOException/HttpRequestException），
                // 属切分错误的连带，修复后自愈，允许覆盖；但**非可重试**错误（InvalidOperationException/
                // ArgumentException）与真实取消是独立缺陷，必须原样抛出，不能被尺寸修复吞掉——
                // 否则一次性的协议错会在重切分后被误报为成功。
                if (sizeAttempt > 0)
                    throw new RemoteSizeMismatchException(fileSize, mismatchTotal);
                if (clipFailure is not null && !IsSizeArtifactFailure(clipFailure))
                    ExceptionDispatchInfo.Capture(clipFailure).Throw();
                fileSize = mismatchTotal;
                CleanStaleClipsFor(path);
                Logger.LogDebug("Content-Range 总长与探测大小不符，已按权威总长({0})重新切分下载", fileSize);
                continue;
            }
            if (clipFailure is not null)
                ExceptionDispatchInfo.Capture(clipFailure).Throw();
            break;
        }
        // 返回本次产生的精确分片列表：与 allClips 的 index 一一对应。
        // 合并/清理调用方据此操作，不扫描目录（避免混入其它任务的残留分片）。
        return (allClips!
            .Select(c => ClipPathFor(path, c.index))
            .OrderBy(p => p)
            .ToList(), fileSize);
    }

    /// <summary>
    /// 单个分片的带重试下载（重试退避、异常归一化都在这里）。返回 null 表示分片已下载完成；
    /// 返回非 null 表示远端权威总长（206 Content-Range 与探测大小不符）——所有分片共享同一个错误
    /// 探测大小，本分片就地放弃，由调用方按权威总长整条轨道重新切分下载。
    /// </summary>
    private static async Task<long?> DownloadClipWithRetryAsync(string url, string path, Clip clip, long fileSize,
        int maxRetry, ProgressAggregator progressAggregator, ProgressBar progress, string? ifRange, CancellationToken token)
    {
        int retry = 0;
        string tmp = ClipPathFor(path, clip.index);
        while (retry < maxRetry)
        {
            try
            {
                await RangeDownloadToTmpAsync(new RangeDownloadRequest(
                    clip.index, url, tmp, clip.from, clip.to == -1 ? null : clip.to,
                    (index, downloaded, _) =>
                    {
                        // 同一分片的回调只在它自己的任务里串行发生，
                        // 因此这里只需保证跨分片累加的原子性
                        var current = progressAggregator.Report(index, downloaded);
                        progress.Report(fileSize > 0 ? (double)current / fileSize : 0, current);
                    },
                    FailOnRangeNotSupported: true, IfRange: ifRange, ExpectedTotalSize: fileSize), token);
                return null;
            }
            catch (RemoteSizeMismatchException ex)
            {
                // 所有分片共享同一错误探测大小，本地重试必然再次失败：不做空转。
                // 记录权威总长后立即结束本分片，交由下方按权威总长重新切分下载。
                return ex.ActualTotal;
            }
            catch (NotSupportedException)
            {
                // 服务器不支持 Range（确定性不可重试）：与单线程路径一致直接抛出，不做无意义退避重试。
                // 规范化为 InvalidOperationException 而非原样抛 NotSupportedException（RF-14）：
                // 页面级/批级两级 catch 过滤器白名单只含 InvalidOperationException， NotSupportedException
                // 会穿透两级过滤器中止整批下载（丢 webhook/failedPages），与"单 P 失败隔离"设计矛盾。
                throw new InvalidOperationException("服务器可能并不支持多线程下载, 请使用 --multi-thread false 关闭多线程");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw; // non-retryable
            }
            catch (Exception ex) when (ExceptionPolicies.IsTransportFailure(ex))
            {
                if (token.IsCancellationRequested) throw;
                int backoffMs = (retry + 1) * Config.Current.RetryDelayMs;
                Logger.LogDebug("分段下载失败(第{0}次重试, {1}ms后): {2}", retry + 1, backoffMs, ex.Message);
                await Task.Delay(backoffMs, token);
                if (++retry == maxRetry) throw new IOException($"分段 {clip.index} 下载失败，请检查网络或关闭多线程重试", ex);
            }
        }
        // maxRetry <= 0 时上面的循环一次都不执行：与原实现的 lambda 正常结束语义一致
        return null;
    }

    /// <summary>
    /// 某条轨道第 <paramref name="index"/> 个分片的路径（<c>00000_&lt;stem&gt;.vclip/.aclip</c>）。
    /// 序号 5 位对齐（目录内按名排序即分片顺序，合并顺序也依赖它），扩展名按轨类型区分：
    /// 视频 xxx.mp4 与音频 xxx.m4a 的 stem 相同，必须靠 .vclip/.aclip 把两条轨的分片隔离，
    /// 否则清理视频残留会连带删掉音频轨的可续传分片。轨道级续传清单挂在首分片名下（index = 0）。
    /// </summary>
    private static string ClipPathFor(string path, int index)
        => Path.Combine(Path.GetDirectoryName(path)!, index.ToString("00000") + "_" + Path.GetFileNameWithoutExtension(path) + (IsVideoClipPath(path) ? ".vclip" : ".aclip"));

    /// <summary>
    /// 删除某个目标路径对应的历史分片文件（上次中断遗留）。
    /// 视频与音频的 stem 相同（xxx.mp4 / xxx.m4a），必须按各自扩展名匹配
    /// （.vclip / .aclip），否则清理视频残留会把音频轨的可续传分片一起删掉。
    /// </summary>
    internal static void CleanStaleClipsFor(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        string prefix = Path.GetFileNameWithoutExtension(path);
        string clipExt = IsVideoClipPath(path) ? ".vclip" : ".aclip";
        foreach (var clip in new DirectoryInfo(dir).EnumerateFiles("*_" + prefix + clipExt))
        {
            try { clip.Delete(); }
            catch (IOException) { /* 并发占用时跳过，下次运行再清理 */ }
        }
    }

    /// <summary>
    /// 删除历史中断遗留的合并临时文件（<paramref name="path"/> + ".merging"）。
    /// 成品路径已完整时该文件无任何消费者（重下会经 File.Create 截断自愈），
    /// 但"跳过下载"路径不会走到合并步骤——不显式清理就永久泄漏磁盘。
    /// </summary>
    private static void DeleteStaleMergeTmp(string path)
    {
        var merging = path + ".merging";
        try { if (File.Exists(merging)) File.Delete(merging); }
        catch (IOException) { /* 占用中：下次运行再清理 */ }
    }

    /// <summary>
    /// 成品文件已完整（本次产出或跳过判定）时，清理该目标路径上全部不再有消费者的
    /// 下载工作残留：分片（*_.vclip/.aclip）、合并临时文件（.merging）、多线程轨道清单、
    /// 单线程临时文件 path+".tmp" 及其续传清单、aria2c 控制文件 path+".aria2"。
    /// 跨下载模式的残留（先单线程中断留下 GB 级 .tmp，之后改用多线程/aria2 下完）
    /// 此前无人清理：既永久占用磁盘，又让 aid 工作目录因非空永不回收
    ///（DownloadFinalizer 只删空目录）。绝不触碰成品 path 本身，也不动
    /// path+".manifest.json"——那是 aria2 的"完成证书"，供下次跳过完整文件。
    /// internal 供 DownloadPipelineTests 直接验证清理范围（成品与完成证书不受影响）。
    /// </summary>
    internal static void CleanStaleWorkArtifactsFor(string path)
    {
        CleanStaleClipsFor(path);
        DeleteStaleMergeTmp(path);
        string tmpName = path + ".tmp";
        TryDeleteStale(tmpName);
        DeleteResumeManifest(tmpName);
        TryDeleteStale(path + ".aria2");
        DeleteResumeManifest(ClipPathFor(path, 0));
    }

    //此函数主要是切片下载逻辑
    internal static List<Clip> GetAllClips(string url, long fileSize)
    {
        List<Clip> clips = [];
        int index = 0;
        long counter = 0;
        long perSize = Config.Current.ThreadSegmentSizeMb * 1024L * 1024;
        while (fileSize > 0)
        {
            long segmentSize = Math.Min(perSize, fileSize);
            // to 必须始终指向段末（而非末段用 -1 表示"到 EOF"）：
            // RangeDownloadToTmpAsync 的"已下载完成跳过"检查以 toPosition > 0 为前提，
            // 末段 to=-1 会被调用处映射为 null 而跳过该检查；断点续传时完整末段会发送
            // Range: bytes=<fileSize>-（起始即 EOF），服务器回 416，重试同请求直至永久失败。
            Clip c = new()
            {
                index = index,
                from = counter,
                to = counter + segmentSize - 1
            };
            clips.Add(c);
            fileSize -= segmentSize;
            counter += segmentSize;
            index++;
        }
        return clips;
    }

    /// <summary>
    /// 分片失败是否属于"切分错误"的连带（越界分片 416 / 网络错）：这类失败在按权威总长
    /// 重新切分后会自愈，允许被尺寸修复覆盖。其余（非可重试的输入/逻辑错误、真实取消）
    /// 是独立缺陷，必须原样抛出。
    /// </summary>
    private static bool IsSizeArtifactFailure(Exception ex)
        => ex is IOException or HttpRequestException
           || (ex is AggregateException ae && ae.InnerExceptions.All(IsSizeArtifactFailure));

    /// <summary>
    /// 用 GET（Range: bytes=0-0）读取资源的权威总长，用于"已下载完成"跳过前的复核：
    /// HEAD 探测可能被 CDN 返回缓存/占位长度（与 <see cref="RangeDownloadToTmpAsync"/> 的
    /// Content-Range 交叉校验同源的问题），若既有文件长度恰好等于错误的 HEAD 值，会被误判为
    /// "已完整下载"而把陈旧/截断文件报成成功。
    /// 返回 null 表示**无法确认**权威总长（复核失败 / 服务端不提供总长）：调用方必须退回
    /// 纯长度跳过、**绝不能删除既有文件**——否则对 `bytes 0-0/*`（未知总长）或分块响应
    ///（无 Content-Length）的服务，每次重跑都会误删一份完整文件。
    /// 只走跳过路径（文件已存在），不影响全新下载的探测合并（仍只发一次 HEAD）。
    /// </summary>
    private static async Task<long?> GetAuthoritativeSizeAsync(string url, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyMediaRequestHeaders(request, url);
            request.Headers.Range = new(0, 0);
            // 复核只是单字节小请求，独立短超时兜底，避免卡死跳过路径（失败一律视为"无法确认"）
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await HTTPUtil.MediaDownloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            response.EnsureSuccessStatusCode();
            // 206 的 Content-Range 尾段 /total 是权威总长；缺尾段（bytes 0-0/*）无法确认
            if (response.Content.Headers.ContentRange is { HasRange: true, Length: { } total })
                return total;
            // 服务器忽略 Range 返回 200：Content-Length 是权威总长（完整响应）。
            // 206 部分响应的 Content-Length 只是分片大小（如 bytes 0-0/* 时为 1），
            // 绝不能当总长——否则会据此误删有效文件；分块（无 Content-Length）同样无法确认。
            if (response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentLength is { } length)
                return length;
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // 真正的用户取消必须向上传播；复核失败（瞬时网络/被拒）不阻断跳过——
            // "无法确认"≠"不符"，退回纯长度跳过，不删除既有文件。
            if (token.IsCancellationRequested) throw;
            Logger.LogDebug("权威大小复核失败，退回长度跳过: {0}", ex.Message);
            return null;
        }
    }

    private static async Task<(long size, HttpResponseHeaders? headers, HttpContentHeaders? contentHeaders)> GetFileSizeAndHeadersAsync(string url, CancellationToken token = default)
    {
        // HEAD 优先：媒体 CDN 普遍支持 HEAD，不传输响应体，连接可直接归还连接池。
        // 此前用 GET + ResponseHeadersRead 又不消费响应体，连接会被关闭无法复用
        //（"响应体未消费弃连接"）。HEAD 失败（405/403 等）或未返回 Content-Length 时
        // 回退 GET，保证返回的 size 是权威值（不因 HEAD 行为差异误判为 0 而退化单线程）。
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var method = attempt == 0 ? HttpMethod.Head : HttpMethod.Get;
            try
            {
                using var httpRequestMessage = new HttpRequestMessage(method, url);
                ApplyMediaRequestHeaders(httpRequestMessage, url);
                httpRequestMessage.RequestUri = new(url);
                using var response = (await HTTPUtil.MediaDownloadClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead, token)).EnsureSuccessStatusCode();
                long totalSizeBytes = response.Content.Headers.ContentLength ?? 0;
                if (method == HttpMethod.Head && totalSizeBytes <= 0)
                    continue; // HEAD 未声明长度：部分 CDN 对 HEAD 不返回 Content-Length，回退 GET 再探测
                return (totalSizeBytes, response.Headers, response.Content.Headers);
            }
            catch (HttpRequestException) when (method == HttpMethod.Head)
            {
                // HEAD 不被支持（405/403 等）：回退 GET（GET 仍失败时异常向上抛出）
            }
        }
        // 仅当 GET 也失败时不可达（GET 异常会向上抛出）；此处为编译器兜底
        return (0, null, null);
    }

    /// <summary>
    /// 断点续传的资源身份清单：记录某份 .tmp 临时文件对应的远端资源身份，防止
    /// "长度相同但内容来自另一资源"被静默续传/采用（同一 aid/cid 从 1080P 切 720P、
    /// AVC 切 HEVC 时路径不变但内容不同）。仅当清单与当前请求的资源身份一致时，
    /// 已有的 .tmp 前缀才是可信的续传素材。
    /// 身份用 <see cref="StableResourceIdentity"/>（剥离会刷新的签名 query 参数），
    /// 而非完整签名 URL——媒体 URL 的 deadline/sign 等参数每次请求都会刷新。
    /// </summary>
    internal sealed record ResumeManifest(string Identity, long TotalLength, string? LastModified, string? ETag,
        long SegmentSizeBytes = 0);

    /// <summary>
    /// 签名/时间戳等每次请求都会刷新的 query 参数：它们不构成资源身份，续传清单比较
    /// 时必须剥离，否则同一资源在不同时刻的 URL 会被误判为不同资源而永远无法续传。
    /// </summary>
    private static readonly HashSet<string> SignatureQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "deadline", "sign", "w_rid", "wts", "ts", "expires", "auth_key", "ok", "ok_av",
        "oid", "trid", "platform", "fnval", "fnver", "fourk", "type", "uparams", "t",
    };

    /// <summary>剥离签名 query 参数后的稳定资源身份（路径 + 非签名参数，按参数名排序）。</summary>
    internal static string StableResourceIdentity(string url)
    {
        var qIndex = url.IndexOf('?');
        if (qIndex < 0) return url;
        var path = url[..qIndex];
        var query = url[(qIndex + 1)..];
        var kept = new List<string>();
        foreach (var pair in query.Split('&'))
        {
            if (pair.Length == 0) continue;
            var sep = pair.IndexOf('=');
            var key = sep > 0 ? pair[..sep] : pair;
            if (SignatureQueryKeys.Contains(key)) continue;
            kept.Add(pair);
        }
        kept.Sort(StringComparer.Ordinal);
        return kept.Count == 0 ? path : path + "?" + string.Join("&", kept);
    }

    private static string ResumeManifestPath(string tmpName) => tmpName + ".manifest.json";

    /// <summary>把本次下载的资源身份写入清单（.tmp.manifest.json 旁车文件）。
    /// LastModified 来自内容头（HttpContentHeaders），ETag 来自响应头。</summary>
    private static async Task WriteResumeManifestAsync(string tmpName, string url, long totalLength,
        HttpResponseHeaders? headers, HttpContentHeaders? contentHeaders, CancellationToken token, long segmentSizeBytes = 0)
    {
        try
        {
            var m = new ResumeManifest(
                StableResourceIdentity(url),
                totalLength,
                contentHeaders?.LastModified?.ToString("R"),
                headers?.ETag?.ToString(),
                segmentSizeBytes);
            // 原子写入：写唯一临时文件后同目录 rename。进程在写入中被杀会留下截断 JSON，
            // 下次只能放弃已有 .tmp（CanResumeFrom 读清单失败）。rename 保证清单要么完整
            // 要么不存在；失败时清理临时文件。
            string manifestPath = ResumeManifestPath(tmpName);
            string tmp = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(tmp,
                    System.Text.Json.JsonSerializer.Serialize(m, DownloadManifestJsonContext.Default.ResumeManifest), token);
                token.ThrowIfCancellationRequested();
                File.Move(tmp, manifestPath, true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception ex) when (ExceptionPolicies.IsBestEffortFailure(ex))
                {
                    /* 清理失败不影响主流程 */
                }
            }
        }
        catch (Exception ex) when (ExceptionPolicies.IsBestEffortFailure(ex))
        {
            // 清单写入失败不阻断本次下载；下次若缺清单则拒绝续传并完整重下。
            Logger.LogDebug("写入续传清单失败: {0}", ex.Message);
        }
    }

    /// <summary>
    /// 校验某份 .tmp 是否可用于续传当前资源。返回 true 表示身份一致可续传；
    /// 返回 false 表示身份不符/清单缺失/长度不符，调用方应删除 .tmp 完整重下。
    /// <paramref name="currentETag"/>/<paramref name="currentLastModified"/> 是本次探测到的
    /// 服务器校验器：清单与当前都有校验器且不一致时（同路径内容变化但长度不变），必须拒绝
    /// ——否则旧的完整 .tmp 会被直接采用，产出"长度正确但内容损坏"的文件。
    /// internal 供测试验证"等长但资源身份不同"的 .tmp 被拒绝。
    /// </summary>
    internal static async Task<(bool CanResume, string? Reason)> CanResumeFromAsync(
        string tmpName, string url, long totalLength, string? currentETag = null,
        string? currentLastModified = null, CancellationToken token = default, long segmentSizeBytes = 0)
    {
        try
        {
            if (!File.Exists(ResumeManifestPath(tmpName)))
            {
                return (false, "缺少续传清单（无法确认 .tmp 内容属于当前资源）");
            }
            var m = System.Text.Json.JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(ResumeManifestPath(tmpName), token),
                DownloadManifestJsonContext.Default.ResumeManifest);
            if (m is null)
            {
                return (false, "续传清单为空");
            }
            // 用稳定身份比较（剥离签名参数）：媒体 URL 的 deadline/sign 每次请求刷新，
            // 直接用完整 URL 相等会让同一资源永远无法续传。
            var currentIdentity = StableResourceIdentity(url);
            if (m.Identity != currentIdentity)
            {
                return (false, "续传清单资源与当前资源不一致");
            }
            if (m.TotalLength != totalLength)
            {
                return (false, $"续传清单总长({m.TotalLength})与当前探测({totalLength})不一致");
            }
            // 旧多线程清单没有布局信息（默认 0），也不能确认后续分片的偏移。
            // 单线程/aria2c 不依赖本工具的分片布局，仍按原有身份和长度校验。
            if (segmentSizeBytes > 0 && m.SegmentSizeBytes != segmentSizeBytes)
            {
                return (false, "续传清单分片布局缺失或与当前分片大小不一致");
            }
            // 一方有可用校验器而另一方没有时，也无法证明已有字节属于当前版本。
            var currentValidator = SelectIfRange(currentETag, currentLastModified);
            var previousValidator = SelectIfRange(m.ETag, m.LastModified);
            if ((currentValidator is null) != (previousValidator is null))
                return (false, "续传清单与当前探测缺少可比较的资源校验器");
            if (currentValidator is not null && previousValidator is not null
                && !string.Equals(currentValidator, previousValidator, StringComparison.Ordinal))
                return (false, "续传清单与当前探测的资源校验器不一致");
            if (currentETag is not null && m.ETag is not null && currentETag != m.ETag)
            {
                return (false, $"服务器 ETag 已变化（{m.ETag} → {currentETag}），内容不可信");
            }
            if (currentLastModified is not null && m.LastModified is not null
                && !string.Equals(currentLastModified, m.LastModified, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "服务器 Last-Modified 已变化，内容不可信");
            }
            return (true, null);
        }
        catch (Exception ex) when (ExceptionPolicies.IsJsonOrIoFailure(ex))
        {
            return (false, $"续传清单读取失败: {ex.Message}");
        }
    }

    /// <summary>清理 .tmp 的续传清单（下载完成后随 .tmp 一起移除）。</summary>
    private static void DeleteResumeManifest(string tmpName)
    {
        try { if (File.Exists(ResumeManifestPath(tmpName))) File.Delete(ResumeManifestPath(tmpName)); }
        catch (Exception ex) when (ExceptionPolicies.IsBestEffortFailure(ex)) { /* 清理失败不影响主流程 */ }
    }

    // RFC If-Range 不允许弱 ETag；弱 ETag 必须回退到 Last-Modified。
    private static string? SelectIfRange(string? etag, string? lastModified)
    {
        if (EntityTagHeaderValue.TryParse(etag, out var parsed) && !parsed.IsWeak)
            return parsed.ToString();
        return string.IsNullOrEmpty(lastModified) ? null : lastModified;
    }

    /// <summary>读取续传清单里的 If-Range 值（ETag 优先，其次 Last-Modified）；清单缺失返回 null。</summary>
    internal static async Task<string?> ReadManifestIfRangeAsync(string tmpName, CancellationToken token = default)
    {
        try
        {
            if (!File.Exists(ResumeManifestPath(tmpName))) return null;
            var m = System.Text.Json.JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(ResumeManifestPath(tmpName), token),
                DownloadManifestJsonContext.Default.ResumeManifest);
            if (m is null) return null;
            return SelectIfRange(m.ETag, m.LastModified);
        }
        catch (Exception ex) when (ExceptionPolicies.IsJsonOrIoFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// 将下载地址强制转换为HTTP
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    private static string ReplaceUrl(string url)
    {
        if (url.Contains(".mcdn.bilivideo.cn:"))
        {
            Logger.LogDebug("对[*.mcdn.bilivideo.cn:xxx]域名不做处理");
            return url;
        }

        // B3-F3：--force-http 会同时使登录 Cookie 与下载票据在明文链路可见。
        // 这是用户显式选项（抓包/调试场景），无法强制去 Cookie（多数 CDN 需 Cookie
        // 才能返回完整体，强制去除会直接下载失败）。至少显式告警，让用户在明文链路上
        // 的暴露面可知。
        Logger.LogWarn("--force-http：媒体请求降级为明文 http，登录 Cookie 将随请求在链路上可见（建议仅在抓包/调试的仅本机链路使用）");
        Logger.LogDebug("将https更改为http");
        return url.Replace("https:", "http:");
    }
}

/// <summary>分片 If-Range 未获得 206，整条轨道必须重新探测并下载。</summary>
internal sealed class RemoteResourceChangedException : InvalidOperationException
{
    public RemoteResourceChangedException()
        : base("远端资源可能已变化或服务器不支持分片，已丢弃分片，请重试下载") { }
}

/// <summary>AOT 裁剪安全的续传清单 JSON 序列化上下文。</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(BBDownDownloadUtil.ResumeManifest))]
internal partial class DownloadManifestJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

/// <summary>
/// 206 响应 Content-Range 声明的资源总长与探测大小不符：说明 HEAD 探测被 CDN 返回
/// 缓存/占位长度（或探测后内容已变化）。携带权威总长 <see cref="ActualTotal"/>，
/// 上层据此修正后重试/重新切分，而不是用错误大小空转重试。
/// 继承 <see cref="IOException"/> 使页面级重试的既有 catch 原样生效。
/// </summary>
internal sealed class RemoteSizeMismatchException : IOException
{
    /// <summary>当前探测（HEAD/上轮切分）使用的大小。</summary>
    public long ExpectedTotalSize { get; }

    /// <summary>206 Content-Range 声明的权威总长。</summary>
    public long ActualTotal { get; }

    public RemoteSizeMismatchException(long expectedTotalSize, long actualTotal)
        : base($"Content-Range 声明总长({actualTotal})与探测大小({expectedTotalSize})不符，远端内容已变化，已丢弃本地内容并按权威总长重新下载")
    {
        ExpectedTotalSize = expectedTotalSize;
        ActualTotal = actualTotal;
    }
}
