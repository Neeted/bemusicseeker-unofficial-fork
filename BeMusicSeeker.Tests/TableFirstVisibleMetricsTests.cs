using BeMusicSeeker.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class TableFirstVisibleMetricsTests
{
    [TestMethod]
    public void CalculateVisibleCellCount_ReturnsProduct()
    {
        Assert.AreEqual(5000, TableFirstVisibleMetrics.CalculateVisibleCellCount(100, 50));
    }

    [TestMethod]
    public void CalculateVisibleCellCount_ReturnsMinusOneForUnknown()
    {
        Assert.AreEqual(-1, TableFirstVisibleMetrics.CalculateVisibleCellCount(-1, 50));
        Assert.AreEqual(-1, TableFirstVisibleMetrics.CalculateVisibleCellCount(100, -1));
    }

    [TestMethod]
    public void Format_IncludesComparableFields()
    {
        TableFirstVisibleTiming timing = new TableFirstVisibleTiming(
            requestVersion: 7,
            requestToBuildStartMs: 10,
            requestToBuildCompleteMs: 20,
            requestToVisibleRenderMs: 90,
            buildToVisibleRenderMs: 70,
            viewCount: 979);
        TableFirstVisibleMetrics metrics = new TableFirstVisibleMetrics(
            controlType: "DataGrid",
            trigger: "target_updated_render",
            sourceGenerationId: 11,
            viewGenerationId: 12,
            rowCount: 979,
            visibleRowCount: 100,
            visibleColumnCount: 50,
            visibleCellCount: 5000,
            firstRenderMs: 80,
            renderWorkMs: -1,
            textCacheHitRate: 0.5d,
            stateLogMs: 1,
            timing: timing);

        string message = TableFirstVisibleLogFormatter.Format(metrics);

        StringAssert.StartsWith(message, "table_first_visible ");
        StringAssert.Contains(message, "controlType=DataGrid");
        StringAssert.Contains(message, "trigger=target_updated_render");
        StringAssert.Contains(message, "requestVersion=7");
        StringAssert.Contains(message, "visibleCellCount=5000");
        StringAssert.Contains(message, "isPreparationRender=False");
        StringAssert.Contains(message, "firstRenderMs=80");
        StringAssert.Contains(message, "renderWorkMs=-1");
        StringAssert.Contains(message, "textCacheHitRate=0.5");
        StringAssert.Contains(message, "stateLogMs=1");
    }

    [TestMethod]
    public void Format_CanMarkPreparationRender()
    {
        TableFirstVisibleTiming timing = new TableFirstVisibleTiming(
            requestVersion: 8,
            requestToBuildStartMs: 10,
            requestToBuildCompleteMs: 20,
            requestToVisibleRenderMs: 30,
            buildToVisibleRenderMs: 10,
            viewCount: 100);
        TableFirstVisibleMetrics metrics = new TableFirstVisibleMetrics(
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
