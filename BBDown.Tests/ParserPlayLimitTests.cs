using System.Text.Json;
using BBDown.Core;

namespace BBDown.Tests;

public class ParserPlayLimitTests
{
    [Fact]
    public void ThrowIfPlayLimited_AreaLimit_ThrowsClearMessage()
    {
        const string json = """
        {"code":0,"message":"success","result":{"play_check":{"limit_play_reason":"AREA_LIMIT","play_detail":"PLAY_NONE"},"play_view_business_info":{"episode_info":{"aid":0,"cid":0,"delivery_business_fragment_video":false,"delivery_fragment_video":false,"ep_id":4448895,"ep_status":0},"user_status":{"follow_info":{"follow":0,"follow_status":2},"is_login":1,"vip_info":{"due_date":1786723200000,"real_vip":true,"status":1,"type":2}}}}}
        """;

        using var doc = JsonDocument.Parse(json);
        var ex = Assert.Throws<InvalidOperationException>(() => Parser.ThrowIfPlayLimited(doc.RootElement));

        Assert.Contains("区域限制", ex.Message);
        Assert.Contains("limit_play_reason=AREA_LIMIT", ex.Message);
        Assert.Contains("play_detail=PLAY_NONE", ex.Message);
    }

    // TV 接口（playurltv / x/tv/playurl）顶层 result 是历史遗留字符串（线上实测 "suee"），
    // 非对象 result 下根本没有 play_check 可言：必须原样放行，不能让 TryGetProperty
    // 在非对象元素上抛 JsonElementHasWrongType（曾致 TV 模式番剧/电影下载全量失败）。
    [Theory]
    [InlineData("""{"code":0,"result":"suee","durl":[]}""")]
    [InlineData("""{"code":0,"result":null}""")]
    [InlineData("""{"code":0,"result":123}""")]
    [InlineData("""{"code":0,"result":["a"]}""")]
    [InlineData("""{"code":0,"result":{"no_play_check":true}}""")]
    [InlineData("""{"code":0}""")]
    [InlineData("""[1,2,3]""")]
    public void ThrowIfPlayLimited_NonObjectResult_DoesNotThrow(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Parser.ThrowIfPlayLimited(doc.RootElement); // 不应抛异常
    }

    [Fact]
    public void ThrowIfBizError_NonZeroCode_ThrowsReadableMessage()
    {
        const string json = """{"code":-86038,"message":"抱歉，您所在地区暂时无法观看","data":{}}""";
        using var doc = JsonDocument.Parse(json);
        var ex = Assert.Throws<InvalidOperationException>(() => Parser.ThrowIfBizError(doc.RootElement));
        Assert.Contains("86038", ex.Message);
        Assert.Contains("无法观看", ex.Message);
    }

    [Theory]
    [InlineData("""{"code":0,"message":"success","data":{}}""")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"code":"-412","data":{}}""")]  // code 非数字
    [InlineData("""[1,2,3]""")]                    // 根节点非对象
    public void ThrowIfBizError_ZeroOrMissingOrNonNumericCode_DoesNotThrow(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Parser.ThrowIfBizError(doc.RootElement); // 不应抛异常
    }

    // ── RF-11-P1：大会员回退判定改 JSON message 字段解析（不再依赖裸子串匹配）──

    [Theory]
    [InlineData("""{"code":-10403,"message":"大会员专享限制"}""", true)]
    [InlineData("""{"code":-10403,"message":"大会员专享限制","data":{}}""", true)]
    [InlineData("""{"code":0,"message":"success","data":{}}""", false)]      // 正常响应
    [InlineData("""{"code":-10403,"message":"版权受限"}""", false)]           // 其它限制文案不算大会员
    [InlineData("""{"data":{}}""", false)]                                    // 无 message
    public void IsVipRestrictedResponse_ReadsJsonMessageField(string json, bool expected)
        => Assert.Equal(expected, Parser.IsVipRestrictedResponse(json));

    [Theory]
    [InlineData("""<html>window.__playinfo__="大会员专享限制"</html>""", true)]  // 非 JSON 兜底子串
    [InlineData("<html>risk-control</html>", false)]                            // 非 JSON 无命中
    public void IsVipRestrictedResponse_NonJson_FallsBackToSubstring(string body, bool expected)
        => Assert.Equal(expected, Parser.IsVipRestrictedResponse(body));

    // ── RF-11-P2：BaseUrlRegex 收紧（query 中的 ":数字" 不得误判为端口）──

    [Theory]
    [InlineData("http://host:8080/path", true)]
    [InlineData("https://host:443/", true)]
    [InlineData("http://upos-sz.example.com:8443/a.m4s?b=1", true)]
    [InlineData("http://host/path?x=1:2", false)]   // query 含 :数字 不误判为端口
    [InlineData("https://host/path", false)]         // 无端口
    [InlineData("host:8080/path", false)]            // 缺 scheme
    public void BaseUrlRegex_MatchesOnlyHostPort(string url, bool expected)
        => Assert.Equal(expected, Parser.BaseUrlRegex().IsMatch(url));
}
