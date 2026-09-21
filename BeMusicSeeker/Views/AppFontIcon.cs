using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BeMusicSeeker.Views;

/// <summary>
/// アイコンフォントの字形を、文字のベースラインではなく実描画領域を基準に表示領域の中央へ配置します。
/// </summary>
public sealed class AppFontIcon : FrameworkElement
{
    /// <summary>
    /// <see cref="Glyph"/> を識別します。
    /// </summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(AppFontIcon),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            InvalidateTextLayout));

    /// <summary>
    /// <see cref="FontFamily"/> を識別します。
    /// </summary>
    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily),
        typeof(FontFamily),
        typeof(AppFontIcon),
        new FrameworkPropertyMetadata(
            SystemFonts.MessageFontFamily,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            InvalidateTextLayout));

    /// <summary>
    /// <see cref="FontSize"/> を識別します。
    /// </summary>
    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize),
        typeof(double),
        typeof(AppFontIcon),
        new FrameworkPropertyMetadata(
            16d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            InvalidateTextLayout),
        IsValidFontSize);

    /// <summary>
    /// <see cref="Foreground"/> を識別します。
    /// </summary>
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground),
        typeof(Brush),
        typeof(AppFontIcon),
        new FrameworkPropertyMetadata(
            Brushes.Black,
            FrameworkPropertyMetadataOptions.AffectsRender));

    private FormattedText formattedText;
    private Rect inkBounds = Rect.Empty;
    private double formattedPixelsPerDip = double.NaN;

    /// <summary>
    /// 表示する一文字のアイコン字形を取得または設定します。
    /// </summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>
    /// アイコン字形のフォントファミリを取得または設定します。
    /// </summary>
    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>
    /// アイコン字形のemサイズをDIP単位で取得または設定します。
    /// </summary>
    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>
    /// アイコン字形の描画ブラシを取得または設定します。
    /// </summary>
    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (string.IsNullOrEmpty(Glyph))
        {
            return new Size(0d, 0d);
        }

        EnsureTextLayout();
        if (formattedText == null || inkBounds.IsEmpty)
        {
            return new Size(FontSize, FontSize);
        }

        return new Size(
            Math.Max(FontSize, inkBounds.Width),
            Math.Max(FontSize, inkBounds.Height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (string.IsNullOrEmpty(Glyph) || Foreground == null || RenderSize.Width <= 0d || RenderSize.Height <= 0d)
        {
            return;
        }

        EnsureTextLayout();
        if (formattedText == null)
        {
            return;
        }

        formattedText.SetForegroundBrush(Foreground);

        Point origin;
        if (!inkBounds.IsEmpty && inkBounds.Width > 0d && inkBounds.Height > 0d)
        {
            origin = new Point(
                ((RenderSize.Width - inkBounds.Width) / 2d) - inkBounds.X,
                ((RenderSize.Height - inkBounds.Height) / 2d) - inkBounds.Y);
        }
        else
        {
            origin = new Point(
                (RenderSize.Width - formattedText.WidthIncludingTrailingWhitespace) / 2d,
                (RenderSize.Height - formattedText.Height) / 2d);
        }

        drawingContext.DrawText(formattedText, origin);
    }

    private void EnsureTextLayout()
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (formattedText != null && Math.Abs(formattedPixelsPerDip - pixelsPerDip) < double.Epsilon)
        {
            return;
        }

        formattedText = new FormattedText(
            Glyph ?? string.Empty,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily ?? SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            FontSize,
            Brushes.Black,
            pixelsPerDip);

        Geometry geometry = formattedText.BuildGeometry(new Point(0d, 0d));
        inkBounds = geometry.Bounds;
        formattedPixelsPerDip = pixelsPerDip;
    }

    private static void InvalidateTextLayout(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var icon = (AppFontIcon)dependencyObject;
        icon.formattedText = null;
        icon.inkBounds = Rect.Empty;
        icon.formattedPixelsPerDip = double.NaN;
    }

    private static bool IsValidFontSize(object value)
    {
        double fontSize = (double)value;
        return fontSize > 0d && !double.IsNaN(fontSize) && !double.IsInfinity(fontSize);
    }
}
