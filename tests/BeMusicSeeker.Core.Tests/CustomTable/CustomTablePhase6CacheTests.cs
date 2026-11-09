using System.Windows;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views;
using static BeMusicSeeker.TestSupport.Components.CustomTableCacheTestFixture;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class CustomTablePhase6CacheTests
{
    /// <summary>三列の指定幅と水平移動から可視列・対象列・幅変更の端を解決することを確認し、配置キャッシュの座標や表示範囲の計算誤りを検出します。</summary>
    [TestMethod]
    public void ColumnLayoutSnapshot_ResolvesVisibleColumnsAndResizeEdges()
    {
        CustomTableColumn[] columns =
        [
            CreateColumn("A", 50),
            CreateColumn("B", 80),
            CreateColumn("C", 30)
        ];

        var snapshot = CustomTableColumnLayoutSnapshot.Create(columns, horizontalOffset: 20d, viewportWidth: 100d);

        Assert.AreEqual(170d, snapshot.ExtentWidth);
        Assert.AreEqual(2, snapshot.VisibleColumnCount);
        Assert.IsTrue(snapshot.TryResolveColumn(60d, out CustomTableColumnLayoutEntry entry));
        Assert.AreEqual("B", entry.Column.Id);
        Assert.AreEqual(50d, entry.TableX);
        Assert.IsTrue(snapshot.TryResolveResizeColumn(30d, 4d, out CustomTableColumnLayoutEntry resizeEntry, out Rect resizeRect));
        Assert.AreEqual("A", resizeEntry.Column.Id);
        Assert.AreEqual(26d, resizeRect.X);
    }

    /// <summary>移動量・可視幅・列参照のいずれかが変わると配置キャッシュを再利用しないことを確認し、古い配置で描画やヒット判定を行う誤りを検出します。</summary>
    [TestMethod]
    public void ColumnLayoutSnapshot_MatchesOnlySameViewportAndColumns()
    {
        CustomTableColumn[] columns = [CreateColumn("A", 50)];
        var snapshot = CustomTableColumnLayoutSnapshot.Create(columns, 0d, 100d);

        Assert.IsTrue(snapshot.Matches(columns, 0d, 100d));
        Assert.IsFalse(snapshot.Matches(columns, 1d, 100d));
        Assert.IsFalse(snapshot.Matches(columns, 0d, 101d));
        Assert.IsFalse(snapshot.Matches([CreateColumn("A", 50)], 0d, 100d));
    }

    /// <summary>同じ行の文字とブラシを再利用し、行失効後は他行を保って更新し未定義背景も解除することを確認します。</summary>
    [TestMethod]
    public void CellValueCache_ReusesValuesUntilRowInvalidated()
    {
        int textCalls = 0;
        int foregroundCalls = 0;
        int backgroundCalls = 0;
        object row = new();
        bool undefined = true;
        var column = new CustomTableColumn(
            "Title",
            "TITLE",
            layout: null,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Left,
            textSelector: delegate
            {
                textCalls++;
                return "hello";
            },
            foregroundSelector: delegate
            {
                foregroundCalls++;
                return Brushes.Red;
            },
            backgroundSelector: delegate
            {
                backgroundCalls++;
                return undefined ? Brushes.Yellow : null;
            },
            textStyle: CustomTableTextStyle.Score);
        var cache = new CustomTableRowCache();

        CustomTableCellValue first = cache.GetOrCreate(row).GetValue(column, out bool firstHit);
        CustomTableCellValue second = cache.GetOrCreate(row).GetValue(column, out bool secondHit);
        object otherRow = new();
        cache.GetOrCreate(otherRow).GetValue(column, out _);
        undefined = false;
        cache.InvalidateRow(row);
        CustomTableCellValue third = cache.GetOrCreate(row).GetValue(column, out bool thirdHit);

        cache.GetOrCreate(otherRow).GetValue(column, out bool otherHit);
        Assert.IsTrue(otherHit);
        Assert.IsFalse(firstHit);
        Assert.IsTrue(secondHit);
        Assert.IsFalse(thirdHit);
        Assert.AreEqual("hello", first.Text);
        Assert.AreEqual("hello", second.Text);
        Assert.AreEqual("hello", third.Text);
        Assert.AreSame(Brushes.Yellow, first.Background);
        Assert.AreSame(Brushes.Yellow, second.Background);
        Assert.IsNull(third.Background);
        Assert.AreSame(CustomTableTextStyle.Score, first.TextStyle);
        Assert.AreSame(CustomTableTextStyle.Score, third.TextStyle);
        Assert.AreEqual(3, textCalls);
        Assert.AreEqual(3, foregroundCalls);
        Assert.AreEqual(3, backgroundCalls);
    }

    /// <summary>テキストセルの指定色を全文の描画区間へ変換することを確認し、描画経路でセル全体の強調色を失う誤りを検出します。</summary>
    [TestMethod]
    public void CellValueCreate_NormalizesColoredTextCellsToTextRuns()
    {
        object row = new();
        var column = new CustomTableColumn(
            "Clear",
            "CLEAR",
            layout: null,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Center,
            textSelector: _ => "HARD",
            foregroundSelector: _ => Brushes.Red);

        var value = CustomTableCellValue.Create(row, column);

        Assert.AreSame(Brushes.Red, value.Foreground);
        Assert.AreEqual(1, value.TextRuns.Count);
        Assert.AreEqual(0, value.TextRuns[0].StartIndex);
        Assert.AreEqual("HARD".Length, value.TextRuns[0].Length);
        Assert.AreSame(Brushes.Red, value.TextRuns[0].Foreground);
    }

    /// <summary>既定色と同色の別ブラシ・アイコン・チェックセルには不要な文字区間を作らないことを確認し、通常描画や非文字セルへ強調用描画を混ぜる誤りを検出します。</summary>
    [TestMethod]
    public void CellValueCreate_KeepsDefaultForegroundAndNonTextCellsWithoutSyntheticTextRuns()
    {
        object row = new();
        var defaultTextColumn = new CustomTableColumn(
            "Title",
            "TITLE",
            layout: null,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Left,
            textSelector: _ => "title",
            foregroundSelector: _ => CustomTableScoreBrushProvider.DefaultForeground);
        var iconColumn = new CustomTableColumn(
            "Download",
            "DL",
            layout: new CustomTableColumnSettings.ColumnLayout { Width = 40 },
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Center,
            textSelector: _ => "download",
            foregroundSelector: _ => Brushes.Red,
            cellKind: CustomTableCellKind.DownloadIcon);
        var checkBoxColumn = new CustomTableColumn(
            "Checked",
            "CHECK",
            layout: null,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Center,
            textSelector: _ => "checked",
            foregroundSelector: _ => Brushes.Red,
            checkedSelector: _ => true,
            cellKind: CustomTableCellKind.CheckBox);

        Assert.AreEqual(0, CustomTableCellValue.Create(row, defaultTextColumn).TextRuns.Count);
        if (CustomTableScoreBrushProvider.DefaultForeground is SolidColorBrush defaultForeground)
        {
            var equivalentDefaultTextColumn = new CustomTableColumn(
                "EquivalentTitle",
                "EQUIVALENT_TITLE",
                layout: null,
                fallbackOrder: 0,
                sortMemberPath: null,
                alignment: TextAlignment.Left,
                textSelector: _ => "title",
                foregroundSelector: _ => new SolidColorBrush(defaultForeground.Color));
            Assert.AreEqual(0, CustomTableCellValue.Create(row, equivalentDefaultTextColumn).TextRuns.Count);
        }

        Assert.AreEqual(0, CustomTableCellValue.Create(row, iconColumn).TextRuns.Count);
        Assert.AreEqual(0, CustomTableCellValue.Create(row, checkBoxColumn).TextRuns.Count);
    }

    /// <summary>全文色があっても指定済みの部分色区間をそのまま採用することを確認し、履歴の前後など個別の強調範囲を全文色で上書きする誤りを検出します。</summary>
    [TestMethod]
    public void CellValueCreate_PreservesExplicitTextRuns()
    {
        object row = new();
        var column = new CustomTableColumn(
            "Delta",
            "DELTA",
            layout: null,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Left,
            textSelector: _ => "A -> AA",
            foregroundSelector: _ => Brushes.Red,
            textRunSelector: (_, _) =>
            [
                new CustomTableTextRunStyle(0, 1, Brushes.Blue)
            ]);

        var value = CustomTableCellValue.Create(row, column);

        Assert.AreEqual(1, value.TextRuns.Count);
        Assert.AreEqual(0, value.TextRuns[0].StartIndex);
        Assert.AreEqual(1, value.TextRuns[0].Length);
        Assert.AreSame(Brushes.Blue, value.TextRuns[0].Foreground);
    }

}
