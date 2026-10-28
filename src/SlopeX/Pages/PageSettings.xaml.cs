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
                ChkIsolation.IsChecked = c.VersionIsolation;
                JavaRuntimeHint.Text = "托管运行时目录：" + Paths.RuntimeDir;

                ChipMirror.IsChecked = c.UseMirror;
                ChipOfficial.IsChecked = !c.UseMirror;

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

                var isolationChanged = c.VersionIsolation != (ChkIsolation.IsChecked == true);
                c.VersionIsolation = ChkIsolation.IsChecked == true;
                if (isolationChanged)
                    Log.Info($"版本隔离已{(c.VersionIsolation ? "启用" : "关闭")}");

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
            LoadFromConfig();
            MainWindow.Current?.ApplyConfigChanged();
        }

        /// <summary>把根目录里的存档 / 材质包 / 模组复制到当前（最近启动的）版本的隔离目录。</summary>
        private async void ImportShared_Click(object sender, RoutedEventArgs e)
        {
            var c = Config.Current;
            var id = c.LastVersion;

            if (string.IsNullOrWhiteSpace(id))
            {
                MessageBox.Show("还没有启动过任何版本，先启动一次游戏再回来导入。", "SlopeX");
                return;
            }

            var target = VersionEnv.Resolve(c.GameDir, id, true);
            var r = MessageBox.Show(
                $"把游戏根目录里的 mods / 资源包 / 存档 / 设置复制到版本「{id}」：\n{target}\n\n已存在的文件不会被覆盖。",
                "导入到当前版本", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;

            try
            {
                var result = await Task.Run(() => VersionEnv.CopyShared(c.GameDir, target));
                var msg = result.Files > 0
                    ? $"已复制 {result.Files} 个文件（{VersionEnv.FormatSize(result.Bytes)}）到版本「{id}」。"
                    : "没有需要复制的内容（目标已有同名文件，或公共目录为空）。";
                Log.Info($"导入公共目录内容到 {id}：{result.Files} 个文件");
                MessageBox.Show(msg, "SlopeX");
            }
            catch (Exception ex)
            {
                Log.Error("导入公共目录内容失败", ex);
                MessageBox.Show("导入失败：" + ex.Message, "SlopeX");
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
