using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModDownload;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Windows;

namespace SlopeX.Pages
{
    public partial class PageDownload : UserControl, ILauncherPage
    {
        private List<VersionSummary> _all = new List<VersionSummary>();
        private string _filter = "all";
        private bool _loaded;

        public PageDownload()
        {
            InitializeComponent();
        }

        public void OnShown()
        {
            SourceHint.Text = Config.Current.UseMirror ? "下载源：BMCLAPI 镜像" : "下载源：Mojang 官方";
            if (!_loaded) _ = LoadAsync();
            else
            {
                MarkInstalled();
                ApplyFilter();
            }
        }

        private async Task LoadAsync()
        {
            try
            {
                StatusText.Visibility = Visibility.Visible;
                StatusText.Text = "正在获取版本清单…";

                var list = await Mojang.GetVersionListAsync(true);
                _all = list;
                _loaded = true;
                MarkInstalled();
                ApplyFilter();

                SubTitle.Text = $"共 {_all.Count} 个可安装版本 · {Config.Current.GameDir}";
            }
            catch (Exception ex)
            {
                StatusText.Visibility = Visibility.Visible;
                StatusText.Text = "获取版本清单失败：" + ex.Message + "\n可在「设置」中切换下载源后重试。";
                SubTitle.Text = "版本清单获取失败";
                Log.Error("获取版本清单失败", ex);
            }
        }

        private void MarkInstalled()
        {
            try
            {
                var installed = new HashSet<string>(
                    VersionManager.Scan(Config.Current.GameDir).Select(v => v.Id),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var v in _all)
                {
                    v.Installed = installed.Contains(v.Id);
                    v.Busy = TaskCenter.IsBusy(v.Id);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("标记安装状态失败：" + ex.Message);
            }
        }

        private void ApplyFilter()
        {
            IEnumerable<VersionSummary> query = _all;

            switch (_filter)
            {
                case "release":
                    query = query.Where(v => v.Type == "release");
                    break;
                case "snapshot":
                    query = query.Where(v => v.Type == "snapshot");
                    break;
                case "old":
                    query = query.Where(v => (v.Type ?? "").StartsWith("old"));
                    break;
            }

            var search = (SearchBox.Text ?? "").Trim();
            if (search.Length > 0)
                query = query.Where(v => (v.Id ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                                         || (v.DateLabel ?? "").Contains(search));

            var list = query.ToList();
            VersionList.ItemsSource = list;

            if (list.Count == 0)
            {
                StatusText.Visibility = Visibility.Visible;
                StatusText.Text = _loaded ? "没有匹配的版本" : "正在获取版本清单…";
            }
            else if (_loaded)
            {
                StatusText.Visibility = Visibility.Collapsed;
            }
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is string id)) return;

            var summary = _all.FirstOrDefault(v => v.Id == id);
            if (summary == null || summary.Installed) return;

            if (!EnsureGameDir()) return;

            summary.Busy = true;
            var task = TaskCenter.StartInstall(id);
            MainWindow.Current?.SetStatus($"正在安装 {id}…", null);

            task.Updated += t =>
            {
                if (t.Running) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    summary.Busy = false;
                    summary.Installed = VanillaInstaller.IsInstalled(Config.Current.GameDir, id);
                    MainWindow.Current?.RefreshStatusAsync();

                    if (t.Completed)
                    {
                        Config.Current.LastVersion = id;
                        Config.Current.Save();
                        MainWindow.Current?.SetStatus($"已安装 {id}，可在「启动」页运行", null);
                    }
                    else if (t.Failed)
                    {
                        MainWindow.Current?.SetStatus($"安装 {id} 失败：{t.Error}", null);
                    }
                }));
            };

            ApplyFilter();
        }

        private bool EnsureGameDir()
        {
            var dir = Config.Current.GameDir;
            if (string.IsNullOrWhiteSpace(dir))
            {
                MessageBox.Show("游戏目录未设置，请先到「设置」中指定。", "SlopeX");
                return false;
            }

            try
            {
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法创建游戏目录：" + ex.Message, "SlopeX");
                return false;
            }
        }

        private void Chip_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is ToggleButton chip)) return;
            foreach (var c in new[] { ChipAll, ChipRelease, ChipSnapshot, ChipOld })
                c.IsChecked = ReferenceEquals(c, chip);

            _filter = chip.Tag?.ToString() ?? "all";
            ApplyFilter();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            _ = LoadAsync();
        }
    }
}
