using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SlopeX.Themes
{
    /// <summary>
    /// 主题插画的加载。
    ///
    /// 插画与背景音乐属于私人素材，**不进仓库**（见 .gitignore）。
    /// 因此这里不能假定资源存在：找不到就返回 null，由调用方退回纯渐变背景。
    /// 若在 XAML 里直接写 <c>Source="pack://…"</c>，资源缺失时会在 InitializeComponent
    /// 抛 XamlParseException，整个主窗口都建不起来 —— 那才是真正的坑。
    /// </summary>
    internal static class ArtSource
    {
        /// <summary>取内嵌插画；缺失或损坏返回 null。</summary>
        public static ImageSource TryLoad(string fileName)
        {
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/" + fileName, UriKind.Absolute);
                if (Application.GetResourceStream(uri) == null) return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = uri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }
}
