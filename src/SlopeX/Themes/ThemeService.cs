using System;
using System.Windows;
using Microsoft.Win32;
using SlopeX.Modules.ModBase;

namespace SlopeX.Themes
{
    /// <summary>外观模式（用户可选的三态）。</summary>
    public enum AppearanceMode
    {
        /// <summary>跟随系统深浅色。</summary>
        System,
        /// <summary>极简白纸（默认）。</summary>
        Light,
        /// <summary>极简墨纸。</summary>
        Dark,
        /// <summary>复古未来（合成波暮色位图 + 深紫卡片）。</summary>
        RetroFuture,
        /// <summary>ViceCity（迈阿密暮色插画 + 深梅色卡片）。</summary>
        ViceCity
    }

    /// <summary>
    /// 主题服务：把 <c>Themes/Palette.Light.xaml</c> / <c>Themes/Palette.Dark.xaml</c>
    /// 整份替换掉，实现运行时换主题（不重启）。
    ///
    /// 前提：所有颜色引用必须是 <c>{DynamicResource …}</c>。
    /// 用 <c>{StaticResource …}</c> 的地方不会刷新——这是换肤最常见的坑。
    /// </summary>
    public static class ThemeService
    {
        private const string LightUri = "Themes/Palette.Light.xaml";
        private const string DarkUri = "Themes/Palette.Dark.xaml";
        private const string RetroFutureUri = "Themes/Palette.RetroFuture.xaml";
        private const string ViceCityUri = "Themes/Palette.ViceCity.xaml";
        private const string PersonalizeKey =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private static bool _hooked;

        /// <summary>当前生效的是否为深色（霓虹黄昏也算深色）。</summary>
        public static bool IsDark { get; private set; }

        /// <summary>当前实际生效的外观（跟随系统时会解析成 Light / Dark）。</summary>
        public static AppearanceMode CurrentMode { get; private set; } = AppearanceMode.Light;

        /// <summary>当前是否是复古未来（决定是否显示位图背景）。</summary>
        public static bool IsRetroFuture => CurrentMode == AppearanceMode.RetroFuture;

        /// <summary>当前是否是 ViceCity（决定是否显示位图背景）。</summary>
        public static bool IsViceCity => CurrentMode == AppearanceMode.ViceCity;

        /// <summary>当前主题是否用插画当背景（两种插画主题之一）。</summary>
        public static bool HasArtBackdrop => IsRetroFuture || IsViceCity;

        /// <summary>主题变化后触发（供将来需要联动重绘的控件使用）。</summary>
        public static event EventHandler Changed;

        public static AppearanceMode ParseMode(string raw) => raw switch
        {
            "System" => AppearanceMode.System,
            "Dark" => AppearanceMode.Dark,
            "RetroFuture" => AppearanceMode.RetroFuture,
            "Neon" => AppearanceMode.RetroFuture,   // 旧配置兼容：这个主题原名「霓虹黄昏」
            "ViceCity" => AppearanceMode.ViceCity,
            _ => AppearanceMode.Light
        };

        public static string ToConfigString(AppearanceMode mode) => mode switch
        {
            AppearanceMode.System => "System",
            AppearanceMode.Dark => "Dark",
            AppearanceMode.RetroFuture => "RetroFuture",
            AppearanceMode.ViceCity => "ViceCity",
            _ => "Light"
        };

        /// <summary>启动时调用一次：绑系统事件 + 应用配置里的主题。</summary>
        public static void Initialize()
        {
            if (!_hooked)
            {
                try
                {
                    SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                    _hooked = true;
                }
                catch (Exception ex)
                {
                    // 没有消息循环等极端情况下绑不上，不影响手动切换
                    Log.Warn("监听系统主题变化失败：" + ex.Message);
                }
            }

            Apply(ParseMode(Config.Current.Appearance), force: true);
        }

        /// <summary>按指定模式应用主题（System 时实时读注册表）。</summary>
        public static void Apply(AppearanceMode mode, bool force = false)
        {
            // 跟随系统时解析成实际深浅色；霓虹是独立主题，不参与这个解析
            var effective = mode switch
            {
                AppearanceMode.System => IsSystemDark() ? AppearanceMode.Dark : AppearanceMode.Light,
                _ => mode
            };

            if (!force && effective == CurrentMode) return;

            CurrentMode = effective;
            IsDark = effective != AppearanceMode.Light;

            SwapPalette(effective switch
            {
                AppearanceMode.RetroFuture => RetroFutureUri,
                AppearanceMode.ViceCity => ViceCityUri,
                AppearanceMode.Dark => DarkUri,
                _ => LightUri
            });

            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>重新按配置应用（设置页保存后调用）。</summary>
        public static void RefreshFromConfig() => Apply(ParseMode(Config.Current.Appearance));

        /// <summary>系统当前是否深色（读 AppsUseLightTheme，0 表示深色）。</summary>
        public static bool IsSystemDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
            }
            catch (Exception ex)
            {
                Log.Warn("读取系统主题失败：" + ex.Message);
            }
            return false;
        }

        private static void SwapPalette(string uri)
        {
            var app = Application.Current;
            if (app == null) return;

            var merged = app.Resources.MergedDictionaries;
            var dict = new ResourceDictionary { Source = new Uri(uri, UriKind.Relative) };

            for (var i = 0; i < merged.Count; i++)
            {
                var src = merged[i].Source?.OriginalString ?? "";
                if (src.IndexOf("Palette.", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    merged[i] = dict;
                    return;
                }
            }

            // 没找到（理论上不会）：插到最前面，保证优先级高于 Style
            merged.Insert(0, dict);
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General &&
                e.Category != UserPreferenceCategory.Color)
                return;

            // 用户选了固定浅/深时，忽略系统变化
            if (ParseMode(Config.Current.Appearance) != AppearanceMode.System) return;

            var app = Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
                app.Dispatcher.BeginInvoke(new Action(() => Apply(AppearanceMode.System, force: true)));
            else
                Apply(AppearanceMode.System, force: true);
        }
    }
}
