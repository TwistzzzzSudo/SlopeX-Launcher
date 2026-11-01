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

        /// <summary>
        /// 装完后对 mods 目录做的体检（缺前置 / 版本不符 / 冲突 / 重复 / 坏包）。
        /// 失败时为 null——体检不该影响安装结果本身。
        /// </summary>
        public HealthReport Health { get; set; }

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
            if (Health != null && !Health.Clean) text += $"；体检：{Health.Summary()}";
            return text;
        }

        /// <summary>体检发现的前几条问题，给界面展开用。</summary>
        public List<string> HealthLines(int max = 3)
            => Health == null ? new List<string>() : Health.Issues.Take(max).Select(i => i.Message).ToList();
    }

    /// <summary>
    /// 把模组装进某个版本的 <c>mods\</c>。
    ///
    /// 依赖处理只做"必需的"（Modrinth 的 <c>dependency_type = required</c>）：
    /// 广度遍历最多两轮，按 <c>projectId</c> 去重；同名文件已存在就跳过，
    /// 这样重复安装同一个模组不会重复下载，也不会覆盖用户手改过的文件。
    ///
    /// 依赖**按声明钉版本**：Modrinth 的依赖项带 <c>version_id</c> 时直接装那一版，
    /// 而不是该项目的最新适配版——作者验证过的是那一版，最新版可能已越过区间。
    /// 声明版本与当前 MC 版本 / 加载器不匹配时回落到最新适配版（见 <see cref="FilesForAsync"/>）。
    /// </summary>
    public static class ModInstaller
    {
        public static async Task<ModInstallResult> InstallAsync(string gameDir, string versionId,
            string mcVersion, string loader, ModSearchHit hit, CancellationToken ct = default,
            Action<string> progress = null, Action<DownloadProgress> download = null)
        {
            if (hit == null) throw new ArgumentNullException(nameof(hit));

            // 进度回调来自界面时，它自己出错（典型是跨线程写控件）不该把整个安装带崩：
            // 下载是几十秒的事，界面文字没刷新远不如文件没落地严重。
            void Report(string msg)
            {
                if (progress == null) return;
                try { progress(msg); }
                catch (Exception ex) { Log.Warn("安装进度回调异常（已忽略）：" + ex.Message); }
            }

            var result = new ModInstallResult();

            // 已排入的项目 → 它被钉住的版本（null = 不钉，取最新适配版）。
            // 两个模组要求同一个前置的不同版本时保留先到的、记一条日志：
            // 这里是"别重复下载"，真正的区间校验交给后续的元数据体检。
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 待装队列：(项目, 深度, 钉住的版本 ID)
            var queue = new Queue<(ModSearchHit Hit, int Depth, string Pinned)>();
            queue.Enqueue((hit, 0, null));
            seen[hit.Source + ":" + hit.ProjectId] = null;

            var files = new List<ModFileRef>();

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var (cur, depth, pinned) = queue.Dequeue();
                Report($"解析 {cur.Title}…");

                var list = await FilesForAsync(cur, pinned, mcVersion, loader, ct).ConfigureAwait(false);
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

                // 依赖递归最多两层：够覆盖"模组 → 前置 → 前置的前置"这种常见结构，
                // 再深就容易把整包依赖链一次拖下来。
                if (depth >= 2) continue;

                var deps = await RequiredDepsAsync(cur, list[0], mcVersion, loader, ct).ConfigureAwait(false);

                foreach (var (depId, depVersion, depName) in deps)
                {
                    // 依赖跟着主模组走同一个平台：CF 的依赖是数字 modId，Modrinth 的是 base62，
                    // 两套 ID 不能互认，所以 Source 必须一起带上
                    var key = cur.Source + ":" + depId;
                    if (seen.TryGetValue(key, out var prev))
                    {
                        if (!string.IsNullOrEmpty(depVersion) && !string.IsNullOrEmpty(prev) &&
                            !string.Equals(prev, depVersion, StringComparison.Ordinal))
                        {
                            Log.Warn($"依赖 {depId} 被要求两个不同版本（{prev} / {depVersion}），保留先到的那个");
                        }
                        continue;
                    }
                    seen[key] = depVersion;

                    var title = depName;
                    if (string.IsNullOrWhiteSpace(title))
                        title = await TitleAsync(cur, depId, ct).ConfigureAwait(false);

                    queue.Enqueue((new ModSearchHit
                    {
                        Source = cur.Source,
                        ProjectId = depId,
                        Title = title,
                        Mirror = cur.Mirror
                    }, depth + 1, depVersion));
                }
            }

            // 下载
            var modsDir = ModManager.ModsDir(gameDir, versionId);
            Directory.CreateDirectory(modsDir);

            for (var index = 0; index < files.Count; index++)
            {
                ct.ThrowIfCancellationRequested();

                var f = files[index];
                var target = Path.Combine(modsDir, f.FileName);

                if (File.Exists(target))
                {
                    result.Skipped.Add(f.FileName);
                    continue;
                }

                Report($"下载 {index + 1}/{files.Count}  {f.FileName}（{Format.Bytes(f.Size)}）");

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
                        await Net.DownloadToFileAsync(f.Url, target, f.Sha1, f.Size, ct, timeoutMs, download)
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
                            Report($"下载失败，重试 {f.FileName}…");
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

            // 装完顺手体检：缺的东西（依赖下载失败、版本不符、与已装模组冲突）当场告诉用户，
            // 不必等他启动崩了再来查。只读 zip 元数据，几十毫秒；失败也不影响安装结果。
            try
            {
                result.Health = ModHealth.Check(modsDir);
            }
            catch (Exception ex)
            {
                Log.Warn("安装后体检失败（不影响安装结果）：" + ex.Message);
            }

            return result;
        }

        private static bool IsCurseForge(ModSearchHit hit) =>
            string.Equals(hit.Source, CurseForgeSource.SourceName, StringComparison.OrdinalIgnoreCase);

        private static Task<List<ModFileRef>> ListFilesAsync(ModSearchHit hit, string mc, string loader,
            CancellationToken ct)
            => IsCurseForge(hit)
                ? CurseForgeSource.ListFilesAsync(hit.ProjectId, mc, loader, ct)
                : ModrinthSource.ListFilesAsync(hit.ProjectId, mc, loader, ct, hit.Mirror);

        /// <summary>
        /// 取这个项目要装的文件。依赖声明里带了 <c>version_id</c> 时优先装那一版，
        /// 而不是"该项目的最新适配版"——模组作者验证过的就是那一版，
        /// 装最新版可能已经越过它要求的区间（典型如 Create ↔ Flywheel）。
        /// 声明版本不适配当前 MC 版本 / 加载器时回落到最新适配版，并留一条日志。
        /// </summary>
        private static async Task<List<ModFileRef>> FilesForAsync(ModSearchHit hit, string pinned, string mc,
            string loader, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(pinned) && !IsCurseForge(hit))
            {
                var file = await ModrinthSource.FileOfVersionAsync(pinned, hit.ProjectId, mc, loader, ct,
                    hit.Mirror).ConfigureAwait(false);

                if (file != null)
                {
                    Log.Info($"依赖「{hit.Title}」按声明锁定 {file.VersionLabel}（{file.FileName}）");
                    return new List<ModFileRef> { file };
                }

                Log.Warn($"依赖「{hit.Title}」声明的版本 {pinned} 与 {mc}/{loader} 不匹配，改用最新适配版");
            }

            return await ListFilesAsync(hit, mc, loader, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 取必需依赖。两个平台拿依赖的方式不同：
        /// Modrinth 要按版本号反查 version 的 <c>dependencies</c>；
        /// CurseForge 在文件对象里就直接给了 <c>relationType = 3</c> 的列表。
        /// </summary>
        private static async Task<List<(string Id, string VersionId, string Name)>> RequiredDepsAsync(
            ModSearchHit hit, ModFileRef file, string mc, string loader, CancellationToken ct)
        {
            if (IsCurseForge(hit))
            {
                // CF 的依赖只给数字 modId（relationType = 3），没有"要求的具体版本"，VersionId 留空
                var cf = await CurseForgeSource.RequiredDepsAsync(file, ct).ConfigureAwait(false);
                var mapped = new List<(string, string, string)>(cf.Count);
                foreach (var d in cf) mapped.Add((d.Id, null, d.Name));
                return mapped;
            }

            return await ModrinthSource.RequiredDepsAsync(file, mc, loader, ct, hit.Mirror).ConfigureAwait(false);
        }

        private static Task<string> TitleAsync(ModSearchHit hit, string projectId, CancellationToken ct)
            => IsCurseForge(hit)
                ? CurseForgeSource.ProjectTitleAsync(projectId, ct)
                : ModrinthSource.ProjectTitleAsync(projectId, ct, hit.Mirror);

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
