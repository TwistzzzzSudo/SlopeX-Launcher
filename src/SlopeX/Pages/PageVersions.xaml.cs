using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModDownload;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Windows;

namespace SlopeX.Pages
{
    public partial class PageVersions : UserControl, ILauncherPage
    {
        private List<InstalledVersion> _all = new List<InstalledVersion>();

        public PageVersions()
        {
            InitializeComponent();
            VersionList.MouseDoubleClick += (s, e) => LaunchSelected();
        }

        public void OnShown() => Reload();

        private void Reload()
        {
            try
            {
                _all = VersionManager.Scan(Config.Current.GameDir);
                ApplyFilter();
                SubTitle.Text = $"共 {_all.Count} 个版本 · {Config.Current.GameDir}";
            }
            catch (Exception ex)
            {
                Log.Error("扫描版本失败", ex);
                SubTitle.Text = "扫描失败：" + ex.Message;
            }
        }

        private void ApplyFilter()
        {
            var q = (SearchBox.Text ?? "").Trim();

            List<InstalledVersion> list;
            if (string.IsNullOrEmpty(q))
                list = _all;
            else
                list = _all.Where(v =>
                    (v.Id ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (v.BaseVersion ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (v.Loader ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            VersionList.ItemsSource = list;

            if (list.Count == 0)
            {
                EmptyText.Visibility = Visibility.Visible;
                EmptyText.Text = _all.Count == 0
                    ? "还没有安装任何版本\n点右上角「安装新版本」开始"
                    : "没有匹配的版本";
            }
            else
            {
                EmptyText.Visibility = Visibility.Collapsed;
            }
        }

        private void LaunchSelected()
        {
            if (VersionList.SelectedItem is InstalledVersion v) StartVersion(v.Id);
        }

        private void StartVersion(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            Config.Current.LastVersion = id;
            Config.Current.Save();
            MainWindow.Current?.Navigate(0);
        }

        private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private void LaunchOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string id) StartVersion(id);
            e.Handled = true;
        }

        private void OpenOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string dir) OpenFolder(dir);
            e.Handled = true;
        }

        /// <summary>
        /// 导入整合包：先只读解析给出摘要（格式 / 版本 / 文件数），确认后再走安装任务。
        /// 解析失败直接报错，不会留下半个版本目录。
        /// </summary>
        private void ImportPack_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择整合包",
                Filter = "整合包 (*.mrpack;*.zip)|*.mrpack;*.zip|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };

            if (dlg.ShowDialog() != true) return;

            PackPlan plan;
            try
            {
                plan = ModpackInstaller.Inspect(dlg.FileName);
            }
            catch (Exception ex)
            {
                Log.Error("识别整合包失败", ex);
                MessageBox.Show("这个文件不像整合包：\n\n" + ex.Message, "SlopeX");
                return;
            }

            var loader = string.IsNullOrEmpty(plan.LoaderKind)
                ? "原版"
                : plan.LoaderKind + " " + plan.LoaderVersion;

            var summary =
                $"格式：{plan.FormatLabel}\n" +
                $"名称：{plan.Name} {plan.PackVersion}\n".TrimEnd() + "\n" +
                $"Minecraft：{plan.McVersion}\n" +
                $"加载器：{loader}\n" +
                $"文件：{plan.Files.Count} 个\n" +
                $"版本 ID：{plan.SuggestVersionId()}\n";

            if (plan.Warnings.Count > 0)
                summary += "\n提示：\n· " + string.Join("\n· ", plan.Warnings) + "\n";

            summary += "\n开始导入？原版与加载器缺失时会先自动安装。";

            if (MessageBox.Show(summary, "导入整合包", MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            var task = TaskCenter.StartModpackInstall(dlg.FileName);
            MainWindow.Current?.SetStatus($"正在导入 {plan.Name}…（进度见「任务与日志」）", null);

            task.Updated += t =>
            {
                if (t.Running) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Reload();
                    MainWindow.Current?.RefreshStatusAsync();

                    if (t.Completed)
                    {
                        Config.Current.LastVersion = t.VersionId;
                        Config.Current.Save();
                        MainWindow.Current?.SetStatus($"整合包已导入：{t.VersionId}", null);
                    }
                    else if (t.Failed)
                    {
                        MainWindow.Current?.SetStatus($"整合包导入失败：{t.Error}", null);
                    }
                }));
            };
        }

        private void DeleteOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string id)
            {
                var dir = Path.Combine(Paths.VersionsDir(Config.Current.GameDir), id);

                // 安全边界：只允许删除 versions 目录下的子目录
                var root = Path.GetFullPath(Paths.VersionsDir(Config.Current.GameDir))
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(dir);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("该版本目录不在游戏目录内，已取消删除。", "SlopeX");
                    return;
                }

                var result = MessageBox.Show(
                    $"确定删除版本「{id}」吗？\n\n将移除目录：{dir}\n" +
                    "该目录同时包含此版本的存档、材质包与模组，会一并删除。\n" +
                    "此操作不可撤销。",
                    "删除版本", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (result != MessageBoxResult.OK) return;

                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    Log.Info($"已删除版本 {id}");
                    Reload();
                    MainWindow.Current?.RefreshStatusAsync();
                }
                catch (Exception ex)
                {
                    Log.Error("删除版本失败", ex);
                    MessageBox.Show("删除失败：" + ex.Message + "\n游戏可能正在运行。", "SlopeX");
                }
            }
            e.Handled = true;
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Reload();
            MainWindow.Current?.RefreshStatusAsync();
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Config.Current.GameDir);

        private void GoDownload_Click(object sender, RoutedEventArgs e) => MainWindow.Current?.Navigate(2);

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private static void OpenFolder(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("打开目录失败：" + ex.Message);
            }
        }
    }
}
