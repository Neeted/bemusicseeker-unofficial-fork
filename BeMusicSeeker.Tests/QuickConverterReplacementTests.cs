using System;
using System.Globalization;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class QuickConverterReplacementTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [TestMethod]
    public void VisibilityColumnRoundtripPreservesVisibleAndHiddenContract()
    {
        var converter = new visibilityToIsCheckedConverter();

        Assert.IsTrue((bool)converter.Convert(Visibility.Visible, typeof(bool), null, Culture));
        Assert.IsFalse((bool)converter.Convert(Visibility.Collapsed, typeof(bool), null, Culture));
        Assert.AreSame(DependencyProperty.UnsetValue, converter.Convert(DependencyProperty.UnsetValue, typeof(bool), null, Culture));
        Assert.AreEqual(Visibility.Visible, converter.ConvertBack(true, typeof(Visibility), null, Culture));
        Assert.AreEqual(Visibility.Hidden, converter.ConvertBack(false, typeof(Visibility), null, Culture));
    }

    [TestMethod]
    public void PresentationConvertersPreserveTypedBoundarySemantics()
    {
        var minHeight = new mainWindowMinHeightConverter();
        Assert.AreEqual(328d, minHeight.Convert(new object[] { 286d, Visibility.Visible, 42d }, typeof(double), null, Culture));
        Assert.AreEqual(286d, minHeight.Convert(new object[] { 286d, Visibility.Collapsed, 42d }, typeof(double), null, Culture));
        Assert.AreSame(DependencyProperty.UnsetValue, minHeight.Convert(new object[] { 286d, Visibility.Visible }, typeof(double), null, Culture));

        var playerTitle = new playerTitleVisibilityConverter();
        Assert.AreEqual(Visibility.Collapsed, playerTitle.Convert(new object[] { Visibility.Visible, Visibility.Collapsed }, typeof(Visibility), null, Culture));
        Assert.AreEqual(Visibility.Visible, playerTitle.Convert(new object[] { Visibility.Hidden, Visibility.Collapsed }, typeof(Visibility), null, Culture));

        var duration = new positiveTimeSpanToVisibilityHiddenConverter();
        Assert.AreEqual(Visibility.Visible, duration.Convert(TimeSpan.FromSeconds(1), typeof(Visibility), null, Culture));
        Assert.AreEqual(Visibility.Hidden, duration.Convert(TimeSpan.Zero, typeof(Visibility), null, Culture));

        var title = new stringToVisibilityHiddenConverter();
        Assert.AreEqual(Visibility.Hidden, title.Convert(null, typeof(Visibility), null, Culture));
        Assert.AreSame(DependencyProperty.UnsetValue, title.Convert(42, typeof(Visibility), null, Culture));

        var collapsed = new stringToVisibilityCollapsedConverter();
        Assert.AreEqual(Visibility.Collapsed, collapsed.Convert(null, typeof(Visibility), null, Culture));
    }

    [TestMethod]
    public void SettingsConvertersPreserveBackupValues()
    {
        var backup = new backupSpanRadioConverter();
        Assert.IsTrue((bool)backup.Convert(7, typeof(bool), "7", Culture));
        Assert.IsFalse((bool)backup.Convert(1, typeof(bool), "7", Culture));
        Assert.AreEqual(30, backup.ConvertBack(true, typeof(int), "30", Culture));
        Assert.AreEqual(0, backup.ConvertBack(false, typeof(int), "30", Culture));
        Assert.AreSame(DependencyProperty.UnsetValue, backup.Convert(7, typeof(bool), "invalid", Culture));

        var positiveCount = new positiveIntToBoolConverter();
        Assert.IsFalse((bool)positiveCount.Convert((uint)0, typeof(bool), null, Culture));
        Assert.IsTrue((bool)positiveCount.Convert((uint)2, typeof(bool), null, Culture));
        Assert.AreSame(DependencyProperty.UnsetValue, positiveCount.Convert("2", typeof(bool), null, Culture));
    }

}
