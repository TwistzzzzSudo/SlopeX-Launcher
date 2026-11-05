using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlopeX.Controls;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModDownload;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Modules.ModMods;
using SlopeX.Modules.ModNet;
using SlopeX.Windows;

namespace SlopeX.Pages
{
    /// <summary>
    /// 整合包页：搜索 → 挑包版本 → 下载归档 → 交给 <see cref="TaskCenter.StartModpackInstall"/>。
    ///
    /// 这里只负责"把包取下来"：解包、补原版、装加载器、按清单下几百个模组，全部由
    /// <see cref="ModpackInstaller"/> 完成——它同样服务于本地导入（CLI 的 <c>import-pack</c>），
    /// 所以两条入口的安装行为完全一致。
    ///
    /// 与资源包面板一样不按 MC 版本过滤：整合包是启动器级别的入口，不挂在某个已装版本上，
    /// 支持范围放在列表的「支持版本」列，具体装哪一版由用户在浮层里挑。
    /// </summary>
    public partial class PageModpack : UserControl, ILauncherPage
    {
        private ModSourceFilter _filter = ModSourceFilter.All;
        private CancellationTokenSource _cts;

        /// <summary>每页条数。总页数不展示，也不去算——两源合并后总数没有可信来源，只看「还有没有下一页」。</summary>
        private const int PageSize = 20;
        private int _page = 1;
        private bool _hasMore;

        private PackRow _activeRow;          // 正在选版本的那一行
        private Button _activeButton;        // 那一行的「安装」按钮

        public PageModpack()
        {
            InitializeComponent();
        }

        public void OnShown()
        {
            RefreshCounts();
            QueryBox.Focus();

            // 进页面就把榜单列出来：空关键词按下载量排，用户不必先想到搜什么
            _page = 1;
            _ = SearchAsync();
        }

        private void RefreshCounts()
        {
            var packs = 0;
            try { packs = VersionManager.Scan(Config.Current.GameDir).Count(v => v.IsPack); } catch { }

            SubTitle.Text = $"从 Modrinth 与 CurseForge 下载；中文关键词经 MC百科 找对应项目。已装 {packs} 个整合包。";
        }

        // ---------------- 来源 ----------------

        private void Filter_Click(object sender, RoutedEventArgs e)
        {
            // 三个 Chip 互为单选，点哪个就把另外两个取消
            var which = ReferenceEquals(sender, ChipModrinth) ? ModSourceFilter.Modrinth
                : ReferenceEquals(sender, ChipCurseForge) ? ModSourceFilter.CurseForge
                : ModSourceFilter.All;

            ChipAll.IsChecked = which == ModSourceFilter.All;
            ChipModrinth.IsChecked = which == ModSourceFilter.Modrinth;
            ChipCurseForge.IsChecked = which == ModSourceFilter.CurseForge;

            if (which == _filter) return;
            _filter = which;

            // 换来源后旧结果不再对应，直接重搜一次（与模组面板的行为一致）
            _page = 1;
            _ = SearchAsync();
        }

        // ---------------- 搜索 ----------------

        private void Query_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _page = 1;
            _ = SearchAsync();
        }

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            _page = 1;
            _ = SearchAsync();
        }

        private void UpdatePager()
        {
            // 只显示当前页：总数与总页数都不告诉用户，靠「还有没有下一页」决定能不能翻
            Pager.Visibility = _page > 1 || _hasMore ? Visibility.Visible : Visibility.Collapsed;
            PageText.Text = $"第 {_page} 页";
            PrevPageBtn.IsEnabled = _page > 1;
            NextPageBtn.IsEnabled = _hasMore;
        }

        private void PrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_page <= 1) return;
            _page--;
            _ = SearchAsync();
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasMore) return;
            _page++;
            _ = SearchAsync();
        }

        private async Task SearchAsync()
        {
            var query = QueryBox.Text?.Trim() ?? "";

            // 空关键词 = 看榜单（按总下载量从高到低），有关键词才按相关度搜
            var browsing = query.Length == 0;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            SetStatus(browsing ? "正在列出整合包…" : $"正在搜索「{query}」…");
            PackList.ItemsSource = null;
            EmptyPanel.Visibility = Visibility.Collapsed;

            try
            {
                var result = await PackSearch.SearchPageAsync(query, _filter, PageSize, _page, token);

                if (token.IsCancellationRequested) return;

                // 中文走百科映射、条数本来就少，不分页
                _hasMore = !result.FromMcMod && result.HasMore;

                var rows = result.Hits.Select(h => new PackRow(h)).ToList();
                PackList.ItemsSource = rows;
                if (rows.Count > 0) PackList.ScrollIntoView(rows[0]);

                // 列表先出来，图标随后补
                _ = IconLoader.FillAsync(result.Hits, token);

                SetStatus(result.StatusText());
                EmptyPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdatePager();

                Log.Info($"整合包{(browsing ? "榜单" : "搜索「" + query + "」")}（{_filter}）第 {_page} 页：" +
                         $"{rows.Count} 个结果，下一页={_hasMore}" +
                         (result.FromMcMod ? $"，MC百科 命中 {result.Total} 个" : ""));
            }
            catch (OperationCanceledException)
            {
                // 又搜了一次，旧结果丢掉
            }
            catch (Exception ex)
            {
                SetStatus("搜索失败：" + ex.Message);
                Log.Warn($"整合包搜索失败：{ex.Message}");
            }
        }

        // ---------------- 选版本 ----------------

        private async void ChooseVersion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not PackRow row) return;

            _activeRow = row;
            _activeButton = button;

            if (row.Versions.Count == 0)
            {
                SetStatus($"正在读取「{row.Title}」的包版本…");
                button.IsEnabled = false;

                try
                {
                    var versions = await PackSearch.ListVersionsAsync(row.Hit);
                    foreach (var v in versions) row.Versions.Add(new PackVersionRow(v));
                }
                catch (Exception ex)
                {
                    SetStatus("读取版本失败：" + ex.Message);
                    Log.Warn($"读取整合包版本失败 {row.Hit.Slug}：{ex.Message}");
                    return;
                }
                finally
                {
                    button.IsEnabled = true;
                }
            }

            if (row.Versions.Count == 0)
            {
                SetStatus("这个整合包在平台上没有可下载的包文件（可能作者只在外部站点发布）。");
                return;
            }

            VersionList.ItemsSource = row.Versions;
            VersionPopup.PlacementTarget = button;
            VersionPopup.IsOpen = true;

            SetStatus($"共 {row.Versions.Count} 个包版本。点一个就开始下载，装好后会出现在「版本管理」里。");
        }

        private async void VersionPick_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not PackVersionRow row) return;
            if (_activeRow == null || _activeButton == null) return;

            var pack = _activeRow;
            var installButton = _activeButton;

            VersionPopup.IsOpen = false;
            await InstallAsync(pack, installButton, row);
        }

        // ---------------- 下载 → 安装 ----------------

        private async Task InstallAsync(PackRow row, Button button, PackVersionRow version)
        {
            button.IsEnabled = false;
            SetStatus($"正在下载「{row.Title}」{version.Label}（{version.SizeText}）…");
            BeginProgress();

            var target = "";
            try
            {
                var dir = Path.Combine(Paths.CacheDir, "packs");
                Directory.CreateDirectory(dir);
                target = Path.Combine(dir, SafeFileName(version.File.FileName, row.Hit));

                // 同一个包版本下过一次就不再下：整合包动辄一两百 MB
                if (!(File.Exists(target) && version.File.Size > 0 && new FileInfo(target).Length == version.File.Size))
                {
                    var timeout = version.File.Size > 0
                        ? (int)Math.Min(1800000, Math.Max(300000, version.File.Size / 1024 / 20 * 1000))
                        : 900000;

                    await Net.DownloadToFileAsync(version.File.Url, target, version.File.Sha1,
                        version.File.Size, CancellationToken.None, timeout, p => UpdateProgress(p));

                    SetStatus($"下载完成（{version.SizeText}），正在解析整合包…");
                }
                else
                {
                    SetStatus("这个包已经下载过，直接用本地缓存。");
                }

                EndProgress();
                TaskCenter.StartModpackInstall(target);

                SetStatus($"已开始安装「{row.Title}」。补原版、装加载器、下模组的进度都在「任务与日志」页，完成后会出现在「版本管理」。");
                MainWindow.Current?.ShowToast($"开始安装 {row.Title}");
                Log.Info($"整合包下载完成并开始安装：{target}");
            }
            catch (Exception ex)
            {
                SetStatus("下载失败：" + ex.Message + "\n可以重试一次；若仍失败，检查网络或加速器。");
                Log.Warn($"整合包下载失败 {version.File.Url}：{ex.Message}");
            }
            finally
            {
                button.IsEnabled = true;
                EndProgress();
            }
        }

        /// <summary>
        /// 落盘文件名。CF 的文件名可能是中文或带空格，直接拼路径本身没问题，
        /// 但要把路径分隔符与 Windows 非法字符换掉，避免在缓存目录里建出子目录。
        /// </summary>
        private static string SafeFileName(string fileName, ModSearchHit hit)
        {
            var name = string.IsNullOrWhiteSpace(fileName) ? (hit?.Slug ?? "modpack") + ".zip" : fileName;
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshCounts();
            if (PackList.Items.Count > 0) _ = SearchAsync();
        }

        // ---------------- 状态与进度 ----------------

        /// <summary>
        /// 下载回调来自线程池，直接写控件会抛"另一个线程拥有该对象"，统一走 Dispatcher。
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
    }

    /// <summary>整合包列表的一行。</summary>
    internal sealed class PackRow : ObservableObject
    {
        public PackRow(ModSearchHit hit) => Hit = hit;

        public ModSearchHit Hit { get; }

        /// <summary>包版本列表只在第一次点「安装」时拉一次，之后复用。</summary>
        public ObservableCollection<PackVersionRow> Versions { get; } = new ObservableCollection<PackVersionRow>();

        public string Title => Hit.Title;

        public string Description => Hit.Description;

        public string TooltipText => string.IsNullOrWhiteSpace(Hit.Description) ? Hit.Title : Hit.Description;

        public string SubTitle => string.Join("  ·  ", new[]
        {
            Hit.SourceText,
            Hit.Author,
            Hit.DownloadsText,
            Hit.HasMcMod ? "MC百科" : ""
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        public string SupportText => string.IsNullOrWhiteSpace(Hit.VersionSupportText) ? "—" : Hit.VersionSupportText;

        public string UpdatedText => Hit.Updated == DateTime.MinValue
            ? "—"
            : Hit.Updated.ToLocalTime().ToString("yyyy-MM-dd");
    }

    /// <summary>浮层里的一行包版本。</summary>
    internal sealed class PackVersionRow
    {
        public PackVersionRow(ModFileRef file) => File = file;

        public ModFileRef File { get; }

        /// <summary>Modrinth 给的是版本号，CF 给的是 displayName；都没有时退回文件名。</summary>
        public string Label => string.IsNullOrWhiteSpace(File.VersionLabel) ? File.FileName : File.VersionLabel;

        public string SizeText => File.Size > 0 ? Format.Bytes(File.Size) : "大小未知";

        public string SubText => string.Join("  ·  ", new[]
        {
            SizeText,
            File.FileName
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }
}
