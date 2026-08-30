using System.Globalization;

namespace BBDown.Tests;

/// <summary>
/// 保存路径模板决策测试（C4 观察点修复）：多P命名模板的判定应基于
/// 【实际下载的分P数】而非视频总P数——-p 单选 1 集时即使视频有多个分P，
/// 也应走单P模板（默认 &lt;videoTitle&gt; 或 -F 自定义），产物不再带 [P##] 前缀。
/// </summary>
public class PathFormatTests
{
    [Theory]
    // 单选 1P、非番剧 → 单P 默认模板（无 [P##]）
    [InlineData("", "", 1, false, "<videoTitle>")]
    // 下载 3P → 多P 默认模板
    [InlineData("", "", 3, false, "<videoTitle>/[P<pageNumberWithZero>]<pageTitle>")]
    // 单选 1P 但番剧未完结 → 强制多P（每P自成文件）
    [InlineData("", "", 1, true, "<videoTitle>/[P<pageNumberWithZero>]<pageTitle>")]
    // -F 自定义单P模板：单选 1P 时生效（C4 原缺陷场景）
    [InlineData("<Fpat>", "", 1, false, "<Fpat>")]
    // -M 自定义多P模板：下载多P时生效
    [InlineData("", "<Mpat>", 3, false, "<Mpat>")]
    // 番剧未完结 + 双模板都给了 → 用多P模板
    [InlineData("<Fpat>", "<Mpat>", 1, true, "<Mpat>")]
    // 单选 1P + 双模板都给了 → 用单P模板
    [InlineData("<Fpat>", "<Mpat>", 1, false, "<Fpat>")]
    public void ResolveSavePathFormat_SelectsTemplateBasedOnActualPageCount(
        string filePattern, string multiFilePattern, int actualPageCount, bool useMultiWhenSingle, string expected)
    {
        Assert.Equal(expected,
            Program.ResolveSavePathFormat(filePattern, multiFilePattern, actualPageCount, useMultiWhenSingle));
    }

    /// <summary>
    /// RF-19：publishDate/videoDate 占位符必须 (a) 固定 InvariantCulture（自定义格式串的
    /// `:` 是时间分隔符占位符，fi-FI 等区域下输出为 `.`，产物跨机漂移），(b) 替换值再过
    /// GetValidFileName（en-US 下 HH:mm 产出含 `:` 的路径，Windows 上可写成 NTFS 备用
    /// 数据流——File.Exists 为真但资源管理器不可见）。
    /// </summary>
    [Fact]
    public void FormatSavePath_DatePlaceholders_AreInvariantAndSanitized()
    {
        long ts = 1700000000;
        var page = new BBDown.Core.Entity.Entity.Page(1, "123", "456", "", "t", 60, "", ts);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            // fi-FI 的时间分隔符是 '.'：若 FormatTimeStamp 未固定 InvariantCulture，
            // <publishDate:HH:mm> 会产出 22.13 而非 22:13（再被 GetValidFileName 漏过，
            // 因为 '.' 不在 InvalidChars 里——净化必须建立在 Invariant 输出之上）
            CultureInfo.CurrentCulture = new CultureInfo("fi-FI");

            var expectedInvariant =
                DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)
                    .Replace(":", "_"); // GetValidFileName 将 ':' 替换为 '_'

            // 注：InfoRegex 为 <([\w:\-.]+?)>，占位符内不允许空格，格式串用 'T' 分隔日期与时间
            Assert.Equal(expectedInvariant + ".mp4",
                Program.FormatSavePath("<publishDate:yyyy-MM-ddTHH:mm>", "t", null, null, page, 1, "web", ts));
            // videoDate 用 p.pubTime，行为一致
            Assert.Equal(expectedInvariant + ".mp4",
                Program.FormatSavePath("<videoDate:yyyy-MM-ddTHH:mm>", "t", null, null, page, 1, "web", 0));
            // 产物不含任何 Windows 非法路径字符
            Assert.DoesNotContain(":", Program.FormatSavePath("<publishDate:yyyy-MM-ddTHH:mm:ss>", "t", null, null, page, 1, "web", ts));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
