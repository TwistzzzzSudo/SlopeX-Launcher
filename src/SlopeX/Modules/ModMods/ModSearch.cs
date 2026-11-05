using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModMods
{
    /// <summary>搜索来源筛选。</summary>
    public enum ModSourceFilter
    {
        /// <summary>两个源一起搜；Modrinth 侧走 MCIM 镜像，与 CurseForge 保持同一个出口。</summary>
        All,

        /// <summary>只搜 Modrinth，并且走**官方**接口（数据最新，中文经 MC百科 映射）。</summary>
        Modrinth,

        /// <summary>只搜 CurseForge（必须经 MCIM 镜像，官方接口要 Key）。</summary>
        CurseForge
    }

    /// <summary>一次搜索的完整结果，兼带给界面拼状态文字的信息。</summary>
    public sealed class ModSearchResult
    {
        public List<ModSearchHit> Hits { get; } = new List<ModSearchHit>();

        /// <summary>是否走了 MC百科。</summary>
        public bool FromMcMod { get; set; }

        /// <summary>本次用的来源筛选。</summary>
        public ModSourceFilter Filter { get; set; }

        /// <summary>来源命中的总条数（百科一页 32 条；英文搜索时等于两源条数之和）。</summary>
        public int Total { get; set; }

        /// <summary>实际核对过的条数（只核对前若干条，每条要读一次百科页）。</summary>
        public int Checked { get; set; }

        /// <summary>百科有、但两个平台都没有（或者作者已删除）的条数。</summary>
        public int NotOnPlatform { get; set; }

        /// <summary>平台上有，但没有目标 MC 版本 / 加载器版本的条数。</summary>
        public int Incompatible { get; set; }

        /// <summary>合并前各自命中多少（英文多源搜索用）。</summary>
        public int ModrinthCount { get; set; }
        public int CurseForgeCount { get; set; }

        /// <summary>关键词为空、按下载量浏览榜单（不是搜索）。状态文案据此换个说法。</summary>
        public bool Browsing { get; set; }

        public string StatusText(string mcVersion, string loader)
        {
            var target = TargetLabel(mcVersion, loader);

            if (Browsing && !FromMcMod)
            {
                if (Hits.Count == 0)
                    return "没能列出热门模组，检查网络后点「搜索」重试。";

                return $"下载量最高的 {Hits.Count} 个模组。已按 {target} 过滤。";
            }

            if (!FromMcMod)
            {
                if (Hits.Count == 0)
                {
                    return Filter == ModSourceFilter.All
                        ? "两个平台都没有找到结果。试试英文原名，或者换个关键词。"
                        : $"{(Filter == ModSourceFilter.CurseForge ? "CurseForge" : "Modrinth")} 上没有找到结果。" +
                          "可以切到「全部」看看另一个平台有没有。";
                }

                var detail = Filter switch
                {
                    ModSourceFilter.All =>
                        $"Modrinth {ModrinthCount} 个、CurseForge {CurseForgeCount} 个，去重后 {Hits.Count} 个" +
                        "（Modrinth 在前，CurseForge 补充在后）",
                    ModSourceFilter.CurseForge => $"CurseForge 共 {Hits.Count} 个",
                    _ => $"Modrinth 共 {Hits.Count} 个"
                };

                return $"找到 {detail}。已按 {target} 过滤。";
            }

            if (Total == 0)
                return "MC百科 没搜到这个关键词的模组。可以试试别的说法，比如用完整名字「机械动力」而不是「机械」。";

            if (Hits.Count == 0)
            {
                return Checked == 0
                    ? $"MC百科 命中 {Total} 个，但没有能对应到{(Filter == ModSourceFilter.CurseForge ? " CurseForge" : " Modrinth")} 的条目。"
                    : $"MC百科 命中 {Total} 个；核对前 {Checked} 个，平台上都找不到，或者不支持 {target}。";
            }

            var tail = new List<string>();
            if (NotOnPlatform > 0) tail.Add($"{NotOnPlatform} 个平台上没有");
            if (Incompatible > 0) tail.Add($"{Incompatible} 个没有 {target} 版本");

            var text = $"MC百科 命中 {Total} 个，核对前 {Checked} 个，其中 {Hits.Count} 个可安装" +
                       (tail.Count > 0 ? "（" + string.Join("，", tail) + "）" : "") + "。";

            if (Filter == ModSourceFilter.All && CurseForgeCount > 0)
                text += " 同一个模组两边都有时优先取 CurseForge。";

            // 版本号读不出来时等于没过滤，装上去很可能是别的版本的 jar，得说清楚
            if (string.IsNullOrEmpty(mcVersion))
                text += " 注意：读不出这个版本的 Minecraft 版本号，列表没有按版本过滤。";

            return text;
        }

        private static string TargetLabel(string mcVersion, string loader)
        {
            if (string.IsNullOrEmpty(loader)) return mcVersion;
            if (string.IsNullOrEmpty(mcVersion)) return loader;

            return mcVersion + " / " + loader;
        }
    }

    /// <summary>
    /// 搜索入口：决定这次查询该问谁。
    ///
    /// 三条路（对应界面上的三选一）：
    /// <list type="bullet">
    /// <item><b>Modrinth</b>：走官方 <c>api.modrinth.com</c>，数据最新；中文经 MC百科 映射到 Modrinth 项目。</item>
    /// <item><b>CurseForge</b>：只能走 MCIM 镜像（官方要 <c>x-api-key</c>）；中文经 MC百科 映射到 CF 的 slug。</item>
    /// <item><b>全部</b>：两源一起搜，Modrinth 侧也改走 MCIM 镜像 —— 既然已经依赖镜像取 CF，
    ///       就让两边共用同一个出口，少一次跨境请求。</item>
    /// </list>
    ///
    /// 合并顺序是「Modrinth 在前、CurseForge 补在后面（同名去重）」：
    /// Modrinth 的相关度排序质量明显更好，CF 的搜索只能按热度排；
    /// 这样既保留了 Modrinth 的排序，又能让只在 CF 上架的模组出现在列表里。
    ///
    /// 百科只负责"这个名字对应哪个项目"，文件列表、依赖、下载仍然全部由各自的平台接口完成。
    /// </summary>
    public static class ModSearch
    {
        /// <summary>要核对多少条百科结果。每条要读一次详情页，32 条全读没必要。</summary>
        public const int CheckLimit = 12;

        private const int ParallelPages = 6;

        public static bool HasChinese(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;

            foreach (var c in s)
            {
                // 基本区 + 扩展 A，覆盖常用汉字足够
                if ((c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF))
                    return true;
            }

            return false;
        }

        public static async Task<ModSearchResult> SearchAsync(string query, string mcVersion, string loader,
            ModSourceFilter filter, int limit = CheckLimit, CancellationToken ct = default,
            string index = "relevance")
        {
            if (!HasChinese(query))
                return await SearchDirectAsync(query, mcVersion, loader, filter, limit, ct, index).ConfigureAwait(false);

            return await SearchChineseAsync(query, mcVersion, loader, filter, limit, ct).ConfigureAwait(false);
        }

        // ---------------- 英文：直接问平台 ----------------

        private static async Task<ModSearchResult> SearchDirectAsync(string query, string mcVersion, string loader,
            ModSourceFilter filter, int limit, CancellationToken ct, string index)
        {
            // 空关键词 + downloads = 浏览「下载量最高的模组」
            var browsing = string.IsNullOrWhiteSpace(query) && index == "downloads";

            var modrinthTask = filter != ModSourceFilter.CurseForge
                ? ModrinthSource.SearchAsync(query, mcVersion, loader, limit, 0, ct, filter == ModSourceFilter.All, index)
                : Task.FromResult(new List<ModSearchHit>());

            // CF 没有「相关度」可选，浏览时换成总下载量，否则用热度
            var curseforgeTask = filter != ModSourceFilter.Modrinth
                ? CurseForgeSource.SearchAsync(query, mcVersion, loader, limit, ct,
                    CurseForgeSource.ClassMods,
                    browsing ? CurseForgeSource.SortDownloads : CurseForgeSource.SortPopularity)
                : Task.FromResult(new List<ModSearchHit>());

            // 一个源挂了不该让整次搜索失败：另一个的结果照样能用
            var modrinth = await SafeAsync(modrinthTask, "Modrinth").ConfigureAwait(false);
            var curseforge = await SafeAsync(curseforgeTask, "CurseForge").ConfigureAwait(false);

            var result = new ModSearchResult
            {
                Filter = filter,
                Browsing = browsing,
                ModrinthCount = modrinth.Count,
                CurseForgeCount = curseforge.Count,
                Total = modrinth.Count + curseforge.Count,
                Checked = modrinth.Count + curseforge.Count
            };

            // Modrinth 的顺序更可信，先放；CF 只补它没有的（按标题归一化去重）
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hit in modrinth)
            {
                if (seen.Add(hit.DedupeKey)) result.Hits.Add(hit);
            }

            foreach (var hit in curseforge)
            {
                if (seen.Add(hit.DedupeKey)) result.Hits.Add(hit);
            }

            // 榜单模式要的是「整体按下载量从高到低」：两个源的排序规则不同，合并后统一再排一次
            if (browsing)
                result.Hits.Sort((a, b) => b.Downloads.CompareTo(a.Downloads));

            return result;
        }

        private static async Task<List<ModSearchHit>> SafeAsync(Task<List<ModSearchHit>> task, string name)
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"{name} 搜索失败：{ex.Message}");
                return new List<ModSearchHit>();
            }
        }

        // ---------------- 中文：经 MC百科 映射 ----------------

        private static async Task<ModSearchResult> SearchChineseAsync(string query, string mcVersion, string loader,
            ModSourceFilter filter, int limit, CancellationToken ct)
        {
            var entries = await McModSource.SearchAsync(query, ct).ConfigureAwait(false);

            var result = new ModSearchResult { FromMcMod = true, Filter = filter, Total = entries.Count };
            if (entries.Count == 0) return result;

            var batch = entries.Take(Math.Max(1, limit > 0 ? limit : CheckLimit)).ToList();
            result.Checked = batch.Count;

            var slots = new ModSearchHit[batch.Count];
            var reasons = new string[batch.Count];

            // 详情页基本不限流，六条并发能把 12 条压到两秒左右；
            // 再高意义不大，反而容易被站点当成爬虫。
            using var gate = new SemaphoreSlim(ParallelPages);

            await Task.WhenAll(batch.Select(async (entry, index) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var (modrinthSlug, curseforgeSlug) = await McModSource.LinksAsync(entry.ClassId, ct)
                        .ConfigureAwait(false);

                    // 「全部」优先试 CurseForge：这次要捞回来的主要就是只在 CF 上架的模组。
                    // 单选 Modrinth 时完全不碰 CF，保证那条链路和以前一致。
                    if (filter != ModSourceFilter.Modrinth && !string.IsNullOrEmpty(curseforgeSlug))
                    {
                        var cf = await CurseForgeSource.SearchBySlugAsync(curseforgeSlug, ct).ConfigureAwait(false);
                        if (cf != null)
                        {
                            var files = await CurseForgeSource.ListFilesAsync(cf.ProjectId, mcVersion, loader, ct)
                                .ConfigureAwait(false);

                            if (files.Count == 0)
                            {
                                reasons[index] = "ver";
                                return;
                            }

                            cf.Title = entry.Name;      // 百科的中文名比 CF 的英文名好认
                            if (!string.IsNullOrEmpty(entry.Intro)) cf.Description = entry.Intro;
                            cf.McModId = entry.ClassId;
                            cf.GameVersions.Clear();

                            slots[index] = cf;
                            return;
                        }
                    }

                    if (filter == ModSourceFilter.CurseForge)
                    {
                        reasons[index] = "no";
                        return;
                    }

                    if (string.IsNullOrEmpty(modrinthSlug))
                    {
                        reasons[index] = "no";
                        return;
                    }

                    var project = await ModrinthSource.ProjectAsync(modrinthSlug, ct,
                        filter == ModSourceFilter.All).ConfigureAwait(false);
                    if (project == null)
                    {
                        reasons[index] = "no";
                        return;
                    }

                    // 百科"模组"列表偶尔也收数据包/资源包；装进 mods 目录只会被忽略
                    if (!string.IsNullOrEmpty(project.ProjectType) &&
                        !string.Equals(project.ProjectType, "mod", StringComparison.OrdinalIgnoreCase))
                    {
                        reasons[index] = "no";
                        return;
                    }

                    if (!project.Supports(mcVersion, loader))
                    {
                        reasons[index] = "ver";
                        return;
                    }

                    slots[index] = new ModSearchHit
                    {
                        Source = "Modrinth",
                        ProjectId = string.IsNullOrEmpty(project.Id) ? modrinthSlug : project.Id,
                        Slug = string.IsNullOrEmpty(project.Slug) ? modrinthSlug : project.Slug,
                        Title = entry.Name,
                        Description = string.IsNullOrEmpty(entry.Intro) ? project.Description : entry.Intro,
                        Downloads = project.Downloads,
                        IconUrl = project.IconUrl,
                        McModId = entry.ClassId,
                        Mirror = filter == ModSourceFilter.All
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    reasons[index] = "no";
                    Log.Warn($"核对 MC百科 条目 {entry.ClassId}（{entry.Name}）失败：{ex.Message}");
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);

            // 按百科的排序输出；两个词条指向同一个项目时只留第一个
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null)
                {
                    if (reasons[i] == "ver") result.Incompatible++;
                    else result.NotOnPlatform++;
                    continue;
                }

                if (!used.Add(slots[i].Source + ":" + slots[i].ProjectId)) continue;
                result.Hits.Add(slots[i]);
            }

            result.ModrinthCount = result.Hits.Count(h => !string.Equals(h.Source, "CurseForge",
                StringComparison.OrdinalIgnoreCase));
            result.CurseForgeCount = result.Hits.Count - result.ModrinthCount;

            return result;
        }
    }
}
