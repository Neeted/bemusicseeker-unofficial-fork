using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Data;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;


/// <remarks>列見出しの並べ替え要求で、表自身が並べ替え列・方向を先に書き換えず、購読側のBindingの値だけで矢印が決まることを検査します。MainWindowでの起動中の抑止への接続は <c>MainWindowTreePresentationWpfTests</c> が分担します。</remarks>
[TestClass]
// 共通WPF Applicationと所有Windowの表示・フォーカスをケースの終了まで所有します。
[DoNotParallelize]
public sealed class CustomTableSortStateTests
{
    /// <summary>
    /// 購読側が要求を採用しない場合（起動中の抑止など）に、見出しの要求を出しても表の並べ替え列・方向がBindingの値（初期のnullを含む）のままで、
    /// その後に元の値を変えると表が追従する（Bindingが切れていない）ことを確認します。
    /// 要求の送出前に表自身が矢印を書き換えて実際の並びと食い違う誤りと、書き換えでBindingが切れる誤りを検出します。
    /// </summary>
    [TestMethod]
    public void HeaderSortRejectedBySubscriber_KeepsBoundSortStateAndBinding()
    {
        var source = new SortSource();
        WithBoundTable(source, (table, header) =>
        {
            int requests = 0;
            table.SortRequested += (_, _) => requests++;

            Invoke(header);
            Assert.AreEqual(1, requests, "要求は購読側へ届く。");
            Assert.IsNull(table.SortColumnName);
            Assert.IsNull(table.SortDirection);
            Invoke(header);
            Assert.AreEqual(2, requests);
            Assert.IsNull(table.SortColumnName, "拒否した要求を重ねても表示は変わらない。");
            Assert.IsNull(table.SortDirection);

            source.Set("value", ListSortDirection.Descending);
            Assert.AreEqual("value", table.SortColumnName, "Bindingは切れていない。");
            Assert.AreEqual(ListSortDirection.Descending, table.SortDirection);
            Invoke(header);
            Assert.AreEqual("value", table.SortColumnName);
            Assert.AreEqual(ListSortDirection.Descending, table.SortDirection, "拒否した要求ではBindingの値のまま。");

            source.Set("value", ListSortDirection.Ascending);
            Assert.AreEqual(ListSortDirection.Ascending, table.SortDirection);
        });
    }

    /// <summary>
    /// 購読側が要求を採用して元の値を更新した場合に、表の矢印がBindingの更新で変わり、次のクリックの向きは現在の表示値から決まることを確認します。
    /// 受理された要求で矢印が変わらない誤りと、表示値を使わず向きが毎回同じになる誤りを検出します。
    /// </summary>
    [TestMethod]
    public void HeaderSortAcceptedBySubscriber_FollowsBoundSortState()
    {
        var source = new SortSource();
        WithBoundTable(source, (table, header) =>
        {
            var requested = new List<(string Column, ListSortDirection Direction)>();
            table.SortRequested += (_, request) =>
            {
                requested.Add((request.SortMemberPath, request.Direction));
                source.Set(request.SortMemberPath, request.Direction);
            };

            Invoke(header);
            Assert.AreEqual("value", table.SortColumnName);
            Assert.AreEqual(ListSortDirection.Ascending, table.SortDirection);
            Invoke(header);
            Assert.AreEqual(ListSortDirection.Descending, table.SortDirection, "次のクリックの向きは現在の表示値から決まる。");
            CollectionAssert.AreEqual(
                new[] { ("value", ListSortDirection.Ascending), ("value", ListSortDirection.Descending) },
                requested);

            source.Set("value", ListSortDirection.Ascending);
            Assert.AreEqual(ListSortDirection.Ascending, table.SortDirection, "元の値を変えると追従する。");
        });
    }

    private static void Invoke(AutomationPeer header)
        => (header.GetPattern(PatternInterface.Invoke) as IInvokeProvider ?? throw new AssertFailedException("見出しにInvokeがありません。")).Invoke();

    private static void WithBoundTable(SortSource source, Action<CustomTableView, AutomationPeer> action)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var table = new CustomTableView
            {
                Width = 300,
                Height = 150,
                HeaderHeight = 20,
                RowHeight = 25,
                Columns = [new CustomTableColumn("Title", "Title",
                    new CustomTableColumnSettings.ColumnLayout { Width = 240, Visibility = Visibility.Visible },
                    0, "value", TextAlignment.Left, row => ((TestRow)row).Value, editPropertyName: "Value")],
                ItemsSource = new List<object> { new TestRow("value 0") },
            };
            BindingOperations.SetBinding(table, CustomTableView.SortColumnNameProperty,
                new Binding(nameof(SortSource.ColumnName)) { Source = source, Mode = BindingMode.OneWay });
            BindingOperations.SetBinding(table, CustomTableView.SortDirectionProperty,
                new Binding(nameof(SortSource.Direction)) { Source = source, Mode = BindingMode.OneWay });
            var window = new Window { Width = 340, Height = 200, Content = table, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                scope.ShowAndWaitForContentRendered(window);
                AutomationPeer root = UIElementAutomationPeer.CreatePeerForElement(table) ?? throw new AssertFailedException("表peerがありません。");
                AutomationPeer header = root.GetChildren().Single(child => child.GetAutomationControlType() == AutomationControlType.HeaderItem);
                action(table, header);
            }
            finally
            {
                table.HandleKeyDown(Key.Escape, ModifierKeys.None);
                BindingOperations.ClearAllBindings(table);
                table.ItemsSource = null;
                table.DataContext = null;
            }
        });
    }

    private sealed class TestRow(string value)
    {
        internal string Value { get; } = value;
    }

    /// <summary>購読側（ViewModel）の並べ替え状態を模し、値の変更だけをBindingへ通知します。</summary>
    private sealed class SortSource : INotifyPropertyChanged
    {
        private string? columnName;

        private ListSortDirection? direction;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string? ColumnName => columnName;

        public ListSortDirection? Direction => direction;

        internal void Set(string column, ListSortDirection value)
        {
            columnName = column;
            direction = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColumnName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Direction)));
        }
    }
}
