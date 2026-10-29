using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;
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
    ///
    /// 具体的进程管理、Java 选择、launcher_profiles 兜底等都在 <see cref="InstallerSupport"/>，
    /// 与 NeoForge 共用。
    /// </summary>
    public static class ForgeInstaller
    {
        private const string PromotionsUrl =
            "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";

        private const string MavenBase =
            "https://maven.minecraftforge.net/net/minecraftforge/forge/";

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

            var node = await Net.GetJsonRawAsync(PromotionsUrl, ct).ConfigureAwait(false);
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

            var installerPath = await InstallerSupport.DownloadInstallerAsync(
                new[] { InstallerUrl(mcVersion, loaderVersion) },
                "forge",
                $"forge-{mcVersion}-{loaderVersion}-installer.jar",
                task, ct).ConfigureAwait(false);

            // ---- 2. Java：installer 自己也是 Java 程序，版本不够会直接跑不起来 ----
            var java = await InstallerSupport.EnsureJavaAsync(gameDir, mcVersion, task, ct).ConfigureAwait(false);

            // ---- 3. installer 需要一个 launcher_profiles.json 才肯干活 ----
            InstallerSupport.EnsureLauncherProfiles(gameDir);

            // ---- 4. 无头执行 ----
            task.SetStage($"正在运行 Forge 安装器（Java {java.Major}）");
            Log.Info($"运行 Forge installer：{java.Path} -jar {installerPath} --installClient \"{gameDir}\"");

            var (exitCode, output) = await InstallerSupport.RunAsync(java.Path, installerPath, gameDir,
                task, "forge-installer.log", ct).ConfigureAwait(false);

            // ---- 5. 校验产物：installer 退出码 0 不等于真装好了 ----
            if (!IsInstalled(gameDir, versionId))
            {
                var tail = InstallerSupport.Tail(output, 12);
                throw new InvalidOperationException(
                    $"Forge 安装器已结束（退出码 {exitCode}），但没有生成 {versionId}.json。" +
                    InstallerSupport.Describe(output) +
                    (string.IsNullOrEmpty(tail) ? "" : "\n最近输出：\n" + tail));
            }

            // ---- 6. 兜底补齐依赖（installer 未必把 JSON 里列的库全下） ----
            await InstallerSupport.EnsureLibrariesAsync(gameDir, versionId, task, ct, null)
                .ConfigureAwait(false);

            // ---- 7. 版本目录（mods / saves 等） ----
            VersionEnv.Ensure(Path.Combine(Paths.VersionsDir(gameDir), versionId));

            task.SetStage("安装完成");
            Log.Info($"Forge {loaderVersion}（{mcVersion}）安装完成：{versionId}，退出码 {exitCode}");
            return versionId;
        }
    }
}
