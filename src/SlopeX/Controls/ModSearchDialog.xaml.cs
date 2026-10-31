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
    /// 下载来源只有 Modrinth（免 Key、有公开搜索接口）。
    /// CurseForge 的搜索被移除了 —— 官方接口强制 x-api-key，没有 Key 一律 403，
    /// 而保留一个"填了 Key 才能用"的入口，实际价值不抵它带来的复杂度。
    ///
    /// 关键词含中文时改走 MC百科：那边能按中文名搜到模组，再用词条里的 Modrinth 链接
    /// 把结果对回 Modrinth 项目（详见 <see cref="Modules.ModMods.ModSearch"/>）。
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
                StatusText.Text = "先输入要搜索的模组名（中文、英文都行，如 机械动力 / jei）。";
                return;
            }

            if (string.IsNullOrEmpty(_mcVersion))
                StatusText.Text = "读不出这个版本的 Minecraft 版本号，搜索结果可能不匹配。";

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            var chinese = ModSearch.HasChinese(query);

            StatusText.Text = chinese
                ? $"正在 MC百科 检索「{query}」…（要把词条对上 Modrinth，可能慢几秒）"
                : $"正在搜索「{query}」…";

            ResultList.ItemsSource = null;

            try
            {
                var result = await ModSearch.SearchAsync(query, _mcVersion, _loader, ModSearch.CheckLimit,
                    _cts.Token);

                if (_cts.IsCancellationRequested) return;

                ResultList.ItemsSource = result.Hits;
                StatusText.Text = result.StatusText(_mcVersion, _loader);

                if (chinese)
                    Log.Info($"MC百科 搜索「{query}」：命中 {result.Total}，核对 {result.Checked}，" +
                             $"可安装 {result.Hits.Count}");
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

        /// <summary>
        /// 改状态文字。
        ///
        /// 必须是这一个入口：安装流程里第一次进度回调发生在 <c>await</c> 之前（还在 UI 线程），
        /// 之后 ModInstaller 全线 ConfigureAwait(false)，回调就从线程池上来了。
        /// 直接在回调里写 StatusText.Text 会抛
        /// "The calling thread cannot access this object because a different thread owns it"，
        /// 表现成"安装失败"，但实际一个文件都没下。
        /// </summary>
        private void SetStatus(string text)
        {
            if (text == null) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => StatusText.Text = text));
                return;
            }

            StatusText.Text = text;
        }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not ModSearchHit hit) return;

            b.IsEnabled = false;
            StatusText.Text = $"正在安装「{hit.Title}」…";

            try
            {
                var result = await ModInstaller.InstallAsync(Config.Current.GameDir, _versionId,
                    _mcVersion, _loader, hit, CancellationToken.None, SetStatus);

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
                SetStatus("安装失败：" + ex.Message);
                Log.Warn($"模组安装失败 {hit.Title}：{ex.Message}");
            }
            finally
            {
                b.IsEnabled = true;
            }
        }

        /// <summary>打开百科词条——看中文介绍、合成配方、支持的版本，比英文 README 好读。</summary>
        private void Wiki_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not ModSearchHit hit || !hit.HasMcMod) return;

            OpenUrl(hit.McModUrl, "打开百科页面失败：");
        }

        /// <summary>
        /// 在 MC百科 里搜当前关键词。
        /// 只在 CurseForge 上架的模组映射不到 Modrinth，这时让用户自己去站上下载。
        /// </summary>
        private void OpenWiki_Click(object sender, RoutedEventArgs e)
        {
            var query = QueryBox.Text?.Trim() ?? "";
            var url = query.Length == 0
                ? McModSource.Host + "/modlist.html"
                : McModSource.Host + "/modlist.html?key=" + Uri.EscapeDataString(query);

            OpenUrl(url, "打开百科页面失败：");
        }

        private void OpenUrl(string url, string errorPrefix)
        {
            if (string.IsNullOrEmpty(url)) return;

            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus(errorPrefix + ex.Message);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => MainWindow.Current?.HideModal();
    }
}
