using System;
using System.IO;
using System.Linq;
using static BBDown.Core.Entity.Entity;
using System.Text.RegularExpressions;

using BBDown.Core.Util;
using System.Text.Json;
using BBDown.Core;
namespace BBDown;

internal partial class Program
{
    /// <summary>
    /// 决策保存路径模板：单P模板（-F/默认）还是多P模板（-M/默认）。
    /// 多P判定基于【实际下载的分P数】而非视频总P数——-p 单选 1 集时即使视频
    /// 共有多个分P也走单P模板（-F 生效，产物不再带 [P##] 前缀）；
    /// 番剧未完结时固定按多P处理（每P自成文件）。useMultiWhenSingle 为真时强制多P。
    /// </summary>
    internal static string ResolveSavePathFormat(string filePattern, string multiFilePattern, int actualPageCount, bool useMultiWhenSingle)
    {
        var single = string.IsNullOrEmpty(filePattern) ? SinglePageDefaultSavePath : filePattern;
        if (actualPageCount > 1 || useMultiWhenSingle)
            return string.IsNullOrEmpty(multiFilePattern) ? MultiPageDefaultSavePath : multiFilePattern;
        return single;
    }

    // internal 供 PathFormatTests 直接验证占位符替换（publishDate 净化等，RF-19）
    internal static string FormatSavePath(string savePathFormat, string title, Video? videoTrack, Audio? audioTrack, Page p, int pagesCount, string apiType, long pubTime)
    {
        var result = savePathFormat.Replace('\\', '/');
        var regex = InfoRegex();
        foreach (Match m in regex.Matches(result).Cast<Match>())
        {
            var key = m.Groups[1].Value;

            //解析自定义日期格式
            var defaultDateFormat = "yyyy-MM-dd_HH-mm-ss";
            string[] prefixes = ["publishDate:", "videoDate:"];
            foreach (var prefix in prefixes)
            {
                if (key.StartsWith(prefix))
                {
                    defaultDateFormat = key[(key.IndexOf(':') + 1)..];
                    key = prefix.Replace(":", "");
                    break;
                }
            }

            var v = key switch
            {
                "videoTitle" => BBDownUtil.GetValidFileName(title, filterSlash: true).Trim().TrimEnd('.').Trim(),
                "pageNumber" => p.index.ToString(),
                "pageNumberWithZero" => p.index.ToString().PadLeft(pagesCount.ToString().Length, '0'),
                "pageTitle" => BBDownUtil.GetValidFileName(p.title, filterSlash: true).Trim().TrimEnd('.').Trim(),
                "bvid" => p.bvid,
                "aid" => p.aid,
                "cid" => p.cid,
                "ownerName" => p.ownerName == null ? "" : BBDownUtil.GetValidFileName(p.ownerName, filterSlash: true).Trim().TrimEnd('.').Trim(),
                "ownerMid" => p.ownerMid ?? "",
                "dfn" => videoTrack == null ? "" : videoTrack.dfn,
                "res" => videoTrack == null ? "" : videoTrack.res,
                "fps" => videoTrack == null ? "" : videoTrack.fps,
                "videoCodecs" => videoTrack == null ? "" : videoTrack.codecs,
                "videoBandwidth" => videoTrack == null ? "" : videoTrack.bandwidth.ToString(),
                "audioCodecs" => audioTrack == null ? "" : audioTrack.codecs,
                "audioBandwidth" => audioTrack == null ? "" : audioTrack.bandwidth.ToString(),
                // publishDate/videoDate 的自定义格式是用户输入，CultureInfo 决定 `:` 占位符
                // 的实际输出（RF-19）：替换值再过一次 GetValidFileName——否则 en-US 下
                // <publishDate:yyyy-MM-dd HH:mm:ss> 产出含 `:` 的路径（Windows 上可写成
                // NTFS 备用数据流，资源管理器不可见）。videoTitle 等占位符本就走此净化。
                "publishDate" => BBDownUtil.GetValidFileName(FormatTimeStamp(pubTime, defaultDateFormat)),
                "videoDate" => BBDownUtil.GetValidFileName(FormatTimeStamp(p.pubTime, defaultDateFormat)),
                "apiType" => apiType,
                _ => $"<{key}>"
            };
            result = result.Replace(m.Value, v);
        }
        // 大小写不敏感判断：用户模板可能产出 ".MP4"/".Mp4"（例如占位符替换自大写扩展名），
        // 大小写敏感会让 ".MP4" 再被追加一次 ".mp4" 变成 ".MP4.mp4"。
        if (!result.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) { result += ".mp4"; }
        return result;
    }

    [GeneratedRegex("<([\\w:\\-.]+?)>")]
    private static partial Regex InfoRegex();

}
