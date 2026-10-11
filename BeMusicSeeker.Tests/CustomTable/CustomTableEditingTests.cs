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

/// <remarks>実表の編集TextBoxと候補Popupから確定・取消イベントまでの接続をWPFで確認します。候補装飾と実パスの分離は列生成の下位ケースが担います。</remarks>
[TestClass]
// 共通Dispatcherの文化圏を表示待機中も所有し、他の画面テストとの再入を避けます。
[DoNotParallelize]
public sealed class CustomTableEditingTests
{
    /// <summary>候補が一件または二件あっても空入力のEnterは空の導入先を一回確定することを確認し、未選択候補を自動採用する誤りを検出します。</summary>
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

    /// <summary>下キーで二件目を選び、上へ戻す場合も含めEnterで明示選択の実パスを確定することを確認し、候補の移動や表示用文字を保存する誤りを検出します。</summary>
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

    /// <summary>候補選択後に手入力パスまたは空へ書き換えるとEnterは現在入力を確定することを確認し、古い選択候補が手入力を上書きする誤りを検出します。</summary>
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

    /// <summary>二件目の候補クリックはその実パスを一回確定して編集を閉じることを確認し、先頭候補を採用する誤りやクリックの二重確定を検出します。</summary>
    [TestMethod]
    public void InstallDestination_ClickCommitsTargetCandidate()
    {
        WithEditor(2, string.Empty, (table, _, completed) =>
        {
            Assert.IsTrue(table.HandleEditSuggestionClick(CandidateB));
            AssertCommitted(completed, CandidateB);
        });
    }

    /// <summary>候補数と空/手入力の各条件で編集内部のフォーカス移動は確定せず、外へ出た場合だけ現在入力を確定することを確認し、候補への移動で意図せず保存する誤りを検出します。</summary>
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

    /// <summary>入力変更後の最初のEscapeは候補だけ閉じ、次のEscapeは一回の取消で編集を閉じることを確認し、候補を閉じる操作で入力を保存する誤りを検出します。</summary>
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

    /// <summary>候補を選択していてもTabは手入力の実パスを一回確定することを確認し、フォーカス移動で未確定の候補選択を採用する誤りを検出します。</summary>
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
