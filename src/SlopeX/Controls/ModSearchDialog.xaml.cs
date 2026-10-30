using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModMods;
using SlopeX.Windows;

namespace SlopeX.Controls
{
    /// <summary>
    /// 模组搜索对话框。
    ///
    /// 只有一个来源：Modrinth（免 Key、有公开搜索接口）。
    /// CurseForge 的搜索被移除了 —— 官方接口强制 x-api-key，没有 Key 一律 403，
    /// 而保留一个"填了 Key 才能用"的入口，实际价值不抵它带来的复杂度。
    /// </summary>
    public partial class ModSearchDialog : UserControl
    {
        private readonly string _versionId;
        private readonly string _mcVersion;
        private readonly string _loader;

        private CancellationTokenSource _cts;

        /// <summary>装完一个模组后通知外面刷新列表。</summary>
        public event EventHandler Installed;

        public ModSearchDialog(string versionId, string mcVersion, string loader)
        {
            InitializeComponent();

            _versionId = versionId;
            _mcVersion = mcVersion;
            _loader = loader;

            var loaderText = string.IsNullOrEmpty(loader) ? "原版（无加载器）" : loader;
            TargetText.Text = $"安装到 {versionId}  ·  Minecraft {mcVersion}  ·  {loaderText}";

            if (string.IsNullOrEmpty(loader))
                StatusText.Text = "这个版本没有安装加载器，模组装进去也不会被加载。建议先安装 Fabric / Forge / NeoForge。";
        }

        public void FocusQuery()
        {
            QueryBox.Focus();
        }

        private void Query_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _ = SearchAsync();
        }

        private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

        private async System.Threading.Tasks.Task SearchAsync()
        {
            var query = QueryBox.Text?.Trim() ?? "";
            if (query.Length == 0)
            {
                StatusText.Text = "先输入要搜索的模组名（支持英文名，如 jei、sodium）。";
                return;
            }

            if (string.IsNullOrEmpty(_mcVersion))
                StatusText.Text = "读不出这个版本的 Minecraft 版本号，搜索结果可能不匹配。";

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            StatusText.Text = $"正在搜索「{query}」…";
            ResultList.ItemsSource = null;

            try
            {
                var hits = await ModrinthSource.SearchAsync(query, _mcVersion, _loader, 20, 0, _cts.Token);

                if (_cts.IsCancellationRequested) return;

                ResultList.ItemsSource = hits;
                StatusText.Text = hits.Count == 0
                    ? $"没有找到适用于 Minecraft {_mcVersion} / {_loader} 的模组。可能是关键词不准确，或该模组不支持这个版本。"
                    : $"找到 {hits.Count} 个结果（已按 Minecraft {_mcVersion} / {_loader} 过滤）。";
            }
            catch (OperationCanceledException)
            {
                // 用户又搜了一次，忽略旧结果
            }
            catch (Exception ex)
            {
                StatusText.Text = "搜索失败：" + ex.Message;
                Log.Warn($"模组搜索失败：{ex.Message}");
            }
        }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not ModSearchHit hit) return;

            b.IsEnabled = false;
            StatusText.Text = $"正在安装「{hit.Title}」…";

            try
            {
                var result = await ModInstaller.InstallAsync(Config.Current.GameDir, _versionId,
                    _mcVersion, _loader, hit, CancellationToken.None, msg => StatusText.Text = msg);

                StatusText.Text = result.Summary();
                Installed?.Invoke(this, EventArgs.Empty);

                if (result.Installed.Count > 0)
                    MainWindow.Current?.ShowToast($"{hit.Title}：{result.Summary()}");

                if (result.Failed.Count > 0)
                    MessageBox.Show(
                        "以下文件下载失败：\n" + string.Join("\n", result.Failed) +
                        "\n\n可以重试一次；若仍失败，检查网络或加速器。", "SlopeX");
            }
            catch (Exception ex)
            {
                StatusText.Text = "安装失败：" + ex.Message;
                Log.Warn($"模组安装失败 {hit.Title}：{ex.Message}");
            }
            finally
            {
                b.IsEnabled = true;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => MainWindow.Current?.HideModal();
    }
}
