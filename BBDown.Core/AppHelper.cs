using BBDown.Core.Protobuf;
using BBDown.Core.Util;
using Google.Protobuf;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBDown.Core;

static partial class AppHelper
{
    /// <summary>调试日志中 PlayViewReply 摘要的最大字符数（防巨响应刷屏/耗内存）。</summary>
    private const int LogJsonSummaryMaxChars = 1024;

    private static readonly string API = "https://grpc.biliapi.net/bilibili.app.playurl.v1.PlayURL/PlayView";
    private static readonly string API2 = "https://app.bilibili.com/bilibili.pgc.gateway.player.v2.PlayURL/PlayView";
    private static readonly string dalvikVer = "2.1.0";
    private static readonly string osVer = "11";
    private static readonly string brand = "M2012K11AC";
    private static readonly string model = "Build/RKQ1.200826.002";
    private static readonly string appVer = "7.32.0";
    private static readonly int build = 7320200; // 新版才能抓到配音
    private static readonly string channel = "xiaomi_cn_tv.danmaku.bili_zm20200902";
    private static readonly Network.Types.TYPE networkType = Network.Types.TYPE.Wifi;
    private static readonly string networkOid = "46007";
    private static readonly string cronet = "1.36.1";
    private static readonly string buvid = "";
    private static readonly string mobiApp = "android";
    private static readonly string appKey = "android64";
    private static readonly string sessionId = "dedf8669";
    private static readonly string platform = "android";
    private static readonly string env = "prod";
    private static readonly int appId = 1;
    private static readonly string region = "CN";
    private static readonly string language = "zh";

    private static PlayViewReq.Types.CodeType GetVideoCodeType(string code)
    {
        return code switch
        {
            "AVC" => PlayViewReq.Types.CodeType.Code264,
            "HEVC" => PlayViewReq.Types.CodeType.Code265,
            "AV1" => PlayViewReq.Types.CodeType.Codeav1,
            _ => PlayViewReq.Types.CodeType.Code265
        };
    }

    /// <summary>
    /// 发起请求并返回响应报文(protobuf -> json)
    /// </summary>
    /// <param name="epId"></param>
    /// <param name="cid"></param>
    /// <param name="qn"></param>
    /// <param name="appkey"></param>
    /// <returns></returns>
    public static async Task<string> DoReqAsync(string aid, string cid, string epId, string qn, bool bangumi, string encoding, string appkey = "", CancellationToken token = default)
    {
        static long ParseId(string value, string name, bool allowEmpty = false)
        {
            if (string.IsNullOrEmpty(value))
            {
                return allowEmpty ? 0 : throw new ArgumentException($"{name} 必须是有效的数字 ID，当前值为空");
            }
            return long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var result)
                ? result
                : throw new ArgumentException($"{name} 必须是有效的数字 ID，当前值: '{value}'");
        }

        var headers = GetHeader(appkey);
        // headers 里的 authorization 携带 access_token，直接序列化会把它写进日志文件
        Logger.LogDebug("App-Req-Headers: {0}",
            JsonSerializer.Serialize(SensitiveDataMasker.MaskHeaderMap(headers), JsonContext.Default.DictionaryStringString));
        byte[] data;
        // 只有pgc接口才有配音和片头尾信息
        if (bangumi)
        {
            if (!(string.IsNullOrEmpty(encoding) || encoding == "HEVC"))
                Logger.LogWarn("APP的番剧不支持 HEVC 以外的编码");
            var body = GetPayload(ParseId(epId, nameof(epId), allowEmpty: true), ParseId(cid, nameof(cid), allowEmpty: false), ParseId(qn, nameof(qn), allowEmpty: true), PlayViewReq.Types.CodeType.Code265);
            data = await HTTPUtil.GetPostResponseAsync(API2, body, headers, token);
        }
        else
        {
            var body = GetPayload(ParseId(aid, nameof(aid), allowEmpty: false), ParseId(cid, nameof(cid), allowEmpty: false), ParseId(qn, nameof(qn), allowEmpty: true), GetVideoCodeType(encoding));
            data = await HTTPUtil.GetPostResponseAsync(API, body, headers, token);
        }
        var resp = new MessageParser<PlayViewReply>(() => new PlayViewReply()).ParseFrom(ReadMessage(data));

        // 调试日志不记录完整 PlayViewReply：其中含每个轨道的 base_url/backup_url
        //（带 sign/deadline 参数的临时签名 CDN 地址），全文落盘会把可用的签名媒体 URL
        // 写进日志文件（与 Parser.cs 对 PlayJson 的自我约束一致）。截断到 1KB 仍可能
        // 容纳完整 URL，因此对 URL 里的 sign 参数值做脱敏，只保留长度 + 摘要。
        if (Config.Current.DebugLog)
        {
            var json = JsonSerializer.Serialize(resp, JsonContext.Default.PlayViewReply);
            var summary = json.Length > LogJsonSummaryMaxChars ? json[..LogJsonSummaryMaxChars] + "…" : json;
            summary = SignedUrlParamRegex().Replace(summary, "$1***");
            Logger.LogDebug("PlayViewReply {0} chars: {1}", json.Length, summary);
        }
        return ConvertToDashJson(resp);
    }

    /// <summary>脱敏 URL 中的签名参数值：sign=<值>（及 x_sign 等变体）在摘要日志里替换为 ***。
    /// 媒体地址的有效性依赖这些签名值，落入日志文件等于把临时下载权外泄。</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"([?&](?:sign|x_sign|w_rid)=)[^&\s""]+")]
    private static partial System.Text.RegularExpressions.Regex SignedUrlParamRegex();

    /// <summary>
    /// 将protobuf转换成网页那种json 这样就不用修改之前的解析逻辑了
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    private static string ConvertToDashJson(object data)
    {
        var resp = (PlayViewReply)data;
        if (resp.VideoInfo == null)
        {
            throw new InvalidOperationException("APP接口未返回视频流信息（可能需要大会员登录或存在区域/播放限制）");
        }

        var videos = new List<object>();
        var audios = new List<object>();
        var clips = new List<object>();

        if (resp.VideoInfo.StreamList != null)
        {
            foreach (var item in resp.VideoInfo.StreamList)
            {
                if (item.DashVideo != null)
                {
                    // StreamInfo 是 optional 消息：畸形帧含 dashVideo 但不含 streamInfo 时
                    // 直接解引用会 NRE（服务端下发任意 protobuf 即可终止本任务，B3-F4）。
                    // 空判回落 0 与下方 DashAudio 分支一致。
                    videos.Add(new AudioInfoWitCodecId(
                        item.StreamInfo?.Quality ?? 0,
                        item.DashVideo.BaseUrl,
                        item.DashVideo.BackupUrl.ToList(),
                        // Size 是粉丝向带宽展示值：size*8 在 ulong 域计算防极端值乘法回绕
                        (uint)checked((ulong)item.DashVideo.Size * 8 / Math.Max(resp.VideoInfo.Timelength / 1000, 1)),
                        item.DashVideo.Codecid
                    ));
                }
            }

            if (videos.Count == 0)
            {
                var limitedItem = resp.VideoInfo.StreamList.FirstOrDefault(s => s.StreamInfo?.Limit != null || s.StreamInfo?.NeedVip == true);
                if (limitedItem != null)
                {
                    var msg = limitedItem.StreamInfo?.Limit?.Msg ?? (limitedItem.StreamInfo?.NeedVip == true ? "需要大会员权限" : "存在播放限制");
                    throw new InvalidOperationException($"APP接口返回播放限制: {msg}");
                }
            }
        }

        if (resp.VideoInfo.DashAudio != null)
        {
            audios.AddRange(resp.VideoInfo.DashAudio.Select(item => new AudioInfoWithCodecName(
                item.Id,
                item.BaseUrl,
                item.BackupUrl.ToList(),
                item.Bandwidth,
                "M4A"
            )));
        }

        if (resp.VideoInfo.Flac != null && resp.VideoInfo.Flac.Audio != null)
        {
            audios.Add(new AudioInfoWithCodecName(
                resp.VideoInfo.Flac.Audio.Id,
                resp.VideoInfo.Flac.Audio.BaseUrl,
                resp.VideoInfo.Flac.Audio.BackupUrl.ToList(),
                resp.VideoInfo.Flac.Audio.Bandwidth,
                "FLAC"
            ));
        }

        if (resp.VideoInfo.Dolby != null && resp.VideoInfo.Dolby.Audio != null)
        {
            audios.Add(new AudioInfoWithCodecName(
                resp.VideoInfo.Dolby.Audio.Id,
                resp.VideoInfo.Dolby.Audio.BaseUrl,
                resp.VideoInfo.Dolby.Audio.BackupUrl.ToList(),
                resp.VideoInfo.Dolby.Audio.Bandwidth,
                "E-AC-3"
            ));
        }

        if (resp.Business != null && resp.Business.ClipInfo != null)
        {
            clips.AddRange(resp.Business.ClipInfo.Select(clip => new DashClip(
                clip.Start,
                clip.End,
                clip.ToastText
            )));
        }

        var backgroundAudios = new List<object>();
        var roles = new List<object>();
        if (resp.PlayExtInfo != null && resp.PlayExtInfo.PlayDubbingInfo != null && resp.PlayExtInfo.PlayDubbingInfo.BackgroundAudio != null)
        {
            var dubInfo = resp.PlayExtInfo.PlayDubbingInfo;

            backgroundAudios.AddRange(dubInfo.BackgroundAudio.Audio.Select(item => new AudioInfoWithCodecName(
                item.Id,
                item.BaseUrl,
                item.BackupUrl.ToList(),
                item.Bandwidth,
                "M4A"
            )));

            foreach (var item in dubInfo.RoleAudioList)
            {
                foreach (var role in item.AudioMaterialList)
                {
                    List<object> roleAudios = role.Audio.Select(item => new AudioInfoWithCodecName(
                        item.Id,
                        item.BaseUrl,
                        item.BackupUrl.ToList(),
                        item.Bandwidth,
                        "M4A"
                    )).Cast<object>().ToList();

                    roles.Add(new AudioMaterial(
                        role.AudioId,
                        role.Title ?? role.AudioId,
                        role.PersonName ?? role.Edition ?? "",
                        roleAudios
                    ));
                }
            }
        }

        var json = new DashJson(
            0,
            "0",
            1,
            new DashData(
                resp.VideoInfo.Timelength,
                new DashInfo(
                    videos,
                    audios
                ),
                clips
            ),
            new DubbingInfo(
                backgroundAudios,
                roles
            )
        );

        return JsonSerializer.Serialize(json, JsonContext.Default.DashJson);
    }

    private static byte[] GetPayload(long aid, long cid, long qn, PlayViewReq.Types.CodeType codec)
    {
        var obj = new PlayViewReq
        {
            EpId = aid,
            Cid = cid,
            // 用户通过 --dfn-priority 指定的清晰度应传递到接口；
            // qn=0（未指定）时保持原默认 127（接口返回全部清晰度），避免默认行为回归
            Qn = qn == 0 ? 127 : qn,
            Fnval = 4048,
            Fourk = true,
            Spmid = "main.ugc-video-detail.0.0",
            FromSpmid = "main.my-history.0.0",
            PreferCodecType = codec,
            Download = 0, //0:播放 1:flv下载 2:dash下载
            ForceHost = 2 //0:允许使用ip 1:使用http 2:使用https
        };
        Logger.LogDebug("PayLoadPlain: {0}", JsonSerializer.Serialize(obj, JsonContext.Default.PlayViewReq));
        return PackMessage(obj.ToByteArray());
    }


    #region 生成Headers相关方法

    private static Dictionary<string, string> GetHeader(string appkey)
    {
        // 不设置 Host 头（RF-26）：HttpClient 会按目标 URI 自动设置正确的 Host——
        // 此前硬编码 grpc.biliapi.net 与番剧 gRPC 目标 app.bilibili.com 不符，
        // TLS SNI 与 Host 头不一致，依赖基础设施忽略 Host 路由，属潜伏脆弱性。
        return new Dictionary<string, string>()
        {
            ["user-agent"] = $"Dalvik/{dalvikVer} (Linux; U; Android {osVer}; {brand} {model}) {appVer} os/android model/{brand} mobi_app/android build/{build} channel/{channel} innerVer/{build} osVer/{osVer} network/2 grpc-java-cronet/{cronet}",
            ["te"] = "trailers",
            ["x-bili-fawkes-req-bin"] = GenerateFawkesReqBin(),
            ["x-bili-metadata-bin"] = GenerateMetadataBin(appkey),
            ["authorization"] = $"identify_v1 {Config.Current.Token}",
            ["x-bili-device-bin"] = GenerateDeviceBin(),
            ["x-bili-network-bin"] = GenerateNetworkBin(),
            ["x-bili-restriction-bin"] = "",
            ["x-bili-locale-bin"] = GenerateLocaleBin(),
            ["x-bili-exps-bin"] = "",
            ["grpc-encoding"] = "gzip",
            ["grpc-accept-encoding"] = "identity,gzip",
            ["grpc-timeout"] = "17996161u",
        };
    }

    private static string GenerateLocaleBin()
    {
        var obj = new Locale
        {
            CLocale = new Locale.Types.LocaleIds
            {
                Language = language,
                Region = region
            }
        };
        return Convert.ToBase64String(obj.ToByteArray());
    }

    private static string GenerateNetworkBin()
    {
        var obj = new Network
        {
            Type = networkType,
            Oid = networkOid
        };
        return Convert.ToBase64String(obj.ToByteArray());
    }

    private static string GenerateDeviceBin()
    {
        var obj = new Device
        {
            AppId = appId,
            Build = build,
            Buvid = buvid,
            MobiApp = mobiApp,
            Platform = platform,
            Channel = channel,
            Brand = brand,
            Model = model,
            Osver = osVer
        };
        return Convert.ToBase64String(obj.ToByteArray());
    }

    private static string GenerateMetadataBin(string appkey)
    {
        var obj = new Metadata
        {
            AccessKey = appkey,
            MobiApp = mobiApp,
            Build = build,
            Channel = channel,
            Buvid = buvid,
            Platform = platform
        };
        return Convert.ToBase64String(obj.ToByteArray());
    }

    private static string GenerateFawkesReqBin()
    {
        var obj = new FawkesReq
        {
            Appkey = appKey,
            Env = env,
            SessionId = sessionId
        };
        return Convert.ToBase64String(obj.ToByteArray());
    }

    #endregion

    /// <summary>
    /// 读取gRPC响应流 通过前5字节信息 解析/解压后面的报文体。
    /// B3-S1/S2：gRPC 响应可能来自被攻破/恶意的服务端（或 --insecure 中间人）——
    /// 帧首字节必须显式校验（gRPC 约定 0=未压缩/1=gzip，其它值报畸形而非静默当未压缩）；
    /// gzip 解压设 48MB 输出上限防解压炸弹占满内存。
    /// </summary>
    /// <param name="data"></param>
    /// <returns>字节流</returns>
    public static byte[] ReadMessage(byte[] data)
    {
        if (data.Length < 5)
            throw new InvalidDataException($"gRPC response too short: {data.Length} bytes");
        (byte first, int size) = ReadInfo(data);
        // B3-S2：gRPC 帧首字节合法值只有 0（未压缩）/ 1（gzip）。其它值说明通信层被破坏
        // 或响应根本不是 gRPC 帧，显式报错替代静默按“未压缩”解析的误导性反序列化错误。
        if (first is not 0 and not 1)
            throw new InvalidDataException($"Invalid gRPC compression flag: 0x{first:X2}（期望 0=未压缩 或 1=gzip）");
        int payloadLen = first == 1 ? data.Length - 5 : Math.Min(size, data.Length - 5);
        if (payloadLen < 0 || (first == 1 && payloadLen == 0))
            throw new InvalidDataException($"Invalid gRPC payload length: {payloadLen}");
        if (first == 1)
            return GzipDecompress(data[5..]);
        return payloadLen == 0 ? [] : data[5..(5 + payloadLen)];
    }

    /// <summary>
    /// 读取报文长度
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    private static (byte first, int size) ReadInfo(byte[] data)
    {
        var value1 = data[0];
        var value2 = data[1..5];

        return (value1, BinaryPrimitives.ReadInt32BigEndian(value2));
    }

    /// <summary>
    /// 给请求载荷添加头部信息
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static byte[] PackMessage(byte[] input)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            var comp = GzipCompress(input);
            var reverse = (stackalloc byte[4]);
            writer.Write((byte)1);
            BinaryPrimitives.WriteInt32BigEndian(reverse, comp.Length);
            writer.Write(reverse);
            writer.Write(comp);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// gzip压缩
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    private static byte[] GzipCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var comp = new GZipStream(output, CompressionMode.Compress))
        {
            comp.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    /// <summary>
    /// gzip解压
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    private static byte[] GzipDecompress(byte[] data)
    {
        // B3-S1：解压上限防 gzip 炸弹——恶意/被攻破的服务端可下发小压缩包膨胀为 GB 级
        // 内存占用。上限 48MB（合法播放响应远小于此，参见 DRM/播放数据量级）。
        const int MaxDecompressedBytes = 48 * 1024 * 1024;
        using var output = new MemoryStream();
        using (var input = new MemoryStream(data))
        {
            using var decomp = new GZipStream(input, CompressionMode.Decompress);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                int read = decomp.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                if (output.Length + read > MaxDecompressedBytes)
                    throw new InvalidDataException($"gRPC gzip 解压超过 {MaxDecompressedBytes / 1024 / 1024}MB 上限（疑似解压炸弹），已中止");
                output.Write(buffer, 0, read);
            }
        }
        return output.ToArray();
    }
}


[JsonSerializable(typeof(AudioMaterial))]
[JsonSerializable(typeof(DubbingInfo))]
[JsonSerializable(typeof(DashClip))]
[JsonSerializable(typeof(AudioInfoWithCodecName))]
[JsonSerializable(typeof(AudioInfoWitCodecId))]
[JsonSerializable(typeof(DashJson))]
[JsonSerializable(typeof(PlayViewReq))]
[JsonSerializable(typeof(PlayViewReply))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class JsonContext : JsonSerializerContext { }

internal class AudioMaterial
{
    [JsonPropertyName("audio_id")]
    public string AudioId { get; }
    [JsonPropertyName("title")]
    public string Title { get; }
    [JsonPropertyName("person_name")]
    public string PersonName { get; }
    [JsonPropertyName("audio")]
    public List<object> Audio { get; }

    public AudioMaterial(string audio_id, string title, string person_name, List<object> audio)
    {
        AudioId = audio_id;
        Title = title;
        PersonName = person_name;
        Audio = audio;
    }

    public override bool Equals(object? obj) => obj is AudioMaterial other && AudioId == other.AudioId && Title == other.Title && PersonName == other.PersonName && Audio == other.Audio;
    public override int GetHashCode() => HashCode.Combine(Title, Audio);
}

internal class DubbingInfo
{
    [JsonPropertyName("background_audio")]
    public List<object> BackgroundAudio { get; }
    [JsonPropertyName("role_audio_list")]
    public List<object> RoleAudioList { get; }

    public DubbingInfo(List<object> background_audio, List<object> role_audio_list)
    {
        BackgroundAudio = background_audio;
        RoleAudioList = role_audio_list;
    }

    public override bool Equals(object? obj) => obj is DubbingInfo other && BackgroundAudio == other.BackgroundAudio && RoleAudioList == other.RoleAudioList;
    public override int GetHashCode() => HashCode.Combine(BackgroundAudio, RoleAudioList);
}

internal class DashClip
{
    [JsonPropertyName("start")]
    public int Start { get; }
    [JsonPropertyName("end")]
    public int End { get; }
    [JsonPropertyName("toastText")]
    public string ToastText { get; }

    public DashClip(int start, int end, string toastText)
    {
        Start = start;
        End = end;
        ToastText = toastText;
    }

    public override bool Equals(object? obj) => obj is DashClip other && Start == other.Start && End == other.End && ToastText == other.ToastText;
    public override int GetHashCode() => HashCode.Combine(Start, End, ToastText);
}

internal class AudioInfoWithCodecName
{
    [JsonPropertyName("id")]
    public uint Id { get; }
    [JsonPropertyName("base_url")]
    public string BaseUrl { get; }
    [JsonPropertyName("backup_url")]
    public List<string> BackupUrl { get; }
    [JsonPropertyName("bandwidth")]
    public uint Bandwidth { get; }
    [JsonPropertyName("codecs")]
    public string Codecs { get; }

    public AudioInfoWithCodecName(uint id, string base_url, List<string> backup_url, uint bandwidth, string codecs)
    {
        Id = id;
        BaseUrl = base_url;
        BackupUrl = backup_url;
        Bandwidth = bandwidth;
        Codecs = codecs;
    }

    public override bool Equals(object? obj) => obj is AudioInfoWithCodecName other && Id == other.Id && BaseUrl == other.BaseUrl && BackupUrl.SequenceEqual(other.BackupUrl) && Bandwidth == other.Bandwidth && Codecs == other.Codecs;
    public override int GetHashCode() => HashCode.Combine(Id, BaseUrl, BackupUrl, Bandwidth, Codecs);
}

internal class AudioInfoWitCodecId
{
    [JsonPropertyName("id")]
    public uint Id { get; }
    [JsonPropertyName("base_url")]
    public string BaseUrl { get; }
    [JsonPropertyName("backup_url")]
    public List<string> BackupUrl { get; }
    [JsonPropertyName("bandwidth")]
    public uint Bandwidth { get; }
    [JsonPropertyName("codecid")]
    public uint Codecid { get; }

    public AudioInfoWitCodecId(uint id, string base_url, List<string> backup_url, uint bandwidth, uint codecid)
    {
        Id = id;
        BaseUrl = base_url;
        BackupUrl = backup_url;
        Bandwidth = bandwidth;
        Codecid = codecid;
    }

    public override bool Equals(object? obj) => obj is AudioInfoWitCodecId other && Id == other.Id && BaseUrl == other.BaseUrl && Bandwidth == other.Bandwidth && Codecid == other.Codecid;
    public override int GetHashCode() => HashCode.Combine(Id, BaseUrl, Bandwidth, Codecid);
}

internal class DashInfo
{
    [JsonPropertyName("video")]
    public List<object> Video { get; }
    [JsonPropertyName("audio")]
    public List<object> Audio { get; }

    public DashInfo(List<object> video, List<object> audio)
    {
        Video = video;
        Audio = audio;
    }

    public override bool Equals(object? obj) => obj is DashInfo other && EqualityComparer<List<object>>.Default.Equals(Video, other.Video) && EqualityComparer<List<object>>.Default.Equals(Audio, other.Audio);
    public override int GetHashCode() => HashCode.Combine(Video, Audio);
}

internal class DashData
{
    [JsonPropertyName("timelength")]
    public ulong TimeLength { get; }
    [JsonPropertyName("dash")]
    public DashInfo Dash { get; }
    [JsonPropertyName("clip_info_list")]
    public List<object> ClipList { get; }

    public DashData(ulong timelength, DashInfo dash, List<object> clipList)
    {
        TimeLength = timelength;
        Dash = dash;
        ClipList = clipList;
    }

    public override bool Equals(object? obj) => obj is DashData other && TimeLength == other.TimeLength && EqualityComparer<DashInfo>.Default.Equals(Dash, other.Dash) && EqualityComparer<List<object>>.Default.Equals(ClipList, other.ClipList);
    public override int GetHashCode() => HashCode.Combine(TimeLength, Dash, ClipList);
}

internal class DashJson
{
    [JsonPropertyName("code")]
    public int Code { get; }
    [JsonPropertyName("message")]
    public string Message { get; }
    [JsonPropertyName("ttl")]
    public int Ttl { get; }
    [JsonPropertyName("data")]
    public DashData Data { get; }
    [JsonPropertyName("dubbing_info")]
    public DubbingInfo DubbingInfo { get; }

    public DashJson(int code, string message, int ttl, DashData data, DubbingInfo dubbingInfo)
    {
        Code = code;
        Message = message;
        Ttl = ttl;
        Data = data;
        DubbingInfo = dubbingInfo;
    }

    public override bool Equals(object? obj) => obj is DashJson other && Code == other.Code && Message == other.Message && Ttl == other.Ttl && EqualityComparer<DashData>.Default.Equals(Data, other.Data);
    public override int GetHashCode() => HashCode.Combine(Code, Message, Ttl, Data, DubbingInfo);
}
