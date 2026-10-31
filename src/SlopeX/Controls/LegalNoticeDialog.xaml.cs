using System.Windows;
using System.Windows.Controls;
using SlopeX.Modules.ModBase;
using SlopeX.Windows;

namespace SlopeX.Controls
{
    /// <summary>
    /// 法律声明查看器（完整版）。
    ///
    /// 只读展示 <see cref="Legal.BuildDocument"/> 渲染出来的内容；文本可选中复制。
    /// 正文随 exe 走，用户即使只拷了一个 SlopeX.exe 也能看到全文。
    /// 首次启动的确认框是另一个控件 <see cref="LegalGateDialog"/>。
    /// </summary>
    public partial class LegalNoticeDialog : UserControl
    {
        public LegalNoticeDialog()
        {
            InitializeComponent();

            LegalDocumentHost.Attach(DocView, Legal.BuildDocument());
        }

        private void Close_Click(object sender, RoutedEventArgs e) => MainWindow.Current?.HideModal();
    }
}
