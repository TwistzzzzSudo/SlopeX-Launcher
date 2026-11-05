using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModMods
{
    /// <summary>一次整合包搜索的结果，兼带给界面拼状态文字的信息。</summary>
    public sealed class PackSearchResult
    {
        public List<ModSearchHit> Hits { get; } = new List<ModSearchHit>();

        /// <summary>是否走了 MC百科。</summary>
        public bool FromMcMod { get; set; }

        public ModSourceFilter Filter { get; set; }

        /// <summary>百科命中的总条数。</summary>
        public int Total { get; set; }

        /// <summary>实际核对过的百科条数（每条要读一次词条页）。</summary>
        public int Checked { get; set; }

        /// <summary>百科有、但两个平台都没有（多数是只挂了网盘的国产整合包）。</summary>
        public int NotOnPlatform { get; set; }

        public int ModrinthCount { get; set; }
        public int CurseForgeCount { get; set; }

        /// <summary>关键词为空、按下载量浏览（不是搜索）。</summary>
        public bool Browsing { get; set; }

        /// <summary>后面还有没有更多（翻页按钮用）。</summary>
        public bool HasMore { get; set; }

        public string StatusText()
        {
            if (!FromMcMod)
            {
                if (Hits.Count == 0)
                {
                    return Filter == ModSourceFilter.All
                        ? "两个平台都没有找到结果。试试整合包的英文名，或者换个关键词。"
                        : $"{(Filter == ModSourceFilter.CurseForge ? "CurseForge" : "Modrinth")} 上没有找到结果。" +
                          "可以切到「全部」看看另一个平台有没有。";
                }

                // 不报数量、不提排序规则：这些都是开发者口径，对挑包没有帮助
                return "点「安装」挑一个包版本就开始下载。";
            }

            if (Total == 0)
                return "MC百科 没搜到这个关键词的整合包。换个说法试试，比如用完整的包名。";

            var text = "点「安装」挑一个包版本就开始下载。";
            if (NotOnPlatform > 0) text = "有些词条只挂了网盘 / 论坛，装不了。" + text;
            return text;
        }
    }

    /// <summary>
    /// 整合包搜索：Modrinth + CurseForge，中文经 MC百科 映射。
    ///
    /// 与模组搜索（<see cref="ModSearch"/>）的三点差别：
    /// <list type="bullet">
    /// <item>不按 MC 版本 / 加载器过滤——整合包页是启动器级别的，不挂在某个已装版本上；
    ///       支持范围由列表里的「支持版本」列展示，具体装哪个版本由用户在浮层里挑。</item>
    /// <item>CurseForge 侧走 <c>classId=4471</c>（整合包分类），模组是 <c>6</c>，两套 ID 不通用。</item>
    /// <item>百科侧走全站检索的 <c>filter=2</c> 筛选，而不是模组那种 <c>modlist.html?key=</c>。</item>
    /// </list>
    ///
    /// 中文搜索的现实情况：国产整合包大多只在百科挂了网盘，没有平台项目，这部分会如实计入
    /// 「只在百科上有」；真正能下载的是在百科登记了 CurseForge / Modrinth 链接的那些。
    /// </summary>
    public static class PackSearch
    {
        /// <summary>核对多少条百科结果。每条要读一次词条页，整页 32 条全读没必要。</summary>
        public const int CheckLimit = 12;

        private const int ParallelPages = 5;

        public static async Task<PackSearchResult> SearchAsync(string query, ModSourceFilter filter,
            int limit = CheckLimit, CancellationToken ct = default)
        {
            if (!ModSearch.HasChinese(query))
                return await SearchDirectAsync(query, filter, limit, ct).ConfigureAwait(false);

            return await SearchChineseAsync(query, filter, limit, ct).ConfigureAwait(false);
        }

        /// <summary>翻页窗口上限：两源各取前 limit×page 条做归并，太深会把请求数拖爆。</summary>
        private const int PageWindowCap = 500;

        /// <summary>
        /// 分页搜索：取两源各自的前 <c>limit × page</c> 条，去重合并、排序后切出当前页。
        /// 每页正好 <paramref name="limit"/> 条，翻回上一页内容不变。
        /// </summary>
        public static async Task<PackSearchResult> SearchPageAsync(string query, ModSourceFilter filter,
            int limit, int page, CancellationToken ct = default)
        {
            limit = Math.Clamp(limit, 1, 50);
            page = Math.Max(1, page);

            // 中文走 MC百科 映射，条数本来就少，不分页
            if (ModSearch.HasChinese(query))
                return await SearchChineseAsync(query, filter, CheckLimit, ct).ConfigureAwait(false);

            var browsing = string.IsNullOrWhiteSpace(query);
            var need = Math.Min(limit * page, PageWindowCap);

            var modrinthTask = filter != ModSourceFilter.CurseForge
                ? ModSearch.WithTimeoutAsync(t => FetchModrinthAsync(query, need, t, browsing), ct, "Modrinth")
                : Task.FromResult(new List<ModSearchHit>());

            var curseforgeTask = filter != ModSourceFilter.Modrinth
                ? ModSearch.WithTimeoutAsync(t => FetchCurseForgeAsync(query, need, t, browsing), ct, "CurseForge")
                : Task.FromResult(new List<ModSearchHit>());

            var modrinth = await SafeAsync(modrinthTask, "Modrinth").ConfigureAwait(false);
            var curseforge = await SafeAsync(curseforgeTask, "CurseForge").ConfigureAwait(false);

            // 整合包与模组相反：CF 才是主战场，CF 在前、Modrinth 补在后
            var merged = new List<ModSearchHit>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hit in curseforge)
                if (seen.Add(hit.DedupeKey)) merged.Add(hit);
            foreach (var hit in modrinth)
                if (seen.Add(hit.DedupeKey)) merged.Add(hit);

            // 榜单要的是「整体按下载量」：两个源的排序规则不同，合并后统一重排
            if (browsing)
                merged.Sort((a, b) => b.Downloads.CompareTo(a.Downloads));

            var result = new PackSearchResult
            {
                Filter = filter,
                Browsing = browsing,
                ModrinthCount = modrinth.Count,
                CurseForgeCount = curseforge.Count,
                Total = merged.Count
            };

            var skip = (page - 1) * limit;
            foreach (var hit in merged.Skip(skip).Take(limit))
                result.Hits.Add(hit);

            result.HasMore = merged.Count >= need && result.Hits.Count == limit && need < PageWindowCap;
            return result;
        }

        private static async Task<List<ModSearchHit>> FetchModrinthAsync(string query, int count,
            CancellationToken ct, bool browsing)
        {
            var all = new List<ModSearchHit>();
            var offset = 0;

            // Modrinth 一次最多 100 条
            while (all.Count < count)
            {
                var (hits, total) = await ModrinthSource
                    .SearchByTypePageAsync(query, "modpack", 100, offset, ct,
                        browsing ? "downloads" : "relevance")
                    .ConfigureAwait(false);

                all.AddRange(hits);
                if (hits.Count == 0 || all.Count >= total) break;
                offset += hits.Count;
            }

            return all.Count > count ? all.GetRange(0, count) : all;
        }

        private static async Task<List<ModSearchHit>> FetchCurseForgeAsync(string query, int count,
            CancellationToken ct, bool browsing)
        {
            var all = new List<ModSearchHit>();
            var pageIndex = 0;

            // CF 一页最多 50 条；浏览时按总下载量排，搜索时按热度
            while (all.Count < count)
            {
                var (hits, total) = await CurseForgeSource
                    .SearchPageAsync(query, null, null, 50, pageIndex, ct, CurseForgeSource.ClassModpacks,
                        browsing ? CurseForgeSource.SortDownloads : CurseForgeSource.SortPopularity)
                    .ConfigureAwait(false);

                all.AddRange(hits);
                if (hits.Count == 0 || all.Count >= total) break;
                pageIndex++;
            }

            return all.Count > count ? all.GetRange(0, count) : all;
        }

        // ---------------- 英文：直接问平台 ----------------

        private static async Task<PackSearchResult> SearchDirectAsync(string query, ModSourceFilter filter,
            int limit, CancellationToken ct)
        {
            var modrinthTask = filter != ModSourceFilter.CurseForge
                ? ModrinthSource.SearchByTypeAsync(query, "modpack", limit, 0, ct)
                : Task.FromResult(new List<ModSearchHit>());

            var curseforgeTask = filter != ModSourceFilter.Modrinth
                ? CurseForgeSource.SearchAsync(query, null, null, limit, ct, CurseForgeSource.ClassModpacks)
                : Task.FromResult(new List<ModSearchHit>());

            // 一个源挂了不该让整次搜索失败
            var modrinth = await SafeAsync(modrinthTask, "Modrinth").ConfigureAwait(false);
            var curseforge = await SafeAsync(curseforgeTask, "CurseForge").ConfigureAwait(false);

            var result = new PackSearchResult
            {
                Filter = filter,
                ModrinthCount = modrinth.Count,
                CurseForgeCount = curseforge.Count,
                Total = modrinth.Count + curseforge.Count,
                Checked = modrinth.Count + curseforge.Count
            };

            // 整合包与模组相反：CF 才是主战场（ATM 这类整包基本只在 CF 上架），
            // Modrinth 的相关度排序在整合包上偏差很大——搜 all the mods 前几条全是
            // "All the MrCrayfish's Mods" 之类的小包，所以 CF 的排前面。
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hit in curseforge)
                if (seen.Add(hit.DedupeKey)) result.Hits.Add(hit);

            foreach (var hit in modrinth)
                if (seen.Add(hit.DedupeKey)) result.Hits.Add(hit);

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
                Log.Warn($"{name} 整合包搜索失败：{ex.Message}");
                return new List<ModSearchHit>();
            }
        }

        // ---------------- 中文：经 MC百科 映射 ----------------

        private static async Task<PackSearchResult> SearchChineseAsync(string query, ModSourceFilter filter,
            int limit, CancellationToken ct)
        {
            var entries = await McModSource.SearchPacksAsync(query, ct).ConfigureAwait(false);

            var result = new PackSearchResult { FromMcMod = true, Filter = filter, Total = entries.Count };
            if (entries.Count == 0) return result;

            var batch = entries.Take(Math.Max(1, limit > 0 ? limit : CheckLimit)).ToList();
            result.Checked = batch.Count;

            var slots = new ModSearchHit[batch.Count];
            var missing = new bool[batch.Count];

            using var gate = new SemaphoreSlim(ParallelPages);

            await Task.WhenAll(batch.Select(async (entry, index) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var (modrinthSlug, cfSlug) = await McModSource.PackLinksAsync(entry.PackId, ct)
                        .ConfigureAwait(false);

                    // 「全部」优先试 CurseForge：整合包的大头在 CF，Modrinth 只补它没有的
                    if (filter != ModSourceFilter.Modrinth && !string.IsNullOrEmpty(cfSlug))
                    {
                        var cf = await CurseForgeSource.SearchBySlugAsync(cfSlug, ct,
                            CurseForgeSource.ClassModpacks).ConfigureAwait(false);
                        if (cf != null)
                        {
                            Decorate(cf, entry);
                            slots[index] = cf;
                            return;
                        }
                    }

                    if (filter != ModSourceFilter.CurseForge && !string.IsNullOrEmpty(modrinthSlug))
                    {
                        var project = await ModrinthSource.ProjectAsync(modrinthSlug, ct).ConfigureAwait(false);

                        // 百科也可能把整合包链到资源包或模组项目上，类型不对就跳过
                        if (project != null && string.Equals(project.ProjectType, "modpack",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            var hit = new ModSearchHit
                            {
                                Source = "Modrinth",
                                ProjectId = string.IsNullOrEmpty(project.Id) ? modrinthSlug : project.Id,
                                Slug = string.IsNullOrEmpty(project.Slug) ? modrinthSlug : project.Slug,
                                Title = entry.Name,
                                Description = string.IsNullOrEmpty(entry.Intro) ? project.Description : entry.Intro,
                                Downloads = project.Downloads,
                                IconUrl = project.IconUrl,
                                McModId = entry.PackId,
                                McModKind = "modpack"
                            };

                            // 支持版本要带上：列表里的「支持版本」列靠它，缺了会显示成空
                            foreach (var v in project.GameVersions) hit.GameVersions.Add(v);

                            slots[index] = hit;
                            return;
                        }
                    }

                    // 没登记链接时用英文名兜底精确搜一次；只认标题或 slug 完全一致的，
                    // 否则「All the Mods」这类名字会把一堆改版包都捞进来
                    if (!string.IsNullOrWhiteSpace(entry.EnglishName) && filter != ModSourceFilter.Modrinth)
                    {
                        var guess = await CurseForgeSource.SearchAsync(entry.EnglishName, null, null, 5, ct,
                            CurseForgeSource.ClassModpacks).ConfigureAwait(false);

                        var exact = guess.FirstOrDefault(h =>
                            string.Equals(h.Title, entry.EnglishName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(h.Slug, entry.EnglishName.Replace(' ', '-'), StringComparison.OrdinalIgnoreCase));

                        if (exact != null)
                        {
                            Decorate(exact, entry);
                            slots[index] = exact;
                            return;
                        }
                    }

                    missing[index] = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    missing[index] = true;
                    Log.Warn($"核对 MC百科 整合包 {entry.PackId}（{entry.Name}）失败：{ex.Message}");
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null)
                {
                    result.NotOnPlatform++;
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

        /// <summary>百科的中文名与简介比平台原文好认，能覆盖就覆盖。</summary>
        private static void Decorate(ModSearchHit hit, McModPackEntry entry)
        {
            hit.Title = entry.Name;
            if (!string.IsNullOrWhiteSpace(entry.Intro)) hit.Description = entry.Intro;
            hit.McModId = entry.PackId;
            hit.McModKind = "modpack";
        }

        // ---------------- 版本列表 ----------------

        private static readonly Regex ServerPackRx =
            new Regex(@"(^|[-_ .])server(files)?([-_ .]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 列出某个整合包可下载的包版本。CF 的文件列表里混着服务端包（<c>ServerFiles-x.y.zip</c>），
        /// 装进启动器没有意义，这里剔掉。
        /// </summary>
        public static async Task<List<ModFileRef>> ListVersionsAsync(ModSearchHit hit, CancellationToken ct = default)
        {
            if (hit == null) throw new ArgumentNullException(nameof(hit));

            var files = string.Equals(hit.Source, "CurseForge", StringComparison.OrdinalIgnoreCase)
                ? await CurseForgeSource.ListFilesAsync(hit.ProjectId, null, null, ct).ConfigureAwait(false)
                : await ModrinthSource.ListFilesAsync(hit.ProjectId, null, null, ct).ConfigureAwait(false);

            return files
                .Where(f => !string.IsNullOrEmpty(f.Url))
                .Where(f => !ServerPackRx.IsMatch(f.FileName ?? ""))
                .ToList();
        }
    }
}
