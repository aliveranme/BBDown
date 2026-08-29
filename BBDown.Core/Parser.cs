using System.Text;
using BBDown.Core.Util;
using System.Text.RegularExpressions;
using System.Text.Json;
using static BBDown.Core.Entity.Entity;
using System.Security.Cryptography;
using BBDown.Core.Entity;

namespace BBDown.Core;

public static partial class Parser
{
    /// <summary>调试日志中 PlayJson 摘要的最大字符数（防巨响应刷屏/耗内存）。</summary>
    private const int LogJsonSummaryMaxChars = 1024;

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

    private static async Task<string> GetPlayJsonAsync(string encoding, string aidOri, string aid, string cid, string epId, bool tvApi, bool intl, bool appApi, bool wantDrm, string qn = "0", CancellationToken token = default)
    {
        Logger.LogDebug("aid={0},cid={1},epId={2},tvApi={3},IntlApi={4},appApi={5},qn={6}", aid, cid, epId, tvApi, intl, appApi, qn);

        if (intl) return await GetPlayJsonAsync(aid, cid, epId, qn, token: token);


        bool cheese = aidOri.StartsWith("cheese:");
        bool bangumi = cheese || aidOri.StartsWith("ep:");
        Logger.LogDebug("bangumi={0},cheese={1}", bangumi, cheese);

        if (appApi) return await AppHelper.DoReqAsync(aid, cid, epId, qn, bangumi, encoding, Config.Current.Token, token);

        string prefix = tvApi ? bangumi ? $"{Config.Current.TvHost}/pgc/player/api/playurltv" : $"{Config.Current.TvHost}/x/tv/playurl"
            : bangumi ? $"{Config.Current.Host}/pgc/player/web/v2/playurl" : $"{Config.Current.Host}/x/player/wbi/playurl";
        prefix = $"{WithApiScheme(prefix)}?";

        string api;
        if (tvApi)
        {
            StringBuilder apiBuilder = new();
            if (Config.Current.Token != "") apiBuilder.Append($"access_key={Config.Current.Token}&");
            apiBuilder.Append($"appkey=4409e2ce8ffd12b8&build=106500&cid={cid}&device=android");
            if (bangumi) apiBuilder.Append($"&ep_id={epId}&expire=0");
            apiBuilder.Append($"&fnval=4048&fnver=0&fourk=1&mid=0&mobi_app=android_tv_yst");
            apiBuilder.Append($"&object_id={aid}&platform=android&playurl_type=1&qn={qn}&ts={GetTimeStamp(true)}");
            api = $"{prefix}{apiBuilder}&sign={GetSign(apiBuilder.ToString(), false)}";
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
        string webJson = await HTTPUtil.GetWebSourceAsync(api, token: token);
        //以下情况从网页源代码尝试解析
        if (webJson.Contains("\"大会员专享限制\""))
        {
            Logger.Log("此视频需要大会员，您大概率需要登录一个有大会员的账号才可以下载，尝试从网页源码解析");
            // 该回退只对番剧成立：UGC 的 epId 为空，构造 /bangumi/play/ep<空> 会拿到
            // 无效页面并被 PlayerJsonRegex 误替换成空/垃圾内容，导致后续解析失败
            if (!string.IsNullOrEmpty(epId))
            {
                string webUrl = "https://www.bilibili.com/bangumi/play/ep" + epId;
                string webSource = await HTTPUtil.GetWebSourceAsync(webUrl, token: token, rejectHtml: false);
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

    private static async Task<string> GetPlayJsonAsync(string aid, string cid, string epId, string qn, string code = "0", CancellationToken token = default)
    {
        bool isBiliPlus = Config.Current.Host != "api.bilibili.com";
        string api = $"{WithApiScheme(isBiliPlus ? Config.Current.Host : "api.biliintl.com")}/intl/gateway/v2/ogv/playurl?";

        StringBuilder paramBuilder = new();
        if (Config.Current.Token != "") paramBuilder.Append($"access_key={Config.Current.Token}&");
        paramBuilder.Append($"aid={aid}");
        if (isBiliPlus) paramBuilder.Append($"&appkey=7d089525d3611b1c&area={(Config.Current.Area == "" ? "th" : Config.Current.Area)}");
        paramBuilder.Append($"&cid={cid}&ep_id={epId}&platform=android&prefer_code_type={code}&qn={qn}");
        if (isBiliPlus) paramBuilder.Append($"&ts={GetTimeStamp(true)}");

        paramBuilder.Append("&s_locale=zh_SG");
        string param = paramBuilder.ToString();
        api += (isBiliPlus ? $"{param}&sign={GetSign(param, true)}" : param);

        string webJson = await HTTPUtil.GetWebSourceAsync(api, token: token);
        return webJson;
    }

    public static async Task<ParsedResult> ExtractTracksAsync(string aidOri, string aid, string cid, string epId, bool tvApi, bool intlApi, bool appApi, string encoding, bool wantDrm = false, string qn = "0", CancellationToken token = default)
    {
        ParsedResult parsedResult = new();

        //调用解析
        parsedResult.WebJsonString = await GetPlayJsonAsync(encoding, aidOri, aid, cid, epId, tvApi, intlApi, appApi, wantDrm, qn, token);

        // 调试日志不记录完整播放 JSON：其中包含带签名的媒体地址（deadline/sign 参数），
        // 全文落盘会把可用的临时签名 URL 写进日志文件。只记录长度 + 前 1KB 摘要，
        // 排查问题足够，避免签名媒体地址泄漏到日志。
        if (Config.Current.DebugLog)
        {
            Logger.LogDebug("PlayJson {0} chars: {1}",
                parsedResult.WebJsonString.Length,
                parsedResult.WebJsonString.Length > LogJsonSummaryMaxChars ? parsedResult.WebJsonString[..LogJsonSummaryMaxChars] + "…" : parsedResult.WebJsonString);
        }

        //intl接口需要两次请求(code=0和code=1)
        if (intlApi)
        {
            foreach (var code in new[] { "0", "1" })
            {
                if (code == "1")
                    parsedResult.WebJsonString = await GetPlayJsonAsync(aid, cid, epId, qn, code, token);

                using var intlJson = JsonDocument.Parse(parsedResult.WebJsonString);
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
                            var urlList = new List<string>() { dashVideo.GetValueAsStringSafe("base_url") };
                            urlList.AddRange(dashVideo.EnumerateArraySafe("backup_url").Select(i => i.ToString()));
                            Video v = new()
                            {
                                dur = pDur,
                                id = videoId,
                                dfn = AppSettings.QualityMap.GetValueOrDefault(videoId, $"未知({videoId})"),
                                bandwidth = dashVideo.GetInt64Safe("bandwidth") / 1000,
                                baseUrl = urlList.FirstOrDefault(i => !BaseUrlRegex().IsMatch(i), urlList.First()),
                                codecs = GetVideoCodec(dashVideo.GetValueAsStringSafe("codecid")),
                                size = dashVideo.GetDoubleSafe("size")
                            };
                            if (!parsedResult.VideoTracks.Contains(v)) parsedResult.VideoTracks.Add(v);
                        }
                    }
                }

                foreach (var node in audioElements)
                {
                    var urlList = new List<string>() { node.GetValueAsStringSafe("base_url") };
                    urlList.AddRange(node.EnumerateArraySafe("backup_url").Select(i => i.ToString()));
                    Audio a = new()
                    {
                        id = node.GetValueAsStringSafe("id"),
                        dfn = node.GetValueAsStringSafe("id"),
                        dur = pDur,
                        bandwidth = node.GetInt64Safe("bandwidth") / 1000,
                        baseUrl = urlList.FirstOrDefault(i => !BaseUrlRegex().IsMatch(i), urlList.First()),
                        codecs = "M4A"
                    };
                    if (!parsedResult.AudioTracks.Contains(a)) parsedResult.AudioTracks.Add(a);
                }
            }
            return parsedResult;
        }

        var respJson = JsonDocument.Parse(parsedResult.WebJsonString);
        var data = respJson.RootElement;
        try
        {
            ThrowIfPlayLimited(data);
            // UGC 的播放限制通过顶层业务 code 表达（区域限制 -86038、风控 -412、视频失效 -404 等），
            // 而 play_check 只在 pgc 响应的 result 节点出现、对 UGC 不可达，这里统一兜底
            ThrowIfBizError(data);
        }
        catch
        {
            // 校验抛出的异常路径不会走到方法末尾的 respJson.Dispose()：
            // JsonDocument 内部租用 ArrayPool 缓冲，不释放会造成池化内存积压
            respJson.Dispose();
            throw;
        }
        // 外层 try/finally：覆盖 DRM 提取 throw、GetPlayJsonAsync await 抛错等所有中途
        // 异常路径——respJson 已 parse 但未走到方法末尾 dispose 时，由 finally 统一释放
        //（JsonDocument.Dispose 幂等，与显式释放路径不冲突）。
        try
        {
            // 根据API版本自动定位数据节点
            JsonElement root;
            if (data.TryGetProperty("result", out var resultElem) && resultElem.ValueKind == JsonValueKind.Object)
            {
                root = resultElem.TryGetProperty("video_info", out var vi) ? vi : resultElem;
            }
            else if (data.TryGetProperty("data", out var dataElem))
            {
                root = dataElem;
            }
            else
            {
                root = data;
            }

            bool bangumi = aidOri.StartsWith("ep:");

            if (root.TryGetProperty("dash", out _)) //dash
            {
                List<JsonElement>? audio = null;
                List<JsonElement>? video = null;
                List<JsonElement>? backgroundAudio = null;
                List<JsonElement>? roleAudio = null;
                int pDur = 0;

                if (root.TryGetProperty("dash", out var dashElem))
                    pDur = dashElem.GetInt32Safe("duration");
                if (pDur == 0)
                    pDur = root.GetInt32Safe("timelength") / 1000;

                parsedResult.ActualDurationSec = pDur;

                // DRM metadata
                parsedResult.IsDrm = root.GetBooleanSafe("is_drm");
                parsedResult.DrmTechType = root.GetInt32Safe("drm_tech_type");
                parsedResult.DrmType = root.GetValueAsStringSafe("drm_type");
                if (parsedResult.IsDrm) Logger.LogDebug("DRM detected: type={0}, tech={1}", parsedResult.DrmType, parsedResult.DrmTechType);

                //免二压视频需要重新请求
                for (int reparsePass = 0; reparsePass < 2; reparsePass++)
                {
                    if (reparsePass == 1)
                    {
                        if (appApi) break; //只有非APP接口需要免二压
                        try
                        {
                            var reparsePlayJson = await GetPlayJsonAsync(encoding, aidOri, aid, cid, epId, tvApi, intlApi, appApi, wantDrm, GetMaxQn(), token);
                            var newResp = JsonDocument.Parse(reparsePlayJson);
                            var newRoot = newResp.RootElement;
                            ThrowIfBizError(newRoot);
                            ThrowIfPlayLimited(newRoot);
                            var pickedRoot = newRoot.TryGetProperty("result", out var rr) && rr.ValueKind == JsonValueKind.Object && rr.TryGetProperty("video_info", out var vvii) ? vvii :
                                   newRoot.TryGetProperty("result", out var rr2) && rr2.ValueKind == JsonValueKind.Object ? rr2 :
                                   newRoot.TryGetProperty("data", out var dd) ? dd : newRoot;
                            if (pickedRoot.TryGetProperty("dash", out var newDash) && newDash.TryGetProperty("video", out _))
                            {
                                respJson.Dispose(); // 旧文档退役，新文档接管生命周期
                                respJson = newResp;
                                root = pickedRoot;
                                parsedResult.WebJsonString = reparsePlayJson;
                                video = newDash.TryGetProperty("video", out var newVidArr) ? newVidArr.EnumerateArray().ToList() : null;
                                audio = newDash.TryGetProperty("audio", out var newAudArr) ? newAudArr.EnumerateArray().ToList() : null;
                            }
                            else
                            {
                                newResp.Dispose();
                            }
                        }
                        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TimeoutException or TaskCanceledException)
                        {
                            Logger.LogDebug("免二压重新请求失败（降级沿用第一轮结果）: {0}", ex.Message);
                        }
                    }
                    if (root.TryGetProperty("dash", out var dash) && dash.TryGetProperty("video", out var vidArr))
                        video = vidArr.EnumerateArray().ToList();
                    if (root.TryGetProperty("dash", out dash) && dash.TryGetProperty("audio", out var audArr))
                        audio = audArr.EnumerateArray().ToList();

                    if (appApi && bangumi)
                    {
                        if (data.TryGetProperty("dubbing_info", out var dub) && dub.TryGetProperty("background_audio", out var bgArr))
                            backgroundAudio = bgArr.EnumerateArray().ToList();
                        if (data.TryGetProperty("dubbing_info", out dub) && dub.TryGetProperty("role_audio_list", out var roleArr))
                            roleAudio = roleArr.EnumerateArray().ToList();
                    }
                    //处理杜比音频
                    try
                    {
                        if (!tvApi && root.GetPropertySafe("dash").TryGetProperty("dolby", out JsonElement dolby))
                        {
                            if (dolby.TryGetProperty("audio", out JsonElement db))
                            {
                                audio ??= new List<JsonElement>();
                                audio.AddRange(db.EnumerateArray());
                            }
                        }
                    }
                    catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
                    { Logger.LogDebug("杜比音频解析失败: {0}", e.Message); }

                    //处理Hi-Res无损
                    try
                    {
                        if (!tvApi && root.GetPropertySafe("dash").TryGetProperty("flac", out JsonElement hiRes))
                        {
                            if (hiRes.TryGetProperty("audio", out JsonElement db))
                            {
                                if (db.ValueKind != JsonValueKind.Null)
                                {
                                    audio ??= new List<JsonElement>();
                                    audio.Add(db);
                                }
                            }
                        }
                    }
                    catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
                    { Logger.LogDebug("Hi-Res音频解析失败: {0}", e.Message); }

                    if (video != null)
                    {
                        foreach (var node in video)
                        {
                            var urlList = new List<string>() { node.GetValueAsStringSafe("base_url") };
                            urlList.AddRange(node.EnumerateArraySafe("backup_url").Select(i => i.ToString()));
                            var videoId = node.GetValueAsStringSafe("id");
                            Video v = new()
                            {
                                dur = pDur,
                                id = videoId,
                                dfn = AppSettings.QualityMap.GetValueOrDefault(videoId, $"未知({videoId})"),
                                bandwidth = node.GetInt64Safe("bandwidth") / 1000,
                                baseUrl = urlList.FirstOrDefault(i => !BaseUrlRegex().IsMatch(i), urlList.First()),
                                codecs = GetVideoCodec(node.GetValueAsStringSafe("codecid")),
                                size = node.GetDoubleSafe("size")
                            };
                            if (!tvApi && !appApi)
                            {
                                v.res = node.GetValueAsStringSafe("width") + "x" + node.GetValueAsStringSafe("height");
                                v.fps = node.GetValueAsStringSafe("frame_rate");
                            }
                            if (!parsedResult.VideoTracks.Contains(v)) parsedResult.VideoTracks.Add(v);
                        }

                        if (parsedResult.IsDrm && string.IsNullOrEmpty(parsedResult.KidHex))
                        {
                            try
                            {
                                var firstVideo = video.FirstOrDefault();
                                if (firstVideo.ValueKind == System.Text.Json.JsonValueKind.Undefined)
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
                            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
                            { Logger.LogWarn($"DRM license info extraction error: {ex.Message}"); }
                        }
                    }

                } // end for reparsePass

                if (audio != null)
                {
                    foreach (var node in audio)
                    {
                        var urlList = new List<string>() { node.GetValueAsStringSafe("base_url") };
                        urlList.AddRange(node.EnumerateArraySafe("backup_url").Select(i => i.ToString()));
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
                            baseUrl = urlList.FirstOrDefault(i => !BaseUrlRegex().IsMatch(i), urlList.First()),
                            codecs = codecs
                        });
                    }
                }

                if (backgroundAudio != null && roleAudio != null)
                {
                    foreach (var node in backgroundAudio)
                    {
                        var audioId = node.GetValueAsStringSafe("id");
                        var urlList = new List<string> { node.GetValueAsStringSafe("base_url") };
                        urlList.AddRange(node.EnumerateArraySafe("backup_url").Select(i => i.ToString()));
                        parsedResult.BackgroundAudioTracks.Add(new Audio()
                        {
                            id = audioId,
                            dfn = audioId,
                            dur = pDur,
                            bandwidth = node.GetInt64Safe("bandwidth") / 1000,
                            baseUrl = urlList.FirstOrDefault(i => !BaseUrlRegex().IsMatch(i), urlList.First()),
                            codecs = node.GetValueAsStringSafe("codecs")
                        });
                    }

                    foreach (var role in roleAudio)
                    {
                        var roleAudioTracks = new List<Audio>();
                        foreach (var node in role.EnumerateArraySafe("audio"))
                        {
                            var audioId = node.GetValueAsStringSafe("id");
                            var urlList = new List<string> { node.GetValueAsStringSafe("base_url") };
                            urlList.AddRange(node.EnumerateArraySafe("backup_url").Select(i => i.ToString()));
                            roleAudioTracks.Add(new Audio()
                            {
                                id = audioId,
                                dfn = audioId,
                                dur = pDur,
                                bandwidth = node.GetInt64Safe("bandwidth") / 1000,
                                baseUrl = urlList.FirstOrDefault(i => !BaseUrlRegex().IsMatch(i), urlList.First()),
                                codecs = node.GetValueAsStringSafe("codecs")
                            });
                        }
                        parsedResult.RoleAudioList.Add(new AudioMaterialInfo()
                        {
                            title = role.GetValueAsStringSafe("title"),
                            personName = role.GetValueAsStringSafe("person_name"),
                            path = PathUtil.ResolveWorkPath($"{aid}/{aid}.{cid}.{role.GetValueAsStringSafe("audio_id")}.m4a"),
                            audio = roleAudioTracks
                        });
                    }
                }
            }
            else if (root.TryGetProperty("durl", out _)) //flv
            {
                // 默认以最高清晰度解析。重发响应与首次一样须经业务校验
                //（ThrowIfPlayLimited/ThrowIfBizError）：风控/错误页未经校验会静默
                // 产出零轨道，到下载阶段才失败——正是校验函数注释里自述要避免的场景。
                // 重发失败（业务错误或无 durl）时沿用首次已校验的响应降级，不丢可用轨道。
                string firstWebJson = parsedResult.WebJsonString;
                var firstRoot = root;
                // 重发可能抛网络/超时/解析异常（dash 分支同款过滤器）：重发失败但
                // 首次响应已通过业务校验且完全可用，沿用首次响应降级，不把整个解析拖垮。
                // 真正的用户取消（OperationCanceledException，非 TaskCanceledException）
                // 不被过滤器捕获，向上传播走取消路径。
                JsonDocument? retriedResp = null;
                try
                {
                    parsedResult.WebJsonString = await GetPlayJsonAsync(encoding, aidOri, aid, cid, epId, tvApi, intlApi, appApi, wantDrm, GetMaxQn(), token);
                    retriedResp = JsonDocument.Parse(parsedResult.WebJsonString);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TimeoutException or TaskCanceledException)
                {
                    Logger.LogWarn($"最高清晰度重发失败（沿用首次解析结果）: {ex.Message}");
                    parsedResult.WebJsonString = firstWebJson;
                    root = firstRoot;
                }
                if (retriedResp is not null)
                {
                    var pickedRoot = retriedResp.RootElement;
                    bool usable = true;
                    try
                    {
                        ThrowIfPlayLimited(retriedResp.RootElement);
                        ThrowIfBizError(retriedResp.RootElement);
                    }
                    catch (InvalidOperationException ex)
                    {
                        // 校验失败：沿用首次响应（fallback 分支负责 Dispose retriedResp）。
                        // 只捕业务校验异常（两者仅抛 InvalidOperationException），不吞编程错误。
                        usable = false;
                        Logger.LogWarn($"最高清晰度重发被接口拒绝，沿用首次解析结果: {ex.Message}");
                    }
                    if (usable)
                    {
                        if (pickedRoot.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object)
                            pickedRoot = r.TryGetProperty("video_info", out var vi) ? vi : r;
                        else if (pickedRoot.TryGetProperty("data", out var d))
                            pickedRoot = d;
                        // 只查键存在会放行 "durl": null/空数组 的退化响应（code=0 但零轨道）；
                        // 要求非空数组才接管，否则沿用首次响应，封死静默零轨道路径。
                        usable = pickedRoot.TryGetProperty("durl", out var durlElem)
                            && durlElem.ValueKind == JsonValueKind.Array
                            && durlElem.GetArrayLength() > 0;
                    }
                    if (usable)
                    {
                        respJson.Dispose(); // 旧文档退役，新文档接管生命周期
                        respJson = retriedResp;
                        root = pickedRoot;
                    }
                    else
                    {
                        // 最高清晰度重发无可用 durl：沿用首次（已校验）响应
                        retriedResp.Dispose();
                        parsedResult.WebJsonString = firstWebJson;
                        root = firstRoot;
                    }
                }
                string quality = "";
                string videoCodecid = "";
                string url = "";
                double size = 0;
                double length = 0;

                quality = root.GetValueAsStringSafe("quality");
                videoCodecid = root.GetValueAsStringSafe("video_codecid");
                //获取所有分段
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

                Video v = new()
                {
                    id = quality,
                    dfn = AppSettings.QualityMap.GetValueOrDefault(quality, $"未知({quality})"),
                    baseUrl = url,
                    codecs = GetVideoCodec(videoCodecid),
                    dur = (int)length / 1000,
                    size = size
                };
                if (!parsedResult.VideoTracks.Contains(v)) parsedResult.VideoTracks.Add(v);
            }

            // 番剧片头片尾转分段信息, 预计效果: 正片? -> 片头 -> 正片 -> 片尾
            if (bangumi)
            {
                if (root.TryGetProperty("clip_info_list", out JsonElement clipList))
                {
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

            }

            respJson.Dispose();
            return parsedResult;
        }
        finally
        {
            respJson.Dispose();
        }
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
        if (!root.TryGetProperty("result", out var result))
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

    private static string GetTimeStamp(bool bflag)
    {
        // 经服务器时钟偏移校准（ServerClock）：本地时钟偏差超 ~60s 时效窗口会让 wts/ts
        // 被 B 站拒绝签名（虚拟机时钟不同步/未启用 NTP 的容器等）。offset=0 时与 UTC
        // 当前时间等价，行为零回归。
        DateTimeOffset ts = ServerClock.Now;
        return bflag ? ts.ToUnixTimeSeconds().ToString() : ts.ToUnixTimeMilliseconds().ToString();
    }

    private static string GetSign(string parameters, bool isBiliPlus)
    {
        string toEncode = parameters + (isBiliPlus ? "acd495b248ec528c2eed1e862d393126" : "59b43e04ad6965f34319062b478f83dd");
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(toEncode)));
    }

    [GeneratedRegex("window.__playinfo__=([\\s\\S]*?)<\\/script>")]
    private static partial Regex PlayerJsonRegex();
    [GeneratedRegex("http.*:\\d+")]
    private static partial Regex BaseUrlRegex();
}
