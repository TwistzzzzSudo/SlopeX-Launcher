using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModMods
{
    /// <summary>一个可更新的模组。</summary>
    public sealed class ModUpdate
    {
        public string FileName { get; set; } = "";
        public string ModId { get; set; } = "";
        public string ProjectId { get; set; } = "";

        /// <summary>本地 jar 里声明的版本（可能带 <c>+mc1.20.1</c> 这样的构建号）。</summary>
        public string CurrentVersion { get; set; } = "";

        public string LatestVersion { get; set; } = "";
        public string LatestFileName { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string Sha1 { get; set; } = "";
        public long Size { get; set; }

        public string Describe() =>
            $"{FileName}  {CurrentVersion} → {LatestVersion}  （{LatestFileName}）";
    }

    /// <summary>
    /// 检查已装模组有没有新版本。
    ///
    /// 只查 Modrinth：拿每个 jar 的文件 SHA1 批量问 <c>/v2/version_files/update</c>。
    /// CurseForge 的同类接口要按它的指纹算法（MurmurHash2 + 特定的条目遍历规则）算值，
    /// 算错一位就全查不到，所以这一版先不接——CF 来的模组会安静地跳过，不误报。
    /// </summary>
    public static class ModUpdater
    {
        /// <summary>一批问多少个。服务端能吃更多，但大批次失败时整批都要重来，取 50 折中。</summary>
        private const int BatchSize = 50;

        /// <summary>
        /// 扫描目录并查更新。只返回**确实有新版本**的条目：
        /// 已是最好版本的、不在 Modrinth 上架的、读不出元数据的，都不出现在结果里。
        /// </summary>
        public static async Task<List<ModUpdate>> CheckAsync(string modsDir, string mcVersion, string loader,
            CancellationToken ct = default, bool mirror = false)
        {
            var mods = ModJar.ScanDir(modsDir).Where(m => m.Ok).ToList();
            if (mods.Count == 0) return new List<ModUpdate>();

            // hash → (本地 jar, 该 hash)。一个文件一个 hash，重复文件只留第一个
            var byHash = new Dictionary<string, JarMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in mods)
            {
                var h = ModJar.Sha1Of(m.FilePath);
                if (h.Length == 0) continue;
                byHash.TryAdd(h, m);
            }

            if (byHash.Count == 0) return new List<ModUpdate>();

            var result = new List<ModUpdate>();
            var hashes = byHash.Keys.ToList();

            for (var i = 0; i < hashes.Count; i += BatchSize)
            {
                ct.ThrowIfCancellationRequested();

                var batch = hashes.Skip(i).Take(BatchSize).ToList();

                Dictionary<string, JsonObject> found;
                try
                {
                    found = await ModrinthSource
                        .CheckUpdatesAsync(batch, mcVersion, loader, ct, mirror).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 一批失败不该让整次检查归零：剩下的批次继续问
                    Log.Warn($"查更新失败（第 {i / BatchSize + 1} 批，{batch.Count} 个）：{ex.Message}");
                    continue;
                }

                foreach (var pair in found)
                {
                    if (!byHash.TryGetValue(pair.Key, out var local)) continue;

                    var update = Build(local, pair.Key, pair.Value);
                    if (update != null) result.Add(update);
                }
            }

            return result.OrderBy(u => u.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// 应用一条更新：先下到临时文件（完整性由下载引擎按 SHA1 校验），成功后再动旧文件。
        /// 顺序是「旧文件改名 → 新文件就位 → 删旧」，任何一步失败都能回退到原状，
        /// 不会出现「旧的删了、新的没下完」这种半成品。
        /// </summary>
        public static async Task ApplyAsync(ModUpdate update, string modsDir,
            Action<DownloadProgress> progress = null, CancellationToken ct = default)
        {
            if (update == null) throw new ArgumentNullException(nameof(update));

            var oldPath = Path.Combine(modsDir, update.FileName);
            var newPath = Path.Combine(modsDir, update.LatestFileName);
            var tmpPath = newPath + ".new";
            var bakPath = oldPath + ".old";

            try
            {
                var timeoutMs = update.Size > 0
                    ? (int)Math.Min(900000, Math.Max(300000, update.Size / 1024 / 20 * 1000))
                    : 600000;

                await Net.DownloadToFileAsync(update.DownloadUrl, tmpPath, update.Sha1, update.Size, ct,
                    timeoutMs, progress).ConfigureAwait(false);

                if (File.Exists(bakPath)) File.Delete(bakPath);
                if (File.Exists(oldPath)) File.Move(oldPath, bakPath);
                File.Move(tmpPath, newPath);
                if (File.Exists(bakPath)) File.Delete(bakPath);

                Log.Info($"模组已更新：{update.FileName} → {update.LatestFileName}");
            }
            catch
            {
                try
                {
                    if (File.Exists(bakPath) && !File.Exists(oldPath)) File.Move(bakPath, oldPath);
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);
                }
                catch (Exception ex)
                {
                    Log.Warn("更新回退失败：" + ex.Message);
                }

                throw;
            }
        }

        private static ModUpdate Build(JarMod local, string localHash, JsonObject version)
        {
            var files = J.Arr(version, "files");
            if (files == null) return null;

            JsonObject picked = null;
            foreach (var f in files)
            {
                if (f is JsonObject fo && J.Bool(fo, "primary")) { picked = fo; break; }
            }
            picked ??= files.FirstOrDefault() as JsonObject;
            if (picked == null) return null;

            var latestSha1 = J.Str(J.Obj(picked, "hashes"), "sha1") ?? "";
            if (string.IsNullOrEmpty(latestSha1)) return null;

            // 返回的那个版本的主文件就是本地这一份 → 已经是最新，不是更新
            if (string.Equals(latestSha1, localHash, StringComparison.OrdinalIgnoreCase)) return null;

            var url = J.Str(picked, "url");
            if (string.IsNullOrEmpty(url)) return null;

            return new ModUpdate
            {
                FileName = local.FileName,
                ModId = local.ModId,
                ProjectId = J.Str(version, "project_id") ?? "",
                CurrentVersion = local.Version,
                LatestVersion = J.Str(version, "version_number") ?? "",
                LatestFileName = J.Str(picked, "filename") ?? "",
                DownloadUrl = url,
                Sha1 = latestSha1,
                Size = J.Long(picked, "size")
            };
        }
    }
}
