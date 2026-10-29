using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SlopeX.Controls;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModDownload;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Windows;

namespace SlopeX.Pages
{
    public partial class PageDownload : UserControl, ILauncherPage
    {
        private List<VersionSummary> _all = new List<VersionSummary>();

        // 默认只看正式版：快照和远古版本是少数场景，别让它们占满首屏
        private string _filter = "release";
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
                ApplyFilter();   // 副标题由 ApplyFilter 统一更新
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
            UpdateSubTitle(list.Count);

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

        /// <summary>
        /// 副标题跟随筛选变化：显示"当前筛出多少 / 清单总共多少"，
        /// 避免只写总数时与实际列表对不上。
        /// </summary>
        private void UpdateSubTitle(int shown)
        {
            if (!_loaded) return;

            var total = _all.Count;
            var search = (SearchBox.Text ?? "").Trim();

            var head = search.Length > 0
                ? $"搜索「{search}」{shown} 个"
                : $"{FilterLabel(_filter)} {shown} 个";

            SubTitle.Text = $"{head}（清单共 {total} 个）· {Config.Current.GameDir}";
        }

        private static string FilterLabel(string filter) => filter switch
        {
            "release" => "正式版",
            "snapshot" => "快照",
            "old" => "远古版本",
            _ => "全部版本"
        };

        /// <summary>点「安装」先问类型（原版 / Fabric / Forge / NeoForge），再走对应流程。</summary>
        private void Install_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is string id)) return;

            var summary = _all.FirstOrDefault(v => v.Id == id);
            if (summary == null || summary.Busy) return;

            if (!EnsureGameDir()) return;

            var vanillaInstalled = VanillaInstaller.IsInstalled(Config.Current.GameDir, id);
            var dialog = new InstallChoiceDialog(id, vanillaInstalled);

            dialog.Closed += choice =>
            {
                MainWindow.Current?.HideModal();
                if (choice == null) return;

                switch (choice.Value)
                {
                    case InstallKindChoice.Fabric:
                        _ = StartFabricAsync(summary);
                        break;

                    case InstallKindChoice.Forge:
                        _ = StartForgeAsync(summary);
                        break;

                    case InstallKindChoice.NeoForge:
                        // 占位：选项已置灰，正常点不到；万一将来放开但安装器未实现，给出明确说明
                        MessageBox.Show("NeoForge 安装器尚未实装。\n\n当前可选：原版、Fabric、Forge。", "SlopeX");
                        break;

                    default:
                        StartVanilla(summary);
                        break;
                }
            };

            MainWindow.Current?.ShowModal(dialog);
            dialog.FocusInitial();
        }

        private void StartVanilla(VersionSummary summary)
        {
            var id = summary.Id;

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

        /// <summary>为某个原版版本安装 Fabric 加载器（缺失原版时由安装器先补原版）。</summary>
        /// <summary>
        /// 安装 Forge。查询推荐版本很快，真正的等待在 installer 里（几分钟、无细粒度进度），
        /// 所以这里只把状态栏切到"正在安装"，进度看「任务与日志」页。
        /// </summary>
        private async Task StartForgeAsync(VersionSummary summary)
        {
            var id = summary.Id;

            try
            {
                summary.Busy = true;
                ApplyFilter();
                MainWindow.Current?.SetStatus($"正在查询 {id} 的 Forge 版本…", null);

                var loader = await ForgeInstaller.GetRecommendedAsync(id);
                if (string.IsNullOrEmpty(loader))
                {
                    summary.Busy = false;
                    ApplyFilter();
                    MessageBox.Show($"Forge 目前没有为 {id} 提供安装包。\n\n" +
                                    "可在 https://files.minecraftforge.net 确认该版本的可用性。", "SlopeX");
                    return;
                }

                var task = TaskCenter.StartForgeInstall(id, loader);
                MainWindow.Current?.SetStatus(
                    $"正在安装 Forge {loader}（{id}）——安装器在后台运行，进度见「任务与日志」", null);

                task.Updated += t =>
                {
                    if (t.Running) return;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        summary.Busy = false;
                        MainWindow.Current?.RefreshStatusAsync();
                        ApplyFilter();

                        if (t.Completed)
                        {
                            Config.Current.LastVersion = t.VersionId;
                            Config.Current.Save();
                            MainWindow.Current?.SetStatus($"已安装 {t.VersionId}，可在「启动」页运行", null);
                        }
                        else if (t.Failed)
                        {
                            MainWindow.Current?.SetStatus($"Forge 安装失败：{t.Error}", null);
                        }
                    }));
                };
            }
            catch (Exception ex)
            {
                summary.Busy = false;
                ApplyFilter();
                MainWindow.Current?.SetStatus("Forge 安装失败：" + ex.Message, null);
                Log.Error("Forge 安装失败", ex);
            }
        }

        private async Task StartFabricAsync(VersionSummary summary)
        {
            var id = summary.Id;

            try
            {
                summary.Busy = true;
                ApplyFilter();
                MainWindow.Current?.SetStatus($"正在查询 {id} 的 Fabric 加载器…", null);

                var loader = await FabricInstaller.GetLatestStableAsync(id);
                if (string.IsNullOrEmpty(loader))
                {
                    summary.Busy = false;
                    ApplyFilter();
                    MessageBox.Show($"Fabric 目前没有为 {id} 提供加载器。", "SlopeX");
                    return;
                }

                var task = TaskCenter.StartFabricInstall(id, loader);
                MainWindow.Current?.SetStatus($"正在安装 Fabric {loader}（{id}）…", null);

                task.Updated += t =>
                {
                    if (t.Running) return;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        summary.Busy = false;
                        MainWindow.Current?.RefreshStatusAsync();
                        ApplyFilter();

                        if (t.Completed)
                        {
                            Config.Current.LastVersion = t.VersionId;
                            Config.Current.Save();
                            MainWindow.Current?.SetStatus($"已安装 {t.VersionId}，可在「启动」页运行", null);
                        }
                        else if (t.Failed)
                        {
                            MainWindow.Current?.SetStatus($"Fabric 安装失败：{t.Error}", null);
                        }
                    }));
                };
            }
            catch (Exception ex)
            {
                summary.Busy = false;
                ApplyFilter();
                MainWindow.Current?.SetStatus("Fabric 安装失败：" + ex.Message, null);
                Log.Error("Fabric 安装失败", ex);
            }
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
