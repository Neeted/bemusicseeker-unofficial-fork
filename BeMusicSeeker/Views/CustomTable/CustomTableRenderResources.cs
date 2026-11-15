using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using BeMusicSeeker.Models;
using Ribbit.Logging;
using D2D = Vortice.Direct2D1;
using DWrite = Vortice.DirectWrite;
using Wic = Vortice.WIC;

namespace BeMusicSeeker.Views;

/// <summary>一覧の文字を描く書体の種別です。文字のレイアウトの保持と、整形方式の選択に使います。</summary>
internal enum CustomTableTextKind
{
    /// <summary>表示フォントの通常の文字です。GDI互換の方式で整形します。</summary>
    Normal,

    /// <summary>表示フォントの太字です。GDI互換の方式で整形します。</summary>
    Bold,

    /// <summary>スコア用のフォントの文字です。通常の方式で整形し、描く位置を画素へ丸めません。</summary>
    Score
}

/// <summary>表示フォントの解決結果です。書式のファミリー名と、字形のない文字を補う代替の規則を持ちます。</summary>
/// <param name="FamilyName">書式のファミリーにするシステムのフォント名。</param>
/// <param name="Fallback">字形のない文字を既定のフォント、システムの代替の順に補う規則。使えない環境ではnull。</param>
internal sealed record CustomTableResolvedFont(string FamilyName, DWrite.IDWriteFontFallback Fallback);

/// <summary>メモリ上から読み込んだスコア用のフォントです。</summary>
/// <param name="Collection">読み込んだフォントだけを持つ集合。</param>
/// <param name="FamilyName">集合の中のファミリー名。</param>
internal sealed record CustomTableScoreFont(DWrite.IDWriteFontCollection Collection, string FamilyName);

/// <summary>
/// 一覧の描画に使う、プロセスで共有する資源を持ちます。ファクトリ、フォントの解決結果、同梱フォントの読込み、線の端の形がこれに当たり、
/// 表ごと・描画ごとの資源は持ちません。DirectWriteは共有ファクトリ、Direct2Dはマルチスレッド用のファクトリで作るため、
/// 型の初期化後はどのスレッドからも使えます。アプリ終了専用の解放は行わず、プロセスの終了に任せます。
/// </summary>
internal static class CustomTableRenderResources
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, CustomTableResolvedFont> ResolvedFonts = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<FontFamily, ScoreFontHolder> ScoreFonts = new();
    private static DWrite.IDWriteFontFallback defaultFallback;
    private static bool defaultFallbackCreated;

    static CustomTableRenderResources()
    {
        DWriteFactory = DWrite.DWrite.DWriteCreateFactory<DWrite.IDWriteFactory>(DWrite.FactoryType.Shared);
        SystemFonts = DWriteFactory.GetSystemFontCollection(false);
        D2DFactory = D2D.D2D1.D2D1CreateFactory<D2D.ID2D1Factory1>(D2D.FactoryType.MultiThreaded, D2D.DebugLevel.None);
        WicFactory = new Wic.IWICImagingFactory();
        SquareCapStroke = D2DFactory.CreateStrokeStyle(new D2D.StrokeStyleProperties
        {
            StartCap = D2D.CapStyle.Square,
            EndCap = D2D.CapStyle.Square,
            DashCap = D2D.CapStyle.Flat,
            LineJoin = D2D.LineJoin.Miter,
            MiterLimit = 10f,
            DashStyle = D2D.DashStyle.Solid
        });
        RoundCapStroke = D2DFactory.CreateStrokeStyle(new D2D.StrokeStyleProperties
        {
            StartCap = D2D.CapStyle.Round,
            EndCap = D2D.CapStyle.Round,
            DashCap = D2D.CapStyle.Flat,
            LineJoin = D2D.LineJoin.Round,
            MiterLimit = 10f,
            DashStyle = D2D.DashStyle.Solid
        });
    }

    /// <summary>文字の整形に使うDirectWriteの共有ファクトリです。</summary>
    internal static DWrite.IDWriteFactory DWriteFactory { get; }

    /// <summary>描画先、デバイス、図形を作るDirect2Dのファクトリです。</summary>
    internal static D2D.ID2D1Factory1 D2DFactory { get; }

    /// <summary>表示面のビットマップを作るWICのファクトリです。</summary>
    internal static Wic.IWICImagingFactory WicFactory { get; }

    /// <summary>端を四角にした線の形です。ダウンロードの印に使います。</summary>
    internal static D2D.ID2D1StrokeStyle SquareCapStroke { get; }

    /// <summary>端と接合を丸くした線の形です。状態の印に使います。</summary>
    internal static D2D.ID2D1StrokeStyle RoundCapStroke { get; }

    private static DWrite.IDWriteFontCollection SystemFonts { get; }

    /// <summary>
    /// 表示フォントを解決します。第一候補がシステムのフォント集合にあればそれを書式のファミリーにし、なければ既定のフォントにします。
    /// 候補の並びの規則は <see cref="AppFontService"/> が持ちます。
    /// </summary>
    /// <param name="fontFamily">一覧に設定された表示フォント。</param>
    internal static CustomTableResolvedFont ResolveTextFont(FontFamily fontFamily)
    {
        IReadOnlyList<string> names = AppFontService.GetCandidateFamilyNames(fontFamily);
        string key = string.Join("\u0001", names);
        lock (Gate)
        {
            if (ResolvedFonts.TryGetValue(key, out CustomTableResolvedFont resolved))
            {
                return resolved;
            }
            string familyName = names.Count > 0 && SystemFonts.FindFamilyName(names[0], out _)
                ? names[0]
                : AppFontService.DefaultFontFamilyName;
            resolved = new CustomTableResolvedFont(familyName, GetDefaultFallback());
            ResolvedFonts.Add(key, resolved);
            return resolved;
        }
    }

    /// <summary>
    /// スコア用のフォントを、WPFの字形の書体が持つバイト列からメモリ上で読み込みます。結果はプロセスで <see cref="FontFamily"/> ごとに一度だけ保持します。
    /// APIがない環境や、字形を得られない書体は読み込めないものとして扱い、nullを返します。呼び出し側は表示フォントで描きます。
    /// </summary>
    /// <param name="fontFamily">スコア用のフォント。nullは読み込めないものとして扱います。</param>
    internal static CustomTableScoreFont TryGetScoreFont(FontFamily fontFamily)
    {
        if (fontFamily == null)
        {
            return null;
        }
        lock (Gate)
        {
            if (!ScoreFonts.TryGetValue(fontFamily, out ScoreFontHolder holder))
            {
                holder = new ScoreFontHolder(LoadScoreFont(fontFamily));
                ScoreFonts.Add(fontFamily, holder);
            }
            return holder.Font;
        }
    }

    /// <summary>
    /// 文字の書式を作ります。行の高さと基線は、渡されたWPFのフォントの値（文字サイズに対する倍率）に文字サイズを掛けた固定値にし、
    /// フォントが申告する行の寸法を使いません。
    /// </summary>
    /// <param name="font">表示フォントの解決結果。</param>
    /// <param name="scoreFont">スコア用のフォント。<paramref name="kind"/> がスコアで読み込めたときだけ使います。</param>
    /// <param name="kind">書体の種別。</param>
    /// <param name="sizePx">文字サイズ（物理画素）。</param>
    /// <param name="locale">言語タグ。</param>
    /// <param name="lineSpacing">行の高さ（文字サイズに対する倍率）。</param>
    /// <param name="baseline">基線の位置（文字サイズに対する倍率）。</param>
    /// <param name="lineHeightPx">固定した行の高さ（物理画素）。</param>
    internal static DWrite.IDWriteTextFormat CreateTextFormat(
        CustomTableResolvedFont font,
        CustomTableScoreFont scoreFont,
        CustomTableTextKind kind,
        float sizePx,
        string locale,
        double lineSpacing,
        double baseline,
        out float lineHeightPx)
    {
        bool useScoreFont = kind == CustomTableTextKind.Score && scoreFont != null;
        DWrite.IDWriteTextFormat format = DWriteFactory.CreateTextFormat(
            useScoreFont ? scoreFont.FamilyName : font.FamilyName,
            useScoreFont ? scoreFont.Collection : null,
            kind == CustomTableTextKind.Bold ? DWrite.FontWeight.Bold : DWrite.FontWeight.Normal,
            DWrite.FontStyle.Normal,
            DWrite.FontStretch.Normal,
            sizePx,
            locale);
        format.WordWrapping = DWrite.WordWrapping.NoWrap;
        format.ParagraphAlignment = DWrite.ParagraphAlignment.Near;
        format.TextAlignment = DWrite.TextAlignment.Leading;
        lineHeightPx = (float)(lineSpacing * sizePx);
        // 基線は画素の境界へ置き、同じ文字が整数の位置の移動だけで同じ画素の並びになるようにする。
        format.SetLineSpacing(DWrite.LineSpacingMethod.Uniform, lineHeightPx, (float)Math.Round(baseline * sizePx, MidpointRounding.AwayFromZero));
        if (font.Fallback != null)
        {
            using DWrite.IDWriteTextFormat1 format1 = format.QueryInterfaceOrNull<DWrite.IDWriteTextFormat1>();
            if (format1 != null)
            {
                format1.FontFallback = font.Fallback;
            }
        }
        return format;
    }

    /// <summary>省略記号の挿入に使う印を作ります。書式ごとに一つ作り、同じ書式のレイアウトで共有します。</summary>
    internal static DWrite.IDWriteInlineObject CreateEllipsisSign(DWrite.IDWriteTextFormat format)
    {
        return DWriteFactory.CreateEllipsisTrimmingSign(format);
    }

    private static DWrite.IDWriteFontFallback GetDefaultFallback()
    {
        if (defaultFallbackCreated)
        {
            return defaultFallback;
        }
        defaultFallbackCreated = true;
        IntPtr namePointer = IntPtr.Zero;
        IntPtr arrayPointer = IntPtr.Zero;
        try
        {
            using DWrite.IDWriteFactory2 factory2 = DWriteFactory.QueryInterfaceOrNull<DWrite.IDWriteFactory2>();
            if (factory2 == null)
            {
                return null;
            }
            using DWrite.IDWriteFontFallbackBuilder builder = factory2.CreateFontFallbackBuilder();
            namePointer = Marshal.StringToHGlobalUni(AppFontService.DefaultFontFamilyName);
            arrayPointer = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(arrayPointer, namePointer);
            // 選択されたフォントに字形がない文字は、全Unicode範囲を既定のフォントへ、それでも足りなければシステムの代替へ回す。
            builder.AddMapping([new DWrite.UnicodeRange { First = 0u, Last = 0x10FFFFu }], 1u, arrayPointer, 1u, null, null, null, 1f);
            builder.AddMappings(factory2.SystemFontFallback);
            defaultFallback = builder.CreateFontFallback();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TryLogWarning(exception, "custom table font fallback");
            defaultFallback = null;
        }
        finally
        {
            if (arrayPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(arrayPointer);
            }
            if (namePointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(namePointer);
            }
        }
        return defaultFallback;
    }

    private static CustomTableScoreFont LoadScoreFont(FontFamily fontFamily)
    {
        IntPtr memory = IntPtr.Zero;
        bool loaded = false;
        try
        {
            using DWrite.IDWriteFactory5 factory5 = DWriteFactory.QueryInterfaceOrNull<DWrite.IDWriteFactory5>();
            if (factory5 == null)
            {
                return null;
            }
            var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out GlyphTypeface glyphTypeface))
            {
                return null;
            }
            byte[] data;
            using (Stream stream = glyphTypeface.GetFontStream())
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                data = buffer.ToArray();
            }
            if (data.Length == 0)
            {
                return null;
            }
            // 読み込めた集合はプロセスの終了まで使うため、字形のデータは成功したときだけ解放しない。
            memory = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, memory, data.Length);
            DWrite.IDWriteInMemoryFontFileLoader loader = factory5.CreateInMemoryFontFileLoader();
            DWriteFactory.RegisterFontFileLoader(loader);
            using DWrite.IDWriteFontFile file = loader.CreateInMemoryFontFileReference(DWriteFactory, memory, (uint)data.Length, null);
            using DWrite.IDWriteFontSetBuilder1 builder = factory5.CreateFontSetBuilder();
            builder.AddFontFile(file);
            using DWrite.IDWriteFontSet fontSet = builder.CreateFontSet();
            DWrite.IDWriteFontCollection collection = factory5.CreateFontCollectionFromFontSet(fontSet);
            try
            {
                if (collection.FontFamilyCount == 0)
                {
                    return null;
                }
                using DWrite.IDWriteFontFamily family = collection.GetFontFamily(0);
                using DWrite.IDWriteLocalizedStrings names = family.FamilyNames;
                string familyName = names.GetString(0);
                if (string.IsNullOrEmpty(familyName))
                {
                    return null;
                }
                loaded = true;
                return new CustomTableScoreFont(collection, familyName);
            }
            finally
            {
                // 使わない集合は、空で返る経路や例外の経路でも解放する。
                if (!loaded)
                {
                    collection.Dispose();
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TryLogWarning(exception, "custom table score font");
            return null;
        }
        finally
        {
            if (!loaded && memory != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(memory);
            }
        }
    }

    private static void TryLogWarning(Exception exception, string message)
    {
        try { NLogWrapper.FileLogger?.Warn(exception, message); }
        catch { /* 診断の失敗で表示フォントへの切替えを妨げない。 */ }
    }

    private sealed class ScoreFontHolder(CustomTableScoreFont font)
    {
        internal CustomTableScoreFont Font { get; } = font;
    }
}
