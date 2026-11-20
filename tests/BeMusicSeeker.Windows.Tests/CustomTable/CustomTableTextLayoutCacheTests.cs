using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;

/// <summary>文字のレイアウトの保持（キー、追い出し、前提の変更による破棄、整形方式）を単体で確かめます。描画結果は <see cref="CustomTableRenderingTests"/> が担当します。</summary>
[TestClass]
public sealed class CustomTableTextLayoutCacheTests
{
    private static FontFamily ScoreFont => new(new Uri("pack://application:,,,/BeMusicSeeker;component/Assets/Fonts/"), "./#Sovjet Box");

    /// <summary>同じ文字列・書体・省略の幅で再取得すると同じレイアウトを再利用し、省略しない文字は配置が違っても再利用することを確認します。反復描画のたびにレイアウトを作る誤りと、配置をキーに含めて同じ文字列のレイアウトが増える誤りを検出します。</summary>
    [TestMethod]
    public void GetOrCreate_ReusesLayoutForSameKeyRegardlessOfAlignmentWhenNotTrimmed()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using CustomTableTextLayoutCache cache = Create();
            CustomTableTextLayout first = cache.GetOrCreate("AAA", CustomTableTextKind.Normal, TextAlignment.Left, 0, out bool firstHit);
            CustomTableTextLayout second = cache.GetOrCreate("AAA", CustomTableTextKind.Normal, TextAlignment.Left, 0, out bool secondHit);
            CustomTableTextLayout centered = cache.GetOrCreate("AAA", CustomTableTextKind.Normal, TextAlignment.Center, 0, out bool centeredHit);
            CustomTableTextLayout right = cache.GetOrCreate("AAA", CustomTableTextKind.Normal, TextAlignment.Right, -5, out bool rightHit);

            Assert.IsFalse(firstHit);
            Assert.IsTrue(secondHit && centeredHit && rightHit);
            Assert.AreSame(first, second);
            Assert.AreSame(first, centered);
            Assert.AreSame(first, right);
            Assert.AreEqual(1, cache.Count);
        });
    }

    /// <summary>文字列・書体の種別・省略に使う幅・（省略するときの）配置のどれか一つだけを変えると、レイアウトを再利用しないことを確認します。キーから条件を落として別の文字のレイアウトを描く誤りを検出します。</summary>
    [TestMethod]
    public void GetOrCreate_KeyIncludesTextKindTrimWidthAndAlignmentWhenTrimmed()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            AssertMiss(cache => cache.GetOrCreate("AAA", CustomTableTextKind.Normal, TextAlignment.Left, 0, out _), cache => { cache.GetOrCreate("AA", CustomTableTextKind.Normal, TextAlignment.Left, 0, out bool hit); return hit; });
            AssertMiss(cache => cache.GetOrCreate("AAA", CustomTableTextKind.Normal, TextAlignment.Left, 0, out _), cache => { cache.GetOrCreate("AAA", CustomTableTextKind.Bold, TextAlignment.Left, 0, out bool hit); return hit; });
            AssertMiss(cache => cache.GetOrCreate("AAA", CustomTableTextKind.Normal, TextAlignment.Left, 0, out _), cache => { cache.GetOrCreate("AAA", CustomTableTextKind.Score, TextAlignment.Left, 0, out bool hit); return hit; });
            AssertMiss(cache => cache.GetOrCreate("AAA", CustomTableTextKind.Normal, TextAlignment.Left, 0, out _), cache => { cache.GetOrCreate("AAA", CustomTableTextKind.Normal, TextAlignment.Left, 30, out bool hit); return hit; });
            AssertMiss(cache => cache.GetOrCreate("AAA", CustomTableTextKind.Normal, TextAlignment.Left, 30, out _), cache => { cache.GetOrCreate("AAA", CustomTableTextKind.Normal, TextAlignment.Left, 31, out bool hit); return hit; });
            AssertMiss(cache => cache.GetOrCreate("AAA", CustomTableTextKind.Normal, TextAlignment.Left, 30, out _), cache => { cache.GetOrCreate("AAA", CustomTableTextKind.Normal, TextAlignment.Center, 30, out bool hit); return hit; });
        });
    }

    /// <summary>容量二件のキャッシュへ三種類を入れると、再参照済みでも最古の挿入を退役して二件を保ち、追い出したレイアウトを解放することを確認します。容量超過や保持順の誤りと、解放漏れを検出します。</summary>
    [TestMethod]
    public void GetOrCreate_EvictsOldestEntryAndDisposesIt()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using CustomTableTextLayoutCache cache = Create(maxEntryCount: 2);
            CustomTableTextLayout evicted = cache.GetOrCreate("A", CustomTableTextKind.Normal, TextAlignment.Left, 0, out _);
            cache.GetOrCreate("B", CustomTableTextKind.Normal, TextAlignment.Left, 0, out _);
            cache.GetOrCreate("A", CustomTableTextKind.Normal, TextAlignment.Left, 0, out bool existingHit);
            cache.GetOrCreate("C", CustomTableTextKind.Normal, TextAlignment.Left, 0, out _);
            cache.GetOrCreate("A", CustomTableTextKind.Normal, TextAlignment.Left, 0, out bool evictedHit);

            Assert.IsTrue(existingHit);
            Assert.IsFalse(evictedHit);
            Assert.AreEqual(2, cache.Count);
            Assert.AreEqual(IntPtr.Zero, evicted.Layout.NativePointer, "追い出したレイアウトは解放します。");
        });
    }

    /// <summary>
    /// 上限の引き上げは下限としてだけ働き、下げないことを、上限を超えて入れたときに保持する件数で確認します。
    /// 保持範囲を常に上回る上限にする規則が、上限を縮める誤りや引き上げが効かない誤りで崩れることを検出します。
    /// </summary>
    [TestMethod]
    public void EnsureCapacity_RaisesTheLimitButNeverLowersIt()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            void Insert(CustomTableTextLayoutCache cache, int from, int count)
            {
                for (int index = from; index < from + count; index++) { cache.GetOrCreate("T" + index, CustomTableTextKind.Normal, TextAlignment.Left, 0, out _); }
            }
            using CustomTableTextLayoutCache cache = Create(maxEntryCount: 4);
            cache.EnsureCapacity(2);
            Insert(cache, 0, 6);
            Assert.AreEqual(4, cache.Count, "小さい値の指定では上限を下げません。");

            cache.EnsureCapacity(100);
            Insert(cache, 6, 100);
            Assert.AreEqual(100, cache.Count, "引き上げた上限まで保持します。");

            cache.EnsureCapacity(10);
            Insert(cache, 106, 1);
            Assert.AreEqual(100, cache.Count, "小さい値の指定のあとも、引き上げた上限を保ちます。");
            // T6 だけが追い出され、T7 から T106 の100件が残る。
            cache.GetOrCreate("T7", CustomTableTextKind.Normal, TextAlignment.Left, 0, out bool retainedHit);
            Assert.IsTrue(retainedHit, "上限の範囲に残る文字列は追い出しません。");
        });
    }

    /// <summary>フォント・文字サイズ・表示倍率・表示言語・文化圏のどれか一つが変わると保持したレイアウトをすべて破棄し、同じ前提なら破棄しないことを確認します。古い前提で整形したレイアウトを使い続ける誤りを検出します。</summary>
    [TestMethod]
    public void Configure_DiscardsEveryLayoutWhenAnyInputChanges()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CultureInfo en = CultureInfo.GetCultureInfo("en-US"), ja = CultureInfo.GetCultureInfo("ja-JP");
            FontFamily arial = AppFontService.CreateFontFamily("Arial", AppFontRole.Table);
            CustomTableTextEnvironment baseline = Environment(AppFontService.DefaultTableFontFamily, null, 11d, 1d, en, en);
            CustomTableTextEnvironment[] changes =
            [
                Environment(arial, null, 11d, 1d, en, en),
                Environment(AppFontService.DefaultTableFontFamily, null, 12d, 1d, en, en),
                Environment(AppFontService.DefaultTableFontFamily, null, 11d, 1.25d, en, en),
                Environment(AppFontService.DefaultTableFontFamily, null, 11d, 1d, en, ja),
                Environment(AppFontService.DefaultTableFontFamily, null, 11d, 1d, ja, en),
                Environment(AppFontService.DefaultTableFontFamily, ScoreFont, 11d, 1d, en, en)
            ];
            foreach (CustomTableTextEnvironment changed in changes)
            {
                using CustomTableTextLayoutCache cache = Create();
                cache.Configure(baseline);
                CustomTableTextLayout layout = cache.GetOrCreate("AAA", CustomTableTextKind.Normal, TextAlignment.Left, 0, out _);
                Assert.IsFalse(cache.Configure(Environment(AppFontService.DefaultTableFontFamily, null, 11d, 1d, en, en)), "同じ前提では破棄しません。");
                Assert.AreEqual(1, cache.Count);
                Assert.IsTrue(cache.Configure(changed));
                Assert.AreEqual(0, cache.Count);
                Assert.AreEqual(IntPtr.Zero, layout.Layout.NativePointer);
                cache.GetOrCreate("AAA", CustomTableTextKind.Normal, TextAlignment.Left, 0, out bool hit);
                Assert.IsFalse(hit);
            }
        });
    }

    /// <summary>通常の文字は、自然な幅が物理画素の整数になるGDI互換の方式で整形することを、100%と125%で確認します。表示倍率に合わせない小数の幅になる誤りを検出します。</summary>
    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.25d)]
    public void GetOrCreate_FormatsNormalTextWithWholePixelWidths(double pixelsPerDip)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using CustomTableTextLayoutCache cache = Create(pixelsPerDip: pixelsPerDip);
            foreach (CustomTableTextKind kind in new[] { CustomTableTextKind.Normal, CustomTableTextKind.Bold })
            {
                foreach (string text in new[] { "The quick brown fox", "0123456789", "日本語のタイトル", "AAA", "mmmm WWWW iiii" })
                {
                    double width = cache.GetOrCreate(text, kind, TextAlignment.Left, 0, out _).NaturalWidth;
                    Assert.IsTrue(width > 0d);
                    Assert.AreEqual(Math.Round(width), width, 0d, kind + ": " + text + " " + width);
                }
            }
        });
    }

    /// <summary>スコア用の文字は通常の方式で整形し、WPFの既定の方式（Ideal）の幅に近く、画素に合わせる方式（Display）の幅へは寄らないことを確認します。スコア用の文字を画素に合わせて線が細く描かれる誤りを検出します。</summary>
    [TestMethod]
    public void GetOrCreate_FormatsScoreTextWithIdealWidths()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            FontFamily scoreFont = ScoreFont;
            // 代わりのフォントへ解決されたままでは、本番のスコア表示を再現しない。
            StringAssert.Contains(GlyphFontProbe.FontUri(scoreFont, "A").ToLowerInvariant(), "sovjetbox");
            string text = string.Join(" ", Enumerable.Repeat("FULLCOMBO AAA HARD 1234", 6));
            var typeface = new Typeface(scoreFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            double Reference(TextFormattingMode mode) => new FormattedText(text, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight, typeface, 12d,
                Brushes.Black, null, mode, 1d).WidthIncludingTrailingWhitespace;
            double ideal = Reference(TextFormattingMode.Ideal), display = Reference(TextFormattingMode.Display);
            Assert.IsTrue(Math.Abs(ideal - display) >= 2d, "二つの方式で幅が違う文字列であることが前提です。");

            using CustomTableTextLayoutCache cache = Create(fontSize: 12d, score: scoreFont);
            double width = cache.GetOrCreate(text, CustomTableTextKind.Score, TextAlignment.Left, 0, out _).NaturalWidth;
            Assert.IsTrue(Math.Abs(width - ideal) < Math.Abs(width - display), $"Score={width} Ideal={ideal} Display={display}");
        });
    }

    /// <summary>省略の判定が、自然な幅と使える幅の比較（超えるときだけ省略）で、境界の前後の整数幅で入れ替わることを確認します。判定の境界が一画素ずれる誤りを検出します。</summary>
    [TestMethod]
    public void ExceedsAvailableWidth_SwitchesAtTheNaturalWidth()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using CustomTableTextLayoutCache cache = Create();
            int natural = (int)cache.GetOrCreate("Very very long title text", CustomTableTextKind.Normal, TextAlignment.Left, 0, out _).NaturalWidth;
            Assert.IsTrue(CustomTableTextLayoutCache.ExceedsAvailableWidth(natural, natural - 1));
            Assert.IsFalse(CustomTableTextLayoutCache.ExceedsAvailableWidth(natural, natural));
            Assert.IsFalse(CustomTableTextLayoutCache.ExceedsAvailableWidth(natural, natural + 1));
            Assert.IsFalse(CustomTableTextLayoutCache.ExceedsAvailableWidth(cache.GetOrCreate("Short", CustomTableTextKind.Normal, TextAlignment.Left, 0, out _).NaturalWidth, 200));
        });
    }

    /// <summary>単語の区切りを含む長い文字列を、省略に使う幅の狭いレイアウトにしても一行のままで、改行を含む文字列は最初の行だけを整形することを確認します。折り返して複数行になる誤りと、二行目を描く誤りを検出します。</summary>
    [TestMethod]
    public void GetOrCreate_KeepsSingleLineWhenTrimmedAndCutsAtLineBreak()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            const string text = "one two three four five six seven eight nine ten";
            using CustomTableTextLayoutCache cache = Create();
            CustomTableTextLayout single = cache.GetOrCreate(text, CustomTableTextKind.Normal, TextAlignment.Left, 0, out _);
            CustomTableTextLayout narrow = cache.GetOrCreate(text, CustomTableTextKind.Normal, TextAlignment.Left, 60, out _);
            Assert.AreEqual(1u, single.Layout.Metrics.LineCount);
            Assert.AreEqual(1u, narrow.Layout.Metrics.LineCount);
            Assert.AreEqual(single.Layout.Metrics.Height, narrow.Layout.Metrics.Height, 0.01d);
            Assert.IsTrue(narrow.Layout.Metrics.WidthIncludingTrailingWhitespace <= 60d, narrow.Layout.Metrics.WidthIncludingTrailingWhitespace.ToString(CultureInfo.InvariantCulture));

            CustomTableTextLayout broken = cache.GetOrCreate("first\r\nsecond", CustomTableTextKind.Normal, TextAlignment.Left, 0, out _);
            Assert.AreEqual(5, broken.TextLength);
            Assert.AreEqual(cache.GetOrCreate("first", CustomTableTextKind.Normal, TextAlignment.Left, 0, out _).NaturalWidth, broken.NaturalWidth, 0d);
        });
    }

    private static void AssertMiss(Action<CustomTableTextLayoutCache> seed, Func<CustomTableTextLayoutCache, bool> candidate)
    {
        using CustomTableTextLayoutCache cache = Create();
        seed(cache);
        Assert.IsFalse(candidate(cache));
    }

    private static CustomTableTextLayoutCache Create(int maxEntryCount = CustomTableTextLayoutCache.DefaultMaxEntryCount, double fontSize = 11d, double pixelsPerDip = 1d, FontFamily? score = null)
    {
        var cache = new CustomTableTextLayoutCache(maxEntryCount);
        var culture = CultureInfo.GetCultureInfo("en-US");
        cache.Configure(Environment(AppFontService.DefaultTableFontFamily, score, fontSize, pixelsPerDip, culture, culture));
        return cache;
    }

    private static CustomTableTextEnvironment Environment(FontFamily text, FontFamily? score, double fontSize, double pixelsPerDip, CultureInfo culture, CultureInfo uiCulture)
    {
        return new CustomTableTextEnvironment(text, score!, fontSize, pixelsPerDip, culture, uiCulture);
    }
}
