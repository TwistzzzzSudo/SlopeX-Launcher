using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModMods;
using SlopeX.Windows;

namespace SlopeX.Controls
{
    /// <summary>
    /// 资源包面板：光影包 / 材质包的搜索、选版本、下载。
    ///
    /// 这里**不按 MC 版本与加载器过滤**（与模组面板相反）：光影和材质的版本约束很松，
    /// 硬过滤会漏掉大半结果，所以版本由用户自己在下拉里挑，选中的版本与当前 MC 不一致时只给提示、不拦。
    /// </summary>
    public partial class PackPanel : UserControl
    {
        private string _versionId = "";
        private string _mcVersion = "";

        private PackKind _kind = PackKind.Shader;
        private CancellationTokenSource _cts;

        private PackRow _activeRow;          // 正在选版本的那一行
        private bool _suppressSelection;     // 打开浮层时重置选中项，别当成用户选择

        /// <summary>返回版本列表。</summary>
        public event EventHandler BackRequested;

        public PackPanel()
        {
            InitializeComponent();
        }

        public void Load(string versionId)
        {
            _versionId = versionId;

            // 换了版本就丢掉上一批结果：包本身与版本无关，但"选中的包支不支持当前 MC"的提示依赖版本
            PackList.ItemsSource = null;

            var (mc, loader) = ModInstaller.ResolveTarget(Config.Current.GameDir, versionId);
            _mcVersion = mc;

            SubText.Text = $"{versionId}  ·  " +
                           (string.IsNullOrEmpty(mc) ? "读不出 MC 版本" : "Minecraft " + mc) +
                           (!string.IsNullOrEmpty(loader) ? " / " + loader : "");

            ApplyKind();
            RefreshCounts();
        }

        // ---------------- 类型 ----------------

        private void Kind_Click(object sender, RoutedEventArgs e)
        {
            // 两个 Chip 互为单选：点哪个就把另一个取消，避免同时亮着
            var shader = ReferenceEquals(sender, ChipShader);
            ChipShader.IsChecked = shader;
            ChipPack.IsChecked = !shader;

            _kind = shader ? PackKind.Shader : PackKind.ResourcePack;
            ApplyKind();
            RefreshCounts();

            if (PackList.Items.Count > 0) _ = SearchAsync();
        }

        private void ApplyKind()
        {
            ShaderHint.Visibility = _kind == PackKind.Shader ? Visibility.Visible : Visibility.Collapsed;
            VersionPopup.IsOpen = false;
            EmptyTitle.Text = _kind == PackKind.Shader ? "还没有搜到光影" : "还没有搜到材质包";
            EmptyHint.Text = _kind == PackKind.Shader
                ? "上面输入关键词，比如 complementary、bsl、bsl-shaders"
                : "上面输入关键词，比如 stay-true、fresh-animations";

            // 列表还空着就把空状态亮出来，否则刚进面板会是一大块空白卡片，没有引导
            if (PackList.Items.Count == 0) EmptyPanel.Visibility = Visibility.Visible;
        }

        private void RefreshCounts()
        {
            var shaders = PackInstaller.Count(Config.Current.GameDir, _versionId, PackKind.Shader);
            var packs = PackInstaller.Count(Config.Current.GameDir, _versionId, PackKind.ResourcePack);

            var dir = Path.GetFileName(PackInstaller.Dir(Config.Current.GameDir, _versionId, _kind));
            StatusText.Text = $"当前目录 {dir}\\：光影 {shaders} 个，材质包 {packs} 个。";
        }

        // ---------------- 搜索 ----------------

        private void Query_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _ = SearchAsync();
        }

        private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

        private async Task SearchAsync()
        {
            var query = QueryBox.Text?.Trim() ?? "";
            if (query.Length == 0)
            {
                SetStatus("先输入要搜索的名字，比如 complementary（光影）或 stay-true（材质）。");
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            var projectType = _kind == PackKind.Shader ? "shader" : "resourcepack";
            SetStatus($"正在搜索「{query}」…");
            PackList.ItemsSource = null;
            EmptyPanel.Visibility = Visibility.Collapsed;

            try
            {
                var hits = await ModrinthSource.SearchByTypeAsync(query, projectType, 20, 0, _cts.Token);

                if (_cts.IsCancellationRequested) return;

                var rows = hits.Select(h => new PackRow(h, _mcVersion)).ToList();
                PackList.ItemsSource = rows;

                var kindText = _kind == PackKind.Shader ? "光影" : "材质包";
                SetStatus(rows.Count == 0
                    ? $"没有找到「{query}」相关的{kindText}。换个关键词，或确认识别方式是英文名。"
                    : $"找到 {rows.Count} 个{kindText}。点每行的「选择版本」挑要装的那个，再点「安装」。");

                EmptyPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                Log.Info($"资源包搜索「{query}」（{projectType}）：{rows.Count} 个结果");
            }
            catch (OperationCanceledException)
            {
                // 又搜了一次，忽略旧结果
            }
            catch (Exception ex)
            {
                SetStatus("搜索失败：" + ex.Message);
                Log.Warn($"资源包搜索失败：{ex.Message}");
            }
        }

        // ---------------- 选版本 ----------------

        private async void ChooseVersion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not PackRow row) return;

            _activeRow = row;

            if (row.Versions.Count == 0)
            {
                SetStatus($"正在读取「{row.Title}」的全部版本…");
                button.IsEnabled = false;

                try
                {
                    var versions = await PackInstaller.ListVersionsAsync(row.Hit.ProjectId);
                    foreach (var v in versions) row.Versions.Add(v);
                }
                catch (Exception ex)
                {
                    SetStatus("读取版本失败：" + ex.Message);
                    Log.Warn($"读取资源包版本失败 {row.Hit.Slug}：{ex.Message}");
                    return;
                }
                finally
                {
                    button.IsEnabled = true;
                }
            }

            if (row.Versions.Count == 0)
            {
                SetStatus("这个资源包没有可下载的文件。");
                return;
            }

            // 每次打开都清掉上次的选中，免得点同一个版本不触发 SelectionChanged
            _suppressSelection = true;
            VersionList.ItemsSource = row.Versions;
            VersionList.SelectedIndex = -1;
            _suppressSelection = false;

            VersionPopup.PlacementTarget = button;
            VersionPopup.IsOpen = true;

            var match = row.Versions.Count(v => v.MatchesMc(_mcVersion));
            SetStatus(match > 0
                ? $"共 {row.Versions.Count} 个版本，其中 {match} 个声明支持 {_mcVersion}。"
                : $"共 {row.Versions.Count} 个版本，都没有声明支持 {_mcVersion} —— 仍然可以装，只是不一定生效。");
        }

        private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelection || _activeRow == null) return;
            if (VersionList.SelectedItem is not PackVersionRef version) return;

            _activeRow.Selected = version;
            VersionPopup.IsOpen = false;

            SetStatus(version.MatchesMc(_mcVersion)
                ? $"已选「{_activeRow.Title}」{version.VersionNumber}，可以点「安装」了。"
                : $"已选 {version.VersionNumber}。注意：它声明支持 {version.McText}，与当前 {_mcVersion} 不一致。");
        }

        // ---------------- 安装 ----------------

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not PackRow row) return;

            if (row.Selected == null)
            {
                SetStatus("先在右侧「选择版本」里挑一个要装的包版本。");
                return;
            }

            var version = row.Selected;
            button.IsEnabled = false;

            try
            {
                var (path, skipped) = await PackInstaller.InstallAsync(
                    Config.Current.GameDir, _versionId, _kind, version, CancellationToken.None,
                    msg => SetStatus(msg));

                var folder = Path.GetFileName(PackInstaller.Dir(Config.Current.GameDir, _versionId, _kind));
                SetStatus(skipped
                    ? $"{folder}\\{Path.GetFileName(path)} 已存在，跳过。"
                    : $"已安装到 {folder}\\{Path.GetFileName(path)}");

                if (!version.MatchesMc(_mcVersion))
                    SetStatus($"已安装 {Path.GetFileName(path)}。注意：这个包声明支持 {version.McText}，" +
                              $"与当前 {_mcVersion} 不一致，进游戏后不一定生效。");

                MainWindow.Current?.ShowToast(skipped ? "文件已存在，跳过" : $"已安装 {row.Title}");
                RefreshCounts();
            }
            catch (Exception ex)
            {
                SetStatus("安装失败：" + ex.Message +
                          "\n可以重试一次；若仍失败，检查网络或加速器。");
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        // ---------------- 目录 / 返回 ----------------

        private void OpenDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = PackInstaller.Dir(Config.Current.GameDir, _versionId, _kind);
                Directory.CreateDirectory(dir);   // 打开前确保存在，用户能直接把 zip 拖进去

                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus("打开目录失败：" + ex.Message);
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshCounts();
            if (PackList.Items.Count > 0) _ = SearchAsync();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            VersionPopup.IsOpen = false;
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 改状态行。
        ///
        /// 下载的进度回调来自线程池（`Net.DownloadToFileAsync` 全程 ConfigureAwait(false)），
        /// 直接写控件会抛"另一个线程拥有该对象" —— 这里统一走 Dispatcher。
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
    }

    /// <summary>资源包列表的一行（含它自己的版本列表与当前选中项）。</summary>
    internal sealed class PackRow : ObservableObject
    {
        private PackVersionRef _selected;

        public PackRow(ModSearchHit hit, string mcVersion)
        {
            Hit = hit;
            McVersion = mcVersion ?? "";
        }

        public ModSearchHit Hit { get; }

        /// <summary>当前版本的 MC 号，用于判断选中包版本是否匹配。</summary>
        public string McVersion { get; set; } = "";

        public ObservableCollection<PackVersionRef> Versions { get; } = new ObservableCollection<PackVersionRef>();

        public string Title => Hit.Title;

        public string Description => Hit.Description;

        public string TooltipText => string.IsNullOrWhiteSpace(Hit.Description) ? Hit.Title : Hit.Description;

        public string SubTitle => string.Join("  ·  ", new[]
        {
            Hit.Author,
            Hit.DownloadsText
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        public string SupportText => string.IsNullOrWhiteSpace(Hit.VersionSupportText) ? "—" : Hit.VersionSupportText;

        public string UpdatedText => Hit.Updated == DateTime.MinValue
            ? "—"
            : Hit.Updated.ToLocalTime().ToString("yyyy-MM-dd");

        public PackVersionRef Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                OnPropertyChanged(nameof(SelectionText));
            }
        }

        public string SelectionText => _selected == null ? "选择版本" : _selected.VersionNumber;
    }
}
