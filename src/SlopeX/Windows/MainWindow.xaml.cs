using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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

            // 霓虹主题才有插画背景；主题切换时跟着显隐
            Themes.ThemeService.Changed += (s2, e2) => ApplyBackdrop();
            ApplyBackdrop();

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
            var session = SlopeX.Modules.ModAccount.Msa.Current;

            var name = session?.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = string.IsNullOrWhiteSpace(Config.Current.PlayerName) ? "Steve" : Config.Current.PlayerName;

            SideAccount.Text = name;
            SideAvatar.Text = name.Substring(0, 1).ToUpperInvariant();
            SideAccountType.Text = session != null ? "正版登录" : "离线登录";
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

        /// <summary>只有霓虹黄昏需要矢量背景，其余主题保持纯色纸面。</summary>
        private void ApplyBackdrop()
        {
            Backdrop.Visibility = Themes.ThemeService.IsNeon ? Visibility.Visible : Visibility.Collapsed;
            ViceBackdrop.Visibility = Themes.ThemeService.IsViceCity ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------- 模态 ----------------

        /// <summary>
        /// 每次 Show/Hide 递增，用于判断淡出结束时模态是否已经被重新打开。
        /// 没有这个序号，"快速关掉又打开"会因为旧动画的 Completed 回调把新模态收起来。
        /// </summary>
        private int _modalEpoch;

        /// <summary>系统设置里关掉"显示动画"时，模态直接出现，不做过渡。</summary>
        private static bool MotionEnabled => SystemParameters.ClientAreaAnimation;

        /// <summary>在主窗口内显示模态卡片（遮罩挡住下层点击，Esc / 取消由内容自己处理）。</summary>
        public void ShowModal(UIElement content)
        {
            _modalEpoch++;

            ModalHost.Content = content;
            ModalLayer.Visibility = Visibility.Visible;

            if (!MotionEnabled)
            {
                ModalScrim.Opacity = 1;
                ModalCard.Opacity = 1;
                ModalCardShift.Y = 0;
                return;
            }

            // 遮罩慢一点淡入，卡片更快，视觉上"先压暗再浮起"
            Animate(ModalScrim, UIElement.OpacityProperty, 0, 1, 160);
            Animate(ModalCard, UIElement.OpacityProperty, 0, 1, 130);
            Animate(ModalCardShift, TranslateTransform.YProperty, 10, 0, 180);
            Animate(ModalCardScale, ScaleTransform.ScaleXProperty, 0.985, 1, 180);
        }

        public void HideModal()
        {
            if (ModalLayer.Visibility != Visibility.Visible) return;

            var epoch = ++_modalEpoch;

            void Collapse()
            {
                // 淡出期间又打开了新模态 → 这次收起作废
                if (epoch != _modalEpoch) return;

                ModalLayer.Visibility = Visibility.Collapsed;
                ModalHost.Content = null;
                ModalScrim.Opacity = 0;
                ModalCard.Opacity = 0;
                ModalCardShift.Y = 10;
            }

            if (!MotionEnabled)
            {
                Collapse();
                return;
            }

            // 退出比进入快：退出是"确认完成"，拖沓会显得迟钝
            Animate(ModalScrim, UIElement.OpacityProperty, null, 0, 120);
            Animate(ModalCard, UIElement.OpacityProperty, null, 0, 110, Collapse);
            Animate(ModalCardShift, TranslateTransform.YProperty, null, 6, 110);
            Animate(ModalCardScale, ScaleTransform.ScaleXProperty, null, 0.99, 110);
        }

        public bool IsModalOpen => ModalLayer.Visibility == Visibility.Visible;

        private static void Animate(IAnimatable target, DependencyProperty prop, double? from, double to,
            int ms, Action onDone = null)
        {
            var anim = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            if (from.HasValue) anim.From = from;

            if (onDone != null)
                anim.Completed += (s, e) => onDone();

            target.BeginAnimation(prop, anim);
        }

        // ---------------- Toast ----------------

        private DispatcherTimer _toastTimer;

        /// <summary>
        /// 非阻断提示：底部居中出现，1.6 秒后淡出。
        /// 用于"已保存 / 已复制 / 已导入"这类不需要用户操作、也不该弹框的反馈。
        /// </summary>
        public void ShowToast(string text, int milliseconds = 1600)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            ToastLabel.Text = text;
            ToastLayer.Visibility = Visibility.Visible;

            // 重入：前一条还没消失时直接替换文字并重置计时，避免两条叠在一起
            _toastTimer?.Stop();

            AnimateToast(1.0, 120);

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer?.Stop();
                AnimateToast(0.0, 220, onDone: () => ToastLayer.Visibility = Visibility.Collapsed);
            };
            _toastTimer.Start();
        }

        private void AnimateToast(double to, int ms, Action onDone = null) =>
            Animate(ToastLayer, OpacityProperty, null, to, ms, onDone);
    }
}
