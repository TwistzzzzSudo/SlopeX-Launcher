using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModJava;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Pages;

namespace SlopeX.Windows
{
    /// <summary>页面生命周期约定。</summary>
    public interface ILauncherPage
    {
        void OnShown();
    }

    public partial class MainWindow : Window
    {
        public static MainWindow Current { get; private set; }

        private PageHome _home;
        private PageVersions _versions;
        private PageDownload _download;
        private PageTasks _tasks;
        private PageSettings _settings;
        private int _currentIndex = -1;

        public MainWindow()
        {
            InitializeComponent();
            Current = this;

            StateChanged += (s, e) =>
            {
                BtnMax.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            };

            Loaded += async (s, e) =>
            {
                NavList.SelectedIndex = 0;
                UpdateAccount();
                await RefreshStatusAsync();
            };

            Closing += (s, e) => Config.Current.Save();
        }

        // ---------------- 导航 ----------------

        private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(NavList.SelectedItem is ListBoxItem item)) return;
            if (!int.TryParse(item.Tag?.ToString(), out var index)) return;
            ShowPage(index);
        }

        public void Navigate(int index)
        {
            if (index < 0 || index > 4) return;
            if (NavList.SelectedIndex == index)
            {
                ShowPage(index);
                return;
            }
            NavList.SelectedIndex = index;
        }

        private void ShowPage(int index)
        {
            if (index == _currentIndex && PageHost.Content != null) return;
            _currentIndex = index;

            FrameworkElement page = index switch
            {
                0 => _home ??= new PageHome(),
                1 => _versions ??= new PageVersions(),
                2 => _download ??= new PageDownload(),
                3 => _tasks ??= new PageTasks(),
                4 => _settings ??= new PageSettings(),
                _ => null
            };

            PageHost.Content = page;
            (page as ILauncherPage)?.OnShown();
        }

        // ---------------- 窗口按钮 ----------------

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Account_Click(object sender, RoutedEventArgs e) => Navigate(4);

        // ---------------- 状态 ----------------

        public void UpdateAccount()
        {
            var name = string.IsNullOrWhiteSpace(Config.Current.PlayerName) ? "Steve" : Config.Current.PlayerName;
            SideAccount.Text = name;
            SideAvatar.Text = name.Substring(0, 1).ToUpperInvariant();
        }

        public void SetStatus(string left, string right = null)
        {
            StatusLeft.Text = left;
            if (right != null) StatusRight.Text = right;
        }

        public async Task RefreshStatusAsync()
        {
            var gameDir = Config.Current.GameDir;
            var count = 0;
            try { count = VersionManager.Scan(gameDir).Count; } catch { }

            SetStatus($"正在检测 Java 运行时…");

            var javas = await Task.Run(() => JavaFinder.All(true));

            var javaText = javas.Count > 0 ? $"Java {javas.Max(j => j.Major)}" : "未检测到 Java";
            SetStatus($"已安装 {count} 个版本   ·   {javaText}   ·   {gameDir}",
                $"内存 {Config.Current.MemoryMb / 1024.0:0.#} GB   ·   {(Config.Current.UseMirror ? "BMCLAPI 镜像" : "官方源")}");
        }

        /// <summary>其它页面更新配置后调用，同步侧边栏与状态栏。</summary>
        public void ApplyConfigChanged()
        {
            UpdateAccount();
            _ = RefreshStatusAsync();
        }
    }
}
