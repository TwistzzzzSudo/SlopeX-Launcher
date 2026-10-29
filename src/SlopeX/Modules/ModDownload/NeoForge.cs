using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModDownload
{
    /// <summary>
    /// NeoForge 安装。
    ///
    /// 与 Forge 同一条路线（官方 installer 无头执行 <c>--installClient</c>），但有三个实质差异：
    /// <list type="number">
    /// <item>版本号规则不同：NeoForge 用「MC 次版本.补丁.构建」，例如 1.21.1 → 21.1.x、26.3 → 26.3.0.x。
    ///       所以没法像 Forge 那样靠 promotions 表查，只能取 maven-metadata.xml 自己筛。</item>
    /// <item>仓库域名不同（maven.neoforged.net），该域名在国内常见 DNS 污染，
    ///       <b>必须走镜像</b>：BMCLAPI 的 <c>/maven</c> 代理能取到元数据与 installer。</item>
    /// <item>1.20.1 时代的 NeoForge 发布在另一个 artifact（<c>net.neoforged:forge</c>），本模块不覆盖。</item>
    /// </list>
    ///
    /// 一个容易踩的细节：BMCLAPI 转发的镜像对**带浏览器 User-Agent 或 Referer** 的请求返回 403，
    /// 用普通 UA（本项目的 <c>SlopeX/1.0</c>）才放行——这不是权限问题，是防盗链。
    /// </summary>
    public static class NeoForgeInstaller
    {
        private const string MirrorMeta =
            "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/maven-metadata.xml";

        private const string OfficialMeta =
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";

        private const string MirrorBase =
            "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/";

        private const string OfficialBase =
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/";

        /// <summary>installer 的 --mirror 参数要的是 maven 根，而不是具体 artifact 目录。</summary>
        private const string MirrorMavenRoot = "https://bmclapi2.bangbang93.com/maven/";

        // 元数据缓存：界面查一次、安装时又查一次，没必要重复拉
        private static readonly Dictionary<string, (DateTime At, List<string> Versions)> MetaCache =
            new Dictionary<string, (DateTime At, List<string> Versions)>();

        public static string BuildVersionId(string loaderVersion) => "neoforge-" + loaderVersion;

        public static bool IsInstalled(string gameDir, string versionId)
        {
            var json = Path.Combine(Paths.VersionsDir(gameDir), versionId, versionId + ".json");
            return File.Exists(json);
        }

        public static string InstallerUrl(string loaderVersion) =>
            $"{MirrorBase}{loaderVersion}/neoforge-{loaderVersion}-installer.jar";

        private static string OfficialInstallerUrl(string loaderVersion) =>
            $"{OfficialBase}{loaderVersion}/neoforge-{loaderVersion}-installer.jar";

        /// <summary>
        /// MC 版本 → NeoForge 版本前缀。
        /// 1.21.1 → <c>21.1</c>；1.20.2 → <c>20.2</c>；26.3 → <c>26.3</c>。
        /// </summary>
        public static string PrefixOf(string mcVersion)
        {
            if (string.IsNullOrWhiteSpace(mcVersion)) return "";
            var v = mcVersion.Trim();
            if (v.StartsWith("1.", StringComparison.Ordinal)) v = v.Substring(2);

            var parts = v.Split('.');
            if (parts.Length >= 2) return parts[0] + "." + parts[1];
            return v;
        }

        /// <summary>取该原版可用的 NeoForge 版本（新的在前）。</summary>
        public static async Task<List<string>> GetVersionsAsync(string mcVersion, CancellationToken ct = default)
        {
            var prefix = PrefixOf(mcVersion);
            if (string.IsNullOrEmpty(prefix)) return new List<string>();

            var all = await GetAllVersionsAsync(ct).ConfigureAwait(false);

            return all
                .Where(v => v.StartsWith(prefix + ".", StringComparison.Ordinal))
                .Where(v => v.IndexOf('-') < 0)          // 排除 beta / 预发布
                .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions))
                .ToList();
        }

        /// <summary>取该原版最新的 NeoForge 版本；没有返回 null。</summary>
        public static async Task<string> GetLatestAsync(string mcVersion, CancellationToken ct = default)
        {
            var list = await GetVersionsAsync(mcVersion, ct).ConfigureAwait(false);
            return list.FirstOrDefault();
        }

        /// <summary>
        /// 安装 NeoForge：原版缺失先补 → 下 installer（镜像优先）→ 无头执行 → 校验产物。
        /// 返回版本 ID（形如 <c>neoforge-21.1.265</c>）。
        /// </summary>
        public static async Task<string> InstallAsync(string gameDir, string mcVersion, string loaderVersion,
            InstallTask task, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(mcVersion))
                throw new ArgumentException("未指定原版版本", nameof(mcVersion));

            if (PrefixOf(mcVersion) == "20.1")
                throw new InvalidOperationException(
                    "NeoForge 在 1.20.1 上以 net.neoforged:forge 47.1.x 的形式发布，本启动器暂不支持这条线。" +
                    "\n1.20.1 用 Forge 即可；NeoForge 请选 1.20.2 及以后的版本。");

            if (!VanillaInstaller.IsInstalled(gameDir, mcVersion))
            {
                task.SetStage($"NeoForge 需要原版 {mcVersion}，先安装原版");
                Log.Info($"安装 NeoForge 前先补装原版 {mcVersion}");
                await VanillaInstaller.InstallAsync(gameDir, mcVersion, task, ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(loaderVersion))
            {
                task.SetStage("查询 NeoForge 版本");
                loaderVersion = await GetLatestAsync(mcVersion, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(loaderVersion))
                    throw new InvalidOperationException(
                        $"NeoForge 没有为 {mcVersion} 提供版本。可核对原版版本号是否正确（NeoForge 只支持 1.20.2 及以后）。");
            }

            var versionId = BuildVersionId(loaderVersion);

            var installerPath = await InstallerSupport.DownloadInstallerAsync(
                new[] { InstallerUrl(loaderVersion), OfficialInstallerUrl(loaderVersion) },
                "neoforge",
                $"neoforge-{loaderVersion}-installer.jar",
                task, ct).ConfigureAwait(false);

            var java = await InstallerSupport.EnsureJavaAsync(gameDir, mcVersion, task, ct).ConfigureAwait(false);

            InstallerSupport.EnsureLauncherProfiles(gameDir);

            // installer 内部的 maven 地址写死为 maven.neoforged.net，国内不可达，
            // 必须先把 install_profile 里的库从镜像预取好，否则必然 SocketTimeout。
            await InstallerSupport.PrefetchProfileLibrariesAsync(installerPath, gameDir, MirrorMavenRoot,
                task, ct).ConfigureAwait(false);

            task.SetStage($"正在运行 NeoForge 安装器（Java {java.Major}）");
            Log.Info($"运行 NeoForge installer：{java.Path} -jar {installerPath} --installClient \"{gameDir}\"");

            // --mirror 指向 BMCLAPI 的 maven 根：installer 拉库时也走镜像，
            // 否则它会去连 maven.neoforged.net（国内 DNS 污染，必失败）
            var (exitCode, output) = await InstallerSupport.RunAsync(java.Path, installerPath, gameDir,
                task, "neoforge-installer.log", ct, MirrorMavenRoot).ConfigureAwait(false);

            if (!IsInstalled(gameDir, versionId))
            {
                var tail = InstallerSupport.Tail(output, 12);
                throw new InvalidOperationException(
                    $"NeoForge 安装器已结束（退出码 {exitCode}），但没有生成 {versionId}.json。" +
                    InstallerSupport.Describe(output) +
                    (string.IsNullOrEmpty(tail) ? "" : "\n最近输出：\n" + tail));
            }

            // installer 只下 processor 阶段的库，版本 JSON 里的运行期库要自己补
            await InstallerSupport.EnsureLibrariesAsync(gameDir, versionId, task, ct, MirrorMavenRoot)
                .ConfigureAwait(false);

            VersionEnv.Ensure(Path.Combine(Paths.VersionsDir(gameDir), versionId));

            task.SetStage("安装完成");
            Log.Info($"NeoForge {loaderVersion}（{mcVersion}）安装完成：{versionId}，退出码 {exitCode}");
            return versionId;
        }

        // ---------------- 元数据 ----------------

        private static async Task<List<string>> GetAllVersionsAsync(CancellationToken ct)
        {
            lock (MetaCache)
            {
                if (MetaCache.TryGetValue("all", out var hit) &&
                    DateTime.UtcNow - hit.At < TimeSpan.FromHours(6))
                    return hit.Versions;
            }

            string xml = null;

            try
            {
                xml = await Net.GetStringRawAsync(MirrorMeta, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"NeoForge 镜像元数据不可用，尝试官方源：{ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(xml))
                xml = await Net.GetStringRawAsync(OfficialMeta, ct).ConfigureAwait(false);

            var list = new List<string>();
            try
            {
                var doc = XDocument.Parse(xml);
                foreach (var e in doc.Descendants("version"))
                {
                    var v = e.Value?.Trim();
                    if (!string.IsNullOrEmpty(v)) list.Add(v);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("NeoForge 版本元数据解析失败：" + ex.Message, ex);
            }

            if (list.Count == 0)
                throw new InvalidDataException("NeoForge 版本元数据为空。");

            lock (MetaCache)
            {
                MetaCache["all"] = (DateTime.UtcNow, list);
            }

            Log.Info($"NeoForge 版本元数据：{list.Count} 个");
            return list;
        }

        /// <summary>比较 "21.1.265" 这类版本号，逐段按数字比；段数不同按缺失为 0。</summary>
        private static int CompareVersions(string a, string b)
        {
            var pa = a.Split('.');
            var pb = b.Split('.');

            var n = Math.Max(pa.Length, pb.Length);
            for (var i = 0; i < n; i++)
            {
                var x = i < pa.Length && int.TryParse(pa[i], out var xi) ? xi : 0;
                var y = i < pb.Length && int.TryParse(pb[i], out var yi) ? yi : 0;
                if (x != y) return x.CompareTo(y);
            }

            return 0;
        }
    }
}
