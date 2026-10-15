using System;
using System.Windows;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>実モニターを使わず、物理座標の配置と最小DIP寸法の規則を検証します。</summary>
[TestClass]
public sealed class WindowPlacementCalculatorTests
{
    /// <summary>中央、端の補正、片軸縮小を区別し、収まる軸まで縮めたり縮小時の左上を取り違える誤りを検出します。</summary>
    [DataTestMethod]
    [DataRow(200, 100, 800, 600, 600, 400, 300, 200, 600, 400)]
    [DataRow(900, 700, 200, 200, 600, 400, 400, 400, 600, 400)]
    [DataRow(200, 100, 800, 600, 1100, 400, 0, 0, 1000, 400)]
    [DataRow(200, 100, 800, 600, 600, 900, 0, 0, 600, 800)]
    public void Placement_centersClampsOrShrinksOnlyExceedingAxes(
        int left, int top, int width, int height, int desiredWidth, int desiredHeight,
        int expectedLeft, int expectedTop, int expectedWidth, int expectedHeight)
    {
        Rect result = WindowPlacementCalculator.Calculate(new Rect(0, 0, 1000, 800),
            new Rect(left, top, width, height), new Size(desiredWidth, desiredHeight), 1);
        Assert.AreEqual(new Rect(expectedLeft, expectedTop, expectedWidth, expectedHeight), result);
    }

    /// <summary>負原点と非整数倍率で寸法だけを拡大し、符号をまたぐ半整数位置でも平行移動・丸め後の包含を守ることを確認します。</summary>
    [TestMethod]
    public void Placement_fractionalDpiScalesDimensionsAndPreservesTranslatedOrigins()
    {
        Rect work = new(-1920, -1080, 1920, 1040);
        Rect reference = new(-1720, -980, 1500, 900);
        Size size = new(800.5, 600.5);
        Rect result = WindowPlacementCalculator.Calculate(work, reference, size, 1.25);
        Assert.AreEqual(1001d, result.Width);
        Assert.AreEqual(751d, result.Height);
        Assert.IsTrue(work.Contains(result));
        Assert.AreEqual(reference.Left + (reference.Width - result.Width) / 2, result.Left, 1d);
        Assert.AreEqual(reference.Top + (reference.Height - result.Height) / 2, result.Top, 1d);
        work.Offset(217, -83);
        reference.Offset(217, -83);
        Rect translated = WindowPlacementCalculator.Calculate(work, reference, size, 1.25);
        result.Offset(217, -83);
        Assert.AreEqual(result, translated);
        Assert.IsTrue(work.Contains(translated));
        Rect shrunk = WindowPlacementCalculator.Calculate(work, reference, new Size(4000, 600), 1.25);
        Assert.AreEqual(work.TopLeft, shrunk.TopLeft);
        Assert.AreEqual(750d, shrunk.Height);
        Assert.IsTrue(work.Contains(shrunk));

        work = new Rect(-500, -500, 1000, 1000);
        reference = new Rect(-50, -50, 100, 100);
        size = new Size(101, 101);
        result = WindowPlacementCalculator.Calculate(work, reference, size, 1);
        work.Offset(100, 100);
        reference.Offset(100, 100);
        translated = WindowPlacementCalculator.Calculate(work, reference, size, 1);
        result.Offset(100, 100);
        Assert.AreEqual(result, translated, "半整数の中心が負座標から正座標へ移っても、結果は同量だけ移動します。");
        Assert.IsTrue(work.Contains(translated));
    }

    /// <summary>狭い表示先では必要な最小軸だけ緩和し、同じDPIの広い画面でも通常値へ復帰することを確認します。</summary>
    [TestMethod]
    public void MinimumSize_relaxesRequiredAxisAndRestoresNormalMinimum()
    {
        Size normal = new(820, 560);
        Assert.AreEqual(new Size(600, 560), WindowPlacementCalculator.MinimumSize(new Rect(-900, 0, 900, 1000), normal, 1.5));
        Assert.AreEqual(normal, WindowPlacementCalculator.MinimumSize(new Rect(0, 0, 3000, 2000), normal, 1.5));
    }

    /// <summary>無効な領域・寸法・DPIを配置成功へ読み替えず拒否する境界を確認します。</summary>
    [TestMethod]
    public void Placement_rejectsInvalidGeometryAndDpi()
    {
        Rect work = new(0, 0, 1000, 800);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => WindowPlacementCalculator.Calculate(work, work, new Size(0, 400), 1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => WindowPlacementCalculator.Calculate(work, work, new Size(600, 400), double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => WindowPlacementCalculator.Calculate(Rect.Empty, work, new Size(600, 400), 1));
    }
}
