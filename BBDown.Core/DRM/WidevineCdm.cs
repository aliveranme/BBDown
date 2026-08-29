using System.Net.Http.Headers;
using System.Security.Cryptography;
using Google.Protobuf;
using BBDown.Core.DRM.Proto;
using BBDown.Core.Util;

namespace BBDown.Core.DRM;

public sealed class WidevineCdm : IDisposable
{
    private readonly WvdDevice _device;
    private bool _disposed;

    private const string LicenseUrl = "https://bvc-drm.bilivideo.com/bili_widevine";
    private const string CertUrl = "https://bvc-drm.bilivideo.com/cer/bilibili_certificate.bin";
    private static readonly byte[] WidevineSystemId = {
        0xed, 0xef, 0x8b, 0xa9, 0x79, 0xd6, 0x4a, 0xce,
        0xa3, 0xc8, 0x27, 0xdc, 0xd5, 0x1d, 0x21, 0xed
    };

    private WidevineCdm(WvdDevice device)
    {
        _device = device;
    }

    public static async Task<(string kid, string key)[]?> GetKeysAsync(string psshB64, string wvdPath, CancellationToken token = default)
    {
        WvdDevice device;
        try
        {
            device = WvdDevice.Load(wvdPath);
        }
        catch (Exception ex)
        {
            Logger.LogWarn($"加载 device.wvd 失败: {ex.Message}");
            return null;
        }

        using var cdm = new WidevineCdm(device);
        try
        {
            return await cdm.GetKeysInternalAsync(psshB64, token);
        }
        catch (OperationCanceledException)
        {
            // 用户取消（Ctrl+C / serve 关停）必须向上传播：GetKeysInternalAsync 内部的
            // 许可证请求用调用方 token，取消时抛 OCE——若被下方 catch (Exception) 吞掉
            // 会误报"解密失败"，取消信号丢失导致上层继续执行已无意义的下载流程。
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarn($"Widevine 解密失败: {ex.Message}");
            return null;
        }
    }

    private async Task<(string kid, string key)[]?> GetKeysInternalAsync(string psshB64, CancellationToken token = default)
    {
        // BiliBili 不需要 service certificate / privacy mode
        var (psshPayload, keyIds) = ParsePsshBox(psshB64);
        if (keyIds.Count == 0)
        {
            Logger.LogWarn("PSSH 中未找到 key ID");
            return null;
        }

        var (challenge, requestBytes) = BuildChallenge(keyIds, psshPayload);

        byte[] responseBytes;
        try
        {
            responseBytes = await SendRequestAsync(challenge, token);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarn($"许可证请求失败: {ex.Message}");
            return null;
        }
        catch (TimeoutException ex)
        {
            // SendRequestAsync 重试耗尽后把超时转成 TimeoutException（与 HTTPUtil 策略一致）：
            // 若保留 TaskCanceledException 原样抛出，会在 GetKeysAsync 被
            // catch (OperationCanceledException) throw 误判为"用户取消"（诊断误导）。
            Logger.LogWarn($"许可证请求超时: {ex.Message}");
            return null;
        }

        return ParseResponse(responseBytes, requestBytes);
    }

    // ---- PSSH parser ----

    private static (byte[] payload, List<byte[]> keyIds) ParsePsshBox(string psshB64)
    {
        var kids = new List<byte[]>();
        byte[] payload = Array.Empty<byte>();
        try
        {
            var raw = Convert.FromBase64String(psshB64);
            if (raw.Length < 28) return (payload, kids);

            var pos = 8; // skip box size + type
            var version = raw[pos];
            pos += 4; // version + flags
            if (!raw.AsSpan(pos, 16).SequenceEqual(WidevineSystemId))
                return (payload, kids);
            pos += 16;

            if (version >= 1)
            {
                if (pos + 4 <= raw.Length)
                {
                    var count = ReadU32Be(raw, pos); pos += 4;
                    for (var i = 0; i < count && pos + 16 <= raw.Length; i++)
                    {
                        var kid = new byte[16];
                        Buffer.BlockCopy(raw, pos, kid, 0, 16);
                        kids.Add(kid);
                        pos += 16;
                    }
                }
            }

            if (pos + 4 <= raw.Length)
            {
                var dataSize = (int)ReadU32Be(raw, pos); pos += 4;
                if (dataSize > 0 && dataSize <= 4096 && pos + dataSize <= raw.Length)
                {
                    payload = new byte[dataSize];
                    Buffer.BlockCopy(raw, pos, payload, 0, dataSize);
                    if (kids.Count == 0)
                    {
                        var header = WidevineCencHeader.Parser.ParseFrom(payload);
                        foreach (var k in header.KeyIds)
                            kids.Add(k.ToByteArray());
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidProtocolBufferException or FormatException)
        {
            Logger.LogDebug("PSSH parse error: {0}", ex.Message);
        }
        return (payload, kids);
    }

    private static uint ReadU32Be(byte[] buf, int offset)
    {
        return ((uint)buf[offset] << 24) | ((uint)buf[offset + 1] << 16)
             | ((uint)buf[offset + 2] << 8) | buf[offset + 3];
    }

    // ---- license challenge ----

    private (byte[] challenge, byte[] requestBytes) BuildChallenge(List<byte[]> keyIds, byte[] psshPayload)
    {
        // request_id: 16 random bytes, stored as uppercase hex string bytes
        var requestIdRaw = new byte[16];
        RandomNumberGenerator.Fill(requestIdRaw);
        var requestId = Convert.ToHexString(requestIdRaw).ToUpperInvariant();
        var requestIdBytes = System.Text.Encoding.ASCII.GetBytes(requestId);

        var wid = new LicenseRequest.Types.ContentIdentification.Types.WidevinePsshData();
        wid.PsshData.Add(ByteString.CopyFrom(psshPayload));
        wid.RequestId = ByteString.CopyFrom(requestIdBytes);
        wid.LicenseType = LicenseType.Streaming;

        var req = new LicenseRequest
        {
            ClientId = _device.ClientIdentification,
            Type = LicenseRequest.Types.RequestType.New,
            RequestTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ProtocolVersion = ProtocolVersion.Version21,
            KeyControlNonce = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue),
            ContentId = new LicenseRequest.Types.ContentIdentification
            {
                WidevinePsshData = wid
            }
        };

        var plaintext = req.ToByteArray();

        // Sign with device RSA private key, SHA1 + PSS
        var sig = _device.Rsa.SignData(plaintext, HashAlgorithmName.SHA1, RSASignaturePadding.Pss);

        var sm = new SignedMessage
        {
            Type = SignedMessage.Types.MessageType.LicenseRequest,
            Msg = ByteString.CopyFrom(plaintext),
            Signature = ByteString.CopyFrom(sig),
        };
        return (sm.ToByteArray(), plaintext);
    }

    // ---- HTTP ----

    /// <summary>许可证请求最大尝试次数（含首次）：CDN 瞬时 5xx/传输故障重试上限。</summary>
    private const int MaxLicenseAttempts = 3;

    /// <summary>许可证重试退避毫秒数：500ms × 2^(attempt-1)，单次封顶 4s。</summary>
    private static int LicenseRetryDelayMs(int attempt) => Math.Min(500 * (1 << (attempt - 1)), 4000);

    private static async Task<byte[]> SendRequestAsync(byte[] body, CancellationToken token = default)
    {
        // 许可证请求有界重试：5xx（服务器瞬时过载/CDN 抖动）与瞬时传输故障（无状态码的
        // HttpRequestException、token 未取消的超时）参与最多 MaxLicenseAttempts 次尝试。
        // 4xx 是确定性失败（设备证书被拒/吊销），重试无意义，立即抛出。
        // 真正的用户取消（token 已取消）不重试，原样向上传播——GetKeysAsync 的
        // catch (OperationCanceledException) throw 保证取消信号不丢失。
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var content = new ByteArrayContent(body);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-protobuf");

                using var req = new HttpRequestMessage(HttpMethod.Post, LicenseUrl) { Content = content };
                req.Headers.TryAddWithoutValidation("User-Agent", HTTPUtil.GetUserAgent(null));
                req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
                req.Headers.TryAddWithoutValidation("Accept", "*/*");

                // 许可证响应携带内容解密密钥、请求体是设备私钥签名的 challenge：
                // 强制走始终校验证书的客户端（VerifiedNoRedirectClient），不受用户 --insecure
                // 影响——跳过 TLS 校验会让中间人直接窃取内容密钥；同时禁自动跟随重定向
                // （RF-4）：AllowAutoRedirect=true 会在 307/308 上连同 body 重放到跨主机目标，
                // 签名载荷随跳转外发。3xx 在此显式拦下报错，不跟随、不外发（与 gRPC POST 同构）。
                using var resp = await HTTPUtil.VerifiedNoRedirectClient.SendAsync(req, token);
                // 3xx：许可证端点不应重定向，显式拦截（不允许 challenge/body 随 307/308 重放）。
                // 状态码 <500 不满足重试谓词，按确定性失败立即抛出。
                if ((int)resp.StatusCode is >= 300 and < 400)
                    throw new HttpRequestException(
                        $"许可证端点返回意外重定向 (HTTP {(int)resp.StatusCode})，已拒绝跟随以避免签名请求体外发",
                        null, resp.StatusCode);
                if (!resp.IsSuccessStatusCode)
                {
                    // 5xx 瞬时故障参与有界重试；4xx 确定性失败立即抛（带状态码供上层定位）
                    if ((int)resp.StatusCode >= 500 && attempt < MaxLicenseAttempts)
                    {
                        await Task.Delay(LicenseRetryDelayMs(attempt), token);
                        continue;
                    }
                    // 非 2xx：许可证服务器常回错误状态文本（设备证书被拒/吊销等）。读错误体
                    // 给出可操作诊断，替代 EnsureSuccessStatusCode 的裸状态码消息（状态码消息
                    // 不含吊销/证书这类可定位信息）。错误体通常很短且不含密钥材料。
                    string errorBody;
                    try { errorBody = await resp.Content.ReadAsStringAsync(token); }
                    catch (OperationCanceledException) { throw; } // 用户取消向上传播，不吞
                    catch (Exception) { errorBody = ""; }
                    throw new HttpRequestException(
                        $"许可证请求失败 (HTTP {(int)resp.StatusCode})" +
                        (string.IsNullOrEmpty(errorBody) ? "" : $": {errorBody}"),
                        null, resp.StatusCode);
                }
                return await resp.Content.ReadAsByteArrayAsync(token);
            }
            catch (HttpRequestException ex) when (attempt < MaxLicenseAttempts
                                                  && (ex.StatusCode is null || (int)ex.StatusCode >= 500))
            {
                // 瞬断（无状态码的 HttpRequestException，如连接被重置）或 5xx：退避重试。
                // 重试耗尽或带 4xx 状态码（确定性失败）时 when 不满足，异常原样向上抛出。
                await Task.Delay(LicenseRetryDelayMs(attempt), token);
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            {
                // 超时抛的 TaskCanceledException 其 token 未取消：瞬时故障，参与重试。
                // 重试耗尽后转成 TimeoutException——若保留 OCE 原样抛出，会被 GetKeysAsync
                // 的 catch (OperationCanceledException) throw 误判为"用户取消"（诊断误导，
                // 与 HTTPUtil 的超时转 TimeoutException 策略不一致）。
                if (attempt < MaxLicenseAttempts)
                {
                    await Task.Delay(LicenseRetryDelayMs(attempt), token);
                    continue;
                }
                throw new TimeoutException($"许可证请求超时（{attempt} 次尝试后）: {LicenseUrl}", ex);
            }
        }
    }

    // ---- license response ----

    private (string kid, string key)[]? ParseResponse(byte[] data, byte[] challenge)
    {
        SignedMessage sm;
        try
        {
            sm = SignedMessage.Parser.ParseFrom(data);
        }
        catch (InvalidProtocolBufferException ex)
        {
            Logger.LogDebug("License response protobuf parse failed: {0}", ex.Message);
            try
            {
                var err = System.Text.Encoding.UTF8.GetString(data);
                Logger.LogWarn($"License server returned error: {err}");
            }
            catch (Exception decodeEx)
            {
                Logger.LogDebug("Unable to decode license server error response: {0}", decodeEx.Message);
                Logger.LogWarn($"License request failed with unparseable response ({data.Length} bytes)");
            }
            return null;
        }

        if (sm.Type != SignedMessage.Types.MessageType.License)
        {
            // 升为 Warn：异常响应类型通常是设备证书被服务器拒绝/吊销的征兆。
            // 仅 Debug 记录会让用户在 DRM 解密失败时拿不到任何定位信息。
            Logger.LogWarn($"许可证返回异常响应类型: {sm.Type}（可能为设备证书被服务器拒绝或已吊销）");
            return null;
        }

        // Decrypt session key with device RSA private key
        // Try OAEP-SHA1 first (older devices), fall back to OAEP-SHA256
        var encSessionKey = sm.SessionKey.ToByteArray();
        byte[] sessionKey;
        try
        {
            sessionKey = _device.Rsa.Decrypt(encSessionKey, RSAEncryptionPadding.OaepSHA1);
        }
        catch (CryptographicException)
        {
            Logger.LogDebug("OAEP-SHA1 session key decryption failed, trying OAEP-SHA256");
            sessionKey = _device.Rsa.Decrypt(encSessionKey, RSAEncryptionPadding.OaepSHA256);
        }

        if (sessionKey.Length != 16)
        {
            Logger.LogWarn($"会话密钥长度异常: {sessionKey.Length}");
            return null;
        }

        // Derive keys for signature verification and content decryption
        var (encContext, macContext) = WidevineCrypto.DeriveContext(challenge);
        var (encKey, macKeyServer, _) = WidevineCrypto.DeriveKeys(sessionKey, encContext, macContext);

        try
        {
            // Verify HMAC-SHA256 signature
            var msg = sm.Msg.ToByteArray();
            var sig = sm.Signature.ToByteArray();
            using var hmac = new HMACSHA256(macKeyServer);
            var oem = sm.OemcryptoCoreMessage?.ToByteArray() ?? Array.Empty<byte>();
            hmac.TransformBlock(oem, 0, oem.Length, null, 0);
            var computed = hmac.ComputeHash(msg);

            // 常量时间比较：SequenceEqual 在首个差异字节短路，对 HMAC 签名做时序探测
            // 理论上可逐字节还原签名；FixedTimeEquals 在定长输入上耗时与内容无关。
            if (!CryptographicOperations.FixedTimeEquals(sig, computed))
            {
                Logger.LogWarn("许可证 HMAC 签名校验失败");
                return null;
            }

            // msg is plaintext License
            var license = License.Parser.ParseFrom(msg);
            if (license.Key.Count == 0)
            {
                Logger.LogWarn("许可证中未包含密钥");
                return null;
            }

            var keys = new List<(string kid, string key)>();
            foreach (var kc in license.Key)
            {
                if (kc.Type != License.Types.KeyContainer.Types.KeyType.Content)
                    continue;

                var kidBytes = kc.Id?.ToByteArray();
                if (kidBytes == null || kidBytes.Length == 0)
                    continue;

                var keyIv = kc.Iv?.ToByteArray() ?? new byte[16];
                if (keyIv.Length < 16)
                {
                    var tmp = new byte[16];
                    Buffer.BlockCopy(keyIv, 0, tmp, 0, Math.Min(keyIv.Length, 16));
                    keyIv = tmp;
                }

                var encContentKey = kc.Key.ToByteArray();
                if (encContentKey.Length == 0)
                    continue;

                byte[] contentKey;

                // Widevine spec: if IV is unset or all zeros → ECB, otherwise CBC
                var isZeroIv = keyIv.All(b => b == 0);
                try
                {
                    if (isZeroIv)
                    {
                        contentKey = WidevineCrypto.AesEcbDecrypt(encContentKey, encKey);
                    }
                    else
                    {
                        var dec = WidevineCrypto.AesCbcDecrypt(encContentKey, encKey, keyIv);
                        contentKey = WidevineCrypto.Pkcs7Unpad(dec);
                    }
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException)
                {
                    // 单个 key 解密失败（key/IV 不匹配或数据损坏）不应放弃整份授权：
                    // 其余 key 仍可正常解密，跳过这条损坏记录
                    Logger.LogDebug("解密 content key 失败: {0}", ex.Message);
                    continue;
                }

                var kidHex = Convert.ToHexString(kidBytes).ToLowerInvariant();
                var keyHex = Convert.ToHexString(contentKey).ToLowerInvariant();
                CryptographicOperations.ZeroMemory(contentKey);
                keys.Add((kidHex, keyHex));
            }

            return keys.Count > 0 ? keys.ToArray() : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
            CryptographicOperations.ZeroMemory(encKey);
            CryptographicOperations.ZeroMemory(macKeyServer);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _device?.Dispose();
    }
}
