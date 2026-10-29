using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModJava;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModDownload
{
    /// <summary>
    /// Forge 安装（B 方案：直接调官方 installer 的 <c>--installClient</c>）。
    ///
    /// 为什么不自己跑 processors：Forge 的 <c>install_profile.json</c> 里列着一串需要在 JVM 里执行的
    /// 处理器（MCP→SRG 映射、client-extra 拆分、srg 命名表、jar 改写），要复现等于把 installer 的
    /// 核心逻辑重写一遍，失败模式极多。让官方 installer 干这件事，成功率接近 100%。
    ///
    /// 代价（已知且接受）：拿不到细粒度进度，只能显示"正在安装 + 已用时长"，且 installer 自己走
    /// 官方 maven，国内可能慢。
    /// </summary>
    public static class ForgeInstaller
    {
        private const string PromotionsUrl =
            "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";

        private const string MavenBase =
            "https://maven.minecraftforge.net/net/minecraftforge/forge/";

        /// <summary>整个安装过程的上限。装 processors 要下载并改写几百 MB，给足余量。</summary>
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(20);

        public static string BuildVersionId(string mcVersion, string loaderVersion) =>
            $"{mcVersion}-forge-{loaderVersion}";

        public static string InstallerUrl(string mcVersion, string loaderVersion) =>
            $"{MavenBase}{mcVersion}-{loaderVersion}/forge-{mcVersion}-{loaderVersion}-installer.jar";

        public static bool IsInstalled(string gameDir, string versionId)
        {
            var json = Path.Combine(Paths.VersionsDir(gameDir), versionId, versionId + ".json");
            return File.Exists(json);
        }

        /// <summary>
        /// 取推荐版本：优先 <c>&lt;mc&gt;-recommended</c>，没有则 <c>&lt;mc&gt;-latest</c>。
        /// 返回 null 表示 Forge 不支持该原版版本。
        /// </summary>
        public static async Task<string> GetRecommendedAsync(string mcVersion, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(mcVersion))
                throw new ArgumentException("未指定原版版本", nameof(mcVersion));

            var node = await GetMetaAsync(ct).ConfigureAwait(false);
            var promos = J.Obj(node, "promos");
            if (promos == null) return null;

            return J.Str(promos, mcVersion + "-recommended")
                   ?? J.Str(promos, mcVersion + "-latest");
        }

        /// <summary>Forge 是否有该原版版本（用于界面提前拦截，避免点了才报错）。</summary>
        public static async Task<bool> SupportsAsync(string mcVersion, CancellationToken ct = default)
        {
            try
            {
                return !string.IsNullOrEmpty(await GetRecommendedAsync(mcVersion, ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                Log.Warn($"查询 Forge 支持情况失败：{ex.Message}");
                return true; // 查询失败不拦，交给真正安装时报错
            }
        }

        /// <summary>
        /// 安装 Forge：原版缺失先补原版 → 下载 installer → 用合适的 Java 无头执行 → 校验产物。
        /// 返回最终版本 ID（形如 <c>1.20.1-forge-47.4.28</c>）。
        /// </summary>
        public static async Task<string> InstallAsync(string gameDir, string mcVersion, string loaderVersion,
            InstallTask task, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(mcVersion))
                throw new ArgumentException("未指定原版版本", nameof(mcVersion));

            // ---- 0. 原版是前提：Forge 的版本 JSON 靠 inheritsFrom 指向它 ----
            if (!VanillaInstaller.IsInstalled(gameDir, mcVersion))
            {
                task.SetStage($"Forge 需要原版 {mcVersion}，先安装原版");
                Log.Info($"安装 Forge 前先补装原版 {mcVersion}");
                await VanillaInstaller.InstallAsync(gameDir, mcVersion, task, ct).ConfigureAwait(false);
            }

            // ---- 1. 加载器版本 ----
            if (string.IsNullOrWhiteSpace(loaderVersion))
            {
                task.SetStage("查询 Forge 推荐版本");
                loaderVersion = await GetRecommendedAsync(mcVersion, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(loaderVersion))
                    throw new InvalidOperationException(
                        $"Forge 没有为 {mcVersion} 提供安装包。可在 https://files.minecraftforge.net 确认该版本的可用性。");
            }

            var versionId = BuildVersionId(mcVersion, loaderVersion);
            var installerPath = await DownloadInstallerAsync(mcVersion, loaderVersion, task, ct)
                .ConfigureAwait(false);

            // ---- 2. Java：installer 自己也是 Java 程序，版本不够会直接跑不起来 ----
            var java = await EnsureJavaAsync(gameDir, mcVersion, task, ct).ConfigureAwait(false);

            // ---- 3. installer 需要一个 launcher_profiles.json 才肯干活 ----
            EnsureLauncherProfiles(gameDir);

            // ---- 4. 无头执行 ----
            task.SetStage($"正在运行 Forge 安装器（Java {java.Major}）");
            Log.Info($"运行 Forge installer：{java.Path} -jar {installerPath} --installClient \"{gameDir}\"");

            var (exitCode, output) = await RunInstallerAsync(java.Path, installerPath, gameDir, task, ct)
                .ConfigureAwait(false);

            // ---- 5. 校验产物：installer 退出码 0 不等于真装好了 ----
            if (!IsInstalled(gameDir, versionId))
            {
                var tail = Tail(output, 12);
                throw new InvalidOperationException(
                    $"Forge 安装器已结束（退出码 {exitCode}），但没有生成 {versionId}.json。{Describe(output)}" +
                    (string.IsNullOrEmpty(tail) ? "" : "\n最近输出：\n" + tail));
            }

            // ---- 6. 版本目录（mods / saves 等） ----
            VersionEnv.Ensure(Path.Combine(Paths.VersionsDir(gameDir), versionId));

            task.SetStage("安装完成");
            Log.Info($"Forge {loaderVersion}（{mcVersion}）安装完成：{versionId}，退出码 {exitCode}");
            return versionId;
        }

        /// <summary>下载 installer 到缓存目录（同一个版本只下一次）。</summary>
        private static async Task<string> DownloadInstallerAsync(string mcVersion, string loaderVersion,
            InstallTask task, CancellationToken ct)
        {
            var dir = Path.Combine(Paths.CacheDir, "forge");
            Directory.CreateDirectory(dir);

            var fileName = $"forge-{mcVersion}-{loaderVersion}-installer.jar";
            var path = Path.Combine(dir, fileName);

            // 已存在且是个像样的 jar（> 100 KB）就直接复用
            if (File.Exists(path))
            {
                var len = new FileInfo(path).Length;
                if (len > 100 * 1024)
                {
                    Log.Info($"复用已下载的 Forge 安装器（{Format.Bytes(len)}）");
                    task.SetStage("复用已缓存的安装器");
                    return path;
                }

                File.Delete(path); // 下了一半的残缺文件
            }

            var url = InstallerUrl(mcVersion, loaderVersion);
            task.SetStage("下载 Forge 安装器");

            var queue = new DownloadQueue();
            queue.Enqueue(new DownloadJob { Name = fileName, Url = url, Path = path });

            queue.Changed += () => task.SetProgress(queue.DoneBytes, Math.Max(queue.TotalBytes, queue.DoneBytes), queue.Speed);

            var failed = await queue.RunAsync(1, ct).ConfigureAwait(false);
            if (failed > 0 || !File.Exists(path))
            {
                var job = queue.Jobs.FirstOrDefault();
                throw new IOException("下载 Forge 安装器失败：" + (job?.Error ?? "未知原因") +
                                      "\n可手动下载后放到 " + dir);
            }

            Log.Info($"Forge 安装器已下载：{fileName}（{Format.Bytes(new FileInfo(path).Length)}）");
            return path;
        }

        /// <summary>取运行 installer 所需的 Java；没有就按既有策略自动装一个。</summary>
        private static async Task<JavaInfo> EnsureJavaAsync(string gameDir, string mcVersion,
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
                Log.Info($"Forge 安装器使用 Java {java.Major}：{java.Path}");
                return java;
            }

            if (!Config.Current.AutoInstallJava)
                throw new InvalidOperationException(
                    $"安装 Forge 需要 Java {major}，本机未检测到，且设置里已关闭自动安装。");

            task.SetStage($"安装 Forge 需要 Java {major}，正在下载运行时");
            Log.Info($"未找到 Java {major}，自动下载安装");
            java = await JavaProvisioner.EnsureAsync(major, null, ct).ConfigureAwait(false);

            if (java == null)
                throw new InvalidOperationException($"自动安装 Java {major} 失败，无法运行 Forge 安装器。");

            return java;
        }

        /// <summary>
        /// installer 会往目标目录写 launcher_profiles.json（记录安装配置）。
        /// 我们的 .minecraft 可能没有这个文件，缺了 installer 会直接报错退出。
        /// </summary>
        private static void EnsureLauncherProfiles(string gameDir)
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
            Log.Info("已创建最小 launcher_profiles.json（Forge 安装器要求存在该文件）");
        }

        /// <summary>
        /// 跑 installer 并等到结束。输出同时写日志文件与内存缓冲（用于失败诊断）。
        /// 安装期间每 2 秒刷新一次"已用时长"，让用户知道没死掉。
        /// </summary>
        private static async Task<(int ExitCode, string Output)> RunInstallerAsync(
            string javaPath, string jarPath, string gameDir, InstallTask task, CancellationToken ct)
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

            var buffer = new StringBuilder();
            var logPath = Path.Combine(Paths.LogDir, "forge-installer.log");

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

            void Collect(string line)
            {
                if (line == null) return;
                lock (buffer)
                {
                    // 只留最近 400 行，避免内存无限增长
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
                    task.SetStage($"正在运行 Forge 安装器（已用 {el / 60:00}:{el % 60:00}）");
                }
            }, ticker.Token);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(InstallTimeout);

            var exited = false;
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                exited = true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(p);
                throw new TimeoutException(
                    $"Forge 安装器运行超过 {InstallTimeout.TotalMinutes:0} 分钟仍未结束，已终止。" +
                    "通常是下载卡住（installer 走官方 maven），可换网络环境或手动装一次。");
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
                    $"# Forge installer\n# {started:yyyy-MM-dd HH:mm:ss} → {DateTime.Now:HH:mm:ss}" +
                    $"（{sw.Elapsed.TotalSeconds:0.0}s，退出码 {p.ExitCode}）\n" +
                    $"# java: {javaPath}\n# jar : {jarPath}\n# dir : {gameDir}\n\n{text}");
            }
            catch (Exception ex)
            {
                Log.Warn("写 Forge 安装日志失败：" + ex.Message);
            }

            if (!exited) p.WaitForExit(3000);

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
                Log.Warn("结束 Forge 安装器进程失败：" + ex.Message);
            }
        }

        /// <summary>把 installer 的失败输出翻成人话。</summary>
        private static string Describe(string output)
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
                return "安装器下载依赖超时（它走官方 maven，国内可能很慢）。";

            return "";
        }

        private static string Tail(string text, int lines)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var all = text.Replace("\r\n", "\n").Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }

        /// <summary>取 promotions 元数据（官方源，无镜像）。</summary>
        private static async Task<JsonNode> GetMetaAsync(CancellationToken ct)
        {
            return await Net.GetJsonRawAsync(PromotionsUrl, ct).ConfigureAwait(false);
        }
    }
}
