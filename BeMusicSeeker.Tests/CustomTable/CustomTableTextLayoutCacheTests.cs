using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomTableTextLayoutCacheTests
{
    /// <summary>同じ描画条件で文字配置を再取得すると同じ配置を再利用することを確認し、反復描画ごとに文字配置を作る性能上の誤りを検出します。</summary>
    [TestMethod]
    public void GetOrCreate_ReusesSameKey()
    {
        TestUiDispatcherHost.Invoke(delegate
        {
            var cache = new CustomTableTextLayoutCache();

            FormattedText first = Create(cache, out bool firstHit, text: "AAA");
            FormattedText second = Create(cache, out bool secondHit, text: "AAA");

            Assert.IsFalse(firstHit);
            Assert.IsTrue(secondHit);
            Assert.AreSame(first, second);
            Assert.AreEqual(1, cache.Count);
        });
    }

    /// <summary>文字・領域・整列・書体・部分色・DPI・文化の各条件だけを変えると配置を再利用しないことを確認し、描画条件をキャッシュキーから落とす誤りを検出します。</summary>
    [TestMethod]
    public void GetOrCreate_KeyIncludesLayoutAndStyleInputs()
    {
        TestUiDispatcherHost.Invoke(delegate
        {
            AssertKeyChangeMisses(cache => Create(cache, out _, text: "AAA"), cache => { Create(cache, out bool hit, text: "AA"); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, maxTextWidth: 80d), cache => { Create(cache, out bool hit, maxTextWidth: 81d); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, maxTextHeight: 18d), cache => { Create(cache, out bool hit, maxTextHeight: 19d); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, alignment: TextAlignment.Left), cache => { Create(cache, out bool hit, alignment: TextAlignment.Center); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, textStyle: CustomTableTextStyle.Normal), cache => { Create(cache, out bool hit, textStyle: CustomTableTextStyle.NormalBold); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, textStyle: CustomTableTextStyle.Normal), cache => { Create(cache, out bool hit, textStyle: CustomTableTextStyle.Score); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, textStyle: CustomTableTextStyle.Score), cache => { Create(cache, out bool hit, textStyle: CustomTableTextStyle.Score.WithFontSize(12d)); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, textStyle: CustomTableTextStyle.Score), cache => { Create(cache, out bool hit, textStyle: CustomTableTextStyle.Score, scoreFontFamily: new FontFamily("Arial")); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, foreground: Brushes.Black), cache => { Create(cache, out bool hit, foreground: Brushes.Red); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, textRuns: [new CustomTableTextRunStyle(0, 1, Brushes.Red)]), cache => { Create(cache, out bool hit, textRuns: [new CustomTableTextRunStyle(1, 1, Brushes.Red)]); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, textRuns: [new CustomTableTextRunStyle(0, 1, Brushes.Red)]), cache => { Create(cache, out bool hit, textRuns: [new CustomTableTextRunStyle(0, 1, Brushes.Blue)]); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, pixelsPerDip: 1d), cache => { Create(cache, out bool hit, pixelsPerDip: 1.25d); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, culture: CultureInfo.GetCultureInfo("en-US")), cache => { Create(cache, out bool hit, culture: CultureInfo.GetCultureInfo("ja-JP")); return hit; });
        });
    }

    /// <summary>部分色区間を含む同一描画条件でも文字配置を一件だけ再利用することを確認し、強調付き文字の反復描画でキャッシュを使わない誤りを検出します。</summary>
    [TestMethod]
    public void GetOrCreate_ReusesSameTextRunKey()
    {
        TestUiDispatcherHost.Invoke(delegate
        {
            var cache = new CustomTableTextLayoutCache();
            IReadOnlyList<CustomTableTextRunStyle> textRuns = [new CustomTableTextRunStyle(0, 1, Brushes.Red)];

            FormattedText first = Create(cache, out bool firstHit, textRuns: textRuns);
            FormattedText second = Create(cache, out bool secondHit, textRuns: textRuns);

            Assert.IsFalse(firstHit);
            Assert.IsTrue(secondHit);
            Assert.AreSame(first, second);
            Assert.AreEqual(1, cache.Count);
        });
    }

    /// <summary>旧値と新値を模した二つの色区間が実際の文字描画へ現れることを確認し、配置生成で部分色を無視する誤りを検出します。</summary>
    [TestMethod]
    public void GetOrCreate_AppliesTextRunForegrounds()
    {
        TestUiDispatcherHost.Invoke(delegate
        {
            var cache = new CustomTableTextLayoutCache();
            FormattedText text = Create(
                cache,
                out _,
                text: "MMMM    MMMM",
                maxTextWidth: 220d,
                maxTextHeight: 60d,
                textRuns:
                [
                    new CustomTableTextRunStyle(0, 4, Brushes.Red),
                    new CustomTableTextRunStyle(8, 4, Brushes.Blue)
                ]);

            int redPixels;
            int bluePixels;
            CountDominantRunPixels(text, out redPixels, out bluePixels);

            Assert.IsTrue(redPixels > 0, "Expected red pixels from the old-value text run.");
            Assert.IsTrue(bluePixels > 0, "Expected blue pixels from the new-value text run.");
        });
    }

    /// <summary>容量二件の配置キャッシュへ三種類を入れると再参照済みでも最古の挿入を退役して二件を保つことを確認し、容量超過や保持順の誤りを検出します。</summary>
    [TestMethod]
    public void GetOrCreate_EvictsOldestEntryWhenCapacityIsExceeded()
    {
        TestUiDispatcherHost.Invoke(delegate
        {
            var cache = new CustomTableTextLayoutCache(maxEntryCount: 2);
            Create(cache, out _, text: "A");
            Create(cache, out _, text: "B");
            Create(cache, out bool existingHit, text: "A");
            Create(cache, out _, text: "C");
            Create(cache, out bool evictedHit, text: "A");

            Assert.IsTrue(existingHit);
            Assert.IsFalse(evictedHit);
            Assert.AreEqual(2, cache.Count);
        });
    }

    /// <summary>描画要求なしは未知率、ヒットと失敗が一件ずつなら半分とすることを確認し、未測定を成功率として記録する誤りを検出します。</summary>
    [TestMethod]
    public void CalculateHitRate_ReturnsMinusOneWhenNoTextWasDrawn()
    {
        Assert.AreEqual(-1d, CustomTableTextLayoutCache.CalculateHitRate(0, 0));
        Assert.AreEqual(0.5d, CustomTableTextLayoutCache.CalculateHitRate(1, 1));
    }

    /// <summary>十分な幅の短文は省略不要、狭い幅の長文は省略必要と判定することを確認し、全文tooltipを出すための実書体計測の誤りを検出します。</summary>
    [TestMethod]
    public void WouldTrim_DetectsTextThatExceedsAvailableWidth()
    {
        TestUiDispatcherHost.Invoke(delegate
        {
            Assert.IsFalse(CustomTableTextLayoutCache.WouldTrim(
                "Short",
                200d,
                CustomTableTextStyle.Normal,
                null,
                1d,
                CultureInfo.GetCultureInfo("ja-JP")));
            Assert.IsTrue(CustomTableTextLayoutCache.WouldTrim(
                "Very very long title text",
                20d,
                CustomTableTextStyle.Normal,
                null,
                1d,
                CultureInfo.GetCultureInfo("ja-JP")));
        });
    }

    private static void AssertKeyChangeMisses(Action<CustomTableTextLayoutCache> seed, Func<CustomTableTextLayoutCache, bool> candidate)
    {
        var cache = new CustomTableTextLayoutCache();
        seed(cache);
        bool hit = candidate(cache);
        Assert.IsFalse(hit);
    }

    private static FormattedText Create(
        CustomTableTextLayoutCache cache,
        out bool hit,
        string text = "AAA",
        double maxTextWidth = 80d,
        double maxTextHeight = 18d,
        TextAlignment alignment = TextAlignment.Left,
        CustomTableTextStyle textStyle = null!,
        FontFamily scoreFontFamily = null!,
        Brush foreground = null!,
        IReadOnlyList<CustomTableTextRunStyle> textRuns = null!,
        double pixelsPerDip = 1d,
        CultureInfo culture = null!)
    {
        return cache.GetOrCreate(
            text,
            maxTextWidth,
            maxTextHeight,
            alignment,
            textStyle ?? CustomTableTextStyle.Normal,
            scoreFontFamily,
            foreground ?? Brushes.Black,
            textRuns ?? [],
            pixelsPerDip,
            culture ?? CultureInfo.GetCultureInfo("ja-JP"),
            out hit);
    }

    private static void CountDominantRunPixels(FormattedText text, out int redPixels, out int bluePixels)
    {
        const int width = 220;
        const int height = 60;
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            context.DrawText(text, new Point(4, 4));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);

        redPixels = 0;
        bluePixels = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                byte blue = pixels[offset];
                byte green = pixels[offset + 1];
                byte red = pixels[offset + 2];
                if (red > 120 && green < 100 && blue < 100)
                {
                    redPixels++;
                }
                if (blue > 120 && green < 100 && red < 100)
                {
                    bluePixels++;
                }
            }
        }
    }

}
