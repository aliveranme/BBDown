using System.Text;
using BBDown.Core.Util;
// 签名/时间戳的唯一实现（I3）；盐按端点选择，见 BiliApiKeys
using static BBDown.Core.Util.BiliApiKeys;
using System.Text.RegularExpressions;
using System.Text.Json;
using static BBDown.Core.Entity.Entity;
using System.Security.Cryptography;
using BBDown.Core.Entity;

namespace BBDown.Core;

/// <summary>
/// 播放地址接口模式：<c>--use-tv-api</c> / <c>--use-app-api</c> / <c>--use-intl-api</c>
/// 三个开关解析出的**唯一**结果（收口项：此前"实际分派"与"展示的 &lt;apiType&gt;"各有一套优先级）。
/// </summary>
public enum PlayApiMode
{
    /// <summary>默认：web playurl（走 WBI 签名）。</summary>
    Web,
    /// <summary>国际版 intl playurl（两次请求 code=0/1）。</summary>
    Intl,
    /// <summary>APP 接口（AppHelper gRPC 路径）。</summary>
    App,
    /// <summary>TV 接口（access_key + sign，platform=android_tv_yst）。</summary>
    Tv,
}

public static partial class Parser
{
    /// <summary>调试日志中 PlayJson 摘要的最大字符数（防巨响应刷屏/耗内存）。</summary>
    private const int LogJsonSummaryMaxChars = 1024;

    /// <summary>
    /// 三个 <c>--use-*-api</c> 开关 → 唯一接口模式。**优先级即语义**：INTL &gt; APP &gt; TV &gt; WEB，
    /// 与 <c>GetPlayJsonAsync</c> 的分派逐条对应（分派本身也改为读这个枚举，见下）。
    /// 同时给出多个开关时，日志与 <c>&lt;apiType&gt;</c> 展示值必须等于实际走的接口。
    /// </summary>
    public static PlayApiMode ResolveApiMode(bool tvApi, bool intlApi, bool appApi)
        => intlApi ? PlayApiMode.Intl
            : appApi ? PlayApiMode.App
            : tvApi ? PlayApiMode.Tv
            : PlayApiMode.Web;

    /// <summary><see cref="PlayApiMode"/> 的展示名（日志与 <c>&lt;apiType&gt;</c> 占位符使用）。</summary>
    public static string ApiModeLabel(PlayApiMode mode) => mode switch
    {
        PlayApiMode.Intl => "INTL",
        PlayApiMode.App => "APP",
        PlayApiMode.Tv => "TV",
        _ => "WEB",
    };

    public static string WbiSign(string api)
    {
        // 空 key 产出的 w_rid 必被服务端以 -352 拒绝，而该错误会被上游呈现为"风控"，
        // 用户无从得知根因是 nav 接口失败导致密钥从未取得——签名前显式告警定位真因。
        if (string.IsNullOrEmpty(Config.Current.Wbi))
            Logger.LogWarn("wbi 密钥为空（nav 接口未成功获取），本次签名将被服务端拒绝(-352)，请检查网络后重试");
        return $"{api}&w_rid=" + Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(api + Config.Current.Wbi)));
    }

    /// <summary>
    /// 为 API 主机补默认 https scheme。默认配置（无 scheme，如 api.bilibili.com）与原硬编码
    /// https 行为完全一致；主机自带 scheme（本地调试/测试夹具服务器定向，如 http://127.0.0.1:port）
    /// 时原样保留——否则 https://{Host} 会拼出 "https://http://..." 的畸形 URL。
    /// </summary>
    private static string WithApiScheme(string hostAndPath)
        => hostAndPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || hostAndPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? hostAndPath
            : $"https://{hostAndPath}";

    private static async Task<string> GetPlayJsonAsync(string encoding, string aidOri, string aid, string cid, string epId, bool tvApi, bool intl, bool appApi, bool wantDrm, string qn, CancellationToken token, IApiTransport transport)
    {
        Logger.LogDebug("aid={0},cid={1},epId={2},tvApi={3},IntlApi={4},appApi={5},qn={6}", aid, cid, epId, tvApi, intl, appApi, qn);

        // 接口模式由 ResolveApiMode 单点决定（优先级 INTL > APP > TV > WEB）；展示用的
        // <apiType> 占位符读同一个枚举，两者不可能再漂移。
        var apiMode = ResolveApiMode(tvApi, intl, appApi);
        if (apiMode == PlayApiMode.Intl) return await GetPlayJsonAsync(aid, cid, epId, qn, "0", token, transport);


        bool cheese = aidOri.StartsWith("cheese:");
        bool bangumi = cheese || aidOri.StartsWith("ep:");
        Logger.LogDebug("bangumi={0},cheese={1}", bangumi, cheese);

        if (apiMode == PlayApiMode.App) return await AppHelper.DoReqAsync(transport, aid, cid, epId, qn, bangumi, encoding, Config.Current.Token, token);

        string prefix = apiMode == PlayApiMode.Tv ? bangumi ? $"{Config.Current.TvHost}/pgc/player/api/playurltv" : $"{Config.Current.TvHost}/x/tv/playurl"
            : bangumi ? $"{Config.Current.Host}/pgc/player/web/v2/playurl" : $"{Config.Current.Host}/x/player/wbi/playurl";
        prefix = $"{WithApiScheme(prefix)}?";

        string api;
        if (apiMode == PlayApiMode.Tv)
        {
            StringBuilder apiBuilder = new();
            if (Config.Current.Token != "") apiBuilder.Append($"access_key={Config.Current.Token}&");
            apiBuilder.Append($"appkey={TvAppKey}&build=106500&cid={cid}&device=android");
            if (bangumi) apiBuilder.Append($"&ep_id={epId}&expire=0");
            apiBuilder.Append($"&fnval=4048&fnver=0&fourk=1&mid=0&mobi_app=android_tv_yst");
            apiBuilder.Append($"&object_id={aid}&platform=android&playurl_type=1&qn={qn}&ts={GetTimeStamp(true)}");
            api = $"{prefix}{apiBuilder}&sign={GetSign(apiBuilder.ToString(), TvSignSalt)}";
        }
        else
        {
            // 尝试提高可读性
            StringBuilder apiBuilder = new();
            apiBuilder.Append($"support_multi_audio=true&from_client=BROWSER&avid={aid}&cid={cid}&fnval=4048&fnver=0&fourk=1");
            if (Config.Current.Area != "") apiBuilder.Append($"&access_key={Config.Current.Token}&area={Config.Current.Area}");
            apiBuilder.Append($"&otype=json&qn={qn}");
            if (bangumi) apiBuilder.Append($"&module=bangumi&ep_id={epId}&session=");
            if (Config.Current.Cookie == "" && !wantDrm) apiBuilder.Append("&try_look=1");
            if (wantDrm) apiBuilder.Append("&drm_tech_type=2");
            apiBuilder.Append($"&wts={GetTimeStamp(true)}");
            api = prefix + (bangumi ? apiBuilder.ToString() : WbiSign(apiBuilder.ToString()));
        }

        //课程接口
        if (cheese) api = api.Replace("/pgc/player/web/v2/playurl", "/pugv/player/web/playurl");

        //Console.WriteLine(api);
        string webJson = await transport.GetStringAsync(api, token);
        //以下情况从网页源代码尝试解析
        if (IsVipRestrictedResponse(webJson))
        {
            Logger.Log("此视频需要大会员，您大概率需要登录一个有大会员的账号才可以下载，尝试从网页源码解析");
            // 该回退只对番剧成立：UGC 的 epId 为空，构造 /bangumi/play/ep<空> 会拿到
            // 无效页面并被 PlayerJsonRegex 误替换成空/垃圾内容，导致后续解析失败
            if (!string.IsNullOrEmpty(epId))
            {
                // 镜像站（BiliPlus 等）场景：番剧接口走 Config.Current.EpHost，回退抓取的
                // 网页源也应跟随配置的主机，否则镜像站用户会被重定向回官方域名（可能不可达）。
                // 默认配置 EpHost 即官方 api 主机，直接替换即可；非默认时用配置的镜像主机。
                string webHost = Config.Current.EpHost == "api.bilibili.com" ? "www.bilibili.com" : Config.Current.EpHost;
                string webUrl = $"{WithApiScheme(webHost)}/bangumi/play/ep{epId}";
                string webSource = await transport.GetStringAsync(webUrl, token, rejectHtml: false);
                var match = PlayerJsonRegex().Match(webSource);
                // 页面不含 window.__playinfo__（登录墙/错误页/风控页）时 Groups[1] 为空串，
                // 下游 JsonDocument.Parse("") 会抛与真实原因无关的裸 JsonException
                if (!match.Success || string.IsNullOrEmpty(match.Groups[1].Value))
                    throw new InvalidOperationException("大会员回退失败：网页源码中未找到播放信息（可能是登录墙或风控页）");
                webJson = match.Groups[1].Value;
            }
        }
        return webJson;
    }

    /// <summary>
    /// 判定 playurl API 响应是否为"大会员专享限制"错误。
    /// 优先解析 JSON 根对象的 message 字段（B 站当前返回
    /// {"code":-10403,"message":"大会员专享限制",...}）；子串匹配对文案措辞变化
    /// 脆弱（改文案即失效），仅在响应不是合法 JSON 时作为兜底。
    /// </summary>
    internal static bool IsVipRestrictedResponse(string webJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(webJson);
            return doc.RootElement.TryGetProperty("message", out var msg)
                && msg.ValueKind == JsonValueKind.String
                && msg.GetString()?.Contains("大会员专享限制") == true;
        }
        catch (JsonException)
        {
            // 非 JSON 响应（风控 HTML 等）：退回原子串匹配兜底
            return webJson.Contains("\"大会员专享限制\"");
        }
    }

    private static async Task<string> GetPlayJsonAsync(string aid, string cid, string epId, string qn, string code, CancellationToken token, IApiTransport transport)
    {
        bool isBiliPlus = Config.Current.Host != "api.bilibili.com";
        string api = $"{WithApiScheme(isBiliPlus ? Config.Current.Host : "api.biliintl.com")}/intl/gateway/v2/ogv/playurl?";

        StringBuilder paramBuilder = new();
        if (Config.Current.Token != "") paramBuilder.Append($"access_key={Config.Current.Token}&");
        paramBuilder.Append($"aid={aid}");
        if (isBiliPlus) paramBuilder.Append($"&appkey={BiliPlusAppKey}&area={(Config.Current.Area == "" ? "th" : Config.Current.Area)}");
        paramBuilder.Append($"&cid={cid}&ep_id={epId}&platform=android&prefer_code_type={code}&qn={qn}");
        if (isBiliPlus) paramBuilder.Append($"&ts={GetTimeStamp(true)}");

        paramBuilder.Append("&s_locale=zh_SG");
        string param = paramBuilder.ToString();
        api += (isBiliPlus ? $"{param}&sign={GetSign(param, BiliPlusSignSalt)}" : param);

        string webJson = await transport.GetStringAsync(api, token);
        return webJson;
    }

    /// <summary>
    /// 解析播放地址并映射轨道。I2 拆解：原 532 行巨方法按"取文档 → 数据根定位 → dash/durl/intl
    /// 分派 → 轨道映射"分段，重发接管的所有权收敛到 <see cref="PlayResponse"/>；公开签名与行为不变。
    /// </summary>
    public static Task<ParsedResult> ExtractTracksAsync(string aidOri, string aid, string cid, string epId, bool tvApi, bool intlApi, bool appApi, string encoding, bool wantDrm = false, string qn = "0", CancellationToken token = default)
        => ExtractTracksAsync(HttpApiTransport.Instance, aidOri, aid, cid, epId, tvApi, intlApi, appApi, encoding, wantDrm, qn, token);

    internal static async Task<ParsedResult> ExtractTracksAsync(IApiTransport transport, string aidOri, string aid, string cid, string epId, bool tvApi, bool intlApi, bool appApi, string encoding, bool wantDrm = false, string qn = "0", CancellationToken token = default)
    {
        var request = new PlayRequest(aidOri, aid, cid, epId, tvApi, intlApi, appApi, encoding, wantDrm, qn, transport);
        ParsedResult parsedResult = new();

        //调用解析
        parsedResult.WebJsonString = await GetPlayJsonAsync(request, request.Qn, token);
        LogPlayJsonSummary(parsedResult.WebJsonString);

        //intl接口需要两次请求(code=0和code=1)
        if (intlApi) return await ExtractIntlTracksAsync(request, parsedResult, token);

        var response = new PlayResponse(JsonDocument.Parse(parsedResult.WebJsonString));
        try
        {
            // data 在任何接管发生前就是原始根：业务校验针对原始响应（与拆解前一致）
            JsonElement data = response.Document.RootElement;
            ThrowIfRiskControlVoucher(data);
            ThrowIfPlayLimited(data);
            // UGC 的播放限制通过顶层业务 code 表达（区域限制 -86038、风控 -412、视频失效 -404 等），
            // 而 play_check 只在 pgc 响应的 result 节点出现、对 UGC 不可达，这里统一兜底。
            ThrowIfBizError(data);

            bool bangumi = aidOri.StartsWith("ep:");

            if (response.Root.TryGetProperty("dash", out _)) //dash
                await ExtractDashTracksAsync(request, parsedResult, response, data, bangumi, token);
            else if (response.Root.TryGetProperty("durl", out _)) //flv
                await ExtractDurlTracksAsync(request, parsedResult, response, token);

            // 番剧片头片尾转分段信息, 预计效果: 正片? -> 片头 -> 正片 -> 片尾
            if (bangumi) ExtractClipInfoPoints(parsedResult, response.Root);

            return parsedResult;
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>走 INTL 接口（两次请求）的入参重载，避免各阶段反复展开长参数列表。</summary>
    private static Task<string> GetPlayJsonAsync(PlayRequest request, string qn, CancellationToken token)
        => GetPlayJsonAsync(request.Encoding, request.AidOri, request.Aid, request.Cid, request.EpId,
            request.TvApi, request.IntlApi, request.AppApi, request.WantDrm, qn, token, request.Transport);

    /// <summary>
    /// 一次播放地址解析的入参（I2：从 532 行方法的参数列表提出，与原有形参一一对应）。
    /// </summary>
    private readonly record struct PlayRequest(
        string AidOri, string Aid, string Cid, string EpId,
        bool TvApi, bool IntlApi, bool AppApi, string Encoding, bool WantDrm, string Qn, IApiTransport Transport);

    /// <summary>
    /// 免二压重发的接管结果：<c>null</c> 表示沿用首轮文档（重发失败，或新响应没有可用的 dash.video）。
    /// </summary>
    private readonly record struct ReparseOutcome(List<JsonElement>? Video, List<JsonElement>? Audio);

    /// <summary>
    /// 播放响应文档的所有权载体：dash 的"免二压"重发与 durl 的"最高清晰度"重发都会用新文档
    /// 替换旧文档（旧文档立即释放），当前根节点随之切换。把"文档 + 当前根节点"绑在一起传递，
    /// 避免拆解前 respJson / root 两个变量在数百行里手工同步（I2）。
    /// </summary>
    private sealed class PlayResponse : IDisposable
    {
        public PlayResponse(JsonDocument document)
        {
            Document = document;
            Root = PickDataRoot(document.RootElement);
        }

        public JsonDocument Document { get; private set; }

        /// <summary>当前生效的数据根（<see cref="PickDataRoot"/> 的结果，接管后指向新文档）。</summary>
        public JsonElement Root { get; private set; }

        /// <summary>接管新文档：根节点切到 <paramref name="newRoot"/>，旧文档立即释放。</summary>
        public void TakeOver(JsonDocument newDocument, JsonElement newRoot)
        {
            var previous = Document;
            Document = newDocument;
            Root = newRoot;
            previous.Dispose();
        }

        public void Dispose() => Document.Dispose();
    }

    /// <summary>
    /// 数据根定位（合并原先散在首次定位与 dash/durl 重发里的 3 份漂移变体，I2）：
    /// result.video_info → result → data → 元素自身，覆盖 UGC/TV/番剧/课程四种响应形状。
    /// </summary>
    private static JsonElement PickDataRoot(JsonElement root)
    {
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            return result.TryGetProperty("video_info", out var videoInfo) ? videoInfo : result;
        if (root.TryGetProperty("data", out var data))
            return data;
        return root;
    }

    /// <summary>
    /// 轨道地址选择（原实现逐轨重复 6 遍，I2 收敛）：优先 base_url，跳过 host:port 形式的
    /// 备用地址（<see cref="BaseUrlRegex"/> 判定），列表为空时回落 base_url（即空串）。
    /// </summary>
    private static string PickTrackBaseUrl(JsonElement node)
    {
        var urlList = new List<string> { node.GetValueAsStringSafe("base_url") };
        urlList.AddRange(node.EnumerateArraySafe("backup_url").Select(i => i.ToString()));
        return urlList.FirstOrDefault(i => !BaseUrlRegex().IsMatch(i), urlList.First());
    }

    /// <summary>
    /// 调试日志不记录完整播放 JSON：其中包含带签名的媒体地址（deadline/sign 参数），
    /// 全文落盘会把可用的临时签名 URL 写进日志文件。只记录长度 + 前 1KB 摘要，
    /// 排查问题足够，避免签名媒体地址泄漏到日志。
    /// </summary>
    private static void LogPlayJsonSummary(string webJsonString)
    {
        if (!Config.Current.DebugLog) return;
        Logger.LogDebug("PlayJson {0} chars: {1}",
            webJsonString.Length,
            webJsonString.Length > LogJsonSummaryMaxChars ? webJsonString[..LogJsonSummaryMaxChars] + "…" : webJsonString);
    }

    /// <summary>
    /// INTL 接口两个轮次（code=0 视频 / code=1 补充流）的轨道合并。
    /// 某轮缺 video_info / stream_list 时跳过该轮（两轮返回结构不同，判空缺失即跳过，
    /// 不能让它以 KeyNotFoundException 中断整次解析）。
    /// </summary>
    private static async Task<ParsedResult> ExtractIntlTracksAsync(PlayRequest request, ParsedResult parsedResult, CancellationToken token)
    {
        foreach (var code in new[] { "0", "1" })
        {
            if (code == "1")
                parsedResult.WebJsonString = await GetPlayJsonAsync(request.Aid, request.Cid, request.EpId, request.Qn, code, token, request.Transport);

            using var intlJson = JsonDocument.Parse(parsedResult.WebJsonString);
            // 风控人机验证（v_voucher）：本轮被风控时不能一律抛出——两轮共用同一个
            // parsedResult，round0 已累积的可用轨道会随异常一并丢弃；而 round1 紧随
            // round0 发出，风控按请求量累积，round1 的命中率反而更高（逼近阈值时是常态）。
            // 因此已有可用轨道时降级跳过本轮（与免二压 qn=127 重发同策略：不因重发
            // 被风控而拖垮整页），只有一轮轨道都没拿到时才抛出、交由页面级重试。
            if (HasRiskControlVoucher(intlJson.RootElement))
            {
                if (parsedResult.VideoTracks.Count == 0 && parsedResult.AudioTracks.Count == 0)
                    throw RiskControlVoucherException();
                Logger.LogWarn("INTL 分轮请求触发B站风控人机验证（v_voucher），沿用已解析轨道并跳过本轮");
                continue;
            }
            // intl 接口某次请求可能不返回 video_info / stream_list（code=0 与 code=1 返回结构不同），
            // GetPropertySafe 遇到缺失会抛 KeyNotFoundException 直接中断整次解析，
            // 这里逐级判空后跳过本次迭代，等待下一次请求
            var intlData = intlJson.RootElement.TryGetPropertySafe("data");
            if (intlData is not { ValueKind: JsonValueKind.Object }) continue;
            var videoInfo = intlData.Value.TryGetPropertySafe("video_info");
            if (videoInfo is not { ValueKind: JsonValueKind.Object }) continue;
            var streamList = videoInfo.Value.TryGetPropertySafe("stream_list");
            if (streamList is not { ValueKind: JsonValueKind.Array }) continue;
            int pDur = videoInfo.Value.GetInt32Safe("timelength") / 1000;
            var audioElements = videoInfo.Value.EnumerateArraySafe("dash_audio").ToList();

            foreach (var stream in streamList.Value.EnumerateArray())
            {
                if (stream.TryGetProperty("dash_video", out JsonElement dashVideo))
                {
                    if (dashVideo.GetValueAsStringSafe("base_url") != "")
                    {
                        // 与上方 data/video_info/stream_list 的防御风格一致：某条流缺
                        // stream_info 时跳过该流而不是抛 KeyNotFoundException 中断整次解析
                        var streamInfo = stream.TryGetPropertySafe("stream_info");
                        if (streamInfo is not { ValueKind: JsonValueKind.Object }) continue;
                        var videoId = streamInfo.Value.GetValueAsStringSafe("quality");
                        Video v = new()
                        {
                            dur = pDur,
                            id = videoId,
                            dfn = AppSettings.QualityMap.GetValueOrDefault(videoId, $"未知({videoId})"),
                            bandwidth = dashVideo.GetInt64Safe("bandwidth") / 1000,
                            baseUrl = PickTrackBaseUrl(dashVideo),
                            codecs = GetVideoCodec(dashVideo.GetValueAsStringSafe("codecid")),
                            size = dashVideo.GetDoubleSafe("size")
                        };
                        if (!parsedResult.VideoTracks.Contains(v)) parsedResult.VideoTracks.Add(v);
                    }
                }
            }

            foreach (var node in audioElements)
            {
                Audio a = new()
                {
                    id = node.GetValueAsStringSafe("id"),
                    dfn = node.GetValueAsStringSafe("id"),
                    dur = pDur,
                    bandwidth = node.GetInt64Safe("bandwidth") / 1000,
                    baseUrl = PickTrackBaseUrl(node),
                    codecs = "M4A"
                };
                if (!parsedResult.AudioTracks.Contains(a)) parsedResult.AudioTracks.Add(a);
            }
        }
        return parsedResult;
    }

    /// <summary>DASH 响应解析：时长/DRM 元数据 → 免二压重发（pass 0/1）→ 轨道映射。</summary>
    private static async Task ExtractDashTracksAsync(
        PlayRequest request, ParsedResult parsedResult, PlayResponse response, JsonElement data, bool bangumi, CancellationToken token)
    {
        List<JsonElement>? audio = null;
        List<JsonElement>? video = null;
        List<JsonElement>? backgroundAudio = null;
        List<JsonElement>? roleAudio = null;
        int pDur = 0;

        if (response.Root.TryGetProperty("dash", out var dashElem))
            pDur = dashElem.GetInt32Safe("duration");
        if (pDur == 0)
            pDur = response.Root.GetInt32Safe("timelength") / 1000;

        parsedResult.ActualDurationSec = pDur;

        // DRM metadata
        parsedResult.IsDrm = response.Root.GetBooleanSafe("is_drm");
        parsedResult.DrmTechType = response.Root.GetInt32Safe("drm_tech_type");
        parsedResult.DrmType = response.Root.GetValueAsStringSafe("drm_type");
        if (parsedResult.IsDrm) Logger.LogDebug("DRM detected: type={0}, tech={1}", parsedResult.DrmType, parsedResult.DrmTechType);

        //免二压视频需要重新请求
        // dolby/flac 追加标记（RF-26）：dash 可能完全没有 audio 键而仅有 dolby/flac 音频，
        // 此时 pass 0 已把它们追加进 audio；pass 1 重发失败降级（沿用第一轮文档）时
        // 若不跳过重追加会产生重复音轨。仅当重发的新文档接管（root 换新）时才重置标记重追加。
        bool dolbyApplied = false;
        bool flacApplied = false;
        for (int reparsePass = 0; reparsePass < 2; reparsePass++)
        {
            if (reparsePass == 1)
            {
                if (request.AppApi) break; //只有非APP接口需要免二压
                if (await TryReRequestForDashAsync(request, parsedResult, response, token) is { } takenOver)
                {
                    video = takenOver.Video;
                    audio = takenOver.Audio;
                    // 新文档的 audio 是全新列表：dolby/flac 需要重新追加
                    dolbyApplied = false;
                    flacApplied = false;
                }
            }
            // RF-45：列表重赋值仅在 pass 0 执行。pass 1 的新文档接管分支已自带
            // video/audio 重赋值；降级路径（重发失败被吞/新响应无 dash）必须保持
            // pass 0 的列表（含已追加的 dolby/flac 音轨）不动——否则会从旧文档重新
            // 生成不含 dolby/flac 的列表，而 dolbyApplied/flacApplied 标记仍为 true，
            // 追加块被跳过，最终音轨静默缺失杜比/Hi-Res。
            if (reparsePass == 0)
            {
                if (response.Root.TryGetProperty("dash", out var dash) && dash.TryGetProperty("video", out var vidArr))
                    video = vidArr.EnumerateArray().ToList();
                if (response.Root.TryGetProperty("dash", out dash) && dash.TryGetProperty("audio", out var audArr))
                    audio = audArr.EnumerateArray().ToList();
            }

            if (request.AppApi && bangumi)
            {
                // data 是首轮文档的根。这里只读首轮文档是安全的：appApi 在 pass 1 开头即 break，
                // 永不发生接管（data 不会被释放）。若将来放开 appApi 重发，此处须改用 response.Root。
                if (data.TryGetProperty("dubbing_info", out var dub) && dub.TryGetProperty("background_audio", out var bgArr))
                    backgroundAudio = bgArr.EnumerateArray().ToList();
                if (data.TryGetProperty("dubbing_info", out dub) && dub.TryGetProperty("role_audio_list", out var roleArr))
                    roleAudio = roleArr.EnumerateArray().ToList();
            }

            //处理杜比音频
            if (!dolbyApplied)
                dolbyApplied = TryAppendDolbyAudio(ref audio, response.Root, request.TvApi);

            //处理Hi-Res无损
            if (!flacApplied)
                flacApplied = TryAppendFlacAudio(ref audio, response.Root, request.TvApi);

            MapDashVideoTracks(parsedResult, video, pDur, request);
        } // end for reparsePass

        MapDashAudioTracks(parsedResult, audio, pDur);
        MapDubbingTracks(parsedResult, backgroundAudio, roleAudio, pDur, request);
    }

    /// <summary>
    /// 免二压重发（qn 取可用清晰度上界）。成功接管时：新文档释放旧文档、数据根切到新文档、
    /// <c>WebJsonString</c> 同步为新响应，并返回新文档的 video/audio 列表；任何失败或新响应
    /// 没有可用的 dash.video 时返回 <c>null</c>（调用方沿用首轮结果）。
    /// 用户取消必须传播（RF-17：SendAsync 在用户 token 取消时抛的正是 TaskCanceledException）。
    /// </summary>
    private static async Task<ReparseOutcome?> TryReRequestForDashAsync(
        PlayRequest request, ParsedResult parsedResult, PlayResponse response, CancellationToken token)
    {
        JsonDocument? newResp = null;
        try
        {
            var reparsePlayJson = await GetPlayJsonAsync(request, GetMaxQn(), token);
            newResp = JsonDocument.Parse(reparsePlayJson);
            var newRoot = newResp.RootElement;
            ThrowIfBizError(newRoot);
            ThrowIfPlayLimited(newRoot);
            var pickedRoot = PickDataRoot(newRoot);
            if (!pickedRoot.TryGetProperty("dash", out var newDash) || !newDash.TryGetProperty("video", out _))
                return null;

            response.TakeOver(newResp, pickedRoot);
            newResp = null; // 文档所有权已移交，finally 不再释放
            parsedResult.WebJsonString = reparsePlayJson;
            return new ReparseOutcome(
                newDash.TryGetProperty("video", out var newVidArr) ? newVidArr.EnumerateArray().ToList() : null,
                newDash.TryGetProperty("audio", out var newAudArr) ? newAudArr.EnumerateArray().ToList() : null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 用户取消（Ctrl+C/serve 关停）必须传播：SendAsync 在用户 token 取消时
            // 抛的正是 TaskCanceledException（OperationCanceledException 子类），
            // 若被下方过滤器吞掉会被误记为"降级沿用"继续走完解析（RF-17，
            // 原 :518 注释"真正的用户取消是 OperationCanceledException"的前提有误）。
            throw;
        }
        catch (Exception ex) when (ExceptionPolicies.IsParseDowngradeFailure(ex))
        {
            Logger.LogDebug("免二压重新请求失败（降级沿用第一轮结果）: {0}", ex.Message);
            return null;
        }
        finally
        {
            newResp?.Dispose();
        }
    }

    /// <summary>
    /// 追加杜比音轨（RF-26/RF-45：由调用方的 applied 标记守卫，接管新文档后重置）。
    /// 返回是否真的追加成功。缺节点/形状异常只记 Debug，不影响主解析。
    /// </summary>
    private static bool TryAppendDolbyAudio(ref List<JsonElement>? audio, JsonElement root, bool tvApi)
    {
        if (tvApi) return false;
        try
        {
            if (root.GetPropertySafe("dash").TryGetProperty("dolby", out JsonElement dolby)
                && dolby.TryGetProperty("audio", out JsonElement dolbyAudio))
            {
                audio ??= new List<JsonElement>();
                audio.AddRange(dolbyAudio.EnumerateArray());
                return true;
            }
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
        { Logger.LogDebug("杜比音频解析失败: {0}", e.Message); }
        return false;
    }

    /// <summary>
    /// 追加 Hi-Res 无损音轨（守卫与异常语义同 <see cref="TryAppendDolbyAudio"/>；
    /// flac.audio 为 null 视为无此轨）。
    /// </summary>
    private static bool TryAppendFlacAudio(ref List<JsonElement>? audio, JsonElement root, bool tvApi)
    {
        if (tvApi) return false;
        try
        {
            if (root.GetPropertySafe("dash").TryGetProperty("flac", out JsonElement hiRes)
                && hiRes.TryGetProperty("audio", out JsonElement db)
                && db.ValueKind != JsonValueKind.Null)
            {
                audio ??= new List<JsonElement>();
                audio.Add(db);
                return true;
            }
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
        { Logger.LogDebug("Hi-Res音频解析失败: {0}", e.Message); }
        return false;
    }

    /// <summary>DASH 视频轨映射（含 DRM 元数据提取）；TV/APP 接口不读 res/fps。</summary>
    private static void MapDashVideoTracks(ParsedResult parsedResult, List<JsonElement>? video, int pDur, PlayRequest request)
    {
        if (video is null) return;

        foreach (var node in video)
        {
            var videoId = node.GetValueAsStringSafe("id");
            Video v = new()
            {
                dur = pDur,
                id = videoId,
                dfn = AppSettings.QualityMap.GetValueOrDefault(videoId, $"未知({videoId})"),
                bandwidth = node.GetInt64Safe("bandwidth") / 1000,
                baseUrl = PickTrackBaseUrl(node),
                codecs = GetVideoCodec(node.GetValueAsStringSafe("codecid")),
                size = node.GetDoubleSafe("size")
            };
            if (!request.TvApi && !request.AppApi)
            {
                v.res = node.GetValueAsStringSafe("width") + "x" + node.GetValueAsStringSafe("height");
                v.fps = node.GetValueAsStringSafe("frame_rate");
            }
            if (!parsedResult.VideoTracks.Contains(v)) parsedResult.VideoTracks.Add(v);
        }

        if (parsedResult.IsDrm && string.IsNullOrEmpty(parsedResult.KidHex))
            ExtractDrmInfo(parsedResult, video);
    }

    /// <summary>
    /// 从首条视频轨提取 DRM 元数据（bilidrm_uri → kid、widevine_pssh）。
    /// 失败只告警：DRM 信息缺失由下游解密阶段以"密钥缺失"明确报错。
    /// </summary>
    private static void ExtractDrmInfo(ParsedResult parsedResult, List<JsonElement> video)
    {
        try
        {
            var firstVideo = video.FirstOrDefault();
            if (firstVideo.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("视频轨道为空，无法提取 DRM 信息");
            if (firstVideo.TryGetProperty("bilidrm_uri", out var drmUri))
            {
                var uri = drmUri.GetString() ?? "";
                var lastSlash = uri.LastIndexOf("//", StringComparison.Ordinal);
                if (lastSlash >= 0)
                {
                    // bilidrm://<kid> 的 kid 是 32 位 hex。无校验地提取会把
                    // 带 query/path 的畸形 URI（bilidrm://host/path?x=1）的混合体
                    // 一路带到 mp4decrypt 才失败；这里只接受纯 32 位 hex，
                    // 否则保持 KidHex 为空（下方会以"密钥缺失"明确报错）。
                    var candidate = uri[(lastSlash + 2)..];
                    if (candidate.Length == 32 && candidate.All(Uri.IsHexDigit))
                        parsedResult.KidHex = candidate;
                    else
                        Logger.LogWarn($"bilidrm_uri 的 kid 不是 32 位 hex，已忽略: {candidate}");
                }
            }
            if (firstVideo.TryGetProperty("widevine_pssh", out var pssh) && pssh.GetString() is string ps && ps.Length > 0)
                parsedResult.PsshBase64 = ps;
        }
        catch (Exception ex) when (ExceptionPolicies.IsMissingResponseNodeFailure(ex))
        { Logger.LogWarn($"DRM license info extraction error: {ex.Message}"); }
    }

    /// <summary>DASH 音轨映射（codecs 串按接口惯例归一：mp4a.40.x→M4A、ec-3→E-AC-3、fLaC→FLAC）。</summary>
    private static void MapDashAudioTracks(ParsedResult parsedResult, List<JsonElement>? audio, int pDur)
    {
        if (audio is null) return;

        foreach (var node in audio)
        {
            var audioId = node.GetValueAsStringSafe("id");
            var codecs = node.GetValueAsStringSafe("codecs");
            codecs = codecs switch
            {
                "mp4a.40.2" => "M4A",
                "mp4a.40.5" => "M4A",
                "ec-3" => "E-AC-3",
                "fLaC" => "FLAC",
                _ => codecs
            };

            parsedResult.AudioTracks.Add(new Audio()
            {
                id = audioId,
                dfn = audioId,
                dur = pDur,
                bandwidth = node.GetInt64Safe("bandwidth") / 1000,
                baseUrl = PickTrackBaseUrl(node),
                codecs = codecs
            });
        }
    }

    /// <summary>背景音与角色配音轨（仅 APP 接口的番剧响应带 dubbing_info）。</summary>
    private static void MapDubbingTracks(
        ParsedResult parsedResult, List<JsonElement>? backgroundAudio, List<JsonElement>? roleAudio, int pDur, PlayRequest request)
    {
        if (backgroundAudio is null || roleAudio is null) return;

        foreach (var node in backgroundAudio)
            parsedResult.BackgroundAudioTracks.Add(MapRawAudioTrack(node, pDur));

        foreach (var role in roleAudio)
        {
            var roleAudioTracks = new List<Audio>();
            foreach (var node in role.EnumerateArraySafe("audio"))
                roleAudioTracks.Add(MapRawAudioTrack(node, pDur));

            parsedResult.RoleAudioList.Add(new AudioMaterialInfo()
            {
                title = role.GetValueAsStringSafe("title"),
                personName = role.GetValueAsStringSafe("person_name"),
                // audio_id 来自接口响应（外部输入）：净化后再拼路径，防 ../ 分隔符写出工作目录（RF-18，与 SubUtil lan 同款收口）
                path = PathUtil.ResolveWorkPath($"{request.Aid}/{request.Aid}.{request.Cid}.{PathUtil.GetValidFileName(role.GetValueAsStringSafe("audio_id"))}.m4a"),
                audio = roleAudioTracks
            });
        }
    }

    /// <summary>配音/背景音轨映射：codecs 原样保留（接口已给 E-AC-3/fLaC 等展示名）。</summary>
    private static Audio MapRawAudioTrack(JsonElement node, int pDur)
    {
        var audioId = node.GetValueAsStringSafe("id");
        return new Audio()
        {
            id = audioId,
            dfn = audioId,
            dur = pDur,
            bandwidth = node.GetInt64Safe("bandwidth") / 1000,
            baseUrl = PickTrackBaseUrl(node),
            codecs = node.GetValueAsStringSafe("codecs")
        };
    }

    /// <summary>FLV（durl）响应解析：最高清晰度重发 → 分段/清晰度映射。</summary>
    private static async Task ExtractDurlTracksAsync(
        PlayRequest request, ParsedResult parsedResult, PlayResponse response, CancellationToken token)
    {
        // 默认以最高清晰度解析。重发响应与首次一样须经业务校验
        //（ThrowIfPlayLimited/ThrowIfBizError）：风控/错误页未经校验会静默
        // 产出零轨道，到下载阶段才失败——正是校验函数注释里自述要避免的场景。
        // 重发失败（业务错误或无 durl）时沿用首次已校验的响应降级，不丢可用轨道。
        string firstWebJson = parsedResult.WebJsonString;
        // 重发可能抛网络/超时/解析异常（dash 分支同款过滤器）：重发失败但
        // 首次响应已通过业务校验且完全可用，沿用首次响应降级，不把整个解析拖垮。
        // 用户取消（Ctrl+C/serve 关停）不被过滤器捕获（SendAsync 用户取消抛的
        // 正是 TaskCanceledException，须在下方先行重抛），向上传播走取消路径。
        JsonDocument? retriedResp = null;
        try
        {
            parsedResult.WebJsonString = await GetPlayJsonAsync(request, GetMaxQn(), token);
            retriedResp = JsonDocument.Parse(parsedResult.WebJsonString);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 同 dash 分支（RF-17）：真正的用户取消须传播，不能被记为"沿用首次结果"。
            throw;
        }
        catch (Exception ex) when (ExceptionPolicies.IsParseDowngradeFailure(ex))
        {
            Logger.LogWarn($"最高清晰度重发失败（沿用首次解析结果）: {ex.Message}");
            parsedResult.WebJsonString = firstWebJson;
        }
        if (retriedResp is not null)
        {
            try
            {
                var pickedRoot = retriedResp.RootElement;
                bool usable = true;
                try
                {
                    ThrowIfPlayLimited(pickedRoot);
                    ThrowIfBizError(pickedRoot);
                }
                catch (InvalidOperationException ex)
                {
                    // 校验失败：沿用首次响应；下面的 finally 负责释放重发文档。
                    // 只捕业务校验异常（两者仅抛 InvalidOperationException），不吞编程错误。
                    usable = false;
                    Logger.LogWarn($"最高清晰度重发被接口拒绝，沿用首次解析结果: {ex.Message}");
                }
                if (usable)
                {
                    pickedRoot = PickDataRoot(pickedRoot);
                    // 只查键存在会放行 "durl": null/空数组 的退化响应（code=0 但零轨道）；
                    // 要求非空数组才接管，否则沿用首次响应，封死静默零轨道路径。
                    usable = pickedRoot.TryGetProperty("durl", out var durlElem)
                        && durlElem.ValueKind == JsonValueKind.Array
                        && durlElem.GetArrayLength() > 0;
                }
                if (usable)
                {
                    response.TakeOver(retriedResp, pickedRoot);
                    retriedResp = null; // 文档所有权已移交，finally 不再释放
                }
                else
                {
                    // 最高清晰度重发无可用 durl：沿用首次（已校验）响应
                    parsedResult.WebJsonString = firstWebJson;
                }
            }
            finally
            {
                retriedResp?.Dispose();
            }
        }

        var root = response.Root;
        //获取所有分段
        double size = 0;
        double length = 0;
        foreach (var node in root.EnumerateArraySafe("durl"))
        {
            parsedResult.Clips.Add(node.GetValueAsStringSafe("url"));
            size += node.GetDoubleSafe("size");
            length += node.GetDoubleSafe("length");
        }
        //TV模式可用清晰度
        if (root.TryGetProperty("qn_extras", out JsonElement qnExtras))
        {
            parsedResult.Dfns.AddRange(qnExtras.EnumerateArray().Select(node => node.GetValueAsStringSafe("qn")));
        }
        else if (root.TryGetProperty("accept_quality", out JsonElement acceptQuality)) //非tv模式可用清晰度
        {
            parsedResult.Dfns.AddRange(acceptQuality.EnumerateArray()
                .Select(node => node.ToString())
                .Where(_qn => !string.IsNullOrEmpty(_qn)));
        }

        // 分段累加出的长度才是本次真正能拿到的内容长度；
        // 充电试看片段正是在这里与 timelength 声称的完整时长产生分歧。
        parsedResult.ActualDurationSec = (int)length / 1000;

        var quality = root.GetValueAsStringSafe("quality");
        Video v = new()
        {
            id = quality,
            dfn = AppSettings.QualityMap.GetValueOrDefault(quality, $"未知({quality})"),
            // FLV 分段由 Clips 承载，轨道自身没有单一媒体地址
            baseUrl = "",
            codecs = GetVideoCodec(root.GetValueAsStringSafe("video_codecid")),
            dur = (int)length / 1000,
            size = size
        };
        if (!parsedResult.VideoTracks.Contains(v)) parsedResult.VideoTracks.Add(v);
    }

    /// <summary>
    /// 番剧片头片尾 → 分段信息，预计效果: 正片? -> 片头 -> 正片 -> 片尾。
    /// </summary>
    private static void ExtractClipInfoPoints(ParsedResult parsedResult, JsonElement root)
    {
        if (!root.TryGetProperty("clip_info_list", out JsonElement clipList)) return;

        parsedResult.ExtraPoints.AddRange(clipList.EnumerateArray().Select(clip => new ViewPoint()
        {
            title = clip.GetValueAsStringSafe("toastText").Replace("即将跳过", ""),
            start = clip.GetInt32Safe("start"),
            end = clip.GetInt32Safe("end")
        })
        );
        parsedResult.ExtraPoints.Sort((p1, p2) => p1.start.CompareTo(p2.start));
        var newPoints = new List<ViewPoint>();
        int lastEnd = 0;
        foreach (var point in parsedResult.ExtraPoints)
        {
            if (lastEnd < point.start)
                newPoints.Add(new ViewPoint() { title = "正片", start = lastEnd, end = point.start });
            newPoints.Add(point);
            lastEnd = point.end;
        }
        parsedResult.ExtraPoints = newPoints;
    }


    /// <summary>
    /// 净化服务端可控文本后再拼入异常消息（B3-L3）：play_detail/message 来自 B 站响应，
    /// 可含控制字符（ANSI 转义/换行）——异常消息会经 Logger 落盘并经 serve API 返回，
    /// 直接拼入会让远端内容向操作者的日志/终端注入转义序列（日志投毒/ANSI 注入面）。
    /// 只剥离控制字符，保留可读内容；serrve 由客户端提交 URL 的注入面已在服务端
    /// SanitizeLogString 收口，这里收 Core 侧解析路径。
    /// </summary>
    private static string SanitizeServerText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsControl(ch)) sb.Append(' ');
            else sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    internal static void ThrowIfPlayLimited(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return;

        if (!root.TryGetProperty("result", out var result))
            return;

        // 仅 web 番剧接口的 result 是承载 play_check 的对象；TV 接口（playurltv /
        // x/tv/playurl）顶层 result 是历史遗留字符串（如 "suee"），非对象没有
        // play_check 可言，必须直接放行——对非对象元素调 TryGetProperty 会抛
        // JsonElementHasWrongType（NativeAOT 下呈现为资源键），曾令 TV 模式
        // 番剧/电影的所有下载在解析第一步即失败。
        if (result.ValueKind != JsonValueKind.Object)
            return;

        if (!result.TryGetProperty("play_check", out var playCheck))
            return;

        var reason = playCheck.GetValueAsStringSafe("limit_play_reason");
        var detail = playCheck.GetValueAsStringSafe("play_detail");
        if (string.IsNullOrWhiteSpace(reason) && string.IsNullOrWhiteSpace(detail))
            return;

        var message = reason switch
        {
            "AREA_LIMIT" => "当前番剧/视频存在区域限制，接口返回不可播放",
            "PAY_LIMIT" => "当前番剧/视频存在付费限制，接口返回不可播放",
            "VIP_LIMIT" => "当前番剧/视频需要大会员权限，接口返回不可播放",
            "TIME_LOCK" => "当前番剧/视频尚未到可播放时间，接口返回不可播放",
            _ => "当前番剧/视频存在播放限制，接口返回不可播放"
        };

        throw new InvalidOperationException($"{message} (limit_play_reason={SanitizeServerText(reason)}, play_detail={SanitizeServerText(detail)})");
    }

    /// <summary>
    /// 对 playurl 响应统一兜底：顶层业务 code != 0（如 -86038 区域限制、-412 风控、-404 视频失效）
    /// 时以可读错误抛出不播放限制，避免 UGC 路径静默解析出空轨道后在下载阶段才失败。
    /// </summary>
    internal static void ThrowIfBizError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number) return;
        if (!code.TryGetInt64(out var codeValue) || codeValue == 0) return;
        var message = root.GetValueAsStringSafe("message", $"接口返回错误码 {codeValue}");
        throw new InvalidOperationException($"接口返回错误: {SanitizeServerText(message)} (code={codeValue})");
    }

    /// <summary>
    /// 识别 playurl 的"人机验证"风控响应：HTTP 200、<c>code=0</c>，但 <c>data</c>（或
    /// <c>result</c> / 顶层）里只有 <c>v_voucher</c> 验证码凭据，既无 dash 也无 durl。
    /// 这类响应是合法 JSON，不会触发业务错误或风控页识别。
    /// 单独抽出谓词供"已累积轨道的多轮请求"降级判定（<see cref="ExtractIntlTracksAsync"/>）：
    /// 那里不能一律抛出，否则会把上一轮已拿到的可用轨道一并丢弃。
    /// </summary>
    internal static bool HasRiskControlVoucher(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (HasVoucher(root)) return true;
        // data 是常规响应的数据根；番剧/课程类接口用 result 承载数据根，同名字段可能挂在其下
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && HasVoucher(data))
            return true;
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object && HasVoucher(result))
            return true;
        return false;

        static bool HasVoucher(JsonElement holder)
            => holder.TryGetProperty("v_voucher", out var voucher)
               && voucher.ValueKind == JsonValueKind.String
               && !string.IsNullOrEmpty(voucher.GetString());
    }

    /// <summary>
    /// 风控人机验证的可读错误。<see cref="InvalidOperationException"/> 是页面级重试
    /// （<c>IsRetryablePageFailure</c>）与 sub check 逐 aid 跳过（<c>IsSkippableItemFailure</c>）
    /// 的成员，因此与 code=-412/-352 等业务风控错误同待遇：按 --retry-count/--retry-delay
    /// 退避重试并打印可读原因。
    /// </summary>
    internal static InvalidOperationException RiskControlVoucherException() => new(
        "接口触发B站风控人机验证：playurl 返回 v_voucher 凭据，没有可用播放地址。"
        + "通常为短时风控，已按重试设置自动退避重试；若持续出现，请稍后重试"
        + "或在浏览器打开该视频完成人机验证后再运行。");

    /// <summary>
    /// 无可用播放数据时的风控兜底：此前会静默落入"无可用轨道"分支，只报一句不透明的
    /// "解析此分P失败(建议--debug查看详细信息)"，且因为不是异常而完全不参与页面级重试——
    /// 风控窗口内 sub check 会整批投稿瞬间全部失败且无任何线索。
    /// <para>仅适用于单次请求（无已累积轨道可保留）的解析入口；多轮请求请改用
    /// <see cref="HasRiskControlVoucher"/> 自行判定降级还是抛出。</para>
    /// </summary>
    internal static void ThrowIfRiskControlVoucher(JsonElement root)
    {
        if (HasRiskControlVoucher(root)) throw RiskControlVoucherException();
    }

    /// <summary>
    /// 编码转换
    /// </summary>
    /// <param name="code"></param>
    /// <returns></returns>
    internal static string GetVideoCodec(string code)
    {
        return code switch
        {
            "13" => "AV1",
            "12" => "HEVC",
            "7" => "AVC",
            _ => "UNKNOWN"
        };
    }

    private static string GetMaxQn()
    {
        var max = AppSettings.QualityMap.Keys
            .Select(k => int.TryParse(k, out var v) ? v : 0)
            .Max();
        return max.ToString();
    }

    [GeneratedRegex("window.__playinfo__=([\\s\\S]*?)<\\/script>")]
    private static partial Regex PlayerJsonRegex();
    // internal 供测试直接验证：只匹配主机:端口，query 中的 ":数字" 不得误判为端口
    [GeneratedRegex("^https?://[^/:]+:\\d+")]
    internal static partial Regex BaseUrlRegex();
}
