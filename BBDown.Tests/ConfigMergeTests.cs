namespace BBDown.Tests;

/// <summary>
/// 配置文件与命令行的合并优先级。命令行显式给出的选项必须压过配置文件，
/// 无论用空格还是等号写法；配置文件里以 '-' 开头的取值也不能被吞掉。
/// </summary>
public class ConfigMergeTests
{
    private static string WriteConfig(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bbdown-cfg-{Guid.NewGuid():N}.config");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>合并结果里某个规范选项名最终生效的值（Spectre 后者胜出）。</summary>
    private static string? EffectiveValue(List<string> merged, string longName)
    {
        string? val = null;
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i] == longName && i + 1 < merged.Count) val = merged[i + 1];
            else if (merged[i].StartsWith(longName + "=")) val = merged[i][(longName.Length + 1)..];
        }
        return val;
    }

    [Fact]
    public async Task SpaceStyleCliOption_OverridesConfigFile()
    {
        var cfg = WriteConfig("--dfn-priority\n1080P 高清\n");
        var merged = await BBDownConfigParser.MergeWithConfigAsync(
            ["--dfn-priority", "720P 高清", "--config-file", cfg, "URL"]);
        File.Delete(cfg);

        Assert.Equal("720P 高清", EffectiveValue(merged, "--dfn-priority"));
    }

    [Fact]
    public async Task EqualsStyleCliOption_OverridesConfigFile()
    {
        // 旧实现按精确 token 匹配识别"已显式指定"，--dfn-priority=X 匹配不到，
        // 配置文件的值被追加到末尾，按 Spectre 后者胜出反向覆盖了命令行
        var cfg = WriteConfig("--dfn-priority\n1080P 高清\n");
        var merged = await BBDownConfigParser.MergeWithConfigAsync(
            ["--dfn-priority=720P 高清", "--config-file", cfg, "URL"]);
        File.Delete(cfg);

        Assert.Equal("720P 高清", EffectiveValue(merged, "--dfn-priority"));
    }

    [Fact]
    public async Task EqualsStyleConfigFilePath_IsHonored()
    {
        // --config-file=path 形式此前匹配不到，会回落到默认配置路径而忽略用户指定
        var cfg = WriteConfig("--dfn-priority\n1080P 高清\n");
        var merged = await BBDownConfigParser.MergeWithConfigAsync(
            [$"--config-file={cfg}", "URL"]);
        File.Delete(cfg);

        Assert.Equal("1080P 高清", EffectiveValue(merged, "--dfn-priority"));
    }

    [Fact]
    public async Task ConfigOptionNotOnCommandLine_IsApplied()
    {
        var cfg = WriteConfig("--dfn-priority\n1080P 高清\n");
        var merged = await BBDownConfigParser.MergeWithConfigAsync(["--config-file", cfg, "URL"]);
        File.Delete(cfg);

        Assert.Equal("1080P 高清", EffectiveValue(merged, "--dfn-priority"));
    }

    [Fact]
    public async Task ConfigValueStartingWithDash_IsNotSwallowed()
    {
        // 值本身以 - 开头（如 access-token 的值、负数参数）时，
        // 旧实现把它误判为下一个选项而丢弃该值
        var cfg = WriteConfig("--access-token\n-access-token-value\n");
        var merged = await BBDownConfigParser.MergeWithConfigAsync(["--config-file", cfg, "URL"]);
        File.Delete(cfg);

        Assert.Equal("-access-token-value", EffectiveValue(merged, "--access-token"));
    }

    [Fact]
    public async Task SubCommandInvocation_SkipsConfigMerge()
    {
        // 子命令的 Settings 只声明各自少量选项：把配置文件里的下载选项全集合并进去，
        // Spectre 会以 unknown option 拒绝解析——存在 BBDown.config 时 sub/live 等命令
        // 必须仍然可用。合并结果应原样等于命令行参数。
        var cfg = WriteConfig("--dfn-priority\n1080P 高清\n--cookie\nSESSDATA=abc\n");
        var merged = await BBDownConfigParser.MergeWithConfigAsync(["--config-file", cfg, "sub", "list"]);
        File.Delete(cfg);

        Assert.Equal(["--config-file", cfg, "sub", "list"], merged);
        Assert.True(BBDownConfigParser.IsSubCommandInvocation(["sub", "list"]));
        Assert.True(BBDownConfigParser.IsSubCommandInvocation(["serve", "-l", "http://0.0.0.0:23333"]));
        Assert.True(BBDownConfigParser.IsSubCommandInvocation(["--debug", "live"]));
        Assert.False(BBDownConfigParser.IsSubCommandInvocation(["https://www.bilibili.com/video/BV1qt4y1X7TW"]));
        Assert.False(BBDownConfigParser.IsSubCommandInvocation(["-p", "2", "URL"]));
    }

    [Fact]
    public async Task CliUrl_Present_ConfigUrlIsSkipped()
    {
        // 命令行已给 URL 时，配置文件里的 URL 不再合并：
        // 两个位置参数会让 MyOption 解析失败（unexpected positional argument）
        var cfg = WriteConfig("https://www.bilibili.com/video/BV1AAAAAAAAAA\n--dfn-priority\n1080P 高清\n");
        var merged = await BBDownConfigParser.MergeWithConfigAsync(
            ["--config-file", cfg, "https://www.bilibili.com/video/BV1qt4y1X7TW"]);
        File.Delete(cfg);

        Assert.Single(merged, a => a.StartsWith("https://"));
        Assert.Contains("--dfn-priority", merged);
    }

    [Theory]
    [InlineData("--skip-mux\nav123\n", false)]
    [InlineData("--work-dir\noutput\nav123\n", false)]
    [InlineData("--skip-mux\nav123\n", true)]
    [InlineData("--work-dir\noutput\nav123\n", true)]
    public async Task ConfigUrlAfterOption_RespectsCliUrl(string config, bool cliHasUrl)
    {
        var cfg = WriteConfig(config);
        try
        {
            var args = cliHasUrl ? new[] { "--config-file", cfg, "av456" } : new[] { "--config-file", cfg };
            var merged = await BBDownConfigParser.MergeWithConfigAsync(args);
            Assert.Contains(cliHasUrl ? "av456" : "av123", merged);
            Assert.DoesNotContain(cliHasUrl ? "av123" : "av456", merged);
        }
        finally { File.Delete(cfg); }
    }

    [Theory]
    [InlineData("--skip-mux\nav123\n", "--skip-mux")]
    [InlineData("--work-dir\noutput\nav123\n", "--work-dir")]
    public async Task OverriddenConfigOption_DoesNotDiscardFollowingUrl(string config, string option)
    {
        var cfg = WriteConfig(config);
        try
        {
            var args = option == "--work-dir"
                ? new[] { "--config-file", cfg, option, "other" }
                : new[] { "--config-file", cfg, option };
            var merged = await BBDownConfigParser.MergeWithConfigAsync(args);
            Assert.Contains("av123", merged);
            Assert.Single(merged, value => value == option);
        }
        finally { File.Delete(cfg); }
    }

    [Fact]
    public async Task ConfigBooleanValueAndInlineOption_ArePreserved()
    {
        var cfg = WriteConfig("--multi-thread\nfalse\n--work-dir=output\nav123\n");
        try
        {
            var merged = await BBDownConfigParser.MergeWithConfigAsync(["--config-file", cfg]);
            Assert.Contains("false", merged);
            Assert.Contains("--work-dir=output", merged);
            Assert.Contains("av123", merged);
        }
        finally { File.Delete(cfg); }
    }

    [Fact]
    public async Task UrlLikeOptionValue_DoesNotSuppressConfigUrl()
    {
        // RF-7 回归：--aria2c-proxy 的值（http://127.0.0.1:7890）形似 URL。
        // 旧实现对全部 argv 应用 URL 启发式，"URL 在配置文件 + 命令行有 URL 形值选项"
        // 被误判成"命令行已给出 URL"，配置文件里的 URL 被丢弃 → Spectre 报缺少必填参数。
        var cfg = WriteConfig("https://www.bilibili.com/video/BV1AAAAAAAAAA\n--dfn-priority\n1080P 高清\n");
        var merged = await BBDownConfigParser.MergeWithConfigAsync(
            ["--config-file", cfg, "--aria2c-proxy", "http://127.0.0.1:7890"]);
        File.Delete(cfg);

        Assert.Contains("https://www.bilibili.com/video/BV1AAAAAAAAAA", merged);
        Assert.Equal("1080P 高清", EffectiveValue(merged, "--dfn-priority"));
        Assert.Equal("http://127.0.0.1:7890", EffectiveValue(merged, "--aria2c-proxy"));
    }

    [Fact]
    public async Task IdLikeOptionValue_DoesNotSuppressConfigUrl()
    {
        // 同 RF-7：--work-dir av123 的值命中 av\d+ 形态，同样不应算作"命令行已给 URL"
        var cfg = WriteConfig("https://www.bilibili.com/video/BV1AAAAAAAAAA\n");
        var merged = await BBDownConfigParser.MergeWithConfigAsync(
            ["--config-file", cfg, "--work-dir", "av123"]);
        File.Delete(cfg);

        Assert.Contains("https://www.bilibili.com/video/BV1AAAAAAAAAA", merged);
        Assert.Equal("av123", EffectiveValue(merged, "--work-dir"));
    }

    [Fact]
    public void GetPositionalTokens_SkipsOptionValues()
    {
        // 需要值的选项吞掉下一 token；bool 开关与 "--opt=value" 不吞；
        // 只有真正的位置参数（URL 候选）被收集
        Assert.Equal(["URL"], BBDownConfigParser.GetPositionalTokens(
            ["--config-file", "x.config", "--debug", "--aria2c-proxy=http://127.0.0.1:7890", "-p", "2", "URL"]));
        Assert.Empty(BBDownConfigParser.GetPositionalTokens(["--aria2c-proxy", "http://127.0.0.1:7890"]));
    }
}
