using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SlopeX.Controls
{
    /// <summary>
    /// bool → Visibility。参数写 <c>Invert</c> 表示取反。
    /// 只做这一件事，避免每处都写 DataTrigger。
    /// </summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var flag = value is bool b && b;
            if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
                flag = !flag;

            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var visible = value is Visibility v && v == Visibility.Visible;
            if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
                visible = !visible;

            return visible;
        }
    }
}
