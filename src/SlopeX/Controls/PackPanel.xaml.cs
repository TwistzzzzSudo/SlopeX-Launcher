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
using SlopeX.Modules.ModNet;
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
        private string _subBase = "";        // 页头副标题的固定部分（版本 + MC + 加载器）

        private PackKind _kind = PackKind.Shader;
        private CancellationTokenSource _cts;

        private PackRow _activeRow;          // 正在选版本的那一行
        private Button _activeButton;        // 那一行的「安装」按钮，下载期间禁用

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

            _subBase = $"{versionId}  ·  " +
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

        /// <summary>
        /// 已装数量放进页头副标题，不占状态行——状态行要留给搜索与下载的过程消息，
        /// 装完一句「已安装到 …」不该被计数覆盖掉。
        /// </summary>
        private void RefreshCounts()
        {
            var shaders = PackInstaller.Count(Config.Current.GameDir, _versionId, PackKind.Shader);
            var packs = PackInstaller.Count(Config.Current.GameDir, _versionId, PackKind.ResourcePack);

            SubText.Text = $"{_subBase}  ·  已装光影 {shaders} / 材质 {packs}";
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

                var rows = hits.Select(h => new PackRow(h)).ToList();
                PackList.ItemsSource = rows;

                var kindText = _kind == PackKind.Shader ? "光影" : "材质包";
                SetStatus(rows.Count == 0
                    ? $"没有找到「{query}」相关的{kindText}。换个关键词，或确认识别方式是英文名。"
                    : $"找到 {rows.Count} 个{kindText}。点每行的「安装」，挑一个包版本就开始下载。");

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

        // ---------------- 选版本 = 安装 ----------------

        /// <summary>
        /// 点「安装」先挑版本：挑完这一下就直接开始下载，不再要求第二次点按钮。
        /// 只留一个按钮，是为了避免"选了版本忘了点安装"的空转。
        /// </summary>
        private async void ChooseVersion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not PackRow row) return;

            _activeRow = row;
            _activeButton = button;

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

            VersionList.ItemsSource = row.Versions;

            VersionPopup.PlacementTarget = button;
            VersionPopup.IsOpen = true;

            var match = row.Versions.Count(v => v.MatchesMc(_mcVersion));
            SetStatus(match > 0
                ? $"共 {row.Versions.Count} 个版本，其中 {match} 个声明支持 {_mcVersion}。点一个版本就开始下载。"
                : $"共 {row.Versions.Count} 个版本，都没有声明支持 {_mcVersion} —— 仍然能装，只是不一定生效。");
        }

        private async void VersionPick_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not PackVersionRef version) return;
            if (_activeRow == null || _activeButton == null) return;

            var row = _activeRow;
            var installButton = _activeButton;

            VersionPopup.IsOpen = false;
            await InstallAsync(row, installButton, version);
        }

        // ---------------- 安装 ----------------

        /// <summary>
        /// 装哪个版本、落到哪个目录，全部在进入时定下来：下载途中切页签或换版本也不会串。
        /// </summary>
        private async Task InstallAsync(PackRow row, Button button, PackVersionRef version)
        {
            var kind = _kind;
            var versionId = _versionId;
            var folder = Path.GetFileName(PackInstaller.Dir(Config.Current.GameDir, versionId, kind));

            button.IsEnabled = false;
            SetStatus($"正在下载「{row.Title}」{version.VersionNumber}（{version.SizeText}）…");
            BeginProgress();

            try
            {
                var (path, skipped) = await PackInstaller.InstallAsync(
                    Config.Current.GameDir, versionId, kind, version, CancellationToken.None,
                    msg => SetStatus(msg), p => UpdateProgress(p));

                var name = PrettyName(Path.GetFileName(path));

                if (skipped)
                    SetStatus($"{folder}\\{name} 已存在，跳过。");
                else if (!version.MatchesMc(_mcVersion))
                    SetStatus($"已安装 {name}。注意：这个包声明支持 {version.McText}，" +
                              $"与当前 {_mcVersion} 不一致，进游戏后不一定生效。");
                else
                    SetStatus($"已安装到 {folder}\\{name}");

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
                EndProgress();
            }
        }

        // ---------------- 目录 / 返回 ----------------

        /// <summary>
        /// 材质包文件名里常带 § 颜色码（作者故意的，游戏里会显示成彩色），
        /// 但摆到启动器状态行就是乱码，显示前先剥掉。
        /// </summary>
        private static string PrettyName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('§') < 0) return name;

            var sb = new System.Text.StringBuilder(name.Length);
            for (var i = 0; i < name.Length; i++)
            {
                if (name[i] == '§') { i++; continue; }
                sb.Append(name[i]);
            }
            return sb.ToString();
        }

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

        // ---------------- 下载进度 ----------------

        /// <summary>
        /// 进度条只在下载期间出现，平时不占位置。
        /// 回调同样来自线程池，统一走 Dispatcher，否则会撞"另一个线程拥有该对象"。
        /// </summary>
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

    /// <summary>
    /// 资源包列表的一行。只缓存自己的版本列表——选版本不落状态，
    /// 点中就直接下载（见 PackPanel.InstallAsync），免得界面上出现"选中但没装"的中间态。
    /// </summary>
    internal sealed class PackRow : ObservableObject
    {
        public PackRow(ModSearchHit hit)
        {
            Hit = hit;
        }

        public ModSearchHit Hit { get; }

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
    }
}
