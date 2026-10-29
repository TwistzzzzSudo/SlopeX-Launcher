using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModJava;
using SlopeX.Modules.ModMinecraft;

namespace SlopeX.Modules.ModLaunch
{
    public sealed class LaunchOptions
    {
        /// <summary>实际传给游戏的 --gameDir：开启版本隔离时是 versions\&lt;版本 ID&gt;。</summary>
        public string GameDir { get; set; }

        /// <summary>
        /// 游戏根目录（.minecraft 根）。libraries / assets / natives 都从这里取——
        /// 它们与版本无关，隔离时不应跟着复制一份。
        /// </summary>
        public string RootGameDir { get; set; }

        public string VersionName { get; set; }
        public string PlayerName { get; set; } = "Steve";
        public string Uuid { get; set; }
        public string AccessToken { get; set; } = "0";
        public string JavaPath { get; set; }
        public string NativesDir { get; set; }
        public int MemoryMb { get; set; } = 4096;
        public int MinMemoryMb { get; set; } = 1024;
        public string JvmExtra { get; set; } = "";
        public bool CustomResolution { get; set; }
        public int Width { get; set; } = 854;
        public int Height { get; set; } = 480;
        public string Arch { get; set; } = JavaFinder.ArchOfCurrentProcess;

        public Dictionary<string, bool> Features => new Dictionary<string, bool>
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = CustomResolution,
            ["has_quick_plays_support"] = false,
            ["is_quick_play_singleplayer"] = false,
            ["is_quick_play_multiplayer"] = false,
            ["is_quick_play_realms"] = false
        };
    }

    public sealed class LaunchPlan
    {
        public string JavaPath { get; set; }
        public List<string> Args { get; } = new List<string>();
        public string WorkingDir { get; set; }
        public string NativesDir { get; set; }
        public int ExtractedNatives { get; set; }

        /// <summary>仅用于展示/调试的命令行。</summary>
        public string ToCommandLine()
        {
            var sb = new StringBuilder();
            sb.Append('"').Append(JavaPath).Append('"');
            foreach (var a in Args)
            {
                sb.Append(' ');
                if (a.Contains(' ') || a.Contains('\t') || a.Contains('"'))
                    sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
                else
                    sb.Append(a);
            }
            return sb.ToString();
        }
    }

    /// <summary>规则引擎：按操作系统 / 架构 / feature 决定某条库或参数是否生效。</summary>
    public static class Rules
    {
        public const string OsName = "windows";

        public static bool Allowed(JsonArray rules, Dictionary<string, bool> features, string arch = null)
        {
            if (rules == null || rules.Count == 0) return true;

            var allow = false;
            foreach (var node in rules)
            {
                if (!(node is JsonObject rule)) continue;
                if (!Matches(rule, features, arch)) continue;

                var action = J.Str(rule, "action");
                allow = string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase);
            }
            return allow;
        }

        private static bool Matches(JsonObject rule, Dictionary<string, bool> features, string arch)
        {
            var os = J.Obj(rule, "os");
            if (os != null)
            {
                var name = J.Str(os, "name");
                if (!string.IsNullOrEmpty(name) && !string.Equals(name, OsName, StringComparison.OrdinalIgnoreCase))
                    return false;

                var needArch = J.Str(os, "arch");
                if (!string.IsNullOrEmpty(needArch))
                {
                    var current = arch ?? JavaFinder.ArchOfCurrentProcess;
                    // 32 位系统也能满足 x86 规则
                    var is32 = current == "x86";
                    if (!string.Equals(needArch, current, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!(needArch == "x86_64" && current == "amd64") &&
                            !(needArch == "x86" && is32))
                            return false;
                    }
                }

                var versionRegex = J.Str(os, "version");
                if (!string.IsNullOrEmpty(versionRegex))
                {
                    try
                    {
                        if (!Regex.IsMatch(Environment.OSVersion.Version.ToString(), versionRegex))
                            return false;
                    }
                    catch { /* 正则有问题时忽略该条件 */ }
                }
            }

            var feats = J.Obj(rule, "features");
            if (feats != null)
            {
                foreach (var kv in feats)
                {
                    var want = J.IsTruthy(kv.Value);
                    features.TryGetValue(kv.Key, out var have);
                    if (want != have) return false;
                }
            }

            return true;
        }
    }

    public sealed class NativeJar
    {
        public string Path { get; set; }
        public List<string> Excludes { get; } = new List<string>();
    }

    /// <summary>启动参数构造与游戏进程管理（对应 PCL2 的 ModLaunch）。</summary>
    public static class LaunchBuilder
    {
        public static LaunchPlan Build(ResolvedVersion version, LaunchOptions o, string javaExe)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));
            if (string.IsNullOrWhiteSpace(o.GameDir)) throw new InvalidOperationException("游戏目录未设置");

            // 版本隔离时 GameDir 是 versions\<id>，而 libraries / assets / natives 仍取自根目录
            var root = string.IsNullOrWhiteSpace(o.RootGameDir) ? o.GameDir : o.RootGameDir;

            var plan = new LaunchPlan
            {
                JavaPath = javaExe,
                WorkingDir = o.GameDir,
                NativesDir = string.IsNullOrEmpty(o.NativesDir)
                    ? System.IO.Path.Combine(Paths.VersionsDir(root), version.Id, "natives")
                    : o.NativesDir
            };

            var libsDir = Paths.LibrariesDir(root);
            var classpath = new List<string>();
            var nativeJars = new List<NativeJar>();
            var features = o.Features;
            var missing = new List<string>();

            foreach (var lib in version.Libraries)
            {
                if (!Rules.Allowed(J.Arr(lib, "rules"), features, o.Arch))
                    continue;

                var name = J.Str(lib, "name") ?? "";
                var path = MavenPath(name);
                if (string.IsNullOrEmpty(path)) continue;

                var downloads = J.Obj(lib, "downloads");
                var artifact = J.Obj(downloads, "artifact");
                var artifactPath = J.Str(artifact, "path") ?? path;
                var fullArtifact = System.IO.Path.Combine(libsDir, artifactPath.Replace('/', System.IO.Path.DirectorySeparatorChar));

                // 老式原生库：natives 字段指向 classifier
                var natives = J.Obj(lib, "natives");
                if (natives != null)
                {
                    var classifier = J.Str(natives, Rules.OsName);
                    if (!string.IsNullOrEmpty(classifier))
                    {
                        var classifiers = J.Obj(downloads, "classifiers");
                        var natArtifact = J.Obj(classifiers, classifier);
                        var natPath = J.Str(natArtifact, "path") ?? MavenPath(WithClassifier(name, classifier));
                        if (!string.IsNullOrEmpty(natPath))
                        {
                            var fullNat = System.IO.Path.Combine(libsDir, natPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                            if (File.Exists(fullNat))
                            {
                                var nj = new NativeJar { Path = fullNat };
                                var extract = J.Obj(lib, "extract");
                                var excludes = J.Arr(extract, "exclude");
                                if (excludes != null)
                                    foreach (var ex in excludes)
                                        if (ex != null) nj.Excludes.Add(ex.ToString());
                                nativeJars.Add(nj);
                            }
                            else missing.Add(natPath);
                        }
                    }

                    // 纯原生库包（只有 classifiers、没有 artifact）不进 classpath，只解压
                    if (artifact == null && natives != null)
                        continue;
                }

                if (File.Exists(fullArtifact))
                    classpath.Add(fullArtifact);
                else
                    missing.Add(artifactPath);
            }

            // 客户端本体
            var jar = version.JarPath;
            if (!string.IsNullOrEmpty(jar) && File.Exists(jar))
                classpath.Add(jar);
            else
                missing.Add("client.jar");

            if (missing.Count > 0)
                throw new FileNotFoundException(
                    $"缺少 {missing.Count} 个必要文件（例如 {MissingSample(missing)}），请先在下载页补全该版本。");

            var cp = string.Join(";", classpath);
            plan.ExtractedNatives = ExtractNatives(nativeJars, plan.NativesDir);

            var vars = BuildVariables(version, o, cp, plan.NativesDir, root);

            // ---- JVM 参数 ----
            var jvm = new List<string>();
            var minMem = Math.Max(256, Math.Min(o.MinMemoryMb, o.MemoryMb));
            jvm.Add($"-Xmx{o.MemoryMb}M");
            jvm.Add($"-Xms{minMem}M");
            jvm.Add("-Dfile.encoding=UTF-8");
            jvm.Add("-Dstdout.encoding=UTF-8");
            jvm.Add("-Dstderr.encoding=UTF-8");

            if (version.JvmArgs != null && version.JvmArgs.Count > 0)
                CollectArgs(version.JvmArgs, jvm, vars, features, o.Arch);
            else
            {
                jvm.Add("-Djava.library.path=" + vars["natives_directory"]);
                jvm.Add("-Dminecraft.launcher.brand=" + vars["launcher_name"]);
                jvm.Add("-Dminecraft.launcher.version=" + vars["launcher_version"]);
                jvm.Add("-cp");
                jvm.Add(cp);
            }

            if (!jvm.Any(a => a == "-cp" || a == "-classpath"))
            {
                var idx = jvm.Count;
                jvm.Insert(idx, "-cp");
                jvm.Insert(idx + 1, cp);
            }

            if (!jvm.Any(a => a.StartsWith("-Djava.library.path")))
                jvm.Add("-Djava.library.path=" + vars["natives_directory"]);

            EnsureIgnoreListCoversJar(jvm, jar);

            if (!string.IsNullOrWhiteSpace(o.JvmExtra))
                foreach (var extra in SplitArgs(o.JvmExtra))
                    jvm.Add(extra);

            plan.Args.AddRange(jvm.Where(a => !string.IsNullOrWhiteSpace(a)));

            // log4j 配置（1.7+ 版本需要，缺失时部分版本日志会报错）
            if (!string.IsNullOrEmpty(version.LogFileId) && !string.IsNullOrEmpty(version.LogArgument))
            {
                var logPath = System.IO.Path.Combine(Paths.AssetsDir(root), "log_configs", version.LogFileId);
                if (File.Exists(logPath))
                    plan.Args.Add(version.LogArgument.Replace("${path}", logPath));
            }

            if (string.IsNullOrEmpty(version.MainClass))
                throw new InvalidOperationException("版本缺少 mainClass，无法启动");

            plan.Args.Add(version.MainClass);

            // ---- 游戏参数 ----
            var game = new List<string>();
            if (version.GameArgs != null && version.GameArgs.Count > 0)
                CollectArgs(version.GameArgs, game, vars, features, o.Arch);
            else if (!string.IsNullOrEmpty(version.MinecraftArguments))
                foreach (var seg in SplitArgs(version.MinecraftArguments))
                    game.Add(Replace(seg, vars));

            game.RemoveAll(a => a.Contains("${"));

            if (o.CustomResolution && !game.Any(a => a == "--width"))
            {
                game.Add("--width");
                game.Add(o.Width.ToString());
                game.Add("--height");
                game.Add(o.Height.ToString());
            }

            plan.Args.AddRange(game.Where(a => !string.IsNullOrWhiteSpace(a)));
            return plan;
        }

        private static string MissingSample(List<string> missing) => missing.FirstOrDefault() ?? "unknown";

        private static string WithClassifier(string name, string classifier)
        {
            var parts = name.Split(':');
            if (parts.Length == 3) return name + ":" + classifier;
            if (parts.Length == 4) return parts[0] + ":" + parts[1] + ":" + parts[2] + ":" + classifier;
            return name;
        }

        /// <summary>把 Maven 坐标转成 libraries 下的相对路径。</summary>
        public static string MavenPath(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var parts = name.Split(':');
            if (parts.Length < 3) return null;

            var group = parts[0].Replace('.', '/');
            var artifact = parts[1];
            var version = parts[2];
            var classifier = parts.Length > 3 ? parts[3] : null;

            var file = artifact + "-" + version + (classifier != null ? "-" + classifier : "") + ".jar";
            return $"{group}/{artifact}/{version}/{file}";
        }

        /// <summary>
        /// Forge 的 jvm 参数里写的是 <c>-DignoreList=…${version_name}.jar</c>，本意是把"正在加载的那个
        /// jar"排除在模块层之外。但 Forge 自己没有 jar，真正加载的是父版本（原版）的 jar，于是
        /// <c>${version_name}</c> 展开后名字对不上，原版 jar 会被 BootstrapLauncher 当成自动模块
        /// （如 <c>_1._20._1</c>），与 FML 提供的 <c>minecraft</c> 模块导出同一个 package，启动即报
        /// <c>java.lang.module.ResolutionException</c>。这里把实际 jar 文件名补进 ignoreList（幂等）。
        /// </summary>
        private static void EnsureIgnoreListCoversJar(List<string> jvm, string jarPath)
        {
            if (string.IsNullOrEmpty(jarPath)) return;

            var fileName = System.IO.Path.GetFileName(jarPath);
            if (string.IsNullOrEmpty(fileName)) return;

            const string prefix = "-DignoreList=";

            for (var i = 0; i < jvm.Count; i++)
            {
                if (!jvm[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var items = jvm[i].Substring(prefix.Length).Split(',');
                if (items.Any(x => string.Equals(x.Trim(), fileName, StringComparison.OrdinalIgnoreCase)))
                    return;

                jvm[i] = jvm[i] + "," + fileName;
                return;
            }
        }

        private static Dictionary<string, string> BuildVariables(ResolvedVersion v, LaunchOptions o, string cp, string nativesDir, string root)        {
            var assetIndex = v.EffectiveAssetIndex;
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["auth_player_name"] = string.IsNullOrWhiteSpace(o.PlayerName) ? "Player" : o.PlayerName,
                ["version_name"] = v.Id,
                ["game_directory"] = o.GameDir,
                ["assets_root"] = Paths.AssetsDir(root),
                ["assets_index_name"] = assetIndex,
                ["auth_uuid"] = string.IsNullOrEmpty(o.Uuid) ? OfflineUuid(o.PlayerName) : o.Uuid,
                ["auth_access_token"] = string.IsNullOrEmpty(o.AccessToken) ? "0" : o.AccessToken,
                ["auth_session"] = string.IsNullOrEmpty(o.AccessToken) ? "0" : o.AccessToken,
                ["clientid"] = "slopex-launcher",
                ["auth_xuid"] = "0",
                ["user_type"] = "msa",
                ["version_type"] = v.Type ?? "release",
                ["natives_directory"] = nativesDir,
                ["launcher_name"] = "SlopeX",
                ["launcher_version"] = "1.0.0",
                ["classpath"] = cp,
                ["classpath_separator"] = ";",
                ["library_directory"] = Paths.LibrariesDir(root),
                ["game_assets"] = System.IO.Path.Combine(Paths.AssetsDir(root), "virtual", "legacy"),
                ["resolution_width"] = o.Width.ToString(),
                ["resolution_height"] = o.Height.ToString()
            };
        }

        private static void CollectArgs(JsonArray source, List<string> target, Dictionary<string, string> vars,
            Dictionary<string, bool> features, string arch)
        {
            foreach (var node in source)
            {
                if (node is JsonValue jv)
                {
                    target.Add(Replace(jv.ToString(), vars));
                    continue;
                }

                if (!(node is JsonObject ob)) continue;
                if (!Rules.Allowed(J.Arr(ob, "rules"), features, arch)) continue;

                var value = ob["value"];
                if (value is JsonArray arr)
                {
                    foreach (var item in arr)
                        if (item != null) target.Add(Replace(item.ToString(), vars));
                }
                else if (value != null)
                {
                    target.Add(Replace(value.ToString(), vars));
                }
            }
        }

        private static string Replace(string text, Dictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains("${")) return text;
            var result = text;
            foreach (var kv in vars)
                result = result.Replace("${" + kv.Key + "}", kv.Value ?? "");
            return result;
        }

        /// <summary>按空格切分参数，保留引号内容。</summary>
        public static IEnumerable<string> SplitArgs(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) yield break;

            var sb = new StringBuilder();
            var inQuotes = false;
            foreach (var c in commandLine)
            {
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (c == ' ' && !inQuotes)
                {
                    if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        private static int ExtractNatives(List<NativeJar> jars, string nativesDir)
        {
            if (jars.Count == 0 && !Directory.Exists(nativesDir)) return 0;

            try
            {
                if (Directory.Exists(nativesDir)) Directory.Delete(nativesDir, true);
            }
            catch (Exception ex)
            {
                Log.Warn($"清理 natives 目录失败：{ex.Message}");
            }
            Directory.CreateDirectory(nativesDir);

            var count = 0;
            foreach (var nj in jars)
            {
                try
                {
                    using var zip = ZipFile.OpenRead(nj.Path);
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        if (nj.Excludes.Any(x => entry.FullName.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;

                        var dest = System.IO.Path.Combine(nativesDir, entry.Name);
                        if (File.Exists(dest)) continue;
                        entry.ExtractToFile(dest, true);
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"解压原生库失败 {System.IO.Path.GetFileName(nj.Path)}：{ex.Message}");
                }
            }
            return count;
        }

        /// <summary>离线 UUID：与原版离线模式一致（md5 of "OfflinePlayer:name"）。</summary>
        public static string OfflineUuid(string playerName)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + (playerName ?? "")));
            var hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

            // 按 UUID v3 规则设置版本位
            var chars = hex.ToCharArray();
            chars[12] = '3';
            chars[16] = "89ab"[Convert.ToInt32(chars[16].ToString(), 16) % 4];
            var s = new string(chars);
            return $"{s.Substring(0, 8)}-{s.Substring(8, 4)}-{s.Substring(12, 4)}-{s.Substring(16, 4)}-{s.Substring(20, 12)}";
        }
    }

    /// <summary>游戏进程封装，负责输出转发与退出回调。</summary>
    public sealed class GameProcess
    {
        private Process _process;

        public event Action<string> Output;
        public event Action<int> Exited;

        public bool IsRunning
        {
            get
            {
                try { return _process != null && !_process.HasExited; }
                catch { return false; }
            }
        }

        public int Pid
        {
            get
            {
                try { return _process?.Id ?? 0; }
                catch { return 0; }
            }
        }

        public void Start(LaunchPlan plan)
        {
            var psi = new ProcessStartInfo(plan.JavaPath)
            {
                WorkingDirectory = plan.WorkingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var a in plan.Args) psi.ArgumentList.Add(a);

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += (s, e) => { if (e.Data != null) Output?.Invoke(e.Data); };
            _process.ErrorDataReceived += (s, e) => { if (e.Data != null) Output?.Invoke(e.Data); };
            _process.Exited += (s, e) =>
            {
                var code = -1;
                try { code = _process.ExitCode; } catch { }
                Exited?.Invoke(code);
            };

            if (!_process.Start())
                throw new InvalidOperationException("无法创建游戏进程，请检查 Java 路径是否正确。");

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            Log.Info($"已启动游戏进程，PID = {_process.Id}");
            Log.Trace("命令行：" + plan.ToCommandLine());
        }

        public void Kill()
        {
            try
            {
                if (_process == null || _process.HasExited) return;
                _process.Kill(true);
                Log.Info("已结束游戏进程");
            }
            catch (Exception ex)
            {
                Log.Warn("结束进程失败：" + ex.Message);
            }
        }
    }
}
