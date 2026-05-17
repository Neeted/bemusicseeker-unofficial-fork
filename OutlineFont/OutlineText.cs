using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace OutlineFont;

[ContentProperty("Text")]
internal class OutlineText : FrameworkElement
{
    private FormattedText FormattedText;

    private Geometry TextGeometry;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register("Text", typeof(string), typeof(OutlineText), new FrameworkPropertyMetadata(OnFormattedTextInvalidated));

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register("TextAlignment", typeof(TextAlignment), typeof(OutlineText), new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty TextDecorationsProperty = DependencyProperty.Register("TextDecorations", typeof(TextDecorationCollection), typeof(OutlineText), new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register("TextTrimming", typeof(TextTrimming), typeof(OutlineText), new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register("TextWrapping", typeof(TextWrapping), typeof(OutlineText), new FrameworkPropertyMetadata(TextWrapping.NoWrap, OnFormattedTextUpdated));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register("Fill", typeof(Brush), typeof(OutlineText), new FrameworkPropertyMetadata(Brushes.Red, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register("Stroke", typeof(Brush), typeof(OutlineText), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register("StrokeThickness", typeof(double), typeof(OutlineText), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(OutlineText), new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(OutlineText), new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty FontStretchProperty = TextElement.FontStretchProperty.AddOwner(typeof(OutlineText), new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty FontStyleProperty = TextElement.FontStyleProperty.AddOwner(typeof(OutlineText), new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(OutlineText), new FrameworkPropertyMetadata(OnFormattedTextUpdated));

    public Brush Fill
    {
        get
        {
            return (Brush)GetValue(FillProperty);
        }
        set
        {
            SetValue(FillProperty, value);
        }
    }

    public FontFamily FontFamily
    {
        get
        {
            return (FontFamily)GetValue(FontFamilyProperty);
        }
        set
        {
            SetValue(FontFamilyProperty, value);
        }
    }

    [TypeConverter(typeof(FontSizeConverter))]
    public double FontSize
    {
        get
        {
            return (double)GetValue(FontSizeProperty);
        }
        set
        {
            SetValue(FontSizeProperty, value);
        }
    }

    public FontStretch FontStretch
    {
        get
        {
            return (FontStretch)GetValue(FontStretchProperty);
        }
        set
        {
            SetValue(FontStretchProperty, value);
        }
    }

    public FontStyle FontStyle
    {
        get
        {
            return (FontStyle)GetValue(FontStyleProperty);
        }
        set
        {
            SetValue(FontStyleProperty, value);
        }
    }

    public FontWeight FontWeight
    {
        get
        {
            return (FontWeight)GetValue(FontWeightProperty);
        }
        set
        {
            SetValue(FontWeightProperty, value);
        }
    }

    public Brush Stroke
    {
        get
        {
            return (Brush)GetValue(StrokeProperty);
        }
        set
        {
            SetValue(StrokeProperty, value);
        }
    }

    public double StrokeThickness
    {
        get
        {
            return (double)GetValue(StrokeThicknessProperty);
        }
        set
        {
            SetValue(StrokeThicknessProperty, value);
        }
    }

    public string Text
    {
        get
        {
            return (string)GetValue(TextProperty);
        }
        set
        {
            SetValue(TextProperty, value);
        }
    }

    public TextAlignment TextAlignment
    {
        get
        {
            return (TextAlignment)GetValue(TextAlignmentProperty);
        }
        set
        {
            SetValue(TextAlignmentProperty, value);
        }
    }

    public TextDecorationCollection TextDecorations
    {
        get
        {
            return (TextDecorationCollection)GetValue(TextDecorationsProperty);
        }
        set
        {
            SetValue(TextDecorationsProperty, value);
        }
    }

    public TextTrimming TextTrimming
    {
        get
        {
            return (TextTrimming)GetValue(TextTrimmingProperty);
        }
        set
        {
            SetValue(TextTrimmingProperty, value);
        }
    }

    public TextWrapping TextWrapping
    {
        get
        {
            return (TextWrapping)GetValue(TextWrappingProperty);
        }
        set
        {
            SetValue(TextWrappingProperty, value);
        }
    }

    public OutlineText()
    {
        TextDecorations = new TextDecorationCollection();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        EnsureGeometry();
        drawingContext.DrawGeometry(Fill, new Pen(Stroke, StrokeThickness), TextGeometry);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureFormattedText();
        FormattedText.MaxTextWidth = Math.Min(3579139.0, availableSize.Width);
        FormattedText.MaxTextHeight = availableSize.Height;
        return new Size(FormattedText.Width, FormattedText.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        EnsureFormattedText();
        FormattedText.MaxTextWidth = finalSize.Width;
        FormattedText.MaxTextHeight = finalSize.Height;
        TextGeometry = null;
        return finalSize;
    }

    private static void OnFormattedTextInvalidated(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        OutlineText obj = (OutlineText)dependencyObject;
        obj.FormattedText = null;
        obj.TextGeometry = null;
        obj.InvalidateMeasure();
        obj.InvalidateVisual();
    }

    private static void OnFormattedTextUpdated(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        OutlineText obj = (OutlineText)dependencyObject;
        obj.UpdateFormattedText();
        obj.TextGeometry = null;
        obj.InvalidateMeasure();
        obj.InvalidateVisual();
    }

    private void EnsureFormattedText()
    {
        if (FormattedText == null && Text != null)
        {
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            FormattedText = new FormattedText(Text, CultureInfo.CurrentUICulture, base.FlowDirection, new Typeface(FontFamily, FontStyle, FontWeight, FontStretches.Normal), FontSize, Brushes.Black, pixelsPerDip);
            UpdateFormattedText();
        }
    }

    private void UpdateFormattedText()
    {
        if (FormattedText != null)
        {
            FormattedText.MaxLineCount = ((TextWrapping == TextWrapping.NoWrap) ? 1 : int.MaxValue);
            FormattedText.TextAlignment = TextAlignment;
            FormattedText.Trimming = TextTrimming;
            FormattedText.SetFontSize(FontSize);
            FormattedText.SetFontStyle(FontStyle);
            FormattedText.SetFontWeight(FontWeight);
            FormattedText.SetFontFamily(FontFamily);
            FormattedText.SetFontStretch(FontStretch);
            FormattedText.SetTextDecorations(TextDecorations);
        }
    }

    private void EnsureGeometry()
    {
        if (TextGeometry == null)
        {
            EnsureFormattedText();
            TextGeometry = FormattedText.BuildGeometry(default(Point));
        }
    }
}
