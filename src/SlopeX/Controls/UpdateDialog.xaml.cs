using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlopeX.Modules.ModBase;
using SlopeX.Modules.ModNet;

namespace SlopeX.Controls
{
    /// <summary>
    /// 「发现新版本」弹窗。不自带业务逻辑：点「立即更新」发 <see cref="Install"/>，
    /// 下载进度由调用方回调 <see cref="ReportProgress"/> 推过来。
    ///
    /// 强制更新（本机版本低于清单的 <c>minVersion</c>）时没有「稍后」，
    /// Esc 也不生效——否则这个限制等于没写。
    /// </summary>
    public partial class UpdateDialog : UserControl
    {
        /// <summary>用户点了「稍后」或按 Esc。</summary>
        public event Action Closed;

        /// <summary>用户点了「立即更新」。</summary>
        public event Action<UpdateInfo> Install;

        private readonly UpdateInfo _info;

        public UpdateDialog(UpdateInfo info)
        {
            InitializeComponent();

            _info = info;

            CurrentText.Text = AppInfo.DisplayVersion;
            NewText.Text = info.Headline;

            if (string.IsNullOrWhiteSpace(info.Notes))
                NotesBox.Visibility = Visibility.Collapsed;
            else
                NotesText.Text = info.Notes.Trim();

            if (info.Forced)
            {
                TitleText.Text = "需要更新";
                CancelBtn.Visibility = Visibility.Collapsed;
            }

            // 塞进视觉树时 ShowModal 还没给焦点，等 Loaded 再补一次，Esc 才有人接
            Loaded += (s, e) =>
            {
                if (!IsKeyboardFocusWithin) FocusInitial();
            };
        }

        /// <summary>强制性弹窗默认焦点给「立即更新」，可选弹窗给「稍后」——回车不该等于"现在装"。</summary>
        public void FocusInitial()
        {
            if (_info.Forced) OkBtn.Focus();
            else CancelBtn.Focus();
        }

        /// <summary>进入下载状态：锁住两个按钮，进度条亮出来。</summary>
        public void BeginProgress()
        {
            ErrorText.Visibility = Visibility.Collapsed;
            ProgressRow.Visibility = Visibility.Visible;
            ProgressText.Text = "";
            DownloadBar.Value = 0;

            OkBtn.IsEnabled = false;
            CancelBtn.IsEnabled = false;
            OkBtn.Content = "正在下载";
        }

        /// <summary>下载进度（来自 <c>Downloader</c> 的分段回调）。</summary>
        public void ReportProgress(DownloadProgress p)
        {
            DownloadBar.Value = p.Percent;
            ProgressText.Text = p.CompactText;
        }

        /// <summary>下载后切换到"正在替换"：进度条满了，按钮保持锁住。</summary>
        public void BeginApply()
        {
            DownloadBar.Value = 100;
            ProgressText.Text = "正在替换，启动器会自行重启…";
            OkBtn.Content = "正在更新";
        }

        /// <summary>出错了：解锁按钮并给出原因，用户还能再试一次或关掉。</summary>
        public void Failed(string message)
        {
            ProgressRow.Visibility = Visibility.Collapsed;
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;

            OkBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
            OkBtn.Content = "重试";
            CancelBtn.Visibility = _info.Forced ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            if (_info.Forced) return;      // 强制更新时 Esc 不放行
            e.Handled = true;
            Closed?.Invoke();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Closed?.Invoke();

        private void Ok_Click(object sender, RoutedEventArgs e) => Install?.Invoke(_info);
    }
}
