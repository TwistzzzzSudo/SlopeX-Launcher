using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModMods;
using SlopeX.Modules.ModNet;
using SlopeX.Windows;

namespace SlopeX.Controls
{
    /// <summary>
    /// 模组搜索对话框。
    ///
    /// 三个来源可选（界面上是三个 Chip）：
    /// <list type="bullet">
    /// <item><b>全部</b>（默认）：Modrinth + CurseForge 一起搜，同名去重后合并；这一模式下
    ///       Modrinth 侧也走 MCIM 镜像，和 CurseForge 共用同一个出口。</item>
    /// <item><b>Modrinth</b>：只搜 Modrinth 官方接口，数据最新。</item>
    /// <item><b>CurseForge</b>：只搜 CurseForge；官方接口强制 x-api-key，所以必须经 MCIM 镜像。</item>
    /// </list>
    ///
    /// 关键词含中文时三条路都先问 MC百科（那边能按中文名搜到模组），
    /// 再用词条里的链接把结果对回对应平台（详见 <see cref="Modules.ModMods.ModSearch"/>）。
    /// </summary>
    public partial class ModSearchDialog : UserControl
    {
        private readonly string _versionId;
        private readonly string _mcVersion;
        private readonly string _loader;

        private ModSourceFilter _filter = ModSourceFilter.All;

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
            else
                StatusText.Text = "输入关键词后回车搜索；默认两个平台一起搜，点上方 Chip 可以只搜其中一个。";

            // 打开就列出下载量最高的模组：多数人进来是想「看看有什么」，不是先想好名字
            _ = SearchAsync();
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

        /// <summary>三个 Chip 互为单选；换源时清掉上一次的结果，免得把 A 源的结果当成 B 源的。</summary>
        private void Source_Click(object sender, RoutedEventArgs e)
        {
            _filter = ReferenceEquals(sender, ChipModrinth) ? ModSourceFilter.Modrinth
                : ReferenceEquals(sender, ChipCurseForge) ? ModSourceFilter.CurseForge
                : ModSourceFilter.All;

            ChipAll.IsChecked = _filter == ModSourceFilter.All;
            ChipModrinth.IsChecked = _filter == ModSourceFilter.Modrinth;
            ChipCurseForge.IsChecked = _filter == ModSourceFilter.CurseForge;

            ResultList.ItemsSource = null;

            // 浏览态（没输关键词）换源就等于换榜单，直接重新列，不用用户再点一次搜索
            if (string.IsNullOrWhiteSpace(QueryBox.Text))
            {
                _ = SearchAsync();
                return;
            }

            StatusText.Text = _filter switch
            {
                ModSourceFilter.Modrinth => "只搜 Modrinth（官方接口）。输入关键词后回车。",
                ModSourceFilter.CurseForge => "只搜 CurseForge（经 MCIM 镜像）。输入关键词后回车。",
                _ => "两个平台一起搜，结果去重后合并。输入关键词后回车。"
            };
        }

        private async System.Threading.Tasks.Task SearchAsync()
        {
            var query = QueryBox.Text?.Trim() ?? "";

            // 空关键词 = 看榜单（按总下载量从高到低），有关键词才按相关度搜
            var browsing = query.Length == 0;

            if (string.IsNullOrEmpty(_mcVersion))
                StatusText.Text = "读不出这个版本的 Minecraft 版本号，搜索结果可能不匹配。";

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            var chinese = ModSearch.HasChinese(query);

            if (browsing)
            {
                StatusText.Text = $"正在列出下载量最高的模组…（按 {_mcVersion}" +
                                  (string.IsNullOrEmpty(_loader) ? "" : " / " + _loader) + " 过滤）";
            }
            else
            {
                StatusText.Text = chinese
                    ? $"正在 MC百科 检索「{query}」…（要把词条对上平台项目，可能慢几秒）"
                    : _filter == ModSourceFilter.All
                        ? $"正在 Modrinth 与 CurseForge 搜索「{query}」…"
                        : $"正在 {(_filter == ModSourceFilter.CurseForge ? "CurseForge" : "Modrinth")} 搜索「{query}」…";
            }

            ResultList.ItemsSource = null;

            try
            {
                var result = await ModSearch.SearchAsync(query, _mcVersion, _loader, _filter,
                    browsing ? 20 : ModSearch.CheckLimit, _cts.Token,
                    browsing ? "downloads" : "relevance");

                if (_cts.IsCancellationRequested) return;

                ResultList.ItemsSource = result.Hits;
                StatusText.Text = result.StatusText(_mcVersion, _loader);

                // 列表先出来，图标随后补——网络慢的时候不能让用户对着空列表等
                _ = IconLoader.FillAsync(result.Hits, _cts.Token);

                if (chinese)
                    Log.Info($"MC百科 搜索「{query}」（{_filter}）：命中 {result.Total}，核对 {result.Checked}，" +
                             $"可安装 {result.Hits.Count}（Modrinth {result.ModrinthCount} / " +
                             $"CurseForge {result.CurseForgeCount}）");
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
            BeginProgress();

            try
            {
                var result = await ModInstaller.InstallAsync(Config.Current.GameDir, _versionId,
                    _mcVersion, _loader, hit, CancellationToken.None, SetStatus, p => UpdateProgress(p));

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
                EndProgress();
            }
        }

        // ---------------- 下载进度 ----------------

        private void BeginProgress()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(BeginProgress));
                return;
            }

            ProgressRow.Visibility = Visibility.Visible;
            DownloadBar.Value = 0;
            ProgressText.Text = "连接中…";
        }

        private void UpdateProgress(DownloadProgress p)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateProgress(p)));
                return;
            }

            ProgressRow.Visibility = Visibility.Visible;
            DownloadBar.Value = p.Total > 0 ? p.Percent : 0;
            ProgressText.Text = p.DetailText;
        }

        private void EndProgress()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(EndProgress));
                return;
            }

            ProgressRow.Visibility = Visibility.Collapsed;
            DownloadBar.Value = 0;
            ProgressText.Text = "";
        }

        /// <summary>打开百科词条——看中文介绍、合成配方、支持的版本，比英文 README 好读。</summary>
        private void Wiki_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not ModSearchHit hit || !hit.HasMcMod) return;

            OpenUrl(hit.McModUrl, "打开百科页面失败：");
        }

        /// <summary>
        /// 在 MC百科 里搜当前关键词。
        /// 两个平台都没登记的模组（比如只在自己的发布页维护的）只能让用户自己去站上看。
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
