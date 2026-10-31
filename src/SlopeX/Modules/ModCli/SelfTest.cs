using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SlopeX.Modules.ModMods;

namespace SlopeX.Modules.ModCli
{
    /// <summary>
    /// 命令行自检：用固定样本断言「中文 → MC百科 → 平台」这条链路还活着。
    ///
    /// 为什么要它：模组中文搜索是抓 <c>www.mcmod.cn</c> 的 HTML 再正则解析，
    /// 站点一改版就**悄悄失效**——不抛异常，只是返回空列表，用户搜不到才会有人发现。
    /// 这里把已知答案写死成断言（机械动力是 Modrinth <c>LNytGWDc</c>、苹果皮是 <c>EsAfCjCV</c>、
    /// 钠在 forge 下必须被滤掉……），解析一坏当天就红，而不是等反馈。
    ///
    /// 只做命令行入口，不进启动流程、不做定时任务：它依赖网络与对方站点的在线状态。
    /// 任一断言失败返回退出码 1，可以直接挂到发布脚本里。
    /// </summary>
    internal static class SelfTest
    {
        /// <summary>一条检查：跑一次真实搜索，再用断言核对结果。</summary>
        private sealed class Case
        {
            public string Name;
            public string Query;
            public ModSourceFilter Filter;

            /// <summary>返回 <c>null</c> 表示通过，否则是失败原因。</summary>
            public Func<ModSearchResult, string> Verify;
        }

        public static int Run(IList<string> args)
        {
            if (args.Any(a => a is "help" or "-h" or "--help"))
            {
                Usage();
                return 0;
            }

            // 样本答案是按 1.20.1 / forge 核对的；换目标版本时这些 ID 不一定还成立
            var mc = Option(args, "--mc") ?? "1.20.1";
            var loader = Option(args, "--loader") ?? "forge";
            var verbose = args.Any(a => a is "-v" or "--verbose");

            Cli.Out($"自检：MC {mc} / {loader}    固定样本 {Build(mc, loader).Count} 条（需联网）");
            Cli.Out("");

            var cases = Build(mc, loader);
            var failed = 0;
            var total = Stopwatch.StartNew();

            for (var i = 0; i < cases.Count; i++)
            {
                var c = cases[i];
                var prefix = $"[{i + 1}/{cases.Count}]";

                var sw = Stopwatch.StartNew();
                string failure;
                ModSearchResult result = null;

                try
                {
                    result = ModSearch.SearchAsync(c.Query, mc, loader, c.Filter).GetAwaiter().GetResult();
                    failure = c.Verify(result);
                }
                catch (Exception ex)
                {
                    failure = "异常：" + ex.GetType().Name + " " + ex.Message;
                }

                sw.Stop();

                if (failure == null)
                {
                    Cli.Out($"{prefix} PASS  {sw.Elapsed.TotalSeconds,5:0.0}s  {c.Name}  「{c.Query}」");
                }
                else
                {
                    failed++;
                    Cli.Out($"{prefix} FAIL  {sw.Elapsed.TotalSeconds,5:0.0}s  {c.Name}  「{c.Query}」");
                    Cli.Out("         原因：" + failure);
                    if (result != null) Cli.Out("         实际：" + Describe(result, verbose ? 32 : 5));
                }

                if (verbose && failure == null && result != null)
                    Cli.Out("         结果：" + Describe(result, 32));
            }

            total.Stop();
            Cli.Out("");
            Cli.Out($"{cases.Count} 项检查：{cases.Count - failed} 通过，{failed} 失败，共 {total.Elapsed.TotalSeconds:0.0} s");

            if (failed > 0)
                Cli.Out("提示：中文搜索依赖 MC百科 的页面结构（Modules/ModMods/McMod.cs 里的正则），改版后要跟着改。");

            return failed > 0 ? 1 : 0;
        }

        private static void Usage()
        {
            Cli.Out("用法：selftest [--mc 1.20.1] [--loader forge] [-v]");
            Cli.Out("  用固定样本核对中文搜索链路：机械动力 → LNytGWDc、苹果皮 → EsAfCjCV、");
            Cli.Out("  钠在 forge 下必须被滤掉、「全部」落到 CurseForge、英文直搜两源都有结果。");
            Cli.Out("  全部通过返回 0，任一失败返回 1。默认目标 1.20.1 / forge。");
        }

        /// <summary>
        /// 样本表。每条都对应一个「曾经手工验证过」的结论，所以断言能直接指出是哪一环坏了：
        /// 前两条查百科检索 + 词条链接解析，第三条查版本/加载器过滤，第四条查 CurseForge 分派。
        /// </summary>
        private static List<Case> Build(string mc, string loader)
        {
            return new List<Case>
            {
                new Case
                {
                    Name = "中文 → Modrinth 官方",
                    Query = "机械动力",
                    Filter = ModSourceFilter.Modrinth,
                    Verify = r => Has(r, "LNytGWDc") ? null : "没有命中 Create（LNytGWDc）" + Counts(r)
                },

                new Case
                {
                    Name = "中文 → 别名短名",
                    Query = "苹果皮",
                    Filter = ModSourceFilter.Modrinth,
                    Verify = r => Has(r, "EsAfCjCV") ? null : "没有命中 AppleSkin（EsAfCjCV）" + Counts(r)
                },

                new Case
                {
                    Name = "版本过滤（Sodium 只支持 Fabric）",
                    Query = "钠",
                    Filter = ModSourceFilter.Modrinth,
                    Verify = r =>
                    {
                        // 过滤对了但解析挂了，结果同样是空——所以先要求百科确实返回了条目
                        if (!r.FromMcMod) return "关键词没被识别为中文，走了英文直搜";
                        if (r.Total == 0) return "MC百科 没返回任何条目（检索入口失效或页面结构变了）";

                        var sodium = r.Hits.FirstOrDefault(h =>
                            string.Equals(h.ProjectId, "AANobbMI", StringComparison.OrdinalIgnoreCase));
                        return sodium == null
                            ? null
                            : $"Sodium（{sodium.ProjectId}）没被 forge 过滤掉" + Counts(r);
                    }
                },

                new Case
                {
                    Name = "全部 → 优先 CurseForge",
                    Query = "机械动力",
                    Filter = ModSourceFilter.All,
                    Verify = r =>
                    {
                        // 「全部」下 CF 优先；这一条同时验证 CF 的 slug 解析、去重排序和文件列表接口
                        var cf = r.Hits.FirstOrDefault(h =>
                            string.Equals(h.Source, "CurseForge", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(h.ProjectId, "328085", StringComparison.OrdinalIgnoreCase));
                        return cf != null ? null : "「全部」没有落到 CurseForge 的 Create（328085）" + Counts(r);
                    }
                },

                new Case
                {
                    Name = "英文直搜 → 两源存活",
                    Query = "jei",
                    Filter = ModSourceFilter.All,
                    Verify = r =>
                    {
                        if (r.ModrinthCount < 5) return $"Modrinth 只返回 {r.ModrinthCount} 条";
                        if (r.CurseForgeCount < 5) return $"CurseForge 只返回 {r.CurseForgeCount} 条";
                        return Has(r, "u6dRKJwZ") ? null : "结果里没有 JEI（u6dRKJwZ）" + Counts(r);
                    }
                }
            };
        }

        private static bool Has(ModSearchResult r, string projectId)
            => r.Hits.Any(h => string.Equals(h.ProjectId, projectId, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(h.Slug, projectId, StringComparison.OrdinalIgnoreCase));

        private static string Counts(ModSearchResult r)
            => $"（百科 {r.Total} 条 / 核对 {r.Checked} / 可安装 {r.Hits.Count}）";

        /// <summary>失败时把实际拿到的结果打出来：一眼能看出是「解析崩了」还是「排序变了」。</summary>
        private static string Describe(ModSearchResult r, int take)
        {
            if (r.Hits.Count == 0) return "0 条" + Counts(r);

            var head = string.Join("；", r.Hits.Take(take)
                .Select(h => $"{h.SourceText} {h.ProjectId}「{h.Title}」"));

            return r.Hits.Count > take ? $"{head} … 共 {r.Hits.Count} 条" : head;
        }

        private static string Option(IList<string> args, string name)
        {
            for (var i = 0; i < args.Count - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];

            return null;
        }
    }
}
