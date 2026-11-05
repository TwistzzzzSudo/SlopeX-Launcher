using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModNet
{
    /// <summary>一次更新检查的结果。只在 <c>Version</c> 比本机新时才会被返回。</summary>
    public sealed class UpdateInfo
    {
        /// <summary>远端版本号，例如 <c>1.0.1</c>。</summary>
        public string Version { get; set; } = "";

        /// <summary>新版本 exe 的直链。</summary>
        public string Url { get; set; } = "";

        /// <summary>exe 的 SHA-256（小写十六进制）。为空则不做校验——只允许清单没写时出现。</summary>
        public string Sha256 { get; set; } = "";

        /// <summary>更新说明，直接显示在弹窗里。</summary>
        public string Notes { get; set; } = "";

        /// <summary>允许运行的最低版本。当前版本低于它时必须更新，不能「稍后」。</summary>
        public string MinVersion { get; set; } = "";

        /// <summary>文件大小（字节），0 表示未知。</summary>
        public long Size { get; set; }

        /// <summary>当前版本低于 <see cref="MinVersion"/>，属于必须更新。</summary>
        public bool Forced { get; set; }

        /// <summary>弹窗标题用的一行字。</summary>
        public string Headline => "v" + Version;
    }

    /// <summary>
    /// 启动器自更新。
    ///
    /// 单文件 exe 没有升级通道，换了新版只能让用户手动替换文件。这里做三件事：
    /// 拉清单 → 比对版本 → 下载并校验；真正覆盖 exe 由 <see cref="WriteScript"/> 生成的
    /// 批处理在进程退出后完成（Windows 不允许覆盖正在运行的程序）。
    ///
    /// 清单默认放在 GitHub Releases 的 <c>latest.json</c> 资产上：
    /// <code>
    /// { "version": "1.0.1",
    ///   "url": "https://.../SlopeX.exe",
    ///   "sha256": "…",
    ///   "minVersion": "1.0.0",
    ///   "notes": "…" }
    /// </code>
    /// 清单还没发布时退回 GitHub API 读最新 Release——只要发了 Release 就能收到更新，
    /// 代价是拿不到 <c>minVersion</c>（按不强制处理）。
    /// </summary>
    public static class Updater
    {
        /// <summary>默认清单地址。仓库公开，资产名固定，发版时上传同名文件即可。</summary>
        public const string DefaultSource =
            "https://github.com/TwistzzzzSudo/SlopeX-Launcher/releases/latest/download/latest.json";

        private const string ReleaseApi =
            "https://api.github.com/repos/TwistzzzzSudo/SlopeX-Launcher/releases/latest";

        /// <summary>
        /// 生效的清单地址：配置里的 <see cref="Config.UpdateSource"/> 优先，
        /// 空表示用 <see cref="DefaultSource"/>，<c>off</c> 表示彻底关掉检查。
        /// </summary>
        public static string Source
        {
            get
            {
                var custom = (Config.Current.UpdateSource ?? "").Trim();
                if (custom.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                    custom.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                    custom == "0")
                    return "";
                return custom.Length == 0 ? DefaultSource : custom;
            }
        }

        /// <summary>下载落点：留在缓存目录，替换成功后下次启动会自然被覆盖。</summary>
        public static string UpdateDir => Path.Combine(Paths.CacheDir, "update");

        // ---------------- 检查 ----------------

        /// <summary>查一次更新；没有新版本、已关闭检查都返回 null。更新源不可达时抛异常。</summary>
        public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
        {
            var source = Source;
            if (string.IsNullOrEmpty(source)) return null;

            UpdateInfo info = null;

            // 404 不算故障：清单没发、Release 没发都是正常状态，按"没有更新"处理。
            // 真正的网络错误留到最后——两边都拿不到时向上抛，手动检查才能说出"检查失败"，
            // 而不是把一次连不上的检查伪装成"已是最新"。
            Exception error = null;

            try
            {
                var text = await Net.GetStringAsync(source, ct).ConfigureAwait(false);
                info = ParseManifest(text);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                Log.Info("更新清单还没发布（404），改查 Releases");
            }
            catch (Exception ex)
            {
                error = ex;
                Log.Warn("更新清单读取失败：" + ex.Message);
            }

            if (info == null)
            {
                try
                {
                    var json = await Net.GetStringAsync(ReleaseApi, ct).ConfigureAwait(false);
                    info = ParseRelease(json);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    Log.Info("仓库还没有发布过 Release");
                }
                catch (Exception ex)
                {
                    error = error ?? ex;
                    Log.Warn("GitHub Releases 查询失败：" + ex.Message);
                }
            }

            if (info == null)
            {
                if (error != null) throw new InvalidOperationException("更新源不可达：" + error.Message, error);
                return null;
            }

            if (string.IsNullOrWhiteSpace(info.Url))
            {
                Log.Warn("更新清单里没有可用的下载地址，已忽略");
                return null;
            }

            // 远端不比本机新就算没有更新——清单写错、回退旧版都不会把用户往回推
            if (Compare(info.Version, AppInfo.Version) <= 0) return null;

            info.Forced = !string.IsNullOrWhiteSpace(info.MinVersion) &&
                          Compare(AppInfo.Version, info.MinVersion) < 0;

            Log.Info($"发现新版本 {info.Version}（当前 {AppInfo.Version}" + (info.Forced ? "，低于最低版本 " + info.MinVersion : "") + "）");
            return info;
        }

        /// <summary>解析 <c>latest.json</c>。字段缺版本号或地址就当作无效。</summary>
        private static UpdateInfo ParseManifest(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!(JsonNode.Parse(text) is JsonObject root)) return null;

            var version = Str(root, "version") ?? Str(root, "tag_name") ?? "";
            version = version.TrimStart('v', 'V');

            var url = Str(root, "url") ?? Str(root, "download") ?? "";
            if (version.Length == 0 || url.Length == 0) return null;

            return new UpdateInfo
            {
                Version = version,
                Url = url,
                Sha256 = (Str(root, "sha256") ?? "").Replace("sha256:", "", StringComparison.OrdinalIgnoreCase),
                MinVersion = (Str(root, "minVersion") ?? Str(root, "min_version") ?? "").TrimStart('v', 'V'),
                Notes = Str(root, "notes") ?? Str(root, "body") ?? "",
                Size = Num(root, "size")
            };
        }

        /// <summary>解析 GitHub Releases API 的响应：取第一个 <c>.exe</c> 资产。</summary>
        private static UpdateInfo ParseRelease(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!(JsonNode.Parse(text) is JsonObject root)) return null;

            var version = (Str(root, "tag_name") ?? "").TrimStart('v', 'V');
            if (version.Length == 0) return null;

            var assets = root["assets"] as JsonArray;
            if (assets == null) return null;

            foreach (var node in assets.OfType<JsonObject>())
            {
                var name = Str(node, "name") ?? "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                var url = Str(node, "browser_download_url") ?? "";
                if (url.Length == 0) continue;

                // GitHub 会给新上传的资产附 sha256 摘要，直接拿来当校验值
                var digest = Str(node, "digest") ?? "";

                return new UpdateInfo
                {
                    Version = version,
                    Url = url,
                    Sha256 = digest.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase),
                    Notes = Str(root, "body") ?? "",
                    Size = Num(node, "size")
                };
            }

            return null;
        }

        // ---------------- 下载 ----------------

        /// <summary>
        /// 把新版本下载到缓存目录并校验 SHA-256，返回 exe 路径。
        /// 之前已经下过且校验通过的文件直接复用，不重复下 160 MB。
        /// </summary>
        public static async Task<string> DownloadAsync(UpdateInfo info, Action<DownloadProgress> progress,
            CancellationToken ct = default)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));

            Directory.CreateDirectory(UpdateDir);
            var target = Path.Combine(UpdateDir, "SlopeX-" + Safe(info.Version) + ".exe");

            if (File.Exists(target) && Sha256Matches(target, info.Sha256))
            {
                Log.Info("新版本已经在缓存里，跳过下载");
                return target;
            }

            Log.Info($"开始下载新版本 {info.Version}（{(info.Size > 0 ? Format.Bytes(info.Size) : "大小未知")}）");

            await Downloader.DownloadAsync(info.Url, target, null, info.Size, ct, 600000, progress)
                .ConfigureAwait(false);

            if (!Sha256Matches(target, info.Sha256))
            {
                var actual = Sha256Of(target);
                TryDelete(target);
                throw new InvalidDataException(
                    $"新版本校验失败（期望 {info.Sha256}，实际 {actual}），已删除下载的文件");
            }

            Log.Info("新版本下载完成：" + target);
            return target;
        }

        private static bool Sha256Matches(string path, string expected)
        {
            // 清单没给校验值时只能放行；给了就必须对上
            if (string.IsNullOrWhiteSpace(expected)) return true;
            return string.Equals(Sha256Of(path), expected, StringComparison.OrdinalIgnoreCase);
        }

        public static string Sha256Of(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            var hash = sha.ComputeHash(fs);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // ---------------- 替换 ----------------

        /// <summary>
        /// exe 所在目录能否写入。装在 Program Files 之类的位置时替换会失败，
        /// 与其生成一个必定失败的脚本，不如提前告诉用户手动替换。
        /// </summary>
        public static bool CanReplaceSelf()
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir)) return false;

            var probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("n").Substring(0, 8));
            try
            {
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 生成"等当前进程退出后覆盖 exe 并重启"的批处理，返回脚本路径。
        ///
        /// 为什么要脚本：Windows 不允许覆盖正在运行的程序。这里把活交给一个独立进程，
        /// 由它反复尝试 <c>move /y</c>（进程刚退出时文件仍可能短暂占用），成功后启动新版本并自删。
        /// 重试上限 60 秒：真的替换不了就放弃，至少不会留下一个死循环的进程。
        /// </summary>
        public static string WriteScript(string newExePath, string targetExePath = null)
        {
            if (string.IsNullOrWhiteSpace(newExePath)) throw new ArgumentNullException(nameof(newExePath));

            var target = targetExePath ?? Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(target)) throw new InvalidOperationException("取不到当前 exe 路径");

            // 脚本的默认工作目录是自己的目录，相对路径会解析错位——统一转绝对
            var source = Path.GetFullPath(newExePath);
            target = Path.GetFullPath(target);

            Directory.CreateDirectory(UpdateDir);

            // 上一次没跑完的脚本（进程被强杀时会留下）先清掉，免得越积越多
            try
            {
                foreach (var old in Directory.GetFiles(UpdateDir, "apply-*.cmd"))
                    File.Delete(old);
            }
            catch { /* 正在被占用就留着 */ }

            var script = Path.Combine(UpdateDir, "apply-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".cmd");

            var text = new StringBuilder();
            text.AppendLine("@echo off");
            text.AppendLine("setlocal");
            text.AppendLine("set /a try=0");
            text.AppendLine(":retry");
            // move 不覆盖只读文件，先清属性；目标不存在时 attrib 会报错，用 >nul 吞掉
            text.AppendLine("attrib -r \"" + target + "\" >nul 2>&1");
            text.AppendLine("move /y \"" + source + "\" \"" + target + "\" >nul 2>&1");
            text.AppendLine("if not errorlevel 1 goto done");
            text.AppendLine("set /a try+=1");
            text.AppendLine("if %try% geq 60 goto fail");
            text.AppendLine("timeout /t 1 /nobreak >nul");
            text.AppendLine("goto retry");
            text.AppendLine(":done");
            text.AppendLine("start \"\" \"" + target + "\"");
            text.AppendLine(":fail");
            text.AppendLine("del \"%~f0\"");
            text.AppendLine("exit /b");

            File.WriteAllText(script, text.ToString(), Encoding.Default);
            return script;
        }

        /// <summary>启动替换脚本；调用方随后应立即退出进程。返回 false 表示没能启动。</summary>
        public static bool RunScript(string scriptPath)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c \"" + scriptPath + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? "."
                };
                Process.Start(psi);
                Log.Info("更新脚本已启动，退出后会自动替换并重启");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("启动更新脚本失败", ex);
                return false;
            }
        }

        // ---------------- 版本比较 ----------------

        /// <summary>
        /// 比较两个版本号：返回 &lt;0 表示 a 比 b 旧。
        /// 只认前四段数字，忽略 <c>v</c> 前缀与 <c>+build</c> 后缀；
        /// 数字相同时带 <c>-beta</c> 这类预发布标记的算更旧（<c>1.1.0-beta &lt; 1.1.0</c>）。
        /// </summary>
        public static int Compare(string a, string b)
        {
            var (na, pa) = Split(a);
            var (nb, pb) = Split(b);

            for (var i = 0; i < Math.Max(na.Length, nb.Length); i++)
            {
                var x = i < na.Length ? na[i] : 0;
                var y = i < nb.Length ? nb[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }

            if (pa == pb) return 0;
            if (!pa) return 1;    // 正式的比预发布的新
            return -1;
        }

        private static (int[] numbers, bool prerelease) Split(string version)
        {
            var s = (version ?? "").Trim().TrimStart('v', 'V');
            var prerelease = s.Contains('-');

            var cut = s.IndexOfAny(new[] { '-', '+' });
            if (cut >= 0) s = s.Substring(0, cut);

            var parts = s.Split('.');
            var numbers = new List<int>();
            foreach (var p in parts)
            {
                var digits = new string(p.TakeWhile(char.IsDigit).ToArray());
                if (digits.Length == 0) break;
                numbers.Add(int.TryParse(digits, out var n) ? n : 0);
            }

            return (numbers.ToArray(), prerelease);
        }

        // ---------------- 小工具 ----------------

        private static string Str(JsonObject o, string key)
        {
            var node = o[key];
            if (node == null) return null;
            try
            {
                return node.GetValue<string>();
            }
            catch
            {
                return node.ToString();
            }
        }

        private static long Num(JsonObject o, string key)
        {
            var node = o[key];
            if (node == null) return 0;
            return long.TryParse(node.ToString(), out var n) ? n : 0;
        }

        private static string Safe(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => bad.Contains(c) ? '_' : c).ToArray();
            return new string(chars);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
        }
    }
}
