using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SlopeX.Modules.ModBase
{
    /// <summary>全局路径（对应 PCL2 的 ModBase 路径管理）。</summary>
    public static class Paths
    {
        /// <summary>%APPDATA%\SlopeX：旧位置、便携不可写时的回落点，也是 Java 运行时的默认位置。</summary>
        public static string AppDataDir
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlopeX");
                try { Directory.CreateDirectory(dir); } catch { /* 忽略 */ }
                return dir;
            }
        }

        private static string _dataDir;
        private static bool _migrationChecked;

        /// <summary>
        /// 数据目录（配置 / 日志 / 缓存）：优先 <c>&lt;启动器目录&gt;\SlopeXData</c>，
        /// 整个文件夹拷走即可连设置一起搬走；启动器目录不可写（如装在 Program Files）时回落
        /// <c>%APPDATA%\SlopeX</c>。
        ///
        /// 注意：Java 运行时**不**放这里（占 180 MB，跟着拷贝不划算），见 <see cref="RuntimeDir"/>。
        /// </summary>
        public static string DataDir
        {
            get
            {
                if (_dataDir != null) return _dataDir;

                var root = LauncherRoot;
                string dir;
                string reason = null;

                if (ProbeWrite(root))
                {
                    dir = Path.Combine(root, "SlopeXData");
                }
                else
                {
                    dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlopeX");
                    reason = "启动器目录不可写";
                }

                // 先落字段：迁移过程中写日志会再次读 DataDir，必须已经是最终值
                _dataDir = dir;
                try { Directory.CreateDirectory(dir); } catch { /* 交给后续读写报错 */ }

                MigrateFromAppData(dir, reason);
                return dir;
            }
        }

        /// <summary>兼容旧代码：数据目录即原来的 AppDir。</summary>
        public static string AppDir => DataDir;

        /// <summary>
        /// 首次启用便携目录时，把 <c>%APPDATA%\SlopeX</c> 的配置与日志**复制**过来。
        /// 原文件保留不删——用户想回退到旧位置时不会丢东西。
        /// </summary>
        private static void MigrateFromAppData(string dataDir, string fallbackReason)
        {
            if (_migrationChecked) return;
            _migrationChecked = true;

            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlopeX");

            // 本来就用 %APPDATA%，没什么可迁移的
            if (SamePath(dataDir, legacy)) return;

            try
            {
                var moved = new List<string>();

                var legacyConfig = Path.Combine(legacy, "config.json");
                var newConfig = Path.Combine(dataDir, "config.json");
                if (File.Exists(legacyConfig) && !File.Exists(newConfig))
                {
                    File.Copy(legacyConfig, newConfig);
                    moved.Add("配置");
                }

                var legacyLogs = Path.Combine(legacy, "logs");
                var newLogs = Path.Combine(dataDir, "logs");
                if (Directory.Exists(legacyLogs) && !Directory.Exists(newLogs))
                {
                    CopyDirectory(legacyLogs, newLogs);
                    moved.Add("日志");
                }

                var suffix = fallbackReason != null ? $"（回落原因：{fallbackReason}）" : "";
                if (moved.Count > 0)
                    Log.Info($"便携目录已启用：{dataDir}{suffix}；已复制 {string.Join("、", moved)}，原文件保留");
                else
                    Log.Info($"便携目录已启用：{dataDir}{suffix}");
            }
            catch (Exception ex)
            {
                Log.Warn("迁移到便携目录失败（不影响使用）：" + ex.Message);
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                var dst = Path.Combine(target, Path.GetFileName(file));
                if (!File.Exists(dst)) File.Copy(file, dst);
            }
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }

        /// <summary>启动器根目录（即 SlopeX.exe 所在目录）。</summary>
        public static string LauncherRoot
        {
            get
            {
                try
                {
                    var dir = AppContext.BaseDirectory;
                    if (string.IsNullOrEmpty(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;
                    if (string.IsNullOrEmpty(dir)) dir = Environment.CurrentDirectory;
                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
                }
                catch
                {
                    return Environment.CurrentDirectory;
                }
            }
        }

        private static string _defaultGameDir;

        /// <summary>
        /// 默认游戏目录：启动器根目录下的 .minecraft（便携，整个文件夹可随意搬迁）。
        /// 若启动器所在目录不可写（例如装在 Program Files），回落到 %APPDATA%\SlopeX\.minecraft。
        /// </summary>
        public static string DefaultGameDir
        {
            get
            {
                if (_defaultGameDir != null) return _defaultGameDir;

                var root = LauncherRoot;
                _defaultGameDir = CanWrite(root)
                    ? Path.Combine(root, ".minecraft")
                    : Path.Combine(AppDir, ".minecraft");
                return _defaultGameDir;
            }
        }

        /// <summary>旧版的默认游戏目录（%APPDATA%\.minecraft），仅用于判断是否需要接管配置。</summary>
        public static string LegacyGameDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

        /// <summary>
        /// 静默探测目录可写性。**不能在这里写日志**——日志本身要落盘到 DataDir，
        /// 而 DataDir 解析又依赖本方法，会形成递归。
        /// </summary>
        private static bool ProbeWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".sxwrite");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>启动器目录是否可写（不可写就不能做成便携目录）。</summary>
        private static bool CanWrite(string dir)
        {
            if (ProbeWrite(dir)) return true;
            Log.Warn($"启动器目录不可写（{dir}），游戏目录回落到 %APPDATA%");
            return false;
        }

        /// <summary>把绝对路径转成"可存进配置"的形式：位于启动器目录内则存相对路径。</summary>
        public static string ToStorablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                var root = LauncherRoot;
                if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return "." + full.Substring(root.Length);
                return full;
            }
            catch
            {
                return path;
            }
        }

        /// <summary>读取配置里的路径：相对路径按启动器目录解析（整个文件夹搬走也能用）。</summary>
        public static string FromStorablePath(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return DefaultGameDir;
            try
            {
                return Path.IsPathRooted(stored)
                    ? stored
                    : Path.GetFullPath(Path.Combine(LauncherRoot, stored));
            }
            catch
            {
                return DefaultGameDir;
            }
        }

        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                var x = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
                var y = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
                return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 配置里存的是旧默认目录、且新目录里已经有内容（说明数据已迁移过来）时，
        /// 自动把配置切到新的便携目录。
        /// </summary>
        public static bool ShouldAdoptPortableGameDir(string saved)
        {
            if (string.IsNullOrWhiteSpace(saved)) return false;
            if (!SamePath(saved, LegacyGameDir)) return false;

            var target = DefaultGameDir;
            if (SamePath(target, saved)) return false;
            if (!Directory.Exists(target)) return false;

            if (Directory.Exists(Path.Combine(target, "versions"))) return true;
            // 新目录还是空的：只有在旧目录也没东西时才切，避免把用户的版本"弄丢"
            return !Directory.Exists(Path.Combine(LegacyGameDir, "versions"));
        }

        public static string LogDir
        {
            get
            {
                var dir = Path.Combine(AppDir, "logs");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string ConfigFile => Path.Combine(AppDir, "config.json");
        public static string CacheDir
        {
            get
            {
                var dir = Path.Combine(AppDir, "cache");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// 启动器自己下载安装的 Java 运行时根目录。
        /// 默认仍在 <c>%APPDATA%\SlopeX\runtime</c>——一个 Java 运行时约 180 MB，
        /// 跟着启动器目录拷来拷去不划算；需要彻底便携时可在设置里改到启动器目录内。
        /// </summary>
        public static string RuntimeDir
        {
            get
            {
                var custom = RuntimeDirOverride;
                var dir = string.IsNullOrWhiteSpace(custom) ? Path.Combine(AppDataDir, "runtime") : custom;
                try { Directory.CreateDirectory(dir); } catch { /* 忽略 */ }
                return dir;
            }
        }

        /// <summary>由配置驱动的运行时目录覆盖值（空表示用默认位置）。</summary>
        public static string RuntimeDirOverride { get; set; } = "";

        /// <summary>若配置里的路径为空或非法则返回 null（供可选路径字段使用）。</summary>
        public static string FromStoredPathOrNull(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;
            try
            {
                return Path.IsPathRooted(stored)
                    ? stored
                    : Path.GetFullPath(Path.Combine(LauncherRoot, stored));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>某个主版本 Java 的安装目录，例如 runtime\java-25。</summary>
        public static string JavaDir(int major) => Path.Combine(RuntimeDir, "java-" + major);

        public static string VersionsDir(string gameDir) => Path.Combine(gameDir, "versions");
        public static string LibrariesDir(string gameDir) => Path.Combine(gameDir, "libraries");
        public static string AssetsDir(string gameDir) => Path.Combine(gameDir, "assets");
    }

    public enum LogLevel { Trace, Info, Warn, Error }

    public sealed class LogEntry
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public LogLevel Level { get; set; }
        public string Message { get; set; } = "";
        public string Stamp => Time.ToString("HH:mm:ss");
    }

    /// <summary>日志：内存环形缓冲 + UI 事件 + 落盘。</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static readonly List<LogEntry> Buffer = new List<LogEntry>(512);
        private static string _file;

        /// <summary>最多保留的日志条数，避免长时间运行吃内存。</summary>
        public const int MaxBuffer = 2000;

        public static event Action<LogEntry> OnEntry;

        public static IReadOnlyList<LogEntry> Snapshot()
        {
            lock (Gate) return Buffer.ToArray();
        }

        public static void Clear()
        {
            lock (Gate) Buffer.Clear();
        }

        public static void Trace(string msg) => Write(LogLevel.Trace, msg);
        public static void Info(string msg) => Write(LogLevel.Info, msg);
        public static void Warn(string msg) => Write(LogLevel.Warn, msg);
        public static void Error(string msg) => Write(LogLevel.Error, msg);

        public static void Error(string msg, Exception ex) =>
            Write(LogLevel.Error, ex == null ? msg : msg + " :: " + ex.GetType().Name + ": " + ex.Message);

        private static void Write(LogLevel level, string msg)
        {
            var e = new LogEntry { Level = level, Message = msg ?? "" };
            lock (Gate)
            {
                Buffer.Add(e);
                if (Buffer.Count > MaxBuffer) Buffer.RemoveRange(0, Buffer.Count - MaxBuffer);
            }

            try { OnEntry?.Invoke(e); } catch { /* UI 线程异常不能影响主流程 */ }

            try
            {
                if (_file == null)
                {
                    Directory.CreateDirectory(Paths.LogDir);
                    _file = Path.Combine(Paths.LogDir, "SlopeX-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                }
                File.AppendAllText(_file, $"[{e.Time:HH:mm:ss.fff}] [{level}] {e.Message}{Environment.NewLine}");
            }
            catch { /* 日志落盘失败不应中断启动 */ }
        }
    }

    /// <summary>启动器配置，落在 %APPDATA%\SlopeX\config.json。</summary>
    public sealed class Config : ObservableObject
    {
        private static Config _current;

        public static Config Current
        {
            get
            {
                if (_current == null) _current = Load();
                return _current;
            }
            set
            {
                _current = value;
                Paths.RuntimeDirOverride = value?.JavaRuntimeDir ?? "";
            }
        }

        private string _gameDir = Paths.DefaultGameDir;
        private string _javaPath = "";
        private string _playerName = "Steve";
        private string _uuid = "";
        private int _memoryMb = 4096;
        private int _minMemoryMb = 1024;
        private string _jvmArgs = "";
        private bool _useMirror = true;
        private bool _closeOnLaunch = false;
        private int _windowWidth = 854;
        private int _windowHeight = 480;
        private bool _customResolution = false;
        private string _lastVersion = "";
        private bool _autoInstallJava = true;
        private string _theme = "paper";
        private string _appearance = "Light";
        private string _javaRuntimeDir = "";
        private bool _dismissLegacySavesHint;
        private string _curseForgeApiKey = "";
        private double _contentOpacity = 1.0;

        /// <summary>游戏目录（.minecraft 根），内存中始终是绝对路径。</summary>
        [JsonIgnore]
        public string GameDir
        {
            get => _gameDir;
            set { if (Set(ref _gameDir, value)) { OnPropertyChanged(nameof(GameDirHint)); } }
        }

        /// <summary>
        /// 落盘形式：位于启动器目录内时写成相对路径（如 <c>.\.minecraft</c>），
        /// 这样整个 SlopeX 文件夹搬到别处后依然能正确解析。
        /// </summary>
        [JsonPropertyName("GameDir")]
        public string GameDirStored
        {
            get => Paths.ToStorablePath(_gameDir);
            set
            {
                StoredGameDirWasAbsolute = !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value);
                _gameDir = Paths.FromStorablePath(value);
            }
        }

        /// <summary>配置里存的是绝对路径（老版本写的）——加载后会顺手收敛成相对路径。</summary>
        [JsonIgnore]
        public bool StoredGameDirWasAbsolute { get; private set; }

        [JsonIgnore]
        public string GameDirHint => string.IsNullOrWhiteSpace(GameDir) ? "未设置" : GameDir;

        /// <summary>自定义 Java 路径，空表示自动选择。</summary>
        public string JavaPath { get => _javaPath; set => Set(ref _javaPath, value); }

        /// <summary>
        /// 托管 Java 运行时的根目录（空 = 默认 <c>%APPDATA%\SlopeX\runtime</c>）。
        /// 设在启动器目录内即可让运行时也跟着移植。
        /// </summary>
        [JsonIgnore]
        public string JavaRuntimeDir
        {
            get => _javaRuntimeDir;
            set
            {
                if (!Set(ref _javaRuntimeDir, value ?? "")) return;
                Paths.RuntimeDirOverride = _javaRuntimeDir;
                OnPropertyChanged(nameof(JavaRuntimeDirStored));
                OnPropertyChanged(nameof(JavaRuntimeDirHint));
            }
        }

        /// <summary>落盘形式：位于启动器目录内则存相对路径。</summary>
        [JsonPropertyName("JavaRuntimeDir")]
        public string JavaRuntimeDirStored
        {
            get => Paths.ToStorablePath(_javaRuntimeDir);
            set => JavaRuntimeDir = Paths.FromStoredPathOrNull(value) ?? "";
        }

        /// <summary>界面上显示的实际生效路径。</summary>
        [JsonIgnore]
        public string JavaRuntimeDirHint =>
            string.IsNullOrWhiteSpace(_javaRuntimeDir) ? "默认位置（%APPDATA%\\SlopeX\\runtime）" : Paths.RuntimeDir;

        public string PlayerName { get => _playerName; set => Set(ref _playerName, value); }
        public string Uuid { get => _uuid; set => Set(ref _uuid, value); }

        public int MemoryMb { get => _memoryMb; set => Set(ref _memoryMb, value); }
        public int MinMemoryMb { get => _minMemoryMb; set => Set(ref _minMemoryMb, value); }
        public string JvmArgs { get => _jvmArgs; set => Set(ref _jvmArgs, value); }

        /// <summary>使用 BMCLAPI 镜像下载（国内网络推荐）。</summary>
        public bool UseMirror { get => _useMirror; set => Set(ref _useMirror, value); }

        /// <summary>
        /// CurseForge 官方 API Key（可选，用户在设置里自己填）。
        /// 没有这个 Key，CurseForge 的搜索接口一律 403——官方要求申请后才给，不能绕过。
        /// </summary>
        public string CurseForgeApiKey { get => _curseForgeApiKey; set => Set(ref _curseForgeApiKey, value); }

        /// <summary>启动游戏后自动隐藏启动器。</summary>
        public bool CloseOnLaunch { get => _closeOnLaunch; set => Set(ref _closeOnLaunch, value); }

        /// <summary>
        /// 内容区整体不透明度（0.35–1）。插画主题下用来控制"卡片透出多少背景"。
        /// 作用于整个页面宿主（底板 + 文字 + 按钮一起淡出），不做分层。
        /// </summary>
        public double ContentOpacity
        {
            get => _contentOpacity;
            set => Set(ref _contentOpacity, Math.Clamp(value, 0.35, 1.0));
        }

        public int WindowWidth { get => _windowWidth; set => Set(ref _windowWidth, value); }
        public int WindowHeight { get => _windowHeight; set => Set(ref _windowHeight, value); }
        public bool CustomResolution { get => _customResolution; set => Set(ref _customResolution, value); }

        /// <summary>上次选择的版本 ID。</summary>
        public string LastVersion { get => _lastVersion; set => Set(ref _lastVersion, value); }

        /// <summary>当前版本缺少所需 Java 时，自动下载安装（国内镜像）。</summary>
        public bool AutoInstallJava { get => _autoInstallJava; set => Set(ref _autoInstallJava, value); }

        /// <summary>
        /// 主题族。当前只有纸媒一套 <c>paper</c>（极简白纸 / 极简墨纸共用一套版式），
        /// 将来若增加第二套视觉语言，在这里加取值即可。
        /// </summary>
        public string Theme { get => _theme; set => Set(ref _theme, value); }

        /// <summary>
        /// 外观模式：<c>System</c> 跟随系统 / <c>Light</c> 极简白纸 / <c>Dark</c> 极简墨纸。
        /// 默认 <c>Light</c>——极简白纸是默认主题，跟随系统需用户主动选择。
        /// </summary>
        public string Appearance { get => _appearance; set => Set(ref _appearance, value); }

        /// <summary>用户是否已对"根目录残留旧存档"提示点过「不再提示」。</summary>
        public bool DismissLegacySavesHint
        {
            get => _dismissLegacySavesHint;
            set => Set(ref _dismissLegacySavesHint, value);
        }

        // 注：原 VersionIsolation / IsolationSeeded 两个配置项已移除。
        // 版本隔离（含存档）现在是强制行为，不再是可配置项，也就不该存在"关闭"的记录。
        // 旧配置文件里的这两个字段会在反序列化时被忽略，不需要迁移。

        public static Config Load()
        {
            try
            {
                if (File.Exists(Paths.ConfigFile))
                {
                    var json = File.ReadAllText(Paths.ConfigFile);
                    var cfg = JsonSerializer.Deserialize<Config>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (cfg != null)
                    {
                        if (string.IsNullOrWhiteSpace(cfg.GameDir))
                        {
                            cfg.GameDir = Paths.DefaultGameDir;
                        }
                        else if (Paths.ShouldAdoptPortableGameDir(cfg.GameDir))
                        {
                            Log.Info($"检测到数据已迁到启动器目录，游戏目录切换为 {Paths.DefaultGameDir}");
                            cfg.GameDir = Paths.DefaultGameDir;
                            cfg.Save();
                        }

                        if (string.IsNullOrWhiteSpace(cfg.PlayerName)) cfg.PlayerName = "Steve";

                        // 老配置里的绝对路径：若位于启动器目录内，改写为相对路径，保证文件夹可整体搬迁
                        if (cfg.StoredGameDirWasAbsolute &&
                            !Path.IsPathRooted(Paths.ToStorablePath(cfg.GameDir)))
                            cfg.Save();

                        // 旧配置缺新字段（或残留已移除的隔离开关）时重写一次：
                        // 一是把已删除的 VersionIsolation / IsolationSeeded 清掉，
                        // 二是让 Appearance / Theme 出现在文件里，配置自解释。
                        var needsRewrite =
                            json.Contains("VersionIsolation", StringComparison.OrdinalIgnoreCase) ||
                            json.Contains("IsolationSeeded", StringComparison.OrdinalIgnoreCase) ||
                            !json.Contains("\"Appearance\"", StringComparison.OrdinalIgnoreCase) ||
                            !json.Contains("\"Theme\"", StringComparison.OrdinalIgnoreCase) ||
                            !json.Contains("\"JavaRuntimeDir\"", StringComparison.OrdinalIgnoreCase);

                        if (needsRewrite)
                        {
                            Log.Info("配置文件结构有更新，已重写（移除旧隔离开关，补充主题字段）");
                            cfg.Save();
                        }

                        return cfg;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取配置失败，已回退默认配置", ex);
            }
            return new Config();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Paths.AppDir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Paths.ConfigFile, json);
            }
            catch (Exception ex)
            {
                Log.Error("保存配置失败", ex);
            }
        }
    }

    /// <summary>简易 MVVM 基类。</summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>JSON 小工具。</summary>
    public static class J
    {
        private static JsonObject O(JsonNode node) => node as JsonObject;

        public static string Str(JsonNode node, string key)
        {
            try
            {
                var obj = O(node);
                if (obj == null) return null;
                if (!obj.TryGetPropertyValue(key, out var v) || v == null) return null;
                if (v is JsonValue jv) return jv.TryGetValue<string>(out var s) ? s : jv.ToString();
                return v.ToString();
            }
            catch { return null; }
        }

        public static long Long(JsonNode node, string key, long fallback = 0)
        {
            try
            {
                var obj = O(node);
                if (obj == null) return fallback;
                if (!obj.TryGetPropertyValue(key, out var v) || v == null) return fallback;
                if (v is JsonValue jv)
                {
                    if (jv.TryGetValue<long>(out var l)) return l;
                    if (jv.TryGetValue<double>(out var d)) return (long)d;
                }
            }
            catch { /* ignored */ }
            return fallback;
        }

        public static bool Bool(JsonNode node, string key, bool fallback = false)
        {
            try
            {
                var obj = O(node);
                if (obj == null) return fallback;
                if (!obj.TryGetPropertyValue(key, out var v) || v == null) return fallback;
                if (v is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
            }
            catch { /* ignored */ }
            return fallback;
        }

        public static JsonObject Obj(JsonNode node, string key)
        {
            var obj = O(node);
            if (obj == null) return null;
            return obj.TryGetPropertyValue(key, out var v) ? v as JsonObject : null;
        }

        public static JsonArray Arr(JsonNode node, string key)
        {
            var obj = O(node);
            if (obj == null) return null;
            return obj.TryGetPropertyValue(key, out var v) ? v as JsonArray : null;
        }

        public static bool IsTruthy(JsonNode n) => n is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;
    }
}
