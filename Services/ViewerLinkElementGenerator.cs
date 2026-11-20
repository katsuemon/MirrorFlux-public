using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Rendering;
namespace MirrorFlux.Services;

/// <summary>テキストビューアーの URL をテーマ色で表示する。検索一致の文字色は上書きせず、AvalonEdit 標準の Ctrl+クリックで開く動作は残す。</summary>
public sealed class ViewerLinkElementGenerator(FrameworkElement owner) : LinkElementGenerator
{
    /// <summary>
    /// 一致した URL をテーマ色のリンク要素にする。URL として読めなければ作らない。
    /// </summary>
    protected override VisualLineElement ConstructElementFromMatch(Match match)
    {
        var uri = GetUriFromMatch(match);
        if (uri == null) return null!;
        return new ThemedLink(CurrentContext.VisualLine, match.Length, owner) { NavigateUri = uri, RequireControlModifierForClick = RequireControlModifierForClick };
    }
    /// <summary>
    /// テーマ色で描くリンクの文字列。
    /// </summary>
    private sealed class ThemedLink(VisualLine line, int length, FrameworkElement owner) : VisualLineLinkText(line, length)
    {
        /// <summary>
        /// VisualLineLinkText は通常、色付け処理の後で URL の色を付け直してしまう。ここでは検索一致の文字色が付いていればそれを残す。
        /// </summary>
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            // 検索一致の色でなければ URL の色にし、設定に応じて下線を引く。
            var foreground = TextRunProperties.ForegroundBrush;
            if (!ReferenceEquals(foreground, ViewerPalette.Get(owner, "SearchForeground")) &&
                !ReferenceEquals(foreground, ViewerPalette.Get(owner, "SearchCurrentForeground")))
                TextRunProperties.SetForegroundBrush(ViewerPalette.Get(owner, "UrlForeground"));
            if (context.TextView.LinkTextUnderline) TextRunProperties.SetTextDecorations(TextDecorations.Underline);
            // 表示する範囲の文字列を取り出す。
            int relative = startVisualColumn - VisualColumn;
            var text = context.GetText(context.VisualLine.FirstDocumentLine.Offset + RelativeTextOffset + relative, DocumentLength - relative);
            return new TextCharacters(text.Text, text.Offset, text.Count, TextRunProperties);
        }
        protected override VisualLineText CreateInstance(int length) => new ThemedLink(ParentVisualLine, length, owner) {
            NavigateUri = NavigateUri, TargetName = TargetName, RequireControlModifierForClick = RequireControlModifierForClick
        };
    }
}
