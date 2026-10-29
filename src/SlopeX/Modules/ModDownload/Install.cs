using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Modules.ModNet;

namespace SlopeX.Modules.ModDownload
{
    public sealed class VersionSummary : ObservableObject
    {
        private bool _installed;
        private bool _busy;

        public string Id { get; set; }
        public string Type { get; set; } = "release";
        public string Url { get; set; }
        public string ReleaseTime { get; set; } = "";

        public bool Installed
        {
            get => _installed;
            set
            {
                if (!Set(ref _installed, value)) return;
                OnPropertyChanged(nameof(InstallLabel));
                OnPropertyChanged(nameof(CanInstall));
            }
        }

        public bool Busy
        {
            get => _busy;
            set
            {
                if (!Set(ref _busy, value)) return;
                OnPropertyChanged(nameof(InstallLabel));
                OnPropertyChanged(nameof(CanInstall));
            }
        }

        public string TypeLabel => Type switch
        {
            "release" => "正式版",
            "snapshot" => "快照",
            "old_beta" => "远古 Beta",
            "old_alpha" => "远古 Alpha",
            _ => Type
        };

        public string DateLabel
        {
            get
            {
                if (DateTime.TryParse(ReleaseTime, out var d)) return d.ToString("yyyy-MM-dd");
                return "";
            }
        }

        public string InstallLabel => _busy ? "安装中…" : _installed ? "已安装" : "安装";

        // 已安装也允许再点：装 Fabric 加载器、或重新校验补齐原版，都从这个入口进
        public bool CanInstall => !_busy;
    }

    /// <summary>Mojang 版本清单（对应 PCL2 的下载源处理）。</summary>
    public static class Mojang
    {
        public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

        private static List<VersionSummary> _cache;
        private static DateTime _cacheTime = DateTime.MinValue;

        public static async Task<List<VersionSummary>> GetVersionListAsync(bool force = false, CancellationToken ct = default)
        {
            if (!force && _cache != null && (DateTime.Now - _cacheTime).TotalMinutes < 10)
                return _cache;

            var json = await Net.GetJsonAsync(ManifestUrl, ct).ConfigureAwait(false);
            var versions = J.Arr(json, "versions");
            var list = new List<VersionSummary>();

            if (versions != null)
            {
                foreach (var node in versions)
                {
                    var id = J.Str(node, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    list.Add(new VersionSummary
                    {
                        Id = id,
                        Type = J.Str(node, "type") ?? "release",
                        Url = J.Str(node, "url"),
                        ReleaseTime = J.Str(node, "releaseTime") ?? ""
                    });
                }
            }

            _cache = list;
            _cacheTime = DateTime.Now;
            Log.Info($"获取版本清单成功，共 {list.Count} 个版本");
            return list;
        }
    }

    /// <summary>安装任务的种类。</summary>
    public enum InstallKind
    {
        Vanilla,
        Fabric,
        Forge,
        NeoForge,
        Modpack
    }

    /// <summary>安装任务：可观察的进度对象，供任务页展示。</summary>
    public sealed class InstallTask : ObservableObject
    {
        private string _stage = "等待中";
        private double _percent;
        private string _speedText = "";
        private string _sizeText = "";
        private bool _running;
        private bool _completed;
        private bool _failed;
        private string _error;

        /// <summary>目标任务 ID：原版为版本号，Fabric 为 fabric-loader-&lt;loader&gt;-&lt;mc&gt;，Forge 为 &lt;mc&gt;-forge-&lt;loader&gt;。</summary>
        public string VersionId { get; set; }

        public InstallKind Kind { get; set; } = InstallKind.Vanilla;

        /// <summary>加载器所依赖的原版版本。</summary>
        public string BaseVersion { get; set; }

        /// <summary>加载器版本；为空表示"取推荐/最新版"。</summary>
        public string LoaderVersion { get; set; }

        /// <summary>整合包文件路径（Kind = Modpack 时使用）。</summary>
        public string PackPath { get; set; }

        /// <summary>普通压缩包的基础版本（仅 Kind = Modpack 且包内无清单时需要）。</summary>
        public string BaseVersionFallback { get; set; }

        public string Title
        {
            get
            {
                switch (Kind)
                {
                    case InstallKind.Fabric:
                        return "安装 Fabric " +
                               (string.IsNullOrEmpty(LoaderVersion) ? "加载器" : LoaderVersion) +
                               $"（{BaseVersion}）";
                    case InstallKind.Forge:
                        return "安装 Forge " +
                               (string.IsNullOrEmpty(LoaderVersion) ? "加载器" : LoaderVersion) +
                               $"（{BaseVersion}）";
                    case InstallKind.NeoForge:
                        return "安装 NeoForge " +
                               (string.IsNullOrEmpty(LoaderVersion) ? "加载器" : LoaderVersion) +
                               $"（{BaseVersion}）";
                    case InstallKind.Modpack:
                        return "导入整合包 " + (string.IsNullOrEmpty(PackPath)
                            ? VersionId
                            : System.IO.Path.GetFileNameWithoutExtension(PackPath));
                    default:
                        return "安装 " + VersionId;
                }
            }
        }

        public DownloadQueue Engine { get; } = new DownloadQueue();

        public string Stage { get => _stage; private set => Set(ref _stage, value); }
        public double Percent { get => _percent; private set => Set(ref _percent, value); }
        public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
        public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }
        public bool Running { get => _running; private set => Set(ref _running, value); }
        public bool Completed { get => _completed; private set => Set(ref _completed, value); }
        public bool Failed { get => _failed; private set => Set(ref _failed, value); }
        public string Error { get => _error; private set => Set(ref _error, value); }

        public string StateText => Failed ? "失败" : Completed ? "已完成" : Running ? "进行中" : "等待中";

        public CancellationTokenSource Cts { get; private set; }

        public event Action<InstallTask> Updated;

        private void Raise() => Updated?.Invoke(this);

        public async Task RunAsync()
        {
            if (Running) return;
            Cts = new CancellationTokenSource();
            Running = true;
            Failed = false;
            Completed = false;
            Error = null;
            Raise();

            try
            {
                if (Kind == InstallKind.Fabric)
                {
                    VersionId = await FabricInstaller.InstallAsync(Config.Current.GameDir, BaseVersion,
                        LoaderVersion, this, Cts.Token).ConfigureAwait(false);
                    Log.Info($"{VersionId} 安装完成");
                }
                else if (Kind == InstallKind.Forge)
                {
                    VersionId = await ForgeInstaller.InstallAsync(Config.Current.GameDir, BaseVersion,
                        LoaderVersion, this, Cts.Token).ConfigureAwait(false);
                    Log.Info($"{VersionId} 安装完成");
                }
                else if (Kind == InstallKind.NeoForge)
                {
                    VersionId = await NeoForgeInstaller.InstallAsync(Config.Current.GameDir, BaseVersion,
                        LoaderVersion, this, Cts.Token).ConfigureAwait(false);
                    Log.Info($"{VersionId} 安装完成");
                }
                else if (Kind == InstallKind.Modpack)
                {
                    VersionId = await ModpackInstaller.InstallAsync(Config.Current.GameDir, PackPath,
                        BaseVersionFallback, this, Cts.Token).ConfigureAwait(false);
                    Log.Info($"整合包 {VersionId} 安装完成");
                }
                else
                {
                    await VanillaInstaller.InstallAsync(Config.Current.GameDir, VersionId, this, Cts.Token)
                        .ConfigureAwait(false);
                    Log.Info($"{VersionId} 安装完成");
                }

                Completed = true;
                Stage = "安装完成";
                Percent = 100;
            }
            catch (OperationCanceledException)
            {
                Stage = "已取消";
                Log.Warn($"{VersionId} 安装已取消");
            }
            catch (Exception ex)
            {
                Failed = true;
                Error = ex.Message;
                Stage = "安装失败";
                Log.Error($"{VersionId} 安装失败", ex);
            }
            finally
            {
                Running = false;
                SpeedText = "";
                Cts?.Dispose();
                Cts = null;
                Raise();
                OnPropertyChanged(nameof(StateText));
            }
        }

        public void Cancel() => Cts?.Cancel();

        internal void SetStage(string text)
        {
            Stage = text;
            Raise();
        }

        internal void SetProgress(long done, long total, double speed)
        {
            if (total > 0)
            {
                var p = Math.Min(99.9, done * 100.0 / total);
                Percent = p;

                // total <= 1 是"收尾用的占位调用"（task.SetProgress(1,1,0)），
                // 显示成 "1 B / 1 B" 会让用户以为还在跑，直接留空
                SizeText = total <= 1
                    ? ""
                    : $"{Modules.ModNet.Format.Bytes(done)} / {Modules.ModNet.Format.Bytes(total)}";
            }
            SpeedText = speed > 0 ? Modules.ModNet.Format.Speed(speed) : SpeedText;
            Raise();
        }
    }

    /// <summary>原版版本安装（对应 PCL2 的 ModDownload）。</summary>
    public static class VanillaInstaller
    {
        public static bool IsInstalled(string gameDir, string versionId)
        {
            var dir = Path.Combine(Paths.VersionsDir(gameDir), versionId);
            return File.Exists(Path.Combine(dir, versionId + ".json")) &&
                   File.Exists(Path.Combine(dir, versionId + ".jar"));
        }

        /// <summary>
        /// 安装原版。versionId 独立传参（而不是从 task 里取），
        /// 因为 Fabric 安装时会先借同一个任务去补装所依赖的原版。
        /// </summary>
        public static async Task InstallAsync(string gameDir, string versionId, InstallTask task, CancellationToken ct)
        {
            task.SetStage("获取版本清单");

            var list = await Mojang.GetVersionListAsync(true, ct).ConfigureAwait(false);
            var summary = list.FirstOrDefault(v => v.Id == versionId)
                          ?? throw new InvalidOperationException($"版本清单中找不到 {versionId}");

            // ---- 1. 版本 JSON ----
            task.SetStage("下载版本定义");
            var versionDir = Path.Combine(Paths.VersionsDir(gameDir), versionId);
            Directory.CreateDirectory(versionDir);

            var jsonText = await Net.GetStringAsync(summary.Url, ct).ConfigureAwait(false);
            var jsonPath = Path.Combine(versionDir, versionId + ".json");
            File.WriteAllText(jsonPath, jsonText);

            var root = JsonNode.Parse(jsonText) as JsonObject
                       ?? throw new InvalidDataException("版本 JSON 解析失败");
            var resolved = VersionManager.Resolve(gameDir, versionId);

            // ---- 2. 收集任务 ----
            var jobs = new List<DownloadJob>();
            var libsDir = Paths.LibrariesDir(gameDir);

            var client = J.Obj(J.Obj(root, "downloads"), "client");
            if (client != null)
            {
                jobs.Add(new DownloadJob
                {
                    Name = "客户端主文件",
                    Url = J.Str(client, "url"),
                    Path = Path.Combine(versionDir, versionId + ".jar"),
                    Size = J.Long(client, "size"),
                    Sha1 = J.Str(client, "sha1")
                });
            }

            foreach (var lib in resolved.Libraries)
            {
                var arch = Modules.ModJava.JavaFinder.ArchOfCurrentProcess;
                if (!ModLaunch.Rules.Allowed(J.Arr(lib, "rules"), new Dictionary<string, bool>(), arch))
                    continue;

                var downloads = J.Obj(lib, "downloads");
                var artifact = J.Obj(downloads, "artifact");
                var url = J.Str(artifact, "url");
                var relPath = J.Str(artifact, "path") ?? ModLaunch.LaunchBuilder.MavenPath(J.Str(lib, "name"));
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(relPath)) continue;

                jobs.Add(new DownloadJob
                {
                    Name = J.Str(lib, "name"),
                    Url = url,
                    Path = Path.Combine(libsDir, relPath.Replace('/', Path.DirectorySeparatorChar)),
                    Size = J.Long(artifact, "size"),
                    Sha1 = J.Str(artifact, "sha1")
                });

                // 原生库
                var natives = J.Obj(lib, "natives");
                var classifier = natives != null ? J.Str(natives, "windows") : null;
                if (!string.IsNullOrEmpty(classifier))
                {
                    var natArt = J.Obj(J.Obj(downloads, "classifiers"), classifier);
                    var natUrl = J.Str(natArt, "url");
                    var natPath = J.Str(natArt, "path");
                    if (!string.IsNullOrEmpty(natUrl) && !string.IsNullOrEmpty(natPath))
                    {
                        if (jobs.All(j => j.Path != Path.Combine(libsDir, natPath.Replace('/', Path.DirectorySeparatorChar))))
                            jobs.Add(new DownloadJob
                            {
                                Name = J.Str(lib, "name") + " (natives)",
                                Url = natUrl,
                                Path = Path.Combine(libsDir, natPath.Replace('/', Path.DirectorySeparatorChar)),
                                Size = J.Long(natArt, "size"),
                                Sha1 = J.Str(natArt, "sha1")
                            });
                    }
                }
            }

            // ---- 3. 资源索引 ----
            task.SetStage("获取资源索引");
            var assetIndexId = resolved.EffectiveAssetIndex;
            var indexesDir = Path.Combine(Paths.AssetsDir(gameDir), "indexes");
            Directory.CreateDirectory(indexesDir);
            var indexFile = Path.Combine(indexesDir, assetIndexId + ".json");

            JsonObject index = null;
            if (!string.IsNullOrEmpty(resolved.AssetIndexUrl))
            {
                var indexText = await Net.GetStringAsync(resolved.AssetIndexUrl, ct).ConfigureAwait(false);
                File.WriteAllText(indexFile, indexText);
                index = JsonNode.Parse(indexText) as JsonObject;
            }
            else if (File.Exists(indexFile))
            {
                index = JsonNode.Parse(File.ReadAllText(indexFile)) as JsonObject;
            }

            var assetJobs = new List<DownloadJob>();
            if (index != null)
            {
                var objects = J.Obj(index, "objects");
                var objectsDir = Path.Combine(Paths.AssetsDir(gameDir), "objects");
                if (objects != null)
                {
                    foreach (var kv in objects)
                    {
                        var hash = J.Str(kv.Value, "hash");
                        if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;
                        var sub = hash.Substring(0, 2);
                        assetJobs.Add(new DownloadJob
                        {
                            Name = kv.Key,
                            Url = $"https://resources.download.minecraft.net/{sub}/{hash}",
                            Path = Path.Combine(objectsDir, sub, hash),
                            Size = J.Long(kv.Value, "size"),
                            Sha1 = hash
                        });
                    }
                }
            }

            // ---- 4. 计算总进度 ----
            var totalBytes = jobs.Sum(j => j.Size) + assetJobs.Sum(j => j.Size);
            var baseDone = 0L;
            task.SetProgress(baseDone, totalBytes, 0);

            // ---- 5. 依赖库 + 客户端 ----
            task.SetStage($"下载依赖库（{jobs.Count} 个文件）");
            foreach (var j in jobs) task.Engine.Enqueue(j);

            var lastReport = 0L;
            task.Engine.Changed += () =>
            {
                task.SetProgress(baseDone + task.Engine.DoneBytes, totalBytes, task.Engine.Speed);
                lastReport = task.Engine.DoneBytes;
            };

            var failed = await task.Engine.RunAsync(16, ct).ConfigureAwait(false);
            baseDone += task.Engine.DoneBytes;

            if (failed > 0)
            {
                var bad = task.Engine.Jobs.FirstOrDefault(j => j.State == JobState.Failed);
                throw new IOException($"有 {failed} 个文件下载失败" + (bad != null ? $"（{bad.Name}：{bad.Error}）" : ""));
            }

            // ---- 6. 资源文件 ----
            if (assetJobs.Count > 0)
            {
                task.SetStage($"下载资源文件（{assetJobs.Count} 个）");
                var assetQueue = new DownloadQueue();
                foreach (var j in assetJobs) assetQueue.Enqueue(j);

                assetQueue.Changed += () =>
                    task.SetProgress(baseDone + assetQueue.DoneBytes, totalBytes, assetQueue.Speed);

                var assetFailed = await assetQueue.RunAsync(24, ct).ConfigureAwait(false);
                baseDone += assetQueue.DoneBytes;

                if (assetFailed > 0)
                    Log.Warn($"资源文件有 {assetFailed} 个下载失败，游戏可能缺少音效或语言文件");
            }

            // ---- 7. legacy 资源映射 ----
            if (index != null && (J.Bool(index, "virtual") || J.Bool(index, "map_to_resources")))
            {
                task.SetStage("建立旧版资源映射");
                try
                {
                    BuildLegacyAssets(gameDir, assetIndexId, index);
                }
                catch (Exception ex)
                {
                    Log.Warn("旧版资源映射失败：" + ex.Message);
                }
            }

            // ---- 8. 日志配置 ----
            var logFileId = J.Str(J.Obj(J.Obj(J.Obj(root, "logging"), "client"), "file"), "id");
            var logUrl = J.Str(J.Obj(J.Obj(J.Obj(root, "logging"), "client"), "file"), "url");
            if (!string.IsNullOrEmpty(logUrl) && !string.IsNullOrEmpty(logFileId))
            {
                try
                {
                    var logDir = Path.Combine(Paths.AssetsDir(gameDir), "log_configs");
                    Directory.CreateDirectory(logDir);
                    var dest = Path.Combine(logDir, logFileId);
                    if (!File.Exists(dest))
                        File.WriteAllText(dest, await Net.GetStringAsync(logUrl, ct).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    Log.Warn("下载日志配置失败：" + ex.Message);
                }
            }

            task.SetProgress(totalBytes, totalBytes, 0);
            task.SetStage("安装完成");
            Log.Info($"{versionId} 安装流程结束，共 {jobs.Count + assetJobs.Count} 个文件");
        }

        private static void BuildLegacyAssets(string gameDir, string assetIndexId, JsonObject index)
        {
            var assetsRoot = Paths.AssetsDir(gameDir);
            var objectsDir = Path.Combine(assetsRoot, "objects");
            var target = J.Bool(index, "map_to_resources")
                ? Path.Combine(gameDir, "resources")
                : Path.Combine(assetsRoot, "virtual", assetIndexId);

            var objects = J.Obj(index, "objects");
            if (objects == null) return;

            var copied = 0;
            foreach (var kv in objects)
            {
                var hash = J.Str(kv.Value, "hash");
                if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;

                var source = Path.Combine(objectsDir, hash.Substring(0, 2), hash);
                if (!File.Exists(source)) continue;

                var dest = Path.Combine(target, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                if (File.Exists(dest)) continue;

                try { File.Copy(source, dest, true); copied++; }
                catch { /* 单个文件失败可忽略 */ }
            }
            Log.Info($"旧版资源映射完成，复制 {copied} 个文件到 {target}");
        }
    }
}
