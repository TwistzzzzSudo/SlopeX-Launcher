using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModMods
{
    /// <summary>jar 里声明的一条依赖（或"不兼容"声明）。</summary>
    public sealed class JarDependency
    {
        /// <summary>声明方（多模组 jar 里用它区分是哪一段 dependencies）。</summary>
        public string Owner { get; set; } = "";
        public string ModId { get; set; } = "";
        public string VersionRange { get; set; } = "";

        /// <summary>Forge 的 mandatory；Fabric 没有这个字段，一律 true。</summary>
        public bool Mandatory { get; set; } = true;

        /// <summary>区间语法：<c>maven</c>（Forge / NeoForge，如 <c>[1.20.1,1.21)</c>）或 <c>semver</c>（Fabric，如 <c>&gt;=1.20 &lt;1.21</c>）。</summary>
        public string Syntax { get; set; } = "maven";
    }

    /// <summary>从一个 jar 里读出来的模组元数据。</summary>
    public sealed class JarMod
    {
        public string FilePath { get; set; } = "";
        public string FileName => Path.GetFileName(FilePath);

        public string ModId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";

        /// <summary>fabric / forge / neoforge；空表示没认出来。</summary>
        public string Format { get; set; } = "";

        public List<JarDependency> Depends { get; } = new();
        public List<JarDependency> Breaks { get; } = new();

        /// <summary>非空表示没读出来（不是有效模组、jar 损坏、元数据看不懂）。</summary>
        public string Error { get; set; } = "";

        public bool Ok => string.IsNullOrEmpty(Error) && ModId.Length > 0;
    }

    /// <summary>
    /// 读 <c>mods\</c> 里每个 jar 的模组元数据：modid、版本、依赖声明、不兼容声明。
    ///
    /// 这是「依赖检查 / 冲突检查 / 更新检查」共同的地基——没有它，启动器只知道文件名，
    /// 不知道里面是什么，任何检查都无从谈起。
    ///
    /// 只读元数据、不解压其它条目；读不出来就记 <see cref="JarMod.Error"/> 往下走，
    /// 绝不因为一个坏包中断整次扫描（用户目录里躺着旧版本 jar 是常态）。
    /// </summary>
    public static class ModJar
    {
        private static readonly string[] ForgeManifests =
        {
            "META-INF/neoforge.mods.toml",
            "META-INF/mods.toml"
        };

        /// <summary>扫描目录下所有 jar（不递归）。目录不存在返回空表。</summary>
        public static List<JarMod> ScanDir(string modsDir)
        {
            var list = new List<JarMod>();
            if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir)) return list;

            string[] files;
            try
            {
                files = Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                Log.Warn($"扫描 mods 目录失败：{ex.Message}");
                return list;
            }

            // 文件名排序，输出稳定，方便对比两次扫描的结果
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) list.Add(Read(f));
            return list;
        }

        /// <summary>读单个 jar。任何异常都收敛成 <see cref="JarMod.Error"/>。</summary>
        public static JarMod Read(string path)
        {
            var mod = new JarMod { FilePath = path };

            try
            {
                using var zip = ZipFile.OpenRead(path);

                var fabric = Find(zip, "fabric.mod.json");
                if (fabric != null) return ReadFabric(mod, fabric);

                foreach (var name in ForgeManifests)
                {
                    var entry = Find(zip, name);
                    if (entry == null) continue;

                    return ReadForge(mod, entry,
                        name.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ? "neoforge" : "forge");
                }

                mod.Error = "没有 fabric.mod.json / mods.toml，可能不是模组 jar";
            }
            catch (Exception ex)
            {
                mod.Error = ex.Message;
            }

            return mod;
        }

        /// <summary>
        /// 算整个文件的 SHA1（小写十六进制）——Modrinth 用的就是这个值做文件身份。
        /// 流式读，几百 MB 的整合包也不会把内存吃满。失败返回空串。
        /// </summary>
        public static string Sha1Of(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                Log.Warn($"计算 SHA1 失败 {Path.GetFileName(path)}：{ex.Message}");
                return "";
            }
        }

        /// <summary>zip 里的条目名区分大小写，但书写习惯五花八门，比较时忽略大小写。</summary>
        private static ZipArchiveEntry Find(ZipArchive zip, string name)
            => zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));

        // ---------------- Fabric ----------------

        private static JarMod ReadFabric(JarMod mod, ZipArchiveEntry entry)
        {
            mod.Format = "fabric";

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var node = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
            if (node == null)
            {
                mod.Error = "fabric.mod.json 不是合法 JSON 对象";
                return mod;
            }

            mod.ModId = J.Str(node, "id") ?? "";
            mod.Version = J.Str(node, "version") ?? "";
            mod.Name = J.Str(node, "name") ?? mod.ModId;

            ReadFabricMap(mod, node, "depends", mod.Depends);
            ReadFabricMap(mod, node, "breaks", mod.Breaks);
            return mod;
        }

        /// <summary>
        /// <c>depends</c> / <c>breaks</c> 的值可以是字符串、字符串数组或布尔。
        /// 数组表示"任一满足即可"，所以拆成多条同 modId 的记录。
        /// </summary>
        private static void ReadFabricMap(JarMod mod, JsonObject node, string key, List<JarDependency> into)
        {
            if (node[key] is not JsonObject map) return;

            foreach (var pair in map)
            {
                var ranges = new List<string>();
                CollectRangeText(pair.Value, ranges, 0);

                foreach (var r in ranges)
                {
                    into.Add(new JarDependency
                    {
                        Owner = mod.ModId,
                        ModId = pair.Key,
                        VersionRange = r,
                        Mandatory = true,
                        Syntax = "semver"
                    });
                }
            }
        }

        private static void CollectRangeText(JsonNode node, List<string> into, int depth)
        {
            if (node == null || depth > 3) return;

            switch (node)
            {
                case JsonArray arr:
                    foreach (var item in arr) CollectRangeText(item, into, depth + 1);
                    break;

                case JsonValue v:
                    if (v.TryGetValue<bool>(out var b))
                    {
                        if (b) into.Add("*");
                    }
                    else if (v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    {
                        into.Add(s);
                    }
                    break;
            }
        }

        // ---------------- Forge / NeoForge ----------------

        /// <summary>
        /// 手写 TOML 扫描：只认 <c>[[mods]]</c> 与 <c>[[dependencies.&lt;modid&gt;]]</c> 两种表，
        /// 以及 <c>key = value</c> 形式的标量。模组元数据用不到嵌套表 / 数组 / 多行字符串，
        /// 所以不引第三方 TOML 库（整个项目至今零外部依赖）。
        /// </summary>
        private static JarMod ReadForge(JarMod mod, ZipArchiveEntry entry, string format)
        {
            mod.Format = format;

            string text;
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                text = reader.ReadToEnd();

            var deps = new List<JarDependency>();
            var section = "";      // "mods" / "dependencies"
            var depOwner = "";     // [[dependencies.X]] 里的 X

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim().TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                if (line.StartsWith("[[", StringComparison.Ordinal))
                {
                    var name = line.TrimStart('[').TrimEnd(']').Trim();

                    if (name.StartsWith("dependencies.", StringComparison.OrdinalIgnoreCase))
                    {
                        section = "dependencies";
                        depOwner = name.Substring("dependencies.".Length).Trim().Trim('"');
                    }
                    else if (string.Equals(name, "dependencies", StringComparison.OrdinalIgnoreCase))
                    {
                        section = "dependencies";
                        depOwner = mod.ModId;
                    }
                    else if (string.Equals(name, "mods", StringComparison.OrdinalIgnoreCase))
                    {
                        section = "mods";
                    }
                    else
                    {
                        section = "";   // 其它表（mixins / accessTransformers / features）一概跳过
                    }

                    continue;
                }

                if (!TryParseKeyValue(line, out var key, out var value)) continue;

                if (section == "mods")
                {
                    switch (key)
                    {
                        case "modId":
                            if (mod.ModId.Length == 0) mod.ModId = value;
                            break;
                        case "version":
                            if (mod.Version.Length == 0) mod.Version = value;
                            break;
                        case "displayName":
                            if (mod.Name.Length == 0) mod.Name = value;
                            break;
                    }

                    continue;
                }

                if (section != "dependencies") continue;

                switch (key)
                {
                    case "modId":
                        deps.Add(new JarDependency
                        {
                            Owner = depOwner,
                            ModId = value,
                            Syntax = "maven",
                            Mandatory = true
                        });
                        break;
                    case "versionRange":
                        if (deps.Count > 0) deps[deps.Count - 1].VersionRange = value;
                        break;
                    case "mandatory":
                        if (deps.Count > 0)
                            deps[deps.Count - 1].Mandatory =
                                !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }

            mod.Depends.AddRange(deps);
            if (mod.ModId.Length == 0) mod.Error = "mods.toml 里没有 modId";
            else if (mod.Name.Length == 0) mod.Name = mod.ModId;

            return mod;
        }

        private static bool TryParseKeyValue(string line, out string key, out string value)
        {
            key = "";
            value = "";

            var eq = line.IndexOf('=');
            if (eq <= 0) return false;

            key = line.Substring(0, eq).Trim();
            value = line.Substring(eq + 1).Trim();

            // 去掉行尾注释（简单处理：不在引号内的 #）
            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                var end = value.IndexOf('"', 1);
                if (end > 0) value = value.Substring(1, end - 1);
            }
            else
            {
                var hash = value.IndexOf('#');
                if (hash >= 0) value = value.Substring(0, hash).Trim();
            }

            return key.Length > 0;
        }

        // ---------------- 版本比较与区间匹配 ----------------

        /// <summary>版本是否落在区间内。拿不准时返回 true（宁可漏报，也不误报）。</summary>
        public static bool Satisfies(string version, string range, string syntax)
        {
            if (string.IsNullOrWhiteSpace(range) || range == "*") return true;
            if (string.IsNullOrWhiteSpace(version)) return true;

            return string.Equals(syntax, "semver", StringComparison.OrdinalIgnoreCase)
                ? SemverSatisfies(version, range)
                : MavenSatisfies(version, range);
        }

        /// <summary>
        /// Maven 区间：<c>[1.20.1,1.21)</c>、<c>[1.0,)</c>、<c>(,2.0]</c>、<c>[1.0]</c>。
        /// 不带括号的单个版本按 Forge 习惯当作"不低于"。
        /// </summary>
        private static bool MavenSatisfies(string version, string range)
        {
            var r = range.Trim();

            if (!r.StartsWith("[", StringComparison.Ordinal) && !r.StartsWith("(", StringComparison.Ordinal))
            {
                if (r.EndsWith(".*", StringComparison.Ordinal))
                    return version.StartsWith(r.Substring(0, r.Length - 1), StringComparison.OrdinalIgnoreCase);

                return Compare(version, r) >= 0;
            }

            var close = r.IndexOfAny(new[] { ']', ')' });
            if (close < 0) return true;   // 语法本身是坏的，不拦

            var lowerInclusive = r[0] == '[';
            var upperInclusive = r[close] == ']';

            var body = r.Substring(1, close - 1);
            var parts = body.Split(',');
            var lo = parts.Length > 0 ? parts[0].Trim() : "";
            var hi = parts.Length > 1 ? parts[1].Trim() : "";

            if (lo.Length > 0)
            {
                var c = Compare(version, lo);
                if (c < 0 || (c == 0 && !lowerInclusive)) return false;
            }

            if (hi.Length > 0)
            {
                var c = Compare(version, hi);
                if (c > 0 || (c == 0 && !upperInclusive)) return false;
            }

            return true;
        }

        /// <summary>
        /// Fabric 的 semver 范围：空格分隔多个条件（全部要满足），支持
        /// <c>&gt;=</c> <c>&lt;=</c> <c>&gt;</c> <c>&lt;</c> <c>=</c> <c>^</c> <c>~</c> <c>1.20.x</c>。
        /// </summary>
        private static bool SemverSatisfies(string version, string range)
        {
            var r = range.Trim();
            if (r == "*") return true;

            if (r.IndexOf(' ') < 0 && r.IndexOf('|') < 0)
                return SemverTerm(version, r);

            foreach (var part in r.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // "||" 表示或，这里退化成"任一段满足即可"——真实模组极少用，先保守处理
                if (part == "||") continue;
                if (!SemverTerm(version, part)) return false;
            }

            return true;
        }

        private static bool SemverTerm(string version, string term)
        {
            var t = term.Trim();
            if (t.Length == 0 || t == "*") return true;

            if (t.EndsWith(".x", StringComparison.OrdinalIgnoreCase) ||
                t.EndsWith(".*", StringComparison.Ordinal))
                return version.StartsWith(t.Substring(0, t.Length - 1), StringComparison.OrdinalIgnoreCase);

            if (t.StartsWith(">=", StringComparison.Ordinal)) return Compare(version, t.Substring(2)) >= 0;
            if (t.StartsWith("<=", StringComparison.Ordinal)) return Compare(version, t.Substring(2)) <= 0;
            if (t.StartsWith(">", StringComparison.Ordinal)) return Compare(version, t.Substring(1)) > 0;
            if (t.StartsWith("<", StringComparison.Ordinal)) return Compare(version, t.Substring(1)) < 0;
            if (t.StartsWith("=", StringComparison.Ordinal)) return Compare(version, t.Substring(1)) == 0;

            if (t.StartsWith("^", StringComparison.Ordinal))
            {
                var lo = t.Substring(1);
                var hi = CaretUpper(lo);
                return Compare(version, lo) >= 0 && (hi == null || Compare(version, hi) < 0);
            }

            if (t.StartsWith("~", StringComparison.Ordinal))
            {
                var lo = t.Substring(1);
                var hi = TildeUpper(lo);
                return Compare(version, lo) >= 0 && (hi == null || Compare(version, hi) < 0);
            }

            return Compare(version, t) == 0;
        }

        private static string CaretUpper(string v)
        {
            var p = Numbers(v);
            if (p.Length == 0) return null;
            return p[0] > 0 ? $"{p[0] + 1}.0.0" : p.Length > 1 && p[1] > 0 ? $"0.{p[1] + 1}.0" : $"0.0.{(p.Length > 2 ? p[2] + 1 : 1)}";
        }

        private static string TildeUpper(string v)
        {
            var p = Numbers(v);
            if (p.Length == 0) return null;
            return p.Length > 1 ? $"{p[0]}.{p[1] + 1}.0" : $"{p[0] + 1}.0.0";
        }

        private static int[] Numbers(string v)
        {
            var main = v.Split('-')[0].Split('+')[0];
            return main.Split('.')
                .Select(x => int.TryParse(x, out var n) ? n : 0)
                .ToArray();
        }

        /// <summary>
        /// 版本比较：先比数字段，再按 semver 规则处理预发布（<c>0.5.12-beta.2</c> 低于 <c>0.5.12</c>）。
        /// 构建元数据（<c>+mc1.20.1</c>）不参与比较。
        /// </summary>
        public static int Compare(string a, string b)
        {
            var (an, ap) = Split(a);
            var (bn, bp) = Split(b);

            var len = Math.Max(an.Length, bn.Length);
            for (var i = 0; i < len; i++)
            {
                var x = i < an.Length ? an[i] : 0;
                var y = i < bn.Length ? bn[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }

            // 数字相同：有预发布标签的更小（1.0.0-beta < 1.0.0）
            if (ap.Length == 0 && bp.Length == 0) return 0;
            if (ap.Length == 0) return 1;
            if (bp.Length == 0) return -1;

            var cmp = string.CompareOrdinal(ap, bp);
            return cmp == 0 ? 0 : cmp < 0 ? -1 : 1;
        }

        private static (int[] Numbers, string Pre) Split(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return (Array.Empty<int>(), "");

            var s = v.Trim().TrimStart('v', 'V');
            var plus = s.IndexOf('+');
            if (plus >= 0) s = s.Substring(0, plus);

            var dash = s.IndexOf('-');
            var pre = dash >= 0 ? s.Substring(dash + 1) : "";
            var main = dash >= 0 ? s.Substring(0, dash) : s;

            var nums = main.Split('.')
                .Select(x => int.TryParse(x, out var n) ? n : 0)
                .ToArray();

            return (nums, pre);
        }
    }
}
