using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace SlopeX.Controls
{
    /// <summary>
    /// 给 <see cref="ScrollViewer"/> 补两件 WPF 默认没有的事：
    ///
    /// 1. **平滑滚动**。默认一档滚轮是直接跳 3 行（约 48 px）且没有过渡，内容高的页面会一顿一顿。
    ///    这里改成按距离缓动（默认约 96 px/档，OutCubic），连滚时在上一次的目标上继续累加，
    ///    手感接近浏览器。
    /// 2. **内层滚动条不再"吃掉"滚轮**。WPF 的 ScrollViewer 收到滚轮后，无论自己还能不能滚
    ///    都会把事件标记成 Handled，所以鼠标停在 ListBox（Java 列表、最近版本）或输入框上时，
    ///    外层的设置页就完全滚不动——这就是"滚轮有时会卡住"。
    ///    这里在隧道阶段（Preview）接管：内层还能滚就让给它，内层滚到头就由外层接着滚。
    ///
    /// 用法：<c>&lt;ScrollViewer controls:ScrollAssist.Smooth="True"&gt;</c>
    /// </summary>
    public static class ScrollAssist
    {
        /// <summary>每个 ScrollViewer 的滚动状态：上一次的目标位置与时间，用于连滚时累加。</summary>
        private sealed class State
        {
            public double Target;
            public long LastTick;
        }

        private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

        // ---------------- 附加属性 ----------------

        public static readonly DependencyProperty SmoothProperty = DependencyProperty.RegisterAttached(
            "Smooth", typeof(bool), typeof(ScrollAssist), new PropertyMetadata(false, OnSmoothChanged));

        public static bool GetSmooth(DependencyObject element) =>
            element != null && (bool)element.GetValue(SmoothProperty);

        public static void SetSmooth(DependencyObject element, bool value) =>
            element?.SetValue(SmoothProperty, value);

        private static void OnSmoothChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ScrollViewer sv)) return;

            // 先减后加：属性被重复设置时不会挂上两个处理器
            sv.PreviewMouseWheel -= OnPreviewMouseWheel;
            if ((bool)e.NewValue) sv.PreviewMouseWheel += OnPreviewMouseWheel;
        }

        /// <summary>
        /// 动画用的"影子偏移"。VerticalOffset 是只读的，没法直接做动画，
        /// 所以动画跑在这个附加属性上，每一帧在回调里真正滚动。
        /// </summary>
        private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
            "Offset", typeof(double), typeof(ScrollAssist),
            new PropertyMetadata(0d, OnOffsetChanged));

        private static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv) sv.ScrollToVerticalOffset((double)e.NewValue);
        }

        // ---------------- 滚轮 ----------------

        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!(sender is ScrollViewer sv) || e.Handled || e.Delta == 0) return;

            // Shift + 滚轮是系统的横向滚动，别抢
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;

            var inner = FindInnerScrollViewer(e.OriginalSource as DependencyObject, sv);
            if (inner != null && CanScrollVertically(inner, e.Delta)) return;   // 内层还没滚到头，交给它

            var step = WheelStep(sv);
            if (step <= 0) return;

            // 普通滚轮一格固定是 ±120；高精度滚轮/触控板会发更小的值，按比例缩小才不会被"一抖就跳一段"
            var scale = Math.Abs(e.Delta) / 120.0;
            ScrollBy(sv, (e.Delta > 0 ? -step : step) * scale);
            e.Handled = true;
        }

        /// <summary>
        /// 一档滚轮走多远：跟随系统的"每次滚动行数"，但设了下限。
        /// WPF 默认是 16 px/行（2 行的机器一格才走 32 px），慢得像没动，这才是"不顺畅"的主因。
        /// </summary>
        private static double WheelStep(ScrollViewer sv)
        {
            var lines = SystemParameters.WheelScrollLines;
            if (lines < 0) return 0;                                      // 系统里关掉了滚轮滚动
            if (lines == 0) return Math.Max(0, sv.ViewportHeight * 0.9);   // "一次滚一屏"
            return Math.Max(88.0, lines * 32.0);
        }

        private static void ScrollBy(ScrollViewer sv, double delta)
        {
            if (Math.Abs(delta) < 0.01) return;

            var max = Math.Max(0, sv.ScrollableHeight);
            var state = States.GetValue(sv, _ => new State());
            var now = Environment.TickCount64;

            // 250 ms 内连着滚，就在上一次的目标上继续加；否则每档都从当前位置重新缓动，会很黏
            var baseline = now - state.LastTick < 250 ? state.Target : sv.VerticalOffset;
            var target = Math.Clamp(baseline + delta, 0, max);

            state.Target = target;
            state.LastTick = now;

            var from = sv.VerticalOffset;
            var distance = Math.Abs(target - from);
            if (distance < 0.5) return;

            // 系统里关掉动画时直接落位，别给"减少动效"的用户硬塞过渡
            if (!SystemParameters.ClientAreaAnimation)
            {
                sv.BeginAnimation(OffsetProperty, null);
                sv.SetValue(OffsetProperty, target);
                return;
            }

            var duration = Math.Min(0.26, 0.11 + distance / 2200.0);
            sv.BeginAnimation(OffsetProperty, new DoubleAnimation(from, target, TimeSpan.FromSeconds(duration))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        /// <summary>从事件源往上找，返回外层 ScrollViewer 之前遇到的第一个 ScrollViewer。</summary>
        private static ScrollViewer FindInnerScrollViewer(DependencyObject source, ScrollViewer outer)
        {
            var node = source;
            while (node != null && !ReferenceEquals(node, outer))
            {
                if (node is ScrollViewer sv) return sv;
                node = node is Visual || node is Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }
            return null;
        }

        /// <summary>这个方向还能滚吗？到底了就该让外层接手。</summary>
        private static bool CanScrollVertically(ScrollViewer inner, int delta)
        {
            if (inner.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) return false;
            if (inner.ScrollableHeight <= 0.5) return false;

            return delta > 0
                ? inner.VerticalOffset > 0.5
                : inner.VerticalOffset < inner.ScrollableHeight - 0.5;
        }
    }
}
