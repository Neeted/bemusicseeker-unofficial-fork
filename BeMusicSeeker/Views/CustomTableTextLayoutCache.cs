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
        cache = [];
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
        IReadOnlyList<CustomTableTextRunStyle> textRuns,
        double pixelsPerDip,
        CultureInfo culture,
        out bool hit)
    {
        Brush effectiveForeground = foreground ?? Brushes.Black;
        CultureInfo effectiveCulture = culture ?? CultureInfo.CurrentUICulture;
        CustomTableTextStyle effectiveTextStyle = textStyle ?? CustomTableTextStyle.Normal;
        string runKey = CreateTextRunsCacheKey(textRuns, text?.Length ?? 0);
        var key = Key.Create(text, maxTextWidth, maxTextHeight, alignment, effectiveTextStyle, scoreFontFamily, effectiveForeground, runKey, pixelsPerDip, effectiveCulture);
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
        ApplyTextRuns(formattedText, textRuns, text?.Length ?? 0);
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
        var measuredText = new FormattedText(
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

    private static void ApplyTextRuns(FormattedText formattedText, IReadOnlyList<CustomTableTextRunStyle> textRuns, int textLength)
    {
        if (formattedText == null || textRuns == null || textRuns.Count == 0 || textLength <= 0)
        {
            return;
        }
        foreach (CustomTableTextRunStyle run in textRuns)
        {
            if (run.Foreground == null || run.Length <= 0 || run.StartIndex >= textLength)
            {
                continue;
            }
            int length = Math.Min(run.Length, textLength - run.StartIndex);
            if (length > 0)
            {
                formattedText.SetForegroundBrush(run.Foreground, run.StartIndex, length);
            }
        }
    }

    private static string CreateTextRunsCacheKey(IReadOnlyList<CustomTableTextRunStyle> textRuns, int textLength)
    {
        if (textRuns == null || textRuns.Count == 0)
        {
            return string.Empty;
        }
        var parts = new List<string>(textRuns.Count);
        foreach (CustomTableTextRunStyle run in textRuns)
        {
            if (run.Foreground == null || run.Length <= 0 || run.StartIndex >= textLength)
            {
                continue;
            }
            int length = Math.Min(run.Length, textLength - run.StartIndex);
            if (length <= 0)
            {
                continue;
            }
            parts.Add(run.StartIndex.ToString(CultureInfo.InvariantCulture)
                + ":"
                + length.ToString(CultureInfo.InvariantCulture)
                + ":"
                + GetBrushCacheKey(run.Foreground));
        }
        return string.Join("|", parts);
    }

    private static string GetBrushCacheKey(Brush brush)
    {
        return brush is SolidColorBrush solidColorBrush
            ? "c:" + solidColorBrush.Color.ToString(CultureInfo.InvariantCulture)
            : "r:" + RuntimeHelpers.GetHashCode(brush).ToString(CultureInfo.InvariantCulture);
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
            string textRunsKey,
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
            TextRunsKey = textRunsKey ?? string.Empty;
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

        private string TextRunsKey { get; }

        private double PixelsPerDip { get; }

        private string CultureName { get; }

        internal static Key Create(string text, double maxTextWidth, double maxTextHeight, TextAlignment alignment, CustomTableTextStyle textStyle, FontFamily scoreFontFamily, Brush foreground, string textRunsKey, double pixelsPerDip, CultureInfo culture)
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
                textRunsKey,
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
                && TextRunsKey == other.TextRunsKey
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
                hash = (hash * 31) + TextRunsKey.GetHashCode();
                hash = (hash * 31) + PixelsPerDip.GetHashCode();
                hash = (hash * 31) + CultureName.GetHashCode();
                return hash;
            }
        }
    }
}
