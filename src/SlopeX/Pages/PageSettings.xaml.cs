using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using SlopeX.Controls;
using SlopeX.Modules.ModAccount;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModJava;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Themes;
using SlopeX.Windows;

namespace SlopeX.Pages
{
    public partial class PageSettings : UserControl, ILauncherPage
    {
        private bool _loading;

        /// <summary>
        /// 界面上当前选中的线程档位（8 / 16 / 32 / 64）。
        /// 单独存一份是因为 ToggleButton 会在 Click 之前先把自己的 IsChecked 翻过来，
        /// 想"取消后回到原来的档"就必须知道原来选的是哪个。
        /// </summary>
        private int _threads = 16;

        /// <summary>≥ 这个档位就要先弹警告：连接数足以压满家用路由器的 NAT 表。</summary>
        private const int WarnThreadThreshold = 32;

        public PageSettings()
        {
            InitializeComponent();
            LoadFromConfig();
        }

        public void OnShown()
        {
            LoadFromConfig();
            if (JavaList.Items.Count == 0) _ = DetectJavaAsync();
        }

        private void LoadFromConfig()
        {
            _loading = true;
            try
            {
                var c = Config.Current;
                InputName.Text = c.PlayerName;
                InputGameDir.Text = c.GameDir;
                InputJava.Text = c.JavaPath ?? "";
                InputMemory.Text = c.MemoryMb.ToString();
                InputJvm.Text = c.JvmArgs ?? "";
                InputWidth.Text = c.WindowWidth.ToString();
                InputHeight.Text = c.WindowHeight.ToString();
                ChkResolution.IsChecked = c.CustomResolution;
                ChkMinimize.IsChecked = c.CloseOnLaunch;
                ChkAutoJava.IsChecked = c.AutoInstallJava;
                UpdateIsolationInfo(c);

                DataDirText.Text = Paths.DataDir;
                InputRuntimeDir.Text = c.JavaRuntimeDir ?? "";
                RuntimeDirHint.Text = "当前生效：" + c.JavaRuntimeDirHint;

                ChipMirror.IsChecked = c.UseMirror;
                ChipOfficial.IsChecked = !c.UseMirror;

                UpdateThreadChips(c.DownloadThreads);
                _threads = SelectedThreads;

                UpdateThemeChips();

                UpdateAccountUi();

                ConfigPath.Text = Paths.ConfigFile;
                UpdateMemoryHint();
            }
            finally
            {
                _loading = false;
            }
        }

        private void UpdateMemoryHint()
        {
            if (int.TryParse(InputMemory.Text, out var mb))
                MemoryHint.Text = $"约 {mb / 1024.0:0.#} GB";
            else
                MemoryHint.Text = "";
        }

        /// <summary>
        /// 显示当前版本的存档路径。根目录若还有旧版"共用模式"留下的存档，只做提示，
        /// 不自动搬移、不自动共享——是否共用由用户自己复制文件决定。
        /// </summary>
        private void UpdateIsolationInfo(Config c)
        {
            var root = c.GameDir;

            if (string.IsNullOrWhiteSpace(c.LastVersion))
            {
                SavesPathText.Text = "尚未启动过任何版本；启动一次后会显示该版本的存档目录。";
            }
            else
            {
                try
                {
                    SavesPathText.Text = "当前版本（" + c.LastVersion + "）存档目录：" +
                                         VersionEnv.SavesDir(root, c.LastVersion);
                }
                catch (Exception ex)
                {
                    SavesPathText.Text = "存档目录解析失败：" + ex.Message;
                }
            }

            if (VersionEnv.HasLegacySharedSaves(root, out var legacy) && !c.DismissLegacySavesHint)
            {
                LegacySavesHint.Text =
                    $"根目录 {Path.Combine(root, "saves")} 仍有 {legacy} 个旧存档（旧版共用模式遗留），不会被自动共享。" +
                    "需要时请手动复制到目标版本的 saves 目录。";
                LegacySavesRow.Visibility = Visibility.Visible;
            }
            else
            {
                LegacySavesRow.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>旧存档提示是"一次性通知"，允许用户关掉，避免长期钉在页面上。</summary>
        private void DismissLegacySaves_Click(object sender, RoutedEventArgs e)
        {
            Config.Current.DismissLegacySavesHint = true;
            Config.Current.Save();
            LegacySavesRow.Visibility = Visibility.Collapsed;
        }

        // ---------------- 数据目录与运行时 ----------------

        private void OpenDataDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = Paths.DataDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("打开数据目录失败：" + ex.Message);
            }
        }

        private void BrowseRuntimeDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "选择 Java 运行时存放目录",
                    Multiselect = false
                };

                var current = (InputRuntimeDir.Text ?? "").Trim();
                if (string.IsNullOrEmpty(current)) current = Paths.RuntimeDir;
                if (Directory.Exists(current)) dialog.InitialDirectory = current;

                if (dialog.ShowDialog() == true) InputRuntimeDir.Text = dialog.FolderName;
            }
            catch (Exception ex)
            {
                Log.Warn("选择运行时目录失败：" + ex.Message);
            }
        }

        private void ResetRuntimeDir_Click(object sender, RoutedEventArgs e)
        {
            InputRuntimeDir.Text = "";
            RuntimeDirHint.Text = "当前生效：默认位置（%APPDATA%\\SlopeX\\runtime），点「保存设置」后生效";
        }

        // ---------------- 账号 ----------------

        private void UpdateAccountUi()
        {
            var session = Msa.Current;
            if (session != null)
            {
                AccountStatus.Text = "已登录：" + session.Name;
                AccountHint.Text = session.Expired
                    ? "令牌已过期，启动游戏时会自动续期，不用重新登录。"
                    : $"正版账号：皮肤与正版验证服务器可用。令牌有效至 {session.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm}，到期自动续期。";
                BtnLogin.Content = "重新登录";
                BtnLogout.Visibility = Visibility.Visible;
            }
            else
            {
                AccountStatus.Text = "未登录：使用离线账号";
                AccountHint.Text = "登录正版账号后，皮肤与正版验证服务器可用。";
                BtnLogin.Content = "登录微软账号";
                BtnLogout.Visibility = Visibility.Collapsed;
            }
        }

        private void Login_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new LoginDialog();

            dialog.Finished += session =>
            {
                MainWindow.Current?.HideModal();

                if (session == null)
                {
                    UpdateAccountUi();
                    return;
                }

                UpdateAccountUi();
                MainWindow.Current?.ApplyConfigChanged();
                MainWindow.Current?.SetStatus($"已登录：{session.Name}", null);
                Log.Info($"账号已切换为正版：{session.Name}");
            };

            MainWindow.Current?.ShowModal(dialog);
            dialog.Start();
        }

        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show("退出后将以离线账号启动，确定吗？", "退出登录",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;

            Msa.Logout();
            UpdateAccountUi();
            MainWindow.Current?.ApplyConfigChanged();
            MainWindow.Current?.SetStatus("已退出正版账号", null);
        }

        // ---------------- 主题 ----------------

        private void ThemeChip_Click(object sender, RoutedEventArgs e)
        {
            var mode = ReferenceEquals(sender, ChipThemeVice) ? AppearanceMode.ViceCity
                     : ReferenceEquals(sender, ChipThemeRetro) ? AppearanceMode.RetroFuture
                     : ReferenceEquals(sender, ChipThemeDark) ? AppearanceMode.Dark
                     : ReferenceEquals(sender, ChipThemeSystem) ? AppearanceMode.System
                     : AppearanceMode.Light;

            Config.Current.Appearance = ThemeService.ToConfigString(mode);
            Config.Current.Save();
            ThemeService.Apply(mode, force: true);
            UpdateThemeChips();

            var applied = ThemeName(ThemeService.CurrentMode);
            MainWindow.Current?.SetStatus("主题已切换：" + applied, null);
            MainWindow.Current?.ShowToast(applied + " 已应用");
            Log.Info($"主题切换为 {Config.Current.Appearance}（{(ThemeService.IsDark ? "深色" : "浅色")}）");
        }

        private static string ThemeName(AppearanceMode mode) => mode switch
        {
            AppearanceMode.RetroFuture => "复古未来",
            AppearanceMode.ViceCity => "ViceCity",
            AppearanceMode.Dark => "极简墨纸",
            _ => "极简白纸"
        };

        /// <summary>三选一互斥 + 说明文字。</summary>
        private void UpdateThemeChips()
        {
            var mode = ThemeService.ParseMode(Config.Current.Appearance);

            ChipThemeSystem.IsChecked = mode == AppearanceMode.System;
            ChipThemeRetro.IsChecked = mode == AppearanceMode.RetroFuture;
            ChipThemeVice.IsChecked = mode == AppearanceMode.ViceCity;
            ChipThemeLight.IsChecked = mode == AppearanceMode.Light;
            ChipThemeDark.IsChecked = mode == AppearanceMode.Dark;

            ThemeHint.Text = mode switch
            {
                AppearanceMode.System =>
                    "跟随系统：Windows 当前为" + (ThemeService.IsSystemDark() ? "深色" : "浅色") +
                    " → 使用「" + ThemeName(ThemeService.CurrentMode) + "」",
                AppearanceMode.Light => "固定浅色：使用「极简白纸」（默认）",
                AppearanceMode.Dark => "固定深色：使用「极简墨纸」",
                AppearanceMode.RetroFuture => "插画主题：「复古未来」——合成波暮色位图，卡片半透明",
                AppearanceMode.ViceCity => "插画主题：「ViceCity」——迈阿密暮色位图，卡片近乎不透明",
                _ => "固定浅色：使用「极简白纸」（默认）"
            };
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var c = Config.Current;

                var name = (InputName.Text ?? "").Trim();
                if (string.IsNullOrEmpty(name)) name = "Steve";
                c.PlayerName = name;

                var dir = (InputGameDir.Text ?? "").Trim();
                if (!string.IsNullOrEmpty(dir)) c.GameDir = dir;

                c.JavaPath = (InputJava.Text ?? "").Trim();
                c.JvmArgs = (InputJvm.Text ?? "").Trim();

                if (int.TryParse(InputMemory.Text, out var mb))
                    c.MemoryMb = Math.Max(512, Math.Min(65536, mb));
                InputMemory.Text = c.MemoryMb.ToString();

                if (int.TryParse(InputWidth.Text, out var w)) c.WindowWidth = Math.Max(320, Math.Min(7680, w));
                if (int.TryParse(InputHeight.Text, out var h)) c.WindowHeight = Math.Max(240, Math.Min(4320, h));
                InputWidth.Text = c.WindowWidth.ToString();
                InputHeight.Text = c.WindowHeight.ToString();

                c.CustomResolution = ChkResolution.IsChecked == true;
                c.CloseOnLaunch = ChkMinimize.IsChecked == true;
                c.AutoInstallJava = ChkAutoJava.IsChecked == true;
                c.UseMirror = ChipMirror.IsChecked == true;
                c.DownloadThreads = SelectedThreads;

                c.JavaRuntimeDir = (InputRuntimeDir.Text ?? "").Trim();

                c.Save();
                RuntimeDirHint.Text = "当前生效：" + c.JavaRuntimeDirHint;
                MainWindow.Current?.ApplyConfigChanged();
                MainWindow.Current?.SetStatus("设置已保存", null);
                MainWindow.Current?.ShowToast("设置已保存");
                Log.Info("设置已保存");
            }
            catch (Exception ex)
            {
                Log.Error("保存设置失败", ex);
                MessageBox.Show("保存失败：" + ex.Message, "SlopeX");
            }
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show("确定恢复默认设置吗？", "SlopeX", MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;

            Config.Current = new Config();
            Config.Current.Save();
            ThemeService.Apply(AppearanceMode.Light, force: true);   // 默认回到「极简白纸」
            LoadFromConfig();
            MainWindow.Current?.ApplyConfigChanged();
        }

        /// <summary>
        /// 打开该版本自己的存档目录。<b>只做导航，不复制任何文件</b>——
        /// 跨版本共用存档由用户自行复制，启动器不提供共享通道。
        /// </summary>
        private void OpenSaves_Click(object sender, RoutedEventArgs e)
        {
            var c = Config.Current;
            if (string.IsNullOrWhiteSpace(c.LastVersion))
            {
                MessageBox.Show("还没有启动过任何版本，先启动一次游戏。", "SlopeX");
                return;
            }

            try
            {
                var dir = VersionEnv.SavesDir(c.GameDir, c.LastVersion);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("打开存档目录失败：" + ex.Message);
                MessageBox.Show("打开存档目录失败：" + ex.Message, "SlopeX");
            }
        }

        // ---------------- 浏览 ----------------

        private void BrowseGameDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "选择游戏目录（.minecraft）",
                    Multiselect = false
                };

                var current = (InputGameDir.Text ?? "").Trim();
                if (Directory.Exists(current)) dialog.InitialDirectory = current;

                if (dialog.ShowDialog() == true) InputGameDir.Text = dialog.FolderName;
            }
            catch (Exception ex)
            {
                Log.Warn("选择目录失败：" + ex.Message);
            }
        }

        private void BrowseJava_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "选择 java.exe",
                    Filter = "Java 可执行文件|java.exe;javaw.exe|所有文件|*.*",
                    CheckFileExists = true
                };

                if (dialog.ShowDialog() == true) InputJava.Text = dialog.FileName;
            }
            catch (Exception ex)
            {
                Log.Warn("选择 Java 失败：" + ex.Message);
            }
        }

        private async Task DetectJavaAsync()
        {
            JavaList.ItemsSource = null;
            var list = await Task.Run(() => JavaFinder.All(true));
            JavaList.ItemsSource = list;

            if (list.Count == 0)
                Log.Warn("未检测到任何 Java 运行时");
        }

        private async void DetectJava_Click(object sender, RoutedEventArgs e)
        {
            await DetectJavaAsync();
        }

        private void JavaList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (JavaList.SelectedItem is JavaInfo java)
                InputJava.Text = java.Path;
        }

        // ---------------- 内存 ----------------

        private void MemoryMinus_Click(object sender, RoutedEventArgs e) => StepMemory(-512);
        private void MemoryPlus_Click(object sender, RoutedEventArgs e) => StepMemory(512);

        private void StepMemory(int delta)
        {
            if (!int.TryParse(InputMemory.Text, out var mb)) mb = Config.Current.MemoryMb;
            mb = Math.Max(512, Math.Min(65536, mb + delta));
            InputMemory.Text = mb.ToString();
            UpdateMemoryHint();
        }

        // ---------------- 下载源 ----------------

        private void Source_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (sender is ToggleButton chip)
            {
                ChipMirror.IsChecked = ReferenceEquals(chip, ChipMirror);
                ChipOfficial.IsChecked = ReferenceEquals(chip, ChipOfficial);
            }
        }

        // ---------------- 下载线程 ----------------

        /// <summary>
        /// 配置里存的是 1–64 的任意值，界面上只有 4 个档；落在档与档之间时高亮最近的那一档。
        /// </summary>
        private void UpdateThreadChips(int threads)
        {
            ChipThread8.IsChecked = threads <= 12;
            ChipThread16.IsChecked = threads > 12 && threads <= 24;
            ChipThread32.IsChecked = threads > 24 && threads <= 48;
            ChipThread64.IsChecked = threads > 48;
        }

        private void Thread_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (!(sender is ToggleButton chip)) return;

            var target = ThreadsOf(chip);

            // 点的是已经选中的那一档：ToggleButton 已经把它反选掉了，按原值重画即可
            if (target == _threads)
            {
                UpdateThreadChips(_threads);
                return;
            }

            // 32 / 64 的连接数足以压满家用路由器的 NAT 表：先讲清代价，确认后才生效。
            // 8 / 16 在安全区内，直接切换，不打断操作。
            if (target >= WarnThreadThreshold)
            {
                ShowThreadWarning(target);
                return;
            }

            _threads = target;
            UpdateThreadChips(_threads);
        }

        /// <summary>
        /// 高并发档位的风险提示。取消时把界面退回原来的档，
        /// 避免出现"看起来选了 64、实际保存下去还是 16"的错觉。
        /// </summary>
        private void ShowThreadWarning(int target)
        {
            // ToggleButton 在 Click 之前就把自己点亮了，先按原值重画：
            // 确认之前界面保持旧档，不会出现"新旧两档同时高亮"。
            UpdateThreadChips(_threads);

            var isMax = target >= 64;

            string[] risks = isMax
                ? new[]
                {
                    "老旧或低端路由器可能出现整屋断网，要重启路由器才能恢复",
                    "Wi-Fi 下容易占满无线信道，同网络其它设备明显卡顿",
                    "部分 CDN 限制单 IP 并发，分段请求被拒后会退回单连接，反而更慢"
                }
                : new[]
                {
                    "路由器和光猫的连接数压力明显上升，访问其它网站可能变慢",
                    "同网络看视频、打游戏时更容易卡顿或跳 ping",
                    "个别网络会触发运营商限频，实际速度不一定比 16 更快"
                };

            var dialog = new ConfirmDialog(
                title: isMax ? "确定要开 64 条连接？" : "确定要开 32 条连接？",
                message: isMax
                    ? "下载单个文件时最多同时开 64 条连接，这是本启动器的上限，也是 PCL2 的默认值。速度通常还能再涨一截，但对路由器的要求也最高。"
                    : "下载单个文件时最多同时开 32 条连接。多数家用网络没问题，但路由器 NAT 表偏小的型号可能会出状况。",
                confirmText: isMax ? "仍然使用 64" : "确认使用 32",
                risks: risks,
                footnote: isMax
                    ? "如果出现断网或下载反复重试，先降回 32，再不行就回 16。"
                    : "出问题时降回 16 即可，从下一次下载开始恢复。");

            dialog.Closed += ok =>
            {
                MainWindow.Current?.HideModal();

                if (!ok)
                {
                    UpdateThreadChips(_threads);
                    return;
                }

                _threads = target;
                UpdateThreadChips(_threads);
                MainWindow.Current?.ShowToast($"{target} 线程已选中，点「保存设置」后生效");
            };

            MainWindow.Current?.ShowModal(dialog);
            dialog.FocusInitial();
        }

        /// <summary>档位数字就直接写在 Chip 的 Content 上，不必再为每个 chip 写一遍比较。</summary>
        private static int ThreadsOf(ToggleButton chip) =>
            int.TryParse(chip?.Content?.ToString(), out var n) ? n : 16;

        private int SelectedThreads =>
            ChipThread8.IsChecked == true ? 8
            : ChipThread32.IsChecked == true ? 32
            : ChipThread64.IsChecked == true ? 64
            : 16;

        private void OpenConfigDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Paths.AppDir + "\"")
                    { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("打开配置目录失败：" + ex.Message);
            }
        }

        private void OpenLegal_Click(object sender, RoutedEventArgs e)
            => MainWindow.Current?.ShowModal(new Controls.LegalNoticeDialog());
    }
}
