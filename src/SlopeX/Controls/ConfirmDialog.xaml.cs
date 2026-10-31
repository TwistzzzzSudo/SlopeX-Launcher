using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SlopeX.Controls
{
    /// <summary>
    /// 通用「先讲清代价，再让人确认」弹窗。
    ///
    /// 和 <see cref="InstallChoiceDialog"/> 一样不自带业务逻辑：
    /// 点「确认」发 <c>true</c>、点「取消」或按 Esc 发 <c>false</c>，
    /// 关不关模态、要不要改设置都由调用方决定。
    /// </summary>
    public partial class ConfirmDialog : UserControl
    {
        /// <summary>true = 确认，false = 取消（含 Esc）。</summary>
        public event Action<bool> Closed;

        public ConfirmDialog(string title, string message, string confirmText = "确认",
            string[] risks = null, string footnote = null)
        {
            InitializeComponent();

            // 弹窗刚被塞进视觉树时 ShowModal 还没来得及给焦点，此时 FocusInitial 会落空、
            // Esc 就没人接。等 Loaded 再补一次，保证键盘焦点一定在弹窗里。
            Loaded += (s, e) =>
            {
                if (!IsKeyboardFocusWithin) FocusInitial();
            };

            TitleText.Text = title;
            MessageText.Text = message;

            if (!string.IsNullOrEmpty(confirmText)) OkBtn.Content = confirmText;

            if (risks != null && risks.Length > 0)
            {
                var style = (Style)FindResource("TCaption");
                for (var i = 0; i < risks.Length; i++)
                {
                    RiskList.Children.Add(new TextBlock
                    {
                        Style = style,
                        TextWrapping = TextWrapping.Wrap,
                        Text = "·  " + risks[i],
                        Margin = new Thickness(0, 0, 0, i == risks.Length - 1 ? 0 : 6)
                    });
                }
            }
            else
            {
                RiskCard.Visibility = Visibility.Collapsed;
            }

            if (string.IsNullOrEmpty(footnote))
                FootnoteText.Visibility = Visibility.Collapsed;
            else
                FootnoteText.Text = footnote;
        }

        /// <summary>默认焦点给「取消」——回车不该等于"同意承担风险"。</summary>
        public void FocusInitial() => CancelBtn.Focus();

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Closed?.Invoke(false);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Closed?.Invoke(false);

        private void Ok_Click(object sender, RoutedEventArgs e) => Closed?.Invoke(true);
    }
}
