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
                JavaRuntimeHint.Text = "托管运行时目录：" + Paths.RuntimeDir;
                UpdateIsolationInfo(c);

                ChipMirror.IsChecked = c.UseMirror;
                ChipOfficial.IsChecked = !c.UseMirror;

                UpdateThemeChips();

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

            if (VersionEnv.HasLegacySharedSaves(root, out var legacy))
            {
                LegacySavesHint.Text =
                    $"注意：根目录 {Path.Combine(root, "saves")} 仍有 {legacy} 个旧存档（旧版共用模式遗留）。" +
                    "它们不会自动共享给任何版本；需要的话请手动复制到目标版本的 saves 目录。";
                LegacySavesHint.Visibility = Visibility.Visible;
            }
            else
            {
                LegacySavesHint.Visibility = Visibility.Collapsed;
            }
        }

        // ---------------- 主题 ----------------

        private void ThemeChip_Click(object sender, RoutedEventArgs e)
        {
            var mode = ReferenceEquals(sender, ChipThemeDark) ? AppearanceMode.Dark
                     : ReferenceEquals(sender, ChipThemeSystem) ? AppearanceMode.System
                     : AppearanceMode.Light;

            Config.Current.Appearance = ThemeService.ToConfigString(mode);
            Config.Current.Save();
            ThemeService.Apply(mode, force: true);
            UpdateThemeChips();

            MainWindow.Current?.SetStatus("主题已切换：" + ThemeName(ThemeService.IsDark), null);
            Log.Info($"主题切换为 {Config.Current.Appearance}（{(ThemeService.IsDark ? "深色" : "浅色")}）");
        }

        private static string ThemeName(bool dark) => dark ? "极简墨纸" : "极简白纸";

        /// <summary>三选一互斥 + 说明文字。</summary>
        private void UpdateThemeChips()
        {
            var mode = ThemeService.ParseMode(Config.Current.Appearance);

            ChipThemeSystem.IsChecked = mode == AppearanceMode.System;
            ChipThemeLight.IsChecked = mode == AppearanceMode.Light;
            ChipThemeDark.IsChecked = mode == AppearanceMode.Dark;

            ThemeHint.Text = mode switch
            {
                AppearanceMode.System =>
                    "跟随系统：Windows 当前为" + (ThemeService.IsSystemDark() ? "深色" : "浅色") +
                    " → 使用「" + ThemeName(ThemeService.IsDark) + "」",
                AppearanceMode.Dark => "固定深色：使用「极简墨纸」",
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

                c.Save();
                MainWindow.Current?.ApplyConfigChanged();
                MainWindow.Current?.SetStatus("设置已保存", null);
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
    }
}
