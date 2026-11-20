using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Block = System.Windows.Documents.Block;
using MarkdigTable = Markdig.Extensions.Tables.Table;
using MarkdigTableRow = Markdig.Extensions.Tables.TableRow;
using MarkdigTableCell = Markdig.Extensions.Tables.TableCell;
using WpfTable = System.Windows.Documents.Table;
using WpfTableRow = System.Windows.Documents.TableRow;
using WpfTableCell = System.Windows.Documents.TableCell;

namespace MirrorFlux.Services;

/// <summary>
/// Markdig で解析した Markdown を、アプリのテーマ色に合わせた WPF の FlowDocument に組み立てる。
/// </summary>
public static class MarkdownFlowDocumentBuilder
{
    /// <summary>
    /// Markdown の表示に使うテーマ色と等幅フォント。
    /// </summary>
    private sealed record MarkdownThemePalette(
        Brush Highlight,
        Brush PaneText,
        Brush Muted,
        Brush DarkPurple,
        Brush Border,
        FontFamily MonoFont)
    {
        /// <summary>
        /// アプリのリソースからテーマ色を取る。取れないときは既定の色を使う。
        /// </summary>
        public static MarkdownThemePalette Resolve()
        {
            // 既定の色を用意する。
            var fallbackHighlight = CreateFrozenBrush("#8494FC");
            var fallbackPaneText = CreateFrozenBrush("#D8D8D8");
            var fallbackMuted = CreateFrozenBrush("#888888");
            var fallbackDarkPurple = CreateFrozenBrush("#19112E");
            var fallbackBorder = CreateFrozenBrush("#2D224D");
            var fallbackMono = new FontFamily("Consolas");

            // アプリが動いていればリソースのテーマ色を使う。
            try
            {
                var app = Application.Current;
                if (app is not null)
                {
                    return new MarkdownThemePalette(
                        GetBrush(app, "HighlightText", fallbackHighlight),
                        GetBrush(app, "PaneText", fallbackPaneText),
                        GetBrush(app, "MutedText", fallbackMuted),
                        GetBrush(app, "DarkPurpleBackground", fallbackDarkPurple),
                        GetBrush(app, "InactiveBorder", fallbackBorder),
                        fallbackMono
                    );
                }
            }
            catch { }

            return new MarkdownThemePalette(
                fallbackHighlight,
                fallbackPaneText,
                fallbackMuted,
                fallbackDarkPurple,
                fallbackBorder,
                fallbackMono
            );
        }

        /// <summary>
        /// リソースからブラシを探し、なければ fallback を返す。
        /// </summary>
        private static Brush GetBrush(Application app, string key, Brush fallback)
        {
            try
            {
                if (app.TryFindResource(key) is Brush b) return b;
            }
            catch { }
            return fallback;
        }

        /// <summary>
        /// 「#RRGGBB」の色からフリーズ済みのブラシを作る。
        /// </summary>
        private static Brush CreateFrozenBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>
    /// Markdown の文字列から FlowDocument を作る。
    /// </summary>
    public static FlowDocument Build(string markdownText, FontFamily fontFamily, double baseFontSize)
    {
        var palette = MarkdownThemePalette.Resolve();

        // 1段組みで全幅に表示する文書を用意する。
        var doc = new FlowDocument
        {
            Background = Brushes.Transparent,
            Foreground = palette.PaneText,
            FontFamily = fontFamily,
            FontSize = baseFontSize,
            PagePadding = new Thickness(24, 16, 24, 24),
            ColumnWidth = double.PositiveInfinity
        };

        if (string.IsNullOrEmpty(markdownText))
        {
            return doc;
        }

        // 解析して、ブロックごとに WPF の要素へ変換する。
        var mdDoc = Markdown.Parse(markdownText, Pipeline);

        foreach (var block in mdDoc)
        {
            var uiBlock = RenderBlock(block, baseFontSize, palette);
            if (uiBlock is not null)
            {
                doc.Blocks.Add(uiBlock);
            }
        }

        return doc;
    }

    /// <summary>
    /// Markdown の1ブロックを WPF のブロックに変換する。対応しない種類は null を返す。
    /// </summary>
    private static Block? RenderBlock(Markdig.Syntax.Block block, double baseFontSize, MarkdownThemePalette palette)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                // 見出しはレベルに応じて大きくし、強調色の太字にする。レベル1・2には下線を引く。
                var p = new Paragraph
                {
                    Margin = new Thickness(0, 16, 0, 8),
                    KeepWithNext = true
                };

                double size = heading.Level switch
                {
                    1 => baseFontSize + 10,
                    2 => baseFontSize + 6,
                    3 => baseFontSize + 3,
                    4 => baseFontSize + 1,
                    _ => baseFontSize
                };
                p.FontSize = size;
                p.FontWeight = FontWeights.Bold;
                p.Foreground = palette.Highlight;

                if (heading.Inline is not null)
                {
                    RenderInlines(heading.Inline, p.Inlines, baseFontSize, palette.Highlight, palette);
                }

                if (heading.Level == 1)
                {
                    p.BorderBrush = palette.Highlight;
                    p.BorderThickness = new Thickness(0, 0, 0, 1.5);
                    p.Padding = new Thickness(0, 0, 0, 4);
                }
                else if (heading.Level == 2)
                {
                    p.BorderBrush = palette.Border;
                    p.BorderThickness = new Thickness(0, 0, 0, 1);
                    p.Padding = new Thickness(0, 0, 0, 3);
                }

                return p;
            }

            case ParagraphBlock para:
            {
                // 段落は行間を広めにする。
                var p = new Paragraph
                {
                    Margin = new Thickness(0, 4, 0, 6),
                    LineHeight = baseFontSize * 1.5
                };
                if (para.Inline is not null)
                {
                    RenderInlines(para.Inline, p.Inlines, baseFontSize, palette.PaneText, palette);
                }
                return p;
            }

            case ListBlock list:
            {
                // 箇条書きは番号付きか黒丸にし、項目の中のブロックも再帰的に変換する。
                var wpfList = new List
                {
                    Margin = new Thickness(0, 4, 0, 8),
                    Padding = new Thickness(20, 0, 0, 0),
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc
                };

                foreach (var item in list)
                {
                    if (item is ListItemBlock listItem)
                    {
                        var li = new ListItem();
                        foreach (var itemBlock in listItem)
                        {
                            var b = RenderBlock(itemBlock, baseFontSize, palette);
                            if (b is not null)
                            {
                                li.Blocks.Add(b);
                            }
                        }
                        wpfList.ListItems.Add(li);
                    }
                }
                return wpfList;
            }

            case QuoteBlock quote:
            {
                // 引用は左に線を引いた背景付きの区画にし、文字を控えめな色にする。
                var section = new Section
                {
                    Margin = new Thickness(0, 8, 0, 8),
                    Padding = new Thickness(12, 4, 8, 4),
                    BorderBrush = palette.Highlight,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Background = palette.DarkPurple
                };

                foreach (var innerBlock in quote)
                {
                    var b = RenderBlock(innerBlock, baseFontSize, palette);
                    if (b is not null)
                    {
                        b.Foreground = palette.Muted;
                        section.Blocks.Add(b);
                    }
                }
                return section;
            }

            case FencedCodeBlock code:
            {
                // 囲みのコードは等幅フォントの枠付き段落にし、言語名があれば先頭に出す。
                var codeText = string.Empty;
                using (var sw = new StringWriter())
                {
                    for (int i = 0; i < code.Lines.Count; i++)
                    {
                        sw.WriteLine(code.Lines.Lines[i].ToString());
                    }
                    codeText = sw.ToString().TrimEnd('\r', '\n');
                }

                var p = new Paragraph
                {
                    Margin = new Thickness(0, 8, 0, 8),
                    Padding = new Thickness(12, 8, 12, 8),
                    Background = palette.DarkPurple,
                    BorderBrush = palette.Border,
                    BorderThickness = new Thickness(1),
                    FontFamily = palette.MonoFont,
                    FontSize = Math.Max(11, baseFontSize - 1),
                    LineHeight = baseFontSize * 1.3
                };

                if (!string.IsNullOrEmpty(code.Info))
                {
                    var headerRun = new Run($"[{code.Info}]\n")
                    {
                        Foreground = palette.Highlight,
                        FontWeight = FontWeights.Bold
                    };
                    p.Inlines.Add(headerRun);
                }

                var codeRun = new Run(codeText)
                {
                    Foreground = palette.PaneText
                };
                p.Inlines.Add(codeRun);

                return p;
            }

            case CodeBlock code:
            {
                // 字下げのコードも等幅フォントの枠付き段落にする。
                var codeText = string.Empty;
                using (var sw = new StringWriter())
                {
                    for (int i = 0; i < code.Lines.Count; i++)
                    {
                        sw.WriteLine(code.Lines.Lines[i].ToString());
                    }
                    codeText = sw.ToString().TrimEnd('\r', '\n');
                }

                var p = new Paragraph
                {
                    Margin = new Thickness(0, 8, 0, 8),
                    Padding = new Thickness(12, 8, 12, 8),
                    Background = palette.DarkPurple,
                    BorderBrush = palette.Border,
                    BorderThickness = new Thickness(1),
                    FontFamily = palette.MonoFont,
                    FontSize = Math.Max(11, baseFontSize - 1),
                    LineHeight = baseFontSize * 1.3
                };
                p.Inlines.Add(new Run(codeText) { Foreground = palette.PaneText });
                return p;
            }

            case ThematicBreakBlock:
            {
                // 区切り線は上辺だけ線のある空の段落で表す。
                return new Paragraph
                {
                    Margin = new Thickness(0, 12, 0, 12),
                    BorderBrush = palette.Border,
                    BorderThickness = new Thickness(0, 1, 0, 0)
                };
            }

            case MarkdigTable table:
            {
                // 表は罫線付きにし、先頭の行を見出しとして背景付きの強調色の太字にする。
                var wpfTable = new WpfTable
                {
                    Margin = new Thickness(0, 8, 0, 12),
                    BorderBrush = palette.Border,
                    BorderThickness = new Thickness(1),
                    CellSpacing = 0
                };

                var rowGroup = new TableRowGroup();
                wpfTable.RowGroups.Add(rowGroup);

                bool isFirst = true;
                foreach (var rowObj in table)
                {
                    if (rowObj is MarkdigTableRow row)
                    {
                        var wpfRow = new WpfTableRow();
                        if (isFirst)
                        {
                            wpfRow.Background = palette.DarkPurple;
                        }

                        foreach (var cellObj in row)
                        {
                            if (cellObj is MarkdigTableCell cell)
                            {
                                var wpfCell = new WpfTableCell
                                {
                                    BorderBrush = palette.Border,
                                    BorderThickness = new Thickness(1),
                                    Padding = new Thickness(8, 4, 8, 4)
                                };

                                foreach (var cellBlock in cell)
                                {
                                    var renderedCellBlock = RenderBlock(cellBlock, baseFontSize, palette);
                                    if (renderedCellBlock is not null)
                                    {
                                        if (isFirst)
                                        {
                                            renderedCellBlock.FontWeight = FontWeights.Bold;
                                            renderedCellBlock.Foreground = palette.Highlight;
                                        }
                                        wpfCell.Blocks.Add(renderedCellBlock);
                                    }
                                }
                                wpfRow.Cells.Add(wpfCell);
                            }
                        }
                        rowGroup.Rows.Add(wpfRow);
                        isFirst = false;
                    }
                }
                return wpfTable;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// 行内の要素（文字、改行、強調、コード、リンク）を WPF の行内要素に変換して target へ加える。
    /// </summary>
    private static void RenderInlines(
        ContainerInline inlines,
        InlineCollection target,
        double baseFontSize,
        Brush defaultBrush,
        MarkdownThemePalette palette)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    target.Add(new Run(lit.Content.ToString()) { Foreground = defaultBrush });
                    break;

                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;

                case EmphasisInline emp:
                {
                    // 「**」は強調色の太字、「*」は斜体にする。
                    var span = new Span();
                    if (emp.DelimiterCount >= 2)
                    {
                        span.FontWeight = FontWeights.Bold;
                        span.Foreground = palette.Highlight;
                    }
                    else
                    {
                        span.FontStyle = FontStyles.Italic;
                    }
                    RenderInlines(emp, span.Inlines, baseFontSize, defaultBrush, palette);
                    target.Add(span);
                    break;
                }

                case CodeInline code:
                {
                    // 行内コードは背景付きの等幅フォントにする。
                    var run = new Run($" {code.Content} ")
                    {
                        Background = palette.DarkPurple,
                        Foreground = palette.Highlight,
                        FontFamily = palette.MonoFont
                    };
                    target.Add(run);
                    break;
                }

                case LinkInline link:
                {
                    // リンクは下線付きの強調色にする。表示文字がなければ URL を出す。
                    var span = new Span
                    {
                        Foreground = palette.Highlight,
                        TextDecorations = TextDecorations.Underline
                    };
                    if (link.FirstChild is not null)
                    {
                        RenderInlines(link, span.Inlines, baseFontSize, palette.Highlight, palette);
                    }
                    else
                    {
                        span.Inlines.Add(new Run(link.Url ?? string.Empty));
                    }
                    target.Add(span);
                    break;
                }

                case ContainerInline container:
                    RenderInlines(container, target, baseFontSize, defaultBrush, palette);
                    break;
            }
        }
    }
}
