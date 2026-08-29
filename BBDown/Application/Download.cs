using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static BBDown.Core.Entity.Entity;
using BBDown.Core;
using BBDown.Core.Entity;
using System.Text;
using System.Text.Json;

using BBDown.Core.Util;
namespace BBDown;

internal partial class Program
{
    /// <summary>调试日志中 JSON 响应摘要的最大字符数（防巨响应刷屏/耗内存）。</summary>
    private const int LogJsonSummaryMaxChars = 1024;

    /// <summary>混流后短暂等待外部进程释放输出文件句柄的毫秒数（防 finally 删除轨道文件时仍被占用）。</summary>
    private const int FileHandleReleaseDelayMs = 200;

    public static async Task DownloadPagesAsync(MyOption myOption, VInfo vInfo, Dictionary<string, byte> encodingPriority, Dictionary<string, int> dfnPriority,
        string? firstEncoding, bool downloadDanmaku, BBDownDanmakuFormat[] downloadDanmakuFormats, string input, string savePathFormat, string lang, string aidOri, int delay, string apiType, DownloadTask? relatedTask = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<Page> pagesInfo = vInfo.PagesInfo;
        bool bangumi = vInfo.IsBangumi;
        bool cheese = vInfo.IsCheese;
        //获取已选择的分P列表
        List<string>? selectedPages = GetSelectedPages(myOption, vInfo, input);

        Logger.Log($"共计 {pagesInfo.Count} 个分P, 已选择：" + (selectedPages == null ? "ALL" : string.Join(",", selectedPages)));
        var pagesCount = pagesInfo.Count;

        //过滤不需要的分P
        if (selectedPages != null)
        {
            pagesInfo = pagesInfo.Where(p => selectedPages.Contains(p.index.ToString())).ToList();
        }

        // 选中的分P全部不存在（如 -p 99）时，空列表会让 foreach 空转后打印"任务完成"，
        // 脚本与 serve 客户端拿到假成功。必须显式报错中止任务。
        if (pagesInfo.Count == 0)
        {
            throw new InvalidOperationException(
                $"所选分P不存在: {(selectedPages is null ? "ALL" : string.Join(",", selectedPages))}，视频共有 {pagesCount} 个分P");
        }

        // 保存路径模板按【实际下载的分P数】决策：-p 单选 1 集时即使视频总P>1
        // 也走单P模板（-F 生效，产物不再带 [P##] 前缀）；番剧未完结时固定按多P处理。
        savePathFormat = ResolveSavePathFormat(myOption.FilePattern, myOption.MultiFilePattern, pagesInfo.Count, bangumi && !vInfo.IsBangumiEnd);

        var failedPages = new List<int>();

        // 存档以 aid 为粒度，而一个多P稿件的所有分P共享同一个 aid。
        // 若下完第一个分P就写入存档，同稿件余下的分P会在下一次循环里被
        // CheckAidFromFile 判定为"已下载"而全部跳过——收藏夹、合集、UP 主投稿
        // 这类含多P稿件的列表尤其容易踩到。因此必须等一个 aid 的分P全部成功
        // 之后再写入。
        var remainingPagesByAid = pagesInfo
            .GroupBy(p => p.aid)
            .ToDictionary(g => g.Key, g => g.Count());
        var failedAids = new HashSet<string>();

        // 计数循环而非 foreach + IndexOf：IndexOf 是 O(n)，全量分P时 O(n²) 且按值
        // 匹配重复分P会得到错误序号；序号在循环顶部递增，与 foreach 遍历一一对应。
        int pageOrdinal = 0;
        foreach (Page p in pagesInfo)
        {
            pageOrdinal++;
            if (pagesInfo.Count > 1 && delay > 0)
            {
                Logger.Log($"停顿{delay}秒...");
                await Task.Delay(delay * 1000, cancellationToken);
            }
            Logger.Log($"开始解析P{p.index}: {p.aid}... ({pageOrdinal} of {pagesInfo.Count})");

            if (myOption.SaveArchivesToFile && CheckAidFromFile(p.aid))
            {
                Logger.Log($"aid: {p.aid}已下载过, 跳过下载...");
                remainingPagesByAid[p.aid]--;
                continue;
            }

            bool succeeded;
            try
            {
                succeeded = await DownloadPageAsync(p, myOption, vInfo, pagesInfo, encodingPriority, dfnPriority, firstEncoding,
                    downloadDanmaku, downloadDanmakuFormats, input, savePathFormat, lang, aidOri, apiType, relatedTask, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException or TimeoutException or TaskCanceledException)
            {
                // 真正的用户取消/服务关停（token 已取消）必须正常中止整批，不能进失败分支续跑；
                // HTTP 超时抛的 TaskCanceledException 其 token 未取消，会进入下方"记录失败后继续"分支。
                if (cancellationToken.IsCancellationRequested) throw;
                // DownloadPageAsync 内部重试（--retry-count 次，默认 3）耗尽后抛出：若不在此接住，异常会直接跳出
                // foreach，剩余分P全部放弃下载，且完成通知（NotifyWebhook）与 failedPages
                // 汇总都不再执行——与 return false 的失败路径（记录后继续）行为不一致。
                Logger.LogError($"P{p.index} 下载失败: [{ex.GetType().Name}] {ex.Message}");
                failedPages.Add(p.index);
                continue;
            }

            if (myOption.SaveArchivesToFile)
            {
                remainingPagesByAid[p.aid]--;
                // 只要该稿件有任何一个分P失败就不入档，否则下次运行会跳过尚未下全的稿件
                if (!succeeded) failedAids.Add(p.aid);
                if (remainingPagesByAid[p.aid] == 0 && !failedAids.Contains(p.aid))
                {
                    SaveAidToFile(p.aid);
                }
            }

            if (!succeeded)
            {
                // 记录后继续处理其余分P
                failedPages.Add(p.index);
            }
        }

        // 下载任务完成通知（CLI 版回调）。失败也会通知，但调用方仍会因 failedPages 抛出而获得非零退出码。
        if (!string.IsNullOrEmpty(myOption.NotifyWebhook))
        {
            await NotifyCompletionAsync(myOption.NotifyWebhook, vInfo, failedPages.Count == 0, cancellationToken);
        }

        if (failedPages.Count > 0)
        {
            // 必须抛出：调用方据此决定退出码，serve 模式据此把任务标记为失败。
            // 此前这里只打印"任务完成"，失败对脚本与 API 客户端完全不可见。
            throw new InvalidOperationException(
                $"共 {failedPages.Count} 个分P下载失败：P{string.Join(", P", failedPages)}");
        }

        Logger.Log("任务完成");
    }

    /// <summary>
    /// 下载任务完成回调：向用户配置的 webhook POST 任务结果。
    /// 失败只降级为日志，不影响下载流程本身。
    /// </summary>
    private static async Task NotifyCompletionAsync(string webhook, VInfo vInfo, bool success, CancellationToken token)
    {
        try
        {
            var payload = new NotifyPayload(
                vInfo.Title,
                vInfo.PagesInfo.Count,
                success ? "completed" : "completed-with-failures",
                DateTimeOffset.Now.ToUnixTimeSeconds());
            var json = JsonSerializer.Serialize(payload, MyOptionJsonContext.Default.NotifyPayload);
            using var req = new HttpRequestMessage(HttpMethod.Post, webhook)
            {
                Content = new StringContent(json, Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")),
            };
            using var resp = await HTTPUtil.AppHttpClient.SendAsync(req, token);
            if (!resp.IsSuccessStatusCode)
                Logger.LogWarn($"通知回调返回 HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            // 回调必须不影响下载结果：URL 畸形（UriFormatException）或内容构造失败都不能把成功任务变失败。
            // 但真正的用户取消必须向上传播：HttpClient 超时抛的 TaskCanceledException 其 token 未取消，
            // 按失败降级；token 已取消时吞掉会让"任务完成"在 Ctrl+C 后仍被打印，取消信号丢失。
            if (token.IsCancellationRequested) throw;
            Logger.LogDebug("通知回调失败: {0}", ex.Message);
        }
    }

    /// <summary>
    /// 下载单个分P。返回 false 表示该分P最终失败——调用方据此避免把它记为已完成，
    /// 并让整体任务状态反映失败，而不是照常报告"任务完成"。
    /// </summary>
    /// <summary>
    /// 计算角色配音轨的安全下标：主音轨的 aIndex 只对 AudioTracks.Count 校验过，
    /// 每个 role 的 audio 列表更小，越界抛 ArgumentOutOfRangeException 且不在下载重试的
    /// catch 过滤内，会直接中止整批下载。返回 -1 表示该 role 没有可用音频（调用方跳过）。
    /// </summary>
    internal static int ClampRoleAudioIndex(int aIndex, int audioCount)
        => audioCount <= 0 ? -1 : Math.Min(Math.Max(aIndex, 0), audioCount - 1);

    /// <summary>最终路径锁内临界区的结果：跳过（文件已存在）、成功、失败。</summary>
    private enum MuxOutcome
    {
        Skipped,
        Succeeded,
        Failed,
    }

    /// <summary>
    /// 混流 + 产物校验 + 临时文件清理，作为最终路径独占锁内的临界区。
    /// 拆出独立方法使调用点能用 <see cref="BBDownDownloadUtil.RunWithPathLockAsync{T}"/>
    /// 对 savePath 加锁：防止 serve 下两个同标题任务同时写同一个最终输出文件。
    /// 锁内先做权威的存在性检查：即使两个任务都通过了锁外的快速跳过判定并各自下载，
    /// 到锁内这一步时，若文件已存在（另一个任务刚写完），则跳过而非覆盖。
    /// </summary>
    private static async Task<MuxOutcome> MuxAndFinalizeAsync(bool useMp4box, MyOption myOption, Page p, VInfo vInfo, List<Page> selectedPagesInfo, ParsedResult parsedResult,
        string desc, string title, string coverPath, string lang,
        List<Subtitle> subtitleInfo, List<AudioMaterial> audioMaterial, string videoPath, string audioPath, string savePath, bool isHevc,
        bool videoOnly, bool audioOnly, bool bangumi, bool fastSkipChecked, DownloadTask? relatedTask, CancellationToken cancellationToken)
    {
        // 锁内权威判定：文件已存在（可能是另一个任务刚写完成，或本次下载期间被跳过判定
        // 的其它任务写入了）→ 跳过，不覆盖。
        if (fastSkipChecked && File.Exists(savePath) && new FileInfo(savePath).Length != 0)
        {
            Logger.Log($"{savePath}已存在, 跳过下载...");
            relatedTask?.AddSavePath(savePath);
            // 清理当前任务已下载的临时音视频/字幕文件，防止并发败者任务残留泄漏
            if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath)) try { File.Delete(videoPath); } catch { }
            if (!string.IsNullOrEmpty(audioPath) && File.Exists(audioPath)) try { File.Delete(audioPath); } catch { }
            foreach (var s in subtitleInfo) if (File.Exists(s.path)) try { File.Delete(s.path); } catch { }
            foreach (var a in audioMaterial) if (File.Exists(a.path)) try { File.Delete(a.path); } catch { }
            // 章节 meta 文件同样可能由本次或上次任务残留（见 DeleteResidualChapterFiles），
            // 跳过路径一并兜底清理，避免重跑已下载视频时 aid 目录残留章节文件。
            DeleteResidualChapterFiles(PathUtil.ResolveWorkPath(p.aid));
            var aidDir = PathUtil.ResolveWorkPath(p.aid);
            if (Directory.Exists(aidDir) && !Directory.EnumerateFileSystemEntries(aidDir).Any())
            {
                try { Directory.Delete(aidDir, true); } catch { }
            }
            return MuxOutcome.Skipped;
        }
        // 混流产物事务化：ffmpeg/mp4box 写入唯一的 .muxing-{guid} 临时路径而非最终 savePath。
        // 此前直接写最终路径，非零退出/取消后只返回 Failed 不删除半成品，下次运行发现
        // "存在且非空"就跳过，可能永久保留截断视频并报告成功。临时产物成功且校验通过后
        // 才原子替换到最终路径；失败/取消清理临时产物，绝不留半成品当成品。
        // 轨道文件（已下载的音视频/字幕）清理必须覆盖"成功/失败/异常"全部路径：
        // 此前只在成功路径清理，混流失败或 MuxAV 抛异常（超时/取消/进程失败）时，
        // GB 级的视频/音频轨道文件残留在 aid 工作目录，多 P 批量反复失败会累积大量磁盘占用。
        // 因此这里把轨道清理并入 finally（见 CleanupDownloadedTracks），与 .muxing-*
        // 临时产物一并兜底。
        var muxingPath = savePath + $".muxing-{Guid.NewGuid():N}";
        bool muxSucceeded = false;
        try
        {
            int code = await BBDownMuxer.MuxAV(useMp4box, p.bvid, videoPath, audioPath, audioMaterial, muxingPath,
                desc,
                title,
                p.ownerName ?? "",
                (selectedPagesInfo.Count > 1 || (bangumi && !vInfo.IsBangumiEnd)) ? p.title : "",
                File.Exists(coverPath) ? coverPath : "",
                lang,
                subtitleInfo, audioOnly, videoOnly, p.points, p.pubTime, myOption.SimplyMux, isHevc, cancellationToken);
            if (code != 0 || !File.Exists(muxingPath) || new FileInfo(muxingPath).Length == 0)
            {
                // 混流失败/取消：返回失败（finally 会清理半成品临时产物与已下载轨道）
                return MuxOutcome.Failed;
            }
            // 产物合法：原子替换到最终路径。File.Move(overwrite) 在 Windows 上是"删除目标+改名"，
            // 但目标原本不存在（fastSkipChecked 已判定），不会破坏已有文件。
            File.Move(muxingPath, savePath, true);
            muxingPath = null; // 已移动成功，finally 无需清理
            muxSucceeded = true;
            Logger.Log("清理临时文件...");
            // 短暂等待外部进程释放输出文件句柄后，finally 再删除轨道文件。
            // 取消在这里正常传播（不吞 OCE）：混流已成功、产物已保存，轨道可以清理，
            // 但取消必须中止整批下载，而不是继续处理剩余分 P。
            await Task.Delay(FileHandleReleaseDelayMs, cancellationToken);
        }
        finally
        {
            // 取消/超时/失败后清理仍存在的临时产物：此路径覆盖 MuxAV 抛异常（await 之后的
            // 清理代码无法执行的场景），避免遗留 .muxing-* 大文件持续占用磁盘。
            if (muxingPath is not null)
            {
                try { if (File.Exists(muxingPath)) File.Delete(muxingPath); }
                catch (IOException) { /* 清理失败不影响主流程 */ }
            }
            // 轨道文件清理纳入 finally：成功/失败/异常（MuxAV 抛超时/取消/进程失败）都执行，
            // 杜绝混流失败时已下载的 GB 级音视频/字幕文件残留在 aid 工作目录。
            CleanupDownloadedTracks(parsedResult, p, videoPath, audioPath, subtitleInfo, audioMaterial, selectedPagesInfo, coverPath);
        }
        return muxSucceeded ? MuxOutcome.Succeeded : MuxOutcome.Failed;
    }

    /// <summary>
    /// 清理已下载但未被混流消耗的轨道临时文件（视频/音频/字幕/封面/章节），并删除
    /// 变空的 aid 工作目录。供 <see cref="MuxAndFinalizeAsync"/> 的 finally 兜底调用，
    /// 保证混流成功、失败或抛异常（超时/取消）路径都不残留大文件。
    /// 单文件清理失败不影响整体（磁盘占用/句柄异常不该掩盖主流程结果）。
    /// </summary>
    private static void CleanupDownloadedTracks(ParsedResult parsedResult, Page p, string videoPath, string audioPath,
        List<Subtitle> subtitleInfo, List<AudioMaterial> audioMaterial, List<Page> selectedPagesInfo, string coverPath)
    {
        if (parsedResult.VideoTracks.Any() && File.Exists(videoPath))
        {
            try { File.Delete(videoPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // 仅删除非空音轨路径：flv 分支 audioPath 为空串，File.Delete("") 会抛异常。
        if (!string.IsNullOrEmpty(audioPath) && parsedResult.AudioTracks.Any() && File.Exists(audioPath))
        {
            try { File.Delete(audioPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (p.points.Any())
        {
            var dir = Path.GetDirectoryName(string.IsNullOrEmpty(videoPath) ? audioPath : videoPath);
            if (dir is not null)
            {
                DeleteResidualChapterFiles(dir);
            }
        }
        foreach (var s in subtitleInfo)
        {
            try { if (File.Exists(s.path)) File.Delete(s.path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var a in audioMaterial)
        {
            try { if (File.Exists(a.path)) File.Delete(a.path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (selectedPagesInfo.Count == 1 || p.index == selectedPagesInfo.Last().index || p.aid != selectedPagesInfo.Last().aid)
        {
            try { if (File.Exists(coverPath)) File.Delete(coverPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var aidDir = PathUtil.ResolveWorkPath(p.aid);
        if (Directory.Exists(aidDir))
        {
            try { if (Directory.GetFiles(aidDir).Length == 0) Directory.Delete(aidDir, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 删除目录下残留的章节 meta 文件。muxer 按输出文件名派生唯一名
    /// （chapters-{basename}，见 BBDownMuxer，防并发混流互相覆盖），早期版本与
    /// 部分清理路径用固定名 "chapters"；这里按前缀匹配两者，跳过/失败路径兜底清理。
    /// 单文件清理失败不影响整体（磁盘占用/句柄异常不该掩盖主流程结果）。
    /// internal 供测试直接验证前缀匹配清理行为。
    /// </summary>
    internal static void DeleteResidualChapterFiles(string dir)
    {
        try
        {
            foreach (var f in Directory.GetFiles(dir, "chapters*"))
            {
                try { File.Delete(f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static async Task<bool> DownloadPageAsync(Page p, MyOption myOption, VInfo vInfo, List<Page> selectedPagesInfo, Dictionary<string, byte> encodingPriority, Dictionary<string, int> dfnPriority,
        string? firstEncoding, bool downloadDanmaku, BBDownDanmakuFormat[] downloadDanmakuFormats, string input, string savePathFormat, string lang, string aidOri, string apiType, DownloadTask? relatedTask = null, CancellationToken cancellationToken = default)
    {
        string desc = string.IsNullOrEmpty(p.desc) ? vInfo.Desc : p.desc;
        bool bangumi = vInfo.IsBangumi;
        // 补零宽度用"全部分P总数"而非筛选后的数量：单独下载 P1（-p 1）与稍后下载
        // 全部分P（-p all）时，<pageNumberWithZero> 应产生相同宽度的文件名，
        // 否则同一视频因筛选方式不同会得到不同路径（P01 vs P1）。
        var pagesCount = vInfo.PagesInfo.Count;
        // 产物成功判定：是否实际生成了至少一个请求的输出产物。
        // SubOnly/VideoOnly/AudioOnly/DanmakuOnly 等提前返回模式若零产物，
        // 必须返回 false 而非假成功（否则 CLI/Serve 报成功、SavePaths 为空）。
        bool anyProductProduced = false;
        List<Subtitle> subtitleInfo = [];
        string title = vInfo.Title;
        string pic = vInfo.Pic;
        long pubTime = vInfo.PubTime;
        bool selected = false; //用户是否已经手动选择过了轨道
        int retryCount = 0;
        // 页面级重试次数与间隔尊重 --retry-count / --retry-delay（Options.cs 已校验
        // 1~100 / 0~600000）：此前硬编码 3 与 3000ms，用户配置完全被无视。
        int maxRetry = myOption.RetryCount;
        try
        {
            while (retryCount < maxRetry)
            {
                try
                {
                    Logger.LogDebug("尝试获取章节信息...");
                    p.points = await BBDownUtil.FetchPointsAsync(p.cid, p.aid, cancellationToken);

                    // 工作区路径（分P 的 aid 目录）统一基于任务流工作目录解析为绝对路径：
                    // serve 下不写进程 CWD，相对路径必须经 PathUtil.ResolveWorkPath 落到
                    // Config.Current.WorkDir，否则并发任务各自 --work-dir 的文件会互相错位。
                    string videoPath = PathUtil.ResolveWorkPath($"{p.aid}/{p.aid}.P{p.index}.{p.cid}.mp4");
                    string audioPath = PathUtil.ResolveWorkPath($"{p.aid}/{p.aid}.P{p.index}.{p.cid}.m4a");
                    var coverPath = PathUtil.ResolveWorkPath($"{p.aid}/{p.aid}.jpg");

                    //处理文件夹以.结尾导致的异常情况
                    if (title.EndsWith('.')) title += "_fix";
                    //处理文件夹以.开头导致的异常情况
                    if (title.StartsWith('.')) title = "_" + title;

                    //处理封面&&字幕
                    if (!myOption.OnlyShowInfo)
                    {
                        var workAidDir = PathUtil.ResolveWorkPath(p.aid);
                        if (!Directory.Exists(workAidDir))
                        {
                            Directory.CreateDirectory(workAidDir);
                        }
                        if (!myOption.SkipCover && !myOption.SubOnly && !File.Exists(coverPath) && !myOption.DanmakuOnly && !myOption.CoverOnly)
                        {
                            // 封面是装饰性资源：下载失败只降级为警告，不应进入页面重试循环
                            // 拖垮整批下载（与下方评论/webhook 的"非关键副作用降级"一致）。
                            try
                            {
                                await BBDownDownloadUtil.DownloadFileAsync(pic == "" ? p.cover! : pic, coverPath, new BBDownDownloadUtil.DownloadConfig(), cancellationToken);
                            }
                            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                            {
                                // 真正的取消（token 已取消）必须向上传播中止下载，不能当"封面失败（已跳过）"
                                // 吞掉后继续执行字幕等无可取消的网络调用。HttpClient 超时抛的
                                // TaskCanceledException 其 token 未取消，仍按封面降级处理。
                                if (cancellationToken.IsCancellationRequested) throw;
                                Logger.LogWarn($"封面下载失败（已跳过）: {ex.Message}");
                            }
                        }

                        if (!myOption.SkipSubtitle && !myOption.DanmakuOnly && !myOption.CoverOnly)
                        {
                            Logger.LogDebug("获取字幕...");
                            subtitleInfo = await SubUtil.GetSubtitlesAsync(p.aid, p.cid, p.epid, p.index, myOption.UseIntlApi, cancellationToken);
                            if (myOption.SkipAi && subtitleInfo.Any())
                            {
                                Logger.Log($"跳过下载AI字幕");
                                subtitleInfo = subtitleInfo.Where(s => !s.lan.StartsWith("ai-")).ToList();
                            }
                            var downloadedSubtitles = new List<Subtitle>();
                            foreach (Subtitle s in subtitleInfo)
                            {
                                Logger.Log($"下载字幕 {s.lan} => {SubUtil.GetSubtitleCode(s.lan).Item2}...");
                                Logger.LogDebug("下载：{0}", s.url);
                                // 字幕是装饰性资源：任何下载失败（含过期签名 URL 返回 200+HTML
                                // 风控页）只降级为警告并跳过该条，绝不进入页面级重试或中止整批。
                                // SubOnly 模式下字幕是唯一产物，失败应抛出交由页面级重试恢复。
                                if (!await TryDownloadSubtitleAsync(s, cancellationToken, degradeOnFailure: !myOption.SubOnly))
                                    continue;
                                downloadedSubtitles.Add(s);
                                if (myOption.SubOnly && File.Exists(s.path) && File.ReadAllText(s.path) != "")
                                {
                                    var _outSubPath = PathUtil.ResolveWorkPath(FormatSavePath(savePathFormat, title, null, null, p, pagesCount, apiType, pubTime));
                                    var dir = Path.GetDirectoryName(_outSubPath);
                                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                        Directory.CreateDirectory(dir);
                                    _outSubPath = Path.ChangeExtension(_outSubPath, $".{s.lan}.srt");
                                    File.Move(s.path, _outSubPath, true);
                                    // 记录最终产物：SubOnly 提前返回不经过下方统一 AddSavePath，
                                    // 若这里不记录，serve API 的成功响应里产物列表会缺字幕文件。
                                    relatedTask?.AddSavePath(_outSubPath);
                                    anyProductProduced = true;
                                }
                            }
                            // 只把成功落盘的字幕交给混流/清理：失败的字幕不参与，避免 mux 嵌入
                            // 不存在的文件或按不存在的路径清理。
                            subtitleInfo = downloadedSubtitles;
                        }

                        if (myOption.SubOnly)
                        {
                            if (Directory.Exists(PathUtil.ResolveWorkPath(p.aid)) && Directory.GetFiles(PathUtil.ResolveWorkPath(p.aid)).Length == 0) Directory.Delete(PathUtil.ResolveWorkPath(p.aid), true);
                            // SubOnly 但没有任何字幕生成（视频无字幕/全部跳过）→ 零产物成功。
                            // 必须返回 false，避免 CLI 报成功、serve 标记 Succeeded、SavePaths 为空。
                            if (!anyProductProduced)
                            {
                                Logger.LogWarn("SubOnly 模式未生成任何字幕文件");
                                return false;
                            }
                            return true;
                        }
                    }

                    //调用解析
                    ParsedResult parsedResult = await Parser.ExtractTracksAsync(aidOri, p.aid, p.cid, p.epid, myOption.UseTvApi, myOption.UseIntlApi, myOption.UseAppApi, firstEncoding!, myOption.DecryptDrm, token: cancellationToken);
                    List<AudioMaterial> audioMaterial = [];
                    if (!p.points.Any())
                    {
                        p.points = parsedResult.ExtraPoints;
                    }

                    // 充电专属视频：接口对无权限身份照常返回 code=0 且谎报完整时长，
                    // 只把完整流悄悄换成试看片段。必须在开始下载前拦下，
                    // 否则会产出一个被报告为"下载成功"的残片。
                    var previewVerdict = UpowerGuard.Inspect(
                        vInfo.IsUpowerExclusive, vInfo.IsUpowerPlay, p.dur, parsedResult.ActualDurationSec);
                    if (previewVerdict.IsPreview)
                    {
                        Logger.LogWarn("========================================");
                        Logger.LogWarn("  充电专属视频");
                        Logger.LogWarn($"  {previewVerdict.Reason}");
                        if (!myOption.AllowPreview && !myOption.OnlyShowInfo)
                        {
                            Logger.LogWarn("  已跳过。如需下载试看片段，请加 --allow-preview");
                            Logger.LogWarn("========================================");
                            return false;
                        }
                        if (myOption.OnlyShowInfo)
                        {
                            // 仅解析模式不落盘，放行但要说清下面列出的流属于试看片段
                            Logger.LogWarn("  仅解析模式，以下流信息对应的是试看片段");
                            Logger.LogWarn("========================================");
                        }
                        else
                        {
                            Logger.LogWarn("  已启用 --allow-preview，将下载试看片段");
                            Logger.LogWarn("========================================");

                            // 标记在标题上而非拼接到最终路径：<videoTitle> 是所有产物(视频/封面/弹幕)
                            // 共用的占位符，改这里能一次覆盖 dash 与 flv 两条保存路径，
                            // 也不会破坏用户自定义的 --file-pattern。
                            if (!title.StartsWith("[试看]"))
                                title = $"[试看]{title}";
                        }
                    }

                    if (Config.Current.DebugLog)
                    {
                        // debug 文件也落在任务工作目录：serve 下各任务的调试输出互不混杂
                        var debugFile = PathUtil.ResolveWorkPath($"debug_{DateTime.Now:yyyyMMddHHmmssfff}.json");
                        File.WriteAllText(debugFile, parsedResult.WebJsonString);
                        // 限制 debug 文件数量，保留最近 20 个
                        var debugFiles = Directory.GetFiles(PathUtil.ResolveWorkPath("."), "debug_*.json").Order().ToArray();
                        for (int i = 0; i < debugFiles.Length - 20; i++)
                            File.Delete(debugFiles[i]);
                    }

                    var savePath = "";

                    var downloadConfig = new BBDownDownloadUtil.DownloadConfig()
                    {
                        UseAria2c = myOption.UseAria2c,
                        Aria2cArgs = myOption.Aria2cArgs,
                        ForceHttp = myOption.ForceHttp,
                        MultiThread = myOption.MultiThread,
                        RelatedTask = relatedTask,
                    };

                    //此处代码简直灾难, 后续优化吧
                    if ((parsedResult.VideoTracks.Any() || parsedResult.AudioTracks.Any()) && !parsedResult.Clips.Any())   //dash
                    {
                        if (parsedResult.VideoTracks.Count == 0)
                        {
                            Logger.LogWarn("没有找到符合要求的视频流");
                            // VideoOnly 但没有任何视频流 → 零产物：返回 false 而非假成功
                            if (myOption.VideoOnly) return false;
                        }
                        if (parsedResult.AudioTracks.Count == 0)
                        {
                            Logger.LogWarn("没有找到符合要求的音频流");
                            // AudioOnly 但没有任何音频流 → 零产物：返回 false 而非假成功
                            if (myOption.AudioOnly) return false;
                        }

                        if (myOption.AudioOnly)
                        {
                            parsedResult.VideoTracks.Clear();
                        }
                        if (myOption.VideoOnly)
                        {
                            parsedResult.AudioTracks.Clear();
                            parsedResult.BackgroundAudioTracks.Clear();
                            parsedResult.RoleAudioList.Clear();
                        }

                        //排序
                        parsedResult.VideoTracks = SortTracks(parsedResult.VideoTracks, dfnPriority, encodingPriority, myOption.VideoAscending);
                        parsedResult.AudioTracks = SortTracks(parsedResult.AudioTracks, encodingPriority, myOption.AudioAscending);
                        parsedResult.BackgroundAudioTracks = SortTracks(parsedResult.BackgroundAudioTracks, encodingPriority, myOption.AudioAscending);
                        foreach (var role in parsedResult.RoleAudioList)
                        {
                            role.audio = SortTracks(role.audio, encodingPriority, myOption.AudioAscending);
                        }

                        //打印轨道信息
                        if (!myOption.HideStreams)
                        {
                            PrintAllTracksInfo(parsedResult, p.dur, myOption.OnlyShowInfo);
                        }

                        //仅展示 跳过下载
                        if (myOption.OnlyShowInfo)
                        {
                            return true;
                        }

                        int vIndex = 0; //用户手动选择的视频序号
                        int aIndex = 0; //用户手动选择的音频序号

                        //选择轨道
                        if (myOption.Interactive && !selected)
                        {
                            SelectTrackManually(parsedResult, ref vIndex, ref aIndex);
                            selected = true;
                        }

                        Video? selectedVideo = parsedResult.VideoTracks.ElementAtOrDefault(vIndex);
                        Audio? selectedAudio = parsedResult.AudioTracks.ElementAtOrDefault(aIndex);
                        Audio? selectedBackgroundAudio = parsedResult.BackgroundAudioTracks.ElementAtOrDefault(aIndex);

                        Logger.LogDebug("Format Before: " + savePathFormat);
                        savePath = PathUtil.ResolveWorkPath(FormatSavePath(savePathFormat, title, selectedVideo, selectedAudio, p, pagesCount, apiType, pubTime));
                        Logger.LogDebug("Format After: " + savePath);

                        if (downloadDanmaku)
                        {
                            var danmakuXmlPath = Path.ChangeExtension(savePath, ".xml");
                            var danmakuAssPath = Path.ChangeExtension(savePath, ".ass");
                            Logger.Log("正在下载弹幕Xml文件");
                            var danmakuUrl = $"https://comment.bilibili.com/{p.cid}.xml";
                            await BBDownDownloadUtil.DownloadFileAsync(danmakuUrl, danmakuXmlPath, downloadConfig, cancellationToken);
                            var danmakus = DanmakuUtil.ParseXml(danmakuXmlPath);
                            if (danmakus == null)
                            {
                                Logger.Log("弹幕Xml解析失败, 删除Xml...");
                                File.Delete(danmakuXmlPath);
                            }
                            else if (danmakus.Length == 0)
                            {
                                Logger.Log("当前视频没有弹幕, 删除Xml...");
                                File.Delete(danmakuXmlPath);
                            }
                            else if (downloadDanmakuFormats.Contains(BBDownDanmakuFormat.Ass))
                            {
                                var filtered = DanmakuUtil.Filter(danmakus, myOption.DanmakuFilter, myOption.DanmakuFilterUser);
                                if (filtered.Length == 0)
                                {
                                    Logger.Log("过滤后没有剩余弹幕, 跳过Ass保存");
                                }
                                else
                                {
                                    Logger.Log($"正在保存弹幕Ass文件{(filtered.Length < danmakus.Length ? $"(过滤掉 {danmakus.Length - filtered.Length} 条)" : "")}...");
                                    await DanmakuUtil.SaveAsAssAsync(filtered, danmakuAssPath);
                                }
                            }

                            // delete xml if possible
                            if (!downloadDanmakuFormats.Contains(BBDownDanmakuFormat.Xml) && File.Exists(danmakuXmlPath))
                            {
                                File.Delete(danmakuXmlPath);
                            }

                            if (myOption.DanmakuOnly)
                            {
                                // 记录最终产物：DanmakuOnly 提前返回不经过下方统一 AddSavePath，
                                // 若这里不记录，serve API 的成功响应里产物列表会缺弹幕文件。
                                bool danmakuProduced = false;
                                if (downloadDanmakuFormats.Contains(BBDownDanmakuFormat.Xml) && File.Exists(danmakuXmlPath))
                                {
                                    relatedTask?.AddSavePath(danmakuXmlPath);
                                    danmakuProduced = true;
                                }
                                if (downloadDanmakuFormats.Contains(BBDownDanmakuFormat.Ass) && File.Exists(danmakuAssPath))
                                {
                                    relatedTask?.AddSavePath(danmakuAssPath);
                                    danmakuProduced = true;
                                }
                                // 只清理空目录：非空意味着存在上次中断留下的可续传分片
                                // （.vclip/.aclip 等，设计上保留）或并发任务的产物，
                                // 递归删除会把它们一起毁掉（与 SubOnly/CoverOnly 同一守卫语义）。
                                if (Directory.Exists(PathUtil.ResolveWorkPath(p.aid)) && Directory.GetFiles(PathUtil.ResolveWorkPath(p.aid)).Length == 0)
                                {
                                    try { Directory.Delete(PathUtil.ResolveWorkPath(p.aid), true); } catch (IOException) { }
                                }
                                // DanmakuOnly 但没有任何有效弹幕文件（解析失败/为空/过滤后为空被删除）
                                // → 零产物：返回 false 而非假成功
                                if (!danmakuProduced)
                                {
                                    Logger.LogWarn("DanmakuOnly 模式未生成任何弹幕文件");
                                    return false;
                                }
                                return true;
                            }
                        }

                        if (myOption.CoverOnly)
                        {
                            // 仅下载封面：封面保存成功后必须立即 return，否则会继续执行下方
                            // 轨道解析、视频/音频下载与混流——用户只要封面却白白下载完整视频。
                            var coverUrl = pic == "" ? p.cover! : pic;
                            // coverUrl 为空时 DownloadFileAsync 直接返回（不生成文件）：
                            // 此时若仍 AddSavePath 并 return true，SavePaths 指向不存在的文件。
                            // 无封面资源时明确失败，避免零产物成功。
                            if (string.IsNullOrEmpty(coverUrl))
                            {
                                Logger.LogWarn("CoverOnly 模式无封面资源可下载");
                                return false;
                            }
                            var newCoverPath = Path.ChangeExtension(savePath, Path.GetExtension(coverUrl));
                            await BBDownDownloadUtil.DownloadFileAsync(coverUrl, newCoverPath, downloadConfig, cancellationToken);
                            if (Directory.Exists(PathUtil.ResolveWorkPath(p.aid)) && Directory.GetFiles(PathUtil.ResolveWorkPath(p.aid)).Length == 0) Directory.Delete(PathUtil.ResolveWorkPath(p.aid), true);
                            relatedTask?.AddSavePath(newCoverPath);
                            return true;
                        }

                        Logger.Log($"已选择的流:");
                        PrintSelectedTrackInfo(selectedVideo, selectedAudio, p.dur);

                        //用户开启了强制替换
                        if (myOption.ForceReplaceHost && string.IsNullOrEmpty(myOption.UposHost))
                        {
                            myOption.UposHost = BACKUP_HOST;
                        }

                        //处理PCDN
                        HandlePcdn(myOption, selectedVideo, selectedAudio);

                        if (!myOption.OnlyShowInfo && File.Exists(savePath) && new FileInfo(savePath).Length != 0)
                        {
                            Logger.Log($"{savePath}已存在, 跳过下载...");
                            relatedTask?.AddSavePath(savePath);
                            File.Delete(coverPath);
                            // 清理本次已下载但未被消费的装饰性文件（字幕/章节）：它们下载于
                            // 跳过判定之前（GetSubtitlesAsync 在提取轨道前执行），若不清理，
                            // 每次重跑已下载的视频都会残留字幕/章节文件，且 Directory 非空
                            // 时 aid 目录也删不掉。与 flv 分支的跳过清理行为保持一致。
                            foreach (var s in subtitleInfo)
                            {
                                try { if (File.Exists(s.path)) File.Delete(s.path); }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                            }
                            DeleteResidualChapterFiles(PathUtil.ResolveWorkPath(p.aid));
                            if (Directory.Exists(PathUtil.ResolveWorkPath(p.aid)) && Directory.GetFiles(PathUtil.ResolveWorkPath(p.aid)).Length == 0)
                            {
                                Directory.Delete(PathUtil.ResolveWorkPath(p.aid), true);
                            }
                            return true;
                        }

                        if (selectedVideo != null)
                        {
                            //杜比视界, 若ffmpeg版本小于5.0, 使用mp4box封装
                            if (selectedVideo.dfn == AppSettings.QualityMap["126"] && !myOption.UseMP4box && !ExternalToolHelper.CheckFFmpegDOVI())
                            {
                                Logger.LogWarn($"检测到杜比视界清晰度且您的ffmpeg版本小于5.0,将使用mp4box混流...");
                                myOption.UseMP4box = true;
                            }
                            Logger.Log($"开始下载P{p.index}视频...");
                            await DownloadTrackAsync(selectedVideo.baseUrl, videoPath, downloadConfig, video: true, cancellationToken);
                        }

                        if (selectedAudio != null)
                        {
                            Logger.Log($"开始下载P{p.index}音频...");
                            await DownloadTrackAsync(selectedAudio.baseUrl, audioPath, downloadConfig, video: false, cancellationToken);
                        }

                        if (selectedBackgroundAudio != null)
                        {
                            var backgroundPath = PathUtil.ResolveWorkPath($"{p.aid}/{p.aid}.{p.cid}.P{p.index}.back_ground.m4a");
                            Logger.Log($"开始下载P{p.index}背景配音...");
                            await DownloadTrackAsync(selectedBackgroundAudio.baseUrl, backgroundPath, downloadConfig, video: false, cancellationToken);
                            audioMaterial.Add(new AudioMaterial("背景音频", "", backgroundPath));
                        }

                        if (parsedResult.RoleAudioList.Any())
                        {
                            foreach (var role in parsedResult.RoleAudioList)
                            {
                                // aIndex 只对 AudioTracks.Count 校验过，而每个 role 的 audio 是独立列表
                                //（通常只有 1-2 个清晰度），主列表的序号可能越界。越界会抛
                                // ArgumentOutOfRangeException 且不在下载重试的 catch 过滤内，直接中止整批。
                                int roleIdx = ClampRoleAudioIndex(aIndex, role.audio.Count);
                                if (roleIdx < 0) continue;
                                var roleAudio = role.audio[roleIdx];
                                Logger.Log($"开始下载P{p.index}配音[{role.title}]...");
                                await DownloadTrackAsync(roleAudio.baseUrl, role.path, downloadConfig, video: false, cancellationToken);
                                audioMaterial.Add(new AudioMaterial(role));
                            }
                        }

                        Logger.Log($"下载P{p.index}完毕");

                        if (myOption.DownloadComments && p.index == 1 && long.TryParse(p.aid, out var commentAid))
                        {
                            // 评论是附加功能：任何失败都只降级为警告，绝不能触发页面级重试
                            // 或中止整批（页面级 try 会把评论异常误判为下载失败而重下已混流的视频）
                            try
                            {
                                var commentsPath = Path.ChangeExtension(savePath, ".comments.json");
                                Logger.Log("正在下载评论...");
                                var commentPage = await CommentUtil.FetchAsync(commentAid, token: cancellationToken);
                                await CommentUtil.SaveToJsonAsync(commentPage.Items, commentsPath);
                                Logger.Log($"评论已保存: {commentsPath} ({commentPage.Items.Count} 条)");
                                // 达到分页上限仍有更多评论：明确提示结果不完整，避免用户误以为已抓全
                                if (commentPage.Truncated)
                                {
                                    Logger.LogWarn($"评论数量达到抓取上限（{commentPage.Items.Count} 条），可能还有更多评论未导出");
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                // 用户取消：必须向上传播，不能当"评论失败已跳过"吞掉——
                                // 否则取消信号丢失，后续 SkipMux 等分支仍返回成功。
                                throw;
                            }
                            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                                        or IOException or TaskCanceledException or KeyNotFoundException or FormatException)
                            {
                                Logger.LogWarn($"评论下载失败（已跳过）: {ex.Message}");
                            }
                        }

                        if (parsedResult.IsDrm && myOption.DecryptDrm && (!string.IsNullOrEmpty(parsedResult.KidHex) || !string.IsNullOrEmpty(parsedResult.PsshBase64)))
                        {
                            await DecryptDrmAsync(parsedResult, videoPath, audioPath, myOption, cancellationToken);
                        }

                        if (!parsedResult.VideoTracks.Any()) videoPath = "";
                        if (!parsedResult.AudioTracks.Any()) audioPath = "";
                        if (myOption.SkipMux)
                        {
                            // 记录原始轨道产物：SkipMux 跳过混流，返回前若不记录 SavePaths，
                            // serve API 的成功响应里产物列表会缺本次下载的裸音视频流。
                            if (File.Exists(videoPath)) relatedTask?.AddSavePath(videoPath);
                            if (File.Exists(audioPath)) relatedTask?.AddSavePath(audioPath);
                            foreach (var a in audioMaterial) if (File.Exists(a.path)) relatedTask?.AddSavePath(a.path);
                            return true;
                        }
                        Logger.Log($"开始合并音视频{(subtitleInfo.Any() ? "和字幕" : "")}...");
                        if (myOption.AudioOnly)
                            // 用 Path.ChangeExtension 而非 savePath[..^4] 魔法切片：虽然后者在
                            // FormatSavePath 保证 .mp4 后缀下不会截错，但魔法 4 字符切片脆弱且
                            // 难读；ChangeExtension 按真实扩展名替换，语义清晰更稳健。
                            savePath = Path.ChangeExtension(savePath, ".m4a");

                        var isHevc = selectedVideo?.codecs == "HEVC";
                        // 最终路径独占锁：serve 下两个不同 Aid、相同标题的任务会解析出同一个
                        // savePath（默认单文件模板是 <videoTitle>）。锁内完成"存在性判定 → 混流 →
                        // 校验 → 清理"：即使两个任务都通过了上面的快速跳过判定并各自下载到临时路径，
                        // 到锁内这一步时若文件已存在（另一个任务先写完），也会跳过而非覆盖。
                        var muxOutcome = await BBDownDownloadUtil.RunWithPathLockAsync(savePath,
                            () => MuxAndFinalizeAsync(myOption.UseMP4box, myOption, p, vInfo, selectedPagesInfo, parsedResult, desc, title, coverPath, lang, subtitleInfo, audioMaterial,
                                videoPath, audioPath, savePath, isHevc, videoOnly: false, audioOnly: myOption.AudioOnly,
                                bangumi, fastSkipChecked: true, relatedTask, cancellationToken),
                            cancellationToken);
                        if (muxOutcome == MuxOutcome.Failed)
                        {
                            Logger.LogError("合并失败"); return false;
                        }
                    }
                    else if (parsedResult.Clips.Any() && parsedResult.Dfns.Any())   //flv
                    {
                        if (myOption.DecryptDrm)
                        {
                            Logger.LogError("此视频需要大会员登录才能获取完整DRM内容。");
                            Logger.LogError($"请先运行: BBDown login  或使用 --cookie 参数");
                            return false;
                        }
                        var clips = parsedResult.Clips;
                        var dfns = parsedResult.Dfns;

                        int vIndex = 0;
                        if (myOption.Interactive && !selected)
                        {
                            int i = 0;
                            dfns.ForEach(key => Logger.LogColor($"{i++}.{AppSettings.QualityMap.GetValueOrDefault(key, $"未知({key})")}"));
                            Logger.Log("请选择最想要的清晰度(输入序号): ", false);
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            vIndex = ReadIntSafe();
                            // 下一行直接 dfns[vIndex]；用 > 会放过 vIndex==dfns.Count 而抛
                            // ArgumentOutOfRangeException（不在重试白名单里，整个下载中止）。必须 >=。
                            if (vIndex >= dfns.Count || vIndex < 0) vIndex = 0;
                            Console.ResetColor();
                            //重新解析
                            parsedResult = await Parser.ExtractTracksAsync(aidOri, p.aid, p.cid, p.epid, myOption.UseTvApi, myOption.UseIntlApi, myOption.UseAppApi, firstEncoding!, myOption.DecryptDrm, dfns[vIndex], cancellationToken);
                            if (!p.points.Any()) p.points = parsedResult.ExtraPoints;
                            selected = true;
                            vIndex = 0; // 重新解析后第一个轨道即为所选清晰度
                        }
                        //排序
                        parsedResult.VideoTracks = SortTracks(parsedResult.VideoTracks, dfnPriority, encodingPriority, myOption.VideoAscending);

                        Logger.Log($"共计{parsedResult.VideoTracks.Count}条流(共有{clips.Count}个分段).");
                        int index = 0;
                        foreach (var v in parsedResult.VideoTracks)
                        {
                            var kbps = v.dur > 0 ? v.size / 1024 / v.dur * 8 : 0;
                            Logger.LogColor($"{index++}. [{v.dfn}] [{v.res}] [{v.codecs}] [{v.fps}] [~{kbps:00} kbps] [{BBDownUtil.FormatFileSize(v.size)}]".Replace("[] ", ""), false);
                            if (myOption.OnlyShowInfo)
                            {
                                clips.ForEach(Console.WriteLine);
                            }
                        }
                        if (myOption.OnlyShowInfo) return true;
                        savePath = PathUtil.ResolveWorkPath(FormatSavePath(savePathFormat, title, parsedResult.VideoTracks.ElementAtOrDefault(vIndex), null, p, pagesCount, apiType, pubTime));

                        if (downloadDanmaku)
                        {
                            var danmakuXmlPath = Path.ChangeExtension(savePath, ".xml");
                            var danmakuAssPath = Path.ChangeExtension(savePath, ".ass");
                            Logger.Log("正在下载弹幕Xml文件");
                            var danmakuUrl = $"https://comment.bilibili.com/{p.cid}.xml";
                            await BBDownDownloadUtil.DownloadFileAsync(danmakuUrl, danmakuXmlPath, downloadConfig, cancellationToken);
                            var danmakus = DanmakuUtil.ParseXml(danmakuXmlPath);
                            if (danmakus == null)
                            {
                                Logger.Log("弹幕Xml解析失败, 删除Xml...");
                                File.Delete(danmakuXmlPath);
                            }
                            else if (danmakus.Length == 0)
                            {
                                Logger.Log("当前视频没有弹幕, 删除Xml...");
                                File.Delete(danmakuXmlPath);
                            }
                            else if (downloadDanmakuFormats.Contains(BBDownDanmakuFormat.Ass))
                            {
                                var filtered = DanmakuUtil.Filter(danmakus, myOption.DanmakuFilter, myOption.DanmakuFilterUser);
                                if (filtered.Length == 0)
                                {
                                    Logger.Log("过滤后没有剩余弹幕, 跳过Ass保存");
                                }
                                else
                                {
                                    Logger.Log($"正在保存弹幕Ass文件{(filtered.Length < danmakus.Length ? $"(过滤掉 {danmakus.Length - filtered.Length} 条)" : "")}...");
                                    await DanmakuUtil.SaveAsAssAsync(filtered, danmakuAssPath);
                                }
                            }

                            // delete xml if possible
                            if (!downloadDanmakuFormats.Contains(BBDownDanmakuFormat.Xml) && File.Exists(danmakuXmlPath))
                            {
                                File.Delete(danmakuXmlPath);
                            }

                            if (myOption.DanmakuOnly)
                            {
                                bool danmakuProduced = false;
                                if (downloadDanmakuFormats.Contains(BBDownDanmakuFormat.Xml) && File.Exists(danmakuXmlPath))
                                {
                                    relatedTask?.AddSavePath(danmakuXmlPath);
                                    danmakuProduced = true;
                                }
                                if (downloadDanmakuFormats.Contains(BBDownDanmakuFormat.Ass) && File.Exists(danmakuAssPath))
                                {
                                    relatedTask?.AddSavePath(danmakuAssPath);
                                    danmakuProduced = true;
                                }
                                // 只清理空目录（守卫语义同上方 DanmakuOnly 第一处：保留可续传分片）
                                if (Directory.Exists(PathUtil.ResolveWorkPath(p.aid)) && Directory.GetFiles(PathUtil.ResolveWorkPath(p.aid)).Length == 0)
                                {
                                    try { Directory.Delete(PathUtil.ResolveWorkPath(p.aid), true); } catch (IOException) { }
                                }
                                if (!danmakuProduced)
                                {
                                    Logger.LogWarn("DanmakuOnly 模式未生成任何弹幕文件");
                                    return false;
                                }
                                return true;
                            }
                        }

                        if (myOption.CoverOnly)
                        {
                            var coverUrl = pic == "" ? p.cover! : pic;
                            if (string.IsNullOrEmpty(coverUrl))
                            {
                                Logger.LogWarn("CoverOnly 模式无封面资源可下载");
                                return false;
                            }
                            var newCoverPath = Path.ChangeExtension(savePath, Path.GetExtension(coverUrl));
                            await BBDownDownloadUtil.DownloadFileAsync(coverUrl, newCoverPath, downloadConfig, cancellationToken);
                            if (Directory.Exists(PathUtil.ResolveWorkPath(p.aid)) && Directory.GetFiles(PathUtil.ResolveWorkPath(p.aid)).Length == 0)
                            {
                                try { Directory.Delete(PathUtil.ResolveWorkPath(p.aid), true); } catch (IOException) { }
                            }
                            relatedTask?.AddSavePath(newCoverPath);
                            return true;
                        }

                        if (File.Exists(savePath) && new FileInfo(savePath).Length != 0)
                        {
                            Logger.Log($"{savePath}已存在, 跳过下载...");
                            relatedTask?.AddSavePath(savePath);
                            // 清理本次已下载但未被消费的装饰性文件（封面/字幕/章节）：与 dash
                            // 分支的跳过清理行为保持一致。封面下载于轨道提取前，若不清理，
                            // 每次重跑已下载的视频都会残留封面/字幕/章节文件，且 Directory
                            // 非空时 aid 目录也删不掉。
                            try { if (File.Exists(coverPath)) File.Delete(coverPath); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                            foreach (var s in subtitleInfo)
                            {
                                try { if (File.Exists(s.path)) File.Delete(s.path); }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                            }
                            DeleteResidualChapterFiles(PathUtil.ResolveWorkPath(p.aid));
                            // 只清理空目录：目录里的可续传分片可能属于其它分P的上次中断下载
                            // （aid 工作目录按稿件共享），不能因本P跳过而连带删除。
                            if (selectedPagesInfo.Count == 1 && Directory.Exists(PathUtil.ResolveWorkPath(p.aid)) && Directory.GetFiles(PathUtil.ResolveWorkPath(p.aid)).Length == 0)
                            {
                                try { Directory.Delete(PathUtil.ResolveWorkPath(p.aid), true); } catch (IOException) { }
                            }
                            return true;
                        }
                        var pad = string.Empty.PadRight(clips.Count.ToString().Length, '0');
                        var segFiles = new List<string>();
                        for (int i = 0; i < clips.Count; i++)
                        {
                            var link = clips[i];
                            videoPath = PathUtil.ResolveWorkPath($"{p.aid}/{p.aid}.P{p.index}.{p.cid}.{i.ToString(pad)}.mp4");
                            Logger.Log($"开始下载P{p.index}视频, 片段({(i + 1).ToString(pad)}/{clips.Count})...");
                            await DownloadTrackAsync(link, videoPath, downloadConfig, video: true, token: cancellationToken);
                            segFiles.Add(videoPath);
                        }
                        Logger.Log($"下载P{p.index}完毕");
                        Logger.Log("开始合并分段...");
                        // 传入本次下载的精确分段列表，而非扫描整个目录（GetFiles(".mp4") 会连同
                        // 同 aid 其它分P 的成品一起捞进来，多P FLV 视频会被拼串味）
                        videoPath = PathUtil.ResolveWorkPath($"{p.aid}/{p.aid}.P{p.index}.{p.cid}.mp4");
                        await BBDownMuxer.MergeFLV(segFiles.ToArray(), videoPath, cancellationToken);
                        if (myOption.SkipMux)
                        {
                            // 记录原始轨道产物：SkipMux 跳过混流，返回前若不记录 SavePaths，
                            // serve API 的成功响应里产物列表会缺本次下载的合并视频流。
                            if (File.Exists(videoPath)) relatedTask?.AddSavePath(videoPath);
                            return true;
                        }
                        Logger.Log($"开始混流视频{(subtitleInfo.Any() ? "和字幕" : "")}...");
                        if (myOption.AudioOnly)
                            // 与 dash 分支一致：用 Path.ChangeExtension 替换真实扩展名
                            savePath = Path.ChangeExtension(savePath, ".m4a");
                        // 与 dash 分支一致：对最终 savePath 加独占锁，锁内完成"存在性判定 → 混流 →
                        // 校验 → 清理"，防止 serve 同标题任务并发覆盖
                        var muxOutcome = await BBDownDownloadUtil.RunWithPathLockAsync(savePath,
                            () => MuxAndFinalizeAsync(useMp4box: false, myOption, p, vInfo, selectedPagesInfo, parsedResult, desc, title, coverPath, lang, subtitleInfo, audioMaterial,
                                videoPath, "", savePath, isHevc: false, videoOnly: myOption.VideoOnly, audioOnly: myOption.AudioOnly,
                                bangumi, fastSkipChecked: true, relatedTask, cancellationToken),
                            cancellationToken);
                        if (muxOutcome == MuxOutcome.Failed)
                        {
                            Logger.LogError("合并失败"); return false;
                        }
                    }
                    else
                    {
                        // 无可用轨道（既非 DASH 也非 FLV）→ 解析失败。必须显式返回 false：
                        // 此前记录错误后仍落到下方 return true，CLI 报成功、Serve 标记 Succeeded、
                        // 还可能写入下载存档，且 SavePath 指向不存在的文件。
                        if (myOption.DecryptDrm)
                        {
                            Logger.LogError("此视频需要大会员登录才能获取完整DRM内容。");
                            Logger.LogError("请先运行: BBDown login  或使用 --cookie 参数");
                        }
                        else
                        {
                            Logger.LogError("解析此分P失败(建议--debug查看详细信息)");
                        }
                        if (parsedResult.WebJsonString.Length < 100)
                        {
                            Logger.LogError(parsedResult.WebJsonString);
                        }
                        // 完整播放 JSON 含带签名的媒体地址（deadline/sign 等参数），全文落盘
                        // 会把可用的临时签名 URL 写进日志文件。与 Parser 的 debug 摘要一致，
                        // 只记录长度 + 前 1KB 摘要，避免签名 URL 泄漏。
                        var webJson = parsedResult.WebJsonString;
                        // 截断实参含子串分配，DebugLog 关闭时跳过求值
                        if (Config.Current.DebugLog)
                            Logger.LogDebug("WebJson {0} chars: {1}",
                                webJson.Length,
                                webJson.Length > LogJsonSummaryMaxChars ? webJson[..LogJsonSummaryMaxChars] + "…" : webJson);
                        return false;
                    }

                    if (!string.IsNullOrWhiteSpace(savePath))
                    {
                        relatedTask?.AddSavePath(savePath);
                    }
                    return true; // success, exit retry loop
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException or TimeoutException
                                  || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    // 风控页（200+HTML 的 RiskControlResponseException，继承 JsonException）也参与
                    // 页面级重试：B 站 windmill/风控页常为瞬时故障（数秒到一分钟内自动解除），
                    // 重试可在解除后恢复，且次数受 --retry-count 约束。装饰性资源（字幕/封面/
                    // 评论）的抓取失败已在各自调用点降级为警告，不会进入此 catch 触发整页重下。
                    // 超时（HttpClient 超时抛的 TaskCanceledException 其 token 未取消）同样参与重试；
                    // 真正的用户取消（token 已取消）不重试，直接向上传播。
                    retryCount++;
                    if (retryCount >= maxRetry)
                    {
                        Logger.LogError($"下载尝试 {retryCount} 次后仍失败，最后错误: [{ex.GetType().Name}] {ex.Message}");
                        throw;
                    }
                    // 与轨道级重试一致：退避基数 retryCount * RetryDelayMs 线性放大
                    //（默认 3000ms → 首次失败 3s、二次 6s...），符合 --retry-delay 的"基础毫秒数"语义
                    int backoffMs = retryCount * myOption.RetryDelay;
                    Logger.LogError($"[{ex.GetType().Name}] {ex.Message}");
                    Logger.LogWarn($"下载出现异常, {backoffMs / 1000.0:0.#} 秒后将进行自动重试...");
                    await Task.Delay(backoffMs, cancellationToken);
                }
            }
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户取消/服务关停：清理当前分P工作目录中"确定非续传资产"的残留
            //（空 aid 目录 + 无清单死 .tmp），保留 .vclip/.aclip 与带有效清单的
            // .tmp——它们是跨进程断点续传资产，无脑删除会让中断的文件无法续传。
            // 清理后原样向上传播取消。
            CleanNonResumableWorkArtifacts(p.aid);
            throw;
        }
    }

    /// <summary>
    /// 取消路径的定位清理：只删"确定不是续传资产"的残留。
    /// - aid 目录为空 → 删目录（镜像成功路径的删空目录语义）；
    /// - 无对应 *.manifest.json 的 *.tmp → 删（此类必被 CanResumeFrom 拒绝、下次运行
    ///   也会删，是纯死重）；
    /// 绝不删 .vclip/.aclip 与带有效清单的 .tmp。aid 目录跨分P共享，取消某 P 不能清
    /// 其它 P 的续传资产，故按文件粒度而非整体删目录。
    /// </summary>
    private static void CleanNonResumableWorkArtifacts(string aid)
    {
        try
        {
            var dir = PathUtil.ResolveWorkPath(aid);
            if (!Directory.Exists(dir)) return;
            foreach (var tmp in Directory.GetFiles(dir, "*.tmp"))
            {
                // 清单伴生文件是 xxx.tmp.manifest.json；无清单的 .tmp 是 WriteResumeManifest
                // 失败窗口或取消竞态留下的死文件，续传必被拒，清理掉避免累积。
                if (!File.Exists(tmp + ".manifest.json"))
                {
                    try { File.Delete(tmp); }
                    catch (IOException) { /* 占用时跳过，下次再清 */ }
                }
            }
            if (Directory.GetFiles(dir).Length == 0)
            {
                try { Directory.Delete(dir, true); }
                catch (IOException) { /* 目录被占用时跳过 */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogDebug("取消清理工作目录失败: {0}", ex.Message);
        }
    }

    /// <summary>
    /// 下载单条字幕文件。默认（非 SubOnly）字幕是装饰性资源：任何失败（含过期签名 URL
    /// 返回 200+HTML 风控页的 <see cref="RiskControlResponseException"/>，继承 JsonException）
    /// 都只降级为警告并返回 false，由调用方跳过该条字幕——绝不进入页面级重试或中止整批。
    /// SubOnly 模式下字幕是唯一产物（<paramref name="degradeOnFailure"/> = false），失败应
    /// 抛出交由页面级重试恢复。真正的用户取消（token 已取消）向上传播。
    /// </summary>
    internal static async Task<bool> TryDownloadSubtitleAsync(Subtitle s, CancellationToken token, bool degradeOnFailure = true)
    {
        try
        {
            await SubUtil.SaveSubtitleAsync(s.url, s.path, token);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or TaskCanceledException or InvalidOperationException or TimeoutException)
        {
            // HttpClient 超时抛的 TaskCanceledException 其 token 未取消，仍按字幕降级处理；
            // HTTPUtil 重试耗尽后抛的 TimeoutException（内部包裹 OCE）同属瞬时故障：字幕是装饰性
            // 资源，持续超时也应降级为"无字幕"而非穿透到页面级重试击沉主下载（E1）。
            // 真正的取消必须向上传播中止下载，不能吞掉后继续执行无可取消的网络调用。
            if (token.IsCancellationRequested) throw;
            if (!degradeOnFailure) throw;
            Logger.LogWarn($"字幕 {s.lan} 下载失败（已跳过）: {ex.Message}");
            return false;
        }
    }

}
