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
                    Out("  repair <版本>            补齐缺失的库 / 客户端 jar / 资源（不改写版本 JSON）");
                    Out("  diagnose <版本> [日志文本]  对最近一次启动失败做诊断（排障用）");
                    Out("  music-test               播放当前主题背景音乐 3 秒并打印进度（排障用）");
                    Out("  music-sim                模拟「启动游戏 / 退出游戏」的音乐挂起与恢复（排障用）");
                    Out("  fix-libs <版本>          补齐缺失的依赖库（走镜像）");
                    Out("  msa-explain <状态码> [原文]  预览登录错误会被翻译成什么（排障用）");
                    Out("  mods-list <版本>         列出该版本 mods 目录的模组与元数据");
                    Out("  mods-deps <版本> <项目>  打印该模组的必需依赖解析结果，标明哪些锁定了版本（排障用）");
                    Out("  mods-scan <版本|目录> [--check]   扫描 jar 元数据、查重复装；--check 顺带校验依赖是否齐全（排障用）");
                    Out("  selftest [--mc 版本] [--loader 加载器] [-v]   用固定样本自检中文搜索链路（需联网，失败返回 1）");
                    Out("  mods-search <版本> <关键词> [all|modrinth|curseforge]   搜索模组（英文按来源搜，中文走 MC百科）");
                    Out("  legal [brief]            打印内置法律声明（brief = 首次启动的简版）");
                    Out("  packs-search <关键词> [shader|resourcepack]   搜光影 / 材质包（不做版本过滤）");
                    Out("  packs-versions <项目ID>  列出该资源包的全部版本");
                    Out("  packs-install <版本> <shader|resourcepack> <项目ID> [版本号]   下载到该版本目录");                    Out("  mods-install <版本> <关键词|项目ID>   搜索并安装首个结果（含必需依赖）");
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

                case "mods-search":
                    return ModsSearch(args.FirstOrDefault(), args.Skip(1).FirstOrDefault(),
                        args.Skip(2).FirstOrDefault());

                case "mods-install":
                    return ModsInstall(args.FirstOrDefault(), args.Skip(1).FirstOrDefault(),
                        args.Skip(2).FirstOrDefault());

                case "mods-deps":
                    return ModsDeps(args.FirstOrDefault(), args.Skip(1).FirstOrDefault());

                case "mods-scan":
                    return ModsScan(args.FirstOrDefault(), args.Skip(1).FirstOrDefault());

                case "mods-list":
                    return ModsList(args.FirstOrDefault());

                case "selftest":
                    return SelfTest.Run(args);

                case "legal":
                    return LegalDump(args.FirstOrDefault());

                case "packs-search":
                    return PacksSearch(args);

                case "packs-versions":
                    return PacksVersions(args.FirstOrDefault());

                case "packs-install":
                    return PacksInstall(args);

                case "msa-explain":
                    return MsaExplain(args.FirstOrDefault(), string.Join(" ", args.Skip(1)));

                case "music-test":
                    return MusicTest();

                case "music-sim":
                    return MusicSim();

                case "diagnose":
                    return Diagnose(args.FirstOrDefault(), string.Join(" ", args.Skip(1)));

                case "repair":
                    return RepairVersion(args.FirstOrDefault());

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
            // 顺带自愈：旧版本存下的 UUID 是截断的，这里会补全并写回
            var session = Msa.EnsureFreshAsync().GetAwaiter().GetResult() ?? Msa.Current;
            if (session == null)
            {
                Out("当前为离线登录：" + Config.Current.PlayerName);
                Out("（可用 --cli login 登录微软账号）");
                return 0;
            }

            var when = session.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            Out($"正版账号：{session.Name}");
            Out($"  UUID：{session.Uuid}");
            Out(session.Expired
                ? $"  令牌：已过期（{when} 到期），启动游戏时自动续期"
                : $"  令牌：有效（{when} 到期，自动续期，无需重新登录）");
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

        /// <summary>排障用：直接看某个 HTTP 状态会被翻译成什么提示，不必真的登录一次。</summary>
        /// <summary>
        /// 老命令里可以带 cf 指定 CurseForge。该来源已移除，
        /// 但直接忽略第三个参数会让人以为"选了 CurseForge 却出来 Modrinth"，所以明说一句。
        /// </summary>
        private static int ModsSearch(string versionId, string query, string source)
        {
            if (string.IsNullOrEmpty(versionId) || string.IsNullOrEmpty(query))
            {
                Out("用法：mods-search <版本> <关键词> [all|modrinth|curseforge]");
                return 2;
            }

            var (mc, loader) = ModMods.ModInstaller.ResolveTarget(Config.Current.GameDir, versionId);
            Out($"目标：{versionId} → MC {mc}，加载器 {(string.IsNullOrEmpty(loader) ? "原版" : loader)}");

            var result = Search(query, mc, loader, ParseFilter(source));
            Out(result.StatusText(mc, loader));
            if (result.Hits.Count == 0) return 0;

            foreach (var h in result.Hits)
                Out($"[{h.Source}] {h.Title}  ({h.DownloadsText})  id={h.ProjectId}" +
                    (h.HasMcMod ? $"  百科 {h.McModUrl}" : ""));

            return 0;
        }

        private static int ModsInstall(string versionId, string query, string source)
        {
            if (string.IsNullOrEmpty(versionId) || string.IsNullOrEmpty(query))
            {
                Out("用法：mods-install <版本> <关键词|项目ID> [all|modrinth|curseforge]");
                return 2;
            }

            var (mc, loader) = ModMods.ModInstaller.ResolveTarget(Config.Current.GameDir, versionId);
            Out($"目标：{versionId} → MC {mc}，加载器 {(string.IsNullOrEmpty(loader) ? "原版" : loader)}");

            var hits = Search(query, mc, loader, ParseFilter(source)).Hits;
            if (hits.Count == 0) { Out("（无结果）"); return 1; }

            var hit = hits[0];
            Out($"选中：{hit.Title}（{hit.Source} {hit.ProjectId}）");

            try
            {
                var result = ModMods.ModInstaller.InstallAsync(Config.Current.GameDir, versionId, mc, loader,
                    hit, CancellationToken.None, msg => Out("  " + msg)).GetAwaiter().GetResult();

                Out("结果：" + result.Summary());
                foreach (var n in result.Installed) Out("  + " + n);
                foreach (var n in result.Skipped) Out("  = 已存在 " + n);
                return result.Installed.Count > 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Out("安装失败：" + ex.Message);
                return 1;
            }
        }

        private static ModMods.ModSearchResult Search(string query, string mc, string loader,
            ModMods.ModSourceFilter filter)
            => ModMods.ModSearch.SearchAsync(query, mc, loader, filter).GetAwaiter().GetResult();

        /// <summary>
        /// 打印某个模组的必需依赖解析结果（排障用）。
        /// 声明了 <c>version_id</c> 的依赖标「锁定」，并显示最终解析到的文件——
        /// 用来确认「依赖按作者声明的版本落盘，而不是装最新版」这条逻辑有没有生效。
        /// </summary>
        private static int ModsDeps(string versionId, string projectId)
        {
            if (string.IsNullOrEmpty(versionId) || string.IsNullOrEmpty(projectId))
            {
                Out("用法：mods-deps <版本> <项目ID|slug>");
                return 2;
            }

            var (mc, loader) = ModMods.ModInstaller.ResolveTarget(Config.Current.GameDir, versionId);
            Out($"目标：{versionId} → MC {mc}，加载器 {(string.IsNullOrEmpty(loader) ? "原版" : loader)}");

            try
            {
                var files = ModMods.ModrinthSource
                    .ListFilesAsync(projectId, mc, loader, CancellationToken.None).GetAwaiter().GetResult();

                if (files.Count == 0)
                {
                    Out("（该项目在当前 MC 版本 / 加载器下没有文件）");
                    return 1;
                }

                var file = files[0];
                Out($"项目文件：{file.VersionLabel}  {file.FileName}");
                Out($"  versionId：{(string.IsNullOrEmpty(file.VersionId) ? "（空）" : file.VersionId)}");

                var deps = ModMods.ModrinthSource
                    .RequiredDepsAsync(file, mc, loader, CancellationToken.None).GetAwaiter().GetResult();

                Out($"必需依赖：{(deps.Count == 0 ? "（无）" : deps.Count + " 个")}");

                foreach (var (pid, verId, name) in deps)
                {
                    var tag = string.IsNullOrEmpty(verId) ? "最新适配版" : "锁定 " + verId;
                    Out($"  {pid}  [{tag}]{(string.IsNullOrEmpty(name) ? "" : "  " + name)}");

                    if (string.IsNullOrEmpty(verId)) continue;

                    var pinned = ModMods.ModrinthSource
                        .FileOfVersionAsync(verId, pid, mc, loader, CancellationToken.None)
                        .GetAwaiter().GetResult();

                    Out(pinned == null
                        ? "      → 取不到，安装时回落到最新适配版"
                        : $"      → {pinned.VersionLabel}  {pinned.FileName}");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Out("解析失败：" + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// 扫描 mods 目录，逐个读 jar 里的模组元数据（排障用）。
        /// 参数可以是版本 ID，也可以直接给一个目录——后者便于对着任意模组包做核对。
        /// 加 <c>--check</c> 时顺带做一次清单校验：缺哪些前置、哪些版本不满足区间。
        /// 有读不出来的 jar、重复 modid 或校验不过时返回 1。
        /// </summary>
        private static int ModsScan(string target, string flag)
        {
            if (string.IsNullOrEmpty(target))
            {
                Out("用法：mods-scan <版本|目录> [--check]");
                return 2;
            }

            var dir = Directory.Exists(target)
                ? Path.GetFullPath(target)
                : ModMods.ModManager.ModsDir(Config.Current.GameDir, target);

            var check = string.Equals(flag, "--check", StringComparison.OrdinalIgnoreCase);

            Out($"目录：{dir}");

            var mods = ModMods.ModJar.ScanDir(dir);
            if (mods.Count == 0)
            {
                Out("（没有 jar）");
                return 0;
            }

            var ok = mods.Where(m => m.Ok).ToList();
            Out($"共 {mods.Count} 个 jar：{ok.Count} 个可读，{mods.Count - ok.Count} 个没读出来");
            Out("");

            foreach (var m in mods)
            {
                if (!m.Ok)
                {
                    Out(m.FileName);
                    Out($"  × {m.Error}");
                    continue;
                }

                Out($"{m.FileName}");
                Out($"  {m.Format}  {m.ModId}  {m.Version}  {m.Name}");

                if (m.Depends.Count > 0)
                {
                    var head = m.Depends.Take(8).Select(d => $"{d.ModId} {d.VersionRange}");
                    Out($"  依赖 {m.Depends.Count}：{string.Join("、", head)}" +
                        (m.Depends.Count > 8 ? " …" : ""));
                }

                if (m.Breaks.Count > 0)
                    Out($"  不兼容：{string.Join("、", m.Breaks.Select(b => b.ModId))}");
            }

            var dup = ok.GroupBy(m => m.ModId, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToList();

            if (dup.Count > 0)
            {
                Out("");
                Out("重复 modid（同一模组装了多份，游戏侧会报 DuplicateModsFound）：");
                foreach (var g in dup)
                    Out($"  {g.Key}：" + string.Join("、", g.Select(x => x.FileName)));
            }

            var problems = check ? PrintHealth(mods) : 0;

            return mods.Count == ok.Count && dup.Count == 0 && problems == 0 ? 0 : 1;
        }

        /// <summary>
        /// 体检（复用 <see cref="ModMods.ModHealth"/>）：缺前置、版本不符、冲突、重复、坏包。
        /// 判断只看 jar 自己声明的元数据，不联网。
        /// </summary>
        private static int PrintHealth(List<ModMods.JarMod> mods)
        {
            var report = ModMods.ModHealth.Check(mods);

            Out("");
            Out($"依赖检查（{report.OkCount} 个已装模组）：");

            if (report.Clean)
            {
                Out("  没有发现问题");
                return 0;
            }

            foreach (var i in report.Issues) Out("  × " + i.Message);
            return report.Issues.Count;
        }

        /// <summary>命令行里的来源写法 → 筛选枚举；认不出来就用「全部」。</summary>
        private static ModMods.ModSourceFilter ParseFilter(string s) => (s ?? "").ToLowerInvariant() switch
        {
            "mr" or "modrinth" => ModMods.ModSourceFilter.Modrinth,
            "cf" or "curseforge" => ModMods.ModSourceFilter.CurseForge,
            _ => ModMods.ModSourceFilter.All
        };

        private static int ModsList(string versionId)
        {
            if (string.IsNullOrEmpty(versionId)) { Out("用法：mods-list <版本>"); return 2; }

            var list = ModMods.ModManager.List(Config.Current.GameDir, versionId);
            if (list.Count == 0) { Out("（无模组）"); return 0; }

            foreach (var m in list)
                Out($"{(m.Enabled ? "[启用]" : "[禁用]")} {m.Title}  {m.Version}  " +
                    $"({m.LoaderHint}  {m.SizeText})  ←  {m.FileName}");

            Out($"共 {list.Count} 个，启用 {list.Count(m => m.Enabled)} 个");
            return 0;
        }

        // ---------------- 资源包（光影 / 材质）----------------

        /// <summary>搜光影或材质包（不按版本过滤，与界面里同一个入口）。</summary>
        private static int PacksSearch(System.Collections.Generic.List<string> args)
        {
            var query = args.FirstOrDefault();
            if (string.IsNullOrEmpty(query))
            {
                Out("用法：packs-search <关键词> [shader|resourcepack]");
                return 2;
            }

            var type = IsResourcePack(args.Skip(1).FirstOrDefault()) ? "resourcepack" : "shader";

            try
            {
                var hits = ModMods.ModrinthSource.SearchByTypeAsync(query, type, 20, 0).GetAwaiter().GetResult();
                Out($"{type} 搜索「{query}」：{hits.Count} 个结果");

                foreach (var h in hits)
                    Out($"  {h.Title}  [支持 {h.VersionSupportText}]  {h.DownloadsText}  " +
                        $"更新 {(h.Updated == default ? "—" : h.Updated.ToLocalTime().ToString("yyyy-MM-dd"))}  " +
                        $"slug={h.Slug}");

                return 0;
            }
            catch (Exception ex)
            {
                Out("搜索失败：" + ex.Message);
                return 1;
            }
        }

        /// <summary>列出某个资源包的全部版本（验证"不按版本过滤"这件事）。</summary>
        private static int PacksVersions(string projectId)
        {
            if (string.IsNullOrEmpty(projectId))
            {
                Out("用法：packs-versions <项目 ID 或 slug>");
                return 2;
            }

            try
            {
                var versions = ModMods.PackInstaller.ListVersionsAsync(projectId).GetAwaiter().GetResult();
                Out($"{projectId}：{versions.Count} 个版本（新的在前）");

                foreach (var v in versions.Take(30))
                    Out($"  {v.VersionNumber,-18} [{v.McText}] {v.SizeText,9}  {v.PublishedText}  {v.FileName}");

                return 0;
            }
            catch (Exception ex)
            {
                Out("读取版本失败：" + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// 装一个光影 / 材质包到指定版本目录。
        /// 不给版本号时优先挑「声明支持该 MC 版本」的最新一个，没有就退回最新。
        /// </summary>
        private static int PacksInstall(System.Collections.Generic.List<string> args)
        {
            var versionId = args.ElementAtOrDefault(0);
            var projectId = args.ElementAtOrDefault(2);
            var wanted = args.ElementAtOrDefault(3);

            if (string.IsNullOrEmpty(versionId) || string.IsNullOrEmpty(projectId))
            {
                Out("用法：packs-install <版本> <shader|resourcepack> <项目 ID 或 slug> [版本号]");
                return 2;
            }

            var kind = IsResourcePack(args.ElementAtOrDefault(1)) ? ModMods.PackKind.ResourcePack : ModMods.PackKind.Shader;
            var (mc, loader) = ModMods.ModInstaller.ResolveTarget(Config.Current.GameDir, versionId);

            Out($"目标：{versionId} → MC {(string.IsNullOrEmpty(mc) ? "?" : mc)}，加载器 {(string.IsNullOrEmpty(loader) ? "原版" : loader)}");
            Out($"类型：{ModMods.PackInstaller.FolderName(kind)}");

            try
            {
                var versions = ModMods.PackInstaller.ListVersionsAsync(projectId).GetAwaiter().GetResult();

                var pick = (!string.IsNullOrEmpty(wanted)
                        ? versions.FirstOrDefault(v => string.Equals(v.VersionNumber, wanted, StringComparison.OrdinalIgnoreCase))
                        : null)
                    ?? versions.FirstOrDefault(v => v.MatchesMc(mc))
                    ?? versions.FirstOrDefault();

                if (pick == null)
                {
                    Out("这个项目没有可下载的文件。");
                    return 1;
                }

                Out($"选中：{pick.VersionNumber}（{pick.FileName}，{pick.SizeText}）" +
                    (pick.MatchesMc(mc) ? "" : $"  ⚠ 未声明支持 {mc}，仍按用户选择安装"));

                var (path, skipped) = ModMods.PackInstaller.InstallAsync(
                    Config.Current.GameDir, versionId, kind, pick, default, m => Out("  " + m)).GetAwaiter().GetResult();

                Out((skipped ? "已存在，跳过：" : "已下载：") + path);
                Out("目录：" + ModMods.PackInstaller.Dir(Config.Current.GameDir, versionId, kind));
                return 0;
            }
            catch (Exception ex)
            {
                Out("安装失败：" + ex.Message);
                return 1;
            }
        }

        private static bool IsResourcePack(string arg) =>
            !string.IsNullOrEmpty(arg) &&
            (arg.StartsWith("r", StringComparison.OrdinalIgnoreCase) ||
             arg.Equals("pack", StringComparison.OrdinalIgnoreCase) ||
             arg.Equals("resourcepacks", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 排障用：确认内置法律声明真的打进包里、并且能渲染出来。
        ///
        /// 界面上看到的是 FlowDocument，截图容易糊；这里把渲染结果再转回纯文本打印，
        /// 逐行对照就知道有没有丢内容（标题、列表、粗体标记有没有被吃掉）。
        /// 参数写 <c>brief</c> 时看首次启动用的简版。
        /// </summary>
        private static int LegalDump(string which)
        {
            var brief = string.Equals(which, "brief", StringComparison.OrdinalIgnoreCase);
            var source = brief ? ModBase.Legal.BriefMarkdown : ModBase.Legal.Markdown;

            Out($"内置法律声明（{(brief ? "简版" : "完整版")}）：{source.Length} 字符，" +
                $"{source.Replace("\r\n", "\n").Split('\n').Length} 行");
            Out($"配置状态：LegalAccepted={Config.Current.LegalAccepted}，时间={(string.IsNullOrEmpty(Config.Current.LegalAcceptedAt) ? "—" : Config.Current.LegalAcceptedAt)}");

            // FlowDocument 是 DispatcherObject：不仅要在 STA 线程上建，读它的内容
            // （TextRange / 块数量）也必须在同一个线程上，否则跨线程抛 InvalidOperationException。
            // 所以整段工作都塞进同一个 STA 线程里做。
            string text = null;
            var blockCount = 0;
            Exception error = null;

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var doc = brief ? ModBase.Legal.BuildBriefDocument() : ModBase.Legal.BuildDocument();
                    blockCount = doc.Blocks.Count;
                    text = new System.Windows.Documents.TextRange(doc.ContentStart, doc.ContentEnd).Text;
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (error != null)
            {
                Out($"渲染失败：{error.GetType().Name}：{error.Message}");
                return 1;
            }

            Out($"渲染成功：{blockCount} 个块，正文 {text.Length} 字符");
            Out("---- 界面实际显示的纯文本 ----");

            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                Out(line);

            return 0;
        }

        private static int MsaExplain(string statusText, string desc)
        {
            if (!int.TryParse(statusText, out var status))
            {
                Out("用法：msa-explain <状态码> [错误原文]");
                Out("例：msa-explain 403 \"Invalid app registration, see https://aka.ms/AppRegInfo\"");
                return 2;
            }

            Out("—— 用户会看到的提示 ——");
            Out(Modules.ModAccount.Msa.ExplainHttpError(status, desc, null));
            return 0;
        }

        /// <summary>排障用：对某个版本目录的日志（或追加的文本）跑一次诊断。</summary>
        /// <summary>排障用：确认背景音乐真的在走（MediaPlayer 不报错也可能没出声音）。</summary>
        /// <summary>
        /// 排障用：不用真的开游戏，把"启动游戏 → 退出游戏"的音乐挂起 / 恢复走一遍。
        /// 重点是两个边界：用户本来就暂停时不该被恢复；用户手动接管后也不该被恢复。
        /// </summary>
        private static int MusicSim()
        {
            Themes.ThemeService.Apply(Themes.ThemeService.ParseMode(Config.Current.Appearance), force: true);
            ThemeMusic.Initialize();

            if (!ThemeMusic.HasTrack) { Out("当前主题没有可播曲目，跳过"); return 1; }

            var ok = true;
            void Step(string name, bool paused, bool expect)
            {
                var hit = paused == expect;
                ok &= hit;
                Out($"{name}：暂停={paused}（期望 {expect}）{(hit ? "" : "  ← 不符")}");
            }

            ThemeMusic.ApplyForTheme();
            Step("初始", ThemeMusic.IsPaused, false);

            ThemeMusic.PauseForGame();
            Step("游戏启动后", ThemeMusic.IsPaused, true);

            ThemeMusic.ResumeAfterGame();
            Step("游戏退出后", ThemeMusic.IsPaused, false);

            ThemeMusic.Toggle();                 // 用户自己按了暂停
            Step("手动暂停后", ThemeMusic.IsPaused, true);

            ThemeMusic.PauseForGame();
            Step("（暂停状态下）游戏启动后", ThemeMusic.IsPaused, true);

            ThemeMusic.ResumeAfterGame();
            Step("（暂停状态下）游戏退出后", ThemeMusic.IsPaused, true);

            return ok ? 0 : 1;
        }

        private static int MusicTest()
        {
            // CLI 模式没有走 App 的启动流程，主题服务还没应用过配置
            Themes.ThemeService.Apply(Themes.ThemeService.ParseMode(Config.Current.Appearance), force: true);
            ThemeMusic.Initialize();

            Out($"主题：{Themes.ThemeService.CurrentMode}，有曲目：{(ThemeMusic.HasTrack ? "是" : "否")}");
            if (!ThemeMusic.HasTrack) return 1;

            Out($"音量：{ThemeMusic.Volume:P0}");
            System.Threading.Thread.Sleep(3000);

            var playerField = typeof(ThemeMusic).GetField("Player",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var player = playerField?.GetValue(null) as System.Windows.Media.MediaPlayer;

            if (player == null) { Out("读不到 MediaPlayer"); return 1; }

            Out($"播放位置：{player.Position:mm\\:ss}，状态：{player.HasAudio}");
            return player.Position.TotalSeconds > 1 ? 0 : 1;
        }

        private static int Diagnose(string versionId, string extraText)
        {
            if (string.IsNullOrEmpty(versionId))
            {
                Out("用法：diagnose <版本> [日志文本]");
                return 2;
            }

            var versionDir = Path.Combine(Paths.VersionsDir(Config.Current.GameDir), versionId);
            var result = ModLaunch.Diagnosis.Analyze(versionDir, extraText, 1, TimeSpan.FromSeconds(30));

            Out($"命中：{(result.Matched ? "是" : "否")}");
            Out($"[{result.Title}]");
            foreach (var line in result.Detail.Split('\n'))
                Out("  " + line);
            if (!string.IsNullOrEmpty(result.Action)) Out("建议动作：" + result.Action);
            return 0;
        }

        private static int RepairVersion(string versionId)
        {
            if (string.IsNullOrEmpty(versionId)) { Out("用法：repair <版本>"); return 2; }

            try
            {
                var before = ModDownload.VersionRepair.Check(Config.Current.GameDir, versionId);
                Out($"修复前缺失 {before.Count} 项");

                var task = new InstallTask { Kind = InstallKind.Libraries, VersionId = versionId };
                var lastStage = "";
                task.Updated += t =>
                {
                    if (string.IsNullOrEmpty(t.Stage) || t.Stage == lastStage) return;
                    lastStage = t.Stage;
                    Out("  " + t.Stage);
                };

                var n = ModDownload.VersionRepair.RepairAsync(Config.Current.GameDir, versionId, task,
                    CancellationToken.None).GetAwaiter().GetResult();

                var after = ModDownload.VersionRepair.Check(Config.Current.GameDir, versionId);
                Out($"补下 {n} 个文件，修复后仍缺 {after.Count} 项");
                return after.Count == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Out("修复失败：" + ex.Message);
                return 1;
            }
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

        private static int Verify(string versionId)
        {
            if (string.IsNullOrEmpty(versionId)) { Out("请指定版本 ID"); return 2; }

            var gameDir = Config.Current.GameDir;
            var resolved = VersionManager.Resolve(gameDir, versionId);

            var missing = ModDownload.VersionRepair.Check(gameDir, versionId);

            // 资源对象数量单独统计（可能上千个，只报个数）
            var indexFile = Path.Combine(Paths.AssetsDir(gameDir), "indexes",
                resolved.EffectiveAssetIndex + ".json");
            if (File.Exists(indexFile))
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

                    if (missingAssets > 0) missing.Add($"资源对象缺失 {missingAssets} 个");
                }
                catch (Exception ex)
                {
                    missing.Add("资源索引解析失败 " + ex.Message);
                }
            }

            Out($"版本 {versionId}：库 {resolved.Libraries.Count} 个，继承链 {string.Join(" -> ", resolved.Chain)}");
            Out($"Java 要求：{JavaFinder.RequiredMajor(versionId, resolved.JavaMajor)}");

            if (missing.Count == 0) { Out("校验通过，依赖齐全 ✓"); return 0; }

            Out($"缺失 {missing.Count} 项：");
            foreach (var m in missing.Take(20)) Out("  " + m);
            if (missing.Count > 20) Out($"  …… 另有 {missing.Count - 20} 项");
            Out("可用 repair <版本> 自动补齐");
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

        internal static void Out(string line)
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
