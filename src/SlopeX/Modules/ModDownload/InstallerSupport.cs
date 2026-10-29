using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModJava;
using SlopeX.Modules.ModLaunch;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Modules.ModNet;
using System.Text.Json.Nodes;

namespace SlopeX.Modules.ModDownload
{
    /// <summary>
    /// Forge / NeoForge 共用的安装器执行逻辑。
    ///
    /// 两者走的是同一条路线（下载官方 installer → 用合适的 Java 无头执行 <c>--installClient</c>），
    /// 差别只在"版本从哪来、installer 叫什么名字"，所以进程管理、Java 选择、
    /// launcher_profiles 兜底、失败诊断这些都放在这里。
    /// </summary>
    internal static class InstallerSupport
    {
        /// <summary>整个安装过程的上限。装 processors 要下载并改写几百 MB，给足余量。</summary>
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(20);

        /// <summary>
        /// 依次尝试若干来源下载 installer，成功即返回本地路径；全失败抛异常。
        /// 同名文件已存在且像样（&gt; 100 KB）时直接复用。
        /// </summary>
        public static async Task<string> DownloadInstallerAsync(IReadOnlyList<string> urls, string cacheSubdir,
            string fileName, InstallTask task, CancellationToken ct)
        {
            var dir = Path.Combine(Paths.CacheDir, cacheSubdir);
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, fileName);

            if (File.Exists(path))
            {
                var len = new FileInfo(path).Length;
                if (len > 100 * 1024)
                {
                    Log.Info($"复用已下载的安装器（{Format.Bytes(len)}）：{fileName}");
                    task.SetStage("复用已缓存的安装器");
                    return path;
                }

                File.Delete(path); // 下了一半的残缺文件
            }

            task.SetStage("下载安装器");

            Exception lastError = null;

            foreach (var url in urls)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var queue = new DownloadQueue();
                    queue.Enqueue(new DownloadJob { Name = fileName, Url = url, Path = path });

                    queue.Changed += () => task.SetProgress(queue.DoneBytes,
                        Math.Max(queue.TotalBytes, queue.DoneBytes), queue.Speed);

                    var failed = await queue.RunAsync(1, ct).ConfigureAwait(false);
                    if (failed == 0 && File.Exists(path) && new FileInfo(path).Length > 100 * 1024)
                    {
                        Log.Info($"安装器已下载：{fileName}（{Format.Bytes(new FileInfo(path).Length)}）来源 {url}");
                        return path;
                    }

                    lastError = new IOException(queue.Jobs.FirstOrDefault()?.Error ?? "文件不完整");
                    Log.Warn($"安装器下载失败，换下一个来源：{url}（{lastError.Message}）");

                    if (File.Exists(path)) File.Delete(path);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Log.Warn($"安装器下载失败，换下一个来源：{url}（{ex.Message}）");
                    if (File.Exists(path)) File.Delete(path);
                }
            }

            throw new IOException("所有来源都下载不到安装器。" +
                                  (lastError != null ? "最后一个错误：" + lastError.Message : "") +
                                  $"\n可手动下载后放到 {dir}");
        }

        /// <summary>取运行 installer 所需的 Java；没有就按既有策略自动装一个。</summary>
        public static async Task<JavaInfo> EnsureJavaAsync(string gameDir, string mcVersion,
            InstallTask task, CancellationToken ct)
        {
            var major = 17;
            try
            {
                var rv = VersionManager.Resolve(gameDir, mcVersion);
                major = JavaFinder.RequiredMajor(mcVersion, rv.JavaMajor);
            }
            catch (Exception ex)
            {
                Log.Warn($"读取 {mcVersion} 的 javaVersion 失败，按版本号推断：{ex.Message}");
                major = JavaFinder.RequiredMajor(mcVersion, 0);
            }

            var java = JavaFinder.FindBest(major);
            if (java != null)
            {
                Log.Info($"安装器使用 Java {java.Major}：{java.Path}");
                return java;
            }

            if (!Config.Current.AutoInstallJava)
                throw new InvalidOperationException(
                    $"运行安装器需要 Java {major}，本机未检测到，且设置里已关闭自动安装。");

            task.SetStage($"需要 Java {major}，正在下载运行时");
            Log.Info($"未找到 Java {major}，自动下载安装");
            java = await JavaProvisioner.EnsureAsync(major, null, ct).ConfigureAwait(false);

            if (java == null)
                throw new InvalidOperationException($"自动安装 Java {major} 失败，无法运行安装器。");

            return java;
        }

        /// <summary>
        /// installer 会往目标目录写 launcher_profiles.json（记录安装配置）。
        /// 我们的 .minecraft 可能没有这个文件，缺了 installer 会直接报错退出。
        /// </summary>
        public static void EnsureLauncherProfiles(string gameDir)
        {
            var path = Path.Combine(gameDir, "launcher_profiles.json");
            if (File.Exists(path)) return;

            const string minimal =
                "{\n" +
                "  \"profiles\": {},\n" +
                "  \"settings\": {},\n" +
                "  \"version\": 3\n" +
                "}\n";

            File.WriteAllText(path, minimal);
            Log.Info("已创建最小 launcher_profiles.json（安装器要求存在该文件）");
        }

        /// <summary>
        /// 预取 installer 的 <c>install_profile.json</c> 里声明的全部库。
        ///
        /// 为什么必须自己做：installer 内部对 maven 地址是**写死**的（<c>--mirror</c> 只覆盖一部分），
        /// 而 NeoForge 的 maven 域名在国内常见 DNS 污染，于是每次下载都 SocketTimeout。
        /// 先把这些库按 maven 路径从镜像拉好放进 libraries/，installer 就会打印
        /// "File ... exists. Checksum valid." 并直接跳过下载。
        /// 返回预取的文件数。
        /// </summary>
        public static async Task<int> PrefetchProfileLibrariesAsync(string installerJar, string gameDir,
            string mirrorMavenRoot, InstallTask task, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(mirrorMavenRoot)) return 0;

            JsonObject profile;
            try
            {
                using var zip = ZipFile.OpenRead(installerJar);
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.Equals("install_profile.json", StringComparison.OrdinalIgnoreCase));
                if (entry == null) return 0;

                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                profile = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
            }
            catch (Exception ex)
            {
                Log.Warn("读取 install_profile.json 失败，跳过库预取：" + ex.Message);
                return 0;
            }

            if (profile == null) return 0;

            var libsDir = Paths.LibrariesDir(gameDir);
            var queue = new DownloadQueue();

            foreach (var node in J.Arr(profile, "libraries") ?? new JsonArray())
            {
                if (node is not JsonObject lib) continue;

                var artifact = J.Obj(J.Obj(lib, "downloads"), "artifact");

                var rel = J.Str(artifact, "path");
                if (string.IsNullOrEmpty(rel)) rel = LaunchBuilder.MavenPath(J.Str(lib, "name"));
                if (string.IsNullOrEmpty(rel)) continue;

                var full = Path.Combine(libsDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) continue;

                queue.Enqueue(new DownloadJob
                {
                    Name = rel,
                    Url = mirrorMavenRoot.TrimEnd('/') + "/" + rel,
                    Path = full,
                    Size = J.Long(artifact, "size"),
                    Sha1 = J.Str(artifact, "sha1"),
                    TimeoutMs = Math.Max(180000, (int)Math.Min(600000, J.Long(artifact, "size") / 1024 / 30 * 1000))
                });
            }

            if (queue.Jobs.Count == 0)
                return 0;

            task.SetStage($"预取安装器依赖（{queue.Jobs.Count} 个）");
            Log.Info($"预取 install_profile 依赖：{queue.Jobs.Count} 个（镜像 {mirrorMavenRoot}）");

            var total = queue.TotalBytes;
            queue.Changed += () => task.SetProgress(queue.DoneBytes,
                Math.Max(total, queue.DoneBytes), queue.Speed);

            var failed = await queue.RunAsync(8, ct).ConfigureAwait(false);
            if (failed > 0)
            {
                var bad = queue.Jobs.FirstOrDefault(j => j.State == JobState.Failed);
                Log.Warn($"预取有 {failed} 个失败" + (bad != null ? $"（{bad.Name}：{bad.Error}）" : ""));
                // 不在这里抛：installer 可能自己还能从别的地址补上
            }

            Log.Info($"预取完成：{queue.Jobs.Count - failed} 成功 / {failed} 失败");
            return queue.Jobs.Count - failed;
        }

        /// <summary>
        /// 补齐版本 JSON 里列出、但磁盘上缺失的依赖库。
        ///
        /// 为什么需要这步：installer 只下载它自己 processor 阶段要用的库，版本 JSON 里另外列出的
        /// 运行期库（如 at-parser、srgutils）它不管；这些库的 url 又写死了 maven.neoforged.net，
        /// 国内不可达。这里按 maven 路径把它改写到镜像下载。
        /// 返回补下的文件数。
        /// </summary>
        public static async Task<int> EnsureLibrariesAsync(string gameDir, string versionId,
            InstallTask task, CancellationToken ct, string mirrorMavenRoot)
        {
            ResolvedVersion resolved;
            try
            {
                resolved = VersionManager.Resolve(gameDir, versionId);
            }
            catch (Exception ex)
            {
                Log.Warn($"解析 {versionId} 的依赖失败，跳过补齐：{ex.Message}");
                return 0;
            }

            var libsDir = Paths.LibrariesDir(gameDir);
            var queue = new DownloadQueue();

            foreach (var lib in resolved.Libraries)
            {
                var artifact = J.Obj(J.Obj(lib, "downloads"), "artifact");

                var rel = J.Str(artifact, "path");
                if (string.IsNullOrEmpty(rel)) rel = LaunchBuilder.MavenPath(J.Str(lib, "name"));
                if (string.IsNullOrEmpty(rel)) continue;

                var full = Path.Combine(libsDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) continue;

                var url = J.Str(artifact, "url");
                if (!string.IsNullOrEmpty(mirrorMavenRoot) &&
                    (string.IsNullOrEmpty(url) || url.Contains("maven.neoforged.net")))
                    url = mirrorMavenRoot.TrimEnd('/') + "/" + rel;

                if (string.IsNullOrEmpty(url)) continue;

                queue.Enqueue(new DownloadJob
                {
                    Name = rel,
                    Url = url,
                    Path = full,
                    Size = J.Long(artifact, "size"),
                    Sha1 = J.Str(artifact, "sha1")
                });
            }

            if (queue.Jobs.Count == 0) return 0;

            task.SetStage($"补齐缺失依赖（{queue.Jobs.Count} 个）");
            Log.Info($"{versionId} 缺 {queue.Jobs.Count} 个依赖库，开始补齐");

            var total = queue.TotalBytes;
            queue.Changed += () => task.SetProgress(queue.DoneBytes,
                Math.Max(total, queue.DoneBytes), queue.Speed);

            var failed = await queue.RunAsync(6, ct).ConfigureAwait(false);
            if (failed > 0)
            {
                var bad = queue.Jobs.FirstOrDefault(j => j.State == JobState.Failed);
                throw new IOException($"有 {failed} 个依赖库下载失败" +
                                      (bad != null ? $"（{bad.Name}：{bad.Error}）" : ""));
            }

            Log.Info($"{versionId} 依赖补齐完成：{queue.Jobs.Count} 个");
            return queue.Jobs.Count;
        }

        /// <summary>
        /// 跑 installer 并等到结束。输出同时写日志文件与内存缓冲（用于失败诊断）。
        /// 安装期间每 2 秒刷新一次"已用时长"，让用户知道没死掉。
        /// </summary>
        public static async Task<(int ExitCode, string Output)> RunAsync(string javaPath, string jarPath,
            string gameDir, InstallTask task, string logFileName, CancellationToken ct,
            string mirrorUrl = null)
        {
            var psi = new ProcessStartInfo(javaPath)
            {
                WorkingDirectory = gameDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.ArgumentList.Add("-jar");
            psi.ArgumentList.Add(jarPath);
            psi.ArgumentList.Add("--installClient");
            psi.ArgumentList.Add(gameDir);

            // NeoForge / Forge 的 installer 支持 --mirror：让它从镜像拉依赖库。
            // 官方 maven 域名在国内常被 DNS 污染，不给 mirror 会卡在下载阶段。
            if (!string.IsNullOrWhiteSpace(mirrorUrl))
            {
                psi.ArgumentList.Add("--mirror");
                psi.ArgumentList.Add(mirrorUrl);
            }

            var buffer = new StringBuilder();
            var logPath = Path.Combine(Paths.LogDir, logFileName);

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

            void Collect(string line)
            {
                if (line == null) return;
                lock (buffer)
                {
                    buffer.AppendLine(line);
                    if (buffer.Length > 64 * 1024)
                        buffer.Remove(0, buffer.Length - 48 * 1024);
                }
            }

            p.OutputDataReceived += (s, e) => Collect(e.Data);
            p.ErrorDataReceived += (s, e) => Collect(e.Data);

            var started = DateTime.Now;
            var sw = Stopwatch.StartNew();

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // 心跳：installer 只在结束时输出，中间几分钟没有动静，必须自己报时长
            using var ticker = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = Task.Run(async () =>
            {
                while (!ticker.IsCancellationRequested)
                {
                    try { await Task.Delay(2000, ticker.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    var el = (int)sw.Elapsed.TotalSeconds;
                    task.SetStage($"正在运行安装器（已用 {el / 60:00}:{el % 60:00}）");
                }
            }, ticker.Token);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(InstallTimeout);

            try
            {
                await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(p);
                throw new TimeoutException(
                    $"安装器运行超过 {InstallTimeout.TotalMinutes:0} 分钟仍未结束，已终止。" +
                    "通常是下载卡住（installer 会自己联网拉依赖），可换网络环境或手动装一次。");
            }
            catch (OperationCanceledException)
            {
                TryKill(p);
                throw;
            }
            finally
            {
                ticker.Cancel();
                try { await heartbeat.ConfigureAwait(false); } catch { }
            }

            // WaitForExit 之后要再等一次，确保异步输出全部读完
            await Task.Run(() => { try { p.WaitForExit(); } catch { } }).ConfigureAwait(false);

            string text;
            lock (buffer) text = buffer.ToString();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? Paths.LogDir);
                File.WriteAllText(logPath,
                    $"# {logFileName}\n# {started:yyyy-MM-dd HH:mm:ss} → {DateTime.Now:HH:mm:ss}" +
                    $"（{sw.Elapsed.TotalSeconds:0.0}s，退出码 {p.ExitCode}）\n" +
                    $"# java: {javaPath}\n# jar : {jarPath}\n# dir : {gameDir}\n\n{text}");
            }
            catch (Exception ex)
            {
                Log.Warn("写安装器日志失败：" + ex.Message);
            }

            return (p.ExitCode, text);
        }

        private static void TryKill(Process p)
        {
            try
            {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Log.Warn("结束安装器进程失败：" + ex.Message);
            }
        }

        /// <summary>把 installer 的失败输出翻成人话。</summary>
        public static string Describe(string output)
        {
            if (string.IsNullOrEmpty(output)) return "";

            if (output.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase))
                return "安装器要求的 Java 版本比当前的更高，请升级 Java 后重试。";

            if (output.Contains("launcher_profiles", StringComparison.OrdinalIgnoreCase))
                return "目标目录缺少 launcher_profiles.json，启动器本应自动创建，请检查目录写权限。";

            if (output.Contains("Could not find or load main class", StringComparison.OrdinalIgnoreCase))
                return "安装器 jar 可能损坏，请删除缓存后重试。";

            if (output.Contains("Connection", StringComparison.OrdinalIgnoreCase) &&
                output.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                return "安装器下载依赖超时（它自己联网拉 maven 依赖，国内可能很慢）。";

            return "";
        }

        public static string Tail(string text, int lines)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var all = text.Replace("\r\n", "\n").Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }
    }
}
