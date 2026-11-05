using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModMods
{
    /// <summary>MC百科 搜索结果里的一条模组。</summary>
    public sealed class McModEntry
    {
        public string ClassId { get; set; } = "";
        public string Name { get; set; } = "";     // 站上的显示名，通常是「机械动力 (Create)」
        public string Intro { get; set; } = "";    // 一句话简介

        public string ClassUrl => McModSource.Host + "/class/" + ClassId + ".html";
    }

    /// <summary>MC百科 整合包词条（<c>/modpack/&lt;id&gt;.html</c>）。</summary>
    public sealed class McModPackEntry
    {
        /// <summary>词条 ID，也就是 <c>/modpack/1234.html</c> 里的数字。</summary>
        public string PackId { get; set; } = "";

        /// <summary>站上的显示名，通常形如「[ATM9] AllTheMods 9 (AllTheMods 9)」。</summary>
        public string Name { get; set; } = "";

        /// <summary>一句话简介（结果卡片里的 body）。</summary>
        public string Intro { get; set; } = "";

        public string PackUrl => McModSource.Host + "/modpack/" + PackId + ".html";

        /// <summary>
        /// 名字里括号中的英文名。绝大多数整合包词条都是「中文名 (English Name)」两段式；
        /// 中文搜索没登记平台链接时，用它去平台做一次精确兜底。
        /// </summary>
        public string EnglishName => McModSource.PickEnglishName(Name);
    }

    /// <summary>
    /// MC百科（mcmod.cn）的只读抓取。
    ///
    /// 没有公开 API，只能取网页，但两个入口的质量差别很大：
    ///
    /// - <c>modlist.html?key=</c>（"模组列表"里的搜索）按名字精确度排得很好：
    ///   搜「机械动力」第一条就是 Create，搜「钠」第一条是 Sodium。
    /// - <c>search.mcmod.cn/s</c>（全站检索）会把子模组排在主模组前面，
    ///   搜「钠」前 30 条里没有 Sodium，搜「机械动力」翻三页也找不到 Create。
    ///   所以这里只用前者。
    ///
    /// 模组详情页的"相关链接"里有 Modrinth 地址，但被包成
    /// <c>link.mcmod.cn/target/{base64}</c> 的跳转，要解一次 base64 才拿到真实 URL。
    /// 这条链接是最可靠的"百科模组 → Modrinth 项目"对应关系，
    /// 比拿英文名去 Modrinth 猜要准得多（猜的时候 Sodium 会被匹配到 Sodium Options API）。
    ///
    /// 限流：搜索接口很敏感，连续请求会直接返回"搜索太频繁"；详情页基本不限，
    /// 所以搜索只发一次请求（失败重试一次），详情页可以并发抓。
    /// </summary>
    public static class McModSource
    {
        public const string Host = "https://www.mcmod.cn";

        // 一个 <div class="modlist-block"> 是一张模组卡片
        private static readonly Regex ListSplit =
            new Regex(@"<div class=""modlist-block"">", RegexOptions.Compiled);

        private static readonly Regex ClassIdRx =
            new Regex(@"href=""/class/(\d+)\.html""", RegexOptions.Compiled);

        private static readonly Regex NameRx =
            new Regex(@"<img alt=""([^""]*)""", RegexOptions.Compiled);

        private static readonly Regex IntroRx =
            new Regex(@"class=""intro-content""><span>(.*?)</span>", RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex TargetRx =
            new Regex(@"link\.mcmod\.cn/target/([A-Za-z0-9+/=%]+)", RegexOptions.Compiled);

        // 全站检索的结果卡片：<div class="result-item"><div class="head"><a href=".../modpack/ID.html">名称</a>…
        private static readonly Regex PackResultSplit =
            new Regex(@"<div class=""result-item"">", RegexOptions.Compiled);

        private static readonly Regex PackHeadRx =
            new Regex(@"<div class=""head""><a[^>]*href=""[^""]*?/modpack/(\d+)\.html""[^>]*>(.*?)</a>",
                RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex PackBodyRx =
            new Regex(@"<div class=""body"">(.*?)</div>", RegexOptions.Compiled | RegexOptions.Singleline);

        // 整合包在平台上的路径与模组不同，单独一套（见 PackLinksAsync）
        private static readonly Regex ModrinthPackSlugRx =
            new Regex(@"modrinth\.com/modpack/([A-Za-z0-9_\-.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CurseForgePackSlugRx =
            new Regex(@"curseforge\.com/minecraft/modpacks/([A-Za-z0-9_\-.]+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ModrinthSlugRx =
            new Regex(@"modrinth\.com/(?:mod|project|datapack|shader|resourcepack|plugin)/([A-Za-z0-9_\-.]+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CurseForgeSlugRx =
            new Regex(@"curseforge\.com/minecraft/(?:mc-mods|texture-packs|shaders|worlds)/([A-Za-z0-9_\-.]+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 按关键词搜模组（中英文都行，中文才是它的主场）。
        /// 返回站点顺序，也就是它的相关度排序。
        /// </summary>
        public static async Task<List<McModEntry>> SearchAsync(string query, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<McModEntry>();

            var url = Host + "/modlist.html?key=" + Uri.EscapeDataString(query.Trim());

            // 最多两轮：第一轮撞上限流（或网络抖一下）时等两秒再试，
            // 第二轮还不行就把"搜索太频繁"如实报上去，别让用户以为搜不到。
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                string html;
                try
                {
                    html = await Net.GetStringRawAsync(url, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (attempt >= 1)
                        throw new InvalidOperationException("连不上 MC百科：" + ex.Message, ex);

                    await Task.Delay(1200, ct).ConfigureAwait(false);
                    continue;
                }

                if (html.IndexOf("搜索太频繁", StringComparison.Ordinal) >= 0)
                {
                    if (attempt >= 1)
                        throw new InvalidOperationException("MC百科 提示搜索太频繁，等十几秒再试。");

                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    continue;
                }

                return ParseList(html);
            }
        }

        private static List<McModEntry> ParseList(string html)
        {
            var list = new List<McModEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var blocks = ListSplit.Split(html);
            for (var i = 1; i < blocks.Length; i++)
            {
                var block = blocks[i];

                var id = ClassIdRx.Match(block);
                var name = NameRx.Match(block);
                if (!id.Success || !name.Success) continue;
                if (!seen.Add(id.Groups[1].Value)) continue;

                var intro = IntroRx.Match(block);

                list.Add(new McModEntry
                {
                    ClassId = id.Groups[1].Value,
                    Name = Clean(name.Groups[1].Value),
                    Intro = intro.Success ? Clean(intro.Groups[1].Value) : ""
                });
            }

            return list;
        }

        /// <summary>
        /// 取某个模组在 Modrinth 上的 slug；站上没登记 Modrinth 链接就返回空串。
        ///
        /// 扫整页的跳转链接而不是只找"Modrinth"那个锚点：锚点里属性的顺序改过一次
        /// （data-original-title 在 href 前后都出现过），按整页扫不受影响。
        /// </summary>
        public static async Task<string> ModrinthSlugAsync(string classId, CancellationToken ct = default)
            => (await LinksAsync(classId, ct).ConfigureAwait(false)).Modrinth;

        /// <summary>
        /// 一次读完词条页，同时给出 Modrinth 与 CurseForge 的 slug。
        ///
        /// 只读一次页面就把两个平台都解析出来：中文搜索在「全部」模式下要优先试 CurseForge，
        /// 如果分两次读同一页，等于把详情页的请求量翻倍（百科详情页虽然不限流，但没必要）。
        /// </summary>
        public static async Task<(string Modrinth, string CurseForge)> LinksAsync(string classId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(classId)) return ("", "");

            string html;
            try
            {
                html = await Net.GetStringRawAsync(Host + "/class/" + classId + ".html", ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"读取 MC百科 模组页 {classId} 失败：{ex.Message}");
                return ("", "");
            }

            var modrinth = "";
            var curseforge = "";

            foreach (Match m in TargetRx.Matches(html))
            {
                var target = DecodeTarget(m.Groups[1].Value);
                if (target.Length == 0) continue;

                if (modrinth.Length == 0)
                {
                    var slug = ModrinthSlugRx.Match(target);
                    if (slug.Success) modrinth = Uri.UnescapeDataString(slug.Groups[1].Value);
                }

                if (curseforge.Length == 0)
                {
                    var slug = CurseForgeSlugRx.Match(target);
                    if (slug.Success) curseforge = Uri.UnescapeDataString(slug.Groups[1].Value);
                }

                if (modrinth.Length > 0 && curseforge.Length > 0) break;
            }

            return (modrinth, curseforge);
        }

        // ---------------- 整合包 ----------------

        /// <summary>
        /// 按关键词搜**整合包**。
        ///
        /// 整合包没有像 <c>modlist.html</c> 那样的专用搜索入口（<c>modpack.html</c> 只支持元素 / 版本筛选，
        /// 传 <c>key</c> 会被忽略，永远返回默认列表），所以这里走全站检索再把结果限定在
        /// <c>filter=2</c>（整合包），返回的卡片结构与模组搜索不同，需要单独解析。
        /// </summary>
        public static async Task<List<McModPackEntry>> SearchPacksAsync(string query, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<McModPackEntry>();

            var url = "https://search.mcmod.cn/s?key=" + Uri.EscapeDataString(query.Trim()) + "&filter=2";

            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                string html;
                try
                {
                    html = await Net.GetStringRawAsync(url, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (attempt >= 1)
                        throw new InvalidOperationException("连不上 MC百科：" + ex.Message, ex);

                    await Task.Delay(1200, ct).ConfigureAwait(false);
                    continue;
                }

                // 全站检索的限流提示与模组列表不是同一句
                if (html.IndexOf("检索速度过快", StringComparison.Ordinal) >= 0 ||
                    html.IndexOf("搜索太频繁", StringComparison.Ordinal) >= 0)
                {
                    if (attempt >= 1)
                        throw new InvalidOperationException("MC百科 提示检索太快，等十几秒再试。");

                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    continue;
                }

                return ParsePackList(html);
            }
        }

        private static List<McModPackEntry> ParsePackList(string html)
        {
            var list = new List<McModPackEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var blocks = PackResultSplit.Split(html);
            for (var i = 1; i < blocks.Length; i++)
            {
                var block = blocks[i];

                var head = PackHeadRx.Match(block);
                if (!head.Success) continue;

                var id = head.Groups[1].Value;
                if (!seen.Add(id)) continue;

                var body = PackBodyRx.Match(block);

                list.Add(new McModPackEntry
                {
                    PackId = id,
                    Name = Clean(head.Groups[2].Value),
                    Intro = body.Success ? Clean(body.Groups[1].Value) : ""
                });
            }

            return list;
        }

        /// <summary>
        /// 读整合包词条页，取出 Modrinth / CurseForge 的整合包 slug。
        ///
        /// 词条页的"相关链接"同样是 <c>link.mcmod.cn/target/{base64}</c> 跳转，
        /// 但整合包在平台上的路径是 <c>/modpack/&lt;slug&gt;</c> 与 <c>/minecraft/modpacks/&lt;slug&gt;</c>，
        /// 和模组的 <c>/mod/</c>、<c>/mc-mods/</c> 不通用，必须单独匹配。
        /// 很多国产整合包只挂了网盘 / 论坛链接，这种情况下两个都为空。
        /// </summary>
        public static async Task<(string Modrinth, string CurseForge)> PackLinksAsync(string packId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(packId)) return ("", "");

            string html;
            try
            {
                html = await Net.GetStringRawAsync(Host + "/modpack/" + packId + ".html", ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"读取 MC百科 整合包页 {packId} 失败：{ex.Message}");
                return ("", "");
            }

            var modrinth = "";
            var curseforge = "";

            foreach (Match m in TargetRx.Matches(html))
            {
                var target = DecodeTarget(m.Groups[1].Value);
                if (target.Length == 0) continue;

                if (modrinth.Length == 0)
                {
                    var slug = ModrinthPackSlugRx.Match(target);
                    if (slug.Success) modrinth = Uri.UnescapeDataString(slug.Groups[1].Value);
                }

                if (curseforge.Length == 0)
                {
                    var slug = CurseForgePackSlugRx.Match(target);
                    if (slug.Success) curseforge = Uri.UnescapeDataString(slug.Groups[1].Value);
                }

                if (modrinth.Length > 0 && curseforge.Length > 0) break;
            }

            return (modrinth, curseforge);
        }

        /// <summary>
        /// 从「中文名 (English Name)」里挑出英文名；挑不出来返回空串。
        /// 只认含拉丁字母的那一对括号，避免把「（1.16.5）」这种版本号当成名字。
        /// </summary>
        internal static string PickEnglishName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";

            var matches = Regex.Matches(name, @"[(（]([^()（）]{2,80})[)）]");
            for (var i = matches.Count - 1; i >= 0; i--)
            {
                var inner = matches[i].Groups[1].Value.Trim();
                if (inner.Length >= 2 && inner.Any(char.IsAsciiLetter))
                    return inner;
            }

            return "";
        }

        /// <summary>把 link.mcmod.cn 的 base64 跳转还原成真实地址。</summary>
        private static string DecodeTarget(string b64)
        {
            try
            {
                var s = b64.Replace('-', '+').Replace('_', '/');
                s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
                return Encoding.UTF8.GetString(Convert.FromBase64String(s));
            }
            catch
            {
                return "";
            }
        }

        /// <summary>去掉标签、反转义、压空白——搜索卡片里塞了 <em> 高亮和 &amp; 之类。</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            s = Regex.Replace(s, "<[^>]+>", "");
            s = WebUtility.HtmlDecode(s);
            return Regex.Replace(s, @"\s+", " ").Trim();
        }
    }
}
