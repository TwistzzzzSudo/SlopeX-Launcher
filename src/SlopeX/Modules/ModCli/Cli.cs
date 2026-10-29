using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using SlopeX.Modules.ModAccount;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModDownload;
using SlopeX.Modules.ModJava;
using SlopeX.Modules.ModLaunch;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModCli
{
    /// <summary>
    /// 无界面模式：便于自动化测试与排障。
    /// SlopeX.exe --cli [list|java|manifest|install|plan|verify|launch] [参数]
    /// </summary>
    public static class Cli
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int handle);

        private static string _outFile;
        private static StreamWriter _out;
        private static readonly object WriteGate = new object();

        public static int Run(string[] argv)
        {
            AttachToConsole();
            _outFile = Path.Combine(Paths.LogDir, "cli.log");
            try { File.WriteAllText(_outFile, ""); } catch { }

            try
            {
                return RunCore(argv);
            }
            catch (Exception ex)
            {
                Out("ERROR " + ex.GetType().Name + ": " + ex.Message);
                Out(ex.StackTrace ?? "");
                return 1;
            }
        }

        private static int RunCore(string[] argv)
        {
            // 过滤 --cli 自身
            var args = argv.Where(a => a != "--cli").ToList();
            var cmd = args.Count > 0 ? args[0].ToLowerInvariant() : "help";
            args.RemoveAt(0);

            // 支持 --dir <路径>
            var dirIdx = args.FindIndex(a => a == "--dir" || a == "-d");
            if (dirIdx >= 0 && dirIdx + 1 < args.Count)
            {
                Config.Current.GameDir = args[dirIdx + 1];
                args.RemoveRange(dirIdx, 2);
            }

            Out("SlopeX Launcher " + "1.0.0" + "  |  gameDir = " + Config.Current.GameDir);

            switch (cmd)
            {
                case "help":
                    Out("用法：SlopeX.exe --cli <命令> [参数]");
                    Out("  list                     列出已安装版本");
                    Out("  java                     列出检测到的 Java");
                    Out("  java-ensure <主版本>     确保存在可用 Java，缺失则自动下载安装");
                    Out("  manifest [数量]          列出可安装的版本（默认 20 条）");
                    Out("  install <版本>           安装原版版本");
                    Out("  verify <版本>            校验依赖/资源是否齐全");
                    Out("  fix-libs <版本>          补齐缺失的依赖库（走镜像）");
                    Out("  plan <版本>              打印启动命令行（不启动）");
                    Out("  launch <版本>            启动游戏");
                    Out("  fabric-list <原版版本>   列出该原版可用的 Fabric 加载器");
                    Out("  install-fabric <原版版本> [加载器版本]   安装 Fabric（缺原版会先补原版）");
                    Out("  forge-list <原版版本>    查询该原版的 Forge 推荐版本");
                    Out("  install-forge <原版版本> [加载器版本]   安装 Forge（调官方 installer，缺原版会先补）");
                    Out("  neoforge-list <原版版本>   列出该原版可用的 NeoForge 版本");
                    Out("  install-neoforge <原版版本> [加载器版本]   安装 NeoForge（镜像优先，缺原版会先补）");
                    Out("  pack-info <整合包路径>   识别整合包格式与版本，不安装");
                    Out("  import-pack <整合包路径> [基础版本]   导入整合包（.mrpack / CurseForge / MultiMC / 普通 zip）");
                    Out("  login                    微软正版登录（设备码，在浏览器里确认）");
                    Out("  account                  查看当前账号");
                    Out("  logout                   退出正版账号（回到离线）");
                    return 0;

                case "list":
                    return ListVersions();

                case "java":
                    return ListJava();

                case "java-ensure":
                    return EnsureJava(args.Count > 0 && int.TryParse(args[0], out var jm) ? jm : 21);

                case "manifest":
                    return Manifest(args.Count > 0 && int.TryParse(args[0], out var n) ? n : 20);

                case "install":
                    return Install(args.FirstOrDefault());

                case "fix-libs":
                    return FixLibs(args.FirstOrDefault());

                case "verify":
                    return Verify(args.FirstOrDefault());

                case "plan":
                    return Plan(args.FirstOrDefault());

                case "launch":
                    return Launch(args.FirstOrDefault());

                case "fabric-list":
                    return FabricList(args.FirstOrDefault());

                case "install-fabric":
                    return InstallFabric(args.FirstOrDefault(), args.Skip(1).FirstOrDefault());

                case "forge-list":
                    return ForgeList(args.FirstOrDefault());

                case "install-forge":
                    return InstallForge(args.FirstOrDefault(), args.Skip(1).FirstOrDefault());

                case "neoforge-list":
                    return NeoForgeList(args.FirstOrDefault());

                case "install-neoforge":
                    return InstallNeoForge(args.FirstOrDefault(), args.Skip(1).FirstOrDefault());

                case "pack-info":
                    return PackInfo(args.FirstOrDefault());

                case "import-pack":
                    return ImportPack(args.FirstOrDefault(), args.Skip(1).FirstOrDefault());

                case "login":
                    return Login();

                case "account":
                    return ShowAccount();

                case "logout":
                    Msa.Logout();
                    Out("已退出正版账号，后续启动使用离线登录");
                    return 0;

                default:
                    Out("未知命令：" + cmd);
                    return 2;
            }
        }

        private static int ListVersions()
        {
            var list = VersionManager.Scan(Config.Current.GameDir);
            Out($"共 {list.Count} 个已安装版本：");
            foreach (var v in list)
                Out($"  {v.Id,-32} {v.Loader,-10} 基础版本 {v.BaseVersion,-10} {v.CreatedHint}  jar={(v.HasJar ? "有" : "无")}");
            return 0;
        }

        private static int ListJava()
        {
            var list = JavaFinder.All(true);
            Out($"共 {list.Count} 个 Java：");
            foreach (var j in list)
                Out($"  Java {j.Major,-3} {j.Arch,-8} {j.Vendor,-12} {j.Version,-14} {j.Path}");
            if (list.Count == 0) Out("  未检测到任何 Java 运行时");
            return 0;
        }

        private static int EnsureJava(int major)
        {
            Out($"确保存在 Java {major}……");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<JavaInstallProgress>(p => Out("  " + p.Text));
            try
            {
                var java = JavaProvisioner.EnsureAsync(major, progress).GetAwaiter().GetResult();
                sw.Stop();
                Out($"就绪：Java {java.Major}（{java.Vendor}）{java.Version}  {java.Path}");
                Out($"耗时 {sw.Elapsed.TotalSeconds:0.0} 秒");
                return 0;
            }
            catch (Exception ex)
            {
                Out("失败：" + ex.Message);
                return 1;
            }
        }

        private static int Manifest(int count)
        {
            var list = Mojang.GetVersionListAsync(true).GetAwaiter().GetResult();
            Out($"版本清单共 {list.Count} 条，前 {count} 条：");
            foreach (var v in list.Take(count))
                Out($"  {v.Id,-24} {v.TypeLabel,-10} {v.DateLabel}");
            return 0;
        }

        private static int Install(string versionId)
        {
            if (string.IsNullOrEmpty(versionId)) { Out("请指定要安装的版本 ID"); return 2; }

            var task = new InstallTask { VersionId = versionId };
            var lastMilestone = -10;
            task.Updated += t =>
            {
                var p = (int)t.Percent;
                if (p >= lastMilestone + 10 || t.Completed || t.Failed)
                {
                    lastMilestone = p / 10 * 10;
                    Out($"[{p,3}%] {t.Stage} {t.SizeText} {t.SpeedText}".TrimEnd());
                }
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            task.RunAsync().GetAwaiter().GetResult();
            sw.Stop();

            if (task.Failed)
            {
                Out("安装失败：" + task.Error);
                return 1;
            }
            Out($"安装完成，耗时 {sw.Elapsed.TotalSeconds:0.0} 秒");
            return 0;
        }

        private static int Login()
        {
            var existing = Msa.Current;
            if (existing != null)
                Out($"当前已登录：{existing.Name}（{existing.Uuid}），重新登录会覆盖");

            DeviceCodeInfo code;
            try
            {
                code = Msa.RequestDeviceCodeAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Out("申请设备码失败：" + ex.Message);
                return 1;
            }

            Out("");
            Out("==== 请在浏览器完成登录 ====");
            Out("  网址：" + code.VerificationUri);
            Out("  代码：" + code.UserCode);
            Out($"  （{code.ExpiresIn / 60} 分钟内有效）");
            Out("");

            var progress = new Progress<string>(s => Out("  " + s));
            try
            {
                var session = Msa.WaitForLoginAsync(code, progress, CancellationToken.None).GetAwaiter().GetResult();
                Out($"登录成功：{session.Name}  {session.Uuid}");
                Out($"令牌文件：{Msa.StoreFile}");
                return 0;
            }
            catch (Exception ex)
            {
                Out("登录失败：" + ex.Message);
                return 1;
            }
        }

        private static int ShowAccount()
        {
            var session = Msa.Current;
            if (session == null)
            {
                Out("当前为离线登录：" + Config.Current.PlayerName);
                Out("（可用 --cli login 登录微软账号）");
                return 0;
            }

            var state = session.Expired ? "已过期，启动时自动刷新" : "有效";
            Out($"正版账号：{session.Name}");
            Out($"  UUID：{session.Uuid}");
            Out($"  令牌：{state}（到期 {session.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm}）");
            return 0;
        }

        private static int FabricList(string mcVersion)
        {
            if (string.IsNullOrEmpty(mcVersion)) { Out("用法：fabric-list <原版版本>"); return 2; }

            try
            {
                var list = FabricInstaller.GetLoadersAsync(mcVersion).GetAwaiter().GetResult();
                if (list.Count == 0) { Out($"{mcVersion} 没有可用的 Fabric 加载器"); return 1; }

                Out($"{mcVersion} 可用加载器 {list.Count} 个（最新 15 个）：");
                foreach (var l in list.Take(15))
                    Out($"  {l.Version,-16} {(l.Stable ? "稳定" : "测试")}");

                var latest = list.FirstOrDefault(l => l.Stable)?.Version ?? list[0].Version;
                Out($"最新稳定版：{latest}");
                return 0;
            }
            catch (Exception ex)
            {
                Out("查询失败：" + ex.Message);
                return 1;
            }
        }

        private static int InstallFabric(string mcVersion, string loaderVersion)
        {
            if (string.IsNullOrEmpty(mcVersion))
            {
                Out("用法：install-fabric <原版版本> [加载器版本]");
                return 2;
            }

            var task = new InstallTask
            {
                Kind = InstallKind.Fabric,
                BaseVersion = mcVersion,
                LoaderVersion = loaderVersion,
                VersionId = string.IsNullOrEmpty(loaderVersion)
                    ? "fabric-" + mcVersion
                    : FabricInstaller.BuildVersionId(loaderVersion, mcVersion)
            };

            var lastMilestone = -10;
            task.Updated += t =>
            {
                var p = (int)t.Percent;
                if (p >= lastMilestone + 10 || t.Completed || t.Failed)
                {
                    lastMilestone = p / 10 * 10;
                    Out($"[{p,3}%] {t.Stage} {t.SizeText} {t.SpeedText}".TrimEnd());
                }
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string installedId = null;
            try
            {
                installedId = FabricInstaller.InstallAsync(Config.Current.GameDir, mcVersion, loaderVersion,
                    task, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Out("安装失败：" + ex.Message);
                return 1;
            }
            sw.Stop();

            Out($"Fabric 安装完成：{installedId}（耗时 {sw.Elapsed.TotalSeconds:0.0} 秒）");
            Out($"游戏目录：{VersionEnv.Resolve(Config.Current.GameDir, installedId)}");
            return 0;
        }

        private static int NeoForgeList(string mcVersion)
        {
            if (string.IsNullOrEmpty(mcVersion)) { Out("用法：neoforge-list <原版版本>"); return 2; }

            try
            {
                var all = NeoForgeInstaller.GetVersionsAsync(mcVersion).GetAwaiter().GetResult();
                if (all.Count == 0)
                {
                    Out($"NeoForge 没有为 {mcVersion} 提供版本" +
                        (NeoForgeInstaller.PrefixOf(mcVersion) == "20.1"
                            ? "（1.20.1 时代发布为 net.neoforged:forge，本启动器不支持该线）"
                            : "（NeoForge 只支持 1.20.2 及以后）"));
                    return 1;
                }

                Out($"原版 {mcVersion} 的 NeoForge 版本：共 {all.Count} 个，最新 {all[0]}");
                foreach (var v in all.Take(8)) Out("  " + v);
                Out($"安装器：{NeoForgeInstaller.InstallerUrl(all[0])}");
                return 0;
            }
            catch (Exception ex)
            {
                Out("查询失败：" + ex.Message);
                return 1;
            }
        }

        private static int InstallNeoForge(string mcVersion, string loaderVersion)
        {
            if (string.IsNullOrEmpty(mcVersion))
            {
                Out("用法：install-neoforge <原版版本> [加载器版本]");
                return 2;
            }

            var task = new InstallTask
            {
                Kind = InstallKind.NeoForge,
                BaseVersion = mcVersion,
                LoaderVersion = loaderVersion,
                VersionId = string.IsNullOrEmpty(loaderVersion)
                    ? "neoforge-" + mcVersion
                    : NeoForgeInstaller.BuildVersionId(loaderVersion)
            };

            var lastStage = "";
            var lastBeat = -1;
            task.Updated += t =>
            {
                var beat = (int)(DateTime.Now.Second / 15);
                if (t.Stage != lastStage || beat != lastBeat || t.Completed || t.Failed)
                {
                    lastStage = t.Stage;
                    lastBeat = beat;
                    Out($"{t.Stage} {t.SizeText}".TrimEnd());
                }
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string installedId = null;
            try
            {
                installedId = NeoForgeInstaller.InstallAsync(Config.Current.GameDir, mcVersion, loaderVersion,
                    task, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Out("安装失败：" + ex.Message);
                return 1;
            }

            Out($"NeoForge 安装完成：{installedId}（耗时 {sw.Elapsed.TotalSeconds:0.0} 秒）");
            Out($"游戏目录：{VersionEnv.Resolve(Config.Current.GameDir, installedId)}");
            return 0;
        }

        private static int FixLibs(string versionId)
        {
            if (string.IsNullOrEmpty(versionId)) { Out("用法：fix-libs <版本>"); return 2; }

            var task = new InstallTask { VersionId = versionId };

            try
            {
                var n = InstallerSupport.EnsureLibrariesAsync(Config.Current.GameDir, versionId, task,
                    CancellationToken.None, "https://bmclapi2.bangbang93.com/maven/").GetAwaiter().GetResult();
                Out(n == 0 ? "依赖已齐全，无需补齐" : $"已补齐 {n} 个依赖");
                return 0;
            }
            catch (Exception ex)
            {
                Out("补齐失败：" + ex.Message);
                return 1;
            }
        }

        private static int PackInfo(string zipPath)
        {
            if (string.IsNullOrEmpty(zipPath)) { Out("用法：pack-info <整合包路径>"); return 2; }

            try
            {
                var plan = ModpackInstaller.Inspect(zipPath);
                Out($"格式      ：{plan.FormatLabel}");
                Out($"名称      ：{plan.Name} {plan.PackVersion}".TrimEnd());
                Out($"Minecraft ：{plan.McVersion}");
                Out($"加载器    ：{(string.IsNullOrEmpty(plan.LoaderKind) ? "原版" : plan.LoaderKind + " " + plan.LoaderVersion)}");
                Out($"文件清单  ：{plan.Files.Count} 个" +
                    (plan.Format == PackFormat.CurseForge ? "（CurseForge 需在线解析文件名）" : ""));
                Out($"覆盖目录  ：{(string.IsNullOrEmpty(plan.OverridesPrefix) ? "无" : plan.OverridesPrefix)}");
                Out($"建议版本ID：{plan.SuggestVersionId()}");

                foreach (var w in plan.Warnings) Out("提示：" + w);
                return 0;
            }
            catch (Exception ex)
            {
                Out("解析失败：" + ex.Message);
                return 1;
            }
        }

        private static int ImportPack(string zipPath, string baseVersion)
        {
            if (string.IsNullOrEmpty(zipPath))
            {
                Out("用法：import-pack <整合包路径> [基础版本]");
                return 2;
            }

            if (!File.Exists(zipPath))
            {
                Out("找不到文件：" + zipPath);
                return 2;
            }

            var task = new InstallTask
            {
                Kind = InstallKind.Modpack,
                PackPath = zipPath,
                BaseVersionFallback = baseVersion,
                VersionId = Path.GetFileNameWithoutExtension(zipPath)
            };

            var lastStage = "";
            var lastPct = -10;
            task.Updated += t =>
            {
                var pct = (int)t.Percent;
                if (t.Stage != lastStage || pct >= lastPct + 10 || t.Completed || t.Failed)
                {
                    lastStage = t.Stage;
                    lastPct = pct / 10 * 10;
                    Out($"[{pct,3}%] {t.Stage} {t.SizeText} {t.SpeedText}".TrimEnd());
                }
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string packId = null;
            try
            {
                packId = ModpackInstaller.InstallAsync(Config.Current.GameDir, zipPath, baseVersion,
                    task, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Out("导入失败：" + ex.Message);
                return 1;
            }

            Out($"整合包导入完成：{packId}（耗时 {sw.Elapsed.TotalSeconds:0.0} 秒）");
            Out($"游戏目录：{VersionEnv.Resolve(Config.Current.GameDir, packId)}");
            return 0;
        }

        private static int ForgeList(string mcVersion)
        {
            if (string.IsNullOrEmpty(mcVersion)) { Out("用法：forge-list <原版版本>"); return 2; }

            try
            {
                var v = ForgeInstaller.GetRecommendedAsync(mcVersion).GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(v))
                {
                    Out($"Forge 没有为 {mcVersion} 提供安装包");
                    return 1;
                }

                Out($"原版 {mcVersion} 的 Forge 推荐版本：{v}");
                Out($"安装器：{ForgeInstaller.InstallerUrl(mcVersion, v)}");
                return 0;
            }
            catch (Exception ex)
            {
                Out("查询失败：" + ex.Message);
                return 1;
            }
        }

        private static int InstallForge(string mcVersion, string loaderVersion)
        {
            if (string.IsNullOrEmpty(mcVersion))
            {
                Out("用法：install-forge <原版版本> [加载器版本]");
                return 2;
            }

            var task = new InstallTask
            {
                Kind = InstallKind.Forge,
                BaseVersion = mcVersion,
                LoaderVersion = loaderVersion,
                VersionId = string.IsNullOrEmpty(loaderVersion)
                    ? "forge-" + mcVersion
                    : ForgeInstaller.BuildVersionId(mcVersion, loaderVersion)
            };

            // Forge 安装器只在结束时输出，中间没有百分比，所以按阶段原文输出 + 每 15 秒报一次时长
            var lastStage = "";
            var lastBeat = -1;
            task.Updated += t =>
            {
                var beat = (int)(DateTime.Now.Second / 15);
                if (t.Stage != lastStage || beat != lastBeat || t.Completed || t.Failed)
                {
                    lastStage = t.Stage;
                    lastBeat = beat;
                    Out($"{t.Stage} {t.SizeText}".TrimEnd());
                }
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string installedId = null;
            try
            {
                installedId = ForgeInstaller.InstallAsync(Config.Current.GameDir, mcVersion, loaderVersion,
                    task, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Out("安装失败：" + ex.Message);
                return 1;
            }

            Out($"Forge 安装完成：{installedId}（耗时 {sw.Elapsed.TotalSeconds:0.0} 秒）");
            Out($"游戏目录：{VersionEnv.Resolve(Config.Current.GameDir, installedId)}");
            return 0;
        }

        private static int Verify(string versionId)        {
            if (string.IsNullOrEmpty(versionId)) { Out("请指定版本 ID"); return 2; }

            var gameDir = Config.Current.GameDir;
            var resolved = VersionManager.Resolve(gameDir, versionId);
            var libsDir = Paths.LibrariesDir(gameDir);
            var missing = new List<string>();

            foreach (var lib in resolved.Libraries)
            {
                var arch = JavaFinder.ArchOfCurrentProcess;
                if (!Rules.Allowed(J.Arr(lib, "rules"), new Dictionary<string, bool>(), arch)) continue;

                var artifact = J.Obj(J.Obj(lib, "downloads"), "artifact");
                var rel = J.Str(artifact, "path") ?? LaunchBuilder.MavenPath(J.Str(lib, "name"));
                if (string.IsNullOrEmpty(rel)) continue;

                var full = Path.Combine(libsDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) missing.Add("lib  " + rel);
            }

            if (string.IsNullOrEmpty(resolved.JarPath) || !File.Exists(resolved.JarPath))
                missing.Add("jar  客户端主文件");

            var indexFile = Path.Combine(Paths.AssetsDir(gameDir), "indexes", resolved.EffectiveAssetIndex + ".json");
            if (!File.Exists(indexFile)) missing.Add("index 资源索引 " + resolved.EffectiveAssetIndex);
            else
            {
                try
                {
                    var idx = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(indexFile));
                    var objects = J.Obj(idx, "objects");
                    var objectsDir = Path.Combine(Paths.AssetsDir(gameDir), "objects");
                    var missingAssets = 0;
                    if (objects != null)
                        foreach (var kv in objects)
                        {
                            var hash = J.Str(kv.Value, "hash");
                            if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;
                            if (!File.Exists(Path.Combine(objectsDir, hash.Substring(0, 2), hash))) missingAssets++;
                        }
                    if (missingAssets > 0) missing.Add($"asset 缺失 {missingAssets} 个资源对象");
                }
                catch (Exception ex) { missing.Add("index 解析失败 " + ex.Message); }
            }

            Out($"版本 {versionId}：库 {resolved.Libraries.Count} 个，继承链 {string.Join(" -> ", resolved.Chain)}");
            Out($"Java 要求：{JavaFinder.RequiredMajor(versionId, resolved.JavaMajor)}");

            if (missing.Count == 0) { Out("校验通过，依赖齐全 ✓"); return 0; }

            Out($"缺失 {missing.Count} 项：");
            foreach (var m in missing.Take(20)) Out("  " + m);
            if (missing.Count > 20) Out($"  …… 另有 {missing.Count - 20} 项");
            return 1;
        }

        private static int Plan(string versionId)
        {
            if (string.IsNullOrEmpty(versionId)) { Out("请指定版本 ID"); return 2; }

            var root = Config.Current.GameDir;
            var resolved = VersionManager.Resolve(root, versionId);
            var gameDir = VersionEnv.Prepare(root, versionId);
            var required = JavaFinder.RequiredMajor(versionId, resolved.JavaMajor);
            var java = !string.IsNullOrEmpty(Config.Current.JavaPath) && File.Exists(Config.Current.JavaPath)
                ? new JavaInfo { Path = Config.Current.JavaPath, Major = required }
                : JavaFinder.FindBest(required);
            if (java == null)
            {
                Out($"没有可用的 Java {required}（可执行 java-ensure {required} 自动安装）");
                return 1;
            }

            var session = Msa.EnsureFreshAsync().GetAwaiter().GetResult();
            if (session != null) Out($"账号：{session.Name}（正版）");
            var options = new LaunchOptions
            {
                GameDir = gameDir,
                RootGameDir = root,
                VersionName = versionId,
                PlayerName = session?.Name ?? Config.Current.PlayerName,
                Uuid = session?.Uuid,
                AccessToken = session?.AccessToken,
                JavaPath = java.Path,
                MemoryMb = Config.Current.MemoryMb,
                MinMemoryMb = Config.Current.MinMemoryMb,
                CustomResolution = Config.Current.CustomResolution,
                Width = Config.Current.WindowWidth,
                Height = Config.Current.WindowHeight
            };

            var plan = LaunchBuilder.Build(resolved, options, java.Path);
            Out($"Java：{java.Path}（Java {java.Major}）");
            Out($"游戏目录：{gameDir}（存档隔离：强制开启）");
            Out($"存档目录：{VersionEnv.SavesDir(root, versionId)}");
            Out($"mainClass：{resolved.MainClass}");
            Out($"参数数量：{plan.Args.Count}，解压原生库 {plan.ExtractedNatives} 个");
            Out("---- 命令行 ----");
            Out(plan.ToCommandLine());
            return 0;
        }

        private static int Launch(string versionId)
        {
            if (string.IsNullOrEmpty(versionId)) { Out("请指定版本 ID"); return 2; }

            var root = Config.Current.GameDir;
            var resolved = VersionManager.Resolve(root, versionId);
            var gameDir = VersionEnv.Prepare(root, versionId);
            var required = JavaFinder.RequiredMajor(versionId, resolved.JavaMajor);
            var java = JavaFinder.FindBest(required);
            if (java == null)
            {
                Out($"没有可用的 Java {required}，开始自动安装……");
                try
                {
                    var progress = new Progress<JavaInstallProgress>(p => Out("  " + p.Text));
                    java = JavaProvisioner.EnsureAsync(required, progress).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Out("Java 自动安装失败：" + ex.Message);
                    return 1;
                }
            }

            var session = Msa.EnsureFreshAsync().GetAwaiter().GetResult();
            if (session != null) Out($"账号：{session.Name}（正版）");
            var options = new LaunchOptions
            {
                GameDir = gameDir,
                RootGameDir = root,
                VersionName = versionId,
                PlayerName = session?.Name ?? Config.Current.PlayerName,
                Uuid = session?.Uuid,
                AccessToken = session?.AccessToken,
                JavaPath = java.Path,
                MemoryMb = Config.Current.MemoryMb,
                MinMemoryMb = Config.Current.MinMemoryMb
            };

            var plan = LaunchBuilder.Build(resolved, options, java.Path);
            Out("游戏目录：" + gameDir);
            Out("命令行：" + plan.ToCommandLine());

            var proc = new GameProcess();
            proc.Output += line => Out("  | " + line);
            proc.Start(plan);

            Out("游戏已启动，等待进程退出……");
            var exitCode = -1;
            var done = new ManualResetEventSlim(false);
            proc.Exited += code => { exitCode = code; done.Set(); };
            done.Wait(TimeSpan.FromMinutes(5));
            Out("退出码：" + exitCode);
            return exitCode;
        }

        private static void Out(string line)
        {
            lock (WriteGate)
            {
                try { _out?.WriteLine(line); } catch { }
                try { File.AppendAllText(_outFile, line + Environment.NewLine); } catch { }
            }
        }

        /// <summary>接管标准输出：统一用 UTF-8，避免控制台代码页（GBK）导致中文乱码。</summary>
        private static void AttachToConsole()
        {
            try
            {
                if (Console.IsOutputRedirected)
                {
                    // 管道 / 文件重定向：直接写 UTF-8 原始字节
                    _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
                    {
                        AutoFlush = true
                    };
                    return;
                }

                if (!AttachConsole(-1)) return; // 没有父控制台（双击启动）就只写文件

                try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

                var handle = GetStdHandle(-11);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;

                var stream = new FileStream(new SafeFileHandle(handle, false), FileAccess.Write);
                _out = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch { /* 忽略，仍会写入日志文件 */ }
        }
    }
}
