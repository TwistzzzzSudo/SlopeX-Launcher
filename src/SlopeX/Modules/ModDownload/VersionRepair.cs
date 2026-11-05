using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
    /// <summary>一次内容校验的统计结果。</summary>
    public sealed class HashCheckResult
    {
        /// <summary>哈希比对通过的文件数。</summary>
        public int Checked { get; private set; }

        /// <summary>读过的总字节——用来解释这次体检为什么慢。</summary>
        public long Bytes { get; private set; }

        /// <summary>清单没给 SHA1、无法校验的文件数（第三方 maven 常见）。</summary>
        public int Skipped { get; private set; }

        /// <summary>哈希不符的文件，每项是「相对路径：期望 vs 实际」。</summary>
        public List<string> Broken { get; } = new List<string>();

        internal void Add(string name, string fullPath, string expected, CancellationToken ct)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
                if (!info.Exists) return;          // 缺失由 VersionRepair.Check 报，这里不重复
            }
            catch
            {
                return;
            }

            if (string.IsNullOrEmpty(expected)) { Skipped++; return; }

            ct.ThrowIfCancellationRequested();

            string actual;
            try
            {
                actual = Sha1Of(fullPath);
            }
            catch (Exception ex)
            {
                Broken.Add($"{name}：读取失败（{ex.Message}）");
                return;
            }

            Bytes += info.Length;

            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                Checked++;
                return;
            }

            Broken.Add($"{name}：期望 {expected}，实际 {actual}");
        }

        /// <summary>流式算 SHA1，1 GB 的文件也只占 64 KB 缓冲。</summary>
        private static string Sha1Of(string path)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);

            var buffer = new byte[64 * 1024];
            int n;
            while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, n);

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
    }

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
        /// 全量内容校验：逐个比对客户端 jar 与依赖库的 SHA1。
        ///
        /// 和 <see cref="Check"/> 的分工是「内容对不对」与「在不在」——后者只看文件存在性、装完就能跑；
        /// 这里要真的读一遍盘（原版一个版本约 1 GB），所以只在怀疑文件损坏时才调。
        /// 缺失的文件不在这里算，仍由 <see cref="Check"/> 报。
        /// </summary>
        /// <param name="includeAssets">连资源对象一起查（上千个小文件，占大部分字节）。</param>
        public static HashCheckResult CheckHashes(string gameDir, string versionId, bool includeAssets,
            CancellationToken ct = default)
        {
            var resolved = VersionManager.Resolve(gameDir, versionId);
            var libsDir = Paths.LibrariesDir(gameDir);
            var result = new HashCheckResult();

            // ---- 客户端主文件 ----
            if (!string.IsNullOrEmpty(resolved.JarPath))
                result.Add("客户端主文件", resolved.JarPath, resolved.ClientSha1, ct);

            // ---- 依赖库 + 原生库 ----
            foreach (var lib in resolved.Libraries)
            {
                ct.ThrowIfCancellationRequested();

                var arch = JavaFinder.ArchOfCurrentProcess;
                if (!Rules.Allowed(J.Arr(lib, "rules"), new Dictionary<string, bool>(), arch)) continue;

                var downloads = J.Obj(lib, "downloads");

                var artifact = J.Obj(downloads, "artifact");
                var rel = J.Str(artifact, "path") ?? LaunchBuilder.MavenPath(J.Str(lib, "name"));
                if (!string.IsNullOrEmpty(rel))
                    result.Add(rel,
                        Path.Combine(libsDir, rel.Replace('/', Path.DirectorySeparatorChar)),
                        J.Str(artifact, "sha1"), ct);

                var classifier = J.Str(J.Obj(lib, "natives"), "windows");
                if (string.IsNullOrEmpty(classifier)) continue;

                var native = J.Obj(J.Obj(downloads, "classifiers"), classifier);
                var natPath = J.Str(native, "path");
                if (string.IsNullOrEmpty(natPath)) continue;

                result.Add(natPath,
                    Path.Combine(libsDir, natPath.Replace('/', Path.DirectorySeparatorChar)),
                    J.Str(native, "sha1"), ct);
            }

            // ---- 资源对象 ----
            if (includeAssets)
            {
                var indexFile = Path.Combine(Paths.AssetsDir(gameDir), "indexes",
                    resolved.EffectiveAssetIndex + ".json");
                if (File.Exists(indexFile))
                {
                    try
                    {
                        var objectsDir = Path.Combine(Paths.AssetsDir(gameDir), "objects");
                        var objects = J.Obj(JsonNode.Parse(File.ReadAllText(indexFile)), "objects");

                        if (objects != null)
                            foreach (var kv in objects)
                            {
                                ct.ThrowIfCancellationRequested();

                                var hash = J.Str(kv.Value, "hash");
                                if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;

                                var sub = hash.Substring(0, 2);
                                result.Add("assets/" + sub + "/" + hash,
                                    Path.Combine(objectsDir, sub, hash), hash, ct);
                            }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // 索引坏了不该让整条命令崩——默认的 verify 会单独把这条报出来
                        Log.Warn("资源索引解析失败，跳过资源对象校验：" + ex.Message);
                    }
                }
            }

            return result;
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
