using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace BBDown.Tests;

public class ServeApiSecurityTests
{
    // 域名用例需要解析 DNS：注入固定解析器保证测试确定性、不依赖网络
    private static Task<IPAddress[]> ResolvePublic(string _) => Task.FromResult(new IPAddress[] { IPAddress.Parse("93.184.216.34") });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task IsSafeCallbackUrl_Empty_Allowed(string? url)
        => Assert.True(await BBDownApiServer.IsSafeCallbackUrlAsync(url));

    [Theory]
    [InlineData("https://example.com/hook")]
    [InlineData("http://192.168.1.10:9000/cb")]   // RFC1918 私网：局域网回调是 serve 的正常用法
    [InlineData("https://10.0.0.5/cb")]
    [InlineData("https://api.bilibili.com/x/")]
    public async Task IsSafeCallbackUrl_PublicOrPrivateNet_Allowed(string url)
        => Assert.True(await BBDownApiServer.IsSafeCallbackUrlAsync(url, ResolvePublic));

    [Theory]
    [InlineData("ftp://example.com/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative/path")]
    [InlineData("not a url")]
    public async Task IsSafeCallbackUrl_NonHttpOrRelative_Rejected(string url)
        => Assert.False(await BBDownApiServer.IsSafeCallbackUrlAsync(url));

    [Theory]
    [InlineData("http://localhost:5000/cb")]
    [InlineData("http://127.0.0.1/cb")]
    [InlineData("http://[::1]/cb")]
    [InlineData("http://169.254.169.254/cb")]   // 云元数据探测面
    [InlineData("http://[fe80::1]/cb")]         // IPv6 链路本地
    [InlineData("http://[::ffff:169.254.169.254]/cb")] // IPv4-mapped IPv6：映射前是 InterNetworkV6，会绕过下方 169.254 检查
    [InlineData("http://[::ffff:127.0.0.1]/cb")]        // IPv4-mapped IPv6 回环
    [InlineData("http://0.0.0.0/cb")]           // 全零地址连接时绑定回环
    [InlineData("http://[::]/cb")]              // IPv6 全零
    public async Task IsSafeCallbackUrl_LoopbackOrLinkLocal_Rejected(string url)
        => Assert.False(await BBDownApiServer.IsSafeCallbackUrlAsync(url));

    [Fact]
    public async Task IsSafeCallbackUrl_CgnatAndUla_Rejected()
    {
        // F12：CGNAT（100.64.0.0/10）与 IPv6 ULA（fc00::/7）分支此前无直接测试。
        // IsBlockedAddress 由 IsSafeCallbackUrlAsync 的“域名 DNS 解析分支”调用：
        // 域名重绑定解析到这些段即拒绝（公网域名指向运营商级 NAT / ULA 是 SSRF 面）。
        // 字面 IP 分支有意放行（字面 IP 无 DNS 重绑定风险，局域网是 serve 正常用法）。
        Func<string, Task<IPAddress[]>> Resolve(IPAddress ip)
            => _ => Task.FromResult(new IPAddress[] { ip });

        // CGNAT 边界：100.64.0.0/10 = 100.64.0.0 - 100.127.255.255
        Assert.False(await BBDownApiServer.IsSafeCallbackUrlAsync("http://rebind.test/cb", Resolve(IPAddress.Parse("100.64.0.1"))));
        Assert.False(await BBDownApiServer.IsSafeCallbackUrlAsync("http://rebind.test/cb", Resolve(IPAddress.Parse("100.127.255.254"))));
        // IPv6 ULA（fc00::/7 = fc00:: - fdff:...）
        Assert.False(await BBDownApiServer.IsSafeCallbackUrlAsync("http://rebind.test/cb", Resolve(IPAddress.Parse("fc00::1"))));
        Assert.False(await BBDownApiServer.IsSafeCallbackUrlAsync("http://rebind.test/cb", Resolve(IPAddress.Parse("fdff::1"))));
        // 边界外的 100.63.0.1 / 100.128.0.1 不在拒绝段：公网可达地址放行（不为敏感段）
        Assert.True(await BBDownApiServer.IsSafeCallbackUrlAsync("http://rebind.test/cb", Resolve(IPAddress.Parse("100.63.0.1"))));
        Assert.True(await BBDownApiServer.IsSafeCallbackUrlAsync("http://rebind.test/cb", Resolve(IPAddress.Parse("100.128.0.1"))));
    }

    [Fact]
    public async Task IsSafeCallbackUrl_DnsRebindingDomain_Rejected()
    {
        // 攻击者注册一个解析到云元数据地址的域名：字符串比对会放行，
        // 必须解析 DNS 后按地址拒绝
        Task<IPAddress[]> ResolveToMetadata(string _) => Task.FromResult(new IPAddress[] { IPAddress.Parse("169.254.169.254") });
        Assert.False(await BBDownApiServer.IsSafeCallbackUrlAsync("http://metadata.internal/cb", ResolveToMetadata));

        Task<IPAddress[]> ResolveToLoopback(string _) => Task.FromResult(new IPAddress[] { IPAddress.Parse("127.0.0.1") });
        Assert.False(await BBDownApiServer.IsSafeCallbackUrlAsync("http://rebind.test/cb", ResolveToLoopback));

        // 域名解析到 RFC1918 内网也应拒绝：内网地址只允许"字面 IP"显式配置，
        // 域名重绑定打内网是 SSRF 横向面（攻击者注册域名解析到 10.0.0.x）
        Task<IPAddress[]> ResolveToPrivate(string _) => Task.FromResult(new IPAddress[] { IPAddress.Parse("10.0.0.5") });
        Assert.False(await BBDownApiServer.IsSafeCallbackUrlAsync("http://rebind-to-private.test/cb", ResolveToPrivate));

        // 字面 IP 的 RFC1918 仍放行（局域网回调是 serve 的正常用法）
        Assert.True(await BBDownApiServer.IsSafeCallbackUrlAsync("http://10.0.0.5:9000/cb"));
    }

    [Fact]
    public async Task IsSafeCallbackUrl_DnsFailure_Rejected()
    {
        // 域名无法解析：回调必然失败，按不安全处理
        Task<IPAddress[]> Throws(string _) => throw new SocketException((int)SocketError.HostNotFound);
        Assert.False(await BBDownApiServer.IsSafeCallbackUrlAsync("http://unresolvable.test/cb", Throws));
    }

    [Fact]
    public async Task IsSafeCallbackUrl_Ipv6Literal_DoesNotHitDnsResolver()
    {
        // 哨兵：抛非 SocketException 异常。若代码把 IPv6 字面量当主机名走了 DNS 分支，
        // 异常不会被 catch (SocketException) 接住而直接逃逸使测试失败——
        // 证明字面 IP 分支（DnsSafeHost 无方括号）真的被执行，而非 DNS 假阳性。
        Task<IPAddress[]> Sentinel(string _) => throw new InvalidOperationException("DNS 分支不应被调用");

        // 回环/链路本地 IPv6 字面量：必须被字面 IP 检查拒绝，而不是靠 DNS 解析失败兜底
        Assert.False(await BBDownApiServer.IsSafeCallbackUrlAsync("http://[::1]/cb", Sentinel));
        Assert.False(await BBDownApiServer.IsSafeCallbackUrlAsync("http://[fe80::1]/cb", Sentinel));
        Assert.False(await BBDownApiServer.IsSafeCallbackUrlAsync("http://[::ffff:127.0.0.1]/cb", Sentinel));
        Assert.False(await BBDownApiServer.IsSafeCallbackUrlAsync("http://[::]/cb", Sentinel));
        // 公网 IPv6 字面量（管理员显式配置的 webhook）：合法且不解析 DNS——
        // 修复前它会被 Dns.GetHostAddressesAsync("[2001:db8::1]") 解析失败永久判为不合法
        Assert.True(await BBDownApiServer.IsSafeCallbackUrlAsync("http://[2001:db8::1]/cb", Sentinel));
    }

    [Fact]
    public void IsLoopbackOrigin_AcceptsOnlyLoopbackSources()
    {
        // 回环来源（含 IPv6 字面量，DnsSafeHost 无方括号）：放行
        Assert.True(BBDownApiServer.IsLoopbackOrigin("http://127.0.0.1:23333"));
        Assert.True(BBDownApiServer.IsLoopbackOrigin("http://localhost:8080"));
        Assert.True(BBDownApiServer.IsLoopbackOrigin("http://[::1]:23333"));
        // 非回环/攻击者来源：拒绝（DNS rebinding 下 Origin 是攻击者域名）
        Assert.False(BBDownApiServer.IsLoopbackOrigin("https://evil.example"));
        Assert.False(BBDownApiServer.IsLoopbackOrigin("http://192.168.1.10:9000"));
        Assert.False(BBDownApiServer.IsLoopbackOrigin("not a url"));
        Assert.False(BBDownApiServer.IsLoopbackOrigin(""));
    }

    [Fact]
    public void IsAuthLockedOut_SlidingWindow_ThresholdReached()
    {
        // 1 分钟窗口内失败次数达到阈值后必须锁死（令暴力枚举失效）；
        // 窗口按来源 IP 隔离，不同 IP 互不影响。
        var server = new BBDownApiServer();
        for (int i = 0; i < 5; i++)
        {
            Assert.False(server.IsAuthLockedOut("10.0.0.1"), $"第 {i + 1} 次失败不应锁死");
        }
        Assert.True(server.IsAuthLockedOut("10.0.0.1"), "第 6 次失败必须锁死");
        Assert.True(server.IsAuthLockedOut("10.0.0.1"), "锁死后持续拒绝");
        // 其它来源 IP 不受影响（同机合法客户端/攻击者换 IP 各自独立计数）
        Assert.False(server.IsAuthLockedOut("10.0.0.2"));
    }

    [Fact]
    public void IsAuthLockedOut_DictionaryStaysBounded_UnderIpHammering()
    {
        // 攻击者持续换新 IP/XFF 轰炸时，每条失败都会登记新键；这些键是"最近失败"
        // 永不过期，仅删过期条目约束不住字典大小。必须按最后失败时间裁剪，字典有界。
        var server = new BBDownApiServer();
        var maxTracked = (int)typeof(BBDownApiServer)
            .GetField("MaxTrackedAuthFailureIps", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        // 模拟远超上限的独立来源（1.2 倍于上限），每个来源只失败一次（不触发锁死）
        for (int i = 0; i < maxTracked * 12 / 10; i++)
        {
            server.IsAuthLockedOut($"10.0.{i / 250}.{i % 250}");
        }

        var dict = (Dictionary<string, List<DateTime>>)typeof(BBDownApiServer)
            .GetField("_authFailures", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(server)!;
        // 允许 +1 的窗口抖动（裁剪发生在下次登记前），但绝不能随 IP 数线性增长
        Assert.InRange(dict.Count, 1, maxTracked + 1);
    }

    [Fact]
    public void TrimFinishedTasksLocked_KeepsNewestByCreateTime_NotByCompletionOrder()
    {
        // 列表按"完成顺序"追加，与"创建顺序"无关。旧实现按完成顺序 RemoveRange 头部
        // 会误删"后创建但先完成"的任务；必须按 TaskCreateTime 保留最新的上限条。
        var server = new BBDownApiServer();
        var maxFinished = (int)typeof(BBDownApiServer)
            .GetField("MaxFinishedTasks", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        var finished = (List<DownloadTask>)typeof(BBDownApiServer)
            .GetField("finishedTasks", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(server)!;

        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        // 构造 上限+5 条：前 5 条"后创建但先完成"（TaskCreateTime 最新、排在列表头），
        // 其余 上限 条创建时间更旧。旧实现 RemoveRange(0,5) 会删掉头 5 条（最新的，错误）。
        for (int i = 0; i < 5; i++)
            finished.Add(new DownloadTask($"new-{i}", "u", now - i));
        for (int i = 0; i < maxFinished; i++)
            finished.Add(new DownloadTask($"old-{i}", "u", now - maxFinished - i));

        var method = typeof(BBDownApiServer)
            .GetMethod("TrimFinishedTasksLocked", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var taskLock = typeof(BBDownApiServer)
            .GetField("_taskLock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(server)!;
        lock (taskLock)
        {
            method.Invoke(server, null);
        }

        Assert.Equal(maxFinished, finished.Count);
        Assert.Contains(finished, t => t.Aid == "new-0");   // 最新创建的被保留
        Assert.Contains(finished, t => t.Aid == "new-4");
        Assert.DoesNotContain(finished, t => t.Aid == $"old-{maxFinished - 5}"); // 最旧创建的被裁剪
        Assert.DoesNotContain(finished, t => t.Aid == $"old-{maxFinished - 1}");
        Assert.Contains(finished, t => t.Aid == "old-0"); // 较新的 old 条目保留（不是删列表头）
    }

    [Fact]
    public void SanitizeUntrustedOptions_ClearsExecutionFields()
    {
        var req = new ServeRequestOptions
        {
            Aria2cArgs = "--on-download-complete=\"rm -rf ~\"",
            Aria2cPath = "/tmp/evil",
            Aria2cProxy = "http://evil:8080",
            // 同属"让服务器执行指定程序/改动进程环境"的字段：混流二进制路径、DRM 路径、工作目录
            FFmpegPath = "/tmp/evil-ffmpeg",
            Mp4boxPath = "/tmp/evil-mp4box",
            WvdPath = "/tmp/evil.wvd",
            Mp4decryptPath = "/tmp/evil-mp4decrypt",
            WorkDir = "/tmp/evil-dir",
            // 自定义 UA 是进程级静态字段：一个任务设置后污染所有后续任务
            UserAgent = "EvilUA/1.0",
            // NotifyWebhook 会绕过 CallBackWebHook 的 SSRF 校验向任意地址 POST
            NotifyWebhook = "http://evil.example/hook",
            // CallBackWebHook：任务回调改为服务端 allowlist（--notify-webhook），
            // 客户端请求体中的回调字段必须被清零，防止任意客户端驱动本机 POST
            CallBackWebHook = "http://evil.example/client-callback",
            // Insecure 会全局关闭 TLS 校验：serve 下必须忽略，否则任意客户端可让携带
            // 操作者 SESSDATA 的请求跳过证书校验被 MITM 截获
            Insecure = true,
            // ForceHttp 会把媒体 CDN 的 https 改写成明文 http（ReplaceUrl），而下载请求
            // 仍携带操作者 SESSDATA Cookie：与 Insecure 同类威胁，serve 下必须忽略
            ForceHttp = true,
            // FilePattern/MultiFilePattern 会被当作保存路径模板，字面量中的 ".." 段原样保留
            // （路径穿越面），serve 下必须回落默认模板
            FilePattern = "../../../evil/out.mp4",
            MultiFilePattern = "/tmp/evil/multi.mp4",
            // DrmKeyHex/DrmKidHex 会经 mp4decrypt 参与解密，是客户端可控的密钥注入点
            DrmKeyHex = "4141414141414141414141414141414141414141414141414141414141414141",
            DrmKidHex = "42424242424242424242424242424242",
        };
        BBDownApiServer.SanitizeUntrustedOptions(req);
        Assert.Equal("", req.Aria2cArgs);
        Assert.Equal("", req.Aria2cPath);
        Assert.Equal("", req.Aria2cProxy);
        Assert.Equal("", req.FFmpegPath);
        Assert.Equal("", req.Mp4boxPath);
        Assert.Equal("", req.WvdPath);
        Assert.Equal("", req.Mp4decryptPath);
        Assert.Equal("", req.WorkDir);
        Assert.Equal("", req.UserAgent);
        Assert.Equal("", req.NotifyWebhook);
        Assert.Equal("", req.CallBackWebHook);
        Assert.False(req.Insecure);
        Assert.False(req.ForceHttp);
        Assert.Equal("", req.FilePattern);
        Assert.Equal("", req.MultiFilePattern);
        Assert.Equal("", req.DrmKeyHex);
        Assert.Equal("", req.DrmKidHex);
    }

    [Fact]
    public void SanitizeUntrustedOptions_ClampsNumerics()
    {
        // F9：数值钳制是慢速 DoS 防线——客户端可传失控重试次数/超长延迟/超大分片并用
        // “合法值”占满共享并发槽数小时。此前的 Sanitize 仅测字段清零，重试参数 clamp
        // （RetryCount→[1,3]、RetryDelay→[0,5000]、MuxerTimeout→[1,120]、
        // DelayPerPage→[0,30]、ThreadSegmentSize→[1,64]）零测试，删掉防线也全绿。
        var req = new ServeRequestOptions
        {
            RetryCount = 999,
            RetryDelay = 999999,
            MuxerTimeout = 9999,
            DelayPerPage = 999,
            ThreadSegmentSize = 9999,
        };
        BBDownApiServer.SanitizeUntrustedOptions(req);
        Assert.Equal(3, req.RetryCount);
        Assert.Equal(5000, req.RetryDelay);
        Assert.Equal(120, req.MuxerTimeout);
        Assert.Equal(30, req.DelayPerPage);
        Assert.Equal(64, req.ThreadSegmentSize);

        // 下限：RetryCount 钳到 1（显式 0 会被 ValidateNumericOptions 判非法任务，
        // 与 Clamp(0,3) 矛盾——A4 统一下限为 1）；0 延迟合法（不等待直接重试）
        var zero = new ServeRequestOptions { RetryCount = 0, RetryDelay = 0, MuxerTimeout = 0, DelayPerPage = 0, ThreadSegmentSize = 0 };
        BBDownApiServer.SanitizeUntrustedOptions(zero);
        Assert.Equal(1, zero.RetryCount);
        Assert.Equal(0, zero.RetryDelay);
        Assert.Equal(1, zero.MuxerTimeout);
        Assert.Equal(0, zero.DelayPerPage);
        Assert.Equal(1, zero.ThreadSegmentSize);
    }

    [Fact]
    public void SanitizeUntrustedOptions_EmptyHost_FallsBackToOfficial()
    {
        // host 为空/null 时也必须回落官方默认，否则番剧/TV/intl URL 拼成 https:///... 抛 UriFormatException
        var req = new ServeRequestOptions { Host = "", EpHost = "", TvHost = "   ", UposHost = "" };
        BBDownApiServer.SanitizeUntrustedOptions(req);
        Assert.Equal("api.bilibili.com", req.Host);
        Assert.Equal("api.bilibili.com", req.EpHost);
        Assert.Equal("api.snm0516.aisee.tv", req.TvHost);
        Assert.Equal("", req.UposHost);
    }

    [Fact]
    public void IsOfficialHost_SlashConfusion_Rejected()
    {
        // 斜杠协议混淆：纯后缀匹配会放行 "evil.com/.bilibili.com"，规范化后必须拒绝
        Assert.False(BBDownApiServer.IsOfficialHost("evil.com/.bilibili.com"));
        Assert.False(BBDownApiServer.IsOfficialHost("https://evil.com/.bilibili.com"));
        Assert.False(BBDownApiServer.IsOfficialHost("evil.com@bilibili.com"));
        Assert.False(BBDownApiServer.IsOfficialHost("bilibili.com.evil.com"));
        Assert.False(BBDownApiServer.IsOfficialHost("https://bilibili.com.evil.com"));
        // 官方域名（含子域）仍应放行
        Assert.True(BBDownApiServer.IsOfficialHost("api.bilibili.com"));
        Assert.True(BBDownApiServer.IsOfficialHost("https://api.bilibili.com"));
        Assert.True(BBDownApiServer.IsOfficialHost("upos-sz-mirrorcoso1.bilivideo.com"));
        Assert.True(BBDownApiServer.IsOfficialHost("https://grpc.biliapi.net"));
        Assert.True(BBDownApiServer.IsOfficialHost("api.biliintl.com"));
        Assert.True(BBDownApiServer.IsOfficialHost("https://api.biliintl.com"));
        Assert.True(BBDownApiServer.IsOfficialHost("api.biliapi.com"));
        // 空值视为合法（回落默认）
        Assert.True(BBDownApiServer.IsOfficialHost(null));
        Assert.True(BBDownApiServer.IsOfficialHost(""));
    }

    [Fact]
    public void SanitizeUntrustedOptions_HostWhitelist_FallsBackToOfficial()
    {
        // host 字段决定凭据发送目标：指向攻击者域名的 host 必须被回落为官方默认，
        // 否则操作者的 B 站 Cookie 会被发往攻击者服务器（SSRF + 凭据外泄）
        var req = new ServeRequestOptions
        {
            Host = "https://evil.example",
            EpHost = "evil.example",
            TvHost = "http://attacker.com:8080",
            UposHost = "https://user:pass@bilibili.com", // userinfo 伪装信任域也要拒绝
        };
        BBDownApiServer.SanitizeUntrustedOptions(req);
        Assert.Equal("api.bilibili.com", req.Host);
        Assert.Equal("api.bilibili.com", req.EpHost);
        Assert.Equal("api.snm0516.aisee.tv", req.TvHost);
        Assert.Equal("", req.UposHost);

        // 官方域名（含子域）应保留
        var ok = new ServeRequestOptions
        {
            Host = "https://api.bilibili.com",
            EpHost = "https://grpc.biliapi.net",
            TvHost = "https://api.snm0516.aisee.tv",
            UposHost = "upos-sz-mirrorcoso1.bilivideo.com",
        };
        BBDownApiServer.SanitizeUntrustedOptions(ok);
        Assert.Equal("api.bilibili.com", ok.Host);
        Assert.Equal("grpc.biliapi.net", ok.EpHost);
        Assert.Equal("api.snm0516.aisee.tv", ok.TvHost);
        Assert.Equal("upos-sz-mirrorcoso1.bilivideo.com", ok.UposHost);
    }

    [Fact]
    public void Constructor_NonPositiveMaxConcurrent_DoesNotThrow()
    {
        _ = new BBDownApiServer(0);
    }

    [Theory]
    [InlineData("Could not find file 'D:\\data\\video\\xxx.mp4'", "Could not find file 'xxx.mp4'")]
    [InlineData("Could not find file '/home/user/downloads/xxx.mp4'", "Could not find file 'xxx.mp4'")]
    [InlineData("Could not find a part of the path 'C:\\temp\\dir'", "Could not find a part of the path 'dir'")]
    [InlineData("Could not find a part of the path '\\\\server\\share\\dir'", "Could not find a part of the path 'dir'")]
    [InlineData("磁盘空间不足", "磁盘空间不足")] // 无路径消息原样保留
    [InlineData(null, "")]
    // URL/相对路径不是绝对路径，不得被替换（http://x/y 会变成 "http:y"，a/b/c 会变成 "ac"）
    [InlineData("下载失败: https://example.com/a/b/c.mp4 已超时", "下载失败: https://example.com/a/b/c.mp4 已超时")]
    [InlineData("无法打开 data/video/xxx.mp4", "无法打开 data/video/xxx.mp4")]
    public void SanitizeErrorMessage_HidesAbsolutePathLeaksFilename(string? input, string expected)
    {
        // 绝对路径（盘符/UNC/Unix 根）在错误消息中应被替换为末段，防止经 /get-tasks
        // 泄露服务器文件系统布局；消息其余部分保留；无路径消息不受影响。
        Assert.Equal(expected, BBDownApiServer.SanitizeErrorMessage(input));
    }
}
