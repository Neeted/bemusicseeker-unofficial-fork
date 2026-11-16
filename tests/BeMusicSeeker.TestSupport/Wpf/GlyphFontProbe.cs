using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>文字を実際に描いたときに使われるフォントを調べます。代替フォントへ落ちた文字を、指定したフォント名ではなく描画結果で区別するために使います。</summary>
internal static class GlyphFontProbe
{
    /// <summary>指定のフォントで文字列を描き、使われた一つのフォントを返します。複数のフォントに分かれる文字列は渡しません。</summary>
    /// <returns>同じファイルに複数の書体を持つフォントを区別できるよう、書体番号を含むURIの全体。</returns>
    internal static string FontUri(FontFamily family, string text)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawText(
                new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 16d, Brushes.Black, 1d),
                new Point());
        }
        return FontUris(visual.Drawing).Single();
    }

    /// <summary>描画に含まれる文字が使ったフォントを、重複を除いて返します。</summary>
    internal static IEnumerable<string> FontUris(Drawing? drawing)
    {
        return drawing switch
        {
            DrawingGroup group => group.Children.SelectMany(FontUris).Distinct(),
            GlyphRunDrawing run => [run.GlyphRun.GlyphTypeface.FontUri.ToString()],
            _ => []
        };
    }
}
