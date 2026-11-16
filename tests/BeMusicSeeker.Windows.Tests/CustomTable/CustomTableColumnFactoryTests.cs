using System.Windows;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class CustomTableColumnFactoryTests
{

    /// <summary>選択二行のドラッグデータから新旧形式・行種別・主行と選択一覧を取得できることを確認し、既存ドラッグ受付との互換や主行の指定を失う誤りを検出します。</summary>
    [TestMethod]
    public void CustomTableDataTransfer_CreatesLegacyAndCustomDragFormats()
    {
        object[] rows = [new(), new()];

        DataObject dataObject = CustomTableDataTransfer.CreateSelectedRowsDataObject(rows, CustomTableRowDragKind.PlaylistSummaryRows, rows[1]);

        Assert.IsTrue(dataObject.GetDataPresent(CustomTableDataTransfer.SelectedRowsDataFormat));
        Assert.IsTrue(dataObject.GetDataPresent(CustomTableDataTransfer.LegacySelectedRowsDataFormat));
        Assert.IsTrue(CustomTableDataTransfer.HasRowDragKind(dataObject, CustomTableRowDragKind.PlaylistSummaryRows));
        Assert.IsTrue(CustomTableDataTransfer.TryGetPrimaryRow(dataObject, out object primaryRow));
        Assert.AreSame(rows[1], primaryRow);
        Assert.IsTrue(CustomTableDataTransfer.TryGetSelectedRows(dataObject, out System.Collections.Generic.List<object>? resolvedRows));
        Assert.AreEqual(2, resolvedRows.Count);
    }

}
