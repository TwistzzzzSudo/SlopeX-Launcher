using System.Windows.Controls;

namespace SlopeX.Themes
{
    /// <summary>
    /// ViceCity 主题的背景层。
    /// 插画是可选的：仓库里不含 art/（私人素材），拿不到图就用渐变兜底。
    /// </summary>
    public partial class ViceCityBackdrop : UserControl
    {
        public ViceCityBackdrop()
        {
            InitializeComponent();
            ApplyArt();
        }

        private void ApplyArt()
        {
            var image = ArtSource.TryLoad("vicecity.jpg");
            if (image == null) return;      // 保留 Fallback 渐变

            Art.Source = image;
            Art.Visibility = System.Windows.Visibility.Visible;
            Fallback.Visibility = System.Windows.Visibility.Collapsed;
        }
    }
}
