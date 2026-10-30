using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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

        public PageTasks()
        {
            InitializeComponent();

            TaskList.ItemsSource = TaskCenter.Tasks;
            TaskCenter.Tasks.CollectionChanged += (s, e) => UpdateEmptyState();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            _timer.Tick += (s, e) => Flush();
            _timer.Start();

            UpdateEmptyState();
        }

        public void OnShown()
        {
            Hook();
            UpdateEmptyState();
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
            while (_pending.TryDequeue(out var line) && sb.Length < 24000)
                sb.AppendLine(line);

            if (sb.Length == 0) return;

            LogBox.AppendText(sb.ToString());

            // 防止日志无限增长拖慢渲染
            if (LogBox.Text.Length > 240000)
                LogBox.Text = LogBox.Text.Substring(LogBox.Text.Length - 120000);

            LogBox.ScrollToEnd();
            LogCount.Text = $"{LogBox.LineCount} 行";
        }

        private void UpdateEmptyState()
        {
            NoTaskPanel.Visibility = TaskCenter.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CancelTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is InstallTask task)
                task.Cancel();
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogBox.Clear();
            Log.Clear();
            GameLogHub.Clear();
            while (_pending.TryDequeue(out _)) { }
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
