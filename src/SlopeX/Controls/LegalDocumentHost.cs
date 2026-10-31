using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using SlopeX.Modules.ModBase;

namespace SlopeX.Controls
{
    /// <summary>
    /// 把一份法律声明塞进 <see cref="FlowDocumentScrollViewer"/> 并处理好两个共性细节：
    /// 强制单栏（否则窄弹窗里会被切成两三栏），以及链接交给系统浏览器打开。
    ///
    /// 首次启动的简版弹窗与设置里的完整版共用这段逻辑，避免两份重复实现漂移。
    /// </summary>
    internal static class LegalDocumentHost
    {
        public static void Attach(FlowDocumentScrollViewer view, FlowDocument document)
        {
            if (view == null || document == null) return;

            view.Document = document;
            view.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnNavigate));
            view.Loaded += (s, e) => FitSingleColumn(view);
            view.SizeChanged += (s, e) => FitSingleColumn(view);

            FitSingleColumn(view);
        }

        /// <summary>
        /// 把列宽压成视口宽度。
        ///
        /// FlowDocument 默认按可用宽度自动分栏——窄弹窗里通常没事，宽度一变就可能分成两栏，
        /// 长文这样读很难受。跟着视口走，宽高都不会溢出。
        /// </summary>
        private static void FitSingleColumn(FlowDocumentScrollViewer view)
        {
            var doc = view.Document;
            if (doc == null) return;

            var viewport = view.ActualWidth;
            if (viewport <= 0) return;

            // 留出纵向滚动条（约 17px）与右侧一点呼吸空间
            var target = Math.Max(200, viewport - 24);
            if (!double.IsNaN(doc.ColumnWidth) && Math.Abs(doc.ColumnWidth - target) <= 1) return;

            doc.ColumnWidth = target;
        }

        /// <summary>正文里的链接交给系统浏览器 / 邮件客户端，不在应用内开网页。</summary>
        private static void OnNavigate(object sender, RequestNavigateEventArgs e)
        {
            e.Handled = true;

            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("打开法律声明里的链接失败：" + ex.Message);
            }
        }
    }
}
