using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModDownload;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModMods
{
    /// <summary>一次安装的结果（给界面拼提示用）。</summary>
    public sealed class ModInstallResult
    {
        public List<string> Installed { get; } = new List<string>();
        public List<string> Skipped { get; } = new List<string>();
        public List<string> Failed { get; } = new List<string>();

        public string Summary()
        {
            if (Installed.Count == 0 && Failed.Count == 0 && Skipped.Count == 0)
                return "没有可安装的文件";

            var parts = new List<string>();
            if (Installed.Count > 0) parts.Add($"安装 {Installed.Count} 个");
            if (Skipped.Count > 0) parts.Add($"跳过 {Skipped.Count} 个（已存在）");
            if (Failed.Count > 0) parts.Add($"失败 {Failed.Count} 个");

            var text = string.Join("，", parts);
            if (Failed.Count > 0) text += "：" + string.Join("、", Failed.Take(3));
            return text;
        }
    }

    /// <summary>
    /// 把模组装进某个版本的 <c>mods\</c>。
    ///
    /// 依赖处理只做"必需的"（Modrinth 的 <c>dependency_type = required</c>）：
    /// 广度遍历最多两轮，按 <c>projectId</c> 去重；同名文件已存在就跳过，
    /// 这样重复安装同一个模组不会重复下载，也不会覆盖用户手改过的文件。
    /// </summary>
    public static class ModInstaller
    {
        public static async Task<ModInstallResult> InstallAsync(string gameDir, string versionId,
            string mcVersion, string loader, ModSearchHit hit, CancellationToken ct = default,
            Action<string> progress = null)
        {
            if (hit == null) throw new ArgumentNullException(nameof(hit));

            var result = new ModInstallResult();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 待装队列：(来源, 项目 ID, 显示名)
            var queue = new Queue<(ModSearchHit Hit, int Depth)>();
            queue.Enqueue((hit, 0));
            visited.Add(hit.Source + ":" + hit.ProjectId);

            var files = new List<ModFileRef>();

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var (cur, depth) = queue.Dequeue();
                progress?.Invoke($"解析 {cur.Title}…");

                var list = await ListFilesAsync(cur, mcVersion, loader, ct).ConfigureAwait(false);
                if (list.Count == 0)
                {
                    if (depth == 0)
                        throw new InvalidOperationException(
                            $"「{cur.Title}」没有适用于 {mcVersion} / {loader} 的文件。\n" +
                            "可能是这个模组不支持该版本或该加载器。");

                    result.Skipped.Add(cur.Title + "（无匹配文件）");
                    Log.Warn($"依赖 {cur.Title} 没有适用于 {mcVersion}/{loader} 的文件，跳过");
                    continue;
                }

                files.Add(list[0]);

                // 依赖只对 Modrinth 做：CurseForge 的文件级依赖需要再多一次请求，
                // 且作者常把依赖写在描述里而不是元数据里，收益低。
                if (cur.Source != "Modrinth" || depth >= 2) continue;

                var deps = await ModrinthSource.RequiredDepsAsync(cur.ProjectId, list[0].VersionLabel,
                    mcVersion, loader, ct).ConfigureAwait(false);

                foreach (var (depId, depName) in deps)
                {
                    var key = "Modrinth:" + depId;
                    if (!visited.Add(key)) continue;

                    var title = string.IsNullOrWhiteSpace(depName)
                        ? await ModrinthSource.ProjectTitleAsync(depId, ct).ConfigureAwait(false)
                        : depName;

                    queue.Enqueue((new ModSearchHit
                    {
                        Source = "Modrinth",
                        ProjectId = depId,
                        Title = title
                    }, depth + 1));
                }
            }

            // 下载
            var modsDir = ModManager.ModsDir(gameDir, versionId);
            Directory.CreateDirectory(modsDir);

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();

                var target = Path.Combine(modsDir, f.FileName);

                if (File.Exists(target))
                {
                    result.Skipped.Add(f.FileName);
                    continue;
                }

                progress?.Invoke($"下载 {f.FileName}（{Format.Bytes(f.Size)}）");

                // 国内网络经代理常只有几十 KB/s，按体积给足时间（20 KB/s 估算，下限 5 分钟，上限 15 分钟）
                var timeoutMs = f.Size > 0
                    ? (int)Math.Min(900000, Math.Max(300000, f.Size / 1024 / 20 * 1000))
                    : 600000;

                var ok = false;
                for (var attempt = 0; attempt < 2 && !ok; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await Net.DownloadToFileAsync(f.Url, target, f.Sha1, f.Size, ct, timeoutMs)
                            .ConfigureAwait(false);
                        ok = true;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 0)
                        {
                            Log.Warn($"模组下载失败，重试一次：{f.FileName}（{ex.Message}）");
                            progress?.Invoke($"下载失败，重试 {f.FileName}…");
                        }
                        else
                        {
                            result.Failed.Add(f.FileName);
                            Log.Warn($"模组下载失败 {f.FileName}：{ex.Message}");
                        }
                    }
                }

                if (ok)
                {
                    result.Installed.Add(f.FileName);
                    Log.Info($"模组已安装：{f.FileName}（{f.Source} {f.ProjectId}）");
                }
            }

            return result;
        }

        private static Task<List<ModFileRef>> ListFilesAsync(ModSearchHit hit, string mc, string loader,
            CancellationToken ct)
        {
            return hit.Source == "CurseForge"
                ? CurseForgeSource.ListFilesAsync(hit.ProjectId, mc, loader, ct)
                : ModrinthSource.ListFilesAsync(hit.ProjectId, mc, loader, ct);
        }

        /// <summary>由版本 ID 推断 MC 版本与加载器（用于界面自动带出过滤条件）。</summary>
        public static (string McVersion, string Loader) ResolveTarget(string gameDir, string versionId)
        {
            try
            {
                var v = VersionManager.Scan(gameDir).FirstOrDefault(x =>
                    string.Equals(x.Id, versionId, StringComparison.OrdinalIgnoreCase));

                if (v == null) return ("", "");

                // Loader 是给人看的中文标签（原版 / Fabric / Forge / NeoForge），这里归一化成 API 用语。
                // 注意 neoforge 要先于 forge 判断，否则会被 "forge" 抢先匹配。
                var label = (v.Loader ?? "").ToLowerInvariant();
                var loader = label.Contains("fabric") ? "fabric"
                    : label.Contains("neoforge") ? "neoforge"
                    : label.Contains("forge") ? "forge"
                    : label.Contains("quilt") ? "quilt"
                    : "";

                return (v.BaseVersion ?? "", loader);
            }
            catch (Exception ex)
            {
                Log.Warn($"推断 {versionId} 的 MC 版本失败：{ex.Message}");
                return ("", "");
            }
        }
    }
}
