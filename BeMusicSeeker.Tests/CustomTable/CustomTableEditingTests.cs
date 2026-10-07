using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
// 共通Dispatcherの文化圏を表示待機中も所有し、他の画面テストとの再入を避けます。
[DoNotParallelize]
public sealed class CustomTableEditingTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void InstallDestination_EmptyEnterCommitsEmptyWithoutSelectingCandidate(int candidateCount)
    {
        WithEditor(candidateCount, string.Empty, (table, editor, completed) =>
        {
            Assert.AreEqual(string.Empty, editor.Text);
            Assert.IsTrue(table.HandleKeyDown(Key.Return, ModifierKeys.None));
            AssertCommitted(completed, string.Empty);
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InstallDestination_ExplicitArrowSelectionCommitsRawCandidate(bool moveBackUp)
    {
        WithEditor(2, string.Empty, (table, _, completed) =>
        {
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
            if (moveBackUp)
            {
                Assert.IsTrue(table.HandleKeyDown(Key.Up, ModifierKeys.None));
            }
            Assert.IsTrue(table.HandleKeyDown(Key.Return, ModifierKeys.None));
            AssertCommitted(completed, moveBackUp ? CandidateA : CandidateB);
        });
    }

    [TestMethod]
    [DataRow(@"C:\BMS\Manual")]
    [DataRow("")]
    public void InstallDestination_TextChangeAfterSelectionCommitsInput(string input)
    {
        WithEditor(2, CandidateA, (table, editor, completed) =>
        {
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
            editor.Text = input;
            Assert.IsTrue(table.HandleKeyDown(Key.Return, ModifierKeys.None));
            AssertCommitted(completed, input);
        });
    }

    [TestMethod]
    public void InstallDestination_ClickCommitsTargetCandidate()
    {
        WithEditor(2, string.Empty, (table, _, completed) =>
        {
            Assert.IsTrue(table.HandleEditSuggestionClick(CandidateB));
            AssertCommitted(completed, CandidateB);
        });
    }

    [TestMethod]
    [DataRow(1, "")]
    [DataRow(2, "")]
    [DataRow(2, @"C:\BMS\Manual")]
    public void InstallDestination_LeavingEditCommitsCurrentInput(int candidateCount, string input)
    {
        WithEditor(candidateCount, input, (table, _, completed) =>
        {
            table.HandleEditFocusDeparture(focusWithinEditControls: true);
            Assert.AreEqual(0, completed.Count);
            table.HandleEditFocusDeparture(focusWithinEditControls: false);
            AssertCommitted(completed, input);
        });
    }

    [TestMethod]
    public void InstallDestination_EscapeClosesCandidatesThenCancelsEdit()
    {
        WithEditor(2, CandidateA, (table, editor, completed) =>
        {
            editor.Text = @"C:\BMS\Manual";
            Assert.IsTrue(table.HandleKeyDown(Key.Escape, ModifierKeys.None));
            Assert.AreEqual(0, completed.Count);
            Assert.IsTrue(table.HandleKeyDown(Key.Escape, ModifierKeys.None));
            Assert.AreEqual(1, completed.Count);
            Assert.IsFalse(completed[0].Commit);
            Assert.AreEqual(@"C:\BMS\Manual", completed[0].Text);
        });
    }

    [TestMethod]
    public void InstallDestination_TabCommitsInputEvenWhenCandidateIsSelected()
    {
        WithEditor(2, @"C:\BMS\Manual", (table, _, completed) =>
        {
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
            Assert.IsTrue(table.HandleKeyDown(Key.Tab, ModifierKeys.None));
            AssertCommitted(completed, @"C:\BMS\Manual");
        });
    }

    private const string CandidateA = @"C:\BMS\A";
    private const string CandidateB = @"C:\BMS\B";

    private static void WithEditor(int candidateCount, string initial, Action<CustomTableView, TextBox, List<CustomTableCellEditEndedEventArgs>> action)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            using IDisposable culture = TestResourceInitializer.UseJapaneseCulture();
            string[] suggestions = new[] { CandidateA, CandidateB }.Take(candidateCount).ToArray();
            ChartFile chart = ChartFileProjection.WithPackageState(
                (ChartTestValues.Empty()), initial, string.Empty, string.Empty, suggestions, []);
            var row = LibraryChartRow.FromChartFile(chart);
            var settings = new CustomTableColumnSettings();
            settings.InstallDst.Visibility = Visibility.Visible;
            settings.InstallDst.Width = 250;
            CustomTableColumn column = CustomTableColumnFactory.CreateMainColumns(settings).Single(item => item.Id == "InstallDst");
            if (string.IsNullOrEmpty(initial))
            {
                StringAssert.Contains(column.GetText(row), BeMusicSeeker.Properties.Resources.InstallDestination_SelectCandidate);
            }
            var table = new CustomTableView
            {
                Width = 300d,
                Height = 90d,
                HeaderHeight = 0d,
                RowHeight = 60d,
                Columns = [column],
                ItemsSource = new List<object> { row }
            };
            // Popupの所有登録だけに内部参照を使い、開閉のOS状態を判定基準にしません。
            var popup = (Popup)(typeof(CustomTableView).GetField("editSuggestionPopup", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(table)
                ?? throw new InvalidOperationException("候補Popupが見つかりません。"));
            scope.TrackPopup(popup);
            scope.ShowAndWaitForContentRendered(new Window { Width = 320d, Height = 130d, Content = table });
            table.UpdateLayout();
            var completed = new List<CustomTableCellEditEndedEventArgs>();
            table.CellEditEnded += (_, args) => completed.Add(args);
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
            Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
            TextBox editor = table.Children.OfType<Canvas>().SelectMany(canvas => canvas.Children.OfType<TextBox>()).Single();
            action(table, editor, completed);
            Assert.AreEqual(0, table.Children.OfType<Canvas>().SelectMany(canvas => canvas.Children.OfType<TextBox>()).Count());
            CollectionAssert.AreEqual(suggestions, row.Chart.InstallDestinationSuggestions.ToArray());
        });
    }

    private static void AssertCommitted(List<CustomTableCellEditEndedEventArgs> completed, string input)
    {
        Assert.AreEqual(1, completed.Count);
        Assert.IsTrue(completed[0].Commit);
        Assert.AreEqual("instl_dst", completed[0].EditPropertyName);
        Assert.AreEqual(input, completed[0].Text);
    }
}
