using System;
using System.Windows;
using System.Windows.Controls;
using SlopeX.Modules.ModBase;
using SlopeX.Windows;

namespace SlopeX.Controls
{
    /// <summary>
    /// 首次启动的法律声明确认框（简版）。
    ///
    /// 只在 <see cref="Config.LegalAccepted"/> 为 false 时由主窗口弹出一次；
    /// 用户勾选条款并点「确认」后写入配置，之后不再出现。
    /// 不勾选时「确认」保持禁用；不想同意只能点「退出」结束程序。
    /// </summary>
    public partial class LegalGateDialog : UserControl
    {
        public LegalGateDialog()
        {
            InitializeComponent();

            LegalDocumentHost.Attach(DocView, Legal.BuildBriefDocument());
        }

        /// <summary>没勾选就不允许确认——这里是唯一能放行的地方，别在别处再判断一次。</summary>
        private void Agree_Changed(object sender, RoutedEventArgs e)
        {
            var agreed = AgreeBox.IsChecked == true;
            ConfirmBtn.IsEnabled = agreed;
            HintText.Text = agreed ? "" : "勾选后才能继续。";
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (AgreeBox.IsChecked != true) return;

            // 记忆位置：<启动器目录>\SlopeXData\config.json（LegalAccepted / LegalAcceptedAt），
            // 跟着文件夹一起搬走，不另建标记文件——两处记录等于两处可能不一致。
            Config.Current.LegalAccepted = true;
            Config.Current.LegalAcceptedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Config.Current.Save();

            Log.Info($"用户已确认法律声明（{Config.Current.LegalAcceptedAt}）");

            MainWindow.Current?.HideModal();
            MainWindow.Current?.ShowToast("已记录你的同意，之后不再提示");
        }

        /// <summary>不同意就退出——首启弹窗不给"稍后再说"的后门。</summary>
        private void Quit_Click(object sender, RoutedEventArgs e)
        {
            Log.Info("用户未同意法律声明，退出程序");
            Application.Current?.Shutdown(0);
        }
    }
}
