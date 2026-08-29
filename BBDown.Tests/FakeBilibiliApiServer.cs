using System.Net;
using System.Text;

namespace BBDown.Tests;

/// <summary>
/// 本地假 B 站 API 服务器（HttpListener 回环监听 + JSON 夹具回放），仿
/// HttpUtilRetryTests.ScriptedServer / LiveStreamUtilTests.FakeLiveServer 同构模式。
/// 按请求 path 分发登记的夹具；可叠加"参数=值"精确条目（优先于 path 级），
/// 用于免二压重发（qn=127）与 INTL 双次请求（prefer_code_type=0/1）等分轮响应。
/// 记录收到的请求 path/query 供断言 WBI 参数存在性、qn 序列与接口路由。
/// </summary>
internal sealed class FakeBilibiliApiServer : IDisposable
{
    public sealed record RecordedRequest(string Path, string Query);

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _fixturesByPath = new(StringComparer.Ordinal);
    private readonly List<(string Path, string ParamName, string ParamValue, string Json)> _exactFixtures = new();
    private readonly List<RecordedRequest> _requests = new();

    public int Port { get; }

    public FakeBilibiliApiServer()
    {
        Port = TestPort.Allocate();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var ctx = await _listener.GetContextAsync();
                    try
                    {
                        var path = ctx.Request.Url?.AbsolutePath ?? "/";
                        var query = ctx.Request.Url?.Query ?? "";
                        lock (_gate) { _requests.Add(new RecordedRequest(path, query)); }
                        var body = Resolve(path, query);
                        if (body is null)
                        {
                            // 404：夹具未登记。4xx 不触发 HTTPUtil 的 5xx 重试，让断言失败快速直达
                            var miss = Encoding.UTF8.GetBytes($"no fixture registered for {path}");
                            ctx.Response.StatusCode = 404;
                            ctx.Response.ContentLength64 = miss.Length;
                            ctx.Response.OutputStream.Write(miss, 0, miss.Length);
                        }
                        else
                        {
                            var bytes = Encoding.UTF8.GetBytes(body);
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/json";
                            ctx.Response.ContentLength64 = bytes.Length;
                            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        }
                        ctx.Response.Close();
                    }
                    catch { /* 客户端中止：忽略 */ }
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                /* 服务停止 */
            }
        });
    }

    /// <summary>登记 path 级夹具：该 path 的所有请求（任意参数）都返回此内容。</summary>
    public void Register(string path, string json)
    {
        lock (_gate) _fixturesByPath[path] = json;
    }

    /// <summary>登记 path+参数 精确夹具：优先于 path 级，用于按 qn / prefer_code_type 区分分轮响应。</summary>
    public void Register(string path, string paramName, string paramValue, string json)
    {
        lock (_gate) _exactFixtures.Add((path, paramName, paramValue, json));
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    private string? Resolve(string path, string query)
    {
        lock (_gate)
        {
            foreach (var (p, paramName, paramValue, json) in _exactFixtures)
            {
                if (p == path && GetQueryValue(query, paramName) == paramValue)
                    return json;
            }
            return _fixturesByPath.TryGetValue(path, out var fixture) ? fixture : null;
        }
    }

    /// <summary>从 "?a=1&b=2" 形式的 query 提取指定参数首个值；缺失返回空串。</summary>
    public static string GetQueryValue(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            if (key.Equals(name, StringComparison.Ordinal))
                return Uri.UnescapeDataString(eq < 0 ? "" : pair[(eq + 1)..]);
        }
        return "";
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _listener.Close();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
