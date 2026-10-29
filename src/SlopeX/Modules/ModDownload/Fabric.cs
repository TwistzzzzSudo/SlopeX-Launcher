using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModLaunch;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModDownload
{
    /// <summary>Fabric 加载器版本。</summary>
    public sealed class FabricLoader
    {
        public string Version { get; set; }
        public bool Stable { get; set; }
        public string Label => string.IsNullOrEmpty(Version) ? "" : (Stable ? Version : Version + "（测试版）");
    }

    /// <summary>
    /// Fabric 安装（对应 PCL2 的安装器加载器分支）。
    ///
    /// 与 Forge 不同，Fabric 不需要跑任何 Java 处理器：
    /// 官方 meta 直接给出一份完整的版本 profile（<c>inheritsFrom</c> 指向原版 + 一串 Maven 库），
    /// 落盘后由现有的继承链合并与 classpath 逻辑接管，因此这里只需要"下载 + 写 JSON"。
    /// </summary>
    public static class FabricInstaller
    {
        private const string MetaBase = "https://meta.fabricmc.net/v2/versions/loader";
        private const string DefaultMaven = "https://maven.fabricmc.net/";

        public static string BuildVersionId(string loaderVersion, string mcVersion) =>
            $"fabric-loader-{loaderVersion}-{mcVersion}";

        public static bool IsInstalled(string gameDir, string versionId)
        {
            var json = Path.Combine(Paths.VersionsDir(gameDir), versionId, versionId + ".json");
            return File.Exists(json);
        }

        /// <summary>拉取某个原版版本可用的全部加载器（新的在前）。</summary>
        public static async Task<List<FabricLoader>> GetLoadersAsync(string mcVersion, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(mcVersion))
                throw new ArgumentException("未指定原版版本", nameof(mcVersion));

            var node = await GetMetaAsync($"{MetaBase}/{mcVersion}", ct).ConfigureAwait(false);
            var list = new List<FabricLoader>();

            if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    var loader = J.Obj(item, "loader");
                    var version = J.Str(loader, "version");
                    if (string.IsNullOrEmpty(version)) continue;
                    list.Add(new FabricLoader { Version = version, Stable = J.Bool(loader, "stable") });
                }
            }

            return list;
        }

        /// <summary>取最新稳定版；没有稳定版时退回最新的一个；一个都没有返回 null。</summary>
        public static async Task<string> GetLatestStableAsync(string mcVersion, CancellationToken ct = default)
        {
            var list = await GetLoadersAsync(mcVersion, ct).ConfigureAwait(false);
            var stable = list.FirstOrDefault(l => l.Stable);
            return stable?.Version ?? list.FirstOrDefault()?.Version;
        }

        /// <summary>取加载器 profile（含 inheritsFrom / mainClass / libraries）。</summary>
        public static async Task<JsonObject> GetProfileAsync(string mcVersion, string loaderVersion,
            CancellationToken ct = default)
        {
            var url = $"{MetaBase}/{mcVersion}/{loaderVersion}/profile/json";
            var node = await GetMetaAsync(url, ct).ConfigureAwait(false);
            return node as JsonObject ?? throw new InvalidDataException("Fabric profile 不是合法 JSON 对象");
        }

        /// <summary>
        /// 安装 Fabric：原版缺失时先补原版，再写版本 JSON、下载加载器依赖。
        /// 返回最终版本 ID（形如 <c>fabric-loader-0.20.1-1.20.1</c>）。
        /// </summary>
        public static async Task<string> InstallAsync(string gameDir, string mcVersion, string loaderVersion,
            InstallTask task, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(mcVersion))
                throw new ArgumentException("未指定原版版本", nameof(mcVersion));

            // ---- 0. 原版是前提：Fabric 是叠加在原版之上的 ----
            if (!VanillaInstaller.IsInstalled(gameDir, mcVersion))
            {
                task.SetStage($"Fabric 需要原版 {mcVersion}，先安装原版");
                Log.Info($"安装 Fabric 前先补装原版 {mcVersion}");
                await VanillaInstaller.InstallAsync(gameDir, mcVersion, task, ct).ConfigureAwait(false);
            }

            // ---- 1. 加载器版本 ----
            if (string.IsNullOrWhiteSpace(loaderVersion))
            {
                task.SetStage("查询 Fabric 加载器版本");
                loaderVersion = await GetLatestStableAsync(mcVersion, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(loaderVersion))
                    throw new InvalidOperationException($"Fabric 没有为 {mcVersion} 提供任何加载器（可能版本过旧或尚未支持）");
            }

            // ---- 2. profile ----
            task.SetStage($"获取 Fabric {loaderVersion} 配置");
            var profile = await GetProfileAsync(mcVersion, loaderVersion, ct).ConfigureAwait(false);

            var versionId = J.Str(profile, "id");
            if (string.IsNullOrEmpty(versionId)) versionId = BuildVersionId(loaderVersion, mcVersion);

            // 上游字段可能缺，这里补成确定的：ID 用于目录名，inheritsFrom 决定继承链
            profile["id"] = versionId;
            profile["inheritsFrom"] = mcVersion;

            var versionDir = Path.Combine(Paths.VersionsDir(gameDir), versionId);
            Directory.CreateDirectory(versionDir);

            var jsonPath = Path.Combine(versionDir, versionId + ".json");
            File.WriteAllText(jsonPath, profile.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                // 不转义 + / 中文，落盘文件保持可读（默认编码器会把 "+" 写成 \u002B）
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
            Log.Info($"Fabric 版本定义已写入 {jsonPath}");

            // ---- 3. 依赖库 ----
            var libsDir = Paths.LibrariesDir(gameDir);
            var queue = new DownloadQueue();

            foreach (var node in J.Arr(profile, "libraries") ?? new JsonArray())
            {
                if (!(node is JsonObject lib)) continue;

                var name = J.Str(lib, "name");
                if (string.IsNullOrEmpty(name)) continue;

                var rel = LaunchBuilder.MavenPath(name);
                if (string.IsNullOrEmpty(rel)) continue;

                var baseUrl = J.Str(lib, "url");
                if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = DefaultMaven;

                queue.Enqueue(new DownloadJob
                {
                    Name = name,
                    Url = baseUrl.TrimEnd('/') + "/" + rel,
                    Path = Path.Combine(libsDir, rel.Replace('/', Path.DirectorySeparatorChar)),
                    Size = J.Long(lib, "size"),
                    Sha1 = J.Str(lib, "sha1")
                });
            }

            var total = queue.TotalBytes;
            task.SetStage($"下载 Fabric 依赖（{queue.Jobs.Count} 个文件）");

            // profile 里个别库缺 size，总字节会低估 → 以实际下载量为下界，避免出现 "2.9MB / 1.8MB"
            queue.Changed += () =>
            {
                var live = Math.Max(total, queue.DoneBytes);
                task.SetProgress(queue.DoneBytes, live, queue.Speed);
            };

            var failed = await queue.RunAsync(8, ct).ConfigureAwait(false);
            if (failed > 0)
            {
                var bad = queue.Jobs.FirstOrDefault(j => j.State == JobState.Failed);
                throw new IOException($"Fabric 有 {failed} 个依赖下载失败" +
                                      (bad != null ? $"（{bad.Name}：{bad.Error}）" : ""));
            }

            // ---- 4. 版本目录（mods / resourcepacks 等） ----
            VersionEnv.Ensure(versionDir);

            task.SetProgress(total, total, 0);
            task.SetStage("安装完成");
            Log.Info($"Fabric {loaderVersion}（{mcVersion}）安装完成：{versionId}，依赖 {queue.Jobs.Count} 个");
            return versionId;
        }

        /// <summary>
        /// 取元数据：优先镜像，失败回落官方。
        /// Net.GetStringAsync 内部已做镜像改写，但改写后失败没有回退，所以这里显式两级尝试。
        /// </summary>
        private static async Task<JsonNode> GetMetaAsync(string officialUrl, CancellationToken ct)
        {
            var mirrored = Net.Mirror(officialUrl);
            if (!string.Equals(mirrored, officialUrl, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return await Net.GetJsonRawAsync(mirrored, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Fabric 镜像不可用，回落官方源：{ex.Message}");
                }
            }

            return await Net.GetJsonRawAsync(officialUrl, ct).ConfigureAwait(false);
        }
    }
}
