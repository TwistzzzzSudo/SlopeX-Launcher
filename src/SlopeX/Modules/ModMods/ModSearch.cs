using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModMods
{
    /// <summary>一次搜索的完整结果，兼带给界面拼状态文字的信息。</summary>
    public sealed class ModSearchResult
    {
        public List<ModSearchHit> Hits { get; } = new List<ModSearchHit>();

        /// <summary>是否走了 MC百科。</summary>
        public bool FromMcMod { get; set; }

        /// <summary>来源命中的总条数（百科一页 32 条；英文搜索时等于 Hits 数）。</summary>
        public int Total { get; set; }

        /// <summary>实际核对过的条数（只核对前若干条，每条要读一次百科页）。</summary>
        public int Checked { get; set; }

        /// <summary>百科有、Modrinth 上没有（或者作者已删除）的条数。</summary>
        public int NotOnModrinth { get; set; }

        /// <summary>Modrinth 有，但没有目标 MC 版本 / 加载器版本的条数。</summary>
        public int Incompatible { get; set; }

        public string StatusText(string mcVersion, string loader)
        {
            if (!FromMcMod)
            {
                return Hits.Count == 0
                    ? "没有找到结果。"
                    : $"找到 {Hits.Count} 个结果（已按 Minecraft {mcVersion} / {loader} 过滤）。";
            }

            if (Total == 0)
                return "MC百科 没搜到这个关键词的模组。可以试试别的说法，比如用完整名字「机械动力」而不是「机械」。";

            if (Hits.Count == 0)
            {
                return Checked == 0
                    ? $"MC百科 命中 {Total} 个，但没有能对应到 Modrinth 的条目。"
                    : $"MC百科 命中 {Total} 个；核对前 {Checked} 个，都不在 Modrinth 上，或者不支持 {TargetLabel(mcVersion, loader)}。";
            }

            var tail = new List<string>();
            if (NotOnModrinth > 0) tail.Add($"{NotOnModrinth} 个 Modrinth 上没有");
            if (Incompatible > 0) tail.Add($"{Incompatible} 个没有 {TargetLabel(mcVersion, loader)} 版本");

            var text = $"MC百科 命中 {Total} 个，核对前 {Checked} 个，其中 {Hits.Count} 个可安装" +
                       (tail.Count > 0 ? "（" + string.Join("，", tail) + "）" : "") + "。";

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
    /// 含中文就交给 MC百科（用户搜的就是中文名），纯英文仍然直接问 Modrinth
    /// —— 那边元数据更全、没有限流，没必要绕一圈。
    ///
    /// 百科只负责"这个名字叫什么、对应哪个 Modrinth 项目"，
    /// 文件列表、依赖、下载全部还走 Modrinth，所以安装链路一行都没变。
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
            int limit = CheckLimit, CancellationToken ct = default)
        {
            if (!HasChinese(query))
            {
                var hits = await ModrinthSource.SearchAsync(query, mcVersion, loader, limit, 0, ct)
                    .ConfigureAwait(false);

                var direct = new ModSearchResult { Total = hits.Count, Checked = hits.Count };
                direct.Hits.AddRange(hits);
                return direct;
            }

            return await SearchChineseAsync(query, mcVersion, loader, limit, ct).ConfigureAwait(false);
        }

        private static async Task<ModSearchResult> SearchChineseAsync(string query, string mcVersion, string loader,
            int limit, CancellationToken ct)
        {
            var entries = await McModSource.SearchAsync(query, ct).ConfigureAwait(false);

            var result = new ModSearchResult { FromMcMod = true, Total = entries.Count };
            if (entries.Count == 0) return result;

            var batch = entries.Take(Math.Max(1, limit > 0 ? limit : CheckLimit)).ToList();
            result.Checked = batch.Count;

            var slots = new ModSearchHit[batch.Count];
            var reasons = new string[batch.Count];

            // 详情页基本不限流，四条并发能把 16 条压到两秒左右；
            // 再高意义不大，反而容易被站点当成爬虫。
            using var gate = new SemaphoreSlim(ParallelPages);

            await Task.WhenAll(batch.Select(async (entry, index) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var slug = await McModSource.ModrinthSlugAsync(entry.ClassId, ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(slug))
                    {
                        reasons[index] = "no";
                        return;
                    }

                    var project = await ModrinthSource.ProjectAsync(slug, ct).ConfigureAwait(false);
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
                        ProjectId = string.IsNullOrEmpty(project.Id) ? slug : project.Id,
                        Slug = string.IsNullOrEmpty(project.Slug) ? slug : project.Slug,
                        Title = entry.Name,
                        Description = string.IsNullOrEmpty(entry.Intro) ? project.Description : entry.Intro,
                        Downloads = project.Downloads,
                        IconUrl = project.IconUrl,
                        McModId = entry.ClassId
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
                    else result.NotOnModrinth++;
                    continue;
                }

                if (!used.Add(slots[i].ProjectId)) continue;
                result.Hits.Add(slots[i]);
            }

            return result;
        }
    }
}
