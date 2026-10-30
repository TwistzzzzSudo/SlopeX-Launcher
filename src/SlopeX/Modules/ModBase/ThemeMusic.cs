using System;
using System.IO;
using System.Windows.Media;
using SlopeX.Modules.ModNet;
using SlopeX.Themes;

namespace SlopeX.Modules.ModBase
{
    /// <summary>
    /// 主题背景音乐。
    ///
    /// 为什么先把资源落盘再播：<see cref="MediaPlayer"/> 对 pack:// 资源 URI 支持不稳，
    /// 而单文件发布时资源又确实在 exe 内部 —— 折中方案是首次播放时解到
    /// <c>SlopeXData\cache\music</c>（带大小校验，已存在就复用），再用文件路径播。
    ///
    /// 循环用 MediaEnded 手动重播：MediaPlayer 没有 Loop 属性。
    /// </summary>
    public static class ThemeMusic
    {
        private static readonly MediaPlayer Player = new MediaPlayer();

        private static string _currentTrack;      // 当前曲目对应的资源名，避免同曲重播
        private static bool _initialized;

        // 资源是否存在的探测结果（每个曲目只查一次）
        private static readonly System.Collections.Generic.Dictionary<string, bool> _resourceCache = new();

        /// <summary>曲目切换或播放状态变化时触发（供 UI 更新图标）。</summary>
        public static event EventHandler StateChanged;

        /// <summary>当前是否处于暂停（或没有可播曲目）。</summary>
        public static bool IsPaused { get; private set; }

        /// <summary>
        /// 当前主题是否有可播放的背景音乐。
        /// 不只看"这个主题该有曲目"，还要确认音频资源真的在包里 ——
        /// 音频是可选素材（不进仓库），缺失时按钮应当直接隐藏，而不是点了没反应。
        /// </summary>
        public static bool HasTrack
        {
            get
            {
                var track = TrackFor(ThemeService.CurrentMode);
                return !string.IsNullOrEmpty(track) && ResourceExists(track);
            }
        }

        private static bool ResourceExists(string name)
        {
            if (_resourceCache.TryGetValue(name, out var cached)) return cached;

            bool ok;
            try
            {
                var uri = new Uri($"pack://application:,,,/music/{name}", UriKind.Absolute);
                var info = System.Windows.Application.GetResourceStream(uri);
                ok = info?.Stream != null;
                info?.Stream?.Dispose();
            }
            catch
            {
                ok = false;
            }

            _resourceCache[name] = ok;
            return ok;
        }

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            Player.Volume = Math.Clamp(Config.Current.MusicVolume, 0, 1);
            Player.MediaEnded += (s, e) =>
            {
                // 循环播放
                Player.Position = TimeSpan.Zero;
                Player.Play();
            };
            Player.MediaFailed += (s, e) =>
            {
                Log.Warn("背景音乐播放失败：" + e.ErrorException?.Message);
                IsPaused = true;
                StateChanged?.Invoke(null, EventArgs.Empty);
            };

            ThemeService.Changed += (s, e) => ApplyForTheme();
            ApplyForTheme();
        }

        /// <summary>切到当前主题对应的曲目（纸质主题没有音乐，直接停）。</summary>
        public static void ApplyForTheme()
        {
            var track = TrackFor(ThemeService.CurrentMode);
            if (string.IsNullOrEmpty(track))
            {
                Player.Stop();
                _currentTrack = null;
                IsPaused = true;
                StateChanged?.Invoke(null, EventArgs.Empty);
                return;
            }

            if (_currentTrack == track && Player.Source != null && !IsPaused) return;

            try
            {
                var path = EnsureExtracted(track);
                if (path == null)
                {
                    IsPaused = true;
                    StateChanged?.Invoke(null, EventArgs.Empty);
                    return;
                }

                _currentTrack = track;
                Player.Open(new Uri(path, UriKind.Absolute));
                Player.Position = TimeSpan.Zero;
                Player.Play();

                IsPaused = false;
                StateChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Warn($"背景音乐加载失败（{track}）：{ex.Message}");
                IsPaused = true;
                StateChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>播放 / 暂停切换。</summary>
        public static void Toggle()
        {
            if (!HasTrack) return;

            if (IsPaused)
            {
                if (Player.Source == null) ApplyForTheme();
                else Player.Play();

                IsPaused = false;
            }
            else
            {
                Player.Pause();
                IsPaused = true;
            }

            StateChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>音量 0–1，同时写回配置。</summary>
        public static void SetVolume(double volume)
        {
            var v = Math.Clamp(volume, 0, 1);
            Player.Volume = v;
            Config.Current.MusicVolume = v;
        }

        public static double Volume => Player.Volume;

        /// <summary>主题 → 曲目资源名；没有音乐的返回 null。</summary>
        private static string TrackFor(AppearanceMode mode) => mode switch
        {
            AppearanceMode.RetroFuture => "retro-future.mp3",
            AppearanceMode.ViceCity => "vicecity.mp3",
            _ => null
        };

        /// <summary>
        /// 把内嵌音频解到缓存目录（一次即可）。已存在且大小一致就直接复用，
        /// 避免每次启动都重写十几 MB。
        /// </summary>
        private static string EnsureExtracted(string resourceName)
        {
            var dir = Path.Combine(Paths.DataDir, "cache", "music");
            Directory.CreateDirectory(dir);

            var target = Path.Combine(dir, resourceName);
            var uri = new Uri($"pack://application:,,,/music/{resourceName}", UriKind.Absolute);

            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream == null)
            {
                Log.Warn("找不到内嵌音频资源：" + resourceName);
                return null;
            }

            if (File.Exists(target) && new FileInfo(target).Length == stream.Length)
                return target;

            using (var file = File.Create(target))
                stream.CopyTo(file);

            Log.Info($"背景音乐已解出：{target}（{Format.Bytes(stream.Length)}）");
            return target;
        }
    }
}
