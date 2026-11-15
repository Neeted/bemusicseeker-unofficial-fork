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
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Localization;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
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
                    // 言語の切替えは、対象セルの実際の画素で観測する。
                    Int32Rect cellArea = CellPixelArea(table, point);
                    SurfacePixels japanesePixels = CustomTableRenderProbe.Read(table);
                    string japaneseText = "▼ " + Resources.InstallDestination_SelectCandidate;
                    Assert.AreEqual(japaneseText, table.GetCellValue(row, column, out _).Text);
                    Assert.AreEqual(japaneseText, table.ResolveCellToolTip(table.HitTestTable(point)));
                    int realized = rows.RealizedRowCount;
                    Assert.IsTrue(realized > 0 && realized < rows.Count);

                    ResourceService.Current.ChangeCulture("en-US");
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    // 先に通知による実描画を確認し、getterや強制描画で接続漏れを補いません。
                    SurfacePixels englishPixels = CustomTableRenderProbe.Read(table);
                    Assert.IsFalse(japanesePixels.RegionEquals(cellArea, englishPixels, cellArea.X, cellArea.Y), "言語の通知だけで、対象セルが現在語の画素へ変わります。");
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
                    Assert.IsTrue(CustomTableRenderProbe.Read(table).RegionEquals(cellArea, japanesePixels, cellArea.X, cellArea.Y), "再Loaded後も最新語で実描画します。");
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
                    Int32Rect cellArea = CellPixelArea(table, point);
                    SurfacePixels japanesePixels = CustomTableRenderProbe.Read(table);
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
                    SurfacePixels englishPixels = CustomTableRenderProbe.Read(table);
                    Assert.IsFalse(japanesePixels.RegionEquals(cellArea, englishPixels, cellArea.X, cellArea.Y), "言語の通知だけで、対象セルが現在語の画素へ変わります。");
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

    /// <summary>描画失効を呼ばず、現在の描画内容のまま見出し帯の物理画素を読みます。</summary>
    private static byte[] RenderHeaderBandAsIs(CustomTableView table)
    {
        // 描画失効は呼ばず、通知が残した未描画分を配置の確定（UpdateLayout）で描いてから読む。
        table.UpdateLayout();
        SurfacePixels surface = CustomTableRenderProbe.Read(table);
        // 表は左上へ寄せて配置するため、表示面の上端が見出し帯の上端です。
        int height = table.RowGeometry.HeaderHeightPx;
        return surface.Bgra.AsSpan(0, surface.Width * 4 * height).ToArray();
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
                    // 幅40ではアイコンが中央に、41以上では左寄せの文字が描かれる。
                    AssertUrlCellGlyph(RenderExistingTable(table), table, width);
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

    /// <summary>
    /// 非表示中の無通知変更と同じ行参照を含むソース交換から復帰しても、最新の文字・警告を表示することを確認します。
    /// 非表示と読み込み解除（<c>ItemsSource=null</c>）のあとは表示面の画像と描画先を保持せず、再表示後の最初の描画では可視の全文字列を新しく整形します。
    /// </summary>
    /// <remarks>非表示の間も大きな描画先や画像（GPU経路の面）と文字のレイアウトを持ち続ける誤り、再表示で古い描画や古いレイアウトを使う誤りを検出します。</remarks>
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
                Assert.IsNotNull(view.SurfaceImage);
                view.ApplyVisibilityChanged(false);
                Assert.IsNull(view.SurfaceImage, "非表示の間は表示面の画像と描画先を保持しません。");
                row.Text = "changed while hidden";
                row.HasHighlightedWarning = true;
                if (swapSource)
                {
                    view.ItemsSource = new List<object> { row };
                }
                view.ApplyVisibilityChanged(true);
                view.UpdateLayout();
                // 見出しと行の二つの文字列は、再利用されず新しく整形される。
                Assert.AreEqual(0, view.LastRenderStats.TextCacheHits);
                Assert.AreEqual(2, view.LastRenderStats.TextCacheMisses);
                AssertDisplayValue(view, row, "changed while hidden", true);
                // 表示と非表示を繰り返しても例外がなく、そのつど最新の状態から描く。
                for (int cycle = 0; cycle < 5; cycle++)
                {
                    view.ApplyVisibilityChanged(false);
                    Assert.IsNull(view.SurfaceImage);
                    view.ApplyVisibilityChanged(true);
                    view.UpdateLayout();
                    Assert.IsNotNull(view.SurfaceImage);
                    Assert.AreEqual(2, view.LastRenderStats.TextCacheMisses);
                }
                view.ItemsSource = null;
                Assert.IsNull(view.SurfaceImage, "読み込み解除のあとは表示面の画像と描画先を保持しません。");
                TestUiDispatcherHost.AssertNoUnhandledExceptions("show-hide-cycle");
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

    /// <summary>
    /// 実Windowの可視の表で、スクロール入力では行集合へ触れず、保持範囲（可視行と上下各5行）の更新を次の描画で一回だけ、その時点の最新の位置に対して行うことを確認します。
    /// 描画前に位置を三回変えても途中の位置の行は読まず購読せず、上下に重なる移動では入る行だけを読んで購読し出る行だけを解除し、重ならない移動では新しい範囲を全件取得して旧い範囲を全解除します。
    /// </summary>
    /// <remarks>
    /// スクロールのたびに行を集め直す誤り、途中の位置の行の取得と購読、重なる行の再購読、入力がないのに配置や消化で読む誤りを検出します。
    /// 描画も保持範囲が持つ参照を使うため、可視行を含めて、入る行以外の添字アクセスがないことを判定します。
    /// </remarks>
    [TestMethod]
    [DoNotParallelize]
    public void RetainedRows_AreUpdatedOncePerRenderAndOnlyForEnteringRows()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            const int RowTotal = 200;
            var rows = new ObservedRows(RowTotal);
            CustomTableView table = CreateUnarrangedDisplayTable(rows);
            try
            {
                scope.ShowAndWaitForContentRendered(new Window { Content = table, SizeToContent = SizeToContent.WidthAndHeight });
                TestUiDispatcherHost.ProcessQueuedBackground();
                ScrollBar bar = table.Children.OfType<ScrollBar>().Single(item => item.Orientation == Orientation.Vertical);
                int d = table.CalculateDrawableRowCapacity();
                Assert.IsTrue(d >= 4 && RowTotal > 3 * d, "範囲を動かす手順に必要な行数があります。");
                AssertSubscribed(rows, 0, d + 5);

                // 1. 描画をはさまずに位置を三回変えても、行集合へ触れず購読も増減しない。
                rows.ResetAccess();
                int[] subscribedBefore = rows.SubscribedSnapshot();
                int renderCount = table.LastRenderStats.RenderCount;
                bar.Value = 60; bar.Value = 120; bar.Value = 3;
                Assert.AreEqual(3, table.FirstVisibleRowIndex);
                Assert.AreEqual(0, rows.AccessedIndices.Count, "スクロール入力は行集合へ触れない。");
                CollectionAssert.AreEqual(subscribedBefore, rows.SubscribedSnapshot(), "購読の増減もない。");
                Assert.AreEqual(renderCount, table.LastRenderStats.RenderCount);

                // 2. 描画は一回で、入る行 {d+5..d+7} だけを一回ずつ読み、重なる行と途中の位置の行は読まない。
                int[] adds = rows.AddSnapshot();
                table.UpdateLayout();
                Assert.AreEqual(renderCount + 1, table.LastRenderStats.RenderCount);
                AssertAccessOnlyVisibleAndEntering(rows, table, [d + 5, d + 6, d + 7]);
                AssertSubscribed(rows, 0, d + 8);
                AssertAddsOnlyAt(rows, adds, d + 5, d + 6, d + 7);

                // 3. 下へ重なる移動。出る行は先頭の三行、入る行は末尾の五行。
                rows.ResetAccess(); adds = rows.AddSnapshot();
                bar.Value = 8;
                Assert.AreEqual(0, rows.AccessedIndices.Count);
                table.UpdateLayout();
                AssertAccessOnlyVisibleAndEntering(rows, table, Enumerable.Range(d + 8, 5).ToArray());
                AssertSubscribed(rows, 3, d + 13);
                AssertAddsOnlyAt(rows, adds, Enumerable.Range(d + 8, 5).ToArray());

                // 上へ重なる移動。入る行は先頭の三行、出る行は末尾の三行。
                rows.ResetAccess(); adds = rows.AddSnapshot();
                bar.Value = 5;
                table.UpdateLayout();
                AssertAccessOnlyVisibleAndEntering(rows, table, [0, 1, 2]);
                AssertSubscribed(rows, 0, d + 10);
                AssertAddsOnlyAt(rows, adds, 0, 1, 2);

                // 重ならない移動。新しい範囲の全行を取得して購読し、旧い範囲の全行を解除する。
                rows.ResetAccess(); adds = rows.AddSnapshot();
                bar.Value = 100;
                Assert.AreEqual(0, rows.AccessedIndices.Count);
                table.UpdateLayout();
                int[] entering = Enumerable.Range(95, d + 10).ToArray();
                CollectionAssert.IsSubsetOf(rows.AccessedIndices.ToArray(), entering, "新しい範囲の外は読まない。");
                foreach (int index in entering) { Assert.IsTrue(rows.AccessCount(index) >= 1, "新しい範囲の全行を取得する。位置 " + index); }
                AssertSubscribed(rows, 95, 95 + d + 10);
                AssertAddsOnlyAt(rows, adds, entering);

                // 4. 範囲外へ動くキーのDownは、入力の処理中には主行だけを読む。
                int capacity = table.CalculateFullVisibleRowCapacity();
                table.SelectedIndex = table.FirstVisibleRowIndex + capacity - 1;
                table.UpdateLayout();
                int lastVisible = table.SelectedIndex;
                rows.ResetAccess();
                Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
                Assert.AreEqual(lastVisible + 1, table.SelectedIndex);
                CollectionAssert.IsSubsetOf(rows.AccessedIndices.ToArray(), new[] { lastVisible + 1 }, "入力の処理中に読むのは主行だけ。");
                table.UpdateLayout();
                Assert.AreEqual(1, rows.SubscribedSnapshot()[lastVisible + 1]);

                // 5. 入力がなければ、配置・Dispatcherの消化・再配置のいずれも読取り・購読・描画を起こさない。
                rows.ResetAccess();
                int[] stable = rows.SubscribedSnapshot();
                int[] stableAdds = rows.AddSnapshot();
                renderCount = table.LastRenderStats.RenderCount;
                table.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedBackground();
                table.UpdateLayout();
                Assert.AreEqual(0, rows.AccessedIndices.Count);
                CollectionAssert.AreEqual(stable, rows.SubscribedSnapshot());
                CollectionAssert.AreEqual(stableAdds, rows.AddSnapshot());
                Assert.AreEqual(renderCount, table.LastRenderStats.RenderCount);
                // 同じ状態の再描画は、可視行も含めて行集合を読まない。
                CustomTableRenderProbe.Surface(table).InvalidateVisual();
                table.UpdateLayout();
                Assert.AreEqual(renderCount + 1, table.LastRenderStats.RenderCount);
                Assert.AreEqual(0, rows.AccessedIndices.Count, "同じ状態の再描画で行集合を読む。");

                // 6. 可視の位置の行を別の参照へ置き換えてResetすると、描画後に全添字を取り直して新しい参照を購読し、旧い参照を解除する。
                int first = table.FirstVisibleRowIndex;
                int replacedIndex = first + 1;
                ObservedRow oldRow = rows.RowAt(replacedIndex);
                ObservedRow newRow = rows.Replace(replacedIndex);
                int retainedFirst = Math.Max(0, first - 5), retainedEnd = Math.Min(RowTotal, first + d + 5);
                rows.ResetAccess();
                rows.RaiseReset();
                table.UpdateLayout();
                foreach (int index in Enumerable.Range(retainedFirst, retainedEnd - retainedFirst))
                {
                    Assert.IsTrue(rows.AccessCount(index) >= 1, "Resetでは保持範囲の全添字を取り直す。位置 " + index);
                }
                Assert.AreEqual(1, newRow.Subscribed);
                Assert.AreEqual(0, oldRow.Subscribed);
                AssertSubscribed(rows, retainedFirst, retainedEnd);

                // 7. スクロールを保留したまま非表示にすると、即座に全解除して保留を破棄し、後の消化や配置では再購読しない。再表示後の描画で最新の位置の範囲を全件取得する。
                bar.Value = 40;
                rows.ResetAccess();
                table.Visibility = Visibility.Collapsed;
                Assert.AreEqual(0, rows.SubscribedSnapshot().Sum(), "非表示で全購読を同期に解除する。");
                Assert.IsNull(table.SurfaceImage);
                TestUiDispatcherHost.ProcessQueuedBackground();
                table.UpdateLayout();
                TestUiDispatcherHost.ProcessQueuedBackground();
                Assert.AreEqual(0, rows.AccessedIndices.Count, "非表示の間の消化と配置で、行集合を読む（可視行を含む）。");
                Assert.AreEqual(0, rows.SubscribedSnapshot().Sum(), "非表示の間は再購読しない。");
                table.Visibility = Visibility.Visible;
                table.UpdateLayout();
                Assert.AreEqual(40, table.FirstVisibleRowIndex);
                int shownFirst = 40 - 5, shownEnd = 40 + d + 5;
                foreach (int index in Enumerable.Range(shownFirst, shownEnd - shownFirst))
                {
                    Assert.IsTrue(rows.AccessCount(index) >= 1, "再表示後の描画で最新の位置の範囲を全件取得する。位置 " + index);
                }
                AssertSubscribed(rows, shownFirst, shownEnd);
                TestUiDispatcherHost.ProcessQueuedBackground();
            }
            finally { table.ItemsSource = null; }
        });
    }

    private static void AssertSubscribed(ObservedRows rows, int firstIndex, int endIndex)
    {
        int[] snapshot = rows.SubscribedSnapshot();
        for (int i = 0; i < snapshot.Length; i++)
        {
            Assert.AreEqual(i >= firstIndex && i < endIndex ? 1 : 0, snapshot[i], "位置 " + i + " の購読数（保持範囲は " + firstIndex + " から " + endIndex + " の手前）");
        }
    }

    private static void AssertAddsOnlyAt(ObservedRows rows, int[] addsBefore, params int[] expectedIndices)
    {
        int[] adds = rows.AddSnapshot();
        for (int i = 0; i < adds.Length; i++)
        {
            Assert.AreEqual(addsBefore[i] + (Array.IndexOf(expectedIndices, i) >= 0 ? 1 : 0), adds[i], "購読の追加は入る行だけ。位置 " + i);
        }
    }

    // 描画も保持範囲が持つ参照を使うため、行集合から読まれるのは入る行だけで、可視行も読まれない。
    private static void AssertAccessOnlyVisibleAndEntering(ObservedRows rows, CustomTableView table, int[] entering)
    {
        int[] unexpected = [.. rows.AccessedIndices.Where(index => Array.IndexOf(entering, index) < 0)];
        Assert.AreEqual(0, unexpected.Length, "入る行の外の行（可視行を含む）を読んだ: " + string.Join(",", unexpected));
        foreach (int index in entering)
        {
            Assert.AreEqual(1, rows.AccessCount(index), "入る行は一回だけ読む。位置 " + index);
        }
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

    /// <summary>
    /// 表示済みの表でF2の編集中に通常の文字のフォントをMeiryo UIからArialへ変えると、編集を一回確定し、見出しとセルの字形・省略の判定・次の編集欄がArialになり、
    /// 最初からArialで作った表と同じ画素になることを確認します。行・列・選択・スクロール位置は変わらず、フォントを指定しない表とnullは既定のフォントです。
    /// </summary>
    /// <remarks>古いフォントの描画が残る誤り、編集を破棄する誤り、フォントの変更で選択やスクロールを戻す誤り、変更後と作成時で描き方が違う誤りを検出します。ArialとMeiryo UIのインストールを前提にします。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void TextFontFamily_ChangeRedrawsHeaderCellsTrimAndEditorAndKeepsRowsSelectionAndScroll()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            // 本番と同じく、名前を持たない合成フォントどうしを切り替える。
            FontFamily arial = AppFontService.CreateFontFamily("Arial");
            FontFamily meiryoUi = AppFontService.DefaultFontFamily;
            double fontSize = BeMusicSeeker.Properties.Settings.DefaultCustomTableFontSize;
            // 表と同じ方式と表示倍率で測る。
            double pixelsPerDip = VisualTreeHelper.GetDpi(new Border()).PixelsPerDip;
            double Measure(FontFamily family, string value)
            {
                using var cache = new CustomTableTextLayoutCache();
                CultureInfo culture = CultureInfo.CurrentCulture, uiCulture = CultureInfo.CurrentUICulture;
                cache.Configure(new CustomTableTextEnvironment(family, null!, fontSize, pixelsPerDip, culture, uiCulture));
                return cache.GetOrCreate(value, CustomTableTextKind.Normal, TextAlignment.Left, 0, out _).NaturalWidth;
            }
            // 二つのフォントで幅の差が最も大きい文字列を選び、その中間の列幅で省略の有無が入れ替わることを判定に使う。
            string text = new[] { new string('W', 40), new string('i', 80), new string('0', 60), "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz" }
                .MaxBy(candidate => Math.Abs(Measure(arial, candidate) - Measure(meiryoUi, candidate)))!;
            double arialWidth = Measure(arial, text), meiryoUiWidth = Measure(meiryoUi, text);
            Assert.IsTrue(Math.Abs(arialWidth - meiryoUiWidth) >= 12d * pixelsPerDip, "二つのフォントの幅の差で省略を区別できる環境が前提です。");
            // 文字を描ける幅（物理画素）が二つの幅の中間になる列幅（DIP）。
            int columnWidth = (int)Math.Round(((arialWidth + meiryoUiWidth) / 2d + 2 * CustomTablePixelGeometry.TextPadding(pixelsPerDip) + CustomTablePixelGeometry.GridLineWidth(pixelsPerDip)) / pixelsPerDip);
            var layout = new CustomTableColumnSettings.ColumnLayout { Width = columnWidth, Visibility = Visibility.Visible };
            CustomTableView CreateTable(FontFamily? family)
            {
                var column = new CustomTableColumn("Folder", "Folder", layout, fallbackOrder: 0, sortMemberPath: null, alignment: TextAlignment.Left,
                    textSelector: _ => text, editPropertyName: "Folder");
                object[] tableRows = [.. Enumerable.Range(0, 40).Select(_ => new object())];
                var created = new CustomTableView { Width = columnWidth + 60d, Height = 120, HeaderHeight = 20, RowHeight = 20, Columns = [column], ItemsSource = tableRows };
                if (family != null) { created.TextFontFamily = family; }
                return created;
            }
            CustomTableView table = CreateTable(null);
            object[] rows = [.. table.ItemsSource.Cast<object>()];
            Assert.AreSame(meiryoUi, table.TextFontFamily);
            table.TextFontFamily = null!;
            Assert.AreSame(meiryoUi, table.TextFontFamily);
            var ended = new List<CustomTableCellEditEndedEventArgs>();
            table.CellEditEnded += (_, args) => ended.Add(args);
            var window = new Window { Width = columnWidth + 120d, Height = 200, Content = table };
            CustomTableView? fresh = null;
            Window? freshWindow = null;
            try
            {
                scope.ShowAndWaitForContentRendered(window);
                ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
                vertical.Value = 2;
                table.SelectedIndex = 3;
                var point = new Point(15, table.RowGeometry.HeaderHeight + (3 - table.FirstVisibleRowIndex + 0.5) * table.RowGeometry.RowHeight);
                RenderExistingTable(table);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                SurfacePixels meiryoPixels = CustomTableRenderProbe.Read(table);
                bool trimmedBefore = table.ResolveCellToolTip(table.HitTestTable(point)) != null;
                Assert.AreEqual(meiryoUiWidth > arialWidth, trimmedBefore);
                object columns = table.Columns;
                double verticalOffset = vertical.Value;

                Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                TextBox firstEditor = FindVisualChildren<TextBox>(table).Single();
                Assert.AreEqual(meiryoUi, firstEditor.FontFamily);
                // ウィンドウは描画方式を指定しないため、編集欄がセルの文字と同じ方式を自分で指定する。
                Assert.AreEqual(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(firstEditor));
                table.TextFontFamily = arial;
                Assert.AreEqual(1, ended.Count);
                Assert.IsTrue(ended[0].Commit);
                Assert.AreEqual(0, FindVisualChildren<TextBox>(table).Count());
                RenderExistingTable(table);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                SurfacePixels arialPixels = CustomTableRenderProbe.Read(table);

                Assert.IsFalse(meiryoPixels.SameAs(arialPixels), "フォントの変更で古いフォントの描画が残ります。");
                Assert.AreEqual(!trimmedBefore, table.ResolveCellToolTip(table.HitTestTable(point)) != null);
                Assert.AreSame(rows[3], table.ItemsSource[3]);
                Assert.AreSame(columns, table.Columns);
                Assert.AreEqual(columnWidth, layout.Width);
                Assert.AreEqual(3, table.SelectedIndex);
                Assert.AreEqual(verticalOffset, vertical.Value);
                Assert.AreEqual(fontSize, table.TextFontSize);

                // 最初からArialで作った同じ状態の表と、同じ画素になる。
                fresh = CreateTable(arial);
                freshWindow = new Window { Width = columnWidth + 120d, Height = 200, Content = fresh };
                scope.ShowAndWaitForContentRendered(freshWindow);
                fresh.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical).Value = 2;
                fresh.SelectedIndex = 3;
                Assert.IsTrue(fresh.HandleKeyDown(Key.F2, ModifierKeys.None));
                Assert.IsTrue(fresh.HandleKeyDown(Key.Escape, ModifierKeys.None));
                RenderExistingTable(fresh);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(arialPixels.SameAs(CustomTableRenderProbe.Read(fresh)), "変更後の描画は、最初からそのフォントで作った表と同じ画素になります。");
                Assert.AreEqual(!trimmedBefore, fresh.ResolveCellToolTip(fresh.HitTestTable(point)) != null);

                Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                Assert.AreEqual(arial, FindVisualChildren<TextBox>(table).Single().FontFamily);
                Assert.IsTrue(table.HandleKeyDown(Key.Escape, ModifierKeys.None));
            }
            finally
            {
                if (freshWindow != null) { freshWindow.Content = null; fresh!.ItemsSource = null; freshWindow.Close(); }
                window.Content = null; table.ItemsSource = null;
                window.Close();
            }
        });
    }
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) { yield return match; }
            foreach (T descendant in FindVisualChildren<T>(child)) { yield return descendant; }
        }
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

    /// <remarks>区間色の変換は下位ケース、選択時にも見えること・行とセルの背景・文字色の優先順位は実描画の画素で分担します。</remarks>
    /// <summary>
    /// 行背景（選択→警告→交互色）、セル固有の背景（非選択の行だけ）とその上の現在セルの背景、文字色（現在セル→選択→通常色）、
    /// 色付きの文字区間が選択・現在セルでも区間の色を保つことを、実際の物理画素で確認します。
    /// </summary>
    /// <remarks>優先順位の取り違え、選択行でセル固有の背景を描く誤り、区間の色が選択で失われる誤りを検出します。背景は内側の画素の厳密な色、文字は支配的な色で判定します。</remarks>
    [TestMethod]
    public void CustomTableView_RendersCellTextRunsAndPreservesRunsForSelection()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            object[] rows = [new StyledRow(), new StyledRow(), new StyledRow(), new StyledRow { HasHighlightedWarning = true }, new StyledRow()];
            CustomTableColumn runs = new("Delta", "DELTA", new CustomTableColumnSettings.ColumnLayout { Width = 210, Visibility = Visibility.Visible }, 0, null, TextAlignment.Left,
                textSelector: _ => "MMMM    MMMM",
                foregroundSelector: _ => Brushes.Black,
                textRunSelector: (_, _) => [new CustomTableTextRunStyle(0, 4, Brushes.Red), new CustomTableTextRunStyle(8, 4, Brushes.Blue)]);
            CustomTableColumn plain = new("Other", "OTHER", new CustomTableColumnSettings.ColumnLayout { Width = 80, Visibility = Visibility.Visible }, 1, null, TextAlignment.Left, _ => "Other");
            CustomTableColumn yellow = new("Yellow", "YELLOW", new CustomTableColumnSettings.ColumnLayout { Width = 60, Visibility = Visibility.Visible }, 2, null, TextAlignment.Left, _ => "C",
                backgroundSelector: _ => Brushes.Yellow);
            var table = new CustomTableView { Width = 350d, Height = 130d, HeaderHeight = 20d, RowHeight = 20d, Columns = [runs, plain, yellow], ItemsSource = rows };
            CustomTableRenderProbe.SetDpi(table, 1d);
            CustomTableRenderProbe.Arrange(table, 350d, 130d);
            table.ApplyVisibilityChanged(true);
            try
            {
                Color Solid(Brush brush) => ((SolidColorBrush)brush).Color;
                static int RowTop(int row) => 20 + row * 20;
                // 内側の画素の位置（文字のない場所）。
                void AssertBackground(SurfacePixels pixels, int row, int x, Color expected, string state)
                    => Assert.AreEqual(Color.FromRgb(expected.R, expected.G, expected.B), pixels.GetColor(x, RowTop(row) + 10), state);
                void AssertRuns(SurfacePixels pixels, int row, string state)
                {
                    (int red, int blue) = pixels.CountDominant(new Int32Rect(0, RowTop(row), 200, 19));
                    Assert.IsTrue(red > 0, state + "の対象セルに赤の文字区間を残します。");
                    Assert.IsTrue(blue > 0, state + "の対象セルに青の文字区間を残します。");
                }
                void AssertTextColor(SurfacePixels pixels, int row, Color background, Brush expected, string state)
                {
                    Color actual = pixels.DominantInkColor(new Int32Rect(212, RowTop(row) + 2, 40, 15), background);
                    Color want = Solid(expected);
                    Assert.IsTrue(Math.Abs(actual.R - want.R) <= 8 && Math.Abs(actual.G - want.G) <= 8 && Math.Abs(actual.B - want.B) <= 8, state + " 文字の色 実際=" + actual + " 期待=" + want);
                }

                SurfacePixels initial = RenderExistingTable(table);
                CustomTablePalette palette = CustomTablePalette.Current;
                AssertBackground(initial, 0, 200, Solid(palette.RowBackground), "通常の偶数行");
                AssertBackground(initial, 1, 200, Solid(palette.AlternatingRowBackground), "通常の奇数行");
                AssertBackground(initial, 3, 200, Solid(palette.WarningRowBackground), "警告の行は交互色より優先");
                AssertBackground(initial, 0, 330, Colors.Yellow, "非選択の行のセル固有の背景");
                AssertBackground(initial, 2, 330, Colors.Yellow, "非選択の行のセル固有の背景");
                AssertRuns(initial, 0, "通常行0"); AssertRuns(initial, 1, "通常行1");
                AssertTextColor(initial, 0, Solid(palette.RowBackground), palette.DefaultForeground, "通常");

                table.SelectedIndex = 2;
                SurfacePixels selected = RenderExistingTable(table);
                palette = CustomTablePalette.Current;
                AssertBackground(selected, 2, 270, Solid(palette.SelectedRowBackground), "選択行の背景は最優先");
                AssertBackground(selected, 2, 330, Solid(palette.SelectedRowBackground), "選択行ではセル固有の背景を描かない");
                AssertBackground(selected, 3, 200, Solid(palette.WarningRowBackground), "選択しない警告の行");
                AssertBackground(selected, 4, 330, Colors.Yellow, "選択しない行のセル固有の背景");
                AssertRuns(selected, 2, "選択行2");
                AssertTextColor(selected, 2, Solid(palette.SelectedRowBackground), palette.SelectedForeground, "選択");

                // 現在セルを列ごとに移し、現在セルの背景と文字色が選択より優先されることを確かめる。
                var point = new Point(10d, RowTop(2) + 10d);
                Assert.IsTrue(table.HandleCellMouseDown(table.HitTestTable(point), MouseButton.Left, ModifierKeys.None, 1, point));
                SurfacePixels current = RenderExistingTable(table);
                palette = CustomTablePalette.Current;
                AssertBackground(current, 2, 200, Solid(palette.CurrentCellBackground), "現在セルの背景");
                AssertBackground(current, 2, 270, Solid(palette.SelectedRowBackground), "同じ行の他のセルは選択行の背景");
                AssertRuns(current, 2, "現在セルの行2");
                AssertTextColor(current, 2, Solid(palette.SelectedRowBackground), palette.SelectedForeground, "現在セルでない選択行");

                point = new Point(220d, RowTop(2) + 10d);
                Assert.IsTrue(table.HandleCellMouseDown(table.HitTestTable(point), MouseButton.Left, ModifierKeys.None, 1, point));
                SurfacePixels moved = RenderExistingTable(table);
                palette = CustomTablePalette.Current;
                AssertBackground(moved, 2, 200, Solid(palette.SelectedRowBackground), "元の現在セルは選択行の背景へ戻る");
                AssertBackground(moved, 2, 270, Solid(palette.CurrentCellBackground), "移した現在セルの背景");
                AssertRuns(moved, 2, "選択行2（現在セルが別列）");
                AssertTextColor(moved, 2, Solid(palette.CurrentCellBackground), palette.CurrentCellForeground, "現在セル");

                point = new Point(320d, RowTop(2) + 10d);
                Assert.IsTrue(table.HandleCellMouseDown(table.HitTestTable(point), MouseButton.Left, ModifierKeys.None, 1, point));
                SurfacePixels onYellow = RenderExistingTable(table);
                palette = CustomTablePalette.Current;
                AssertBackground(onYellow, 2, 330, Solid(palette.CurrentCellBackground), "セル固有の背景の列でも現在セルの背景");
                AssertBackground(onYellow, 4, 330, Colors.Yellow, "他の行のセル固有の背景");
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
            CustomTableRenderProbe.SetDpi(table, 1d);
            try
            {
                table.Measure(new Size(240d, 100d));
                table.Arrange(new Rect(0d, 0d, 240d, 100d));
                table.ApplyVisibilityChanged(true);
                SurfacePixels pixels = RenderExistingTable(table);
                (int firstRed, int firstBlue) = pixels.CountDominant(new Int32Rect(0, 20, 180, 19));
                (int emptyRed, int emptyBlue) = pixels.CountDominant(new Int32Rect(0, 40, 180, 19));
                (int lastRed, int lastBlue) = pixels.CountDominant(new Int32Rect(0, 60, 180, 19));
                Assert.IsTrue(firstRed > 0, "先頭行の文字を元の位置に描きます。");
                Assert.AreEqual(0, firstBlue);
                Assert.AreEqual(0, emptyRed);
                Assert.AreEqual(0, emptyBlue);
                Assert.AreEqual(0, lastRed);
                Assert.IsTrue(lastBlue > 0, "null行の後の文字を三行目に描きます。");
                AssertPixelColor(table, 100, 25, Brushes.Yellow);
                AssertPixelColor(table, 100, 45, CustomTablePalette.Current.AlternatingRowBackground);
                AssertPixelColor(table, 100, 65, Brushes.Yellow);
                // 空のセルは文字も背景も描かず、通常の罫線だけが残る。
                Color emptyBackground = ((SolidColorBrush)CustomTablePalette.Current.AlternatingRowBackground).Color;
                Assert.AreEqual(0, pixels.CountNotEqual(new Int32Rect(0, 40, 179, 19), emptyBackground), "null行は空のセルです。");

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

    /// <summary>最下部の部分行が縦スクロールで通常行へ移っても、文字・背景・縦罫線と完全表示時の下辺が揃うことを確認します。部分行にも縦罫線を描き、完全に見えない下辺は描かないことも確認します。</summary>
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
            CustomTableRenderProbe.SetDpi(table, 1d);
            try
            {
                table.Measure(new Size(240d, 75d));
                table.Arrange(new Rect(0d, 0d, 240d, 75d));
                table.ApplyVisibilityChanged(true);
                SurfacePixels partial = RenderExistingTable(table);
                Assert.IsTrue(partial.CountDominant(new Int32Rect(0, 60, 180, 15)).Red > 0, "部分行にも文字が表示されます。");
                AssertPixelColor(table, 100, 65, Brushes.Yellow);
                // 部分行にも縦罫線を描き、完全に見えない下辺は描かない。
                AssertPixelColor(table, 179, 70, CustomTablePalette.Current.CellBorder.Brush);
                AssertPixelColor(table, 100, 74, Brushes.Yellow);
                ScrollBar scroll = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
                scroll.Value = 1d;
                SurfacePixels full = RenderExistingTable(table);
                Assert.IsTrue(full.CountDominant(new Int32Rect(0, 40, 180, 19)).Red > 0, "通常行へ移動しても文字が残ります。");
                AssertPixelColor(table, 100, 45, Brushes.Yellow);
                AssertPixelColor(table, 179, 50, CustomTablePalette.Current.CellBorder.Brush);
                AssertPixelColor(table, 100, 59, CustomTablePalette.Current.CellBorder.Brush);
            }
            finally { ReleaseTable(table); }
        });
    }

    /// <summary>候補の印「▼」が、長い導入先の省略後も既定行高のセルの中に描かれ、候補のないセルには描かれず、セルの外へ描画がはみ出さないことを、実際の画素で確認します。</summary>
    /// <remarks>印だけを描いた基準の画像と、印の字形の画素が一致するかで判定するため、省略やクリップで消えた印を成功にしません。</remarks>
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
            var markerOnly = new CustomTableColumn("Marker", "MARKER", new CustomTableColumnSettings.ColumnLayout { Width = 250, Visibility = Visibility.Visible }, 0, null, TextAlignment.Left, _ => "▼");

            CustomTableView table = RenderTable(row, column, out SurfacePixels pixels, width: 250, rowHeight: BeMusicSeeker.Properties.Settings.DefaultCustomTableRowHeight);
            CustomTableView reference = RenderTable(new object(), markerOnly, out SurfacePixels markerPixels, width: 250, rowHeight: BeMusicSeeker.Properties.Settings.DefaultCustomTableRowHeight);
            try
            {
                Color background = ((SolidColorBrush)CustomTablePalette.Current.RowBackground).Color;
                int rowHeight = (int)table.RowGeometry.RowHeight;
                int headerHeight = (int)table.RowGeometry.HeaderHeight;
                var interior = new Int32Rect(0, headerHeight, 249, rowHeight - 1);
                Int32Rect? markerInk = markerPixels.FindInkBounds(interior, background);
                Assert.IsNotNull(markerInk, "基準の画像に印が描かれます。");
                bool markerDrawn = pixels.RegionEquals(markerInk.Value, markerPixels, markerInk.Value.X, markerInk.Value.Y);
                Assert.AreEqual(candidateCount > 0, markerDrawn);
                // セルの外（右の罫線と下の罫線）には何も描かれない。
                Color border = ((SolidColorBrush)CustomTablePalette.Current.CellBorder.Brush).Color;
                for (int y = headerHeight; y < headerHeight + rowHeight; y++) { Assert.AreEqual(border, pixels.GetColor(249, y), "右の罫線 y=" + y); }
                for (int x = 0; x < 250; x++) { Assert.AreEqual(border, pixels.GetColor(x, headerHeight + rowHeight - 1), "下の罫線 x=" + x); }
            }
            finally { ReleaseTable(table); ReleaseTable(reference); }
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

    /// <summary>行の側から見た実際の購読（イベントの追加と解除）を数える行です。</summary>
    private sealed class ObservedRow : DisplayRow, INotifyPropertyChanged
    {
        private PropertyChangedEventHandler? handlers;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { handlers += value; AddCount++; }
            remove { handlers -= value; RemoveCount++; }
        }

        internal int AddCount { get; private set; }

        internal int RemoveCount { get; private set; }

        /// <summary>現在の購読の数です。</summary>
        internal int Subscribed => AddCount - RemoveCount;
    }

    /// <summary>添字ごとの読取り回数を数え、列挙を失敗にする行集合です。購読の数は行の側から読みます。</summary>
    private sealed class ObservedRows : System.Collections.IList, INotifyCollectionChanged
    {
        private readonly List<ObservedRow> rows;
        private readonly Dictionary<int, int> accessCounts = [];

        internal ObservedRows(int count) => rows = [.. Enumerable.Range(0, count).Select(_ => new ObservedRow())];

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        internal Dictionary<int, int>.KeyCollection AccessedIndices => accessCounts.Keys;

        internal int AccessCount(int index) => accessCounts.GetValueOrDefault(index);

        internal void ResetAccess() => accessCounts.Clear();

        // 記録しない直接参照です。期待値を作るためだけに使います。
        internal ObservedRow RowAt(int index) => rows[index];

        internal ObservedRow Replace(int index) => rows[index] = new ObservedRow();

        internal void RaiseReset() => CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

        internal int[] SubscribedSnapshot() => [.. rows.Select(row => row.Subscribed)];

        internal int[] AddSnapshot() => [.. rows.Select(row => row.AddCount)];

        public object? this[int index]
        {
            get
            {
                accessCounts[index] = accessCounts.GetValueOrDefault(index) + 1;
                return rows[index];
            }
            set => throw new NotSupportedException();
        }

        public int Count => rows.Count;

        public bool IsFixedSize => true;

        public bool IsReadOnly => true;

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        public System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException("全行列挙は禁止です。");

        public int Add(object? value) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Contains(object? value) => throw new NotSupportedException();

        public int IndexOf(object? value) => throw new NotSupportedException();

        public void Insert(int index, object? value) => throw new NotSupportedException();

        public void Remove(object? value) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();

        public void CopyTo(Array array, int index) => throw new NotSupportedException();
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
        CustomTableRenderProbe.SetDpi(table, 1d);
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

    /// <summary>描画を失効させ、配置の確定だけで描かれた表示面の物理画素を読みます。</summary>
    private static SurfacePixels RenderExistingTable(CustomTableView table) => CustomTableRenderProbe.Redraw(table);

    /// <summary>表示倍率を考慮して、DIPの点にあるセルの内側の物理画素の矩形を返します。</summary>
    private static Int32Rect CellPixelArea(CustomTableView table, Point point)
    {
        CustomTableHitTestResult hit = table.HitTestTable(point);
        Assert.AreEqual(CustomTableHitKind.Cell, hit.Kind);
        double ppd = VisualTreeHelper.GetDpi(table).PixelsPerDip;
        return new Int32Rect((int)Math.Round(hit.CellRect.Left * ppd), (int)Math.Round(hit.CellRect.Top * ppd),
            (int)Math.Round(hit.CellRect.Width * ppd) - 1, (int)Math.Round(hit.CellRect.Height * ppd) - 1);
    }

    /// <summary>URL列の先頭行を、幅40ではアイコンが中央に、41以上では左寄せの文字が描かれていることを画素で確かめます。</summary>
    private static void AssertUrlCellGlyph(SurfacePixels pixels, CustomTableView table, int width)
    {
        double ppd = VisualTreeHelper.GetDpi(table).PixelsPerDip;
        int rowTop = table.RowGeometry.HeaderHeightPx, rowHeight = table.RowGeometry.RowHeightPx;
        int interiorWidth = (int)Math.Round(width * ppd) - 1;
        Color background = CustomTablePalette.Current.RowBackground is SolidColorBrush solid ? solid.Color : default;
        Int32Rect? ink = pixels.FindInkBounds(new Int32Rect(0, rowTop, interiorWidth, rowHeight - 1), background);
        Assert.IsNotNull(ink, "URL列の先頭行に何かが描かれます。");
        double centerOffset = Math.Abs(ink.Value.X + ink.Value.Width / 2d - interiorWidth / 2d);
        if (width == 40) { Assert.IsTrue(centerOffset <= 2d * ppd, "幅40のアイコンはセルの中央に描かれます。ずれ=" + centerOffset); }
        else { Assert.IsTrue(ink.Value.X <= 5d * ppd && ink.Value.Width > 20d * ppd, "幅41以上の文字は左寄せで描かれます。左=" + ink.Value.X + " 幅=" + ink.Value.Width); }
    }

    private static void AssertDisplayValue(CustomTableView table, DisplayRow row, string text, bool warning)
    {
        RenderExistingTable(table);
        Assert.AreEqual(text, table.GetCellValue(row, table.Columns[0], out _).Text);
        Assert.AreEqual(warning, table.HasHighlightedWarning(row));
        AssertPixelColor(table, 200, 25, warning ? CustomTablePalette.Current.WarningRowBackground : CustomTablePalette.Current.RowBackground);
    }

    /// <summary>描画を失効させて読み、DIPの点が指定の単色であることを物理画素で確かめます。</summary>
    private static void AssertPixelColor(CustomTableView table, int x, int y, Brush expected)
    {
        SurfacePixels pixels = RenderExistingTable(table);
        double ppd = VisualTreeHelper.GetDpi(table).PixelsPerDip;
        var brush = (SolidColorBrush)expected;
        Assert.AreEqual(Color.FromRgb(brush.Color.R, brush.Color.G, brush.Color.B),
            pixels.GetColor((int)Math.Round(x * ppd), (int)Math.Round(y * ppd)));
    }

    /// <summary>一行だけの窓のない表を、倍率100%で配置して描き、表示面の物理画素を返します。</summary>
    private static CustomTableView RenderTable(object row, CustomTableColumn column, out SurfacePixels pixels, int width = 240, double rowHeight = 60d)
    {
        const int height = 70;
        var table = new CustomTableView
        {
            Width = width,
            Height = height,
            HeaderHeight = 0d,
            RowHeight = rowHeight,
            Columns = [column],
            ItemsSource = new List<object> { row }
        };
        CustomTableRenderProbe.SetDpi(table, 1d);
        table.Measure(new Size(width, height));
        table.Arrange(new Rect(0, 0, width, height));
        table.UpdateLayout();
        pixels = CustomTableRenderProbe.Read(table);
        return table;
    }

    private sealed class ReuseRow(int id) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public int Id { get; } = id;

        public Brush Foreground { get; set; } = Brushes.Black;

        public void RaiseChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Foreground)));
    }

    /// <summary>列ごとに表示値の取得回数を数える、一意の文字列を持つ表を作ります。3列目（添字3）の文字列だけは、幅を狭めると省略が要る長さです。</summary>
    private static (CustomTableView Table, int[] Fetches) CreateReuseTable(int columnCount, int rowCount, double width, double height)
    {
        int[] fetches = new int[columnCount];
        CustomTableColumn[] columns = [.. Enumerable.Range(0, columnCount).Select(index => new CustomTableColumn(
            "C" + index, "H" + index, new CustomTableColumnSettings.ColumnLayout { Width = 90, Visibility = Visibility.Visible }, index, null, TextAlignment.Left,
            row => { fetches[index]++; return "r" + ((ReuseRow)row).Id + "c" + index + (index == 3 ? " wide" : string.Empty); },
            foregroundSelector: index == 0 ? row => ((ReuseRow)row).Foreground : null))];
        object[] rows = [.. Enumerable.Range(0, rowCount).Select(id => new ReuseRow(id))];
        var table = new CustomTableView
        {
            Width = width,
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            HeaderHeight = 20d,
            RowHeight = 19d,
            Columns = columns,
            ItemsSource = rows
        };
        return (table, fetches);
    }

    /// <summary>操作を一回行い、配置の確定で描かれた一回の描画の（ミス数、ヒット数）と、列ごとの表示値の取得回数の増分を返します。</summary>
    private static (int Misses, int Hits, int[] Fetches) ObserveRender(CustomTableView table, int[] fetches, Action operation)
    {
        int[] before = [.. fetches];
        int renders = table.LastRenderStats.RenderCount;
        operation();
        table.UpdateLayout();
        Assert.AreEqual(renders + 1, table.LastRenderStats.RenderCount, "操作一回につき描画は一回です。");
        return (table.LastRenderStats.TextCacheMisses, table.LastRenderStats.TextCacheHits, [.. fetches.Select((count, index) => count - before[index])]);
    }

    /// <summary>
    /// 可視列が同じ横スクロール、選択と現在セルの変更では文字のレイアウトを作らず値も取得せず、新しく見える列・行の分だけ作って取得することを、実表示した表で確認します。
    /// 縦に1行と5行スクロールして戻る場合、文字列が同じで色だけ変わる行の更新（その行の値だけ取り直し、レイアウトは作らず、画素は新しい色）、
    /// 列幅の変更（新しく省略が要るセルの分だけ作り、収まる側への変更では作らない）も確認します。
    /// </summary>
    /// <remarks>スクロールや選択のたびに文字を整形し直す性能上の誤り、保持範囲の行を取り直す誤り、色だけの更新で整形し直す誤り、幅の変更で全セルを整形し直す誤りを検出します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void TextLayoutsAndValues_AreReusedAcrossScrollSelectionColorChangeAndResize()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            (CustomTableView table, int[] fetches) = CreateReuseTable(6, 50, 300d, 134d);
            var window = new Window { Width = 360d, Height = 260d, Content = table };
            try
            {
                scope.ShowAndWaitForContentRendered(window);
                ScrollBar horizontal = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Horizontal);
                ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
                RenderExistingTable(table);
                int drawn = table.CalculateDrawableRowCapacity();
                Assert.IsTrue(drawn > 1 && drawn < 50);
                int[] None() => new int[6];
                int[] Plus(int rows, params int[] columns) { int[] delta = new int[6]; foreach (int column in columns) { delta[column] = rows; } return delta; }

                // 可視列が同じ範囲の横スクロール、選択、現在セルの変更。
                int visibleColumns = table.GetColumnLayoutSnapshot().VisibleColumnCount;
                (int misses, int hits, int[] fetched) = ObserveRender(table, fetches, () => horizontal.Value = 10d);
                Assert.AreEqual(visibleColumns, table.GetColumnLayoutSnapshot().VisibleColumnCount, "可視列が同じ範囲です。");
                Assert.AreEqual(0, misses); Assert.IsTrue(hits > 0); CollectionAssert.AreEqual(None(), fetched);
                (misses, _, fetched) = ObserveRender(table, fetches, () => table.SelectedIndex = 3);
                Assert.AreEqual(0, misses); CollectionAssert.AreEqual(None(), fetched);
                var point = new Point(100d, table.RowGeometry.HeaderHeight + 2.5d * table.RowGeometry.RowHeight);
                (misses, _, fetched) = ObserveRender(table, fetches, () => Assert.IsTrue(table.HandleCellMouseDown(table.HitTestTable(point), MouseButton.Left, ModifierKeys.None, 1, point)));
                Assert.AreEqual(0, misses); CollectionAssert.AreEqual(None(), fetched);

                // 新しい列が見える横スクロール。見出しの文字列も新しく整形する。
                (misses, _, fetched) = ObserveRender(table, fetches, () => horizontal.Value = 70d);
                Assert.AreEqual(visibleColumns + 1, table.GetColumnLayoutSnapshot().VisibleColumnCount, "新しい列が一つ見えます。");
                Assert.AreEqual(drawn + 1, misses, "その列の文字列と見出しの分だけ作ります。");
                CollectionAssert.AreEqual(Plus(drawn, 4), fetched);
                visibleColumns = table.GetColumnLayoutSnapshot().VisibleColumnCount;

                // 縦に1行、5行スクロールして戻る。
                (misses, _, fetched) = ObserveRender(table, fetches, () => vertical.Value = 1d);
                Assert.AreEqual(visibleColumns, misses, "新しく見える行の分だけ作ります。");
                CollectionAssert.AreEqual(Plus(1, 0, 1, 2, 3, 4), fetched);
                (misses, _, fetched) = ObserveRender(table, fetches, () => vertical.Value = 6d);
                Assert.AreEqual((drawn - 1) * visibleColumns, misses);
                CollectionAssert.AreEqual(Plus(drawn - 1, 0, 1, 2, 3, 4), fetched);
                (misses, _, fetched) = ObserveRender(table, fetches, () => vertical.Value = 1d);
                Assert.AreEqual(0, misses, "保持範囲に残る行へ戻っても作りません。");
                CollectionAssert.AreEqual(None(), fetched, "保持範囲に残る行の値を取り直しません。");

                // 文字列は同じで色だけ変わる行の更新。画面の上端の行（行1）の値だけを取り直す。
                horizontal.Value = 0d;
                table.UpdateLayout();
                var colored = (ReuseRow)table.ItemsSource[1]!;
                int[] before = [.. fetches];
                SurfacePixels beforeColor = CustomTableRenderProbe.Read(table);
                colored.Foreground = Brushes.Red;
                colored.RaiseChanged();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                table.UpdateLayout();
                Assert.AreEqual(0, table.LastRenderStats.TextCacheMisses, "文字列が同じなら整形し直しません。");
                CollectionAssert.AreEqual(Plus(1, 0, 1, 2, 3), fetches.Select((count, index) => count - before[index]).ToArray(), "その行の値だけを取り直します。");
                double ppd = VisualTreeHelper.GetDpi(table).PixelsPerDip;
                var cell = new Int32Rect(0, table.RowGeometry.HeaderHeightPx, CustomTablePixelGeometry.ToPixels(80d, ppd), table.RowGeometry.RowHeightPx - 1);
                SurfacePixels afterColor = CustomTableRenderProbe.Read(table);
                int blackTextRed = beforeColor.CountDominant(cell).Red;
                Assert.IsTrue(afterColor.CountDominant(cell).Red > blackTextRed + 20, "画素は新しい色です。");

                // 列幅の変更。狭めると省略が要るセルの分だけ作り、収まる側へ戻しても作らない。値は取り直さない。
                horizontal.Value = 70d;
                table.UpdateLayout();
                void ResizeColumn3(int width)
                {
                    CustomTableColumn column = table.Columns[3];
                    int startWidth = column.Width;
                    double edge = 90d * 3 + startWidth - table.HorizontalOffset;
                    CustomTableHitTestResult resize = table.HitTestTable(new Point(edge, 10d));
                    Assert.AreEqual(CustomTableHitKind.HeaderResize, resize.Kind);
                    Assert.AreSame(column, resize.Column);
                    table.BeginColumnResize(resize, edge);
                    // 開始で入力を捕捉すると、現在のマウス位置の移動が同期的に処理され得るため、開始前の幅から求める。
                    table.UpdateColumnResize(edge + width - startWidth);
                    table.EndColumnResize();
                    Assert.AreEqual(width, column.Width, "列幅");
                }
                (misses, _, fetched) = ObserveRender(table, fetches, () => ResizeColumn3(45));
                Assert.AreEqual(drawn, misses, "新しく省略が要るセルの分だけ作ります。");
                CollectionAssert.AreEqual(None(), fetched);
                (misses, _, fetched) = ObserveRender(table, fetches, () => ResizeColumn3(200));
                Assert.AreEqual(0, misses, "収まる側への変更では作りません。");
                CollectionAssert.AreEqual(None(), fetched);
            }
            finally { window.Content = null; table.ItemsSource = null; window.Close(); }
        });
    }

    /// <summary>フォント・文字サイズ・表示倍率・表示文化（<c>CurrentCulture</c> と <c>CurrentUICulture</c>）のどれかが変わると、可視の全文字列を新しく整形し直すことを確認します。</summary>
    /// <remarks>環境の変更に古いレイアウトを使い続ける誤りを検出します。文化圏は共有ホストのスレッドで変えるため、元へ戻します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void TextLayouts_AreRebuiltWhenFontSizeDpiOrCultureChange()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            (CustomTableView table, int[] fetches) = CreateReuseTable(6, 50, 300d, 136d);
            CultureInfo culture = CultureInfo.CurrentCulture, uiCulture = CultureInfo.CurrentUICulture;
            CustomTableRenderProbe.SetDpi(table, 1d);
            CustomTableRenderProbe.Arrange(table, 300d, 136d);
            table.ApplyVisibilityChanged(true);
            try
            {
                RenderExistingTable(table);
                int lookups = table.GetColumnLayoutSnapshot().VisibleColumnCount * (table.CalculateDrawableRowCapacity() + 1);
                void AssertRebuilt(string state, Action change)
                {
                    (int misses, int hits, _) = ObserveRender(table, fetches, change);
                    Assert.AreEqual(0, hits, state);
                    Assert.AreEqual(lookups, misses, state + " 可視の全文字列を新しく整形します。");
                }
                AssertRebuilt("フォント", () => table.TextFontFamily = AppFontService.CreateFontFamily("Arial"));
                AssertRebuilt("文字サイズ", () => table.TextFontSize = table.TextFontSize + 1d);
                AssertRebuilt("表示倍率", () => CustomTableRenderProbe.SetDpi(table, 1.25d));
                CultureInfo other = culture.Name == "fr-FR" ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.GetCultureInfo("fr-FR");
                AssertRebuilt("CurrentCulture", () => { CultureInfo.CurrentCulture = other; CustomTableRenderProbe.Surface(table).InvalidateVisual(); });
                AssertRebuilt("CurrentUICulture", () => { CultureInfo.CurrentUICulture = other; CustomTableRenderProbe.Surface(table).InvalidateVisual(); });
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = uiCulture;
                ReleaseTable(table);
            }
        });
    }

    /// <summary>アプリの表示言語を変えると、可視の全文字列を新しく整形し直すことを、実表示した表で確認します。</summary>
    /// <remarks>言語の変更に古いレイアウトを使い続ける誤りを検出します。共有ホストの言語を変えるため、元へ戻します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void TextLayouts_AreRebuiltWhenTheAppLanguageChanges()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            CultureInfo? previousResourcesCulture = Resources.Culture;
            (CustomTableView table, int[] fetches) = CreateReuseTable(6, 50, 300d, 134d);
            var window = new Window { Width = 360d, Height = 260d, Content = table };
            try
            {
                ResourceService.Current.ChangeCulture("ja-JP");
                scope.ShowAndWaitForContentRendered(window);
                RenderExistingTable(table);
                int lookups = table.GetColumnLayoutSnapshot().VisibleColumnCount * (table.CalculateDrawableRowCapacity() + 1);
                (int misses, int hits, _) = ObserveRender(table, fetches, () =>
                {
                    ResourceService.Current.ChangeCulture("en-US");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                });
                Assert.AreEqual(0, hits);
                Assert.AreEqual(lookups, misses, "アプリの言語の変更で、可視の全文字列を新しく整形します。");
            }
            finally
            {
                window.Content = null; table.ItemsSource = null; window.Close();
                ResourceService.Current.ChangeCulture(previousResourcesCulture?.Name ?? "en-US");
                Resources.Culture = previousResourcesCulture;
                TestUiDispatcherHost.ProcessQueuedPresentation();
            }
        });
    }

    /// <summary>大きな表示範囲でも、同じ状態の再描画ではレイアウトを新しく作らず、値も取得しないことを確認します。</summary>
    /// <remarks>再描画のたびに全セルを整形し直す性能上の誤りを検出します。</remarks>
    [TestMethod]
    public void Redraw_OfTheSameStateReusesEveryLayoutAndValue()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            (CustomTableView table, int[] fetches) = CreateReuseTable(12, 60, 1100d, 20d + 19d * 30d);
            CustomTableRenderProbe.SetDpi(table, 1d);
            CustomTableRenderProbe.Arrange(table, 1100d, 20d + 19d * 30d);
            table.ApplyVisibilityChanged(true);
            try
            {
                RenderExistingTable(table);
                int lookups = 12 * (30 + 1);
                (int misses, int hits, int[] fetched) = ObserveRender(table, fetches, () => CustomTableRenderProbe.Surface(table).InvalidateVisual());
                Assert.AreEqual(0, misses);
                Assert.AreEqual(lookups, hits);
                CollectionAssert.AreEqual(new int[12], fetched);
            }
            finally { ReleaseTable(table); }
        });
    }

    private sealed class StyledRow
    {
        public bool HasHighlightedWarning { get; set; }
    }

}
