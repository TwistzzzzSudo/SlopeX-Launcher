using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SlopeX.Modules.ModAccount;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModJava;
using SlopeX.Modules.ModLaunch;
using SlopeX.Modules.ModMinecraft;
using SlopeX.Windows;

namespace SlopeX.Pages
{
    public partial class PageHome : UserControl, ILauncherPage
    {
        private List<InstalledVersion> _versions = new List<InstalledVersion>();
        private InstalledVersion _selected;
        private GameProcess _game;
        private StreamWriter _gameLog;
        private readonly object _logGate = new object();
        private readonly List<string> _recentOutput = new List<string>();
        private DateTime _launchAt;
        private bool _loading;

        public PageHome()
        {
            InitializeComponent();
        }

        public void OnShown()
        {
            Reload();

            // 每次回到首页都从顶部开始。
            // 注意要等布局与焦点处理完（ContextIdle）再滚 —— 直接同步调用会被
            // "把焦点控件滚进视野"的默认行为覆盖掉，表现就是标题被切掉一截。
            Dispatcher.BeginInvoke(new Action(() => PageScroll.ScrollToTop()),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        public async void Reload()
        {
            if (_loading) return;
            _loading = true;
            try
            {
                var gameDir = Config.Current.GameDir;
                _versions = VersionManager.Scan(gameDir);
                RecentList.ItemsSource = _versions;
                EmptyPanel.Visibility = _versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                _selected = _versions.FirstOrDefault(v => v.Id == Config.Current.LastVersion)
                            ?? _versions.FirstOrDefault();

                ApplySelection();
                UpdateAccount();
                await UpdateJavaInfoAsync();
            }
            catch (Exception ex)
            {
                Log.Error("刷新首页失败", ex);
            }
            finally
            {
                _loading = false;
            }
        }

        private void ApplySelection()
        {
            var running = _game != null && _game.IsRunning;

            StatVersions.Text = _versions.Count.ToString();
            StatMemory.Text = (Config.Current.MemoryMb / 1024.0).ToString("0.#") + " GB";
            StatSource.Text = Config.Current.UseMirror ? "BMCLAPI" : "官方";

            // 游戏目录卡片：版本隔离是强制行为，这里只显示该版本自己的目录
            var root = Config.Current.GameDir;
            GameDirMode.Text = _selected == null
                ? "存档隔离：强制开启（每个版本独立的 mods / 资源包 / 存档）"
                : "存档隔离：强制开启 → " + VersionEnv.Resolve(root, _selected.Id);

            if (_selected == null)
            {
                CurVersionName.Text = "尚未安装版本";
                CurLoaderTag.Visibility = Visibility.Collapsed;
                CurTypeTag.Visibility = Visibility.Collapsed;
                InfoBase.Text = "—";
                InfoMemory.Text = "—";
                InfoPath.Text = "—";
                BtnLaunchText.Text = "去下载一个版本";
                BtnLaunchIcon.Text = "\uE896";
                BtnSwitch.Visibility = Visibility.Collapsed;
                LaunchHint.Visibility = Visibility.Visible;
                LaunchHint.Text = "还没有可用的游戏版本，先到「下载」页安装一个吧。";
                return;
            }

            CurVersionName.Text = _selected.Id;
            CurLoaderTag.Visibility = Visibility.Visible;
            CurTypeTag.Visibility = Visibility.Visible;
            CurLoaderText.Text = _selected.Loader;
            CurTypeText.Text = _selected.TypeLabel;
            CurLoaderTag.Background = _selected.Loader == "原版"
                ? (System.Windows.Media.Brush)FindResource("Ink")
                : (System.Windows.Media.Brush)FindResource("Sub");

            InfoBase.Text = _selected.BaseVersion;
            InfoMemory.Text = $"{Config.Current.MemoryMb} MB";
            InfoPath.Text = _selected.Dir;
            BtnSwitch.Visibility = Visibility.Visible;
            LaunchHint.Visibility = Visibility.Collapsed;

            if (!running)
            {
                BtnLaunchText.Text = "启动游戏";
                BtnLaunchIcon.Text = "\uE768";
            }

            if (RecentList.SelectedItem != _selected)
                RecentList.SelectedItem = _selected;
        }

        private void UpdateAccount()
        {
            var name = string.IsNullOrWhiteSpace(Config.Current.PlayerName) ? "Steve" : Config.Current.PlayerName;
            AccountName.Text = name;
            AvatarText.Text = name.Substring(0, 1).ToUpperInvariant();
            GameDirText.Text = Config.Current.GameDir;
        }

        private async Task UpdateJavaInfoAsync()
        {
            if (_selected == null)
            {
                InfoJava.Text = "—";
                StatJava.Text = "—";
                return;
            }

            InfoJava.Text = "检测中…";
            InfoJava.ClearValue(TextBlock.ForegroundProperty);
            var id = _selected.Id;
            var baseVersion = _selected.BaseVersion;

            var result = await Task.Run(() =>
            {
                try
                {
                    var resolved = VersionManager.Resolve(Config.Current.GameDir, id);
                    var required = JavaFinder.RequiredMajor(baseVersion, resolved.JavaMajor);
                    var java = JavaFinder.FindBest(required);

                    if (java == null)
                    {
                        var text = Config.Current.AutoInstallJava
                            ? $"缺少 Java {required} · 启动时自动安装"
                            : $"缺少 Java {required}";
                        return (text, 0, true);
                    }

                    // 版本高于要求也能跑，但标黄提醒一下
                    return ($"Java {java.Major} · {java.Vendor}", java.Major, java.Major != required);
                }
                catch (Exception ex)
                {
                    return ("无法解析：" + ex.Message, 0, false);
                }
            });

            InfoJava.Text = result.Item1;
            if (result.Item3)
                InfoJava.Foreground = (System.Windows.Media.Brush)FindResource("Warn");
            StatJava.Text = result.Item2 > 0 ? result.Item2.ToString() : "—";
        }

        private void ShowHint(string text, bool warn)
        {
            LaunchHint.Visibility = Visibility.Visible;
            LaunchHint.Text = text;
            LaunchHint.Foreground = (System.Windows.Media.Brush)FindResource(warn ? "Bad" : "Sub");
        }

        /// <summary>
        /// 取得能满足要求的 Java：手动指定 → 已安装 → 自动下载安装。
        /// 返回 null 表示无法取得（已向用户提示原因）。
        /// </summary>
        private async Task<JavaInfo> ResolveJavaAsync(int required)
        {
            if (!string.IsNullOrWhiteSpace(Config.Current.JavaPath))
            {
                var manual = await Task.Run(() => JavaFinder.FromPath(Config.Current.JavaPath));
                if (manual != null && JavaFinder.IsSuitable(manual, required)) return manual;
                if (manual != null)
                {
                    Log.Warn($"设置中指定的 Java {manual.Major} 低于要求的 Java {required}，改为自动选择");
                    ShowHint($"设置中指定的 Java {manual.Major} 无法运行该版本（需要 Java {required}），本次已自动改用其他 Java。", true);
                }
            }

            var hit = await Task.Run(() => JavaFinder.FindBest(required));
            if (hit != null) return hit;

            if (!Config.Current.AutoInstallJava)
            {
                ShowHint($"这个版本需要 Java {required}，本机没有检测到。可在「设置」里开启自动下载，或手动指定 Java 路径。", true);
                return null;
            }

            // ---- 自动下载安装 ----
            Log.Info($"本机缺少 Java {required}，准备自动安装");
            ShowHint($"没有检测到 Java {required}，正在从国内镜像自动下载安装…", false);
            JavaProgressPanel.Visibility = Visibility.Visible;
            JavaProgressBar.Value = 0;
            JavaProgressText.Text = $"Java {required} · 准备中";
            JavaProgressPct.Text = "";
            BtnLaunchText.Text = $"安装 Java {required}…";

            var progress = new Progress<JavaInstallProgress>(p =>
            {
                JavaProgressText.Text = p.Text;
                JavaProgressPct.Text = p.Percent > 0 ? $"{p.Percent:0}%" : "";
                if (p.Percent > 0) JavaProgressBar.Value = p.Percent;
            });

            try
            {
                var java = await JavaProvisioner.EnsureAsync(required, progress);
                JavaProgressBar.Value = 100;
                JavaProgressText.Text = $"Java {java.Major} 已就绪";
                JavaProgressPanel.Visibility = Visibility.Collapsed;
                ShowHint($"已自动安装 Java {java.Major}（{java.Vendor}），位于 {Paths.JavaDir(java.Major)}", false);
                _ = UpdateJavaInfoAsync();   // 刷新「Java 运行时」与实际主版本数字
                return java;
            }
            catch (Exception ex)
            {
                JavaProgressPanel.Visibility = Visibility.Collapsed;
                Log.Error("自动安装 Java 失败", ex);
                ShowHint("Java 自动安装失败：" + ex.Message, true);
                return null;
            }
            finally
            {
                BtnLaunchText.Text = "正在启动…";
            }
        }

        // ---------------- 启动 ----------------

        /// <summary>本轮启动是否已经尝试过补全，避免补完还缺时无限弹窗。</summary>
        private bool _repairTried;

        /// <summary>下一次 Launch_Click 是补全后的自动重试（不是用户新点的）。</summary>
        private bool _retrying;

        private async void Launch_Click(object sender, RoutedEventArgs e)
        {
            if (_game != null && _game.IsRunning) return;

            if (_selected == null)
            {
                MainWindow.Current?.Navigate(2);
                return;
            }

            // 用户新点一次启动 = 新的一轮，补全机会重置；自动重试则沿用上一轮的状态
            var isRetry = _retrying;
            _retrying = false;
            if (!isRetry) _repairTried = false;

            BtnLaunch.IsEnabled = false;
            BtnLaunchText.Text = "正在启动…";
            LaunchHint.Visibility = Visibility.Collapsed;
            JavaProgressPanel.Visibility = Visibility.Collapsed;

            try
            {
                var rootGameDir = Config.Current.GameDir;
                var versionId = _selected.Id;
                var baseVersion = _selected.BaseVersion;

                // 1) 解析版本继承链与 Java 需求，并准备好该版本的游戏目录（版本隔离在此生效）
                var (resolved, required, gameDir) = await Task.Run(() =>
                {
                    var r = VersionManager.Resolve(rootGameDir, versionId);
                    var dir = VersionEnv.Prepare(rootGameDir, versionId);
                    return (r, JavaFinder.RequiredMajor(baseVersion, r.JavaMajor), dir);
                });

                // 2) 找 Java；缺失时自动下载安装
                var java = await ResolveJavaAsync(required);
                if (java == null)
                {
                    SetRunning(false);
                    return;
                }

                Log.Info($"{versionId} 要求 Java {required}，选用 Java {java.Major}（{java.Path}）");

                // 正版账号：令牌过期会自动刷新；刷新失败则回落到离线（不阻塞启动）
                var session = await Msa.EnsureFreshAsync();

                // 3) 拼装启动命令
                var plan = await Task.Run(() =>
                {
                    var options = new LaunchOptions
                    {
                        GameDir = gameDir,
                        RootGameDir = rootGameDir,
                        VersionName = versionId,
                        PlayerName = session?.Name ?? Config.Current.PlayerName,
                        Uuid = session?.Uuid,
                        AccessToken = session?.AccessToken,
                        JavaPath = java.Path,
                        MemoryMb = Config.Current.MemoryMb,
                        MinMemoryMb = Config.Current.MinMemoryMb,
                        JvmExtra = Config.Current.JvmArgs,
                        CustomResolution = Config.Current.CustomResolution,
                        Width = Config.Current.WindowWidth,
                        Height = Config.Current.WindowHeight
                    };

                    return LaunchBuilder.Build(resolved, options, java.Path);
                });

                var logDir = Path.Combine(gameDir, "logs");
                Directory.CreateDirectory(logDir);
                lock (_logGate)
                {
                    _gameLog?.Dispose();
                    _gameLog = new StreamWriter(
                        Path.Combine(logDir, $"slopex-{DateTime.Now:yyyyMMdd-HHmmss}.log"), true)
                    { AutoFlush = true };
                }

                GameLogHub.Clear();
                lock (_logGate) _recentOutput.Clear();
                _launchAt = DateTime.Now;
                _game = new GameProcess();
                _game.Output += OnGameOutput;
                _game.Exited += OnGameExited;
                _game.Start(plan);

                SetRunning(true);
                Log.Info($"启动 {versionId}（Java {java.Major} · {java.Path}）");
                MainWindow.Current?.SetStatus(
                    $"游戏运行中   ·   {versionId}   ·   Java {java.Major}   ·   PID {_game.Pid}");

                Config.Current.LastVersion = versionId;
                Config.Current.Save();

                if (Config.Current.CloseOnLaunch)
                {
                    var w = Window.GetWindow(this);
                    if (w != null) w.WindowState = WindowState.Minimized;
                }
            }
            catch (Exception ex)
            {
                SetRunning(false);
                Log.Error("启动失败", ex);
                LaunchHint.Visibility = Visibility.Visible;
                LaunchHint.Text = ex is FileNotFoundException
                    ? "缺少依赖文件：" + ex.Message
                    : "启动失败：" + ex.Message;

                // 缺文件多半是"外部复制进来的版本没带 libraries/assets"，
                // 直接给一键补全（只补缺失文件，不改写版本 JSON）
                if (ex is FileNotFoundException && !_repairTried && _selected != null)
                {
                    var choice = MessageBox.Show(
                        ex.Message + "\n\n是否自动补全缺失文件？只补缺的部分，不会改动这个版本的设置。",
                        "缺少依赖文件", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                    if (choice == MessageBoxResult.Yes)
                    {
                        _repairTried = true;
                        await RepairThenRetryAsync(_selected.Id);
                        return;
                    }
                }

                MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>补全缺失文件，成功后自动重试启动。</summary>
        private async System.Threading.Tasks.Task RepairThenRetryAsync(string versionId)
        {
            BtnLaunch.IsEnabled = false;
            BtnLaunchText.Text = "正在补全依赖…";

            var task = Modules.ModDownload.TaskCenter.StartLibraryRepair(versionId);
            MainWindow.Current?.SetStatus($"正在补全 {versionId} 的缺失文件…", null);

            var done = new System.Threading.Tasks.TaskCompletionSource<bool>();
            task.Updated += t =>
            {
                if (!t.Running) done.TrySetResult(t.Completed);
            };

            var ok = await done.Task;

            if (!ok)
            {
                SetRunning(false);
                MainWindow.Current?.SetStatus($"补全 {versionId} 失败", null);
                MessageBox.Show(
                    "补全失败：" + (task.Error ?? "未知错误") +
                    "\n\n可在「任务与日志」页查看详情，或换网络环境后重试。",
                    "补全失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MainWindow.Current?.SetStatus("补全完成，正在重试启动…", null);
            _retrying = true;
            Launch_Click(null, null);   // 复用同一套启动流程
        }

        private void OnGameOutput(string line)
        {
            lock (_logGate)
            {
                try { _gameLog?.WriteLine(line); } catch { }
                _recentOutput.Add(line);
                if (_recentOutput.Count > 120) _recentOutput.RemoveAt(0);
            }
            GameLogHub.Push(line);
        }

        private void OnGameExited(int code)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SetRunning(false);
                lock (_logGate)
                {
                    try { _gameLog?.Dispose(); } catch { }
                    _gameLog = null;
                }
                Log.Info($"游戏进程已退出，退出码 {code}");
                MainWindow.Current?.SetStatus($"游戏已退出（{_selected?.Id}，退出码 {code}）", null);
                _ = MainWindow.Current?.RefreshStatusAsync();

                // 非零退出码一律诊断：崩溃不分多少秒，只是秒退还往往更有特征
                var elapsed = DateTime.Now - _launchAt;
                if (code != 0)
                    ShowExitReason(code, elapsed);

                var win = Window.GetWindow(this);
                if (win != null && win.WindowState == WindowState.Minimized)
                    win.WindowState = WindowState.Normal;
            }));
        }

        private void ShowExitReason(int code, TimeSpan elapsed)
        {
            string recent;
            lock (_logGate) recent = string.Join("\n", _recentOutput);

            var versionDir = Path.Combine(Paths.VersionsDir(Config.Current.GameDir), _selected?.Id ?? "");
            var result = Diagnosis.Analyze(versionDir, recent, code, elapsed);

            Log.Warn($"退出诊断（{code}）：{result.Title}");
            ShowHint(result.Text, true);

            if (result.Matched)
                MainWindow.Current?.ShowToast("已识别退出原因：" + result.Title);
        }

        private void SetRunning(bool running)
        {
            BtnLaunch.IsEnabled = !running;
            BtnSwitch.IsEnabled = !running;
            BtnKill.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            BtnLaunchText.Text = running ? "游戏运行中" : "启动游戏";
            BtnLaunchIcon.Text = running ? "\uE71A" : "\uE768";
        }

        private void Kill_Click(object sender, RoutedEventArgs e)
        {
            _game?.Kill();
        }

        // ---------------- 交互 ----------------

        private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

        private void GoVersions_Click(object sender, RoutedEventArgs e) => MainWindow.Current?.Navigate(1);

        private void EditAccount_Click(object sender, RoutedEventArgs e) => MainWindow.Current?.Navigate(4);

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 版本隔离是强制的：打开的就是当前版本自己的目录（材质包 / 存档都在里面）
                var dir = _selected == null
                    ? Config.Current.GameDir
                    : VersionEnv.Resolve(Config.Current.GameDir, _selected.Id);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("打开游戏目录失败：" + ex.Message);
            }
        }

        private void RecentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RecentList.SelectedItem is InstalledVersion v)
            {
                _selected = v;
                ApplySelection();
                Config.Current.LastVersion = v.Id;
                _ = UpdateJavaInfoAsync();
            }
        }
    }
}
