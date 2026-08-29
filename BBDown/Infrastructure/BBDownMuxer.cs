using System;
using BBDown.Core.Util;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using static BBDown.Core.Entity.Entity;
using System.IO;
using BBDown.Core;
using System.Runtime.InteropServices;

namespace BBDown;

static partial class BBDownMuxer
{
    public static string FFMPEG = "ffmpeg";
    public static string MP4BOX = "mp4box";

    /// <summary>混流外部进程执行器。默认用系统进程实现，测试可注入假进程。</summary>
    public static IExternalProcessRunner ProcessRunner { get; set; } = new SystemProcessRunner();

    private static async Task<int> RunExeAsync(string app, List<string> args, int timeoutMinutes, CancellationToken cancellationToken)
    {
        // 参数以逐项 argv 传给执行器，由 OS 层负责转义——不再手工拼引号/反斜杠
        var spec = new ExternalProcessSpec
        {
            FileName = app,
            Arguments = args,
            TimeoutMs = timeoutMinutes * 60_000,
            ToolDisplayName = app,
            OnStandardError = line => Logger.Log(line),
            OnStandardOutput = line => Logger.LogDebug(line),
        };
        return await ProcessRunner.RunAsync(spec, cancellationToken);
    }

    /// <summary>
    /// 外部工具可用性守卫：仅对真实进程执行器生效（测试注入假 runner 时跳过，
    /// 见 ExternalProcessRunnerTests 直接调 MergeFLV 的场景——假 runner 不启动进程，
    /// 启动前校验无意义）。
    /// 默认名（ffmpeg/mp4box）由 OS 从 PATH 解析，显式指定后为含路径的文件。
    /// 用于堵住 FindBinaries 前置探测覆盖不到的路径：MergeFLV（--skip-mux 下
    /// 探测被门控跳过但 FLV 合并仍执行）与 MuxByMp4box（杜比视界自动切换时
    /// 探测分支只探了 ffmpeg、MP4BOX 从未探测）——这两条路径此前在进程启动时
    /// 抛原生 Win32Exception，用户无法定位。
    /// </summary>
    private static void EnsureToolAvailable(string tool, string toolFlag, string purpose)
    {
        if (ProcessRunner is not SystemProcessRunner) return;
        bool explicitPath = tool != "ffmpeg" && tool != "mp4box";
        bool available = explicitPath
            ? File.Exists(tool)
            : ExternalToolHelper.FindExecutable(tool) is not null;
        if (!available)
            // InvalidOperationException 而非 FileNotFoundException：本异常可能从
            // MergeFLV/MuxByMp4box 冒泡到分P批量循环，页面级与批级的失败上报 catch 过滤器
            // 均不含 FileNotFoundException——用工具缺失逃逸会跳过剩余分P、webhook 与
            // failedPages 汇总；InvalidOperationException 已在两处过滤器内，走统一失败上报。
            throw new InvalidOperationException(
                $"找不到可执行的{tool}文件（{purpose}）。请安装并加入 PATH，或使用 {toolFlag} 指定路径。");
    }

    private static string EscapeString(string str)
    {
        // mp4box 的 -itags 值要求双引号包裹、值内 " 和 \ 需转义；CR/LF 不转义会直接破坏
        // itags 的 token 语法（引号内的换行让解析器认为值提前结束）。多行 desc/title 等
        // 元数据来自 B 站内容（信任边界），无法挑出换行输入，只能这里折叠为空格。
        // 替换为空字符会拼接单词、替换为空格是安全的折行。
        return string.IsNullOrEmpty(str) ? str
            : str.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    }

    private static async Task<int> MuxByMp4box(string url, string videoPath, string audioPath, List<AudioMaterial>? audioMaterial, string outPath, string desc, string title, string author, string episodeId, string pic, string lang, List<Subtitle>? subs, bool audioOnly, bool videoOnly, List<ViewPoint>? points, CancellationToken cancellationToken)
    {
        // 杜比视界自动切 mp4box 时 FindBinaries 只探了 ffmpeg（初始 UseMP4box=false），
        // MP4BOX 从未探测：这里运行时守卫给出清晰报错，替代原生 Win32Exception。
        EnsureToolAvailable(MP4BOX, "--mp4box-path", "mp4box 混流");
        // mp4box 的 -itags/-add 值要求双引号包裹、值内 " 和 \ 需转义（mp4box 自身语法）。
        // 转义只在 mp4box 分支做：ffmpeg 分支走 argv 逐项直传，提前转义会把字面
        // \"、\\ 写进 mp4 元数据。
        desc = EscapeString(desc);
        title = EscapeString(title);
        episodeId = EscapeString(episodeId);
        author = EscapeString(author);
        lang = EscapeString(lang);
        // 与 ffmpeg 分支的 MuxAV 一致：多P/嵌套路径模板下输出目录可能尚不存在，
        // mp4box 打不开不存在的父目录下的输出文件，返回非零导致"合并失败"。
        // 杜比视界 + ffmpeg<5.0 会自动切到 mp4box，无弹幕的多P下载稳定踩中此缺陷。
        var outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            Directory.CreateDirectory(outDir);

        var args = new List<string> { "-inter", "500", "-noprog" };
        int nowId = 0;
        if (!string.IsNullOrEmpty(videoPath))
        {
            // trackID 由调用方场景决定：纯音频输出（audioOnly 且无音频文件）时取轨道 2
            string trackId = audioOnly && audioPath == "" ? "2" : "1";
            args.Add("-add");
            args.Add($"{videoPath}#trackID={trackId}:name=");
            nowId++;
        }
        if (!string.IsNullOrEmpty(audioPath))
        {
            // lang 已在 MuxByMp4box 顶部 EscapeString（mp4box 的 -add 值语法要求），值直接拼入
            args.Add("-add");
            args.Add($"{audioPath}:lang={(lang == "" ? "und" : lang)}");
            nowId++;
        }
        // 配音/背景音轨与 ffmpeg 分支对齐：必须进入 -add 链。否则杜比视界自动切 mp4box
        // （ffmpeg<5.0）时这些已下载的轨道被静默丢弃，且随后被 CleanupDownloadedTracks
        // 删除——数据永久丢失且无任何警告。
        foreach (var material in audioMaterial ?? [])
        {
            args.Add("-add");
            args.Add($"{material.path}:lang=und");
            nowId++;
            var name = !string.IsNullOrWhiteSpace(material.title) ? material.title : material.personName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                // 轨道名走 -udta（与下方字幕轨道同机制）；mp4box 的值语法要求转义。
                args.Add("-udta");
                args.Add($"{nowId}:type=name:str=\"{EscapeString(name)}\"");
            }
        }
        string? metaFile = null;
        try
        {
            if (points != null && points.Any())
            {
                var meta = BBDownUtil.GetMp4boxMetaString(points);
                var baseDir = Path.GetDirectoryName(string.IsNullOrEmpty(videoPath) ? audioPath : videoPath);
                if (string.IsNullOrEmpty(baseDir))
                    baseDir = ".";
                // 固定名 "chapters" 会让并发混流互相覆盖（后写者的章节被先写者读到）；
                // 用输出文件派生唯一名，并在结束后清理。
                metaFile = Path.Combine(baseDir, $"chapters-{Path.GetFileNameWithoutExtension(outPath)}");
                File.WriteAllText(metaFile, meta);
                args.Add("-chap");
                args.Add(metaFile);
            }

            // 元数据全部拼进单个 "-itags tool=..." 参数（mp4box 的 itags 语法要求）
            var metaArg = new StringBuilder("tool=");
            if (!string.IsNullOrEmpty(pic))
                // cover 值与其他 itags 值同规则走 EscapeString：Windows 路径天然含 \，
                // 不转义会被 mp4box 当转义序列消费，杜比视界自动切 mp4box 时封面静默丢失
                metaArg.Append($":cover=\"{EscapeString(pic)}\"");
            if (!string.IsNullOrEmpty(episodeId))
                metaArg.Append($":album=\"{title}\":title=\"{episodeId}\"");
            else
                metaArg.Append($":title=\"{title}\"");
            metaArg.Append($":sdesc=\"{desc}\"");
            metaArg.Append($":comment=\"{url}\"");
            metaArg.Append($":artist=\"{author}\"");
            if (metaArg.Length > "tool=".Length)
            {
                args.Add("-itags");
                args.Add(metaArg.ToString());
            }

            if (subs != null)
            {
                for (int i = 0; i < subs.Count; i++)
                {
                    if (File.Exists(subs[i].path) && File.ReadAllText(subs[i].path!) != "")
                    {
                        nowId++;
                        var (subLangCode, subLangName) = SubUtil.GetSubtitleCode(subs[i].lan);
                        // name/lang 值都作为独立 argv：SubUtil 的名称表含空格（如 "Aymar aru"），
                        // 手工拼引号容易拆参，交给 ArgumentList 后天然保持单 token。
                        args.Add("-add");
                        args.Add($"{subs[i].path}#trackID=1:name={subLangName}:hdlr=sbtl:lang={subLangCode}");
                        args.Add("-udta");
                        args.Add($"{nowId}:type=name:str=\"{subLangName}\"");
                    }
                }
            }

            if (Config.Current.DebugLog) args.Add("-v");
            args.Add("-new");
            args.Add("--");
            args.Add(outPath);

            Logger.LogDebug("mp4box命令: {0}", string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
            return await RunExeAsync(MP4BOX, args, Core.Config.Current.MuxerTimeoutMinutes, cancellationToken);
        }
        finally
        {
            if (metaFile != null && File.Exists(metaFile))
            {
                try { File.Delete(metaFile); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.LogDebug("清理章节文件失败: {0}", ex.Message);
                }
            }
        }
    }

    public static async Task<int> MuxAV(bool useMp4box, string bvid, string videoPath, string audioPath, List<AudioMaterial> audioMaterial, string outPath, string desc = "", string title = "", string author = "", string episodeId = "", string pic = "", string lang = "", List<Subtitle>? subs = null, bool audioOnly = false, bool videoOnly = false, List<ViewPoint>? points = null, long pubTime = 0, bool simplyMux = false, bool isHevc = false, CancellationToken cancellationToken = default)
    {
        if (audioOnly && audioPath != "")
            videoPath = "";
        if (videoOnly)
            audioPath = "";
        // 此处不再转义 title/desc 等：ffmpeg 分支走 argv 逐项直传，值按字面写入元数据，
        // 转义会把源数据里的 " 和 \ 以字面 \"、\\ 写进 mp4 元数据（双重转义）。
        // 只有 mp4box 分支（值嵌进其 -itags/-add 自身语法）需要转义，在 MuxByMp4box 内做。
        var url = $"https://www.bilibili.com/video/{bvid}/";

        if (useMp4box)
        {
            return await MuxByMp4box(url, videoPath, audioPath, audioMaterial, outPath, desc, title, author, episodeId, pic, lang, subs, audioOnly, videoOnly, points, cancellationToken);
        }

        string? metaFile = null;
        var outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            Directory.CreateDirectory(outDir);
        //----分析并生成参数
        var args = new List<string>();
        // 输出流选项（-metadata:* / -disposition:*）统一收集到 outputArgs，绝不紧跟 -i 输入：
        // ffmpeg 中紧跟 -i <输入> 的选项会被当作输入选项——旧版本直接报
        // "Option ... cannot be applied to input url ...srt" 导致带字幕/副音轨混流失败；
        // 新版虽静默接受但元数据不作用到输出流（stream title 丢失）。
        // 全部 -i 与 -map 完成后统一追加，让这些选项落在输出选项区。
        var outputArgs = new List<string>();
        int inputCount = 0;
        foreach (string path in new[] { videoPath, audioPath })
        {
            if (!string.IsNullOrEmpty(path))
            {
                inputCount++;
                args.Add("-i");
                args.Add(path);
            }
        }

        if (audioMaterial.Any())
        {
            // 素材元数据下标起点依赖主音轨是否存在：有主音轨时输出音频流 0 是主音轨、
            // 素材从 1 起；主音轨缺失时输出音频流 0 就是首个素材，必须从 0 起，
            // 否则素材 0 的 title 会错位写到素材 1 的流上。
            int audioCount = string.IsNullOrEmpty(audioPath) ? -1 : 0;
            // 仅当存在主音轨（audioPath 非空）时才标记输出音频流 0 为"原音频"：
            // 主音轨缺失时输出音频流 0 实际是 audioMaterial[0]，会被错标。
            // ffmpeg 的 stream specifier 必须与选项名粘连成单个 argv（-metadata:s:a:0），
            // 拆成 "-metadata" + "s:a:0" 会把 s:a:0 当成非法 key=value、后续参数被误当输入文件。
            if (!string.IsNullOrEmpty(audioPath))
            {
                // 输出选项统一收集到 outputArgs（紧跟 -i 会被 ffmpeg 当输入选项处理）
                outputArgs.Add("-metadata:s:a:0");
                outputArgs.Add("title=原音频");
            }
            foreach (var audio in audioMaterial)
            {
                inputCount++;
                audioCount++;
                args.Add("-i");
                args.Add(audio.path);
                if (!string.IsNullOrWhiteSpace(audio.title))
                {
                    outputArgs.Add($"-metadata:s:a:{audioCount}");
                    // argv 逐项直传，值按字面写入，不再 EscapeString
                    outputArgs.Add($"title={audio.title}");
                }
                if (!string.IsNullOrWhiteSpace(audio.personName))
                {
                    outputArgs.Add($"-metadata:s:a:{audioCount}");
                    outputArgs.Add($"artist={audio.personName}");
                }
            }
        }

        if (!string.IsNullOrEmpty(pic))
        {
            inputCount++;
            args.Add("-i");
            args.Add(pic);
        }

        if (subs != null)
        {
            // 字幕流下标独立于原列表下标：跳过空/缺失字幕文件时若沿用原列表下标 i，
            // 实际输出的字幕流序号会跳号（如仅第 2 条有效却写 s:1），-metadata:s:s:N
            // 与真实字幕流错位。这里用只在"实际加入字幕输入"时自增的连续下标。
            int subtitleStreamIndex = 0;
            for (int i = 0; i < subs.Count; i++)
            {
                if (File.Exists(subs[i].path) && File.ReadAllText(subs[i].path!) != "")
                {
                    inputCount++;
                    args.Add("-i");
                    args.Add(subs[i].path);
                    var (subLangCode, subLangName) = SubUtil.GetSubtitleCode(subs[i].lan);
                    // 输出选项统一收集到 outputArgs：旧版 ffmpeg 对
                    // "-i sub.srt -metadata:s:s:0" 报 cannot be applied to input url
                    outputArgs.Add($"-metadata:s:s:{subtitleStreamIndex}");
                    outputArgs.Add($"title={subLangName}");
                    outputArgs.Add($"-metadata:s:s:{subtitleStreamIndex}");
                    outputArgs.Add($"language={subLangCode}");
                    subtitleStreamIndex++;
                }
            }
        }

        if (!string.IsNullOrEmpty(pic))
        {
            // disposition 的 stream specifier 同样须粘连：-disposition:v:0 attached_pic
            // 输出选项统一收集到 outputArgs，紧跟 -i 会被 ffmpeg 当输入选项处理
            outputArgs.Add($"-disposition:v:{(audioOnly ? "0" : "1")}");
            outputArgs.Add("attached_pic");
        }

        if (points != null && points.Any())
        {
            var meta = BBDownUtil.GetFFmpegMetaString(points);
            var baseDir = Path.GetDirectoryName(string.IsNullOrEmpty(videoPath) ? audioPath : videoPath);
            if (string.IsNullOrEmpty(baseDir))
                baseDir = ".";
            // 与 mp4box 分支一致：避免并发混流用固定名互相覆盖章节文件，用后即删
            metaFile = Path.Combine(baseDir, $"chapters-{Path.GetFileNameWithoutExtension(outPath)}");
            File.WriteAllText(metaFile, meta);
            args.Add("-i");
            args.Add(metaFile);
            // 章节 meta 文件的下标就是递增前的 inputCount（它是加入的最后一个输入）；
            // 不能在此 inputCount++，否则 -map_chapters 会指向不存在的输入。
            args.Add("-map_chapters");
            args.Add(inputCount.ToString());
        }

        // 所有输入流依次 -map（-map 0 / -map 1 / ...）
        for (int i = 0; i < inputCount; i++)
        {
            args.Add("-map");
            args.Add(i.ToString());
        }
        // 输出流选项（-metadata:* / -disposition:*）在全部 -i 与 -map 完成后追加，
        // 确保被 ffmpeg 解析为输出选项而非输入选项。
        args.AddRange(outputArgs);

        //----分析完毕
        args.Add("-loglevel");
        args.Add(Config.Current.DebugLog ? "verbose" : "warning");
        args.Add("-y");
        if (!simplyMux)
        {
            args.Add("-metadata");
            args.Add($"title={(episodeId == "" ? title : episodeId)}");
            args.Add("-metadata");
            args.Add($"comment={url}");
            if (lang != "")
            {
                // stream specifier 粘连：-metadata:s:a:0 language=...
                args.Add("-metadata:s:a:0");
                args.Add($"language={lang}");
            }
            if (!string.IsNullOrWhiteSpace(desc))
            {
                args.Add("-metadata");
                args.Add($"description={desc}");
            }
            if (!string.IsNullOrEmpty(author))
            {
                args.Add("-metadata");
                args.Add($"artist={author}");
            }
            if (episodeId != "")
            {
                args.Add("-metadata");
                args.Add($"album={title}");
            }
            if (pubTime != 0)
            {
                args.Add("-metadata");
                // RFC 3339 时间戳喂给机器可读协议：自定义格式的 ":" 是 culture 时间分隔符
                // 占位符，fi-FI 等区域设置下会产出 "19.30.00" 之类非 ISO-8601 串，
                // ffmpeg av_parse_time 解析失败后发布时间元数据静默丢失。
                args.Add($"creation_time={DateTimeOffset.FromUnixTimeSeconds(pubTime).ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture)}");
            }
        }
        args.Add("-c:v");
        args.Add("copy");
        args.Add("-c:a");
        args.Add("copy");
        if (audioOnly && audioPath == "") { args.Add("-vn"); }
        if (subs != null) { args.Add("-c:s"); args.Add("mov_text"); }
        // fix macOS hev1, see https://discussions.apple.com/thread/253081863?sortBy=rank
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && isHevc) { args.Add("-tag:v:0"); args.Add("hvc1"); }
        args.Add("-movflags");
        args.Add("faststart");
        args.Add("-strict");
        args.Add("unofficial");
        args.Add("-strict");
        args.Add("-2");
        args.Add("-f");
        args.Add("mp4");
        args.Add("--");
        args.Add(outPath);

        Logger.LogDebug("ffmpeg命令: {0}", string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        try
        {
            return await RunExeAsync(FFMPEG, args, Core.Config.Current.MuxerTimeoutMinutes, cancellationToken);
        }
        finally
        {
            if (metaFile != null && File.Exists(metaFile))
            {
                try { File.Delete(metaFile); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.LogDebug("清理章节文件失败: {0}", ex.Message);
                }
            }
        }
    }

    public static async Task MergeFLV(string[] files, string outPath, CancellationToken cancellationToken = default)
    {
        if (files.Length == 0) return;
        if (files.Length == 1)
        {
            File.Move(files[0], outPath, true);
            return;
        }

        // --skip-mux 下 FindBinaries 的 ffmpeg 探测被门控跳过，但多段 FLV 合并
        //（每段转 .ts 后 concat）仍执行：运行时守卫给出清晰报错，替代原生 Win32Exception。
        EnsureToolAvailable(FFMPEG, "--ffmpeg-path", "FLV 分段合并");

        // 只处理本次传入的分段派生出的 .ts，不再扫描整个目录：
        // 目录里可能有同一 aid 其它分P 已完成的 .ts / 成品，扫目录会把它们
        // 一并拼进本P 输出，产出串味的损坏文件。
        var tsFiles = new List<string>();
        var sourceFiles = new List<string>();
        try
        {
            foreach (var file in files)
            {
                var tmpFile = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".ts");
                var args = new List<string> { "-loglevel", "warning", "-y", "-i", file, "-map", "0", "-c", "copy", "-f", "mpegts", "-bsf:v", "h264_mp4toannexb", tmpFile };
                Logger.LogDebug("ffmpeg命令: {0}", string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
                int code = await RunExeAsync(FFMPEG, args, Core.Config.Current.MuxerTimeoutMinutes, cancellationToken);
                // 校验退出码与产物：原实现丢弃退出码却无条件删除源分段，
                // ffmpeg 失败时源已删、.ts 为空，合并出缺段文件且不可恢复。
                if (code != 0 || !File.Exists(tmpFile) || new FileInfo(tmpFile).Length == 0)
                {
                    TryDelete(tmpFile);
                    throw new InvalidOperationException(
                        $"FLV 分段转封装失败 (ffmpeg code={code})：{Path.GetFileName(file)}，已保留源分段以便重试");
                }
                tsFiles.Add(tmpFile);
                // 不在此处删除源分段：后续分段失败时，前面已转好的源应保留以便整体重试。
                // 源文件在全部转换、合并成功后才统一删除（见下方）。
                sourceFiles.Add(file);
            }
            await BBDownUtil.CombineMultipleFilesIntoSingleFileAsync(tsFiles.ToArray(), outPath, cancellationToken);
            // 全部转换 + 合并成功后才清理源分段
            foreach (var s in sourceFiles) TryDelete(s);
        }
        finally
        {
            // 无论成功、失败或被取消，中间生成的 .ts 临时文件都应被清理
            foreach (var s in tsFiles) TryDelete(s);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* 清理失败不影响主流程 */ }
    }
}
