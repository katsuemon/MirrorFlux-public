using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MirrorFlux.Controls;

/// <summary>テキストビューアで、行末の改行コードを CR・LF・CRLF の小さな記号として表示する生成器。</summary>
internal sealed class LineEndingElementGenerator : VisualLineElementGenerator
{
    /// <summary>行に改行コードがあれば、その位置を記号の差し込み位置として返す。</summary>
    public override int GetFirstInterestedOffset(int startOffset)
    {
        var line = CurrentContext.VisualLine.LastDocumentLine;
        return line.DelimiterLength != 0 && startOffset <= line.EndOffset ? line.EndOffset : -1;
    }

    /// <summary>改行コードの種類を判定し、記号を表示する要素を作る。</summary>
    public override VisualLineElement ConstructElement(int offset)
    {
        var line = CurrentContext.VisualLine.LastDocumentLine;
        string label = line.DelimiterLength == 2 ? "CRLF" :
            CurrentContext.Document.GetCharAt(offset) == '\r' ? "CR" : "LF";
        return new LineEndingCell(label);
    }

    /// <summary>
    /// 改行記号の要素。表示上は1文字分の幅を取るが、文書上の長さは0で、
    /// カーソルは改行コードの手前の1か所にだけ止まる。
    /// </summary>
    internal sealed class LineEndingCell(string label) : FormattedTextElement("", 0)
    {
        public override bool HandlesLineBorders => true;
        public override int GetVisualColumn(int relativeTextOffset) => VisualColumn;
        /// <summary>カーソルが記号の手前に来るときだけ止まる位置を返し、それ以外は止まらない。</summary>
        public override int GetNextCaretPosition(int column, LogicalDirection direction, CaretPositioningMode mode)
        {
            bool approaching = direction == LogicalDirection.Forward ? column < VisualColumn : column > VisualColumn;
            return approaching ? VisualColumn : -1;
        }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
            new LabelRun(this, context.TextView, label);
    }

    /// <summary>改行記号を全角空白1文字分の枠に描く埋め込みオブジェクト。</summary>
    private sealed class LabelRun(LineEndingCell cell, TextView view, string label)
        : FormattedTextRun(cell, cell.TextRunProperties)
    {
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) =>
            new(view.WideSpaceWidth, view.DefaultLineHeight, view.DefaultBaseline);

        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) =>
            new(0, -view.DefaultBaseline, view.WideSpaceWidth, view.DefaultLineHeight);

        /// <summary>
        /// 3×5ドットの文字を1段に2文字並べて描く。CRLF は CR と LF の2段を同じ文字枠に重ねる。
        /// </summary>
        public override void Draw(DrawingContext dc, Point origin, bool rightToLeft, bool sideways)
        {
            // 文字枠に収まるドットの大きさを決め、枠の中央に配置する。
            double unit = Math.Min(view.WideSpaceWidth / 7, view.DefaultLineHeight / 11);
            double left = origin.X + (view.WideSpaceWidth - 7 * unit) / 2;
            int rows = label.Length == 4 ? 11 : 5;
            double top = origin.Y - view.DefaultBaseline + (view.DefaultLineHeight - rows * unit) / 2;
            // 文字ごとの15ビットの点パターンを、上の行から順に1ドットずつ描く。
            for (int i = 0; i < label.Length; i++)
            {
                int bits = label[i] switch
                {
                    'C' => 0b111_100_100_100_111,
                    'R' => 0b110_101_110_101_101,
                    'L' => 0b100_100_100_100_111,
                    'F' => 0b111_100_110_100_100,
                    _ => 0
                };
                for (int bit = 0; bit < 15; bit++)
                {
                    if ((bits & (1 << (14 - bit))) == 0) continue;
                    double x = left + (i % 2 * 4 + bit % 3) * unit;
                    double y = top + (i / 2 * 6 + bit / 3) * unit;
                    dc.DrawRectangle(view.NonPrintableCharacterBrush, null, new Rect(x, y, unit, unit));
                }
            }
        }
    }
}
