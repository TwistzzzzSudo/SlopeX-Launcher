using System;
using System.Windows.Controls;
using System.Windows.Input;

namespace SlopeX.Controls
{
    /// <summary>要安装的版本类型。</summary>
    public enum InstallKindChoice
    {
        Vanilla,
        Fabric,
        Forge,
        NeoForge
    }

    /// <summary>
    /// 「安装」前的类型选择：原版 / Fabric / Forge / NeoForge。
    /// Forge 与 NeoForge 目前是占位项（置灰 + 即将支持），等安装器实装后放开 IsEnabled 即可。
    /// </summary>
    public partial class InstallChoiceDialog : UserControl
    {
        /// <summary>选择结果；null 表示取消。</summary>
        public event Action<InstallKindChoice?> Closed;

        public InstallChoiceDialog(string versionId, bool vanillaInstalled)
        {
            InitializeComponent();

            TitleText.Text = "安装 " + versionId;
            if (vanillaInstalled)
                VanillaDesc.Text = "已安装。重新安装会校验并补齐缺失文件，不会动存档。";
        }

        /// <summary>把初始焦点放在第一个可选项上，便于键盘操作。</summary>
        public void FocusInitial() => OptVanilla.Focus();

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Closed?.Invoke(null);
        }

        private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e) => Closed?.Invoke(null);

        private void Ok_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            InstallKindChoice kind;
            if (OptFabric.IsChecked == true) kind = InstallKindChoice.Fabric;
            else if (OptForge.IsChecked == true) kind = InstallKindChoice.Forge;
            else if (OptNeoForge.IsChecked == true) kind = InstallKindChoice.NeoForge;
            else kind = InstallKindChoice.Vanilla;

            Closed?.Invoke(kind);
        }
    }
}
