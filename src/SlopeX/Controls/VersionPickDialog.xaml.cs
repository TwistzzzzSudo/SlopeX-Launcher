using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlopeX.Modules.ModMinecraft;

namespace SlopeX.Controls
{
    /// <summary>
    /// 「普通压缩包要贴到哪个版本上」的选择框。
    ///
    /// 只列可以当基础的版本（不带 <c>inheritsFrom</c> 的原版），
    /// 加载器版本不能当基础——它自己要继承一个原版。
    /// </summary>
    public partial class VersionPickDialog : UserControl
    {
        /// <summary>选中的版本 ID；null 表示取消。</summary>
        public event Action<string> Closed;

        public VersionPickDialog(IEnumerable<InstalledVersion> installed)
        {
            InitializeComponent();

            var list = (installed ?? Enumerable.Empty<InstalledVersion>())
                .Where(v => string.IsNullOrEmpty(v.InheritsFrom))
                .OrderByDescending(v => v.ReleaseDate)
                .ToList();

            VersionList.ItemsSource = list;

            if (list.Count == 0)
            {
                MessageText.Text = "还没有装过原版版本，先去「下载」里装一个。";
                MessageText.Visibility = Visibility.Visible;
                VersionList.Visibility = Visibility.Collapsed;
                OkBtn.IsEnabled = false;
            }
            else
            {
                VersionList.SelectedIndex = 0;
            }

            // 弹窗刚进视觉树时焦点还给不上，Loaded 补一次，否则 Esc / 回车没人接
            Loaded += (s, e) =>
            {
                if (!IsKeyboardFocusWithin) FocusInitial();
            };
        }

        /// <summary>默认焦点给列表——回车即导入当前高亮项。</summary>
        public void FocusInitial()
        {
            if (VersionList.Items.Count > 0) VersionList.Focus();
            else CancelBtn.Focus();
        }

        private string SelectedId => (VersionList.SelectedItem as InstalledVersion)?.Id;

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Closed?.Invoke(null);
            }
            else if (e.Key == Key.Enter && SelectedId != null)
            {
                e.Handled = true;
                Closed?.Invoke(SelectedId);
            }
        }

        private void VersionList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SelectedId == null) return;
            e.Handled = true;
            Closed?.Invoke(SelectedId);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Closed?.Invoke(null);

        private void Ok_Click(object sender, RoutedEventArgs e) => Closed?.Invoke(SelectedId);
    }
}
