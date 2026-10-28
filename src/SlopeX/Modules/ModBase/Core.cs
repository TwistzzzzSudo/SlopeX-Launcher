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
        public static string AppDir
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlopeX");
                Directory.CreateDirectory(dir);
                return dir;
            }
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

        /// <summary>启动器目录是否可写（不可写就不能做成便携目录）。</summary>
        private static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".sxwrite");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"启动器目录不可写（{dir}），游戏目录回落到 %APPDATA%：{ex.Message}");
                return false;
            }
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

        /// <summary>启动器自己下载安装的 Java 运行时根目录。</summary>
        public static string RuntimeDir
        {
            get
            {
                var dir = Path.Combine(AppDir, "runtime");
                Directory.CreateDirectory(dir);
                return dir;
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
            set => _current = value;
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
        private bool _versionIsolation = true;
        private bool _isolationSeeded = false;

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

        public string PlayerName { get => _playerName; set => Set(ref _playerName, value); }
        public string Uuid { get => _uuid; set => Set(ref _uuid, value); }

        public int MemoryMb { get => _memoryMb; set => Set(ref _memoryMb, value); }
        public int MinMemoryMb { get => _minMemoryMb; set => Set(ref _minMemoryMb, value); }
        public string JvmArgs { get => _jvmArgs; set => Set(ref _jvmArgs, value); }

        /// <summary>使用 BMCLAPI 镜像下载（国内网络推荐）。</summary>
        public bool UseMirror { get => _useMirror; set => Set(ref _useMirror, value); }

        /// <summary>启动游戏后自动隐藏启动器。</summary>
        public bool CloseOnLaunch { get => _closeOnLaunch; set => Set(ref _closeOnLaunch, value); }

        public int WindowWidth { get => _windowWidth; set => Set(ref _windowWidth, value); }
        public int WindowHeight { get => _windowHeight; set => Set(ref _windowHeight, value); }
        public bool CustomResolution { get => _customResolution; set => Set(ref _customResolution, value); }

        /// <summary>上次选择的版本 ID。</summary>
        public string LastVersion { get => _lastVersion; set => Set(ref _lastVersion, value); }

        /// <summary>当前版本缺少所需 Java 时，自动下载安装（国内镜像）。</summary>
        public bool AutoInstallJava { get => _autoInstallJava; set => Set(ref _autoInstallJava, value); }

        /// <summary>
        /// 版本隔离：每个版本使用独立的游戏目录 versions\&lt;版本 ID&gt;，
        /// mods / 资源包 / 存档 / 设置互不影响。
        /// </summary>
        public bool VersionIsolation { get => _versionIsolation; set => Set(ref _versionIsolation, value); }

        /// <summary>是否已经做过一次"公共目录 → 首个隔离版本"的数据继承。</summary>
        public bool IsolationSeeded { get => _isolationSeeded; set => Set(ref _isolationSeeded, value); }

        public static Config Load()
        {
            try
            {
                if (File.Exists(Paths.ConfigFile))
                {
                    var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(Paths.ConfigFile),
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
