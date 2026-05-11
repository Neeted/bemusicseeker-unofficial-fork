using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace BeMusicSeeker.Views;

internal sealed class CustomTableTextLayoutCache
{
    internal const int DefaultMaxEntryCount = 8192;

    private readonly Dictionary<Key, FormattedText> cache;
    private readonly Queue<Key> insertionOrder;
    private readonly int maxEntryCount;

    internal CustomTableTextLayoutCache(int maxEntryCount = DefaultMaxEntryCount)
    {
        this.maxEntryCount = Math.Max(1, maxEntryCount);
        cache = new Dictionary<Key, FormattedText>();
        insertionOrder = new Queue<Key>();
    }

    internal int Count => cache.Count;

    internal void Clear()
    {
        cache.Clear();
        insertionOrder.Clear();
    }

    internal FormattedText GetOrCreate(
        string text,
        double maxTextWidth,
        double maxTextHeight,
        TextAlignment alignment,
        CustomTableTextStyle textStyle,
        FontFamily scoreFontFamily,
        Brush foreground,
        double pixelsPerDip,
        CultureInfo culture,
        out bool hit)
    {
        Brush effectiveForeground = foreground ?? Brushes.Black;
        CultureInfo effectiveCulture = culture ?? CultureInfo.CurrentUICulture;
        CustomTableTextStyle effectiveTextStyle = textStyle ?? CustomTableTextStyle.Normal;
        Key key = Key.Create(text, maxTextWidth, maxTextHeight, alignment, effectiveTextStyle, scoreFontFamily, effectiveForeground, pixelsPerDip, effectiveCulture);
        if (cache.TryGetValue(key, out FormattedText formattedText))
        {
            hit = true;
            return formattedText;
        }

        hit = false;
        formattedText = new FormattedText(
            text,
            effectiveCulture,
            FlowDirection.LeftToRight,
            effectiveTextStyle.CreateTypeface(scoreFontFamily),
            effectiveTextStyle.FontSize,
            effectiveForeground,
            pixelsPerDip)
        {
            MaxTextWidth = maxTextWidth,
            MaxTextHeight = maxTextHeight,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment
        };
        cache.Add(key, formattedText);
        insertionOrder.Enqueue(key);
        TrimToCapacity();
        return formattedText;
    }

    internal static double CalculateHitRate(int hits, int misses)
    {
        int total = hits + misses;
        return total == 0 ? -1d : (double)hits / total;
    }

    internal static bool WouldTrim(
        string text,
        double maxTextWidth,
        CustomTableTextStyle textStyle,
        FontFamily scoreFontFamily,
        double pixelsPerDip,
        CultureInfo culture)
    {
        if (string.IsNullOrEmpty(text) || maxTextWidth <= 0d)
        {
            return false;
        }
        CustomTableTextStyle effectiveTextStyle = textStyle ?? CustomTableTextStyle.Normal;
        FormattedText measuredText = new FormattedText(
            text,
            culture ?? CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            effectiveTextStyle.CreateTypeface(scoreFontFamily),
            effectiveTextStyle.FontSize,
            Brushes.Black,
            pixelsPerDip);
        return measuredText.WidthIncludingTrailingWhitespace > maxTextWidth + 0.5d;
    }

    private void TrimToCapacity()
    {
        while (cache.Count > maxEntryCount && insertionOrder.Count > 0)
        {
            cache.Remove(insertionOrder.Dequeue());
        }
    }

    private readonly struct Key : IEquatable<Key>
    {
        private Key(
            string text,
            double maxTextWidth,
            double maxTextHeight,
            TextAlignment alignment,
            string textStyleKey,
            Color foregroundColor,
            int foregroundReferenceHash,
            bool usesForegroundColor,
            double pixelsPerDip,
            string cultureName)
        {
            Text = text;
            MaxTextWidth = maxTextWidth;
            MaxTextHeight = maxTextHeight;
            Alignment = alignment;
            TextStyleKey = textStyleKey;
            ForegroundColor = foregroundColor;
            ForegroundReferenceHash = foregroundReferenceHash;
            UsesForegroundColor = usesForegroundColor;
            PixelsPerDip = pixelsPerDip;
            CultureName = cultureName;
        }

        private string Text { get; }

        private double MaxTextWidth { get; }

        private double MaxTextHeight { get; }

        private TextAlignment Alignment { get; }

        private string TextStyleKey { get; }

        private Color ForegroundColor { get; }

        private int ForegroundReferenceHash { get; }

        private bool UsesForegroundColor { get; }

        private double PixelsPerDip { get; }

        private string CultureName { get; }

        internal static Key Create(string text, double maxTextWidth, double maxTextHeight, TextAlignment alignment, CustomTableTextStyle textStyle, FontFamily scoreFontFamily, Brush foreground, double pixelsPerDip, CultureInfo culture)
        {
            bool usesForegroundColor = foreground is SolidColorBrush;
            Color foregroundColor = usesForegroundColor ? ((SolidColorBrush)foreground).Color : default;
            int foregroundReferenceHash = usesForegroundColor || foreground == null ? 0 : RuntimeHelpers.GetHashCode(foreground);
            CustomTableTextStyle effectiveTextStyle = textStyle ?? CustomTableTextStyle.Normal;
            return new Key(
                text ?? string.Empty,
                maxTextWidth,
                maxTextHeight,
                alignment,
                effectiveTextStyle.CreateCacheKey(scoreFontFamily),
                foregroundColor,
                foregroundReferenceHash,
                usesForegroundColor,
                pixelsPerDip,
                (culture ?? CultureInfo.CurrentUICulture).Name);
        }

        public bool Equals(Key other)
        {
            return Text == other.Text
                && MaxTextWidth.Equals(other.MaxTextWidth)
                && MaxTextHeight.Equals(other.MaxTextHeight)
                && Alignment == other.Alignment
                && TextStyleKey == other.TextStyleKey
                && ForegroundColor.Equals(other.ForegroundColor)
                && ForegroundReferenceHash == other.ForegroundReferenceHash
                && UsesForegroundColor == other.UsesForegroundColor
                && PixelsPerDip.Equals(other.PixelsPerDip)
                && CultureName == other.CultureName;
        }

        public override bool Equals(object obj)
        {
            return obj is Key other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + Text.GetHashCode();
                hash = (hash * 31) + MaxTextWidth.GetHashCode();
                hash = (hash * 31) + MaxTextHeight.GetHashCode();
                hash = (hash * 31) + (int)Alignment;
                hash = (hash * 31) + TextStyleKey.GetHashCode();
                hash = (hash * 31) + ForegroundColor.GetHashCode();
                hash = (hash * 31) + ForegroundReferenceHash;
                hash = (hash * 31) + UsesForegroundColor.GetHashCode();
                hash = (hash * 31) + PixelsPerDip.GetHashCode();
                hash = (hash * 31) + CultureName.GetHashCode();
                return hash;
            }
        }
    }
}
