using BBDown.Core;
using BBDown.Core.Entity;
using static BBDown.Core.Entity.Entity;

namespace BBDown.Tests;

/// <summary>
/// ExtractTracksAsync 夹具回放护栏（MAINTENANCE_PLAN 批次一）：
/// 本地假 B 站 API 服务器（<see cref="FakeBilibiliApiServer"/>）+ Fixtures/parser/*.json 回放，
/// 锁住解析主干行为（数据根定位、轨道映射、免二压/durl 重发协议、DRM 提取、业务错误传播）。
/// 下次 B 站改接口返回形状时，红掉的用例直接指明断在哪个节点。
/// 离线可重复，随 PR 单测 job 运行；严禁标 NetworkIntegration。
/// </summary>
public class ParserFixtureTests
{
    private static string LoadFixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "parser", name));

    /// <summary>
    /// G8 卫生约定：快照 Config.Current，用例内 Apply 测试配置（Host/TvHost 定向本地假服务器
    /// + WBI 测试密钥 + 清空凭据），finally 恢复快照。套件已禁用并行
    /// （DisableTestParallelization），恢复全局为兜底而非竞态依赖。
    /// </summary>
    private static async Task WithFakeApiAsync(FakeBilibiliApiServer server, Func<Task> act,
        Func<AppSettings, AppSettings>? customize = null)
    {
        var snapshot = Config.Current;
        try
        {
            var settings = snapshot with
            {
                Host = $"http://127.0.0.1:{server.Port}",
                Wbi = "test_wbi_key",
                Cookie = "",
                Token = "",
                Area = "",
            };
            if (customize is not null) settings = customize(settings);
            Config.Apply(settings);
            await act();
        }
        finally
        {
            Config.Apply(snapshot);
        }
    }

    private static async Task<ParsedResult> ExtractAsync(string aidOri, string aid, string cid, string epId = "",
        bool tvApi = false, bool intlApi = false, bool appApi = false, bool wantDrm = false, string qn = "0")
        => await Parser.ExtractTracksAsync(aidOri, aid, cid, epId, tvApi, intlApi, appApi, "", wantDrm, qn);

    private static void AssertWbiSignedQuery(string query)
    {
        Assert.Contains("w_rid=", query);
        Assert.Contains("wts=", query);
        Assert.Matches(@"w_rid=[0-9a-f]{32}", query);
    }

    // ── F01：UGC data 根 DASH（fnval=4048 标准形状）──

    [Fact]
    public async Task UgcWebDash_ParsesTracks_CodecMapAndRes()
    {
        using var server = new FakeBilibiliApiServer();
        server.Register("/x/player/wbi/playurl", LoadFixture("ugc-web-dash.json"));
        await WithFakeApiAsync(server, async () =>
        {
            var result = await ExtractAsync("av170001", "170001", "999");

            // 免二压协议：qn=0 首请求 + qn=127 重发，均为 WBI 签名的 UGC web playurl
            Assert.Equal(2, server.Requests.Count);
            Assert.All(server.Requests, r =>
            {
                Assert.Equal("/x/player/wbi/playurl", r.Path);
                AssertWbiSignedQuery(r.Query);
                Assert.Contains("avid=170001", r.Query);
                Assert.Contains("cid=999", r.Query);
                Assert.Contains("fnval=4048", r.Query);
            });
            Assert.Equal("0", FakeBilibiliApiServer.GetQueryValue(server.Requests[0].Query, "qn"));
            Assert.Equal("127", FakeBilibiliApiServer.GetQueryValue(server.Requests[1].Query, "qn"));

            Assert.Equal(3, result.VideoTracks.Count);
            var v80 = Assert.Single(result.VideoTracks, v => v.id == "80");
            Assert.Equal("1080P 高清", v80.dfn);
            Assert.Equal("AVC", v80.codecs);
            Assert.Equal(2000, v80.bandwidth);
            Assert.Equal("1920x1080", v80.res);
            Assert.Equal("30", v80.fps);
            Assert.Equal(6215, v80.dur);
            Assert.Equal("https://upos.example.com/80.m4s", v80.baseUrl);
            var v64 = Assert.Single(result.VideoTracks, v => v.id == "64");
            Assert.Equal("720P 高清", v64.dfn);
            Assert.Equal("HEVC", v64.codecs);
            var v32 = Assert.Single(result.VideoTracks, v => v.id == "32");
            Assert.Equal("480P 清晰", v32.dfn);
            Assert.Equal("AV1", v32.codecs);

            Assert.Equal(2, result.AudioTracks.Count);
            var a30280 = Assert.Single(result.AudioTracks, a => a.id == "30280");
            Assert.Equal("M4A", a30280.codecs);
            Assert.Equal(320, a30280.bandwidth);
            Assert.Equal(6215, a30280.dur);

            Assert.Equal(6215, result.ActualDurationSec);
        });
    }

    // ── F02a/F02b：番剧 result 根三段定位链（result.video_info 与 result 直挂两个变体）──

    [Fact]
    public async Task BangumiWebDash_ResultVideoInfoRoot_RoutesPgcPathWithoutWbi()
    {
        using var server = new FakeBilibiliApiServer();
        server.Register("/pgc/player/web/v2/playurl", LoadFixture("bangumi-web-dash-video-info.json"));
        await WithFakeApiAsync(server, async () =>
        {
            var result = await ExtractAsync("ep:307930", "170001", "999", epId: "307930");

            Assert.All(server.Requests, r =>
            {
                Assert.Equal("/pgc/player/web/v2/playurl", r.Path);
                // 番剧路径不走 WBI 签名
                Assert.DoesNotContain("w_rid=", r.Query);
                Assert.Contains("module=bangumi", r.Query);
                Assert.Contains("ep_id=307930", r.Query);
            });

            var v116 = Assert.Single(result.VideoTracks);
            Assert.Equal("116", v116.id);
            Assert.Equal("1080P 高帧率", v116.dfn);
            Assert.Equal("HEVC", v116.codecs);
            Assert.Equal("1920x1080", v116.res);
            Assert.Single(result.AudioTracks);
        });
    }

    [Fact]
    public async Task BangumiWebDash_ResultRootDirect_ParsesTracks()
    {
        using var server = new FakeBilibiliApiServer();
        server.Register("/pgc/player/web/v2/playurl", LoadFixture("bangumi-web-dash-result.json"));
        await WithFakeApiAsync(server, async () =>
        {
            var result = await ExtractAsync("ep:307930", "170001", "999", epId: "307930");

            var v80 = Assert.Single(result.VideoTracks);
            Assert.Equal("80", v80.id);
            Assert.Equal("AVC", v80.codecs);
            var a30216 = Assert.Single(result.AudioTracks);
            Assert.Equal("30216", a30216.id);
        });
    }

    // ── F03：TV DASH（res/fps 应跳过）──

    [Fact]
    public async Task TvDash_SkipsResAndFps_RoutesTvPathWithSign()
    {
        using var server = new FakeBilibiliApiServer();
        server.Register("/x/tv/playurl", LoadFixture("tv-dash.json"));
        await WithFakeApiAsync(server, async () =>
        {
            var result = await ExtractAsync("av170001", "170001", "999", tvApi: true);

            Assert.All(server.Requests, r =>
            {
                Assert.Equal("/x/tv/playurl", r.Path);
                Assert.Contains("appkey=4409e2ce8ffd12b8", r.Query);
                Assert.Contains("mobi_app=android_tv_yst", r.Query);
                Assert.Contains("playurl_type=1", r.Query);
                Assert.Matches(@"sign=[0-9a-f]{32}", r.Query);
            });

            var v80 = Assert.Single(result.VideoTracks);
            Assert.Equal("1080P 高清", v80.dfn);
            Assert.Equal("HEVC", v80.codecs);
            // TV 接口分支不读 res/fps：即使夹具携带 width/height/frame_rate 也保持未赋值
            Assert.Null(v80.res);
            Assert.Null(v80.fps);
            Assert.Single(result.AudioTracks);
        }, s => s with { TvHost = $"http://127.0.0.1:{server.Port}" });
    }

    // ── F04/F05：INTL 双次请求（code=0 视频 + code=1 补充流）──

    [Fact]
    public async Task Intl_TwoPassRequests_MergeStreamLists()
    {
        using var server = new FakeBilibiliApiServer();
        var path = "/intl/gateway/v2/ogv/playurl";
        server.Register(path, "prefer_code_type", "0", LoadFixture("intl-code0.json"));
        server.Register(path, "prefer_code_type", "1", LoadFixture("intl-code1.json"));
        await WithFakeApiAsync(server, async () =>
        {
            var result = await ExtractAsync("av170001", "170001", "999", intlApi: true);

            Assert.Equal(2, server.Requests.Count);
            Assert.All(server.Requests, r => Assert.Equal(path, r.Path));
            Assert.Equal("0", FakeBilibiliApiServer.GetQueryValue(server.Requests[0].Query, "prefer_code_type"));
            Assert.Equal("1", FakeBilibiliApiServer.GetQueryValue(server.Requests[1].Query, "prefer_code_type"));
            Assert.Matches(@"sign=[0-9a-f]{32}", server.Requests[0].Query);

            // stream_list 合并：code=0 的 80 + code=1 的 112
            Assert.Equal(2, result.VideoTracks.Count);
            Assert.Single(result.VideoTracks, v => v.id == "80" && v.codecs == "AVC" && v.bandwidth == 900);
            Assert.Single(result.VideoTracks, v => v.id == "112" && v.codecs == "HEVC" && v.bandwidth == 1800);
            // dash_audio 音轨产出：两轮各一条
            Assert.Equal(2, result.AudioTracks.Count);
            Assert.Single(result.AudioTracks, a => a.id == "0");
            Assert.Single(result.AudioTracks, a => a.id == "1");
            Assert.All(result.AudioTracks, a => Assert.Equal("M4A", a.codecs));
            Assert.All(result.VideoTracks, v => Assert.Equal(150, v.dur));
        });
    }

    // ── F06：FLV durl + accept_quality ──

    [Fact]
    public async Task FlvDurl_ClipsAndDfnsFromAcceptQuality()
    {
        using var server = new FakeBilibiliApiServer();
        server.Register("/x/player/wbi/playurl", LoadFixture("flv-durl.json"));
        await WithFakeApiAsync(server, async () =>
        {
            var result = await ExtractAsync("av170001", "170001", "999");

            // durl 同样触发最高清晰度重发（qn=127），夹具两轮一致 → 接管后内容不变
            Assert.Equal(2, server.Requests.Count);
            Assert.Equal("127", FakeBilibiliApiServer.GetQueryValue(server.Requests[1].Query, "qn"));

            Assert.Equal(2, result.Clips.Count);
            Assert.Equal("https://upos.example.com/seg1.flv", result.Clips[0]);
            Assert.Equal("https://upos.example.com/seg2.flv", result.Clips[1]);
            Assert.Equal(["80", "64", "32"], result.Dfns);

            var v = Assert.Single(result.VideoTracks);
            Assert.Equal("80", v.id);
            Assert.Equal("1080P 高清", v.dfn);
            Assert.Equal("AVC", v.codecs);
            Assert.Equal(6215, v.dur);
            Assert.Equal(3000000, v.size);
            Assert.Equal(6215, result.ActualDurationSec);
        });
    }

    // ── F07：TV durl + qn_extras ──

    [Fact]
    public async Task TvDurl_DfnsFromQnExtras()
    {
        using var server = new FakeBilibiliApiServer();
        server.Register("/x/tv/playurl", LoadFixture("tv-durl-qnxtras.json"));
        await WithFakeApiAsync(server, async () =>
        {
            var result = await ExtractAsync("av170001", "170001", "999", tvApi: true);

            Assert.Equal(["116", "64"], result.Dfns);
            Assert.Single(result.Clips);
            Assert.Equal("https://upos.example.com/tvseg.flv", result.Clips[0]);
            var v = Assert.Single(result.VideoTracks);
            Assert.Equal("64", v.id);
            Assert.Equal("720P 高清", v.dfn);
        }, s => s with { TvHost = $"http://127.0.0.1:{server.Port}" });
    }

    // ── F08：DRM dash（bilidrm_uri 提取 + 非法 kid 告警保持空）──

    [Fact]
    public async Task DrmDash_ExtractsKidAndPssh()
    {
        using var server = new FakeBilibiliApiServer();
        server.Register("/x/player/wbi/playurl", LoadFixture("drm-dash.json"));
        await WithFakeApiAsync(server, async () =>
        {
            var result = await ExtractAsync("av170001", "170001", "999");

            Assert.True(result.IsDrm);
            Assert.Equal(2, result.DrmTechType);
            Assert.Equal("Widevine", result.DrmType);
            Assert.Equal("0123456789abcdef0123456789abcdef", result.KidHex);
            Assert.Equal("QAAAAGB3aWRldmluZQ5WLRhD7hZl", result.PsshBase64);
        });
    }

    [Fact]
    public async Task DrmDash_InvalidKid_KeepsKidHexEmpty()
    {
        using var server = new FakeBilibiliApiServer();
        server.Register("/x/player/wbi/playurl", LoadFixture("drm-dash-badkid.json"));
        await WithFakeApiAsync(server, async () =>
        {
            var result = await ExtractAsync("av170001", "170001", "999");

            Assert.True(result.IsDrm);
            // 畸形 bilidrm_uri（带 host/path/query）不产出 kid，仅告警
            Assert.Equal("", result.KidHex);
            Assert.Equal("", result.PsshBase64);
        });
    }

    // ── F09：免二压重取第二轮接管 ──

    [Fact]
    public async Task DashReparse_HigherQnResponseTakesOver()
    {
        using var server = new FakeBilibiliApiServer();
        var path = "/x/player/wbi/playurl";
        var firstBody = LoadFixture("dash-reparse-pass1.json");
        var secondBody = LoadFixture("dash-reparse-pass2.json");
        server.Register(path, firstBody);
        server.Register(path, "qn", "127", secondBody);
        await WithFakeApiAsync(server, async () =>
        {
            var result = await ExtractAsync("av170001", "170001", "999");

            Assert.Equal("127", FakeBilibiliApiServer.GetQueryValue(server.Requests[1].Query, "qn"));
            // 新文档接管：WebJsonString 被第二轮响应替换
            Assert.Equal(secondBody, result.WebJsonString);
            // 轨道为两轮并集：首轮 80/64 + 接管轮 127（8K）
            Assert.Equal(3, result.VideoTracks.Count);
            Assert.Single(result.VideoTracks, v => v.id == "80");
            Assert.Single(result.VideoTracks, v => v.id == "64");
            var v127 = Assert.Single(result.VideoTracks, v => v.id == "127");
            Assert.Equal("8K 超高清", v127.dfn);
            Assert.Equal("HEVC", v127.codecs);
            Assert.Single(result.AudioTracks, a => a.id == "30280");
        });
    }

    // ── F10：durl 重发空数组退化，回退沿用首次响应 ──

    [Fact]
    public async Task DurlReplay_EmptyArray_FallsBackToFirstResponse()
    {
        using var server = new FakeBilibiliApiServer();
        var path = "/x/player/wbi/playurl";
        var firstBody = LoadFixture("durl-replay-first.json");
        server.Register(path, firstBody);
        server.Register(path, "qn", "127", LoadFixture("durl-replay-empty.json"));
        await WithFakeApiAsync(server, async () =>
        {
            var result = await ExtractAsync("av170001", "170001", "999");

            Assert.Equal(2, server.Requests.Count);
            // 重发被拒（空 durl）后回退：沿用首次已校验响应
            Assert.Equal(firstBody, result.WebJsonString);
            Assert.Equal(2, result.Clips.Count);
            Assert.Equal("https://upos.example.com/replay-seg1.flv", result.Clips[0]);
            Assert.Equal(["80", "64"], result.Dfns);
            Assert.Single(result.VideoTracks);
            Assert.Equal(6215, result.ActualDurationSec);
        });
    }

    // ── F11：dash.dolby.audio[] + dash.flac.audio 追加进音轨列表 ──

    [Fact]
    public async Task DolbyAndFlacAudio_AppendedToAudioTracks()
    {
        using var server = new FakeBilibiliApiServer();
        server.Register("/x/player/wbi/playurl", LoadFixture("dolby-flac-audio.json"));
        await WithFakeApiAsync(server, async () =>
        {
            var result = await ExtractAsync("av170001", "170001", "999");

            Assert.Single(result.VideoTracks);
            Assert.Equal(4, result.AudioTracks.Count);
            Assert.Single(result.AudioTracks, a => a.id == "30280" && a.codecs == "M4A");
            Assert.Single(result.AudioTracks, a => a.id == "30250" && a.codecs == "E-AC-3");
            Assert.Single(result.AudioTracks, a => a.id == "30255" && a.codecs == "E-AC-3");
            Assert.Single(result.AudioTracks, a => a.id == "30251" && a.codecs == "FLAC");
        });
    }

    // ── F12：顶层 code≠0 业务错误（含控制字符净化）──

    [Fact]
    public async Task BizError_NonZeroCode_ThrowsSanitizedServerMessage()
    {
        using var server = new FakeBilibiliApiServer();
        server.Register("/x/player/wbi/playurl", LoadFixture("biz-error.json"));
        await WithFakeApiAsync(server, async () =>
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ExtractAsync("av170001", "170001", "999"));

            Assert.Contains("接口返回错误", ex.Message);
            Assert.Contains("啥都木有 风险", ex.Message);
            Assert.Contains("(code=-404)", ex.Message);
            // 服务端 message 中的控制字符（U+0007）经 SanitizeServerText 净化为空格。
            // DoesNotContain 必须用 Ordinal：NLS 排序对控制字符零权重，CurrentCulture 会误判包含
            Assert.DoesNotContain("\u0007", ex.Message, StringComparison.Ordinal);
        });
    }

    // ── F13：play_check.limit_play_reason 播放限制 ──

    [Fact]
    public async Task PlayLimited_VipLimit_ThrowsReadableReason()
    {
        using var server = new FakeBilibiliApiServer();
        server.Register("/pgc/player/web/v2/playurl", LoadFixture("play-limited.json"));
        await WithFakeApiAsync(server, async () =>
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ExtractAsync("ep:307930", "170001", "999", epId: "307930"));

            Assert.Contains("大会员", ex.Message);
            Assert.Contains("limit_play_reason=VIP_LIMIT", ex.Message);
        });
    }

    // ── F14：缺 backup_url/dolby/flac 键的容错 ──

    [Fact]
    public async Task MissingOptionalNodes_Tolerated()
    {
        using var server = new FakeBilibiliApiServer();
        server.Register("/x/player/wbi/playurl", LoadFixture("missing-nodes-tolerant.json"));
        await WithFakeApiAsync(server, async () =>
        {
            var result = await ExtractAsync("av170001", "170001", "999");

            // 缺失可选节点（backup_url/dolby/flac）不抛 KeyNotFoundException，
            // EnumerateArraySafe 空序列容错，主干轨道正常产出
            var v80 = Assert.Single(result.VideoTracks);
            Assert.Equal("https://upos.example.com/tolerant80.m4s", v80.baseUrl);
            Assert.Single(result.AudioTracks);
        });
    }
}
