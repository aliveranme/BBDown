using System;
using BBDown.Core.Util;
using BBDown.Core;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using static BBDown.Core.Entity.Entity;

namespace BBDown;

public static partial class BBDownUtil
{
    public static async Task CheckUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!;
            string nowVer = $"v{ver.Major}.{ver.Minor}.{ver.Build}";
            string redirectUrl = await HTTPUtil.GetWebLocationAsync("https://github.com/AliverAnme/BBDown/releases/latest", token: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            string latestVer = redirectUrl.Replace("https://github.com/AliverAnme/BBDown/releases/tag/", "");
            if (!nowVer.Equals(latestVer, StringComparison.OrdinalIgnoreCase) && !latestVer.StartsWith("https"))
            {
                Console.Title = $"发现新版本：{latestVer}";
                Logger.LogColor($"发现新版本：{latestVer}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarn($"检查更新失败: {ex.Message}");
            Logger.LogDebug($"检查更新失败详情: {ex}");
        }
    }
    public static Task<string> GetAvIdAsync(string input) => UrlResolver.ResolveAsync(input);

    /// <summary>带取消令牌的 URL 解析：serve 模式下让 /cancel 能中断正在进行的解析。</summary>
    public static Task<string> GetAvIdAsync(string input, CancellationToken cancellationToken)
        => UrlResolver.ResolveAsync(input, cancellationToken);


    public static string FormatFileSize(double fileSize)
    {
        return fileSize switch
        {
            < 0 => throw new ArgumentOutOfRangeException(nameof(fileSize)),
            >= 1024 * 1024 * 1024 => $"{fileSize / (1024 * 1024 * 1024):########0.00} GB",
            >= 1024 * 1024 => $"{fileSize / (1024 * 1024):####0.00} MB",
            >= 1024 => $"{fileSize / 1024:####0.00} KB",
            _ => $"{fileSize} bytes"
        };
    }

    public static string FormatTime(int time, bool absolute = false)
    {
        var ts = TimeSpan.FromSeconds(time);
        var totalHours = (int)ts.TotalHours;
        var minutes = ts.Minutes;
        var seconds = ts.Seconds;

        if (absolute)
        {
            return $"{totalHours:D2}:{minutes:D2}:{seconds:D2}";
        }

        return totalHours == 0 ? $"{minutes:D2}m{seconds:D2}s" : $"{totalHours}h{minutes:D2}m{seconds:D2}s";
    }

    /// <summary>
    /// 把多个分片文件合并成一个文件。异步流式复制（CopyToAsync + 81920 缓冲）：
    /// 数 GB 分片合并不再以同步 CopyTo 阻塞线程池线程数十秒，且支持取消。
    /// 失败/取消时删除半截产物：FLV 路径直接写最终 .mp4、DASH 路径写 .merging，
    /// 不清理会把损坏文件留在磁盘上（下次下载虽会被 File.Create 截断自愈，
    /// 但取消那一刻用户/脚本可能已把最终路径的半截文件当成品消费）。
    /// </summary>
    public static async Task CombineMultipleFilesIntoSingleFileAsync(string[] files, string outputFilePath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!files.Any()) return;
        if (files.Length == 1)
        {
            FileInfo fi = new(files[0]);
            fi.MoveTo(outputFilePath, true);
            return;
        }

        var outDir = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            Directory.CreateDirectory(outDir);

        try
        {
            using var outputStream = File.Create(outputFilePath);
            foreach (var inputFilePath in files)
            {
                if (inputFilePath == "")
                    continue;
                using var inputStream = File.OpenRead(inputFilePath);
                await inputStream.CopyToAsync(outputStream, 81920, token);
            }
        }
        catch
        {
            // 失败/取消：删除半截产物（using 在异常展开时已释放输出流，此处删除不撞句柄）。
            // 清理失败绝不能掩盖原始异常——若删除因 ACL/只读抛 UnauthorizedAccessException 而
            // 只捕 IOException，会替换原始异常（含取消的 OperationCanceledException，导致取消
            // 被误判为失败）。IOException 会传播给调用方触发页面级重试——重试前必须清掉损坏产物。
            try { if (File.Exists(outputFilePath)) File.Delete(outputFilePath); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>
    /// 寻找指定目录下指定后缀的文件的详细路径 如".txt"
    /// </summary>
    /// <param name="dir"></param>
    /// <param name="ext"></param>
    /// <returns></returns>
    public static string[] GetFiles(string dir, string ext)
    {
        List<string> al = [];
        DirectoryInfo d = new(dir);
        foreach (FileInfo fi in d.GetFiles())
        {
            if (fi.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase))
            {
                al.Add(fi.FullName);
            }
        }
        string[] res = al.ToArray();
        Array.Sort(res); //排序
        return res;
    }

    public static string GetValidFileName(string input, string re = "_", bool filterSlash = false, int maxBaseNameLength = 100)
        => BBDown.Core.Util.PathUtil.GetValidFileName(input, re, filterSlash, maxBaseNameLength);


    /// <summary>
    /// 获取url字符串参数, 返回参数值字符串
    /// </summary>
    /// <param name="name">参数名称</param>
    /// <param name="url">url字符串</param>
    /// <returns></returns>
    public static string GetQueryString(string name, string url)
    {
        Regex re = QueryRegex();
        MatchCollection mc = re.Matches(url);
        foreach (Match m in mc.Cast<Match>())
        {
            if (m.Result("$2").Equals(name))
            {
                return m.Result("$3");
            }
        }
        return "";
    }

    public static string GetSign(string parameters)
    {
        string toEncode = parameters + "59b43e04ad6965f34319062b478f83dd";
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(toEncode)));
    }

    public static string GetTimeStamp(bool bflag)
    {
        // 经服务器时钟偏移校准（ServerClock）：本地时钟偏差会让签名时间戳超时效窗口被拒
        DateTimeOffset ts = ServerClock.Now;
        return (bflag ? ts.ToUnixTimeSeconds() : ts.ToUnixTimeMilliseconds()).ToString();
    }

    //https://stackoverflow.com/questions/1344221/how-can-i-generate-random-alphanumeric-strings
    public static string GetRandomString(int length)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_0123456789";
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[Random.Shared.Next(s.Length)]).ToArray());
    }

    //https://stackoverflow.com/a/45088333
    public static string ToQueryString(NameValueCollection nameValueCollection)
    {
        NameValueCollection httpValueCollection = HttpUtility.ParseQueryString(string.Empty);
        httpValueCollection.Add(nameValueCollection);
        return httpValueCollection.ToString()!;
    }

    public static Dictionary<string, string> ToDictionary(this NameValueCollection nameValueCollection)
    {
        var dict = new Dictionary<string, string>();
        foreach (var key in nameValueCollection.AllKeys)
        {
            dict[key!] = nameValueCollection[key]!;
        }
        return dict;
    }

    public static NameValueCollection GetTVLoginParms()
    {
        NameValueCollection sb = new();
        DateTime now = DateTime.Now;
        string deviceId = GetRandomString(20);
        string buvid = GetRandomString(37);
        string fingerprint = $"{now:yyyyMMddHHmmssfff}{GetRandomString(45)}";
        sb.Add("appkey", "4409e2ce8ffd12b8");
        sb.Add("auth_code", "");
        sb.Add("bili_local_id", deviceId);
        sb.Add("build", "102801");
        sb.Add("buvid", buvid);
        sb.Add("channel", "master");
        sb.Add("device", "OnePlus");
        sb.Add($"device_id", deviceId);
        sb.Add("device_name", "OnePlus7TPro");
        sb.Add("device_platform", "Android10OnePlusHD1910");
        sb.Add($"fingerprint", fingerprint);
        sb.Add($"guid", buvid);
        sb.Add($"local_fingerprint", fingerprint);
        sb.Add($"local_id", buvid);
        sb.Add("mobi_app", "android_tv_yst");
        sb.Add("networkstate", "wifi");
        sb.Add("platform", "android");
        sb.Add("sys_ver", "29");
        sb.Add($"ts", GetTimeStamp(true));
        sb.Add($"sign", GetSign(ToQueryString(sb)));

        return sb;
    }



    /// <summary>
    /// 获取章节信息
    /// </summary>
    /// <param name="cid"></param>
    /// <param name="aid"></param>
    /// <returns></returns>
    public static async Task<List<ViewPoint>> FetchPointsAsync(string cid, string aid, CancellationToken token = default)
    {
        var points = new List<ViewPoint>();
        try
        {
            string api = $"https://api.bilibili.com/x/player/wbi/v2?cid={cid}&aid={aid}";
            string json = await HTTPUtil.GetWebSourceAsync(api, token: token);
            using var infoJson = JsonDocument.Parse(json);
            if (infoJson.RootElement.TryGetPropertySafe("data")?.TryGetProperty("view_points", out JsonElement vPoint) == true)
            {
                foreach (var point in vPoint.EnumerateArray())
                {
                    points.Add(new ViewPoint()
                    {
                        title = point.GetStringSafe("content"),
                        start = point.GetInt32Safe("from"),
                        end = point.GetInt32Safe("to")
                    });
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException)
        {
            Logger.LogWarn($"获取章节信息失败 (cid={cid}, aid={aid}): {ex.Message}");
        }
        return points;
    }

    /// <summary>
    /// 生成metadata文件, 用于ffmpeg混流章节信息
    /// </summary>
    /// <param name="points"></param>
    /// <returns></returns>
    public static string GetFFmpegMetaString(List<ViewPoint> points)
    {
        StringBuilder sb = new();
        sb.AppendLine(";FFMETADATA");
        foreach (var p in points)
        {
            var time = 1000; //固定 1000
            sb.AppendLine("[CHAPTER]");
            sb.AppendLine($"TIMEBASE=1/{time}");
            sb.AppendLine($"START={p.start * time}");
            sb.AppendLine($"END={p.end * time}");
            // 标题含换行可伪造额外章节行（如 "1\n[CHAPTER]..."），来源是 B 站内容，需净化
            sb.AppendLine($"title={p.title.Replace('\n', ' ').Replace('\r', ' ')}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// 生成metadata文件, 用于mp4box混流章节信息
    /// </summary>
    /// <param name="points"></param>
    /// <returns></returns>
    public static string GetMp4boxMetaString(List<ViewPoint> points)
    {
        StringBuilder sb = new();
        foreach (var p in points)
        {
            // 标题含换行可注入伪造章节行，来源是 B 站内容，需净化（同 GetFFmpegMetaString）
            sb.AppendLine($"{FormatTime(p.start, true)} {p.title.Replace('\n', ' ').Replace('\r', ' ')}");
        }
        return sb.ToString();
    }



    public static string RSubString(string sub)
    {
        sub = sub[(sub.LastIndexOf('/') + 1)..];
        var lastDot = sub.LastIndexOf('.');
        return lastDot >= 0 ? sub[..lastDot] : sub;
    }

    private static string GetMixinKey(string orig)
    {
        byte[] mixinKeyEncTab =
        [
            46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
            27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13
        ];

        var tmp = new StringBuilder(32);
        foreach (var index in mixinKeyEncTab)
        {
            tmp.Append(orig[index]);
        }
        return tmp.ToString();
    }

    public static async Task<(bool isLoggedIn, bool cookieExpired, string? newWbi)> CheckLoginWithDetails(string cookie, CancellationToken token = default)
    {
        try
        {
            var api = "https://api.bilibili.com/x/web-interface/nav";
            var source = await HTTPUtil.GetWebSourceAsync(api, token: token);
            using var navDoc = JsonDocument.Parse(source);
            var json = navDoc.RootElement;
            int code = json.GetPropertySafe("code").GetInt32();

            // wbi 密钥必须在判断登录状态之前提取：nav 接口在未登录（code=-101）时
            // 依然会返回 wbi_img，而此前的提前 return 会跳过这一步，
            // 使未登录用户的 Config.WBI 恒为空串、w_rid 恒为无效签名。
            // 提取结果随元组返回，由父流程显式 Apply（AsyncLocal 写入不会回流父调用方）。
            string? newWbi = ExtractWbiKey(json);
            if (newWbi is not null)
                Logger.LogDebug("wbi: {0}", newWbi);

            if (code == -101)
            {
                // nav 对"从未登录"和"cookie 已失效"都返回 -101，接口本身无法区分。
                // 以本地是否真的持有 cookie 来判断，否则从未登录过的新用户
                // 会被告知"Cookie 已过期，请重新扫码"——而调用方那条
                // "你尚未登录"的分支将永远无法触发。
                bool hasCookie = !string.IsNullOrEmpty(cookie);
                Logger.LogDebug(hasCookie
                    ? "Cookie 已过期或无效 (code=-101)"
                    : "尚未登录 (code=-101，本地无 Cookie)");
                return (false, hasCookie, newWbi);
            }
            var is_login = json.GetPropertySafe("data").GetPropertySafe("isLogin").GetBoolean();
            return (is_login, false, newWbi);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or TimeoutException)
        {
            Logger.LogDebug("检测登录状态失败: {0}", ex.Message);
            return (false, false, null);
        }
    }

    /// <summary>
    /// 从 nav 响应中提取 wbi 混淆密钥。返回新密钥；缺失/提取失败返回 null（保持原值）。
    /// 注意：不在此处写入 Config——AsyncLocal 写入发生在子异步方法内不会回流父调用方
    /// （父流程的 ExecutionContext 快照在 await 前已捕获）。由调用方拿到返回值后显式 Apply。
    /// </summary>
    private static string? ExtractWbiKey(JsonElement navRoot)
    {
        try
        {
            var wbiImg = navRoot.GetPropertySafe("data").GetPropertySafe("wbi_img");
            var imgUrl = wbiImg.GetPropertySafe("img_url").GetString();
            var subUrl = wbiImg.GetPropertySafe("sub_url").GetString();
            if (string.IsNullOrEmpty(imgUrl) || string.IsNullOrEmpty(subUrl))
            {
                Logger.LogDebug("nav 响应中缺少 wbi_img，跳过 wbi 密钥更新");
                return null;
            }
            return GetMixinKey(RSubString(imgUrl) + RSubString(subUrl));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            Logger.LogDebug("提取 wbi 密钥失败: {0}", ex.Message);
            return null;
        }
    }

    public static async Task<bool> CheckLogin(string cookie)
    {
        var (isLoggedIn, _, newWbi) = await CheckLoginWithDetails(cookie);
        // 同步提取出的 wbi：CheckLogin 是 CLI 顶层单流程调用，子方法返回的新密钥
        // 需由本调用方 Apply（AsyncLocal 写入不会自动回流）。
        if (newWbi is not null) Core.Config.WBI_FLOW = newWbi;
        return isLoggedIn;
    }

    /// <summary>
    /// 估算 SESSDATA 的剩余有效期（天）。B 站 SESSDATA 是 URL 编码的 base64 JSON，
    /// 第一个字段解码后含 expires（Unix 秒）。返回剩余天数；无法解析/未登录返回 null。
    /// 解析必须 fail-open：SESSDATA 结构可能随协议变化，失败绝不抛错、绝不误报。
    /// 供登录会话初始化时做"即将过期"提前警告（serve 长驻进程跨周/月运行会静默失效）。
    /// </summary>
    internal static int? EstimateSessdataExpiryDays(string cookie)
    {
        try
        {
            var sessdata = BBDownLoginUtil.GetCookieValue(cookie, "SESSDATA");
            if (sessdata == "") return null;
            // %2C 是逗号的 URL 转义：SESSDATA 用逗号连接三个字段，第一字段是 base64 JSON。
            var first = Uri.UnescapeDataString(sessdata).Split(',')[0];
            byte[] raw;
            try { raw = Convert.FromBase64String(first); }
            catch (FormatException) { return null; }
            if (raw.Length == 0) return null;
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("expires", out var expires)
                || expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt64(out var expiry) || expiry <= 0)
                return null;
            var days = (int)((expiry - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) / 86400.0);
            return days;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or DecoderFallbackException)
        {
            Logger.LogDebug("SESSDATA 过期估算失败: {0}", ex.Message);
            return null;
        }
    }
    [GeneratedRegex("(^|&)?(\\w+)=([^&]+)(&|$)?", RegexOptions.Compiled)]
    private static partial Regex QueryRegex();
    [GeneratedRegex("libavutil\\s+(\\d+)\\. +(\\d+)\\.")]
    internal static partial Regex LibavutilRegex();
}
