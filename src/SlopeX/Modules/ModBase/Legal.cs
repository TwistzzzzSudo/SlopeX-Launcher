using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SlopeX.Modules.ModBase
{
    /// <summary>
    /// 内置法律声明。
    ///
    /// 正文只有一份：<c>docs/LegalNotice.md</c>。仓库里可 diff、可备份；
    /// 编译时以 EmbeddedResource 塞进程序集，所以发出去的单文件 exe 虽然不带 md，
    /// 「设置 → 法律声明」照样能显示全文，不会因为少了外部文件而空白。
    ///
    /// 渲染用一个极简 Markdown 子集（标题 / 引用 / 有序无序列表 / 分隔线 / 粗斜体 / 链接），
    /// 不引第三方 Markdown 库：只有这一份文档，为它挂一个包不划算。
    /// 支持的写法都在这份声明里出现过；将来正文若用了新语法（表格、图片），
    /// 这里会退化成纯文本 —— 不会报错，但也不好看，届时再补。
    /// </summary>
    public static class Legal
    {
        private const string ResourceName = "SlopeX.LegalNotice.md";
        private const string BriefResourceName = "SlopeX.LegalNotice-Brief.md";

        /// <summary>声明原文（Markdown）。读不到资源时返回一句占位文案，界面上不会崩。</summary>
        public static string Markdown { get; } = Load(ResourceName);

        /// <summary>首次启动弹窗用的简版：不含仓库地址与联系方式。</summary>
        public static string BriefMarkdown { get; } = Load(BriefResourceName);

        private static string Load(string resourceName)
        {
            try
            {
                using var stream = typeof(Legal).Assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    Log.Warn($"内置法律声明资源缺失（{resourceName}）");
                    return "（内置法律声明缺失，请查看程序目录下的 docs/LegalNotice.md）";
                }

                using var reader = new StreamReader(stream, Encoding.UTF8);
                var text = reader.ReadToEnd();

                // 有些编辑器存 md 会带 BOM，会让首行的 "# " 识别失败
                return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
            }
            catch (Exception ex)
            {
                Log.Warn("读取内置法律声明失败：" + ex.Message);
                return "（内置法律声明读取失败：" + ex.Message + "）";
            }
        }

        // 行内标记：粗斜体 / 粗体 / 斜体 / [文字](地址) / <自动链接> / `代码`
        private static readonly Regex InlineRx = new Regex(
            @"\*\*\*(?<bi>.+?)\*\*\*" +
            @"|\*\*(?<b>.+?)\*\*" +
            @"|(?<!\*)\*(?<i>[^*\n]+?)\*(?!\*)" +
            @"|\[(?<lt>[^\]]*)\]\((?<lu>[^)\s]*)\)" +
            @"|<(?<auto>https?://[^>\s]+)>" +
            @"|`(?<code>[^`]*)`",
            RegexOptions.Compiled);

        private static readonly Regex OrderedRx = new Regex(@"^(\d+)\.\s+(.*)$", RegexOptions.Compiled);

        private static readonly FontFamily MonoFont = new FontFamily("Consolas, Cascadia Mono, Courier New");

        /// <summary>完整版：设置页「法律声明 → 查看」用。</summary>
        public static FlowDocument BuildDocument() => Build(Markdown);

        /// <summary>简版：首次启动确认框用（不含仓库地址与联系方式）。</summary>
        public static FlowDocument BuildBriefDocument() => Build(BriefMarkdown);

        /// <summary>把一份 Markdown 渲染成 FlowDocument。</summary>
        public static FlowDocument Build(string markdown)
        {
            var ink = Resource("Ink", Colors.Black);
            var sub = Resource("Sub", Colors.DimGray);
            var muted = Resource("Muted", Colors.Gray);
            var line = Resource("Line", Colors.LightGray);
            var accent = Resource("Primary", Colors.SteelBlue);

            var doc = new FlowDocument
            {
                FontSize = 12.5,
                LineHeight = 20,
                PagePadding = new Thickness(0)
                // ColumnWidth 故意留空（默认 NaN）：列宽由宿主控件按视口宽度设置。
                // 在这里写死一个值，遇到窄窗口不是横向溢出就是被切成两三栏。
            };

            List list = null;              // 当前正在收集的列表
            var listOrdered = false;
            ListItem item = null;          // 当前列表项（缩进续行要往里追加）

            void Reset()
            {
                list = null;
                listOrdered = false;
                item = null;
            }

            List EnsureList(bool ordered, int start)
            {
                if (list != null && listOrdered == ordered) return list;

                Reset();
                list = new List
                {
                    MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    StartIndex = ordered ? Math.Max(1, start) : 1,
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(18, 0, 0, 0),
                    Foreground = ink
                };
                listOrdered = ordered;
                doc.Blocks.Add(list);
                return list;
            }

            foreach (var raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var lineText = raw.TrimEnd();
                if (lineText.Length == 0)
                {
                    // 空行结束列表；不产生空段落（段距由 Margin 控制）
                    Reset();
                    continue;
                }

                var text = lineText.TrimStart();
                var indent = lineText.Length - text.Length;

                if (text.StartsWith("# ", StringComparison.Ordinal))
                {
                    Reset();
                    AddHeading(doc, text.Substring(2), 0, ink);
                }
                else if (text.StartsWith("### ", StringComparison.Ordinal))
                {
                    Reset();
                    AddHeading(doc, text.Substring(4), 2, ink);
                }
                else if (text.StartsWith("## ", StringComparison.Ordinal))
                {
                    Reset();
                    AddHeading(doc, text.Substring(3), 1, ink);
                }
                else if (text == "---" || text == "***" || text == "___")
                {
                    Reset();
                    AddRule(doc, line);
                }
                else if (text.StartsWith("> ", StringComparison.Ordinal))
                {
                    Reset();
                    AddQuote(doc, text.Substring(2), sub, line, accent);
                }
                else if (text.StartsWith("- ", StringComparison.Ordinal))
                {
                    item = AddItem(EnsureList(false, 1), text.Substring(2), ink, accent);
                }
                else if (OrderedRx.Match(text) is { Success: true } om)
                {
                    var number = int.TryParse(om.Groups[1].Value, out var n) ? n : 1;
                    item = AddItem(EnsureList(true, number), om.Groups[2].Value, ink, accent);
                }
                else if (item != null && indent >= 2)
                {
                    // 缩进续行：挂在当前列表项下面，不另起一段
                    var follow = new Paragraph { Margin = new Thickness(0, 2, 0, 0), Foreground = ink };
                    AddInline(follow, text, ink, accent);
                    item.Blocks.Add(follow);
                }
                else
                {
                    Reset();
                    var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8), Foreground = ink };
                    AddInline(p, text, ink, accent);
                    doc.Blocks.Add(p);
                }
            }

            if (doc.Blocks.Count == 0)
            {
                doc.Blocks.Add(new Paragraph(new Run("（法律声明内容为空）"))
                {
                    Foreground = muted
                });
            }

            return doc;
        }

        private static ListItem AddItem(List list, string text, Brush ink, Brush link)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 4), Foreground = ink };
            AddInline(p, text, ink, link);

            var item = new ListItem(p);
            list.ListItems.Add(item);
            return item;
        }

        private static void AddHeading(FlowDocument doc, string text, int level, Brush ink)
        {
            var p = new Paragraph
            {
                FontSize = level == 0 ? 15.5 : 13,
                Margin = new Thickness(0, level == 0 ? 0 : 14, 0, level == 0 ? 6 : 4),
                Foreground = ink
            };
            p.Inlines.Add(new Run(text) { FontWeight = FontWeights.SemiBold });
            doc.Blocks.Add(p);
        }

        private static void AddQuote(FlowDocument doc, string text, Brush sub, Brush line, Brush link)
        {
            var p = new Paragraph
            {
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(10, 2, 0, 2),
                BorderThickness = new Thickness(2, 0, 0, 0),
                BorderBrush = line,
                Foreground = sub
            };
            AddInline(p, text, sub, link);
            doc.Blocks.Add(p);
        }

        /// <summary>分隔线：空段落 + 下边框，宽度自然铺满正文栏。</summary>
        private static void AddRule(FlowDocument doc, Brush line)
        {
            doc.Blocks.Add(new Paragraph
            {
                Margin = new Thickness(0, 12, 0, 12),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = line,
                FontSize = 1
            });
        }

        private static void AddInline(Paragraph p, string text, Brush ink, Brush link)
        {
            var pos = 0;

            foreach (Match m in InlineRx.Matches(text))
            {
                if (m.Index > pos)
                    p.Inlines.Add(new Run(text.Substring(pos, m.Index - pos)) { Foreground = ink });

                if (m.Groups["bi"].Success)
                    p.Inlines.Add(new Bold(new Italic(new Run(m.Groups["bi"].Value))));
                else if (m.Groups["b"].Success)
                    p.Inlines.Add(new Bold(new Run(m.Groups["b"].Value)) { Foreground = ink });
                else if (m.Groups["i"].Success)
                    p.Inlines.Add(new Italic(new Run(m.Groups["i"].Value)) { Foreground = ink });
                else if (m.Groups["lt"].Success)
                    AddLink(p, m.Groups["lt"].Value, m.Groups["lu"].Value, ink, link);
                else if (m.Groups["auto"].Success)
                    AddLink(p, m.Groups["auto"].Value, m.Groups["auto"].Value, ink, link);
                else if (m.Groups["code"].Success)
                    p.Inlines.Add(new Run(m.Groups["code"].Value) { FontFamily = MonoFont, Foreground = ink });

                pos = m.Index + m.Length;
            }

            if (pos < text.Length)
                p.Inlines.Add(new Run(text.Substring(pos)) { Foreground = ink });
        }

        private static void AddLink(Paragraph p, string label, string url, Brush ink, Brush link)
        {
            // 相对链接（比如 LICENSE）在单文件分发里没有可解析的落点，按普通文字显示
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ||
                  uri.Scheme == Uri.UriSchemeMailto))
            {
                p.Inlines.Add(new Run(label) { Foreground = ink });
                return;
            }

            p.Inlines.Add(new Hyperlink(new Run(label))
            {
                NavigateUri = uri,
                Foreground = link,
                // 默认下划线会把中文正文切得很碎，只保留悬停手型
                TextDecorations = null
            });
        }

        private static Brush Resource(string key, Color fallback)
        {
            var app = Application.Current;
            if (app != null && app.TryFindResource(key) is Brush brush) return brush;

            var solid = new SolidColorBrush(fallback);
            solid.Freeze();
            return solid;
        }
    }
}
