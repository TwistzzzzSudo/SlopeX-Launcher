using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModMods;
using SlopeX.Windows;

namespace SlopeX.Controls
{
    /// <summary>
    /// 模组管理面板：挂在版本管理页上，覆盖在版本列表之上。
    /// 数据与操作全部走 <see cref="ModManager"/>，这里只管展示与确认。
    /// </summary>
    public partial class ModsPanel : UserControl
    {
        private string _versionId = "";
        private List<ModEntry> _mods = new List<ModEntry>();

        public ModsPanel()
        {
            InitializeComponent();
        }

        /// <summary>用户点了返回。XAML 里挂接，所以必须是 EventHandler 签名。</summary>
        public event EventHandler BackRequested;

        /// <summary>载入某个版本的模组列表。</summary>
        public void Load(string versionId)
        {
            _versionId = versionId;
            TitleText.Text = "模组管理";
            SubText.Text = versionId;
            Reload();
        }

        private void Reload()
        {
            var gameDir = Config.Current.GameDir;
            _mods = ModManager.List(gameDir, _versionId);

            ModList.ItemsSource = _mods;
            EmptyPanel.Visibility = _mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var enabled = _mods.Count(m => m.Enabled);
            SubText.Text = $"{_versionId}  ·  {_mods.Count} 个模组，{enabled} 个启用";
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OpenDir_Click(object sender, RoutedEventArgs e)
        {
            var dir = ModManager.ModsDir(Config.Current.GameDir, _versionId);
            Directory.CreateDirectory(dir); // 打开前确保存在，用户能直接把 jar 拖进去

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
                {
                    UseShellExecute = true
                });
                MainWindow.Current?.ShowToast("已在资源管理器中打开 mods 目录");
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开目录失败：" + ex.Message, "SlopeX");
            }
        }

        private void AddMod_Click(object sender, RoutedEventArgs e)
        {
            var (mc, loader) = ModInstaller.ResolveTarget(Config.Current.GameDir, _versionId);

            var dialog = new ModSearchDialog(_versionId, mc, loader);
            dialog.Installed += (s2, e2) => Reload();   // 装完直接刷新列表

            MainWindow.Current?.ShowModal(dialog);
            dialog.FocusQuery();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

        /// <summary>
        /// 读每个 jar 里声明的依赖，对已装清单做一次体检。
        /// 只报确定的：缺前置、版本不满足区间、声明的不兼容、同 modid 多份、包读不出来。
        /// </summary>
        private void CheckHealth_Click(object sender, RoutedEventArgs e)
        {
            var dir = ModManager.ModsDir(Config.Current.GameDir, _versionId);

            try
            {
                var report = ModHealth.Check(dir);

                if (report.Mods.Count == 0)
                {
                    MainWindow.Current?.ShowToast("mods 目录是空的，没什么可检查的");
                    return;
                }

                if (report.Clean)
                {
                    SubText.Text = $"{_versionId}  ·  {report.OkCount} 个模组，依赖完整";
                    MainWindow.Current?.ShowToast($"{report.OkCount} 个模组，没有发现问题");
                    return;
                }

                SubText.Text = $"{_versionId}  ·  {report.Summary()}";

                // 逐条写日志：界面只给摘要，细节要能查到
                foreach (var i in report.Issues) Log.Warn($"体检（{i.Kind}）：{i.Message}");

                var first = report.Issues.First().Message;
                MainWindow.Current?.ShowToast($"{report.Summary()}；例如：{first}（详见日志）");
            }
            catch (Exception ex)
            {
                Log.Warn("体检失败：" + ex.Message);
                MainWindow.Current?.ShowToast("检查失败：" + ex.Message);
            }
        }

        /// <summary>更新检查 / 执行期间置位，避免重复点击叠出两次检查。</summary>
        private bool _checkingUpdates;

        /// <summary>
        /// 检查更新：算每个 jar 的 SHA1 去问 Modrinth，确认后逐个下载替换。
        /// 只覆盖 Modrinth 上架的模组；CurseForge 来的会安静跳过（那边要先实现它的指纹算法）。
        /// </summary>
        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_checkingUpdates) return;

            var dir = ModManager.ModsDir(Config.Current.GameDir, _versionId);
            var (mc, loader) = ModInstaller.ResolveTarget(Config.Current.GameDir, _versionId);

            _checkingUpdates = true;
            var before = SubText.Text;

            try
            {
                SubText.Text = $"{_versionId}  ·  正在检查更新…";

                // 算 SHA1 是同步 IO，放到线程池，别让界面卡住
                var updates = await Task.Run(() => ModUpdater.CheckAsync(dir, mc, loader));

                if (updates.Count == 0)
                {
                    SubText.Text = $"{_versionId}  ·  {_mods.Count} 个模组，已是最新";
                    MainWindow.Current?.ShowToast("没有可更新的模组（或这些模组不在 Modrinth 上架）");
                    _checkingUpdates = false;
                    return;
                }

                SubText.Text = before;

                var risks = updates.Take(5)
                    .Select(u => $"{u.FileName}：{u.CurrentVersion} → {u.LatestVersion}")
                    .ToArray();

                var dialog = new ConfirmDialog(
                    title: $"发现 {updates.Count} 个可更新的模组",
                    message: "确认后逐个下载新版本并替换旧文件。下载完成且校验通过才会删除旧文件，中途失败会保留原文件。",
                    confirmText: $"更新 {updates.Count} 个",
                    risks: risks,
                    footnote: updates.Count > 5 ? $"上面只列了前 5 个，共 {updates.Count} 个。" : null);

                dialog.Closed += async ok =>
                {
                    // ConfirmDialog 只回结果，不负责关模态层（与页设置里的用法一致）
                    MainWindow.Current?.HideModal();

                    if (!ok)
                    {
                        _checkingUpdates = false;
                        return;
                    }

                    await ApplyAllAsync(updates, dir);
                };

                MainWindow.Current?.ShowModal(dialog);
            }
            catch (Exception ex)
            {
                SubText.Text = before;
                _checkingUpdates = false;
                Log.Warn("检查更新失败：" + ex.Message);
                MainWindow.Current?.ShowToast("检查更新失败：" + ex.Message);
            }
        }

        private async Task ApplyAllAsync(List<ModUpdate> updates, string dir)
        {
            var done = 0;
            var failed = 0;

            try
            {
                foreach (var u in updates)
                {
                    SubText.Text = $"{_versionId}  ·  更新 {done + failed + 1}/{updates.Count}  {u.FileName}";

                    try
                    {
                        await ModUpdater.ApplyAsync(u, dir);
                        done++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Log.Warn($"更新 {u.FileName} 失败：{ex.Message}");
                    }
                }

                Reload();

                SubText.Text = failed == 0
                    ? $"{_versionId}  ·  已更新 {done} 个模组"
                    : $"{_versionId}  ·  更新 {done} 个，{failed} 个失败";

                MainWindow.Current?.ShowToast(failed == 0
                    ? $"已更新 {done} 个模组"
                    : $"更新 {done} 个，{failed} 个失败（详见日志）");
            }
            finally
            {
                _checkingUpdates = false;
            }
        }

        private void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not ModEntry mod) return;

            try
            {
                if (mod.Enabled) ModManager.Disable(mod.FullPath);
                else ModManager.Enable(mod.FullPath);

                Reload();
                MainWindow.Current?.ShowToast(ModManager.ToggleResultText(mod.Enabled, mod.Title));
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "SlopeX");
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not ModEntry mod) return;

            var result = MessageBox.Show(
                $"确定删除「{mod.Title}」吗？\n\n文件：{mod.FileName}\n此操作不可恢复。",
                "SlopeX", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (result != MessageBoxResult.OK) return;

            try
            {
                ModManager.Delete(mod.FullPath);
                Reload();
                MainWindow.Current?.ShowToast($"已删除 {mod.Title}");
            }
            catch (Exception ex)
            {
                MessageBox.Show("删除失败：" + ex.Message +
                                "\n如果游戏正在运行，请先关闭游戏再试。", "SlopeX");
            }
        }
    }
}
