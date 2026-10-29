using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModJava;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModDownload
{
    /// <summary>整合包格式。</summary>
    public enum PackFormat
    {
        Unknown,
        /// <summary>Modrinth（.mrpack）：含 <c>modrinth.index.json</c>。</summary>
        Modrinth,
        /// <summary>CurseForge：含 <c>manifest.json</c>（manifestType = minecraftModpack）。</summary>
        CurseForge,
        /// <summary>MultiMC / Prism 实例包：含 <c>mmc-pack.json</c>。</summary>
        MultiMc,
        /// <summary>没有清单文件的普通压缩包：只有 mods/ config/ 之类。</summary>
        PlainZip
    }

    /// <summary>整合包里的一个文件。</summary>
    public sealed class PackFile
    {
        /// <summary>相对实例目录的路径，如 <c>mods/xxx.jar</c>。</summary>
        public string Path { get; set; }
        public string Url { get; set; }
        public string Sha1 { get; set; }
        public long Size { get; set; }

        /// <summary>CurseForge 专用：用它去查文件名。</summary>
        public int ModId { get; set; }
        public int FileId { get; set; }

        /// <summary>是否可选（CurseForge 的 required=false）。</summary>
        public bool Optional { get; set; }
    }

    /// <summary>整合包解析结果。</summary>
    public sealed class PackPlan
    {
        public PackFormat Format { get; set; } = PackFormat.Unknown;
        public string Name { get; set; } = "";
        public string PackVersion { get; set; } = "";
        public string McVersion { get; set; } = "";

        /// <summary><c>fabric</c> / <c>forge</c> / 空（原版）。</summary>
        public string LoaderKind { get; set; } = "";
        public string LoaderVersion { get; set; } = "";

        /// <summary>zip 内 overrides 的目录前缀（如 <c>overrides</c>）；空表示没有。</summary>
        public string OverridesPrefix { get; set; } = "";

        /// <summary>zip 内"实例内容"的目录前缀（MultiMC/PlainZip 用）；空表示根目录。</summary>
        public string ContentPrefix { get; set; } = "";

        public List<PackFile> Files { get; } = new List<PackFile>();
        public List<string> Warnings { get; } = new List<string>();

        public string FormatLabel
        {
            get
            {
                switch (Format)
                {
                    case PackFormat.Modrinth: return "Modrinth（.mrpack）";
                    case PackFormat.CurseForge: return "CurseForge";
                    case PackFormat.MultiMc: return "MultiMC / Prism 实例";
                    case PackFormat.PlainZip: return "普通压缩包";
                    default: return "未知";
                }
            }
        }

        /// <summary>建议的版本 ID（文件系统安全）。</summary>
        public string SuggestVersionId()
        {
            var raw = string.IsNullOrWhiteSpace(PackVersion) ? Name : Name + " " + PackVersion;
            return SanitizeId(raw);
        }

        public static string SanitizeId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "modpack";
            var s = Regex.Replace(raw.Trim(), @"[^\w.\-+]+", "-").Trim('-', '.', ' ');
            s = Regex.Replace(s, "-{2,}", "-");
            if (s.Length > 60) s = s.Substring(0, 60).TrimEnd('-', '.');
            return string.IsNullOrWhiteSpace(s) ? "modpack" : s;
        }
    }

    /// <summary>
    /// 整合包导入。
    ///
    /// 支持的四种来源：
    /// <list type="bullet">
    /// <item>Modrinth <c>.mrpack</c>：有完整下载清单，最可靠</item>
    /// <item>CurseForge 整合包 zip：清单只给 projectID/fileID，需要反查文件名（见下方说明）</item>
    /// <item>MultiMC / Prism 实例 zip：模组已打包在 zip 里，不需要下载</item>
    /// <item>普通 zip：只有 mods/ config/，作为覆盖层贴到指定原版版本上</item>
    /// </list>
    ///
    /// 产出的版本目录形如 <c>versions\&lt;包名-版本&gt;\</c>，其 JSON 只写 <c>inheritsFrom</c> 指向
    /// 加载器版本，继承链合并与启动逻辑直接复用，无需另建一套。
    /// </summary>
    public static class ModpackInstaller
    {
        private const string CfWidgetBase = "https://api.cfwidget.com/";
        private const string CfCdnBase = "https://mediafilez.forgecdn.net/files/";

        // ---------------- 解析 ----------------

        /// <summary>只读 zip 目录判断格式并解析清单，不下载任何东西。</summary>
        public static PackPlan Inspect(string zipPath)
        {
            if (!File.Exists(zipPath))
                throw new FileNotFoundException("找不到整合包文件：" + zipPath, zipPath);

            using var zip = ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();

            PackPlan plan;

            var modrinth = names.FirstOrDefault(n => n.EndsWith("modrinth.index.json", StringComparison.OrdinalIgnoreCase));
            if (modrinth != null)
                plan = ReadModrinth(zip, modrinth);
            else
            {
                var cf = names.FirstOrDefault(n => n.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));
                if (cf != null && LooksLikeCurseForge(zip, cf))
                    plan = ReadCurseForge(zip, cf);
                else
                {
                    var mmc = names.FirstOrDefault(n => n.EndsWith("mmc-pack.json", StringComparison.OrdinalIgnoreCase));
                    plan = mmc != null ? ReadMultiMc(zip, mmc, names) : ReadPlainZip(names);
                }
            }

            if (string.IsNullOrWhiteSpace(plan.McVersion))
                throw new InvalidDataException(
                    $"没能从整合包里读出 Minecraft 版本（识别为 {plan.FormatLabel}）。" +
                    "如果是普通压缩包，请指定一个原版版本作为基础。");

            // 覆盖目录：Modrinth 固定 overrides；CurseForge 由 manifest 的 overrides 字段指定
            if (string.IsNullOrEmpty(plan.OverridesPrefix))
            {
                var ov = names.FirstOrDefault(n => n.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase));
                if (ov != null) plan.OverridesPrefix = "overrides";
            }

            return plan;
        }

        private static bool LooksLikeCurseForge(ZipArchive zip, string entryName)
        {
            try
            {
                var node = ParseJson(ReadText(zip, entryName));
                return node is JsonObject o &&
                       (o.ContainsKey("minecraft") || o.ContainsKey("manifestType"));
            }
            catch
            {
                return false;
            }
        }

        private static PackPlan ReadModrinth(ZipArchive zip, string entryName)
        {
            var root = ParseJson(ReadText(zip, entryName)) as JsonObject
                       ?? throw new InvalidDataException("modrinth.index.json 不是合法 JSON 对象");

            var plan = new PackPlan
            {
                Format = PackFormat.Modrinth,
                Name = J.Str(root, "name") ?? "Modrinth 整合包",
                PackVersion = J.Str(root, "versionId") ?? ""
            };

            var deps = J.Obj(root, "dependencies");
            plan.McVersion = J.Str(deps, "minecraft") ?? "";

            if (!string.IsNullOrEmpty(J.Str(deps, "fabric-loader")))
            {
                plan.LoaderKind = "fabric";
                plan.LoaderVersion = J.Str(deps, "fabric-loader");
            }
            else if (!string.IsNullOrEmpty(J.Str(deps, "forge")))
            {
                plan.LoaderKind = "forge";
                plan.LoaderVersion = J.Str(deps, "forge");
            }
            else if (!string.IsNullOrEmpty(J.Str(deps, "quilt-loader")))
            {
                plan.Warnings.Add("这个整合包用 Quilt 加载器，当前只支持 Fabric / Forge，将按原版安装。");
            }
            else if (!string.IsNullOrEmpty(J.Str(deps, "neoforge")))
            {
                plan.Warnings.Add("这个整合包用 NeoForge 加载器，当前尚未支持，将按原版安装。");
            }

            plan.OverridesPrefix = "overrides";

            foreach (var node in J.Arr(root, "files") ?? new JsonArray())
            {
                if (!(node is JsonObject f)) continue;

                var path = J.Str(f, "path");
                var downloads = J.Arr(f, "downloads");
                var url = downloads != null && downloads.Count > 0 ? downloads[0]?.GetValue<string>() : null;
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(url)) continue;

                // env.client = "unsupported" 的文件不需要装到客户端
                var env = J.Obj(f, "env");
                if (string.Equals(J.Str(env, "client"), "unsupported", StringComparison.OrdinalIgnoreCase))
                    continue;

                plan.Files.Add(new PackFile
                {
                    Path = path.Replace('\\', '/'),
                    Url = url,
                    Sha1 = J.Str(J.Obj(f, "hashes"), "sha1"),
                    Size = J.Long(f, "fileSize")
                });
            }

            return plan;
        }

        private static PackPlan ReadCurseForge(ZipArchive zip, string entryName)
        {
            var root = ParseJson(ReadText(zip, entryName)) as JsonObject
                       ?? throw new InvalidDataException("manifest.json 不是合法 JSON 对象");

            var mc = J.Obj(root, "minecraft");
            var plan = new PackPlan
            {
                Format = PackFormat.CurseForge,
                Name = J.Str(root, "name") ?? "CurseForge 整合包",
                PackVersion = J.Str(root, "version") ?? "",
                McVersion = J.Str(mc, "version") ?? ""
            };

            // modLoaders[].id 形如 "forge-47.2.0" / "fabric-0.15.0"
            var loaders = J.Arr(mc, "modLoaders");
            if (loaders != null)
            {
                JsonObject primary = null;
                foreach (var n in loaders)
                {
                    if (n is JsonObject l && J.Bool(l, "primary")) { primary = l; break; }
                }
                primary ??= loaders.FirstOrDefault() as JsonObject;

                var id = J.Str(primary, "id") ?? "";
                if (id.StartsWith("forge-", StringComparison.OrdinalIgnoreCase))
                {
                    plan.LoaderKind = "forge";
                    plan.LoaderVersion = id.Substring("forge-".Length);
                }
                else if (id.StartsWith("fabric-", StringComparison.OrdinalIgnoreCase))
                {
                    plan.LoaderKind = "fabric";
                    plan.LoaderVersion = id.Substring("fabric-".Length);
                }
                else if (id.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
                {
                    plan.Warnings.Add("这个整合包用 NeoForge 加载器，当前尚未支持，将按原版安装。");
                }
                else if (!string.IsNullOrEmpty(id))
                {
                    plan.Warnings.Add($"未识别的加载器「{id}」，将按原版安装。");
                }
            }

            plan.OverridesPrefix = J.Str(root, "overrides") ?? "overrides";

            foreach (var node in J.Arr(root, "files") ?? new JsonArray())
            {
                if (!(node is JsonObject f)) continue;

                var projectId = (int)J.Long(f, "projectID");
                var fileId = (int)J.Long(f, "fileID");
                if (projectId <= 0 || fileId <= 0) continue;

                plan.Files.Add(new PackFile
                {
                    ModId = projectId,
                    FileId = fileId,
                    Optional = !J.Bool(f, "required", true)
                });
            }

            return plan;
        }

        private static PackPlan ReadMultiMc(ZipArchive zip, string entryName, List<string> names)
        {
            var root = ParseJson(ReadText(zip, entryName)) as JsonObject
                       ?? throw new InvalidDataException("mmc-pack.json 不是合法 JSON 对象");

            var prefix = entryName.Substring(0, entryName.Length - "mmc-pack.json".Length);

            var plan = new PackPlan
            {
                Format = PackFormat.MultiMc,
                Name = Path.GetFileNameWithoutExtension(
                    prefix.TrimEnd('/').Split('/').LastOrDefault() ?? "MultiMC 实例"),
                PackVersion = "",
                ContentPrefix = prefix
            };

            foreach (var node in J.Arr(root, "components") ?? new JsonArray())
            {
                if (!(node is JsonObject c)) continue;
                var uid = (J.Str(c, "uid") ?? "").ToLowerInvariant();
                var ver = J.Str(c, "version") ?? "";

                if (uid == "net.minecraft") plan.McVersion = ver;
                else if (uid == "net.fabricmc.fabric-loader")
                {
                    plan.LoaderKind = "fabric";
                    plan.LoaderVersion = ver;
                }
                else if (uid == "net.minecraftforge")
                {
                    plan.LoaderKind = "forge";
                    plan.LoaderVersion = ver;
                }
                else if (uid == "net.neoforged")
                {
                    plan.Warnings.Add("这个实例用 NeoForge，当前尚未支持，将按原版安装。");
                }
            }

            // Prism 的实例内容在 <实例名>/minecraft/ 下；老 MultiMC 直接在实例根目录
            if (names.Any(n => n.StartsWith(prefix + "minecraft/", StringComparison.OrdinalIgnoreCase)))
                plan.ContentPrefix = prefix + "minecraft/";

            return plan;
        }

        private static PackPlan ReadPlainZip(List<string> names)
        {
            // 没有清单：只当作覆盖层，版本由调用方指定
            var hasMods = names.Any(n => n.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
                                         n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
            if (!hasMods)
                throw new InvalidDataException(
                    "这个压缩包里没有 modrinth.index.json / manifest.json / mmc-pack.json，" +
                    "也找不到 mods/*.jar，无法识别为整合包。");

            var plan = new PackPlan
            {
                Format = PackFormat.PlainZip,
                Name = "整合包"
            };
            plan.Warnings.Add("普通压缩包没有版本清单，需要指定一个已安装的原版版本作为基础。");
            return plan;
        }

        // ---------------- 安装 ----------------

        /// <summary>
        /// 导入整合包。返回生成的版本 ID。
        /// <paramref name="fallbackBaseVersion"/> 仅用于普通 zip（无清单时指定基础版本）。
        /// </summary>
        public static async Task<string> InstallAsync(string gameDir, string zipPath, string fallbackBaseVersion,
            InstallTask task, CancellationToken ct)
        {
            var plan = Inspect(zipPath);

            if (plan.Format == PackFormat.PlainZip)
            {
                if (string.IsNullOrWhiteSpace(fallbackBaseVersion))
                    throw new InvalidOperationException(plan.Warnings.FirstOrDefault() ??
                        "普通压缩包需要指定基础版本。");
                plan.McVersion = fallbackBaseVersion;
                plan.Name = Path.GetFileNameWithoutExtension(zipPath);
            }

            foreach (var w in plan.Warnings)
                Log.Warn($"整合包提示：{w}");

            task.SetStage($"识别为 {plan.FormatLabel}（Minecraft {plan.McVersion}）");
            Log.Info($"开始导入整合包：{Path.GetFileName(zipPath)}，格式 {plan.FormatLabel}，" +
                     $"MC {plan.McVersion}，加载器 {plan.LoaderKind} {plan.LoaderVersion}");

            // ---- 1. 原版 ----
            if (!VanillaInstaller.IsInstalled(gameDir, plan.McVersion))
            {
                task.SetStage($"整合包需要原版 {plan.McVersion}，先安装原版");
                await VanillaInstaller.InstallAsync(gameDir, plan.McVersion, task, ct).ConfigureAwait(false);
            }

            // ---- 2. 加载器 ----
            var baseVersionId = plan.McVersion;
            if (plan.LoaderKind == "fabric" && !string.IsNullOrEmpty(plan.LoaderVersion))
            {
                task.SetStage($"安装 Fabric {plan.LoaderVersion}（整合包指定）");
                baseVersionId = await FabricInstaller.InstallAsync(gameDir, plan.McVersion, plan.LoaderVersion,
                    task, ct).ConfigureAwait(false);
            }
            else if (plan.LoaderKind == "forge" && !string.IsNullOrEmpty(plan.LoaderVersion))
            {
                task.SetStage($"安装 Forge {plan.LoaderVersion}（整合包指定）");
                baseVersionId = await ForgeInstaller.InstallAsync(gameDir, plan.McVersion, plan.LoaderVersion,
                    task, ct).ConfigureAwait(false);
            }

            // ---- 3. 建版本目录 ----
            var packId = MakeUniqueId(gameDir, plan.SuggestVersionId());
            var versionDir = Path.Combine(Paths.VersionsDir(gameDir), packId);
            Directory.CreateDirectory(versionDir);

            var json = new JsonObject
            {
                ["id"] = packId,
                ["inheritsFrom"] = baseVersionId,
                ["type"] = "release",
                ["libraries"] = new JsonArray(),

                // 自定义标记：版本列表据此显示「整合包」，并把原始名称/版本留在 JSON 里备查
                ["slopexPack"] = new JsonObject
                {
                    ["name"] = plan.Name,
                    ["version"] = plan.PackVersion,
                    ["format"] = plan.Format.ToString(),
                    ["source"] = Path.GetFileName(zipPath)
                }
            };

            var jsonPath = Path.Combine(versionDir, packId + ".json");
            File.WriteAllText(jsonPath, json.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
            VersionEnv.Ensure(versionDir);

            Log.Info($"整合包版本定义：{jsonPath}（inheritsFrom {baseVersionId}）");

            // ---- 4. 文件清单 ----
            if (plan.Format == PackFormat.CurseForge)
            {
                task.SetStage($"解析 CurseForge 文件清单（{plan.Files.Count} 个）");
                await ResolveCurseForgeAsync(plan, task, ct).ConfigureAwait(false);
            }

            // ---- 5. 下载 ----
            var downloadable = plan.Files.Where(f => !string.IsNullOrEmpty(f.Url)).ToList();
            if (downloadable.Count > 0)
            {
                var queue = new DownloadQueue();
                var downloading = 1;   // 提取阶段要停掉进度回调，否则会显示上一个队列的字节数

                foreach (var f in downloadable)
                {
                    var rel = f.Path.Replace('/', Path.DirectorySeparatorChar);
                    queue.Enqueue(new DownloadJob
                    {
                        Name = f.Path,
                        Url = f.Url,
                        Path = Path.Combine(versionDir, rel),
                        Size = f.Size,
                        Sha1 = f.Sha1,
                        // 整合包动辄几十个文件、8 并发还走代理，按 100 KB/s 估的超时会连续误杀；
                        // 这里给"体积 × 3 倍宽裕 + 3 分钟下限"，上限 10 分钟
                        TimeoutMs = f.Size > 0
                            ? (int)Math.Min(600000, Math.Max(180000, f.Size / 1024 / 30 * 1000))
                            : 300000
                    });
                }

                var total = queue.TotalBytes;
                task.SetStage($"下载整合包文件（{queue.Jobs.Count} 个）");
                queue.Changed += () =>
                {
                    if (System.Threading.Volatile.Read(ref downloading) == 0) return;
                    task.SetProgress(queue.DoneBytes, Math.Max(total, queue.DoneBytes), queue.Speed);
                };

                var failed = await queue.RunAsync(8, ct).ConfigureAwait(false);

                // 失败重试一轮：低并发、只跑失败的那些
                if (failed > 0)
                {
                    var retry = queue.Jobs.Where(j => j.State == JobState.Failed).ToList();
                    Log.Warn($"整合包有 {failed} 个文件首次下载失败，低并发重试 {retry.Count} 个");

                    foreach (var j in retry)
                    {
                        j.State = JobState.Pending;
                        j.Error = null;
                        j.Downloaded = 0;
                    }

                    task.SetStage($"重试 {retry.Count} 个失败文件");
                    failed = await queue.RunAsync(2, ct).ConfigureAwait(false);
                }

                System.Threading.Volatile.Write(ref downloading, 0);
                task.SetProgress(1, 1, 0);

                if (failed > 0)
                {
                    var names = queue.Jobs.Where(j => j.State == JobState.Failed)
                        .Select(j => Path.GetFileName(j.Name)).Take(6).ToList();
                    var bad = queue.Jobs.FirstOrDefault(j => j.State == JobState.Failed);

                    throw new IOException(
                        $"整合包有 {failed} 个文件下载失败：{string.Join("、", names)}" +
                        (failed > names.Count ? " 等" : "") +
                        (bad != null ? $"\n最后一个错误：{bad.Error}" : "") +
                        "\n\n版本目录已生成但缺文件，启动会报「Incompatible mods found」。可重新导入，或手动补下这些文件。");
                }
            }

            // ---- 6. 解压 overrides / 复制实例内容 ----
            task.SetStage("解压配置与模组覆盖");
            var extracted = ExtractContent(zipPath, versionDir, plan);
            Log.Info($"整合包已解压 {extracted} 个文件到版本目录");

            // ---- 7. 收尾 ----
            task.SetProgress(1, 1, 0);
            task.SetStage("安装完成");

            var mods = Directory.Exists(Path.Combine(versionDir, "mods"))
                ? Directory.GetFiles(Path.Combine(versionDir, "mods"), "*.jar").Length
                : 0;

            Log.Info($"整合包导入完成：{packId}，mods {mods} 个，" +
                     $"下载 {downloadable.Count} 个文件，解压 {extracted} 个，警告 {plan.Warnings.Count} 条");
            return packId;
        }

        private static string MakeUniqueId(string gameDir, string id)
        {
            var candidate = id;
            var n = 2;
            while (Directory.Exists(Path.Combine(Paths.VersionsDir(gameDir), candidate)))
                candidate = id + "-" + n++;
            return candidate;
        }

        /// <summary>把 overrides（或实例内容）解压到版本目录，做 zip-slip 防护。</summary>
        private static int ExtractContent(string zipPath, string versionDir, PackPlan plan)
        {
            var root = Path.GetFullPath(versionDir);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
                root += Path.DirectorySeparatorChar;

            var count = 0;

            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith("/")) continue;

                var name = e.FullName.Replace('\\', '/');
                string rel = null;

                if (!string.IsNullOrEmpty(plan.OverridesPrefix) &&
                    name.StartsWith(plan.OverridesPrefix + "/", StringComparison.OrdinalIgnoreCase))
                {
                    rel = name.Substring(plan.OverridesPrefix.Length + 1);
                }
                else if (plan.Format == PackFormat.MultiMc || plan.Format == PackFormat.PlainZip)
                {
                    // 实例内容：去掉前缀，且跳过清单文件本身
                    var p = name;
                    if (!string.IsNullOrEmpty(plan.ContentPrefix) &&
                        p.StartsWith(plan.ContentPrefix, StringComparison.OrdinalIgnoreCase))
                        p = p.Substring(plan.ContentPrefix.Length);

                    var lower = p.ToLowerInvariant();
                    if (lower.EndsWith("mmc-pack.json") || lower.EndsWith("instance.cfg") ||
                        lower.EndsWith("manifest.json") || lower.EndsWith("modrinth.index.json"))
                        continue;

                    rel = p;
                }

                if (string.IsNullOrEmpty(rel)) continue;

                var target = Path.GetFullPath(Path.Combine(versionDir, rel.Replace('/', Path.DirectorySeparatorChar)));

                // zip-slip：目标必须落在版本目录内
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn($"跳过越界路径：{name}");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? versionDir);
                e.ExtractToFile(target, overwrite: true);
                count++;
            }

            return count;
        }

        /// <summary>
        /// CurseForge 的清单只给 projectID/fileID，没有下载地址和文件名。
        ///
        /// 这里用 cfwidget 的公开镜像反查文件名，再按 CurseForge CDN 的固定规则拼直链：
        /// <c>mediafilez.forgecdn.net/files/{id/1000}/{id%1000}/{filename}</c>。
        /// 代价是每个模组要拉一次它自己的文件列表（大模组可达数 MB），因此结果按 30 天缓存到
        /// <c>SlopeXData\cache\cfwidget</c>，同一个包第二次导入就很快。
        /// </summary>
        private static async Task ResolveCurseForgeAsync(PackPlan plan, InstallTask task, CancellationToken ct)
        {
            var cacheDir = Path.Combine(Paths.CacheDir, "cfwidget");
            Directory.CreateDirectory(cacheDir);

            var pending = plan.Files.Where(f => string.IsNullOrEmpty(f.Url)).ToList();
            var done = 0;
            var gate = new SemaphoreSlim(6);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var tasks = pending.Select(async f =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var cacheFile = Path.Combine(cacheDir, f.ModId + ".json");

                    string fileName = null;
                    if (File.Exists(cacheFile) &&
                        DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < TimeSpan.FromDays(30))
                    {
                        fileName = FindFileName(File.ReadAllText(cacheFile), f.FileId);
                    }

                    if (fileName == null)
                    {
                        var text = await Net.GetStringRawAsync(CfWidgetBase + f.ModId, ct).ConfigureAwait(false);
                        File.WriteAllText(cacheFile, text);
                        fileName = FindFileName(text, f.FileId);
                    }

                    if (string.IsNullOrEmpty(fileName))
                    {
                        plan.Warnings.Add($"CurseForge 文件 {f.ModId}/{f.FileId} 查不到文件名，已跳过");
                        Log.Warn($"CurseForge 文件未找到：mod {f.ModId} file {f.FileId}");
                        return;
                    }

                    f.Path = "mods/" + fileName;
                    f.Size = 0;
                    f.Url = CfCdnBase + (f.FileId / 1000) + "/" + (f.FileId % 1000) + "/" +
                            Uri.EscapeDataString(fileName);
                }
                catch (Exception ex)
                {
                    plan.Warnings.Add($"CurseForge 模组 {f.ModId} 解析失败：{ex.Message}");
                    Log.Warn($"CurseForge 模组 {f.ModId} 解析失败：{ex.Message}");
                }
                finally
                {
                    gate.Release();
                    var n = Interlocked.Increment(ref done);
                    if (n % 5 == 0 || n == pending.Count)
                        task.SetStage($"解析 CurseForge 文件清单（{n}/{pending.Count}）");
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            gate.Dispose();

            Log.Info($"CurseForge 清单解析完成：{pending.Count} 个，耗时 {sw.Elapsed.TotalSeconds:0.0} 秒，" +
                     $"可下载 {plan.Files.Count(f => !string.IsNullOrEmpty(f.Url))} 个");
        }

        /// <summary>从 cfwidget 的响应里按 fileId 找文件名。</summary>
        private static string FindFileName(string json, int fileId)
        {
            try
            {
                var node = ParseJson(json) as JsonObject;
                var files = J.Arr(node, "files");
                if (files == null) return null;

                foreach (var n in files)
                {
                    if (!(n is JsonObject o)) continue;
                    if (J.Long(o, "id") == fileId) return J.Str(o, "name");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("解析 cfwidget 响应失败：" + ex.Message);
            }

            return null;
        }

        // ---------------- 小工具 ----------------

        private static string ReadText(ZipArchive zip, string entryName)
        {
            var entry = zip.GetEntry(entryName)
                        ?? throw new InvalidDataException("压缩包里找不到 " + entryName);

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static JsonNode ParseJson(string text) => JsonNode.Parse(text);
    }
}
