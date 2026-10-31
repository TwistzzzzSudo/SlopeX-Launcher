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

        private static readonly Regex ModrinthSlugRx =
            new Regex(@"modrinth\.com/(?:mod|project|datapack|shader|resourcepack|plugin)/([A-Za-z0-9_\-.]+)",
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
        {
            if (string.IsNullOrEmpty(classId)) return "";

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
                return "";
            }

            foreach (Match m in TargetRx.Matches(html))
            {
                var target = DecodeTarget(m.Groups[1].Value);
                if (target.Length == 0) continue;

                var slug = ModrinthSlugRx.Match(target);
                if (slug.Success) return Uri.UnescapeDataString(slug.Groups[1].Value);
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
