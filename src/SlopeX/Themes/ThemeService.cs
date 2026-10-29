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
        Dark
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
        private const string PersonalizeKey =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private static bool _hooked;

        /// <summary>当前生效的是否为深色。</summary>
        public static bool IsDark { get; private set; }

        /// <summary>主题变化后触发（供将来需要联动重绘的控件使用）。</summary>
        public static event EventHandler Changed;

        public static AppearanceMode ParseMode(string raw) => raw switch
        {
            "System" => AppearanceMode.System,
            "Dark" => AppearanceMode.Dark,
            _ => AppearanceMode.Light
        };

        public static string ToConfigString(AppearanceMode mode) => mode switch
        {
            AppearanceMode.System => "System",
            AppearanceMode.Dark => "Dark",
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
            var dark = mode switch
            {
                AppearanceMode.Dark => true,
                AppearanceMode.Light => false,
                _ => IsSystemDark()
            };

            if (!force && dark == IsDark) return;

            IsDark = dark;
            SwapPalette(dark ? DarkUri : LightUri);
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
