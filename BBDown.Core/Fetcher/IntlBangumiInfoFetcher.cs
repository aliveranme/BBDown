using BBDown.Core.Entity;
using BBDown.Core.Util;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using static BBDown.Core.Entity.Entity;

namespace BBDown.Core.Fetcher;

public partial class IntlBangumiInfoFetcher : IFetcher
{
    public async Task<VInfo> FetchAsync(string id, CancellationToken cancellationToken = default)
    {
        id = id[3..];
        string index = "";
        //string api = $"https://api.global.bilibili.com/intl/gateway/ogv/m/view?ep_id={id}";
        string api = "https://" + (Config.Current.Host == "api.bilibili.com" ? "api.bilibili.tv" : Config.Current.Host) +
                     $"/intl/gateway/v2/ogv/view/app/season?ep_id={id}&platform=android&s_locale=zh_SG&mobi_app=bstar_a" + (Config.Current.Token != "" ? $"&access_key={Config.Current.Token}" : "");
        // 原实现在此 .Replace("\\/", "/")：多余且有害（RF-26）——\/ 本是合法 JSON 转义，
        // JsonDocument.Parse 会正确解码；预替换会把原文 \\+\/（值为"反斜杠+斜杠"）错误归并丢数据。
        string json = await HTTPUtil.GetWebSourceAsync(api, token: cancellationToken);
        using var infoJson = JsonDocument.Parse(json);
        // 与 BangumiInfoFetcher 一致：顶层 code/message 不能丢弃，区域限制/失效/风控需可诊断。
        long rootCode = infoJson.RootElement.GetInt64Safe("code");
        if (rootCode != 0)
        {
            var msg = infoJson.RootElement.GetValueAsStringSafe("message");
            throw new InvalidOperationException($"国际版番剧接口返回错误: {msg} (code={rootCode})");
        }
        if (!infoJson.RootElement.TryGetProperty("result", out var result))
            throw new KeyNotFoundException("Intl Bangumi API response missing 'result' node");
        string seasonId = result.GetValueAsStringSafe("season_id");
        string cover = result.GetValueAsStringSafe("cover");
        string title = result.GetValueAsStringSafe("title");
        string desc = result.GetValueAsStringSafe("evaluate");


        if (cover == "")
        {
            string animeUrl = $"https://bangumi.bilibili.com/anime/{seasonId}";
            var web = await HTTPUtil.GetWebSourceAsync(animeUrl, token: cancellationToken, rejectHtml: false);
            if (web != "")
            {
                Regex regex = StateRegex();
                var match = regex.Match(web);
                if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                {
                    string _json = match.Groups[1].Value;
                    using var _tempJson = JsonDocument.Parse(_json);
                    cover = _tempJson.RootElement.GetPropertySafe("mediaInfo").GetValueAsStringSafe("cover");
                    title = _tempJson.RootElement.GetPropertySafe("mediaInfo").GetValueAsStringSafe("title");
                    desc = _tempJson.RootElement.GetPropertySafe("mediaInfo").GetValueAsStringSafe("evaluate");
                }
            }
        }

        string pubTimeStr = result.TryGetPropertySafe("publish")?.GetValueAsStringSafe("pub_time") ?? "";
        // InvariantCulture：pub_time 形如 "2021-07-15 11:00:00"，用 CurrentCulture 解析在
        // 非公历默认历法（fa-IR/ar-SA 等）locale 下会错乱或失败（pubTime 静默归 0）。
        long pubTime = !string.IsNullOrEmpty(pubTimeStr) && DateTimeOffset.TryParse(pubTimeStr, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dto) ? dto.ToUnixTimeSeconds() : 0;
        var pages = new List<JsonElement>();
        if (result.TryGetProperty("episodes", out JsonElement episodes))
        {
            pages = episodes.EnumerateArray().ToList();
        }
        List<Page> pagesInfo = new();
        int i = 1;

        if (result.TryGetProperty("modules", out JsonElement modules))
        {
            foreach (var section in modules.EnumerateArray())
            {
                if (section.TryGetProperty("data", out var secData) &&
                    secData.TryGetProperty("episodes", out var secEps))
                {
                    bool foundInSection = false;
                    foreach (var ep in secEps.EnumerateArray())
                    {
                        if (ep.TryGetProperty("id", out var eid) && eid.ToString() == id)
                        {
                            foundInSection = true;
                            break;
                        }
                    }
                    if (foundInSection)
                    {
                        pages = secEps.EnumerateArray().ToList();
                        break;
                    }
                }
            }
        }

        foreach (var page in pages)
        {
            string pageId = page.GetValueAsStringSafe("id");
            // 跳过非用户显式请求的预告（若用户指定了该 epId 则保留，防止 Index 变空导致整季被静默下载）
            if (page.TryGetProperty("badge", out JsonElement badge) && badge.ToString() == "预告" && (string.IsNullOrEmpty(id) || pageId != id))
                continue;
            string res = "";
            if (page.TryGetProperty("dimension", out var dim) &&
                dim.TryGetProperty("width", out var w) &&
                dim.TryGetProperty("height", out var h))
            {
                res = $"{w}x{h}";
            }
            string _title = page.GetValueAsStringSafe("title");
            if (page.TryGetProperty("long_title", out var lt) && lt.ValueKind != JsonValueKind.Null)
                _title += " " + lt.ToString();
            _title = _title.Trim();
            Page p = new(i++,
                page.GetValueAsStringSafe("aid"),
                page.GetValueAsStringSafe("cid"),
                pageId,
                _title,
                0, res,
                page.GetInt64Safe("pub_time"));
            if (p.epid == id) index = p.index.ToString();
            pagesInfo.Add(p);
        }

        if (!string.IsNullOrEmpty(id) && string.IsNullOrEmpty(index))
            throw new KeyNotFoundException($"未找到指定的剧集分P (ep_id={id})");
        if (pagesInfo.Count == 0)
            throw new KeyNotFoundException("未找到剧集分P信息");

        var info = new VInfo
        {
            Title = title.Trim(),
            Desc = desc.Trim(),
            Pic = cover,
            PubTime = pubTime,
            PagesInfo = pagesInfo,
            IsBangumi = true,
            IsCheese = false,
            Index = index
        };

        return info;
    }

    [GeneratedRegex("window.__INITIAL_STATE__=([\\s\\S].*?);\\(function\\(\\)")]
    private static partial Regex StateRegex();
}
