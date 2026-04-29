using System;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomTableTextLayoutCacheTests
{
    private static readonly Typeface NormalTypeface = new Typeface(new FontFamily("Meiryo UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldTypeface = new Typeface(new FontFamily("Meiryo UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    [TestMethod]
    public void GetOrCreate_ReusesSameKey()
    {
        RunOnSta(delegate
        {
            CustomTableTextLayoutCache cache = new CustomTableTextLayoutCache();

            FormattedText first = Create(cache, out bool firstHit, text: "AAA");
            FormattedText second = Create(cache, out bool secondHit, text: "AAA");

            Assert.IsFalse(firstHit);
            Assert.IsTrue(secondHit);
            Assert.AreSame(first, second);
            Assert.AreEqual(1, cache.Count);
        });
    }

    [TestMethod]
    public void GetOrCreate_KeyIncludesLayoutAndStyleInputs()
    {
        RunOnSta(delegate
        {
            AssertKeyChangeMisses(cache => Create(cache, out _, text: "AAA"), cache => { Create(cache, out bool hit, text: "AA"); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, maxTextWidth: 80d), cache => { Create(cache, out bool hit, maxTextWidth: 81d); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, maxTextHeight: 18d), cache => { Create(cache, out bool hit, maxTextHeight: 19d); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, alignment: TextAlignment.Left), cache => { Create(cache, out bool hit, alignment: TextAlignment.Center); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, useBoldText: false), cache => { Create(cache, out bool hit, useBoldText: true); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, foreground: Brushes.Black), cache => { Create(cache, out bool hit, foreground: Brushes.Red); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, pixelsPerDip: 1d), cache => { Create(cache, out bool hit, pixelsPerDip: 1.25d); return hit; });
            AssertKeyChangeMisses(cache => Create(cache, out _, culture: CultureInfo.GetCultureInfo("en-US")), cache => { Create(cache, out bool hit, culture: CultureInfo.GetCultureInfo("ja-JP")); return hit; });
        });
    }

    [TestMethod]
    public void GetOrCreate_EvictsOldestEntryWhenCapacityIsExceeded()
    {
        RunOnSta(delegate
        {
            CustomTableTextLayoutCache cache = new CustomTableTextLayoutCache(maxEntryCount: 2);
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

    [TestMethod]
    public void CalculateHitRate_ReturnsMinusOneWhenNoTextWasDrawn()
    {
        Assert.AreEqual(-1d, CustomTableTextLayoutCache.CalculateHitRate(0, 0));
        Assert.AreEqual(0.5d, CustomTableTextLayoutCache.CalculateHitRate(1, 1));
    }

    private static void AssertKeyChangeMisses(Action<CustomTableTextLayoutCache> seed, Func<CustomTableTextLayoutCache, bool> candidate)
    {
        CustomTableTextLayoutCache cache = new CustomTableTextLayoutCache();
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
        bool useBoldText = false,
        Brush foreground = null!,
        double pixelsPerDip = 1d,
        CultureInfo culture = null!)
    {
        return cache.GetOrCreate(
            text,
            maxTextWidth,
            maxTextHeight,
            alignment,
            useBoldText,
            foreground ?? Brushes.Black,
            pixelsPerDip,
            culture ?? CultureInfo.GetCultureInfo("ja-JP"),
            useBoldText ? BoldTypeface : NormalTypeface,
            11d,
            out hit);
    }

    private static void RunOnSta(Action action)
    {
        Exception exception = null!;
        Thread thread = new Thread((ThreadStart)delegate
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception != null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
