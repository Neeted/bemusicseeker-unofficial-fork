using System.Collections.Generic;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RegularChartListRefreshTypesTests
{
    /// <summary>段階結果の欠損を空一覧へ正規化し、既に一覧の入力は複製せず保持して更新ごとの無駄な全件コピーを防ぎます。</summary>
    [TestMethod]
    public void RegularChartListStageState_MaterializesNullAndPreservesListInstances()
    {
        List<LibraryChartRow> rows = [];

        List<LibraryChartRow> fromNull = RegularChartListStageState.Materialize(null);
        List<LibraryChartRow> fromList = RegularChartListStageState.Materialize(rows);

        Assert.AreEqual(0, fromNull.Count);
        Assert.AreSame(rows, fromList);
    }

}
