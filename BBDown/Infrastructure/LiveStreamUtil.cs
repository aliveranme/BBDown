using System.Buffers;
using System.Text;
using System.Text.Json;
using BBDown.Core;
using BBDown.Core.Util;

namespace BBDown;

/// <summary>
/// B站直播流解析与录制。直播流是无限流：录制持续写入直到用户取消（Ctrl+C）或主播下播。
/// </summary>
public static class LiveStreamUtil
{
    /// <summary>直播 API 主机。internal 可注入：测试用本地假服务器覆盖，验证完整录制循环。</summary>
    internal static string LiveApiHost { get; set; } = "https://api.live.bilibili.com";

    /// <summary>
    /// 读停滞看门狗阈值：网络波动时连接可能既不复位也不 EOF，只是不再有数据到达
    /// （黑洞/静默断网）。无超时客户端上的 ReadAsync 会永久挂起，录制卡死且重连逻辑
    /// 永远不触发。每收到一段数据重置计时，超时未收到数据视为连接死亡，按读中断
    /// 交上层重连续录。internal 可注入：测试缩短该阈值验证看门狗行为。
    /// </summary>
    internal static TimeSpan ReadStallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 直播间不提供 flv 格式（如仅 HLS/ts，或接口未返回任何流）：终结态，
    /// 重试不会改变接口能力，继续退避重连只会无限空转。
    /// </summary>
    public sealed class LiveStreamUnavailableException : InvalidOperationException
    {
        public LiveStreamUnavailableException(string message) : base(message) { }
    }

    /// <summary>本地写盘失败（磁盘满/权限/文件被占用）：重试无意义，立即终止并保留已录分段。</summary>
    public sealed class LiveStreamWriteException : IOException
    {
        public LiveStreamWriteException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>把 B 站 qn 数值映射为可读画质名（g_qn_desc 常用档位）。</summary>
    public static string QualityName(int qn) => qn switch
    {
        >= 30000 => "杜比",
        >= 25000 => "杜比原画",
        >= 20000 => "4K",
        >= 15000 => "2K",
        10000 => "原画",
        400 => "蓝光",
        250 => "超清",
        150 => "高清",
        80 => "流畅",
        _ => $"qn={qn}",
    };

    /// <summary>
    /// 解析直播间信息与一条可录制的 flv 直播流地址。
    /// 返回的 Quality 是本账号实际拿到的最高的 current_qn（0 表示响应未携带）。
    /// </summary>
    public static async Task<(string Url, string Title, string Uname, string RoomId, int Quality)> ResolveAsync(string roomId, CancellationToken token = default)
    {
        if (!long.TryParse(roomId, out _))
            throw new ArgumentException($"直播间 ID 必须是数字，当前值: '{roomId}'");

        string infoApi = $"{LiveApiHost}/room/v1/Room/get_info?room_id={roomId}";
        string infoJson = await HTTPUtil.GetWebSourceAsync(infoApi, token: token);
        using var infoDoc = JsonDocument.Parse(infoJson);
        int infoCode = infoDoc.RootElement.GetInt32Safe("code");
        if (infoCode != 0)
            throw new InvalidOperationException($"获取直播间信息失败(code={infoCode}): {infoDoc.RootElement.GetValueAsStringSafe("message")}");
        var info = infoDoc.RootElement.GetPropertySafe("data");
        string title = info.GetValueAsStringSafe("title");
        if (title == "") title = $"直播间{roomId}";
        string uname = info.GetValueAsStringSafe("uname");
        if (info.GetInt32Safe("live_status") != 1)
            throw new InvalidOperationException($"直播间 {roomId} 当前未在直播");

        // 画质：先请求 qn=30000（最高档，杜比/4K/原画按账号权限自动回落），接口对未登录
        // 请求只返回游客画质（最高 720P），带 Cookie 才返回账号可看的最高画质——因此调用方
        // 必须先加载登录凭据。个别房间最高画质仅走 ts/fmp4 时回落 qn=10000（原画）再取一次；
        // 两次都取不到 flv 才报不可录制（其中"列出了 flv 但无可用节点"按瞬态故障处理，可重试）。
        string? picked = null;
        int pickedQn = 0;
        string[] lastFormats = [];
        foreach (int qn in new[] { 30000, 10000 })
        {
            string playApi = $"{LiveApiHost}/xlive/web-room/v2/index/getRoomPlayInfo" +
                $"?room_id={roomId}&protocol=0,1&format=0,1,2&codec=0,1&qn={qn}&platform=web";
            string playJson = await HTTPUtil.GetWebSourceAsync(playApi, token: token);
            using var playDoc = JsonDocument.Parse(playJson);
            int playCode = playDoc.RootElement.GetInt32Safe("code");
            if (playCode != 0)
                throw new InvalidOperationException($"获取直播流信息失败(code={playCode}): {playDoc.RootElement.GetValueAsStringSafe("message")}");
            var playData = playDoc.RootElement.GetPropertySafe("data").GetPropertySafe("playurl_info").GetPropertySafe("playurl");
            picked = SelectFlvUrl(playData, out lastFormats, out pickedQn);
            if (picked is not null) break;
        }
        if (picked is null)
        {
            if (lastFormats.Contains("flv"))
            {
                // flv 在接口格式列表里但没有可用节点：CDN/接口瞬态故障，可重试
                throw new InvalidOperationException($"暂时无法获取直播间 {roomId} 的 FLV 流地址（接口列出了 flv 但无可用节点），将自动重试");
            }
            // 不列出格式时用户无法区分"不支持"与"没有流"：把接口实际提供的
            // format_name（flv/ts/fmp4）一并报出，明确说明当前仅支持 flv。
            throw new LiveStreamUnavailableException(
                $"无法获取直播间 {roomId} 的可录制流地址" +
                (lastFormats.Length > 0
                    ? $"（接口可用格式: {string.Join(", ", lastFormats)}；当前仅支持 flv，HLS/ts/fmp4 暂不支持）"
                    : "（接口未返回任何流）"));
        }
        return (picked, title, uname, roomId, pickedQn);
    }

    /// <summary>
    /// 从 getRoomPlayInfo 的 <c>playurl_info.playurl</c> 数据中挑选一条 FLV 直播流地址。
    /// 纯函数（不发起网络请求），供单测注入内联 JSON 覆盖选流逻辑。
    /// 返回第一个可用的 FLV url；<paramref name="availableFormats"/> 收集接口实际提供的
    /// format_name（含被跳过的 ts/fmp4），供调用方在无 FLV 时报出可操作的错误信息；
    /// <paramref name="quality"/> 返回选中流所在 codec 的 current_qn。
    /// </summary>
    internal static string? SelectFlvUrl(JsonElement playData, out string[] availableFormats, out int quality)
    {
        var available = new HashSet<string>();
        string? picked = null;
        int pickedQn = 0;
        foreach (var stream in playData.EnumerateArraySafe("stream"))
        {
            foreach (var format in stream.EnumerateArraySafe("format"))
            {
                var formatName = format.GetValueAsStringSafe("format_name");
                if (formatName != "") available.Add(formatName);
                if (formatName != "flv") continue;
                foreach (var codec in format.EnumerateArraySafe("codec"))
                {
                    var baseUrl = codec.GetValueAsStringSafe("base_url");
                    if (baseUrl == "") continue;
                    var qn = codec.GetInt32Safe("current_qn");
                    foreach (var urlInfo in codec.EnumerateArraySafe("url_info"))
                    {
                        var host = urlInfo.GetValueAsStringSafe("host");
                        var extra = urlInfo.GetValueAsStringSafe("extra");
                        if (host == "") continue;
                        if (picked is null)
                        {
                            picked = host + baseUrl + extra;
                            pickedQn = qn;
                        }
                    }
                }
            }
        }
        availableFormats = available.Count == 0 ? [] : available.ToArray();
        quality = pickedQn;
        return picked;
    }

    /// <summary>
    /// 把直播流持续写入本地文件，直到流结束、取消或主播下播。
    /// 写入 <c>path.segs/会话目录/seg-NNN.flv</c> 分段文件，录制结束后用 FFmpeg concat
    /// 合成最终文件：B 站直播流地址带时效参数，长时间录制中过期是常态，网络瞬断或
    /// 地址过期时重新解析流地址续录。网络中断/API 故障期间持续退避重试（不设重试上限）：
    /// 只要直播间仍在直播且用户未取消，网络恢复后下一次解析成功即自动续录。
    /// 用户取消（Ctrl+C）时当前分段已写入的内容同样保留并参与合成保存。
    /// </summary>
    public enum LiveRecordResult
    {
        Success,
        NoData,
        ConcatFailedWithSegmentsSaved
    }

    /// <summary>
    /// 录制直播流到文件。如果发生断流且房间仍在直播，会自动重连并生成多个分段，
    /// 录制结束后将所有分段合并为最终文件。
    /// </summary>
    public static async Task<LiveRecordResult> DownloadToFileAsync(string roomId, string path, Action<long>? onProgress, CancellationToken token = default)
    {
        // 每段独立文件：重连后的新 FLV 流含新的 FLV 头与重置时间戳，
        // 直接追加到旧流末尾的原始字节拼接不保证可播放。记录在独立分段里，
        // 录制结束后用 FFmpeg concat/remux 合成最终文件。
        // 分段根目录用绝对路径：自定义 --output 目录（如 --output E:/录制/xxx.flv）时
        // 相对路径会让 FFmpeg concat 列表里的 file '...' 相对 CWD 解析失败。
        var segRoot = Path.GetFullPath(path) + ".segs";

        // 每次录制用带时间戳的独立会话子目录：合并失败保留的分段是用户的可恢复资产，
        // 若下一次启动直接递归删除整个 .segs（旧实现正是如此），保留内容即丢失。
        // 这里只隔离旧会话（提示保留路径），不自动删除非空会话；当前会话完成后清理自己的目录。
        ReportStaleSessions(segRoot);
        var segDir = Path.Combine(segRoot, $"session-{DateTime.Now:yyyyMMdd_HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(segDir);

        long total = 0;
        // 连续无进展次数：决定退避时长，任何分段收到数据即清零。
        int consecutiveFailures = 0;
        int segIndex = 0;
        var segmentFiles = new List<string>();

        // 退避重连：指数退避（3s→6s→12s→24s→30s 封顶）。旧实现重连 3 次后放弃，
        // 断网几分钟的场景必然提前终止，网络恢复后也不会自动继续录制——这里改为
        // 无限重试：只要直播间仍在播且用户未取消就持续等网络恢复（每次周期由 API
        // 超时+退避主导，不会高频轮询）。
        async Task BackoffAsync(Exception? cause = null)
        {
            consecutiveFailures++;
            int backoffMs = Math.Min(3000 * (1 << Math.Min(consecutiveFailures - 1, 4)), 30_000);
            Logger.LogWarn(cause is null
                ? $"直播流无数据，{backoffMs / 1000} 秒后重试（第 {consecutiveFailures} 次）..."
                : $"直播流中断（{cause.Message}），{backoffMs / 1000} 秒后重连（第 {consecutiveFailures} 次）...");
            await Task.Delay(backoffMs, token);
        }

        // 确认直播间仍在直播：在播返回 true；确认下播返回 false；其它异常原样抛出，
        // 由下方外层 catch 按瞬态故障退避重连。
        async Task<bool> IsRoomLiveAsync()
        {
            try
            {
                _ = await ResolveAsync(roomId, token);
                return true;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("当前未在直播"))
            {
                return false;
            }
        }

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var (url, _, _, _, _) = await ResolveAsync(roomId, token);
                    var segPath = Path.Combine(segDir, $"seg-{segIndex++:000}.flv");
                    // progressBase = 已录完分段的累计字节（total），进度回调上报累计值，
                    // 重连后新分段从 total 起报而不是从 0 倒退。
                    // 本段收到任何字节都算有效传输：读中断返回的已写字节同样计入，
                    // 使 URL 到期/瞬断这类"有数据"的连接中断不消耗退避预算，不会误终止长录制。
                    var (segBytes, readInterrupted) = await StreamToFileAsync(url, segPath, total, onProgress, token);
                    if (readInterrupted) Logger.LogDebug("直播流连接在读取时中断，已写入 {0} 字节", segBytes);

                    if (segBytes > 0)
                    {
                        // 本段有内容（含"读到数据后中断"的部分段与取消时已写段）：计入已录内容。
                        segmentFiles.Add(segPath);
                        total += segBytes;
                        consecutiveFailures = 0;
                        if (readInterrupted)
                        {
                            // 用户取消：结束录制，已写内容照常保存
                            if (token.IsCancellationRequested) break;
                            // 有数据的中断（URL 到期/瞬断/读停滞是常态）：立即重新解析续录，
                            // 不等退避，避免每次 URL 到期丢失内容。
                            Logger.LogWarn("直播流连接中断但直播间仍在直播，正在重新解析流地址续录...");
                            continue;
                        }
                        // 正常 EOF：确认直播间状态，在播则续录，下播则结束录制
                        if (await IsRoomLiveAsync()) continue;
                        break;
                    }

                    // 零字节段：连接刚建立就结束。若直播仍进行，这是连接到期/CDN 切换，
                    // 退避后重连，避免高速轮询。
                    try { File.Delete(segPath); } catch (IOException) { }
                    if (!await IsRoomLiveAsync()) break; // 确认下播：结束录制
                    Logger.LogWarn("直播流连接立即结束但直播间仍在直播，正在退避后重连...");
                    await BackoffAsync(new IOException("直播流零字节 EOF"));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break; // 用户取消：保留已录分段，退出后合成保存
                }
                catch (OperationCanceledException ex)
                {
                    // 非用户取消的取消异常：直播流读取已改用无超时客户端+停滞看门狗，
                    // 不会再因 HttpClient.Timeout 抛 TaskCanceledException；此处是
                    // ResolveAsync（仍走全局超时客户端）等内部调用超时/取消，
                    // 按瞬态故障退避重连。
                    await BackoffAsync(ex);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TimeoutException or LiveStreamWriteException)
                {
                    // 直播间下播（"当前未在直播"）是终结态而非可恢复故障：正常结束，走合成保存
                    if (ex is InvalidOperationException && ex.Message.Contains("当前未在直播"))
                        break;
                    // 不可恢复的终结态：重试不会改变结果——直播间不提供 flv / 本地写盘失败
                    if (ex is LiveStreamUnavailableException or LiveStreamWriteException)
                        throw;
                    // 网络/API 瞬态故障（连接中断、API 超时、风控页、接口暂不可用）：
                    // 退避重连。持续断网时循环以"API 超时+退避"为周期，网络恢复后
                    // 下一次 ResolveAsync 成功即自动续录，不会中途放弃。
                    await BackoffAsync(ex);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 取消发生在重连等待 Task.Delay 或 while 顶部检查（try 外）：先走到循环后的合成保存再退出
        }
        catch (Exception)
        {
            // 发生终结态异常（LiveStreamUnavailableException / LiveStreamWriteException）或未捕获异常退出时：
            // 若尚未录入任何有效分段，清理本次创建的空会话目录与根目录，避免在磁盘留下空残留。
            if (total == 0 || segmentFiles.Count == 0)
                CleanupSessionDir(segDir, segRoot);
            throw;
        }

        // 未收到任何字节：不生成空文件，返回 NoData 让调用方明确失败
        if (total == 0 || segmentFiles.Count == 0)
        {
            CleanupSessionDir(segDir, segRoot);
            return LiveRecordResult.NoData;
        }

        // 网络中断/用户取消可能使段尾只剩半个 FLV 标签：ffmpeg concat demuxer 在
        // 截断标签处会报错并中止整个合成（"Packet corrupt/Invalid data"），而不是
        // 跳过它继续。合成/改名前把所有分段裁到最后一个完整标签，否则断流重连的
        // 录制几乎必然合成失败。损失仅限截断的半截标签（无法播放的垃圾字节）。
        foreach (var seg in segmentFiles)
            TrimFlvTail(seg);

        // 多段 → FFmpeg concat 合成最终文件；单段直接改名。
        if (segmentFiles.Count == 1)
        {
            try
            {
                File.Move(segmentFiles[0], path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 改名失败（目标目录不存在/被占用等）：保留分段供手动恢复
                Logger.LogWarn($"保存最终文件失败: {ex.Message}；已录分段保留在 {segDir}");
                return LiveRecordResult.ConcatFailedWithSegmentsSaved;
            }
        }
        else
        {
            // 用户取消录制（Ctrl+C）时 token 已取消，但已录分段仍需合成保存——用
            // 已取消的令牌调 ConcatSegmentsAsync 会让 FFmpeg 进程立即被取消，留下
            // 半截产物。这里用独立的合并令牌：录制停止后给合成阶段一个有限的
            // 收尾窗口（MuxerTimeoutMinutes 以内），超时仍失败则保留分段供重录。
            using var finalizeCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            string tempOutPath = path + $".concat-{Guid.NewGuid():N}.tmp.flv";
            bool concatOk = false;
            try
            {
                concatOk = await ConcatSegmentsAsync(segmentFiles, tempOutPath, finalizeCts.Token);
                if (concatOk && File.Exists(tempOutPath))
                {
                    File.Move(tempOutPath, path, true);
                }
            }
            finally
            {
                try { if (File.Exists(tempOutPath)) File.Delete(tempOutPath); } catch (IOException) { }
            }

            if (!concatOk)
            {
                // 合并失败：保留分段目录——那是用户可恢复的资产，且不破坏可能已存在的旧输出文件。
                Logger.LogWarn($"直播分段合成失败，已保留分段在 {segDir}");
                return LiveRecordResult.ConcatFailedWithSegmentsSaved;
            }
        }
        // 合成成功：清理本次会话的分段目录（仅本会话，不动其它会话/旧会话），
        // 会话目录清空后顺手删掉空的 .segs 根目录，避免录制结束残留空文件夹。
        CleanupSessionDir(segDir, segRoot);
        return LiveRecordResult.Success;
    }

    /// <summary>
    /// 删除本次会话的分段目录；若 .segs 根目录已空（没有其它会话的保留分段）则一并删除。
    /// 删除失败（文件被占用等）静默跳过——保留内容总比误删好。
    /// </summary>
    private static void CleanupSessionDir(string segDir, string segRoot)
    {
        try { if (Directory.Exists(segDir)) Directory.Delete(segDir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            if (Directory.Exists(segRoot) && !Directory.EnumerateFileSystemEntries(segRoot).Any())
                Directory.Delete(segRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 扫描分段根目录下的旧会话子目录并提示保留位置，但**不删除任何非空会话**。
    /// 旧实现启动时递归删除整个 .segs 目录，把上次合并失败保留的可恢复分段丢掉了。
    /// internal 供测试验证"非空会话不被删除"。
    /// </summary>
    internal static void ReportStaleSessions(string segRoot)
    {
        if (!Directory.Exists(segRoot)) return;
        try
        {
            foreach (var stale in Directory.GetDirectories(segRoot))
            {
                // 非空旧会话：提示用户保留位置，供手动恢复/重合并
                if (Directory.EnumerateFiles(stale).Any())
                    Logger.LogWarn($"发现上次直播录制未完成的分段，保留在: {stale}（可用 ffmpeg 手动 concat 恢复）");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogDebug("扫描直播分段残留失败: {0}", ex.Message);
        }
    }

    /// <summary>
    /// 把 FLV 文件末尾的截断标签裁掉。网络中断/用户取消时段尾可能只有半个标签
    /// （标签头声明了 N 字节负载但文件提前结束）——ffmpeg concat demuxer 遇到截断
    /// 标签会直接报错中止整个合成，而不是跳过。逐标签扫描定位最后一个完整标签，
    /// 就地截断文件。返回是否发生了裁剪；解析失败/无需裁剪返回 false。
    /// internal 供测试验证（合成假 FLV：完整标签 + 截断尾）。
    /// </summary>
    internal static bool TrimFlvTail(string path)
    {
        // FLV 标签类型：0x08 音频 / 0x09 视频 / 0x12 脚本(元数据) / 0x16-0x18 Enhanced FLV 扩展
        // 掩码 0x1F 过滤高位 Filter 标志（Adobe FLV 标准：Bit 5 标识是否需要预处理/加密）
        static bool IsTagType(int t) => (t & 0x1F) is 0x08 or 0x09 or 0x12 or 0x16 or 0x17 or 0x18;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            long len = fs.Length;
            // 标准 FLV 头 9 字节，其后是 4 字节 PreviousTagSize0（恒 0），首个标签从 13 开始
            long pos = 13;
            long lastGoodEnd = 13;
            Span<byte> head = stackalloc byte[4];
            while (pos + 11 <= len)
            {
                fs.Position = pos;
                if (fs.Read(head) != 4) break;
                if (!IsTagType(head[0])) break; // 数据流错位：不再信任后续字节
                int dlen = (head[1] << 16) | (head[2] << 8) | head[3];
                long total = 11L + dlen + 4; // 标签头 11 字节 + 负载 + 4 字节 prevTagSize
                if (pos + total > len) break; // 截断尾：最后一个完整标签结束于 lastGoodEnd
                pos += total;
                lastGoodEnd = pos;
            }
            if (lastGoodEnd > 13 && lastGoodEnd < len)
            {
                fs.SetLength(lastGoodEnd);
                fs.Flush();
                Logger.LogDebug("已裁剪 FLV 截断尾: {0} ({1} 字节)", path, len - lastGoodEnd);
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogDebug("裁剪 FLV 截断尾失败: {0}", ex.Message);
            return false;
        }
    }

    /// <summary>用 FFmpeg concat demuxer 把多个 FLV 分段合成一个文件。返回是否成功。
    /// internal 供测试注入假执行器捕获参数。</summary>
    internal static async Task<bool> ConcatSegmentsAsync(List<string> segmentFiles, string outPath, CancellationToken token)
    {
        // concat demuxer 需要逐行列出文件，FFmpeg 通过 ArgumentList 传参无法直接传换行
        // 列表——这里写一个临时 concat 列表文件。路径含特殊字符时 concat 列表需要转义，
        // 用 file '...' 单引号包裹（路径中单引号已由 SanitizeFileName 在文件生成阶段处理）。
        // 列表文件与输出都用绝对路径：自定义 --output 目录时若 CWD 与目标目录不同，
        // 相对路径的 file '...' 条目会在 concat demuxer 读取时解析失败。
        var listPath = Path.GetFullPath(outPath) + ".concat.txt";
        var absoluteOutPath = Path.GetFullPath(outPath);
        try
        {
            long totalInputBytes = 0;
            foreach (var seg in segmentFiles)
            {
                if (!File.Exists(seg))
                {
                    Logger.LogWarn($"直播分段不存在: {seg}");
                    return false;
                }
                totalInputBytes += new FileInfo(seg).Length;
            }

            if (totalInputBytes == 0) return false;

            await File.WriteAllLinesAsync(listPath, segmentFiles.Select(f => $"file '{f.Replace("'", "'\\''")}'"), token);
            var args = new List<string>
            {
                "-loglevel", "warning", "-y",
                "-f", "concat", "-safe", "0",
                "-i", listPath,
                "-c", "copy",
                absoluteOutPath,
            };
            // 复用统一外部进程执行器（与混流一致）：支持超时/取消时 Kill 整棵进程树。
            // 用 BBDownMuxer.FFMPEG：FindBinaries 会把用户 --ffmpeg-path 或 PATH 探测的
            // 路径写入该静态字段，此前硬编码 "ffmpeg" 会绕过用户的显式指定。
            var spec = new ExternalProcessSpec
            {
                FileName = BBDownMuxer.FFMPEG,
                Arguments = args,
                TimeoutMs = Core.Config.Current.MuxerTimeoutMinutes * 60_000,
                ToolDisplayName = "ffmpeg",
            };
            int code = await BBDownMuxer.ProcessRunner.RunAsync(spec, token);
            if (code != 0 || !File.Exists(absoluteOutPath))
                return false;

            long outLen = new FileInfo(absoluteOutPath).Length;
            if (outLen == 0) return false;

            // FLV concat copy 时，产物大小应与所有分段总和相当（FLV header 占 9~13 字节，多段合并时略有减少）。
            // 若某个分段遇到坏段/HTML导致 demux 提前终止，ffmpeg exit=0 但输出大小会显著小于全部输入总大小（如只生成了坏段之前的几KB）。
            // 当分段数大于1时，输出大小若低于输入总大小的 80% 且差异超过 64KB，判定为截断坏产物。
            if (segmentFiles.Count > 1)
            {
                long minExpected = (long)(totalInputBytes * 0.8);
                if (outLen < minExpected && (totalInputBytes - outLen) > 64 * 1024)
                {
                    Logger.LogWarn($"直播分段合成产物大小异常(输出: {outLen} 字节, 预期总输入: {totalInputBytes} 字节)，判定为合成截断失败");
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            Logger.LogDebug("直播分段合成失败: {0}", ex.Message);
            return false;
        }
        finally
        {
            try { if (File.Exists(listPath)) File.Delete(listPath); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 把一条直播流写到独立分段文件，返回 (本段写入的字节数, 是否以读中断结束)。
    /// <paramref name="progressBase"/> 为已录完分段的累计字节数：进度回调上报
    /// progressBase + 当前分段写入量，避免重连后进度从零跳回（此前每段从 0 起报，
    /// 用户看到的进度会在重连时倒退）。
    /// 读中断（网络 RST/EOF 异常/读停滞看门狗超时/用户取消）时把已写字节照常返回——
    /// 用户取消时上层把本段计入已录内容并合成保存，否则 Ctrl+C 会把当前分段全部丢弃。
    /// 写盘失败抛 <see cref="LiveStreamWriteException"/>（本地故障，重连无意义）。
    /// </summary>
    private static async Task<(long Written, bool ReadInterrupted)> StreamToFileAsync(string url, string segPath, long progressBase, Action<long>? onProgress, CancellationToken token = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", HTTPUtil.GetUserAgent(null));
        req.Headers.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
        // 直播流是无限连接：用专用的无超时客户端（StreamingHttpClient），而非全局
        // AppHttpClient（Timeout=2min）。实测 HttpClient.Timeout 对 ResponseHeadersRead
        // 之后的流读取不生效，但无限流主体不应携带任何客户端超时，见 HTTPUtil.StreamingHttpClient。
        // 响应头阶段单独给有限超时：TCP+TLS 已建立但服务器永不返回响应头（黑洞）时
        // SendAsync 会永久挂起。headerCts 只覆盖"发请求→收响应头"，取到响应头后立即释放
        // （Dispose 幂等），不影响下方主体流读取（读循环用 token）。
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        headerCts.CancelAfter(TimeSpan.FromMinutes(2));
        using var response = (await HTTPUtil.StreamingHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token)).EnsureSuccessStatusCode();
        headerCts.Dispose(); // 响应头已到达：释放仅覆盖头部阶段的超时
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        await using var fs = new FileStream(segPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        long written = 0;
        try
        {
            // 读停滞看门狗：网络波动时连接可能既不 RST 也不 EOF，只是不再有数据——
            // 无超时客户端上 ReadAsync 会永久挂起，录制卡死且重连逻辑永远不触发。
            // 每收到一段数据重置计时；超时未收到数据视为连接死亡，按读中断返回交上层重连。
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            stallCts.CancelAfter(ReadStallTimeout);
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), stallCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // 用户取消（token 已取消）或看门狗超时（token 未取消）：已写字节照常返回。
                    // 用户取消时上层把本段计入已录内容并在取消检查点结束录制、合成保存。
                    return (written, ReadInterrupted: true);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    // 网络读中断（URL 到期 RST/瞬断）：已写字节照常返回。调用方据此把
                    // 本段计入 total 并清零重试预算——否则连接以异常结束时已写字节丢失。
                    // 写盘失败不在此分支（WriteAsync 单独抛 LiveStreamWriteException）。
                    return (written, ReadInterrupted: true);
                }
                if (read == 0) break; // 流结束（主播下播/正常 EOF）
                stallCts.CancelAfter(ReadStallTimeout); // 收到数据：重置停滞计时
                try
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), token);
                }
                catch (OperationCanceledException)
                {
                    // 写盘被用户取消（Ctrl+C 恰逢写盘）：已写字节照常返回。文件里可能多出
                    // 半截缓冲，与网络中断截断的语义一致，concat 时按截断标签处理即可。
                    return (written, ReadInterrupted: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 本地写盘失败（磁盘满/权限/文件被占用）：不可重试的本地故障，
                    // 不能按网络瞬断处理——否则磁盘故障会陷入无限重连循环。
                    throw new LiveStreamWriteException($"本地写入失败: {segPath} ({ex.Message})", ex);
                }
                written += read;
                onProgress?.Invoke(progressBase + written);
            }
            return (written, ReadInterrupted: false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // 跨平台文件名安全：Path.GetInvalidFileNameChars 在 Unix 上只含 NUL 与 '/',
    // 但下载文件可能被复制到 Windows，因此把 Windows 的非法字符一并替换
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars()
        .Union(@"\/:*?""<>|".ToCharArray())
        .Distinct()
        .ToArray();

    /// <summary>把非法文件名字符替换为下划线，返回安全的文件名。</summary>
    public static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(InvalidFileNameChars.Contains(ch) ? '_' : ch);
        var s = sb.ToString().Trim();
        return string.IsNullOrEmpty(s) ? "直播" : s;
    }
}
