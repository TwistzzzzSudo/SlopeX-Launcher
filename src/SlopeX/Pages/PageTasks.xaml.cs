using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModDownload;
using SlopeX.Windows;

namespace SlopeX.Pages
{
    public partial class PageTasks : UserControl, ILauncherPage
    {
        private readonly ConcurrentQueue<string> _pending = new ConcurrentQueue<string>();
        private readonly DispatcherTimer _timer;
        private bool _hooked;

        /// <summary>
        /// 自己记行数：<see cref="TextBox.LineCount"/> 在控件还没测量过时返回 -1，
        /// 首屏日志因此会显示成「-1 行」；每追加一条就是一行，截断时减掉被切走的换行数即可。
        /// </summary>
        private int _lineCount;

        public PageTasks()
        {
            InitializeComponent();

            TaskList.ItemsSource = TaskCenter.Tasks;
            TaskCenter.Tasks.CollectionChanged += (s, e) => UpdateEmptyState();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            _timer.Tick += (s, e) => Flush();
            _timer.Start();

            // F3 / Shift+F3 在页内任意位置都能跳匹配；Ctrl+F 由主窗口接管（先切到本页再聚焦搜索框）
            PreviewKeyDown += Page_PreviewKeyDown;

            // 命中标记画在 Canvas 上，跟文字的实际位置走：滚动、改窗口大小都要重画
            LogBox.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler((s, e) => RefreshHighlight()), true);
            LogBox.SizeChanged += (s, e) => RefreshHighlight();

            UpdateEmptyState();
        }

        public void OnShown()
        {
            Hook();
            UpdateEmptyState();
        }

        /// <summary>主窗口 Ctrl+F 切到本页后调用。</summary>
        public void FocusSearch()
        {
            LogSearch.Focus();
            LogSearch.SelectAll();
        }

        private void Hook()
        {
            if (_hooked) return;
            _hooked = true;

            foreach (var entry in Log.Snapshot()) _pending.Enqueue(Format(entry));
            foreach (var line in GameLogHub.Snapshot()) _pending.Enqueue("    " + line);
            Flush();

            Log.OnEntry += entry => _pending.Enqueue(Format(entry));
            GameLogHub.Line += line => _pending.Enqueue("    " + line);
        }

        private static string Format(LogEntry e)
        {
            var tag = e.Level switch
            {
                LogLevel.Error => "ERR ",
                LogLevel.Warn => "WARN",
                LogLevel.Trace => "TRC ",
                _ => "INF "
            };
            return $"[{e.Stamp}] {tag} {e.Message}";
        }

        private void Flush()
        {
            if (_pending.IsEmpty) return;

            var sb = new StringBuilder();
            var added = 0;
            while (_pending.TryDequeue(out var line) && sb.Length < 24000)
            {
                sb.AppendLine(line);
                added++;
            }

            if (sb.Length == 0) return;

            // AppendText 会把光标带到文末并滚过去；搜索时得把滚动位置留住，
            // 否则每来一批日志，视图就从命中处被拉回底部
            var keepOffset = _query.Length > 0 ? LogBox.VerticalOffset : -1;

            LogBox.AppendText(sb.ToString());
            _lineCount += added;

            if (keepOffset >= 0) LogBox.ScrollToVerticalOffset(keepOffset);

            // 防止日志无限增长拖慢渲染。截断会让已有的匹配位置整体失效，下面会重算
            if (LogBox.Text.Length > 240000)
            {
                var text = LogBox.Text;
                var cut = text.Length - 120000;

                _lineCount = Math.Max(1, _lineCount - CountNewlines(text, cut));
                LogBox.Text = text.Substring(cut);
            }

            LogCount.Text = $"{_lineCount} 行";

            // 正在搜索时不要跟着滚到底：用户刚跳到某条命中，新日志一来就被拉走等于白找。
            // 同时把命中重扫一遍（新行可能带来新命中），但不改变当前位置。
            if (_query.Length > 0)
            {
                RunSearch(keepCurrent: true, scroll: false);
            }
            else
            {
                LogBox.ScrollToEnd();
            }
        }

        private static int CountNewlines(string text, int length)
        {
            var count = 0;
            var end = Math.Min(length, text.Length);
            for (var i = 0; i < end; i++)
                if (text[i] == '\n') count++;

            return count;
        }

        private void UpdateEmptyState()
        {
            NoTaskPanel.Visibility = TaskCenter.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------- 日志搜索 ----------------

        /// <summary>一次最多记这么多命中：几十万字符的日志里搜一个常见词能上千条，全存下来没有意义。</summary>
        private const int MaxHits = 500;

        private readonly List<int> _hits = new List<int>();
        private int _hitIndex = -1;
        private int _hitStart = -1;
        private int _hitLength;
        private string _query = "";
        private DispatcherTimer _searchDebounce;

        private void LogSearch_TextChanged(object sender, TextChangedEventArgs e) => ScheduleSearch();

        /// <summary>边打边找，但不在每次按键上都扫一遍全文。</summary>
        private void ScheduleSearch()
        {
            _searchDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _searchDebounce.Stop();
            _searchDebounce.Tick -= SearchTick;
            _searchDebounce.Tick += SearchTick;
            _searchDebounce.Start();
        }

        private void SearchTick(object sender, EventArgs e)
        {
            _searchDebounce.Stop();
            RunSearch();
        }

        /// <summary>
        /// 重新扫描 <see cref="LogBox"/> 的全文。
        /// <paramref name="keepCurrent"/> = 日志增长后重扫：尽量停在原来的第几条命中上，不要跳回第一条。
        /// </summary>
        private void RunSearch(bool keepCurrent = false, bool scroll = true)
        {
            var previous = _hitIndex;

            _query = LogSearch.Text ?? "";
            _hits.Clear();
            _hitIndex = -1;

            if (_query.Length > 0)
            {
                var text = LogBox.Text;
                var at = 0;

                while (_hits.Count < MaxHits)
                {
                    var index = text.IndexOf(_query, at, StringComparison.CurrentCultureIgnoreCase);
                    if (index < 0) break;

                    _hits.Add(index);
                    at = index + Math.Max(1, _query.Length);
                }
            }

            if (_hits.Count == 0)
            {
                ClearHighlight();
                UpdateSearchUi();
                return;
            }

            SelectHit(keepCurrent && previous >= 0 ? Math.Min(previous, _hits.Count - 1) : 0, scroll);
        }

        /// <summary>
        /// 跳到第 <paramref name="index"/> 条命中：滚到那一行，并在文字底下画一块标记。
        /// 不用 <see cref="TextBox.Select"/>：选中色只在日志框自己拿到过焦点时才渲染，
        /// 而搜索时焦点在搜索框上，选中等于看不见。
        /// </summary>
        private void SelectHit(int index, bool scroll = true)
        {
            if (_hits.Count == 0)
            {
                UpdateSearchUi();
                return;
            }

            _hitIndex = ((index % _hits.Count) + _hits.Count) % _hits.Count;

            _hitStart = _hits[_hitIndex];
            _hitLength = _query.Length;

            if (scroll)
            {
                var line = LogBox.GetLineIndexFromCharacterIndex(_hitStart);
                if (line >= 0) LogBox.ScrollToLine(line);

                // 滚动是下一轮布局才生效的，先让它落定再按最终位置摆标记
                LogBox.UpdateLayout();
            }

            RefreshHighlight();
            UpdateSearchUi();
        }

        /// <summary>把当前命中画到文字下面的一层 Canvas 上（透明底 + 墨色 16%）。</summary>
        private void RefreshHighlight()
        {
            LogHighlight.Children.Clear();

            if (_hitLength <= 0 || _hitStart < 0 || _hitStart > LogBox.Text.Length) return;

            // 末端取「命中后一个字符的左沿」：trailingEdge=true 拿到的是那个字符的右沿，
            // 框会多盖一个字（中英文都一样），看着像标错了一个词
            var endIndex = _hitStart + _hitLength;
            var start = LogBox.GetRectFromCharacterIndex(_hitStart, false);
            var end = endIndex < LogBox.Text.Length
                ? LogBox.GetRectFromCharacterIndex(endIndex, false)
                : LogBox.GetRectFromCharacterIndex(endIndex - 1, true);

            if (start.IsEmpty || end.IsEmpty) return;

            // 命中跨行（搜索词里不可能有换行，理论上到不了这里）就只标第一段，别画出一条怪线
            if (Math.Abs(end.Y - start.Y) > 1) return;

            var mark = new Border
            {
                Width = Math.Max(1, end.X - start.X),
                Height = start.Height > 0 ? start.Height : 14,
                CornerRadius = new CornerRadius(3),
                Opacity = 0.22,
                IsHitTestVisible = false
            };

            // 用动态资源而不是取一次画刷：切主题后标记也跟着变
            mark.SetResourceReference(Border.BackgroundProperty, "Ink");

            Canvas.SetLeft(mark, start.X);
            Canvas.SetTop(mark, start.Y);
            LogHighlight.Children.Add(mark);
        }

        private void ClearHighlight()
        {
            _hitStart = -1;
            _hitLength = 0;
            LogHighlight.Children.Clear();
        }

        private void StepHit(int delta)
        {
            if (_hits.Count == 0) return;
            SelectHit(_hitIndex < 0 ? 0 : _hitIndex + delta);
        }

        private void UpdateSearchUi()
        {
            var hasQuery = _query.Length > 0;

            SearchNav.Visibility = hasQuery && _hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (!hasQuery)
            {
                SearchCount.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                SearchCount.Text = "";
                return;
            }

            if (_hits.Count == 0)
            {
                SearchCount.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
                SearchCount.Text = "无匹配";
                return;
            }

            SearchCount.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            SearchCount.Text = $"{_hitIndex + 1}/{_hits.Count}" + (_hits.Count >= MaxHits ? "+" : "");
        }

        private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.F3 || _query.Length == 0) return;

            StepHit((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
            e.Handled = true;
        }

        private void LogSearch_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    StepHit((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                    e.Handled = true;
                    break;

                case Key.Escape:
                    LogSearch.Clear();
                    LogBox.Focus();
                    e.Handled = true;
                    break;
            }
        }

        private void PrevHit_Click(object sender, RoutedEventArgs e) => StepHit(-1);

        private void NextHit_Click(object sender, RoutedEventArgs e) => StepHit(1);

        // ---------------- 日志导出 ----------------

        private void ExportLog_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出日志",
                Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt",
                DefaultExt = ".log",
                AddExtension = true,
                FileName = "SlopeX-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".log",
                InitialDirectory = Paths.LogDir
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                // 头两行写清楚「哪台机器、哪个版本、什么时候导的」，日志本身按屏上原样导出
                var header = new StringBuilder();
                header.AppendLine($"# SlopeX Launcher {AppInfo.DisplayVersion}  导出于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                header.AppendLine($"# 游戏目录 {Config.Current.GameDir}");
                header.AppendLine();

                File.WriteAllText(dialog.FileName, header + LogBox.Text, new UTF8Encoding(true));

                MainWindow.Current?.ShowToast($"已导出 {_lineCount} 行日志");
            }
            catch (Exception ex)
            {
                Log.Warn("导出日志失败：" + ex.Message);
                MainWindow.Current?.ShowToast("导出失败：" + ex.Message);
            }
        }

        // ---------------- 任务操作 ----------------

        private void CancelTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is InstallTask task)
                task.Cancel();
        }

        private void PauseTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is InstallTask task)
                task.Pause();
        }

        private void ResumeTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is InstallTask task)
                _ = task.ResumeAsync();
        }

        private void RetryTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is InstallTask task)
                _ = task.RetryAsync();
        }

        private void RemoveTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is InstallTask task)
                TaskCenter.Remove(task);
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogBox.Clear();
            _lineCount = 0;
            Log.Clear();
            GameLogHub.Clear();
            while (_pending.TryDequeue(out _)) { }

            ClearHighlight();
            LogSearch.Clear();
            LogCount.Text = "0 行";
        }

        private void OpenLogDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Paths.LogDir + "\"")
                    { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("打开日志目录失败：" + ex.Message);
            }
        }
    }
}
