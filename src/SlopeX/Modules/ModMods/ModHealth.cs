using System;
using System.Collections.Generic;
using System.Linq;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModMods
{
    /// <summary>一类问题。用字符串而不是枚举，方便日志和界面直接显示。</summary>
    public static class HealthKind
    {
        public const string Broken = "broken";           // jar 读不出来
        public const string Duplicate = "duplicate";     // 同一 modid 装了多份
        public const string Missing = "missing";         // 缺前置
        public const string Version = "version";         // 版本不满足区间
        public const string Conflict = "conflict";       // 声明了不兼容
    }

    public sealed class HealthIssue
    {
        public string Kind { get; set; } = "";

        /// <summary>问题归属的模组（谁提出的要求 / 谁的包坏了）。</summary>
        public string ModId { get; set; } = "";

        /// <summary>涉及的另一方（缺的模组 / 冲突对象）。</summary>
        public string Target { get; set; } = "";

        /// <summary>直接可显示的一句话。</summary>
        public string Message { get; set; } = "";
    }

    public sealed class HealthReport
    {
        public List<JarMod> Mods { get; } = new();
        public List<HealthIssue> Issues { get; } = new();

        public int OkCount => Mods.Count(m => m.Ok);
        public int BrokenCount => Mods.Count(m => !m.Ok);
        public bool Clean => Issues.Count == 0;

        /// <summary>一行摘要，用于状态栏 / 日志。</summary>
        public string Summary()
        {
            if (Mods.Count == 0) return "mods 目录是空的";

            var parts = new List<string> { $"{OkCount} 个模组" };
            if (BrokenCount > 0) parts.Add($"{BrokenCount} 个读不出来");

            var grouped = Issues.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());
            void Add(string kind, string label)
            {
                if (grouped.TryGetValue(kind, out var n) && n > 0) parts.Add($"{label} {n}");
            }

            Add(HealthKind.Missing, "缺前置");
            Add(HealthKind.Version, "版本不符");
            Add(HealthKind.Conflict, "冲突");
            Add(HealthKind.Duplicate, "重复");

            return string.Join("，", parts);
        }
    }

    /// <summary>
    /// 对已装模组做一次体检：坏包、重复、缺前置、版本不符、声明的不兼容。
    ///
    /// 判断依据全部来自 jar 里自己声明的元数据（<see cref="ModJar"/>），
    /// 不联网、不猜——所以它反映的是「作者怎么写的」，而不是「游戏实际怎么跑」。
    /// 拿不准的地方一律不报（见 <see cref="ModJar.Satisfies"/> 的保守策略）。
    /// </summary>
    public static class ModHealth
    {
        /// <summary>平台 / 加载器自带的 id，不算「缺前置」。</summary>
        private static readonly HashSet<string> PlatformIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "minecraft", "forge", "neoforge", "fabricloader", "fabric-api", "fabric",
            "javafml", "lowcodefml", "quilt_loader", "quilted_fabric_api"
        };

        public static HealthReport Check(string modsDir)
        {
            var mods = ModJar.ScanDir(modsDir);
            return Check(mods);
        }

        public static HealthReport Check(IReadOnlyList<JarMod> mods)
        {
            var report = new HealthReport();
            if (mods == null) return report;

            report.Mods.AddRange(mods);

            foreach (var m in mods.Where(m => !m.Ok))
            {
                report.Issues.Add(new HealthIssue
                {
                    Kind = HealthKind.Broken,
                    ModId = m.FileName,
                    Message = $"{m.FileName} 读不出来：{m.Error}"
                });
            }

            var ok = mods.Where(m => m.Ok).ToList();

            // 同 modid 多份：游戏侧会直接 DuplicateModsFound
            foreach (var g in ok.GroupBy(m => m.ModId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                report.Issues.Add(new HealthIssue
                {
                    Kind = HealthKind.Duplicate,
                    ModId = g.Key,
                    Message = $"{g.Key} 装了 {g.Count()} 份：" +
                              string.Join("、", g.Select(x => x.FileName))
                });
            }

            // 一个 modid 有多份时，按版本最高的那份参与后续判断（用户多半是想升级）
            var installed = new Dictionary<string, JarMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in ok.GroupBy(m => m.ModId, StringComparer.OrdinalIgnoreCase))
                installed[g.Key] = g.OrderByDescending(m => m.Version, Comparer<string>.Create(ModJar.Compare)).First();

            CheckDependencies(ok, installed, report);
            CheckConflicts(ok, installed, report);

            return report;
        }

        private static void CheckDependencies(List<JarMod> ok, Dictionary<string, JarMod> installed,
            HealthReport report)
        {
            foreach (var m in ok)
            {
                foreach (var d in m.Depends)
                {
                    if (!d.Mandatory || PlatformIds.Contains(d.ModId)) continue;

                    if (!installed.TryGetValue(d.ModId, out var have))
                    {
                        report.Issues.Add(new HealthIssue
                        {
                            Kind = HealthKind.Missing,
                            ModId = m.ModId,
                            Target = d.ModId,
                            Message = $"{m.ModId} 需要 {d.ModId} {d.VersionRange}，未安装"
                        });
                        continue;
                    }

                    if (!ModJar.Satisfies(have.Version, d.VersionRange, d.Syntax))
                    {
                        report.Issues.Add(new HealthIssue
                        {
                            Kind = HealthKind.Version,
                            ModId = m.ModId,
                            Target = d.ModId,
                            Message = $"{m.ModId} 需要 {d.ModId} {d.VersionRange}，实际装的是 {have.Version}"
                        });
                    }
                }
            }
        }

        /// <summary>
        /// 冲突：A 的 <c>breaks</c> 指名 B，且 B 的当前版本落在声明区间内。
        /// A 声明与 B 不兼容、B 也声明与 A 不兼容时只报一条（用排序后的 key 去重）。
        /// </summary>
        private static void CheckConflicts(List<JarMod> ok, Dictionary<string, JarMod> installed,
            HealthReport report)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in ok)
            {
                foreach (var b in m.Breaks)
                {
                    if (PlatformIds.Contains(b.ModId)) continue;
                    if (!installed.TryGetValue(b.ModId, out var other)) continue;
                    if (!ModJar.Satisfies(other.Version, b.VersionRange, b.Syntax)) continue;

                    var pair = string.Compare(m.ModId, b.ModId, StringComparison.OrdinalIgnoreCase) <= 0
                        ? m.ModId + "|" + b.ModId
                        : b.ModId + "|" + m.ModId;

                    if (!seen.Add(pair)) continue;

                    var range = string.IsNullOrWhiteSpace(b.VersionRange) || b.VersionRange == "*"
                        ? ""
                        : $"（范围 {b.VersionRange}）";

                    report.Issues.Add(new HealthIssue
                    {
                        Kind = HealthKind.Conflict,
                        ModId = m.ModId,
                        Target = b.ModId,
                        Message = $"{m.ModId} 声明与 {b.ModId} 不兼容{range}，当前装的是 {other.Version}"
                    });
                }
            }
        }
    }
}
