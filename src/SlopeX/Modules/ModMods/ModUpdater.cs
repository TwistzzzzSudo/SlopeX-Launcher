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

        /// <summary>这条更新是哪边给的：Modrinth / CurseForge。两边 ID 体系不同，日志里要能分辨。</summary>
        public string Source { get; set; } = "Modrinth";

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
    /// 两条链路，互补：
    /// <list type="number">
    /// <item>**Modrinth**：拿每个 jar 的 SHA1 批量问 <c>/v2/version_files/update</c>，一次问 50 个，最快。</item>
    /// <item>**CurseForge**：CF 没有对应的批量接口，只能先算**文件指纹**（<see cref="JarFingerprint"/>）
    /// 去 <c>/v1/fingerprints</c> 反查"本地这是哪个项目的哪个文件"，再问该项目有没有更新的文件。</item>
    /// </list>
    ///
    /// 分派方式是"Modrinth 认得的就不问 CF"：CF 那一步每个模组都要单独发一次请求，
    /// 只对 Modrinth 查不到的 jar 做，请求数就从"全部"降到"CF 独占的那些"。
    /// 两条都没认出来的（自己编译的、改过的、只在自己网站发布的）安静跳过，不误报。
    /// </summary>
    public static class ModUpdater
    {
        /// <summary>一批问多少个。服务端能吃更多，但大批次失败时整批都要重来，取 50 折中。</summary>
        private const int BatchSize = 50;

        /// <summary>同时问几个 CF 项目。镜像站是社区服务，压太狠不合适。</summary>
        private const int CfConcurrency = 4;

        /// <summary>
        /// 扫描目录并查更新。只返回**确实有新版本**的条目：
        /// 已是最好版本的、两边都没上架的、读不出元数据的，都不出现在结果里。
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

            // Modrinth 认得（返回了版本对象）的 jar —— 包括"已是最新"那些，它们不必再问 CF
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    known.Add(pair.Key);

                    if (!byHash.TryGetValue(pair.Key, out var local)) continue;

                    var update = Build(local, pair.Key, pair.Value);
                    if (update != null) result.Add(update);
                }
            }

            var rest = byHash.Where(p => !known.Contains(p.Key)).Select(p => p.Value).ToList();
            await CheckCurseForgeAsync(rest, mcVersion, loader, result, ct).ConfigureAwait(false);

            return result.OrderBy(u => u.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// CurseForge 侧：指纹反查 → 逐个项目看有没有更新的文件。
        /// 单个项目失败只丢它一个，不影响整次检查。
        /// </summary>
        private static async Task CheckCurseForgeAsync(List<JarMod> candidates, string mcVersion, string loader,
            List<ModUpdate> result, CancellationToken ct)
        {
            if (candidates.Count == 0) return;

            var byFingerprint = new Dictionary<long, JarMod>();
            foreach (var m in candidates)
            {
                var fp = JarFingerprint.OfFile(m.FilePath);
                if (fp == 0) continue;
                byFingerprint.TryAdd(fp, m);
            }

            if (byFingerprint.Count == 0) return;

            var keys = byFingerprint.Keys.ToList();
            var tasks = new List<Task<ModUpdate>>();
            using var gate = new SemaphoreSlim(CfConcurrency, CfConcurrency);

            for (var i = 0; i < keys.Count; i += CurseForgeSource.FingerprintBatch)
            {
                ct.ThrowIfCancellationRequested();

                var batch = keys.Skip(i).Take(CurseForgeSource.FingerprintBatch).ToList();

                Dictionary<long, CurseForgeSource.CfMatch> matches;
                try
                {
                    matches = await CurseForgeSource.MatchFingerprintsAsync(batch, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warn($"CurseForge 指纹反查失败（第 {i / CurseForgeSource.FingerprintBatch + 1} 批，" +
                             $"{batch.Count} 个）：{ex.Message}");
                    continue;
                }

                foreach (var pair in matches)
                {
                    if (!byFingerprint.TryGetValue(pair.Key, out var local)) continue;
                    tasks.Add(CheckOneAsync(local, pair.Value, mcVersion, loader, gate, ct));
                }
            }

            if (tasks.Count == 0) return;

            foreach (var update in await Task.WhenAll(tasks).ConfigureAwait(false))
            {
                if (update != null) result.Add(update);
            }
        }

        private static async Task<ModUpdate> CheckOneAsync(JarMod local, CurseForgeSource.CfMatch match,
            string mcVersion, string loader, SemaphoreSlim gate, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                var latest = await CurseForgeSource
                    .LatestFileAsync(match.ProjectId, mcVersion, loader, ct).ConfigureAwait(false);

                if (latest == null) return null;

                // 最新的就是本地这一份 → 已是最新
                if (string.Equals(latest.FileName, match.FileName, StringComparison.OrdinalIgnoreCase)) return null;

                // 时间上也得更晚：作者补传旧分支时列表顺序会变，只比文件名会误报"降级"
                if (latest.FileDate.HasValue && match.FileDate.HasValue &&
                    latest.FileDate.Value <= match.FileDate.Value) return null;

                if (string.IsNullOrEmpty(latest.Sha1) || string.IsNullOrEmpty(latest.Url)) return null;

                return new ModUpdate
                {
                    FileName = local.FileName,
                    ModId = local.ModId,
                    ProjectId = match.ProjectId,
                    Source = CurseForgeSource.SourceName,
                    CurrentVersion = local.Version,
                    LatestVersion = latest.VersionLabel,
                    LatestFileName = latest.FileName,
                    DownloadUrl = latest.Url,
                    Sha1 = latest.Sha1,
                    Size = latest.Size
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"查 CurseForge 更新失败 {local.FileName}：{ex.Message}");
                return null;
            }
            finally
            {
                gate.Release();
            }
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

            var latestNumber = J.Str(version, "version_number") ?? "";

            // 版本号一模一样、只是另一份构建（典型：同一版本号为不同 MC 版本各传一个 jar，
            // 1.20.x 那个 jar 被换成了 1.19.2 那份）。这种"1.0.2 → 1.0.2"报出来只会让人困惑，
            // 装下去还可能是降级，直接不当更新。
            if (SameVersion(local.Version, latestNumber)) return null;

            var url = J.Str(picked, "url");
            if (string.IsNullOrEmpty(url)) return null;

            return new ModUpdate
            {
                FileName = local.FileName,
                ModId = local.ModId,
                ProjectId = J.Str(version, "project_id") ?? "",
                CurrentVersion = local.Version,
                LatestVersion = latestNumber,
                LatestFileName = J.Str(picked, "filename") ?? "",
                DownloadUrl = url,
                Sha1 = latestSha1,
                Size = J.Long(picked, "size")
            };
        }

        /// <summary>两个版本号是否同一个（忽略大小写与首尾空白）。任一为空都算"不同"。</summary>
        private static bool SameVersion(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
