using System.Windows.Controls;

namespace SlopeX.Themes
{
    /// <summary>
    /// 复古未来主题的背景层。
    /// 插画是可选的：仓库里不含 art/（私人素材），拿不到图就用渐变兜底。
    /// </summary>
    public partial class RetroFutureBackdrop : UserControl
    {
        public RetroFutureBackdrop()
        {
            InitializeComponent();
            ApplyArt();
        }

        private void ApplyArt()
        {
            var image = ArtSource.TryLoad("retro-future.jpg");
            if (image == null) return;      // 保留 Fallback 渐变

            Art.Source = image;
            Art.Visibility = System.Windows.Visibility.Visible;
            Fallback.Visibility = System.Windows.Visibility.Collapsed;
        }
    }
}
