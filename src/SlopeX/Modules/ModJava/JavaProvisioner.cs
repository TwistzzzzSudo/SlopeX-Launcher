using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModJava
{
    /// <summary>Java 运行时的下载安装进度。</summary>
    public sealed class JavaInstallProgress
    {
        public string Stage { get; set; } = "";
        public string Source { get; set; } = "";
        public long Done { get; set; }
        public long Total { get; set; }
        public double Percent { get; set; }
        public int Major { get; set; }

        public string Text
        {
            get
            {
                var head = string.IsNullOrEmpty(Stage) ? "准备中" : Stage;
                if (Major > 0) head = $"Java {Major} · {head}";
                if (Source.Length > 0 && Stage == "正在下载")
                    head += $"（{Source}）";
                if (Percent > 0)
                    return $"{head}  {Percent:0}%  {Format.Bytes(Done)} / {Format.Bytes(Total)}";
                return head;
            }
        }
    }

    /// <summary>一个可用的安装包。</summary>
    public sealed class JavaPackage
    {
        public string Source { get; set; }
        public string Url { get; set; }
        public string FileName { get; set; }
    }

    /// <summary>
    /// 缺失 Java 时自动下载安装（对应 PCL2 的 Java 自动安装能力）。
    ///
    /// 只使用国内镜像，按速度与稳定性依次尝试：
    ///   1. 清华大学 TUNA（Adoptium / Temurin，优先 JRE，体积约为 JDK 的 1/4）
    ///   2. 中科大 USTC（同为 Adoptium 反向镜像）
    ///   3. 华为云（Oracle OpenJDK 构建）
    ///   4. Adoptium 官方 API（境外，仅作最后兜底）
    ///
    /// 安装到 %APPDATA%\SlopeX\runtime\java-&lt;主版本&gt;，供 JavaFinder 统一扫描。
    /// </summary>
    public static class JavaProvisioner
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 8
            };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SlopeX/1.0 (Java Provisioner)");
            return c;
        }

        private static string ArchTag =>
            JavaFinder.ArchOfCurrentProcess == "arm64" ? "aarch64" : "x64";

        private sealed class Provider
        {
            public string Name { get; set; }
            public Func<int, CancellationToken, Task<JavaPackage>> Resolve { get; set; }
        }

        private static readonly List<Provider> Providers = new List<Provider>
        {
            new Provider { Name = "清华镜像", Resolve = (m, ct) => ResolveAdoptiumAsync("https://mirrors.tuna.tsinghua.edu.cn", "清华镜像", m, ct) },
            new Provider { Name = "中科大镜像", Resolve = (m, ct) => ResolveAdoptiumAsync("https://mirrors.ustc.edu.cn/adoptium", "中科大镜像", m, ct) },
            new Provider { Name = "华为云镜像", Resolve = (m, ct) => ResolveHuaweiAsync("https://mirrors.huaweicloud.com/openjdk", "华为云镜像", m, ct) },
            new Provider { Name = "Adoptium 官方", Resolve = ResolveAdoptiumApiAsync }
        };

        /// <summary>
        /// 确保存在能满足 <paramref name="required"/> 主版本的 Java。
        /// 已安装则直接返回；否则下载安装后返回。
        /// </summary>
        public static async Task<JavaInfo> EnsureAsync(
            int required,
            IProgress<JavaInstallProgress> progress = null,
            CancellationToken ct = default)
        {
            if (required <= 0) required = 17;

            var hit = JavaFinder.FindBest(required, rescan: true);
            if (hit != null)
            {
                Log.Info($"已有可用 Java {hit.Major}（要求 {required}）：{hit.Path}");
                return hit;
            }

            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 排队期间可能已被别的流程装好了
                hit = JavaFinder.FindBest(required, rescan: true);
                if (hit != null) return hit;

                Log.Info($"缺少 Java {required}，开始自动下载安装");
                var target = Paths.JavaDir(required);

                Exception last = null;
                foreach (var provider in Providers)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        progress?.Report(new JavaInstallProgress { Major = required, Stage = "解析下载地址", Source = provider.Name });
                        var pkg = await provider.Resolve(required, ct).ConfigureAwait(false);
                        if (pkg == null)
                        {
                            Log.Warn($"{provider.Name} 没有找到 Java {required} 的安装包，换下一个源");
                            continue;
                        }

                        Log.Info($"{provider.Name} 命中：{pkg.Url}");
                        var installed = await DownloadAndInstallAsync(pkg, required, progress, ct).ConfigureAwait(false);
                        if (installed == null)
                        {
                            Log.Warn($"{provider.Name} 安装包校验失败，换下一个源");
                            continue;
                        }

                        JavaFinder.All(true);
                        var info = JavaFinder.FindBest(required, rescan: true);
                        if (info != null)
                        {
                            Log.Info($"Java {info.Major} 安装完成：{info.Path}");
                            return info;
                        }

                        Log.Warn($"{provider.Name} 安装后仍未被识别，换下一个源");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        Log.Warn($"{provider.Name} 安装失败：{ex.Message}");
                    }
                }

                throw new InvalidOperationException(
                    $"无法自动安装 Java {required}，所有镜像均失败" +
                    (last != null ? $"（最后错误：{last.Message}）" : "") +
                    "。可到「设置」中手动指定 Java 路径。", last);
            }
            finally
            {
                Gate.Release();
            }
        }

        // ---------------- 镜像解析 ----------------

        /// <summary>Adoptium 反向镜像（清华 / 中科大）：目录里直接列 zip 文件。</summary>
        private static async Task<JavaPackage> ResolveAdoptiumAsync(
            string host, string label, int major, CancellationToken ct)
        {
            var arch = ArchTag;

            // 优先 JRE（约 50 MB），没有就退 JDK（约 180 MB）
            foreach (var kind in new[] { "jre", "jdk" })
            {
                var dirUrl = $"{host}/Adoptium/{major}/{kind}/{arch}/windows/";
                string html;
                try { html = await Http.GetStringAsync(dirUrl, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { continue; }

                var pattern = $@"OpenJDK\d+U-{kind}_{arch}_windows_hotspot_[0-9._]+\.zip";
                var names = Regex.Matches(html, pattern)
                    .Select(m => m.Value)
                    .Distinct()
                    .OrderByDescending(VersionSortKey)
                    .ToList();
                if (names.Count == 0) continue;

                return new JavaPackage
                {
                    Source = label,
                    Url = dirUrl + names[0],
                    FileName = names[0]
                };
            }

            return null;
        }

        /// <summary>华为云：/openjdk/ 下按版本分目录，包名形如 openjdk-25_windows-x64_bin.zip。</summary>
        private static async Task<JavaPackage> ResolveHuaweiAsync(
            string host, string label, int major, CancellationToken ct)
        {
            if (JavaFinder.ArchOfCurrentProcess == "arm64") return null; // 该镜像只有 x64

            string index;
            try { index = await Http.GetStringAsync(host + "/", ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { return null; }

            var dirs = Regex.Matches(index, @"href=""(\d[\d.]*)/""")
                .Select(m => m.Groups[1].Value)
                .Where(d => d == major.ToString() || d.StartsWith(major + "."))
                .Distinct()
                .OrderByDescending(VersionSortKey)
                .ToList();

            foreach (var dir in dirs)
            {
                string sub;
                try { sub = await Http.GetStringAsync($"{host}/{dir}/", ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { continue; }

                var zips = Regex.Matches(sub, @"openjdk-[\d._]+_windows-x64_bin\.zip")
                    .Select(m => m.Value)
                    .Distinct()
                    .OrderByDescending(VersionSortKey)
                    .ToList();
                if (zips.Count == 0) continue;

                return new JavaPackage
                {
                    Source = label,
                    Url = $"{host}/{dir}/{zips[0]}",
                    FileName = zips[0]
                };
            }

            return null;
        }

        /// <summary>Adoptium 官方 API（境外，兜底用）。</summary>
        private static async Task<JavaPackage> ResolveAdoptiumApiAsync(int major, CancellationToken ct)
        {
            var arch = ArchTag;
            foreach (var kind in new[] { "jre", "jdk" })
            {
                var api = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot" +
                          $"?architecture={arch}&image_type={kind}&os=windows&vendor=eclipse";
                try
                {
                    var json = await Http.GetStringAsync(api, ct).ConfigureAwait(false);
                    var m = Regex.Match(json, @"""link""\s*:\s*""([^""]+\.zip)""");
                    if (!m.Success) continue;

                    var url = m.Groups[1].Value.Replace("\\/", "/");
                    return new JavaPackage
                    {
                        Source = "Adoptium 官方",
                        Url = url,
                        FileName = Path.GetFileName(new Uri(url).AbsolutePath)
                    };
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 换下一个类型 */ }
            }
            return null;
        }

        /// <summary>把 25.0.4.1_1 这类版本号变成可比较的排序键。</summary>
        private static string VersionSortKey(string name)
        {
            var m = Regex.Match(name, @"hotspot_([0-9._]+)\.zip|openjdk-([\d._]+)_windows");
            var raw = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : "0";
            var parts = Regex.Matches(raw, @"\d+").Select(x => int.Parse(x.Value)).ToArray();
            return string.Join(".", parts.Select(p => p.ToString("D5")));
        }

        // ---------------- 下载与安装 ----------------

        private static async Task<JavaInfo> DownloadAndInstallAsync(
            JavaPackage pkg, int major, IProgress<JavaInstallProgress> progress, CancellationToken ct)
        {
            var zip = Path.Combine(Paths.CacheDir, pkg.FileName);
            var target = Paths.JavaDir(major);

            try
            {
                await DownloadAsync(pkg, zip, major, progress, ct).ConfigureAwait(false);

                var info = ExtractAndVerify(zip, major, target, progress);
                return info;
            }
            finally
            {
                try { if (File.Exists(zip)) File.Delete(zip); } catch { }
            }
        }

        private static async Task DownloadAsync(
            JavaPackage pkg, string dest, int major, IProgress<JavaInstallProgress> progress, CancellationToken ct)
        {
            using var resp = await Http.GetAsync(pkg.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long got = 0;
            int read;
            var sw = Stopwatch.StartNew();
            var lastTick = 0L;

            while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                got += read;

                // 进度回调限流，避免刷爆 UI 线程
                if (sw.ElapsedMilliseconds - lastTick >= 120)
                {
                    lastTick = sw.ElapsedMilliseconds;
                    progress?.Report(new JavaInstallProgress
                    {
                        Major = major,
                        Stage = "正在下载",
                        Source = pkg.Source,
                        Done = got,
                        Total = total > 0 ? total : 0,
                        Percent = total > 0 ? got * 100.0 / total : 0
                    });
                }
            }

            if (total > 0 && got != total)
                throw new IOException($"下载不完整（{got}/{total} 字节）");

            Log.Info($"Java 安装包下载完成：{pkg.FileName}，{Format.Bytes(got)}，耗时 {sw.Elapsed.TotalSeconds:0.0} 秒");
        }

        private static JavaInfo ExtractAndVerify(
            string zip, int major, string target, IProgress<JavaInstallProgress> progress)
        {
            progress?.Report(new JavaInstallProgress { Major = major, Stage = "正在解压" });

            var temp = target + ".tmp";
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
            Directory.CreateDirectory(temp);

            try
            {
                ZipFile.ExtractToDirectory(zip, temp, true);

                var home = FindJavaHome(temp);
                if (home == null) throw new IOException("安装包里没有找到 bin/java.exe");

                // 压缩包内通常自带一层版本目录，摊平到 runtime\java-<major>
                if (Directory.Exists(target))
                {
                    try { Directory.Delete(target, true); }
                    catch (Exception ex) { throw new IOException("无法覆盖已存在的 Java 目录：" + ex.Message, ex); }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? Paths.RuntimeDir);

                if (string.Equals(home, temp, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(temp, target);
                }
                else
                {
                    Directory.Move(home, target);
                }

                progress?.Report(new JavaInstallProgress { Major = major, Stage = "正在校验" });

                var exe = Path.Combine(target, "bin", "java.exe");
                if (!File.Exists(exe)) throw new IOException("解压后缺少 bin/java.exe");

                var info = JavaFinder.Probe(exe);
                if (info == null) throw new IOException("安装后的 Java 无法运行");
                if (info.Major < major)
                    throw new IOException($"安装包版本不符（需要 Java {major}，实际 {info.Major}）");

                return info;
            }
            finally
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
            }
        }

        private static string FindJavaHome(string root)
        {
            var direct = Path.Combine(root, "bin", "java.exe");
            if (File.Exists(direct)) return root;

            foreach (var dir in Directory.GetDirectories(root))
            {
                var exe = Path.Combine(dir, "bin", "java.exe");
                if (File.Exists(exe)) return dir;
            }
            return null;
        }
    }
}
