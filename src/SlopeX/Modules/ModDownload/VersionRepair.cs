using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModJava;
using SlopeX.Modules.ModLaunch;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModDownload
{
    /// <summary>
    /// 版本完整性检查与修复。
    ///
    /// 场景：用户把别处装好的版本目录直接复制进 <c>versions\</c>（很常见，尤其整合包），
    /// 但 <c>libraries</c> / <c>assets</c> 是全局共用的、没跟着复制过来，于是启动时报缺文件。
    ///
    /// 与"安装版本"的区别很重要：<b>修复只补缺失文件，绝不改写版本 JSON</b>。
    /// 外部版本的 JSON 里可能带着整合包自定义的字段（加载器、模组列表），覆盖就毁了。
    /// </summary>
    public static class VersionRepair
    {
        /// <summary>检查缺失项（库 / 客户端 jar / 资源 / 资源索引）。</summary>
        public static List<string> Check(string gameDir, string versionId)
        {
            var resolved = VersionManager.Resolve(gameDir, versionId);
            var libsDir = Paths.LibrariesDir(gameDir);
            var missing = new List<string>();

            foreach (var lib in resolved.Libraries)
            {
                var arch = JavaFinder.ArchOfCurrentProcess;
                if (!Rules.Allowed(J.Arr(lib, "rules"), new Dictionary<string, bool>(), arch)) continue;

                var artifact = J.Obj(J.Obj(lib, "downloads"), "artifact");
                var rel = J.Str(artifact, "path") ?? LaunchBuilder.MavenPath(J.Str(lib, "name"));
                if (string.IsNullOrEmpty(rel)) continue;

                var full = Path.Combine(libsDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) missing.Add(rel);
            }

            if (string.IsNullOrEmpty(resolved.JarPath) || !File.Exists(resolved.JarPath))
                missing.Add("客户端主文件 " + (resolved.JarPath ?? "(未定义)"));

            var indexFile = Path.Combine(Paths.AssetsDir(gameDir), "indexes", resolved.EffectiveAssetIndex + ".json");
            if (!File.Exists(indexFile) && !string.IsNullOrEmpty(resolved.AssetIndexUrl))
                missing.Add("资源索引 " + resolved.EffectiveAssetIndex);

            return missing;
        }

        /// <summary>
        /// 补齐缺失的库、客户端 jar 与资源索引。已有文件一律跳过，不改写任何版本定义。
        /// 返回补下的文件数。
        /// </summary>
        public static async Task<int> RepairAsync(string gameDir, string versionId, InstallTask task,
            CancellationToken ct)
        {
            var resolved = VersionManager.Resolve(gameDir, versionId);
            var total = 0;

            // ---- 1. 依赖库：复用安装器那套（会处理 neoforged 镜像改写） ----
            total += await InstallerSupport.EnsureLibrariesAsync(gameDir, versionId, task, ct,
                "https://bmclapi2.bangbang93.com/maven/").ConfigureAwait(false);

            // ---- 2. 客户端 jar ----
            var jar = resolved.JarPath;
            if (!string.IsNullOrEmpty(jar) && !File.Exists(jar) && !string.IsNullOrEmpty(resolved.ClientUrl))
            {
                task.SetStage("下载客户端主文件");
                await Net.DownloadToFileAsync(Net.Mirror(resolved.ClientUrl), jar, resolved.ClientSha1,
                    resolved.ClientSize, ct).ConfigureAwait(false);
                total++;
            }

            // ---- 3. 资源索引 + 缺失的资源对象 ----
            total += await RepairAssetsAsync(gameDir, resolved, task, ct).ConfigureAwait(false);

            Log.Info($"{versionId} 修复完成：补下 {total} 个文件");
            return total;
        }

        private static async Task<int> RepairAssetsAsync(string gameDir, ResolvedVersion resolved,
            InstallTask task, CancellationToken ct)
        {
            var indexFile = Path.Combine(Paths.AssetsDir(gameDir), "indexes", resolved.EffectiveAssetIndex + ".json");

            if (!File.Exists(indexFile))
            {
                if (string.IsNullOrEmpty(resolved.AssetIndexUrl)) return 0;

                task.SetStage("下载资源索引");
                Directory.CreateDirectory(Path.GetDirectoryName(indexFile) ?? Paths.AssetsDir(gameDir));
                var text = await Net.GetStringAsync(Net.Mirror(resolved.AssetIndexUrl), ct).ConfigureAwait(false);
                File.WriteAllText(indexFile, text);
            }

            JsonObject index;
            try
            {
                index = JsonNode.Parse(File.ReadAllText(indexFile)) as JsonObject;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("资源索引损坏，无法修复：" + ex.Message, ex);
            }

            var objects = J.Obj(index, "objects");
            if (objects == null) return 1;   // 只补了索引

            var objectsDir = Path.Combine(Paths.AssetsDir(gameDir), "objects");
            var queue = new DownloadQueue();

            foreach (var kv in objects)
            {
                var hash = J.Str(kv.Value, "hash");
                if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;

                var path = Path.Combine(objectsDir, hash.Substring(0, 2), hash);
                if (File.Exists(path)) continue;

                queue.Enqueue(new DownloadJob
                {
                    Name = kv.Key,
                    Url = $"https://resources.download.minecraft.net/{hash.Substring(0, 2)}/{hash}",
                    Path = path,
                    Size = J.Long(kv.Value, "size")
                });
            }

            if (queue.Jobs.Count == 0) return 1;

            task.SetStage($"补齐资源文件（{queue.Jobs.Count} 个）");
            var size = queue.TotalBytes;
            queue.Changed += () => task.SetProgress(queue.DoneBytes, Math.Max(size, queue.DoneBytes), queue.Speed);

            var failed = await queue.RunAsync(16, ct).ConfigureAwait(false);
            if (failed > 0)
                Log.Warn($"资源补齐有 {failed} 个失败（可能网络问题，可重试）");

            return queue.Jobs.Count - failed;
        }
    }
}
