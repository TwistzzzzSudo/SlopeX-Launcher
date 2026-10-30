using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModAccount
{
    /// <summary>设备码信息：给用户看的码与网址。</summary>
    public sealed class DeviceCodeInfo
    {
        public string UserCode { get; set; }
        public string DeviceCode { get; set; }
        public string VerificationUri { get; set; }
        public string Message { get; set; }
        public int ExpiresIn { get; set; }
        public int Interval { get; set; } = 5;
    }

    /// <summary>已登录的正版账号会话。</summary>
    public sealed class MsaSession
    {
        public string Name { get; set; }
        public string Uuid { get; set; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }

        /// <summary>AccessToken 过期时间（UTC）。</summary>
        public DateTime ExpiresAt { get; set; }

        public bool HasToken => !string.IsNullOrEmpty(AccessToken);
        public bool Expired => DateTime.UtcNow >= ExpiresAt.AddMinutes(-2);
    }

    /// <summary>XSTS 阶段的可读失败原因。</summary>
    public sealed class MsaException : Exception
    {
        public MsaException(string message) : base(message) { }
    }

    /// <summary>
    /// 微软正版登录（OAuth 2.0 设备码 → Xbox Live → XSTS → Minecraft）。
    ///
    /// 设计约束：
    /// <list type="bullet">
    /// <item>登录在**微软自己的网页**上完成，启动器拿不到密码，只拿到 token。</item>
    /// <item>走公共客户端流，没有 client_secret——所以 Azure 那边必须开启「允许公共客户端流」。</item>
    /// <item>token 落盘在 <c>SlopeXData\accounts.json</c>，2 小时过期后自动静默刷新。</item>
    /// </list>
    /// </summary>
    public static class Msa
    {
        /// <summary>自建 Azure 应用（公共客户端）的 ID。公开信息，不是密钥。</summary>
        public const string ClientId = "5fd00f1e-021f-494e-90ad-7a3c3a58989d";

        private const string Scope = "XboxLive.signin offline_access";
        private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
        private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
        private const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
        private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
        private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
        private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

        private static readonly object Gate = new object();
        private static MsaSession _current;
        private static bool _loaded;

        public static string StoreFile => Path.Combine(Paths.DataDir, "accounts.json");

        /// <summary>当前会话；未登录为 null。</summary>
        public static MsaSession Current
        {
            get
            {
                lock (Gate)
                {
                    if (!_loaded) { _current = LoadSession(); _loaded = true; }
                    return _current;
                }
            }
        }

        public static bool IsLoggedIn => Current != null && !string.IsNullOrEmpty(Current.RefreshToken);

        // ---------------- 设备码流程 ----------------

        public static async Task<DeviceCodeInfo> RequestDeviceCodeAsync(CancellationToken ct = default)
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = Scope
            };

            var json = await PostFormAsync(DeviceCodeUrl, form, ct).ConfigureAwait(false);

            var code = new DeviceCodeInfo
            {
                UserCode = J.Str(json, "user_code"),
                DeviceCode = J.Str(json, "device_code"),
                VerificationUri = J.Str(json, "verification_uri") ?? "https://microsoft.com/link",
                Message = J.Str(json, "message"),
                ExpiresIn = (int)J.Long(json, "expires_in"),
                Interval = Math.Max(2, (int)J.Long(json, "interval"))
            };

            if (string.IsNullOrEmpty(code.DeviceCode) || string.IsNullOrEmpty(code.UserCode))
                throw new MsaException("申请设备码失败：微软没有返回有效响应");

            Log.Info($"已申请设备码：{code.UserCode}，{code.VerificationUri}，有效期 {code.ExpiresIn} 秒");
            return code;
        }

        /// <summary>
        /// 轮询直到用户完成授权。每轮通过 progress 回报状态；用户取消时传 ct。
        /// </summary>
        public static async Task<MsaSession> WaitForLoginAsync(DeviceCodeInfo code,
            IProgress<string> progress = null, CancellationToken ct = default)
        {
            if (code == null) throw new ArgumentNullException(nameof(code));

            var interval = Math.Max(2, code.Interval);
            var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresIn > 0 ? code.ExpiresIn : 900);
            var waitingNotified = false;

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);

                var form = new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = code.DeviceCode
                };

                var (status, body) = await PostFormRawAsync(TokenUrl, form, ct).ConfigureAwait(false);

                if (status >= 200 && status < 300)
                {
                    var access = J.Str(body, "access_token");
                    var refresh = J.Str(body, "refresh_token");
                    var expiresIn = (int)J.Long(body, "expires_in");
                    if (string.IsNullOrEmpty(access)) throw new MsaException("授权成功但没有拿到 access_token");

                    progress?.Report("微软账号授权成功，正在换 Minecraft 令牌…");
                    return await CompleteLoginAsync(access, refresh, expiresIn, ct).ConfigureAwait(false);
                }

                var error = J.Str(body, "error") ?? "";
                switch (error)
                {
                    case "authorization_pending":
                        if (!waitingNotified)
                        {
                            progress?.Report("等待你在浏览器中确认…");
                            waitingNotified = true;
                        }
                        break;

                    case "slow_down":
                        interval += 2;
                        progress?.Report($"微软要求放慢轮询，间隔调整为 {interval} 秒");
                        break;

                    case "expired_token":
                        throw new MsaException("设备码已过期，请重新发起登录");

                    case "authorization_declined":
                        throw new MsaException("你在授权页面拒绝了登录");

                    case "bad_verification_code":
                        throw new MsaException("设备码无效，请重新发起登录");

                    default:
                        throw new MsaException("登录失败：" + (J.Str(body, "error_description") ?? error ?? "未知错误"));
                }
            }

            throw new MsaException("等待授权超时，请重新发起登录");
        }

        // ---------------- Xbox Live / XSTS / Minecraft ----------------

        private static async Task<MsaSession> CompleteLoginAsync(string accessToken, string refreshToken,
            int expiresIn, CancellationToken ct)
        {
            var (xblToken, uhs) = await XboxLiveAsync(accessToken, ct).ConfigureAwait(false);
            var xstsToken = await XstsAsync(xblToken, ct).ConfigureAwait(false);
            var (mcToken, mcExpires) = await MinecraftLoginAsync(uhs, xstsToken, ct).ConfigureAwait(false);
            var (uuid, name) = await MinecraftProfileAsync(mcToken, ct).ConfigureAwait(false);

            var session = new MsaSession
            {
                Name = name,
                Uuid = uuid,
                AccessToken = mcToken,
                RefreshToken = refreshToken,
                ExpiresAt = DateTime.UtcNow.AddSeconds(mcExpires > 0 ? mcExpires : expiresIn)
            };

            Save(session);
            Log.Info($"正版登录成功：{name}（{uuid}）");
            return session;
        }

        private static async Task<(string Token, string Uhs)> XboxLiveAsync(string msAccessToken, CancellationToken ct)
        {
            var payload = new JsonObject
            {
                ["Properties"] = new JsonObject
                {
                    ["AuthMethod"] = "RPS",
                    ["SiteName"] = "user.auth.xboxlive.com",
                    ["RpsTicket"] = "d=" + msAccessToken
                },
                ["RelyingParty"] = "http://auth.xboxlive.com",
                ["TokenType"] = "JWT"
            };

            var (status, json) = await PostJsonRawAsync(XblUrl, payload, ct).ConfigureAwait(false);
            if (status < 200 || status >= 300)
                throw new MsaException(ExplainHttpError(status, Describe(json, status), json, "Xbox Live 验证"));

            var token = J.Str(json, "Token");
            var xui = J.Arr(J.Obj(json, "DisplayClaims"), "xui");
            var uhs = xui != null && xui.Count > 0 ? J.Str(xui[0], "uhs") : null;

            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(uhs))
                throw new MsaException("Xbox Live 返回的数据不完整");

            return (token, uhs);
        }

        private static async Task<string> XstsAsync(string xblToken, CancellationToken ct)
        {
            var payload = new JsonObject
            {
                ["Properties"] = new JsonObject
                {
                    ["SandboxId"] = "RETAIL",
                    ["UserTokens"] = new JsonArray(xblToken)
                },
                ["RelyingParty"] = "rp://api.minecraftservices.com/",
                ["TokenType"] = "JWT"
            };

            var (status, json) = await PostJsonRawAsync(XstsUrl, payload, ct).ConfigureAwait(false);

            if (status < 200 || status >= 300)
            {
                // XSTS 失败通常带 XErr，能翻译成具体原因，比"401"有用得多
                var xerr = J.Long(json, "XErr");
                throw new MsaException(xerr switch
                {
                    2148916233 => "这个微软账号还没有 Xbox 档案，请先在 xbox.com 登录一次创建档案",
                    2148916235 => "该账号所在地区不支持 Xbox Live",
                    2148916236 or 2148916237 => "该账号需要完成年龄验证（地区限制）",
                    2148916238 => "该账号是未成年人账号，需要先加入家庭组",
                    _ => "XSTS 授权失败：" + Describe(json, status)
                });
            }

            var token = J.Str(json, "Token");
            if (string.IsNullOrEmpty(token)) throw new MsaException("XSTS 没有返回 token");
            return token;
        }

        private static async Task<(string Token, int ExpiresIn)> MinecraftLoginAsync(string uhs, string xstsToken,
            CancellationToken ct)
        {
            var payload = new JsonObject { ["identityToken"] = $"XBL3.0 x={uhs};{xstsToken}" };

            var (status, json) = await PostJsonRawAsync(McLoginUrl, payload, ct).ConfigureAwait(false);
            if (status < 200 || status >= 300)
                throw new MsaException(ExplainHttpError(status, Describe(json, status), json));

            var token = J.Str(json, "access_token");
            if (string.IsNullOrEmpty(token)) throw new MsaException("Minecraft 登录没有返回 access_token");

            return (token, (int)J.Long(json, "expires_in"));
        }

        private static async Task<(string Uuid, string Name)> MinecraftProfileAsync(string mcToken, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, McProfileUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + mcToken);

            using var resp = await Net.Client.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if ((int)resp.StatusCode == 404)
                throw new MsaException("这个账号没有购买 Minecraft（找不到游戏档案）");
            if (!resp.IsSuccessStatusCode)
                throw new MsaException($"读取游戏档案失败（HTTP {(int)resp.StatusCode}）");

            var json = JsonNode.Parse(text) as JsonObject;
            var id = J.Str(json, "id");
            var name = J.Str(json, "name");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                throw new MsaException("游戏档案数据不完整");

            return (FormatUuid(id), name);
        }

        // ---------------- 刷新 / 登出 ----------------

        /// <summary>用 refresh_token 换新的一套令牌（含游戏令牌）。</summary>
        public static async Task<MsaSession> RefreshAsync(MsaSession session, CancellationToken ct = default)
        {
            if (session == null || string.IsNullOrEmpty(session.RefreshToken))
                throw new MsaException("没有可用的 refresh_token，需要重新登录");

            var form = new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = session.RefreshToken,
                ["scope"] = Scope
            };

            var (status, body) = await PostFormRawAsync(TokenUrl, form, ct).ConfigureAwait(false);
            if (status < 200 || status >= 300)
                throw new MsaException("刷新登录已失效，请重新登录：" + Describe(body, status));

            var access = J.Str(body, "access_token");
            var refresh = J.Str(body, "refresh_token") ?? session.RefreshToken;
            var expiresIn = (int)J.Long(body, "expires_in");

            return await CompleteLoginAsync(access, refresh, expiresIn, ct).ConfigureAwait(false);
        }

        /// <summary>确保有可用令牌：过期就刷新；失败返回 null（调用方回落到离线）。</summary>
        public static async Task<MsaSession> EnsureFreshAsync(CancellationToken ct = default)
        {
            var session = Current;
            if (session == null) return null;
            if (session.HasToken && !session.Expired) return session;

            try
            {
                Log.Info("正版令牌已过期，正在刷新…");
                return await RefreshAsync(session, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("刷新正版令牌失败：" + ex.Message);
                return null;
            }
        }

        public static void Logout()
        {
            lock (Gate)
            {
                _current = null;
                _loaded = true;
            }

            try
            {
                if (File.Exists(StoreFile)) File.Delete(StoreFile);
            }
            catch (Exception ex)
            {
                Log.Warn("删除账号文件失败：" + ex.Message);
            }

            Log.Info("已退出正版账号");
        }

        // ---------------- 持久化 ----------------

        private static MsaSession LoadSession()
        {
            try
            {
                if (!File.Exists(StoreFile)) return null;
                var node = JsonNode.Parse(File.ReadAllText(StoreFile)) as JsonObject;
                var msa = J.Obj(node, "Msa");
                if (msa == null) return null;

                var session = new MsaSession
                {
                    Name = J.Str(msa, "Name"),
                    Uuid = J.Str(msa, "Uuid"),
                    AccessToken = J.Str(msa, "AccessToken"),
                    RefreshToken = J.Str(msa, "RefreshToken"),
                    ExpiresAt = DateTime.TryParse(J.Str(msa, "ExpiresAt"), out var t) ? t.ToUniversalTime() : DateTime.MinValue
                };

                return string.IsNullOrEmpty(session.RefreshToken) ? null : session;
            }
            catch (Exception ex)
            {
                Log.Warn("读取账号文件失败：" + ex.Message);
                return null;
            }
        }

        private static void Save(MsaSession session)
        {
            lock (Gate)
            {
                _current = session;
                _loaded = true;
            }

            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                var root = new JsonObject
                {
                    ["Msa"] = new JsonObject
                    {
                        ["Name"] = session.Name,
                        ["Uuid"] = session.Uuid,
                        ["AccessToken"] = session.AccessToken,
                        ["RefreshToken"] = session.RefreshToken,
                        ["ExpiresAt"] = session.ExpiresAt.ToString("o")
                    }
                };

                File.WriteAllText(StoreFile, root.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }));
            }
            catch (Exception ex)
            {
                Log.Warn("保存账号文件失败：" + ex.Message);
            }
        }

        // ---------------- HTTP 辅助 ----------------

        private static async Task<JsonObject> PostFormAsync(string url, Dictionary<string, string> form,
            CancellationToken ct)
        {
            var (status, body) = await PostFormRawAsync(url, form, ct).ConfigureAwait(false);
            if (status < 200 || status >= 300)
                throw new MsaException("请求失败：" + Describe(body, status));
            return body;
        }

        private static async Task<(int Status, JsonObject Body)> PostFormRawAsync(string url,
            Dictionary<string, string> form, CancellationToken ct)
        {
            using var content = new FormUrlEncodedContent(form);
            using var resp = await Net.Client.PostAsync(url, content, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ((int)resp.StatusCode, TryParse(text));
        }

        private static async Task<(int Status, JsonObject Body)> PostJsonRawAsync(string url, JsonObject payload,
            CancellationToken ct)
        {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await Net.Client.PostAsync(url, content, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ((int)resp.StatusCode, TryParse(text));
        }

        private static JsonObject TryParse(string text)
        {
            try { return JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject; }
            catch { return new JsonObject(); }
        }

        /// <summary>
        /// 把 HTTP 状态 + 英文错误原文翻成用户能看懂的话。
        ///
        /// 最要紧的是 403 "Invalid app registration"：第三方启动器必须单独申请 Minecraft API 白名单，
        /// 官方**没有状态查询页**，只能靠"重试登录是否还卡在这一步"判断。实测该响应只在
        /// XSTS 令牌有效时才出现（伪造 token 会先拿到 401），所以没有更轻量的探测方式。
        /// </summary>
        internal static string ExplainHttpError(int status, string desc, JsonObject body, string context = "Minecraft 登录")
        {
            var raw = (desc ?? "") + " " + (body?.ToJsonString() ?? "");
            var isAppRegistration =
                raw.IndexOf("app registration", StringComparison.OrdinalIgnoreCase) >= 0 ||
                raw.IndexOf("AppRegInfo", StringComparison.OrdinalIgnoreCase) >= 0;

            if (status == 403 && isAppRegistration)
                return "Mojang 还没有放行这个启动器（403 Invalid app registration）。\n" +
                       "第三方启动器必须单独申请 Minecraft API 白名单。官方没有状态查询页，\n" +
                       "提交后一般等一周左右，再次登录不报这个错就是通过了。\n" +
                       "申请入口：https://aka.ms/mce-reviewappid\n" +
                       "等待期间可以先用「离线登录」正常游玩。";

            switch (status)
            {
                case 401:
                    return context + "凭据无效或已过期（401），请重新登录。";
                case 403:
                    return context + "被拒绝（403）：" + desc +
                           "\n如果反复出现，可能是账号权限或地区限制。";
                case 429:
                    return "请求太频繁（429），请等几分钟再试。";
            }

            if (status >= 500)
                return $"Mojang 服务端暂时不可用（HTTP {status}），稍后重试即可。";

            return (string.IsNullOrEmpty(desc) ? $"HTTP {status}" : desc);
        }

        private static string Describe(JsonObject body, int status)
        {
            var desc = J.Str(body, "error_description") ?? J.Str(body, "errorMessage") ?? J.Str(body, "error");
            return string.IsNullOrEmpty(desc) ? $"HTTP {status}" : desc;
        }

        /// <summary>Minecraft 返回的 UUID 没有连字符，补成标准格式。</summary>
        private static string FormatUuid(string raw)
        {
            var s = (raw ?? "").Replace("-", "");
            if (s.Length != 32) return raw;
            return s.Substring(0, 8) + "-" + s.Substring(8, 4) + "-" + s.Substring(12, 4) +
                   "-" + s.Substring(16, 4) + "-" + s.Substring(20, 4);
        }
    }
}
