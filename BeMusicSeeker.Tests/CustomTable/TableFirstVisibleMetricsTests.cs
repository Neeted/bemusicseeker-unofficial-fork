using BeMusicSeeker.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class TableFirstVisibleMetricsTests
{
    /// <summary>可視行100件と50列から可視セル5000件を求めることを確認し、比較ログの作業量を行数だけで報告する誤りを検出します。</summary>
    [TestMethod]
    public void CalculateVisibleCellCount_ReturnsProduct()
    {
        Assert.AreEqual(5000, TableFirstVisibleMetrics.CalculateVisibleCellCount(100, 50));
    }

    /// <summary>行数または列数が未知ならセル数も未知とすることを確認し、未測定をゼロや負の作業量として報告する誤りを検出します。</summary>
    [TestMethod]
    public void CalculateVisibleCellCount_ReturnsMinusOneForUnknown()
    {
        Assert.AreEqual(-1, TableFirstVisibleMetrics.CalculateVisibleCellCount(-1, 50));
        Assert.AreEqual(-1, TableFirstVisibleMetrics.CalculateVisibleCellCount(100, -1));
    }

    /// <summary>初回表示の時間・世代・可視作業量とキャッシュ率を指定した測定値からログへ出すことを確認し、実測値の取違えや比較に必要な項目の欠落を検出します。</summary>
    [TestMethod]
    public void Format_IncludesComparableFields()
    {
        var timing = new TableFirstVisibleTiming(
            requestVersion: 7,
            requestToBuildStartMs: 10,
            requestToBuildCompleteMs: 20,
            requestToVisibleRenderMs: 90,
            buildToVisibleRenderMs: 70,
            viewCount: 979);
        var metrics = new TableFirstVisibleMetrics(
            controlType: "CustomTableView",
            trigger: "custom_onrender",
            sourceGenerationId: 11,
            viewGenerationId: 12,
            rowCount: 979,
            visibleRowCount: 100,
            visibleColumnCount: 50,
            visibleCellCount: 5000,
            firstRenderMs: 80,
            renderWorkMs: 12,
            textCacheHitRate: 0.5d,
            stateLogMs: 1,
            timing: timing);

        string message = TableFirstVisibleLogFormatter.Format(metrics);

        StringAssert.StartsWith(message, "table_first_visible ");
        StringAssert.Contains(message, "controlType=CustomTableView");
        StringAssert.Contains(message, "trigger=custom_onrender");
        StringAssert.Contains(message, "requestVersion=7");
        StringAssert.Contains(message, "visibleCellCount=5000");
        StringAssert.Contains(message, "isPreparationRender=False");
        StringAssert.Contains(message, "firstRenderMs=80");
        StringAssert.Contains(message, "renderWorkMs=12");
        StringAssert.Contains(message, "textCacheHitRate=0.5");
        StringAssert.Contains(message, "stateLogMs=1");
    }

    /// <summary>準備描画として記録した測定に準備フラグを出すことを確認し、空の準備描画を通常の初回表示と混同する誤りを検出します。</summary>
    [TestMethod]
    public void Format_CanMarkPreparationRender()
    {
        var timing = new TableFirstVisibleTiming(
            requestVersion: 8,
            requestToBuildStartMs: 10,
            requestToBuildCompleteMs: 20,
            requestToVisibleRenderMs: 30,
            buildToVisibleRenderMs: 10,
            viewCount: 100);
        var metrics = new TableFirstVisibleMetrics(
            controlType: "CustomTableView",
            trigger: "custom_onrender",
            sourceGenerationId: 1,
            viewGenerationId: 2,
            rowCount: 0,
            visibleRowCount: 0,
            visibleColumnCount: 10,
            visibleCellCount: 0,
            firstRenderMs: 5,
            renderWorkMs: 1,
            textCacheHitRate: -1d,
            stateLogMs: 0,
            timing: timing,
            isPreparationRender: true);

        string message = TableFirstVisibleLogFormatter.Format(metrics);

        StringAssert.Contains(message, "isPreparationRender=True");
    }
}
