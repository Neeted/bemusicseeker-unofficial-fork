using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DWrite = Vortice.DirectWrite;

namespace BeMusicSeeker.Views;

/// <summary>文字のレイアウトを作るときの前提です。これが変わったら、保持するレイアウトをすべて作り直します。</summary>
internal sealed class CustomTableTextEnvironment
{
    /// <summary>文字のレイアウトを作る前提を指定します。</summary>
    /// <param name="textFontFamily">通常の文字と太字に使う表示フォント。</param>
    /// <param name="scoreFontFamily">スコア用のフォント。nullは表示フォントで描きます。</param>
    /// <param name="fontSize">文字サイズ（DIP）。</param>
    /// <param name="pixelsPerDip">表示倍率。</param>
    /// <param name="culture">数値の書式に使う文化圏。</param>
    /// <param name="uiCulture">表示言語の文化圏。</param>
    internal CustomTableTextEnvironment(FontFamily textFontFamily, FontFamily scoreFontFamily, double fontSize, double pixelsPerDip, CultureInfo culture, CultureInfo uiCulture)
    {
        TextFontFamily = textFontFamily;
        ScoreFontFamily = scoreFontFamily;
        FontSize = fontSize;
        PixelsPerDip = pixelsPerDip;
        Culture = culture;
        UICulture = uiCulture;
    }

    internal FontFamily TextFontFamily { get; }

    internal FontFamily ScoreFontFamily { get; }

    internal double FontSize { get; }

    internal double PixelsPerDip { get; }

    internal CultureInfo Culture { get; }

    internal CultureInfo UICulture { get; }

    /// <summary>同じ前提かを返します。同名でも数値書式を変更した文化圏へ交換できるため、文化圏は参照で比べます。</summary>
    internal bool IsSameAs(CustomTableTextEnvironment other)
    {
        return other != null
            && Equals(TextFontFamily, other.TextFontFamily)
            && Equals(ScoreFontFamily, other.ScoreFontFamily)
            && FontSize.Equals(other.FontSize)
            && PixelsPerDip.Equals(other.PixelsPerDip)
            && ReferenceEquals(Culture, other.Culture)
            && ReferenceEquals(UICulture, other.UICulture);
    }
}

/// <summary>保持している文字のレイアウト一つです。省略の判定に使う自然な幅を併せて持ちます。</summary>
internal sealed class CustomTableTextLayout
{
    internal CustomTableTextLayout(DWrite.IDWriteTextLayout layout, double naturalWidth, double lineHeight, int textLength)
    {
        Layout = layout;
        NaturalWidth = naturalWidth;
        LineHeight = lineHeight;
        TextLength = textLength;
    }

    /// <summary>描画に使うレイアウトです。キャッシュから追い出されると解放されます。</summary>
    internal DWrite.IDWriteTextLayout Layout { get; }

    /// <summary>幅の制限なしで整形した一行の幅（物理画素）です。省略するレイアウトでは0です。</summary>
    internal double NaturalWidth { get; }

    /// <summary>固定した行の高さ（物理画素）です。</summary>
    internal double LineHeight { get; }

    /// <summary>整形した文字数です。改行より後ろは含みません。</summary>
    internal int TextLength { get; }
}

/// <summary>
/// 文字のレイアウト（<c>IDWriteTextLayout</c>）を、文字列・書体の種別・省略に使う幅をキーに保持します。
/// 色・区間・位置・選択状態・横位置には依存しません。省略しない文字は幅の制限なしで作り、配置は描く側が自然な幅から求めます。
/// 省略が要る文字だけ、物理画素の整数幅と配置をキーに含む幅つきのレイアウトを別に持ちます。
/// 上限つきの先入れ先出しで、追い出すレイアウトは解放します。UIスレッドだけで使います。
/// </summary>
internal sealed class CustomTableTextLayoutCache : IDisposable
{
    internal const int DefaultMaxEntryCount = 8192;
    private const float UnlimitedWidth = 32768f;

    private readonly Dictionary<Key, CustomTableTextLayout> cache = [];
    private readonly Queue<Key> insertionOrder = new();
    private readonly FormatSlot[] formats = new FormatSlot[3];
    private int maxEntryCount;
    private CustomTableTextEnvironment environment;
    private CustomTableResolvedFont resolvedFont;

    /// <summary>上限を指定して作ります。</summary>
    internal CustomTableTextLayoutCache(int maxEntryCount = DefaultMaxEntryCount)
    {
        this.maxEntryCount = Math.Max(1, maxEntryCount);
    }

    /// <summary>保持しているレイアウトの数です。</summary>
    internal int Count => cache.Count;

    /// <summary>
    /// レイアウトを作る前提を設定します。前回と違う前提ならすべて作り直すため破棄し、破棄したときはtrueを返します。
    /// </summary>
    internal bool Configure(CustomTableTextEnvironment next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (environment != null && environment.IsSameAs(next))
        {
            return false;
        }
        Clear();
        environment = next;
        resolvedFont = CustomTableRenderResources.ResolveTextFont(next.TextFontFamily);
        return true;
    }

    /// <summary>
    /// 保持できる数の下限を引き上げます。保持範囲（可視行と上下の余白の行）の可視列のセルと見出しは、
    /// 一つのセルが幅なしと幅つきの二つのレイアウトを持ち得るため、その二倍を常に保持できる値にします。
    /// 上限は下げません。
    /// </summary>
    /// <param name="requiredCount">保持したいレイアウトの数。</param>
    internal void EnsureCapacity(int requiredCount)
    {
        maxEntryCount = Math.Max(maxEntryCount, requiredCount);
    }

    /// <summary>保持しているレイアウトと書式をすべて解放します。</summary>
    internal void Clear()
    {
        foreach (CustomTableTextLayout layout in cache.Values)
        {
            layout.Layout.Dispose();
        }
        cache.Clear();
        insertionOrder.Clear();
        for (int i = 0; i < formats.Length; i++)
        {
            formats[i]?.Dispose();
            formats[i] = null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Clear();

    /// <summary>
    /// レイアウトを取得し、なければ作ります。取得したレイアウトは、次の取得・挿入までの間だけ使えます。
    /// </summary>
    /// <param name="text">描く文字列。改行より後ろは描きません。</param>
    /// <param name="kind">書体の種別。</param>
    /// <param name="alignment">配置。省略しない（<paramref name="trimWidthPx"/> が0）場合はキーに含めません。</param>
    /// <param name="trimWidthPx">省略に使う幅（物理画素の整数）。省略しない場合は0です。</param>
    /// <param name="hit">保持済みのレイアウトを再利用したときはtrue。</param>
    internal CustomTableTextLayout GetOrCreate(string text, CustomTableTextKind kind, TextAlignment alignment, int trimWidthPx, out bool hit)
    {
        if (environment == null)
        {
            throw new InvalidOperationException("The text environment is not configured.");
        }
        int width = Math.Max(0, trimWidthPx);
        TextAlignment effectiveAlignment = width > 0 && alignment != TextAlignment.Justify ? alignment : TextAlignment.Left;
        var key = new Key(text ?? string.Empty, kind, width, effectiveAlignment);
        if (cache.TryGetValue(key, out CustomTableTextLayout layout))
        {
            hit = true;
            return layout;
        }
        hit = false;
        layout = Create(key);
        cache.Add(key, layout);
        insertionOrder.Enqueue(key);
        TrimToCapacity();
        return layout;
    }

    /// <summary>ヒット率を返します。件数が0のときは-1です。</summary>
    internal static double CalculateHitRate(int hits, int misses)
    {
        int total = hits + misses;
        return total == 0 ? -1d : (double)hits / total;
    }

    /// <summary>描画とツールチップで共通の、省略するかの判定です。自然な幅が使える幅を超えるときに省略します。</summary>
    /// <param name="naturalWidth">幅の制限なしで整形した幅（物理画素）。</param>
    /// <param name="availableWidthPx">文字を描ける幅（物理画素）。</param>
    internal static bool ExceedsAvailableWidth(double naturalWidth, int availableWidthPx)
    {
        return naturalWidth > availableWidthPx;
    }

    private CustomTableTextLayout Create(Key key)
    {
        FormatSlot slot = GetFormat(key.Kind);
        string display = TruncateAtLineBreak(key.Text);
        float layoutWidth = key.TrimWidth > 0 ? key.TrimWidth : UnlimitedWidth;
        float lineHeight = slot.LineHeight;
        DWrite.IDWriteTextLayout layout = key.Kind == CustomTableTextKind.Score
            ? CustomTableRenderResources.DWriteFactory.CreateTextLayout(display, slot.Format, layoutWidth, lineHeight)
            // 通常の文字は、WPFの表示用の整形（画素に合わせる方式）に相当するGDI互換の方式で整形する。
            : CustomTableRenderResources.DWriteFactory.CreateGdiCompatibleTextLayout(display, (uint)display.Length, slot.Format, layoutWidth, lineHeight, 1f, null, false);
        double naturalWidth = 0d;
        if (key.TrimWidth > 0)
        {
            layout.TextAlignment = key.Alignment switch
            {
                TextAlignment.Center => DWrite.TextAlignment.Center,
                TextAlignment.Right => DWrite.TextAlignment.Trailing,
                _ => DWrite.TextAlignment.Leading
            };
            layout.SetTrimming(new DWrite.Trimming { Granularity = DWrite.TrimmingGranularity.Character }, slot.EllipsisSign);
        }
        else
        {
            naturalWidth = layout.Metrics.WidthIncludingTrailingWhitespace;
        }
        return new CustomTableTextLayout(layout, naturalWidth, lineHeight, display.Length);
    }

    private FormatSlot GetFormat(CustomTableTextKind kind)
    {
        FormatSlot slot = formats[(int)kind];
        if (slot != null)
        {
            return slot;
        }
        CustomTableScoreFont scoreFont = kind == CustomTableTextKind.Score ? CustomTableRenderResources.TryGetScoreFont(environment.ScoreFontFamily) : null;
        FontFamily metricsFamily = scoreFont != null ? environment.ScoreFontFamily : environment.TextFontFamily;
        string locale = string.IsNullOrEmpty(environment.UICulture?.Name) ? "en-us" : environment.UICulture.Name;
        DWrite.IDWriteTextFormat format = CustomTableRenderResources.CreateTextFormat(
            resolvedFont,
            scoreFont,
            kind,
            (float)(environment.FontSize * environment.PixelsPerDip),
            locale,
            metricsFamily.LineSpacing,
            metricsFamily.Baseline,
            out float lineHeight);
        slot = new FormatSlot(format, CustomTableRenderResources.CreateEllipsisSign(format), lineHeight);
        formats[(int)kind] = slot;
        return slot;
    }

    private void TrimToCapacity()
    {
        while (cache.Count > maxEntryCount && insertionOrder.Count > 0)
        {
            Key oldest = insertionOrder.Dequeue();
            if (cache.Remove(oldest, out CustomTableTextLayout evicted))
            {
                evicted.Layout.Dispose();
            }
        }
    }

    // 改行は、その前までを一行として描く。WPFの一行表示と同じく、後ろは見せない。
    private static readonly char NextLine = (char)0x0085;
    private static readonly char LineSeparator = (char)0x2028;
    private static readonly char ParagraphSeparator = (char)0x2029;

    private static string TruncateAtLineBreak(string text)
    {
        int index = text.AsSpan().IndexOfAny('\r', '\n');
        if (index < 0)
        {
            index = text.AsSpan().IndexOfAny(NextLine, LineSeparator, ParagraphSeparator);
        }
        return index < 0 ? text : text[..index];
    }

    private sealed class FormatSlot(DWrite.IDWriteTextFormat format, DWrite.IDWriteInlineObject ellipsisSign, float lineHeight) : IDisposable
    {
        internal DWrite.IDWriteTextFormat Format { get; } = format;

        internal DWrite.IDWriteInlineObject EllipsisSign { get; } = ellipsisSign;

        internal float LineHeight { get; } = lineHeight;

        public void Dispose()
        {
            EllipsisSign.Dispose();
            Format.Dispose();
        }
    }

    private readonly record struct Key(string Text, CustomTableTextKind Kind, int TrimWidth, TextAlignment Alignment);
}
