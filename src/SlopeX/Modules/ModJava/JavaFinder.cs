using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModJava
{
    public sealed class JavaInfo
    {
        public string Path { get; set; }
        public string Home { get; set; }
        public string Version { get; set; } = "";
        public int Major { get; set; }
        public string Arch { get; set; } = "";
        public string Vendor { get; set; } = "";
        public bool Is64 { get; set; }

        public string Display => $"Java {Major}" + (string.IsNullOrEmpty(Vendor) ? "" : " · " + Vendor);

        public string Detail
        {
            get
            {
                var bits = (Is64 ? "64 位" : "32 位");
                return $"{Version} · {bits}" + (string.IsNullOrEmpty(Arch) ? "" : $" · {Arch}");
            }
        }
    }

    /// <summary>Java 运行时探测与匹配（对应 PCL2 的 ModJava）。</summary>
    public static class JavaFinder
    {
        private static List<JavaInfo> _cache;
        private static readonly object Gate = new object();

        public static List<JavaInfo> All(bool rescan = false)
        {
            lock (Gate)
            {
                if (_cache != null && !rescan) return _cache;
                var list = new List<JavaInfo>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var exe in CandidatePaths())
                {
                    try
                    {
                        var full = System.IO.Path.GetFullPath(exe);
                        if (seen.Contains(full) || !File.Exists(full)) continue;
                        seen.Add(full);
                        var info = Probe(full);
                        if (info != null) list.Add(info);
                    }
                    catch { /* 单个候选失败不影响整体 */ }
                }

                // 同一主版本只保留路径最短、版本号最高的一个
                _cache = list
                    .GroupBy(j => j.Major)
                    .Select(g => g.OrderByDescending(j => j.Version).First())
                    .OrderByDescending(j => j.Major)
                    .ToList();

                Log.Info($"Java 探测完成，共找到 {_cache.Count} 个：" +
                         string.Join("，", _cache.Select(j => $"{j.Major}({j.Arch})")));
                return _cache;
            }
        }

        private static IEnumerable<string> CandidatePaths()
        {
            // 1) 注册表（先收集，避免在 try/catch 中 yield）
            var registryHits = new List<string>();
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var sub in new[]
                         {
                             @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\Java Development Kit",
                             @"SOFTWARE\JavaSoft\Java Runtime Environment", @"SOFTWARE\JavaSoft\JRE",
                             @"SOFTWARE\WOW6432Node\JavaSoft\JDK",
                             @"SOFTWARE\WOW6432Node\JavaSoft\Java Runtime Environment"
                         })
                {
                    try
                    {
                        using var key = root.OpenSubKey(sub);
                        if (key == null) continue;
                        foreach (var name in key.GetSubKeyNames())
                        {
                            using var ver = key.OpenSubKey(name);
                            if (ver?.GetValue("JavaHome") is string home && !string.IsNullOrEmpty(home))
                                registryHits.Add(System.IO.Path.Combine(home, "bin", "javaw.exe"));
                        }
                    }
                    catch { /* 权限不足或无该项 */ }
                }
            }

            foreach (var hit in registryHits) yield return hit;

            // 2) 常见安装目录
            var roots = new List<string>();
            void AddRoot(Environment.SpecialFolder f)
            {
                var p = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(p)) roots.Add(p);
            }
            AddRoot(Environment.SpecialFolder.ProgramFiles);
            AddRoot(Environment.SpecialFolder.ProgramFilesX86);
            AddRoot(Environment.SpecialFolder.LocalApplicationData);
            AddRoot(Environment.SpecialFolder.UserProfile);

            var vendors = new[]
            {
                "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft", "Zulu", "BellSoft",
                "Amazon Corretto", "Semeru", "SapMachine", "JetBrains", "AdoptOpenJDK", "Temurin"
            };

            foreach (var root in roots.Distinct())
            {
                foreach (var vendor in vendors)
                {
                    var vdir = System.IO.Path.Combine(root, vendor);
                    if (!Directory.Exists(vdir)) continue;

                    foreach (var jdk in SafeDirs(vdir))
                    {
                        yield return System.IO.Path.Combine(jdk, "bin", "javaw.exe");
                        foreach (var inner in SafeDirs(jdk))
                            yield return System.IO.Path.Combine(inner, "bin", "javaw.exe");
                    }
                }

                // .jdks / gradle 缓存里的 JDK（可能多套一层版本目录）
                foreach (var extra in new[] { ".jdks", @"Programs\Eclipse Adoptium", @"scoop\apps" })
                {
                    var d = System.IO.Path.Combine(root, extra);
                    if (!Directory.Exists(d)) continue;
                    foreach (var jdk in SafeDirs(d))
                    {
                        yield return System.IO.Path.Combine(jdk, "bin", "javaw.exe");
                        foreach (var inner in SafeDirs(jdk))
                            yield return System.IO.Path.Combine(inner, "bin", "javaw.exe");
                    }
                }
            }

            // 3) 启动器自己管理的运行时（自动下载安装的 Java）
            foreach (var d in SafeDirs(Paths.RuntimeDir))
            {
                var javaw = System.IO.Path.Combine(d, "bin", "javaw.exe");
                yield return File.Exists(javaw) ? javaw : System.IO.Path.Combine(d, "bin", "java.exe");
                foreach (var inner in SafeDirs(d))
                {
                    var w = System.IO.Path.Combine(inner, "bin", "javaw.exe");
                    yield return File.Exists(w) ? w : System.IO.Path.Combine(inner, "bin", "java.exe");
                }
            }

            // 4) PATH 中的 java
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var seg in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string exe;
                try { exe = System.IO.Path.Combine(seg.Trim(), "javaw.exe"); }
                catch { continue; }
                if (File.Exists(exe)) yield return exe;
            }
        }

        private static IEnumerable<string> SafeDirs(string path)
        {
            try { return Directory.GetDirectories(path); }
            catch { return Array.Empty<string>(); }
        }

        public static JavaInfo Probe(string javawPath)
        {
            var javaExe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(javawPath), "java.exe");
            if (!File.Exists(javaExe)) javaExe = javawPath;

            var info = new JavaInfo
            {
                Path = javaExe,
                Home = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(javaExe))
            };

            try
            {
                var psi = new ProcessStartInfo(javaExe, "-XshowSettings:properties -version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null) return null;
                var text = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(8000))
                {
                    try { p.Kill(); } catch { }
                    return null;
                }

                string version = Match(text, @"java\.version\s*=\s*(\S+)") ?? Match(text, @"version\s+""([^""]+)""");
                string arch = Match(text, @"os\.arch\s*=\s*(\S+)");
                string vendor = Match(text, @"java\.vendor\s*=\s*(.+)");
                string bits = Match(text, @"sun\.arch\.data\.model\s*=\s*(\d+)");

                if (string.IsNullOrEmpty(version)) return null;

                info.Version = version;
                info.Major = ParseMajor(version);
                info.Arch = arch ?? "";
                info.Vendor = ShortVendor(vendor);
                info.Is64 = bits == "64" || (arch != null && (arch.Contains("64") || arch == "aarch64" || arch == "amd64"));
                return info.Major > 0 ? info : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Match(string text, string pattern)
        {
            var m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        private static string ShortVendor(string vendor)
        {
            if (string.IsNullOrEmpty(vendor)) return "";
            var v = vendor.Trim();
            if (v.Contains("Adoptium") || v.Contains("Temurin") || v.Contains("AdoptOpenJDK")) return "Adoptium";
            if (v.Contains("Oracle")) return "Oracle";
            if (v.Contains("Microsoft")) return "Microsoft";
            if (v.Contains("Azul") || v.Contains("Zulu")) return "Zulu";
            if (v.Contains("BellSoft") || v.Contains("Liberica")) return "Liberica";
            if (v.Contains("Amazon")) return "Corretto";
            if (v.Contains("JetBrains")) return "JBR";
            if (v.Contains("IBM") || v.Contains("Semeru")) return "Semeru";
            return v.Length > 16 ? v.Substring(0, 16) : v;
        }

        public static int ParseMajor(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return 0;
            var v = version.Trim();
            if (v.StartsWith("1."))
            {
                var rest = v.Substring(2);
                var dot = rest.IndexOf('.');
                var s = dot > 0 ? rest.Substring(0, dot) : rest;
                return int.TryParse(s, out var m8) ? m8 : 0;
            }

            var first = new string(v.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(first, out var m) ? m : 0;
        }

        /// <summary>某个 Minecraft 版本建议的 Java 主版本。</summary>
        public static int RequiredMajor(string mcVersion, int jsonMajor)
        {
            // 版本 JSON 里的 javaVersion.majorVersion 是权威值，优先采信
            if (jsonMajor > 0) return jsonMajor;
            if (string.IsNullOrWhiteSpace(mcVersion)) return 17;

            var nums = Regex.Match(mcVersion, @"^(\d+)\.(\d+)(?:\.(\d+))?");
            if (!nums.Success) return 17;

            int major = int.Parse(nums.Groups[1].Value);
            int minor = int.Parse(nums.Groups[2].Value);
            int patch = nums.Groups[3].Success ? int.Parse(nums.Groups[3].Value) : 0;

            // 2025 年后 Mojang 改用按年份编号（25.x / 26.x），这些版本至少需要 Java 21
            if (major != 1) return major >= 26 ? 25 : 21;
            if (minor >= 21 || (minor == 20 && patch >= 5)) return 21;
            if (minor >= 18) return 17;
            if (minor >= 17) return 16;
            return 8;
        }

        /// <summary>
        /// 该 Java 能否运行指定要求的版本。
        /// 关键点：<b>低于要求的主版本一定不行</b>（以前这里会静默退回低版本，导致游戏秒退）。
        /// </summary>
        public static bool IsSuitable(JavaInfo java, int required)
        {
            if (java == null) return false;
            if (required <= 0) return true;
            if (java.Major < required) return false;

            // Java 8 时代的旧版本不保证能在高版本 JRE 上运行（LWJGL2、反射限制），只认 8
            if (required <= 8) return java.Major == 8;
            return true;
        }

        /// <summary>找出能满足要求的最佳 Java；没有则返回 null（不再退回低版本）。</summary>
        public static JavaInfo FindBest(int required, bool rescan = false, List<JavaInfo> list = null)
        {
            var candidates = (list ?? All(rescan)).Where(j => IsSuitable(j, required)).ToList();
            if (candidates.Count == 0) return null;

            var exact = candidates.Where(j => j.Major == required).OrderByDescending(j => j.Is64).FirstOrDefault();
            if (exact != null) return exact;

            // 没有精确匹配时，优先用最接近的更高版本
            return candidates.OrderBy(j => j.Major).ThenByDescending(j => j.Is64).FirstOrDefault();
        }

        /// <summary>挑一个最合适的 Java；找不到兼容的返回 null。</summary>
        public static JavaInfo Pick(int required, List<JavaInfo> list = null) => FindBest(required, false, list);

        /// <summary>从用户指定路径解析 Java（支持目录或可执行文件）。</summary>
        public static JavaInfo FromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var p = path.Trim().Trim('"');
                if (Directory.Exists(p))
                {
                    var exe = System.IO.Path.Combine(p, "bin", "java.exe");
                    if (File.Exists(exe)) p = exe;
                    else
                    {
                        var javaw = System.IO.Path.Combine(p, "javaw.exe");
                        if (File.Exists(javaw)) p = javaw;
                    }
                }

                if (!File.Exists(p)) return null;

                var info = Probe(p);
                if (info != null) return info;

                return new JavaInfo
                {
                    Path = p,
                    Home = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(p) ?? ""),
                    Major = 17,
                    Version = "unknown"
                };
            }
            catch
            {
                return null;
            }
        }

        public static string ArchOfCurrentProcess =>
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x86_64";
    }
}
