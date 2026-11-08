using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Localization;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using static BeMusicSeeker.TestSupport.Components.CustomTableCacheTestFixture;
using Resources = BeMusicSeeker.Properties.Resources;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class CustomTablePhase6CacheTests
{
    /// <summary>実ChangeCultureのja→en通知だけで同じ保留仮想行の案内・tooltip・描画を更新し、thread cultureとeditor候補・未確定path・選択/現在セル/scrollを保って確定0とします。明示Enterの旧行/属性/生path一回と、同じ表の再Loadedによる最新語も確認します。</summary>
    /// <remarks>実通知から描画への接続を確認するため、表示済み表の言語切替後は明示的な描画失効を行いません。候補条件・保存・能力判定と環境別cache条件は既存系列へ分担します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void InstallDestinationLanguage_ChangeCultureRedrawsRetainedRowsAndPreservesActiveEdit()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            CultureInfo? previousResourcesCulture = Resources.Culture;
            CultureInfo threadCulture = CultureInfo.CurrentCulture;
            CultureInfo threadUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                ResourceService.Current.ChangeCulture("ja-JP");
                string[] candidates = [@"C:\BMS\CandidateA", @"C:\BMS\CandidateB"];
                PackageChartEntry[] entries = [.. Enumerable.Range(0, 64).Select(index => PackageChartEntry.FromChart(
                    ChartFileProjection.WithPackageState(ChartTestValues.Empty() with
                    {
                        Path = @"C:\pending\" + index + ".bms", Md5 = index.ToString("x32"),
                        RawTitle = "Pending " + index.ToString("D2", CultureInfo.InvariantCulture)
                    }, string.Empty, string.Empty, string.Empty, candidates, [])))];
                List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildPackageRows(entries);
                var rows = new ChartListVirtualView(sourceRows, ChartListOrder.CreateTitleAscending(sourceRows), LibraryChartRow.FromSourceRow);
                var owner = new MainChartListViewModel();
                owner.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
                var settings = new CustomTableColumnSettings();
                foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings)) { layout.Visibility = Visibility.Hidden; }
                settings.InstallDst.Visibility = settings.Path.Visibility = Visibility.Visible;
                settings.InstallDst.DisplayIndex = 0; settings.InstallDst.Width = 40;
                settings.Path.DisplayIndex = 1; settings.Path.Width = 400;
                var table = new CustomTableView { Width = 220, Height = 150, HeaderHeight = 20, RowHeight = 25, DataContext = owner, ColumnsSettings = settings, ItemsSource = rows };
                // 既存Popupの所有登録に使う参照だけを読み、入力・描画・通知を反射で作りません。
                var popup = (Popup)(typeof(CustomTableView).GetField("editSuggestionPopup", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(table)
                    ?? throw new AssertFailedException("編集Popupがありません。"));
                scope.TrackPopup(popup);
                if (popup.Child is not Border { Child: ListBox suggestions }) { throw new AssertFailedException("実候補一覧がありません。"); }
                var window = new Window { Width = 260, Height = 200, Content = table };
                var completed = new List<CustomTableCellEditEndedEventArgs>();
                EventHandler<CustomTableCellEditEndedEventArgs> onEnded = (_, args) => completed.Add(args);
                table.CellEditEnded += onEnded;
                try
                {
                    scope.ShowAndWaitForContentRendered(window);
                    CustomTableColumn column = table.VisibleColumns.Single(item => item.Id == "InstallDst");
                    object columns = table.Columns;
                    ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
                    ScrollBar horizontal = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Horizontal);
                    vertical.Value = 2;
                    table.SelectedIndex = 3;
                    horizontal.Value = 10;
                    var row = (LibraryChartRow)rows[3];
                    Assert.AreSame(entries[3], row.PackageEntry);
                    Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                    TextBox editor = table.Children.OfType<Canvas>().SelectMany(canvas => canvas.Children.OfType<TextBox>()).Single();
                    const string input = @"C:\BMS\Manual";
                    editor.Text = input;
                    Assert.IsTrue(popup.IsOpen);
                    CollectionAssert.AreEqual(candidates, suggestions.Items.Cast<string>().ToArray());
                    Assert.AreEqual(-1, suggestions.SelectedIndex);
                    double verticalOffset = vertical.Value;
                    double horizontalOffset = table.HorizontalOffset;
                    Assert.AreEqual(2d, verticalOffset); Assert.AreEqual(10d, horizontalOffset);
                    var point = new Point(15, table.HeaderHeight + (3 - table.FirstVisibleRowIndex + 0.5) * table.RowHeight);
                    RenderExistingTable(table);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    CustomTableRowDrawing japaneseDrawing = table.GetRowEntry(row).Drawings[3];
                    string japaneseText = "▼ " + Resources.InstallDestination_SelectCandidate;
                    Assert.AreEqual(japaneseText, table.GetCellValue(row, column, out _).Text);
                    Assert.AreEqual(japaneseText, table.ResolveCellToolTip(table.HitTestTable(point)));
                    int realized = rows.RealizedRowCount;
                    Assert.IsTrue(realized > 0 && realized < rows.Count);

                    ResourceService.Current.ChangeCulture("en-US");
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    // 先に通知による実描画を確認し、getterや強制描画で接続漏れを補いません。
                    CustomTableRowDrawing englishDrawing = table.GetRowEntry(row).Drawings[3];
                    Assert.AreNotSame(japaneseDrawing, englishDrawing);
                    string englishText = "▼ " + Resources.InstallDestination_SelectCandidate;
                    Assert.AreNotEqual(japaneseText, englishText);
                    Assert.AreEqual(englishText, table.GetCellValue(row, column, out bool cacheHit).Text); Assert.IsTrue(cacheHit);
                    Assert.AreEqual(englishText, table.ResolveCellToolTip(table.HitTestTable(point)));
                    Assert.AreSame(threadCulture, CultureInfo.CurrentCulture); Assert.AreSame(threadUiCulture, CultureInfo.CurrentUICulture);
                    Assert.AreSame(table, window.Content); Assert.AreSame(rows, table.ItemsSource); Assert.AreSame(columns, table.Columns);
                    Assert.AreSame(row, rows[3]); Assert.AreSame(entries[3], row.PackageEntry);
                    Assert.AreEqual(40, column.Width); Assert.AreEqual(3, table.SelectedIndex); Assert.IsTrue(table.IsCurrentCell(3, column));
                    CollectionAssert.AreEqual(new object[] { row }, table.GetSelectedRowsSnapshot().ToArray());
                    Assert.AreEqual(verticalOffset, vertical.Value); Assert.AreEqual(horizontalOffset, table.HorizontalOffset);
                    Assert.AreSame(editor, table.Children.OfType<Canvas>().SelectMany(canvas => canvas.Children.OfType<TextBox>()).Single());
                    Assert.AreEqual(input, editor.Text); Assert.IsTrue(popup.IsOpen);
                    CollectionAssert.AreEqual(candidates, suggestions.Items.Cast<string>().ToArray()); Assert.AreEqual(-1, suggestions.SelectedIndex);
                    CollectionAssert.AreEqual(candidates, row.Chart.InstallDestinationSuggestions.ToArray());
                    Assert.AreEqual(string.Empty, row.Chart.InstallDestination); Assert.AreEqual(string.Empty, column.GetEditText(row));
                    Assert.AreEqual(0, completed.Count); Assert.AreEqual(realized, rows.RealizedRowCount);

                    Assert.IsTrue(table.HandleKeyDown(Key.Return, ModifierKeys.None));
                    Assert.AreEqual(1, completed.Count); Assert.IsTrue(completed[0].Commit);
                    Assert.AreSame(row, completed[0].Row); Assert.AreSame(column, completed[0].Column);
                    Assert.AreEqual("instl_dst", completed[0].EditPropertyName); Assert.AreEqual(input, completed[0].Text);
                    Assert.IsFalse(popup.IsOpen);
                    window.Content = null;
                    // 表のLoaded／Unloadedで購読と再描画が切り替わるため、Backgroundまで消化する。
                    TestUiDispatcherHost.ProcessQueuedBackground();
                    Assert.IsFalse(table.IsLoaded);
                    ResourceService.Current.ChangeCulture("ja-JP");
                    window.Content = table;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    // 表のLoaded／Unloadedで購読と再描画が切り替わるため、Backgroundまで消化する。
                    TestUiDispatcherHost.ProcessQueuedBackground();
                    Assert.IsTrue(table.IsLoaded); Assert.AreSame(table, window.Content); Assert.AreSame(rows, table.ItemsSource); Assert.AreSame(row, rows[3]);
                    Assert.IsTrue(table.GetRowEntry(row).Drawings.ContainsKey(3), "再Loaded後も最新語で実描画します。");
                    Assert.AreEqual("▼ " + Resources.InstallDestination_SelectCandidate, table.GetCellValue(row, column, out cacheHit).Text); Assert.IsTrue(cacheHit);
                    Assert.AreEqual(1, completed.Count);
                }
                finally
                {
                    window.Content = null; table.ItemsSource = null; table.CellEditEnded -= onEnded;
                    window.Close(); rows.DisposeRealizedRows();
                }
            }
            finally
            {
                ResourceService.Current.ChangeCulture(previousResourcesCulture?.Name ?? "en-US");
                Resources.Culture = previousResourcesCulture;
                TestUiDispatcherHost.ProcessQueuedPresentation();
            }
        });
    }

    /// <summary>実ChangeCultureのja→en通知だけで、保持した保留仮想行のWARNING列の要約・tooltip・描画を現在語へ更新し、強調・行・選択・scrollを保つことを確認します。警告の事実が新しくなった行は新しい事実の現在語になり、非表示中の言語変更も再表示で最新語になります。</summary>
    /// <remarks>警告が翻訳済み文字列を保持して言語変更後も旧語のまま表示される誤りと、言語通知から描画への接続漏れを検出します。表示済み表の言語切替後は明示的な描画失効を行いません。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void WarningLanguage_ChangeCultureRedrawsRetainedWarningCellsAndTooltips()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            CultureInfo? previousResourcesCulture = Resources.Culture;
            CultureInfo threadCulture = CultureInfo.CurrentCulture;
            try
            {
                ResourceService.Current.ChangeCulture("ja-JP");
                PackageChartEntry[] entries = [.. Enumerable.Range(0, 64).Select(index => PackageChartEntry.FromChart(
                    ChartFileProjection.WithPackageState(ChartTestValues.Empty() with
                    {
                        Path = @"C:\pending\" + index + ".bms", Md5 = index.ToString("x32"),
                        RawTitle = "Pending " + index.ToString("D2", CultureInfo.InvariantCulture)
                    }, string.Empty, string.Empty, string.Empty, [],
                    [ChartWarning.CreateWithPathPair(ChartWarningKind.InstallEstimationAmbiguous, @"C:\BMS\A" + index, @"C:\BMS\B" + index)])))];
                List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildPackageRows(entries);
                var rows = new ChartListVirtualView(sourceRows, ChartListOrder.CreateTitleAscending(sourceRows), LibraryChartRow.FromSourceRow);
                var owner = new MainChartListViewModel();
                owner.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
                var settings = new CustomTableColumnSettings();
                foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings)) { layout.Visibility = Visibility.Hidden; }
                settings.Warning.Visibility = Visibility.Visible;
                settings.Warning.DisplayIndex = 0; settings.Warning.Width = 200;
                var table = new CustomTableView { Width = 260, Height = 150, HeaderHeight = 20, RowHeight = 25, DataContext = owner, ColumnsSettings = settings, ItemsSource = rows };
                var window = new Window { Width = 300, Height = 200, Content = table };
                try
                {
                    scope.ShowAndWaitForContentRendered(window);
                    CustomTableColumn column = table.VisibleColumns.Single(item => item.Id == "Warning");
                    object columns = table.Columns;
                    ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
                    vertical.Value = 2;
                    table.SelectedIndex = 3;
                    var row = (LibraryChartRow)rows[3];
                    Assert.AreSame(entries[3], row.PackageEntry);
                    var point = new Point(15, table.HeaderHeight + (3 - table.FirstVisibleRowIndex + 0.5) * table.RowHeight);
                    RenderExistingTable(table);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    CustomTableRowDrawing japaneseDrawing = table.GetRowEntry(row).Drawings[3];
                    string japaneseDigest = "[1] " + Resources.WarningDigest_InstallEstimationAmbiguous;
                    string japaneseDetail = string.Format(Resources.Warning_InstallEstimationAmbiguous, @"C:\BMS\A3", @"C:\BMS\B3");
                    Assert.AreEqual(japaneseDigest, table.GetCellValue(row, column, out _).Text);
                    Assert.AreEqual(japaneseDetail, table.ResolveCellToolTip(table.HitTestTable(point)));
                    Assert.IsTrue(table.HasHighlightedWarning(row));
                    double verticalOffset = vertical.Value;
                    int realized = rows.RealizedRowCount;
                    Assert.IsTrue(realized > 0 && realized < rows.Count);

                    ResourceService.Current.ChangeCulture("en-US");
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    // 明示的な失効なしに、通知だけで現在語の要約・詳細・描画になります。
                    CustomTableRowDrawing englishDrawing = table.GetRowEntry(row).Drawings[3];
                    Assert.AreNotSame(japaneseDrawing, englishDrawing);
                    string englishDigest = "[1] " + Resources.WarningDigest_InstallEstimationAmbiguous;
                    string englishDetail = string.Format(Resources.Warning_InstallEstimationAmbiguous, @"C:\BMS\A3", @"C:\BMS\B3");
                    Assert.AreNotEqual(japaneseDigest, englishDigest);
                    Assert.AreNotEqual(japaneseDetail, englishDetail);
                    Assert.AreEqual(englishDigest, table.GetCellValue(row, column, out bool cacheHit).Text); Assert.IsTrue(cacheHit);
                    Assert.AreEqual(englishDetail, table.ResolveCellToolTip(table.HitTestTable(point)));
                    Assert.IsTrue(table.HasHighlightedWarning(row));
                    Assert.AreSame(threadCulture, CultureInfo.CurrentCulture);
                    Assert.AreSame(table, window.Content); Assert.AreSame(rows, table.ItemsSource); Assert.AreSame(columns, table.Columns);
                    Assert.AreSame(row, rows[3]); Assert.AreSame(entries[3], row.PackageEntry);
                    Assert.AreEqual(3, table.SelectedIndex);
                    Assert.AreEqual(verticalOffset, vertical.Value); Assert.AreEqual(realized, rows.RealizedRowCount);

                    // 行3の警告の事実を新しい推定結果で更新すると、その行は新しい事実の現在語になり、他の保持行も現在語のままです。
                    var result = new InstallEstimationResult
                    {
                        Confidence = InstallEstimationConfidence.Low,
                        HasViableDestination = true,
                        LowConfidenceKind = InstallEstimationLowConfidenceKind.AmbiguousCandidates
                    };
                    result.Candidates.Add(new InstallEstimationCandidate { DirectoryPath = @"C:\BMS\NewA", MetadataRelation = InstallEstimationMetadataRelation.Related, DisplayTitle = "T", DisplayArtist = "A" });
                    result.Candidates.Add(new InstallEstimationCandidate { DirectoryPath = @"C:\BMS\NewB", MetadataRelation = InstallEstimationMetadataRelation.Related, DisplayTitle = "T", DisplayArtist = "A" });
                    entries[3].ApplyInstallEstimationResult(result);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(string.Format(Resources.Warning_InstallEstimationAmbiguous, @"C:\BMS\NewA", @"C:\BMS\NewB"), table.ResolveCellToolTip(table.HitTestTable(point)));
                    Assert.AreEqual(englishDigest, table.GetCellValue(row, column, out _).Text);

                    // 非表示の間に言語を変えても、再表示後は最新語で描画します。
                    window.Content = null;
                    // 表のLoaded／Unloadedで購読と再描画が切り替わるため、Backgroundまで消化する。
                    TestUiDispatcherHost.ProcessQueuedBackground();
                    Assert.IsFalse(table.IsLoaded);
                    ResourceService.Current.ChangeCulture("ja-JP");
                    window.Content = table;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    // 表のLoaded／Unloadedで購読と再描画が切り替わるため、Backgroundまで消化する。
                    TestUiDispatcherHost.ProcessQueuedBackground();
                    Assert.IsTrue(table.IsLoaded);
                    Assert.AreSame(row, rows[3]);
                    Assert.AreEqual(japaneseDigest, table.GetCellValue(row, column, out _).Text);
                    Assert.AreEqual(string.Format(Resources.Warning_InstallEstimationAmbiguous, @"C:\BMS\NewA", @"C:\BMS\NewB"), table.ResolveCellToolTip(table.HitTestTable(point)));
                }
                finally
                {
                    window.Content = null; table.ItemsSource = null;
                    window.Close(); rows.DisposeRealizedRows();
                }
            }
            finally
            {
                ResourceService.Current.ChangeCulture(previousResourcesCulture?.Name ?? "en-US");
                Resources.Culture = previousResourcesCulture;
                TestUiDispatcherHost.ProcessQueuedPresentation();
            }
        });
    }

    /// <summary>追加辞書を含む言語往復で、本体表の導入先見出しと要約表のSTATUS見出しの Header・UIA名・実描画が現在の資源値へ追従し、列・行・編集中editorを作り直さないことを確認します。</summary>
    /// <remarks>ja→追加→ja→追加→en の各段で、通知とキュー処理の後に明示的な描画失効なしで見出し帯を描画し、同じ辞書の画素一致と別辞書の相違を観測します。追加辞書は実行専用profileのlang配下へ置き、既存ファイルと辞書探索を必ず復元します。</remarks>
    [TestMethod]
    [DataRow("main")]
    [DataRow("summary")]
    [DoNotParallelize]
    public void ColumnHeaderLanguage_AdditionalDictionaryRoundTripKeepsColumnsRowsAndEdit(string kind)
    {
        const string additionalCulture = "it-IT";
        const string additionalTitle = "ADDL DST TITLE";
        const string additionalArtist = "ADDL DST ARTIST";
        const string additionalStatus = "ADDL STATUS";
        const string additionalHttp404 = "ADDL 404";
        const string additionalLoading = "ADDL LOADING";
        bool isMain = kind == "main";
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            CultureInfo? previousResourcesCulture = Resources.Culture;
            string languageDirectory = ApplicationPathPolicy.Current.LanguageDirectory;
            string languagePath = Path.Combine(languageDirectory, additionalCulture + ".json");
            byte[]? languageBackup = File.Exists(languagePath) ? File.ReadAllBytes(languagePath) : null;
            ChartListVirtualView? mainRows = null;
            CustomTableView? table = null;
            Window? window = null;
            EventHandler<CustomTableCellEditEndedEventArgs>? onEnded = null;
            EventHandler<CustomTableSortRequestedEventArgs>? onSort = null;
            try
            {
                Assert.AreEqual(Path.GetFullPath(Path.Combine(ApplicationPathPolicy.Current.ProfileDirectory, "lang")), Path.GetFullPath(languageDirectory));
                Directory.CreateDirectory(languageDirectory);
                File.WriteAllText(languagePath, "{\"_language_name\":\"Header regression\","
                    + "\"Header_InstallDstTitle\":\"" + additionalTitle + "\","
                    + "\"Header_InstallDstArtist\":\"" + additionalArtist + "\","
                    + "\"Playlist_summary_status_header\":\"" + additionalStatus + "\","
                    + "\"Playlist_sync_status_404\":\"" + additionalHttp404 + "\","
                    + "\"Statusbar_progress_phase_playlist_loading\":\"" + additionalLoading + "\","
                    + "\"Statusbar_progress_detail_separator_format\":\"{0} ADDL SEP {1}\"}");
                JsonLanguageCatalog.Invalidate(additionalCulture);
                JsonLanguageCatalog.InvalidateLanguages();
                ResourceService.Current.ChangeCulture("ja-JP");

                string[] headerIds = isMain ? ["InstallDstTitle", "InstallDstArtist"] : ["Status"];
                string[] ExpectedHeaders() => isMain ? [Resources.Header_InstallDstTitle, Resources.Header_InstallDstArtist] : [Resources.Playlist_summary_status_header];
                string editedId;
                if (isMain)
                {
                    string[] candidates = [@"C:\BMS\CandidateA", @"C:\BMS\CandidateB"];
                    PackageChartEntry[] entries = [.. Enumerable.Range(0, 64).Select(index => PackageChartEntry.FromChart(
                        ChartFileProjection.WithPackageState(ChartTestValues.Empty() with
                        {
                            Path = @"C:\pending\" + index + ".bms", Md5 = index.ToString("x32"),
                            RawTitle = "Pending " + index.ToString("D2", CultureInfo.InvariantCulture)
                        }, string.Empty, string.Empty, string.Empty, candidates, [])))];
                    List<ChartListSourceRow> sourceRows = ChartListSourceRow.BuildPackageRows(entries);
                    mainRows = new ChartListVirtualView(sourceRows, ChartListOrder.CreateTitleAscending(sourceRows), LibraryChartRow.FromSourceRow);
                    editedId = "InstallDst";
                    var owner = new MainChartListViewModel();
                    owner.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
                    var settings = new CustomTableColumnSettings();
                    foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings)) { layout.Visibility = Visibility.Hidden; }
                    settings.InstallDst.Visibility = settings.InstallDstTitle.Visibility = settings.InstallDstArtist.Visibility = Visibility.Visible;
                    settings.InstallDst.DisplayIndex = 0; settings.InstallDst.Width = 90;
                    settings.InstallDstArtist.DisplayIndex = 1; settings.InstallDstArtist.Width = 130;
                    settings.InstallDstTitle.DisplayIndex = 2; settings.InstallDstTitle.Width = 150;
                    table = new CustomTableView { Width = 380, Height = 150, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, HeaderHeight = 20, RowHeight = 25, DataContext = owner, ColumnsSettings = settings, ItemsSource = mainRows };
                }
                else
                {
                    // 同期状態は事実だけを持たせ、行3は未読込みの案内を付ける行にします。
                    var http404 = new PlaylistSyncRuntimeStatus
                    {
                        Kind = PlaylistSyncStatusKind.Http404,
                        HasResult = true,
                        CheckedAt = new DateTime(2026, 3, 8, 20, 0, 0),
                        PageUri = "https://example.invalid/status.html",
                        Diagnostic = "diagnostic 原文"
                    };
                    object[] rows = [.. Enumerable.Range(0, 64).Select(index => new PlaylistSummaryRow
                    {
                        Name = "Row " + index.ToString("D2", CultureInfo.InvariantCulture), SyncStatus = http404, IsEntryLoadingPending = index == 3
                    })];
                    editedId = "Name";
                    var settings = new PlaylistSummaryColumnSettings();
                    foreach (PlaylistSummaryColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumeratePlaylistSummaryColumnLayouts(settings)) { layout.Visibility = Visibility.Hidden; }
                    settings.Name.Visibility = settings.Status.Visibility = Visibility.Visible;
                    settings.Name.DisplayIndex = 0; settings.Name.Width = 110;
                    settings.Status.DisplayIndex = 1; settings.Status.Width = 130;
                    table = new CustomTableView { Width = 300, Height = 150, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, HeaderHeight = 20, RowHeight = 25, PlaylistSummaryColumnsSettings = settings, ItemsSource = rows };
                }
                window = new Window { Width = 420, Height = 220, ShowInTaskbar = false, Content = table };
                var completed = new List<CustomTableCellEditEndedEventArgs>();
                int sorts = 0;
                onEnded = (_, args) => completed.Add(args);
                onSort = (_, _) => sorts++;
                table.CellEditEnded += onEnded;
                table.SortRequested += onSort;
                scope.ShowAndWaitForContentRendered(window);
                CustomTableView shown = table;
                System.Collections.IList items = shown.ItemsSource;
                // 並べ替え状態は列見出しの操作を通さず直接設定し、言語変更で変わらないことだけを観測する。
                CustomTableColumn sortColumn = shown.VisibleColumns.First(column => !string.IsNullOrWhiteSpace(column.SortMemberPath));
                shown.SetCurrentValue(CustomTableView.SortColumnNameProperty, sortColumn.SortMemberPath);
                shown.SetCurrentValue(CustomTableView.SortDirectionProperty, ListSortDirection.Ascending);
                ScrollBar vertical = shown.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
                vertical.Value = 2;
                shown.SelectedIndex = 3;
                CustomTableColumn editedColumn = shown.VisibleColumns.Single(column => column.Id == editedId);
                object selectedRow = items[3]!;
                Assert.IsTrue(shown.HandleKeyDown(Key.F2, ModifierKeys.None));
                TextBox editor = shown.Children.OfType<Canvas>().SelectMany(canvas => canvas.Children.OfType<TextBox>()).Single();
                const string draft = "uncommitted draft";
                editor.Text = draft;

                object columns = shown.Columns;
                CustomTableColumn[] columnInstances = [.. shown.Columns];
                string[] layoutBefore = [.. shown.VisibleColumns.Select(column => column.Id + ":" + column.Width + ":" + column.DisplayIndex)];
                double verticalOffset = vertical.Value;
                int realizedBefore = isMain ? mainRows!.RealizedRowCount : 0;
                AutomationPeer root = UIElementAutomationPeer.CreatePeerForElement(shown) ?? throw new AssertFailedException("表peerがありません。");

                Dictionary<string, byte[]> pixelsByDictionary = [];
                void AssertStage(string dictionary)
                {
                    string[] expected = ExpectedHeaders();
                    for (int index = 0; index < headerIds.Length; index++)
                    {
                        CustomTableColumn column = shown.Columns.Single(item => item.Id == headerIds[index]);
                        Assert.AreEqual(expected[index], column.Header, dictionary + " Header " + headerIds[index]);
                        AutomationPeer header = root.GetChildren().Single(child => child.GetAutomationId() == "header:" + headerIds[index]);
                        Assert.AreEqual(expected[index], header.GetName(), dictionary + " UIA名 " + headerIds[index]);
                    }
                    // 明示的な描画失効なしで、通知とキュー処理だけが反映した実描画を読む。
                    byte[] pixels = RenderHeaderBandAsIs(shown);
                    if (pixelsByDictionary.TryGetValue(dictionary, out byte[]? previous)) { CollectionAssert.AreEqual(previous, pixels, dictionary + " 同じ辞書の見出し帯画素"); }
                    else { pixelsByDictionary[dictionary] = pixels; }

                    if (!isMain)
                    {
                        // 明示的な描画失効なしで、同じ行のSTATUSセル値とtooltipが現在の資源値へ追従する。
                        string expectedStatus = Resources.Playlist_sync_status_404;
                        string expectedDetail = string.Format(CultureInfo.CurrentCulture, Resources.Statusbar_progress_detail_separator_format,
                            string.Join(Environment.NewLine, expectedStatus, DateTimeDisplayFormatter.FormatTimestamp(new DateTime(2026, 3, 8, 20, 0, 0)),
                                "https://example.invalid/status.html", "diagnostic 原文"),
                            Resources.Statusbar_progress_phase_playlist_loading);
                        if (dictionary == "added")
                        {
                            Assert.AreEqual(additionalHttp404, expectedStatus);
                            StringAssert.Contains(expectedDetail, additionalLoading);
                            StringAssert.Contains(expectedDetail, " ADDL SEP ");
                        }
                        CustomTableColumn statusColumn = shown.Columns.Single(item => item.Id == "Status");
                        var summaryRow = (PlaylistSummaryRow)selectedRow;
                        Assert.AreEqual(expectedStatus, summaryRow.Status, dictionary);
                        Assert.AreEqual(expectedDetail, summaryRow.StatusDetail, dictionary);
                        Assert.AreEqual(expectedStatus, shown.GetCellValue(selectedRow, statusColumn, out bool cellHit).Text, dictionary + " STATUSセル値");
                        Assert.IsTrue(cellHit);
                        CustomTableHitTestResult statusCell = shown.HitTestTable(new Point(110 + 20, 20 + 25 * 1 + 12));
                        Assert.AreEqual(CustomTableHitKind.Cell, statusCell.Kind, dictionary);
                        Assert.AreSame(statusColumn, statusCell.Column, dictionary);
                        Assert.AreSame(selectedRow, statusCell.Row, dictionary);
                        Assert.AreEqual(expectedDetail, shown.ResolveCellToolTip(statusCell), dictionary + " STATUS tooltip");
                    }
                    Assert.AreSame(columns, shown.Columns);
                    CollectionAssert.AreEqual(columnInstances, shown.Columns.ToArray());
                    CollectionAssert.AreEqual(layoutBefore, shown.VisibleColumns.Select(column => column.Id + ":" + column.Width + ":" + column.DisplayIndex).ToArray());
                    Assert.AreSame(items, shown.ItemsSource);
                    Assert.AreSame(selectedRow, items[3]);
                    Assert.AreEqual(3, shown.SelectedIndex);
                    Assert.IsTrue(shown.IsCurrentCell(3, editedColumn));
                    Assert.AreEqual(sortColumn.SortMemberPath, shown.SortColumnName);
                    Assert.AreEqual(ListSortDirection.Ascending, shown.SortDirection);
                    Assert.AreEqual(verticalOffset, vertical.Value);
                    Assert.AreSame(editor, shown.Children.OfType<Canvas>().SelectMany(canvas => canvas.Children.OfType<TextBox>()).Single());
                    Assert.AreEqual(draft, editor.Text);
                    Assert.AreEqual(0, completed.Count);
                    Assert.AreEqual(0, sorts);
                    if (isMain) { Assert.AreEqual(realizedBefore, mainRows!.RealizedRowCount); }
                }

                TestUiDispatcherHost.ProcessQueuedPresentation();
                AssertStage("ja-JP");
                foreach ((string culture, string dictionary) in new[] { (additionalCulture, "added"), ("ja-JP", "ja-JP"), (additionalCulture, "added"), ("en-US", "en-US") })
                {
                    ResourceService.Current.ChangeCulture(culture);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    if (dictionary == "added")
                    {
                        CollectionAssert.AreEqual(isMain ? new[] { additionalTitle, additionalArtist } : new[] { additionalStatus }, ExpectedHeaders(), "追加辞書の見出しは同梱辞書と異なる値です。");
                    }
                    AssertStage(dictionary);
                }
                Assert.AreEqual(3, pixelsByDictionary.Count);
                CollectionAssert.AreNotEqual(pixelsByDictionary["ja-JP"], pixelsByDictionary["added"], "追加辞書の見出しは日本語と異なる描画になります。");
                CollectionAssert.AreNotEqual(pixelsByDictionary["en-US"], pixelsByDictionary["added"], "追加辞書の見出しは英語と異なる描画になります。");
                Assert.IsTrue(shown.HandleKeyDown(Key.Escape, ModifierKeys.None));
            }
            finally
            {
                try
                {
                    if (table != null)
                    {
                        if (onEnded != null) { table.CellEditEnded -= onEnded; }
                        if (onSort != null) { table.SortRequested -= onSort; }
                    }
                    if (window != null) { window.Content = null; }
                    if (table != null) { table.ItemsSource = null; }
                    window?.Close();
                    mainRows?.DisposeRealizedRows();
                }
                finally
                {
                    if (languageBackup != null) { File.WriteAllBytes(languagePath, languageBackup); }
                    else { File.Delete(languagePath); }
                    JsonLanguageCatalog.Invalidate(additionalCulture);
                    JsonLanguageCatalog.InvalidateLanguages();
                    ResourceService.Current.ChangeCulture(previousResourcesCulture?.Name ?? "en-US");
                    Resources.Culture = previousResourcesCulture;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                }
            }
        });
    }

    /// <summary>描画失効を呼ばず、現在の描画内容のまま見出し帯の画素を読みます。</summary>
    private static byte[] RenderHeaderBandAsIs(CustomTableView table)
    {
        // 描画失効は呼ばず、通知が残した未描画分を描画段階まで進めてから読む。
        table.UpdateLayout();
        table.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var bitmap = new RenderTargetBitmap((int)table.ActualWidth, (int)table.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(table);
        // 表は左上へ寄せて配置するため、描画結果の上端が見出し帯の上端です。
        int height = (int)table.HeaderHeight;
        int stride = bitmap.PixelWidth * 4;
        byte[] pixels = new byte[stride * height];
        bitmap.CopyPixels(new Int32Rect(0, 0, bitmap.PixelWidth, height), pixels, stride, 0);
        return pixels;
    }

    /// <summary>実見出しresizeで40→41→広幅→40を描画し、URL値・tooltip・hit/actionとカーソルが追随することを確認します。未編集中は同じ列と他列の保持値を保ち、未表示行を追加評価しません。設定通知あり/直接Columnsの両入口を使います。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void UrlWidth_ActualResizeRefreshesOnlyUrlValuesAndKeepsOtherRetainedCells(bool subscribeSettings)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var settings = new CustomTableColumnSettings();
            settings.Url1.Width = 40;
            settings.Url1.Visibility = Visibility.Visible;
            var visited = new HashSet<object>();
            int otherCalls = 0;
            object[] rows = [.. Enumerable.Range(0, 64).Select(index => new DisplayRow { Text = "https://example.test/long/path/that/remains/truncated/" + index })];
            var url = new CustomTableColumn("Url1", "URL1", settings.Url1, 0, null, TextAlignment.Center,
                row => { visited.Add(row); return "\uE14F"; },
                tooltipSelector: _ => "download tooltip", cellKind: CustomTableCellKind.DownloadIcon,
                editTextSelector: row => { visited.Add(row); return ((DisplayRow)row).Text; }, editOnRepeatClick: false);
            var other = new CustomTableColumn("Other", "OTHER", new CustomTableColumnSettings.ColumnLayout { Width = 100 }, 1, null,
                TextAlignment.Left, _ => { otherCalls++; return "unchanged"; });
            var table = new CustomTableView { Width = 360, Height = 120, HeaderHeight = 20, RowHeight = 25, ColumnsSettings = subscribeSettings ? settings : null, Columns = [url, other], ItemsSource = rows };
            int actions = 0;
            table.CellActionRequested += (_, args) => { Assert.AreSame(url, args.Hit.Column); actions++; };
            try
            {
                scope.ShowAndWaitForContentRendered(new Window { Content = table, SizeToContent = SizeToContent.WidthAndHeight });
                RenderExistingTable(table);
                object[] initialVisited = [.. visited];
                int initialOtherCalls = otherCalls;
                Assert.IsTrue(initialVisited.Length > 0 && initialVisited.Length < rows.Length);
                CustomTableRowDrawing priorDrawing = table.GetRowEntry(rows[0]).Drawings[0];
                foreach (int width in new[] { 41, 200, 40 })
                {
                    double edge = url.Width;
                    CustomTableHitTestResult resize = table.HitTestTable(new Point(edge, 10));
                    Assert.AreEqual(CustomTableHitKind.HeaderResize, resize.Kind); Assert.AreSame(url, resize.Column);
                    table.BeginColumnResize(resize, edge);
                    Assert.AreSame(Cursors.SizeWE, table.Cursor);
                    table.UpdateColumnResize(width);
                    table.EndColumnResize();
                    Assert.IsNull(table.Cursor);
                    Assert.AreSame(url, table.Columns[0]); Assert.AreSame(other, table.Columns[1]); Assert.AreEqual(width, url.Width);
                    RenderExistingTable(table);
                    CustomTableCellValue value = table.GetCellValue(rows[0], url, out bool hit);
                    Assert.IsTrue(hit);
                    Assert.AreEqual(width > 40 ? CustomTableCellKind.Text : CustomTableCellKind.DownloadIcon, value.CellKind);
                    Assert.AreEqual(width > 40 ? ((DisplayRow)rows[0]).Text : "\uE14F", value.Text);
                    Assert.AreEqual(width > 40 ? TextAlignment.Left : TextAlignment.Center, value.Alignment);
                    CustomTableRowDrawing drawing = table.GetRowEntry(rows[0]).Drawings[0];
                    Assert.AreNotSame(priorDrawing, drawing, "現在幅で実描画を作り直します。"); priorDrawing = drawing;
                    var point = new Point(15, 35);
                    CustomTableHitTestResult cell = table.HitTestTable(point);
                    Assert.AreEqual(CustomTableHitKind.Cell, cell.Kind); Assert.AreSame(url, cell.Column);
                    Assert.AreEqual(width > 40 ? ((DisplayRow)rows[0]).Text : "download tooltip", table.ResolveCellToolTip(cell));
                    int beforeActions = actions;
                    Assert.IsTrue(table.HandleCellMouseDown(cell, MouseButton.Left, ModifierKeys.None, 1, point));
                    Assert.AreEqual(beforeActions + (width == 40 ? 1 : 0), actions);
                    Assert.AreEqual(initialOtherCalls, otherCalls, "他列の保持値を再取得しません。");
                    CollectionAssert.AreEquivalent(initialVisited, visited.ToArray(), "幅変更で未表示の行を読みません。");
                }
            }
            finally { table.EndColumnResize(); ReleaseTable(table); }
        });
    }

    /// <summary>実表示済みの同じ行を可→不可→可へ切り替え、editor・選択・scrollを保ちます。旧VMの通知は無効で、同じ表/行の再Loadedは最新文脈を描画します。切離し中のcache寿命は固定しません。</summary>
    [TestMethod]
    public void InstallDestinationPermission_ContextChangesRefreshValuesWithoutReplacingEditor()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var first = new MainChartListViewModel(); var second = new MainChartListViewModel();
            first.SetOperationContext(MainViewUpdateMode.FullScanAllChartsFilterSelected);
            var settings = new CustomTableColumnSettings();
            foreach (CustomTableColumnSettings.ColumnLayout? layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings)) { layout.Visibility = Visibility.Hidden; }
            settings.InstallDst.Visibility = Visibility.Visible; settings.InstallDst.Width = 320;
            ChartFile chart = ChartFileProjection.WithPackageState(ChartTestValues.Empty() with { Path = @"C:\owned\a.bms", Md5 = new string('a', 32) },
                @"C:\destination", string.Empty, string.Empty, [@"C:\candidate"], []);
            var row = LibraryChartRow.FromChartFile(chart);
            var table = new CustomTableView { Width = 220, Height = 120, DataContext = first, ColumnsSettings = settings, ItemsSource = new object[] { row } };
            var popup = (Popup)(typeof(CustomTableView).GetField("editSuggestionPopup", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(table)
                ?? throw new AssertFailedException("編集popupがありません。"));
            scope.TrackPopup(popup);
            var window = new Window { Width = 260, Height = 170, Content = table };
            try
            {
                scope.ShowAndWaitForContentRendered(window);
                CustomTableColumn column = table.VisibleColumns.Single();
                Assert.IsTrue(table.HandleKeyDown(System.Windows.Input.Key.Down, System.Windows.Input.ModifierKeys.None));
                Assert.IsTrue(table.HandleKeyDown(System.Windows.Input.Key.F2, System.Windows.Input.ModifierKeys.None));
                TextBox editor = table.Children.OfType<Canvas>().SelectMany(canvas => canvas.Children.OfType<TextBox>()).Single();
                editor.Text = "uncommitted";
                double offset = table.HorizontalOffset;
                AssertValue(true);
                ChangeContext(first, MainViewUpdateMode.FolderFilterSelected);
                AssertValue(false);
                ChangeContext(first, MainViewUpdateMode.FullScanAllChartsFilterSelected);
                AssertValue(true);
                table.DataContext = second;
                AssertValue(false);
                ChangeContext(first, MainViewUpdateMode.FolderFilterSelected);
                Assert.AreEqual(chart.InstallDestination, table.GetCellValue(row, column, out bool hit).Text); Assert.IsTrue(hit);
                Assert.AreSame(editor, table.Children.OfType<Canvas>().SelectMany(canvas => canvas.Children.OfType<TextBox>()).Single());
                Assert.AreEqual("uncommitted", editor.Text); Assert.AreEqual(offset, table.HorizontalOffset);
                Assert.AreSame(row, table.GetSelectedRowsSnapshot().Single());
                table.HandleKeyDown(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None);
                table.HandleKeyDown(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None);
                AssertValue(false);
                System.Collections.IList sourceRows = table.ItemsSource ?? throw new AssertFailedException("再Loaded前の行集合がありません。");
                double reloadOffset = table.HorizontalOffset;
                window.Content = null;
                // 表のLoaded／Unloadedで購読と再描画が切り替わるため、Backgroundまで消化する。
                TestUiDispatcherHost.ProcessQueuedBackground();
                Assert.IsFalse(table.IsLoaded);
                ChangeContext(second, MainViewUpdateMode.FullScanAllChartsFilterSelected);
                window.Content = table;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                // 表のLoaded／Unloadedで購読と再描画が切り替わるため、Backgroundまで消化する。
                TestUiDispatcherHost.ProcessQueuedBackground();
                Assert.IsTrue(table.IsLoaded); AssertValue(true);
                Assert.AreSame(table, window.Content); Assert.AreSame(sourceRows, table.ItemsSource);
                Assert.AreSame(row, table.ItemsSource[0]); Assert.AreSame(row, table.GetSelectedRowsSnapshot().Single());
                Assert.AreEqual(chart.InstallDestination, column.GetEditText(row));
                Assert.AreEqual(reloadOffset, table.HorizontalOffset);

                // 実表示採用と同じcommit/公開を使う。初期準備用のSetOperationContextだけでは通知しない。
                static void ChangeContext(MainChartListViewModel owner, MainViewUpdateMode mode)
                    => owner.PublishOperationContextChanged(owner.CommitOperationContextWithoutNotification(mode));

                void AssertValue(bool allowed)
                {
                    RenderExistingTable(table);
                    Assert.AreEqual(allowed ? "▼ " + chart.InstallDestination : chart.InstallDestination, table.GetCellValue(row, column, out _).Text);
                }
            }
            finally { window.Content = null; table.ItemsSource = null; window.Close(); }
        });
    }

    /// <summary>実コレクションのReset後に同じ行参照の文字と警告を再評価し、旧値が残らないことを確認します。</summary>
    [TestMethod]
    public void CustomTableView_ResetUpdatesValuesAndWarnings()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var row = new DisplayRow();
            var rows = new ResettableRows { row };
            CustomTableView view = CreateDisplayTable(rows);
            try
            {
                RenderExistingTable(view);
                row.Text = "after reset";
                row.HasHighlightedWarning = true;
                rows.Reset();
                AssertDisplayValue(view, row, "after reset", true);
                view.ItemsSource = new ObservableCollection<object>();
                Assert.IsNull(view.HitTestTable(new Point(10d, 30d)).Row);
                RenderExistingTable(view);
            }
            finally { ReleaseTable(view); }
        });
    }

    /// <summary>非表示中の無通知変更と同じ行参照を含むソース交換から復帰しても、最新の文字・警告を表示することを確認します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CustomTableView_ShownAfterHiddenChangeUsesLatestValues(bool swapSource)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var row = new DisplayRow();
            CustomTableView view = CreateDisplayTable(new List<object> { row });
            try
            {
                RenderExistingTable(view);
                view.ApplyVisibilityChanged(false);
                row.Text = "changed while hidden";
                row.HasHighlightedWarning = true;
                if (swapSource)
                {
                    view.ItemsSource = new List<object> { row };
                }
                view.ApplyVisibilityChanged(true);
                AssertDisplayValue(view, row, "changed while hidden", true);
            }
            finally { ReleaseTable(view); }
        });
    }

    /// <summary>ソース交換と同一IDの列定義交換が、それぞれ最新の取得規則を反映することを確認します。</summary>
    [TestMethod]
    public void CustomTableView_SourceAndSameIdColumnReplacementUseLatestValues()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var row = new DisplayRow();
            CustomTableView view = CreateDisplayTable(new List<object> { row });
            try
            {
                RenderExistingTable(view);
                row.Text = "new source";
                view.ItemsSource = new List<object> { row };
                AssertDisplayValue(view, row, "new source", false);
                CustomTableColumn original = view.Columns[0];
                view.Columns = [new CustomTableColumn(original.Id, original.Header, original.Layout, 0, null,
                    TextAlignment.Left, _ => "new column")];
                Assert.AreEqual("new column", view.GetCellValue(row, view.Columns[0], out _).Text);
                RenderExistingTable(view);
            }
            finally { ReleaseTable(view); }
        });
    }

    /// <summary>範囲の重なりでは値を保持し、最後の参照の完全離脱後は再進入で最新の文字・警告へ更新することを確認します。</summary>
    /// <remarks>通知対応/非対応と0/5位置の重複参照を確認します。スクロール入口が実際の可視状態で購読寿命を判断するため実Windowを使い、表示と閉鎖は共通scopeへ任せます。可視になる前の値は読みません。</remarks>
    [TestMethod]
    [DoNotParallelize]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void CustomTableView_RetainsOverlapAndReleasesLastReference(bool notifying, bool duplicate)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            DisplayRow row = notifying ? new NotifyingDisplayRow() : new DisplayRow();
            var rows = Enumerable.Range(0, 30).Select(_ => (object)new DisplayRow()).ToList();
            rows[0] = row;
            if (duplicate) { rows[5] = row; }
            CustomTableView view = CreateUnarrangedDisplayTable(rows);
            try
            {
                scope.ShowAndWaitForContentRendered(new Window { Content = view, SizeToContent = SizeToContent.WidthAndHeight });
                Assert.IsTrue(view.IsLoaded);
                Assert.IsTrue(view.IsVisible);
                Assert.AreEqual(5, view.CalculateDrawableRowCapacity());
                RenderExistingTable(view);
                int initialCalls = row.TextCalls;
                ScrollBar scroll = view.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
                scroll.Value = duplicate ? 10 : 5;
                RenderExistingTable(view);
                Assert.AreEqual(initialCalls, row.TextCalls, "保持範囲に残る行の値を取り直しません。");
                scroll.Value = 0;
                RenderExistingTable(view);
                Assert.AreEqual(initialCalls, row.TextCalls, "保持範囲内から表示へ戻して再描画しても、同じ値を再利用します。");
                scroll.Value = duplicate ? 11 : 6;
                RenderExistingTable(view);
                row.Text = "after reentry";
                row.HasHighlightedWarning = true;
                scroll.Value = 0;
                AssertDisplayValue(view, row, "after reentry", true);
                Assert.AreEqual(initialCalls + 1, row.TextCalls);
            }
            finally { view.ItemsSource = null; }
        });
    }

    /// <summary>一行の通知後にその行だけ値を取り直し、他行の値と警告を保持することを実表で確認します。</summary>
    [TestMethod]
    // Dispatcher待機中も同じ描画環境を所有し、他のLoadedテストによるパレット再構築を挟まない。
    [DoNotParallelize]
    public void CustomTableView_RowNotificationUpdatesOnlyChangedRow()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var row = new NotifyingDisplayRow();
            var unchanged = new DisplayRow();
            CustomTableView view = CreateDisplayTable(new List<object> { row, unchanged });
            try
            {
                RenderExistingTable(view);
                int unchangedCalls = unchanged.TextCalls;
                row.Text = "notified";
                row.HasHighlightedWarning = true;
                row.RaiseChanged();
                // 本番のRender優先度の失効配送後に、結果を描画・観測する処理の完了を待つ。
                DispatcherOperation observed = view.Dispatcher.InvokeAsync(() => AssertDisplayValue(view, row, "notified", true), DispatcherPriority.Background);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(observed.Task, "custom-table-row-notification");
                Assert.AreEqual(unchangedCalls, unchanged.TextCalls);
            }
            finally { ReleaseTable(view); }
        });
    }

    /// <summary>小さすぎる文字と行・見出し寸法は許容最小へ、NaNや無限大は既定へ補正することを確認し、保存設定の異常値で描画領域を壊す誤りを検出します。</summary>
    [TestMethod]
    public void CustomTableView_CoercesAppearanceMetricsToSupportedRanges()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var view = new CustomTableView
            {
                TextFontSize = 2d,
                RowHeight = 2d,
                HeaderHeight = 2d
            };

            Assert.AreEqual(BeMusicSeeker.Properties.Settings.MinCustomTableFontSize, view.TextFontSize);
            Assert.AreEqual(BeMusicSeeker.Properties.Settings.MinCustomTableRowHeight, view.RowHeight);
            Assert.AreEqual(BeMusicSeeker.Properties.Settings.MinCustomTableHeaderHeight, view.HeaderHeight);

            view.TextFontSize = double.NaN;
            view.RowHeight = double.PositiveInfinity;
            view.HeaderHeight = double.NaN;

            Assert.AreEqual(BeMusicSeeker.Properties.Settings.DefaultCustomTableFontSize, view.TextFontSize);
            Assert.AreEqual(BeMusicSeeker.Properties.Settings.DefaultCustomTableRowHeight, view.RowHeight);
            Assert.AreEqual(BeMusicSeeker.Properties.Settings.DefaultCustomTableHeaderHeight, view.HeaderHeight);
        });
    }

    /// <remarks>購読の開始と解除はWPFのLoaded・Unloadedで起きるため実Windowを使います。キャッシュの条件判断と値失効は同じクラスの下位ケースが担います。</remarks>
    /// <summary>実表示した表の行交換状態が現在のDataContextだけに追随し、変更前とUnloaded後の主体からは通知を受けないことを確認し、古い主体の購読残留を検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void CustomTableView_MainChartListLifecycleFollowsLoadedDataContext()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var first = new MainChartListViewModel();
            var second = new MainChartListViewModel();
            var view = new CustomTableView { DataContext = first };
            var window = new Window
            {
                Width = 320d,
                Height = 240d,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };

            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                Assert.IsTrue(view.IsLoaded);
                first.PrepareRowsReplacement();
                Assert.IsTrue(view.IsItemsSourceSwapPending);
                first.CancelRowsReplacement();
                Assert.IsFalse(view.IsItemsSourceSwapPending);

                view.DataContext = second;
                first.PrepareRowsReplacement();
                Assert.IsFalse(view.IsItemsSourceSwapPending, "Changing DataContext must detach the previous chart-list owner.");
                second.PrepareRowsReplacement();
                Assert.IsTrue(view.IsItemsSourceSwapPending);
                second.FailRowsReplacementPublish();
                Assert.IsFalse(view.IsItemsSourceSwapPending);
            }
            finally
            {
                window.Content = null;
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                window.Close();
            }

            second.PrepareRowsReplacement();
            Assert.IsFalse(view.IsItemsSourceSwapPending, "Unloaded controls must not retain the chart-list owner subscription.");
        });
    }

    /// <remarks>区間色の変換は下位ケース、選択時にも見えることは実WPF描画で分担します。</remarks>
    /// <summary>通常・選択・現在セルの各対象セル内で赤青の文字区間を確認し、別位置の色で対象セルの欠落を見逃す誤りを検出します。</summary>
    [TestMethod]
    public void CustomTableView_RendersCellTextRunsAndPreservesRunsForSelection()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            object row = new();
            var layout = new CustomTableColumnSettings.ColumnLayout
            {
                Width = 210,
                Visibility = Visibility.Visible
            };
            var column = new CustomTableColumn(
                "Delta",
                "DELTA",
                layout,
                fallbackOrder: 0,
                sortMemberPath: null,
                alignment: TextAlignment.Left,
                textSelector: _ => "MMMM    MMMM",
                foregroundSelector: _ => Brushes.Black,
                textRunSelector: (_, _) =>
                [
                    new CustomTableTextRunStyle(0, 4, Brushes.Red),
                    new CustomTableTextRunStyle(8, 4, Brushes.Blue)
                ]);

            CustomTableView table = RenderTable(row, column, selectedIndex: -1, currentCell: false, out _, out _);
            try
            {
                table.ItemsSource = new List<object> { row, row };
                table.Columns = [column, CreateColumn("Other", 80)];
                table.Width = 320d;
                table.Height = 150d;
                table.Measure(new Size(320d, 150d));
                table.Arrange(new Rect(0d, 0d, 320d, 150d));

                void AssertCellTextRuns(int rowIndex, string state)
                {
                    RenderTargetBitmap bitmap = RenderExistingTable(table);
                    var bounds = new Int32Rect(0, (int)(table.HeaderHeight + rowIndex * table.RowHeight),
                        column.Width, (int)table.RowHeight - 1);
                    CountDominantPixels(new CroppedBitmap(bitmap, bounds), out int redPixels, out int bluePixels);
                    Assert.IsTrue(redPixels > 0, $"{state}の対象セルに赤の文字区間を残します。");
                    Assert.IsTrue(bluePixels > 0, $"{state}の対象セルに青の文字区間を残します。");
                }

                AssertCellTextRuns(0, "通常行0");
                AssertCellTextRuns(1, "通常行1");
                table.SelectedIndex = 0;
                AssertCellTextRuns(0, "選択行0");
                CustomTableHitTestResult hit = table.HitTestTable(new Point(10d, table.HeaderHeight + table.RowHeight * 1.5d));
                typeof(CustomTableView).GetField("currentCellHit", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, hit);
                AssertCellTextRuns(1, "現在セル行1");
                AssertPixelColor(table, 200, (int)(table.HeaderHeight + 10d), CustomTablePalette.Current.SelectedRowBackground);
                AssertPixelColor(table, 200, (int)(table.HeaderHeight + table.RowHeight + 10d), CustomTablePalette.Current.CurrentCellBackground);
                table.SelectedIndex = 1;
                typeof(CustomTableView).GetField("currentCellHit", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, table.HitTestTable(new Point(10d, table.HeaderHeight + 10d)));
                AssertCellTextRuns(0, "現在セル行0");
                AssertCellTextRuns(1, "選択行1");
                AssertPixelColor(table, 200, (int)(table.HeaderHeight + 10d), CustomTablePalette.Current.CurrentCellBackground);
                AssertPixelColor(table, 200, (int)(table.HeaderHeight + table.RowHeight + 10d), CustomTablePalette.Current.SelectedRowBackground);
                // 同じ行内で現在セル列だけを移し、元セルの背景が通常へ戻ることも確認する。
                typeof(CustomTableView).GetField("currentCellHit", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, table.HitTestTable(new Point(240d, table.HeaderHeight + 10d)));
                RenderExistingTable(table);
                AssertPixelColor(table, 200, (int)(table.HeaderHeight + 10d), CustomTablePalette.Current.RowBackground);
                AssertPixelColor(table, 260, (int)(table.HeaderHeight + 10d), CustomTablePalette.Current.CurrentCellBackground);
            }
            finally { ReleaseTable(table); }
        });
    }

    /// <summary>null行は列の取得処理を呼ばず空セルと交互背景を描き、前後の通常行を詰めず元の位置へ描くことを確認します。</summary>
    [TestMethod]
    public void CustomTableView_NullRowPreservesEmptyCellsAndRowPositions()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            static T RequireRow<T>(object? row, T value)
            {
                ArgumentNullException.ThrowIfNull(row);
                return value;
            }

            object first = new();
            object last = new();
            var column = new CustomTableColumn("Text", "TEXT", new CustomTableColumnSettings.ColumnLayout
            { Width = 180, Visibility = Visibility.Visible }, 0, null, TextAlignment.Left,
                textSelector: row => RequireRow(row, ReferenceEquals(row, first) ? "MMMM" : "WWWW"),
                foregroundSelector: row => RequireRow(row, ReferenceEquals(row, first) ? Brushes.Red : Brushes.Blue),
                backgroundSelector: row => RequireRow(row, Brushes.Yellow),
                textRunSelector: (row, _) => RequireRow<IReadOnlyList<CustomTableTextRunStyle>>(row,
                    [new CustomTableTextRunStyle(0, 4, ReferenceEquals(row, first) ? Brushes.Red : Brushes.Blue)]),
                checkedSelector: row => RequireRow<bool?>(row, true));
            var table = new CustomTableView
            { Width = 240d, Height = 100d, HeaderHeight = 20d, RowHeight = 20d, Columns = [column], ItemsSource = new object?[] { first, null, last } };
            try
            {
                table.Measure(new Size(240d, 100d));
                table.Arrange(new Rect(0d, 0d, 240d, 100d));
                table.ApplyVisibilityChanged(true);
                RenderTargetBitmap bitmap = RenderExistingTable(table);
                CountDominantPixels(new CroppedBitmap(bitmap, new Int32Rect(0, 20, 180, 19)), out int firstRed, out int firstBlue);
                CountDominantPixels(new CroppedBitmap(bitmap, new Int32Rect(0, 40, 180, 19)), out int emptyRed, out int emptyBlue);
                CountDominantPixels(new CroppedBitmap(bitmap, new Int32Rect(0, 60, 180, 19)), out int lastRed, out int lastBlue);
                Assert.IsTrue(firstRed > 0, "先頭行の文字を元の位置に描きます。");
                Assert.AreEqual(0, firstBlue);
                Assert.AreEqual(0, emptyRed);
                Assert.AreEqual(0, emptyBlue);
                Assert.AreEqual(0, lastRed);
                Assert.IsTrue(lastBlue > 0, "null行の後の文字を三行目に描きます。");
                AssertPixelColor(table, 100, 25, Brushes.Yellow);
                AssertPixelColor(table, 100, 45, CustomTablePalette.Current.AlternatingRowBackground);
                AssertPixelColor(table, 100, 65, Brushes.Yellow);

                CustomTableCellValue empty = table.GetCellValue(null, column, out _);
                Assert.AreEqual(string.Empty, empty.Text);
                Assert.AreSame(CustomTableCellValue.Empty.Foreground, empty.Foreground);
                Assert.IsNull(empty.Background);
                Assert.AreEqual(0, empty.TextRuns.Count);
                Assert.IsNull(empty.IsChecked);
                Assert.AreEqual(CustomTableCellKind.Text, empty.CellKind);
            }
            finally { ReleaseTable(table); }
        });
    }

    /// <summary>最下部の部分行が縦スクロールで通常行へ移っても、文字・背景・縦罫線と完全表示時の下辺が揃うことを確認します。</summary>
    [TestMethod]
    public void CustomTableView_PartialRowBecomesFullWithoutLosingCellsOrBorders()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            object target = new();
            object[] rows = [new(), new(), target, new()];
            var column = new CustomTableColumn("Text", "TEXT", new CustomTableColumnSettings.ColumnLayout
            { Width = 180, Visibility = Visibility.Visible }, 0, null, TextAlignment.Left,
                row => ReferenceEquals(row, target) ? "MMMM" : string.Empty,
                foregroundSelector: _ => Brushes.Red,
                backgroundSelector: row => ReferenceEquals(row, target) ? Brushes.Yellow : null);
            var table = new CustomTableView
            { Width = 240d, Height = 75d, HeaderHeight = 20d, RowHeight = 20d, Columns = [column], ItemsSource = rows };
            try
            {
                table.Measure(new Size(240d, 75d));
                table.Arrange(new Rect(0d, 0d, 240d, 75d));
                table.ApplyVisibilityChanged(true);
                CountDominantPixels(RenderExistingTable(table), out int partialRed, out _);
                Assert.IsTrue(partialRed > 0, "部分行にも文字が表示されます。");
                AssertPixelColor(table, 100, 65, Brushes.Yellow);
                ScrollBar scroll = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
                scroll.Value = 1d;
                CountDominantPixels(RenderExistingTable(table), out int fullRed, out _);
                Assert.IsTrue(fullRed > 0, "通常行へ移動しても文字が残ります。");
                AssertPixelColor(table, 100, 45, Brushes.Yellow);
                DrawingGroup? drawing = VisualTreeHelper.GetDrawing(table.Children.OfType<CustomTableSurface>().Single());
                Assert.IsNotNull(drawing);
                Assert.IsTrue(ContainsLine(drawing, new Point(179.5d, 40d), new Point(179.5d, 60d), Matrix.Identity), "通常行の縦罫線を最後まで描きます。");
                Assert.IsTrue(ContainsLine(drawing, new Point(0d, 59.5d), new Point(240d, 59.5d), Matrix.Identity), "完全表示になった行の下辺を描きます。");
            }
            finally { ReleaseTable(table); }
        });
    }

    /// <summary>候補の印が長い導入先の省略後も、既定行高のセル内に描画されることを確認します。</summary>
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    public void InstallDestinationCell_RendersCandidateMarkerInsideNarrowCell(int candidateCount, bool hasDestination)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var settings = new CustomTableColumnSettings();
            settings.InstallDst.Visibility = Visibility.Visible;
            settings.InstallDst.Width = 250;
            CustomTableColumn column = CustomTableColumnFactory.CreateMainColumns(settings, value => GridRowResolver.TryGetInstallDestinationEditTarget(value, ChartOperationSourceScope.Library, MainViewOperationSection.FullScanCheck, out _))
                .Single(candidate => candidate.Id == "InstallDst");
            string longPath = @"C:\BMS\" + new string('W', 120);
            ChartFile chart = ChartFileProjection.WithPackageState(
                (ChartTestValues.Empty() with { Path = @"C:\owned\marker.bms", Md5 = new string('a', 32) }),
                hasDestination ? longPath : string.Empty, string.Empty, string.Empty,
                new[] { longPath, longPath + "-other" }.Take(candidateCount).ToArray(), []);
            var row = LibraryChartRow.FromChartFile(chart);

            CustomTableView table = RenderTable(row, column, selectedIndex: -1, currentCell: false,
                out _, out _, width: 250, rowHeight: BeMusicSeeker.Properties.Settings.DefaultCustomTableRowHeight);
            try
            {
                CustomTableSurface surface = table.Children.OfType<CustomTableSurface>().Single();
                DrawingGroup? drawing = VisualTreeHelper.GetDrawing(surface);
                Assert.IsNotNull(drawing);
                CustomTableHitTestResult hit = table.HitTestTable(new Point(10d, table.HeaderHeight + table.RowHeight / 2d));
                Assert.AreEqual(CustomTableHitKind.Cell, hit.Kind);
                var cellBounds = Rect.Intersect(hit.CellRect, new Rect(surface.RenderSize));

                // 実際に描画された字形を調べ、末尾の省略・セルのクリップで消えた印を成功にしません。
                bool markerVisible = HasVisibleCandidateMarker(drawing, new RectangleGeometry(cellBounds), Matrix.Identity);
                Assert.AreEqual(candidateCount > 0, markerVisible);
            }
            finally { ReleaseTable(table); }
        });
    }

    /// <summary>幅指定なしのtooltipは元文字列、指定ありは指定幅で折り返す実TextBlockへ変換することを確認し、長い詳細の幅指定や折り返しを落とす誤りを検出します。</summary>
    [TestMethod]
    public void CustomTableView_CreatesWrappedTooltipContentWhenWidthIsSpecified()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            object plainContent = CustomTableView.CreateCellToolTipContent("short tooltip", null);
            object wrappedContent = CustomTableView.CreateCellToolTipContent("long tooltip", 420d);

            Assert.AreEqual("short tooltip", plainContent);
            Assert.IsInstanceOfType(wrappedContent, typeof(TextBlock));
            var textBlock = (TextBlock)wrappedContent;
            Assert.AreEqual("long tooltip", textBlock.Text);
            Assert.AreEqual(420d, textBlock.Width);
            Assert.AreEqual(TextWrapping.Wrap, textBlock.TextWrapping);
        });
    }

    private sealed class ResettableRows : ObservableCollection<object>
    {
        internal void Reset() => OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private class DisplayRow
    {
        private string text = "before";
        public int TextCalls { get; private set; }
        public string Text { get { TextCalls++; return text; } set => text = value; }
        public bool HasHighlightedWarning { get; set; }
    }

    private sealed class NotifyingDisplayRow : DisplayRow, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void RaiseChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
    }

    private static CustomTableView CreateUnarrangedDisplayTable(System.Collections.IList rows)
    {
        var column = new CustomTableColumn("Text", "TEXT", new CustomTableColumnSettings.ColumnLayout
        { Width = 180, Visibility = Visibility.Visible }, 0, null, TextAlignment.Left, row => ((DisplayRow)row).Text);
        return new CustomTableView { Width = 240d, Height = 120d, HeaderHeight = 20d, RowHeight = 20d, Columns = [column], ItemsSource = rows };
    }

    private static CustomTableView CreateDisplayTable(System.Collections.IList rows)
    {
        CustomTableView table = CreateUnarrangedDisplayTable(rows);
        table.Measure(new Size(240d, 120d));
        table.Arrange(new Rect(0d, 0d, 240d, 120d));
        table.UpdateLayout();
        table.ApplyVisibilityChanged(true);
        return table;
    }

    private static void ReleaseTable(CustomTableView table)
    {
        table.ApplyVisibilityChanged(false);
        table.ItemsSource = null;
    }

    private static RenderTargetBitmap RenderExistingTable(CustomTableView table)
    {
        table.Children.OfType<CustomTableSurface>().Single().InvalidateVisual();
        table.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)table.ActualWidth, (int)table.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(table);
        return bitmap;
    }

    private static void AssertDisplayValue(CustomTableView table, DisplayRow row, string text, bool warning)
    {
        RenderExistingTable(table);
        Assert.AreEqual(text, table.GetCellValue(row, table.Columns[0], out _).Text);
        Assert.AreEqual(warning, table.HasHighlightedWarning(row));
        AssertPixelColor(table, 200, 25, warning ? CustomTablePalette.Current.WarningRowBackground : CustomTablePalette.Current.RowBackground);
    }

    private static void AssertPixelColor(CustomTableView table, int x, int y, Brush expected)
    {
        RenderTargetBitmap bitmap = RenderExistingTable(table);
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        var brush = (SolidColorBrush)expected;
        Assert.AreEqual(brush.Color, Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]));
    }

    private static CustomTableView RenderTable(object row, CustomTableColumn column, int selectedIndex, bool currentCell,
        out int redPixels, out int bluePixels, int width = 240, double rowHeight = 60d)
    {
        const int height = 70;
        var table = new CustomTableView
        {
            Width = width,
            Height = height,
            HeaderHeight = 0d,
            RowHeight = rowHeight,
            Columns = [column],
            ItemsSource = new List<object> { row },
            SelectedIndex = selectedIndex
        };
        table.Measure(new Size(width, height));
        table.Arrange(new Rect(0, 0, width, height));
        table.UpdateLayout();
        if (currentCell)
        {
            CustomTableHitTestResult hit = table.HitTestTable(new Point(10d, 30d));
            typeof(CustomTableView)
                .GetField("currentCellHit", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(table, hit);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(table);
        CountDominantPixels(bitmap, out redPixels, out bluePixels);
        return table;
    }

    private static bool ContainsLine(Drawing drawing, Point from, Point to, Matrix transform)
    {
        if (drawing is DrawingGroup group)
        {
            Matrix combined = group.Transform?.Value ?? Matrix.Identity;
            combined.Append(transform);
            return group.Children.Any(child => ContainsLine(child, from, to, combined));
        }
        return drawing is GeometryDrawing geometry && geometry.Geometry is LineGeometry line
            && transform.Transform(line.StartPoint) == from && transform.Transform(line.EndPoint) == to;
    }

    private static bool HasVisibleCandidateMarker(Drawing drawing, Geometry clip, Matrix transform)
    {
        if (drawing is DrawingGroup group)
        {
            Matrix childTransform = group.Transform?.Value ?? Matrix.Identity;
            childTransform.Append(transform);
            Geometry childClip = group.ClipGeometry == null ? clip
                : Geometry.Combine(clip, TransformGeometry(group.ClipGeometry, childTransform), GeometryCombineMode.Intersect, null);
            return group.Opacity > 0d && group.Children.Any(child => HasVisibleCandidateMarker(child, childClip, childTransform));
        }
        if (drawing is not GlyphRunDrawing glyphDrawing || glyphDrawing.ForegroundBrush.Opacity <= 0d)
        {
            return false;
        }
        GlyphRun run = glyphDrawing.GlyphRun;
        if (!run.GlyphTypeface.CharacterToGlyphMap.TryGetValue('▼', out ushort markerGlyph))
        {
            return false;
        }
        double advance = 0d;
        for (int index = 0; index < run.GlyphIndices.Count; index++)
        {
            if (run.GlyphIndices[index] == markerGlyph)
            {
                Point offset = run.GlyphOffsets != null && index < run.GlyphOffsets.Count ? run.GlyphOffsets[index] : default;
                var glyphTransform = new Matrix(1d, 0d, 0d, 1d, run.BaselineOrigin.X + advance + offset.X, run.BaselineOrigin.Y - offset.Y);
                glyphTransform.Append(transform);
                Geometry ink = TransformGeometry(run.GlyphTypeface.GetGlyphOutline(markerGlyph, run.FontRenderingEmSize, run.FontRenderingEmSize), glyphTransform);
                double area = ink.GetArea();
                if (area > 0d && Geometry.Combine(ink, clip, GeometryCombineMode.Exclude, null).GetArea() < 0.01d)
                {
                    return true;
                }
            }
            advance += run.AdvanceWidths[index];
        }
        return false;
    }

    private static Geometry TransformGeometry(Geometry geometry, Matrix transform)
    {
        Geometry result = geometry.CloneCurrentValue();
        Matrix combined = result.Transform?.Value ?? Matrix.Identity;
        combined.Append(transform);
        result.Transform = new MatrixTransform(combined);
        return result;
    }

    private static void CountDominantPixels(BitmapSource bitmap, out int redPixels, out int bluePixels)
    {
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        byte[] pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        redPixels = 0;
        bluePixels = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                byte blue = pixels[offset];
                byte green = pixels[offset + 1];
                byte red = pixels[offset + 2];
                if (red > 120 && green < 100 && blue < 100)
                {
                    redPixels++;
                }
                if (blue > 120 && green < 100 && red < 100)
                {
                    bluePixels++;
                }
            }
        }
    }

}
