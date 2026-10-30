using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

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
