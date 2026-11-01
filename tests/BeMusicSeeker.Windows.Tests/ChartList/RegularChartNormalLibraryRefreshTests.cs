using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>実libraryから通常一覧への変更通知、依存失効と停止時の収束を検証します。</summary>
/// <remarks>Windowを使わず実SQLite・package所有者からの通知接続を確認し、表示操作の接続はMainWindowのcompiled画面caseが分担します。</remarks>
[TestClass]
public sealed partial class RegularChartNormalLibraryRefreshTests
{
    public TestContext? TestContext { get; set; }

    /// <summary>実package所有者が確定したタイトル変更の通知から一覧を再整列し、元項目と旧snapshotを保持して、旧タイトル順の残留や項目差替えを検出します。</summary>
    [TestMethod]
    public void InstalledTitleChangeThroughOwnerFactsRebuildsFinalTitleOrder()
    {
        var databaseScope = new ApplicationDatabaseScope();
        WithTemporarySongDb(songDbPath =>
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath);
            var table = new MainChartListViewModel();
            using RegularChartListOwner receiver = CreateOwner(table, CreateWorkspaceForOwner(table));
            var producer = new PackageLifecycleOwner(new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), _ => { },
                packages => new System.Collections.ObjectModel.ObservableCollection<ChartPackage>(packages ?? []), () => { }, _ => { });
            ChartFile before = ChartTestValues.Empty(ChartFileKind.Bms) with
            {
                Token = new OwnedChartToken(),
                Path = @"C:\Charts\a.bms",
                Title = "A",
                RawTitle = "A"
            };
            var first = PackageChartEntry.FromChart(before);
            var second = PackageChartEntry.FromChart(before with { Token = new OwnedChartToken(), Path = @"C:\Charts\m.bms", Title = "M", RawTitle = "M" });
            var package = ChartPackage.FromChartEntries([first, second]);
            producer.ReplaceInstalledPackages([package]);
            var request = new RegularVirtualChartSubsetApplyRequest
            {
                Library = library,
                SourceEntries = [first, second],
                TreeMode = MainViewUpdateMode.NewlyInstalledFolderSelected,
                SubsetName = "installed_title_connection",
                KeywordFilter = "",
                ModeFilter = ChartModeFilter.All,
                SortColumnName = nameof(LibraryChartRow.Title),
                SortDirection = System.ComponentModel.ListSortDirection.Ascending,
                ColumnSelection = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
                    MainViewUpdateMode.NewlyInstalledFolderSelected, System.Windows.Visibility.Collapsed, new PlaylistSummaryColumnSettings()),
                Mode = MainViewUpdateMode.NewlyInstalledFolderSelected,
                Stopwatch = System.Diagnostics.Stopwatch.StartNew()
            };
            try
            {
                Assert.IsTrue(receiver.TryApplyVirtualChartSubset(request).WasCommitted);
                LibraryChartRow[] initialRows = table.Rows.Cast<LibraryChartRow>().ToArray();
                Assert.AreEqual("A", initialRows[0].Title);
                var changes = new List<InstalledChartCurrentChange>();
                ChartFile current = before with { Title = "Z", RawTitle = "Z" };
                producer.PrepareCommittedChartApplication([current], changes)();
                var publisher = new NormalLibraryRefreshPublisher();
                publisher.Publish(new NormalLibraryRefreshPublishRequest
                {
                    Effects = LibraryChartRefreshEffects.SourceChanged,
                    ChangedCharts = [current],
                    InstalledChartChanges = changes
                });
                NormalLibraryRefreshNotificationBatch batch = publisher.GetNotificationsAfter(0);
                Assert.AreEqual(MainViewRefreshAction.Refresh, receiver.ApplyNotificationPresentation(library, batch,
                    MainViewUpdateMode.NewlyInstalledFolderSelected, package, ChartListFilterSnapshot.Default, false));
                request.Stopwatch = System.Diagnostics.Stopwatch.StartNew();
                Assert.IsTrue(receiver.TryApplyVirtualChartSubset(request).WasCommitted);
                LibraryChartRow[] finalRows = table.Rows.Cast<LibraryChartRow>().ToArray();
                Assert.AreEqual("M", finalRows[0].Title);
                Assert.AreEqual("Z", finalRows[1].Title);
                Assert.AreSame(first, finalRows[1].PackageEntry);
                Assert.AreEqual("A", before.Title);
            }
            finally
            {
                producer.ClearInstalledPackages();
            }
        });
    }

}
