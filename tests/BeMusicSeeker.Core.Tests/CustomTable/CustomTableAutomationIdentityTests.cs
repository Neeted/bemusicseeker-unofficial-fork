using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class CustomTableAutomationIdentityTests
{
    /// <summary>実libraryの保存先から同DBのdomainキーを共有しても、別instanceへscopeを共有しません。packageの外部scopeは構築時のpathに固定し、後続変更を読みません。</summary>
    [TestMethod]
    public void ChartScopes_LibraryDomainAndCapturedPackagePathRemainSeparateFromLifetime()
    {
        RegularChartListOwnerTestSupport.WithTemporarySongDb(path =>
        {
            var databaseScope = new ApplicationDatabaseScope();
            var library = new TestBmsLibrary(databaseScope, path);
            var anotherLibrary = new TestBmsLibrary(databaseScope, path);
            var owner = new CatalogOwnedCollectionOwner();
            owner.ReplaceCharts([Chart(@"C:\Library\a.bms")], []);
            CatalogChartCollectionSnapshot snapshot = owner.CaptureSnapshot();
            ChartListVirtualView before = Normal(snapshot, (library, true, (RegularChartFolderFilterKind?)null, (string?)null));
            ChartListVirtualView another = Normal(snapshot, (anotherLibrary, true, (RegularChartFolderFilterKind?)null, (string?)null));
            Assert.AreEqual(Item(before, 0).AutomationId, Item(another, 0).AutomationId);
            Assert.IsFalse(before.SharesSelectionIdentityScope(another));

            var package = new ChartPackage { path = @"C:\Pending\before.zip" };
            ChartListSourceRow[] source = [.. ChartListSourceRow.BuildPackageRows([PackageChartEntry.FromChart(Chart(@"C:\Pending\a.bms"))])];
            ChartListVirtualView PackageView() => new(source, ChartListOrder.CreateTitleAscending(source), NeverMaterialize,
                selectionScope: (library, MainViewUpdateMode.PendingInstallFolderSelected, "pending", ChartListSourceProjectionMode.PreserveSourceProjection, package));
            ChartListVirtualView captured = PackageView();
            CustomTableAutomationItem original = Item(captured, 0);
            package.path = @"C:\Pending\after.zip";
            ChartListVirtualView changed = PackageView();
            Assert.AreEqual(original.AutomationId, Item(captured, 0).AutomationId);
            Assert.AreNotEqual(original.AutomationId, Item(changed, 0).AutomationId);
            Assert.AreSame(original.Identity, Item(changed, 0).Identity);
            Assert.AreEqual(0, captured.RealizedRowCount); Assert.AreEqual(0, changed.RealizedRowCount);
        });
    }

    /// <summary>同title/hashでもexact path別の通常譜面を別IDで検索し、同じscopeの再評価だけが同じdomainキーを保ちます。未訪問行を生成しません。</summary>
    [TestMethod]
    public void NormalCharts_ExactPathsAndScopesDistinguishTargetsWithoutRealization()
    {
        var owner = new CatalogOwnedCollectionOwner();
        ChartFile first = Chart(@"C:\Library\a.bms"), second = Chart(@"C:\Library\b.bms");
        owner.ReplaceCharts([first, second], []);
        CatalogChartCollectionSnapshot snapshot = owner.CaptureSnapshot();
        ChartListVirtualView rows = Normal(snapshot, (false, "folder", "A"));
        ChartListVirtualView sameScope = Normal(snapshot, (false, "folder", "A"));
        ChartListVirtualView otherScope = Normal(snapshot, (false, "folder", "B"));
        ChartListVirtualView unknownScope = Normal(snapshot, new object());

        CustomTableAutomationItem a = Item(rows, 0), b = Item(rows, 1);
        Assert.AreSame(first.Token, a.Identity); Assert.AreSame(second.Token, b.Identity);
        Assert.AreNotEqual(a.AutomationId, b.AutomationId);
        Assert.AreEqual(a.AutomationId, Item(sameScope, 0).AutomationId);
        Assert.AreNotEqual(a.AutomationId, Item(otherScope, 0).AutomationId);
        Assert.IsTrue(rows.SharesSelectionIdentityScope(sameScope));
        Assert.IsFalse(rows.SharesSelectionIdentityScope(otherScope));
        Assert.AreEqual(0, rows.FindAutomationItem(a.AutomationId, -1));
        Assert.AreEqual(1, rows.FindAutomationItem(b.AutomationId, 0));
        Assert.AreEqual(-1, rows.FindAutomationItem(a.AutomationId, 0));
        Assert.AreEqual(-1, rows.FindAutomationItem(a.AutomationId, int.MaxValue));
        Assert.AreEqual(-1, rows.FindAutomationItem(Item(otherScope, 0).AutomationId, -1));
        Assert.IsNull(unknownScope.GetAutomationItem(0));
        Assert.AreEqual(-1, unknownScope.FindAutomationItem(a.AutomationId, -1));
        Assert.AreEqual(1, rows.IndexOfAutomationIdentity(b.Identity));
        Assert.IsNull(rows.GetAutomationItem(-1)); Assert.IsNull(rows.GetAutomationItem(2));
        Assert.AreEqual(0, rows.RealizedRowCount); Assert.AreEqual(0, sameScope.RealizedRowCount); Assert.AreEqual(0, otherScope.RealizedRowCount);
    }

    /// <summary>ownerが同pathの別tokenへ置換されても、旧snapshotの検索は旧対象を返し、新viewは旧tokenを救済しません。</summary>
    [TestMethod]
    public void NormalCharts_CapturedSnapshotRetainsOldLookupAfterOwnerReplacement()
    {
        var owner = new CatalogOwnedCollectionOwner();
        ChartFile original = Chart(@"C:\Library\same.bms");
        owner.ReplaceCharts([original], []);
        ChartListVirtualView before = Normal(owner.CaptureSnapshot(), "library");
        CustomTableAutomationItem old = Item(before, 0);
        ChartFile replacement = Chart(original.Path);
        owner.ReplaceCharts([replacement], []);
        ChartListVirtualView after = Normal(owner.CaptureSnapshot(), "library");

        Assert.AreEqual(old.AutomationId, Item(after, 0).AutomationId);
        Assert.AreSame(original.Token, Item(before, before.FindAutomationItem(old.AutomationId, -1)).Identity);
        Assert.AreSame(replacement.Token, Item(after, after.FindAutomationItem(old.AutomationId, -1)).Identity);
        Assert.AreEqual(-1, after.IndexOfAutomationIdentity(old.Identity));
        Assert.AreEqual(0, before.IndexOfAutomationIdentity(old.Identity));
        Assert.AreEqual(0, before.RealizedRowCount); Assert.AreEqual(0, after.RealizedRowCount);
    }

    /// <summary>専用のtokenなし行は捕捉source参照で寿命を区別します。同pathの別sourceへ移さず、既存順序索引の有無の両経路で未訪問行を生成しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SubsetCharts_SamePathReplacementDoesNotRescueCapturedSource(bool indexed)
    {
        ChartFile chart = Chart(@"C:\Pending\same.bms") with { Token = null };
        var captured = ChartListSourceRow.FromChartFile(chart);
        var replacement = ChartListSourceRow.FromChartFile(chart with { });
        ChartListVirtualView View(ChartListSourceRow source)
            => new([source], ChartListOrder.CreateTitleAscending([source]), NeverMaterialize,
                scoreInput: indexed ? new ChartListScoreInput(null) : null, selectionScope: ("pending", "package"));
        ChartListVirtualView before = View(captured); ChartListVirtualView after = View(replacement);
        CustomTableAutomationItem old = Item(before, 0);

        Assert.AreSame(captured, old.Identity);
        Assert.AreEqual(old.AutomationId, Item(after, 0).AutomationId);
        Assert.AreEqual(0, before.IndexOfAutomationIdentity(old.Identity));
        Assert.AreEqual(-1, after.IndexOfAutomationIdentity(old.Identity));
        Assert.AreEqual(0, after.FindAutomationItem(old.AutomationId, -1));
        Assert.AreEqual(0, before.RealizedRowCount); Assert.AreEqual(0, after.RealizedRowCount);
    }

    /// <summary>同hashの別entryと別表を区別します。並び替えた同じentryは追従し、同じ保存値・順序の別entryへ旧peerを移しません。</summary>
    [TestMethod]
    public void PlaylistEntries_TableAndEntryLifetimesRemainDistinctWithoutRealization()
    {
        var table = new BMSTable { playlist_id = 10 };
        var first = new BMSTableEntry { parent = table, md5 = Hash, title = "same" };
        var second = new BMSTableEntry { parent = table, md5 = Hash, title = "same" };
        var a = new PlaylistDetailSourceRow(first, null, entryOrder: 0);
        var b = new PlaylistDetailSourceRow(second, null, entryOrder: 1);
        var rows = new PlaylistDetailVirtualView([a, b]);
        var reordered = new PlaylistDetailVirtualView([b, a]);
        var replacement = new PlaylistDetailVirtualView([new(new BMSTableEntry { parent = table, md5 = Hash, title = "same" }, null, entryOrder: 0)]);
        var anotherTable = new BMSTable { playlist_id = 20 };
        var other = new PlaylistDetailVirtualView([new(new BMSTableEntry { parent = anotherTable, md5 = Hash }, null, entryOrder: 0)]);
        CustomTableAutomationItem old = Item(rows, 0), next = Item(rows, 1);

        Assert.AreSame(first, old.Identity); Assert.AreSame(second, next.Identity);
        Assert.AreNotEqual(old.AutomationId, next.AutomationId);
        Assert.AreNotEqual(old.AutomationId, Item(other, 0).AutomationId);
        Assert.AreEqual(1, reordered.IndexOfAutomationIdentity(old.Identity));
        Assert.AreEqual(-1, replacement.IndexOfAutomationIdentity(old.Identity));
        Assert.AreEqual(old.AutomationId, Item(replacement, 0).AutomationId);
        Assert.AreEqual(1, rows.FindAutomationItem(next.AutomationId, 0));
        Assert.AreEqual(-1, rows.FindAutomationItem(old.AutomationId, 0));
        Assert.AreEqual(0, rows.RealizedRowCount); Assert.AreEqual(0, reordered.RealizedRowCount); Assert.AreEqual(0, replacement.RealizedRowCount); Assert.AreEqual(0, other.RealizedRowCount);
    }

    /// <summary>同hash・同時刻の別playと、同history_idの別取得元を区別します。同じ保存値の再取得recordには旧参照を移しません。</summary>
    [TestMethod]
    public void HistoryRecords_RecordAndSourceIdentifyPlaysWithoutNewRows()
    {
        Lr2PlayHistoryRecord first = Raw(1); Lr2PlayHistoryRecord second = Raw(2);
        PlayHistoryVirtualView rows = History(@"C:\LR2\score.db", first, second);
        PlayHistoryVirtualView otherSource = History(@"C:\Other\score.db", Raw(1));
        PlayHistoryVirtualView reacquired = History(@"C:\LR2\score.db", Raw(1));
        var otherProvider = new PlayHistoryVirtualView(PlayHistoryRow.ProjectBeatorajaRows(
            new(PlayHistorySourceProfile.Beatoraja(@"C:\LR2\score.db"),
                [new() { history_id = 1, played_at = 1000, sha256 = new string('a', 64) }], [], Lr2PlayHistorySchemaStatus.Installed),
            PlayHistoryProjectionIndex.Empty).Rows);
        int realized = rows.RealizedRowCount;
        CustomTableAutomationItem a = Item(rows, 0), b = Item(rows, 1);

        Assert.AreSame(first, a.Identity); Assert.AreSame(second, b.Identity);
        Assert.AreNotEqual(a.AutomationId, b.AutomationId);
        Assert.AreNotEqual(a.AutomationId, Item(otherSource, 0).AutomationId);
        Assert.AreNotEqual(a.AutomationId, Item(otherProvider, 0).AutomationId);
        Assert.AreEqual(a.AutomationId, Item(reacquired, 0).AutomationId);
        Assert.AreEqual(-1, reacquired.IndexOfAutomationIdentity(a.Identity));
        Assert.AreEqual(1, rows.IndexOfAutomationIdentity(b.Identity));
        Assert.AreEqual(1, rows.FindAutomationItem(b.AutomationId, 0));
        Assert.AreEqual(-1, rows.FindAutomationItem(a.AutomationId, 0));
        Assert.AreEqual(realized, rows.RealizedRowCount);
    }

    /// <summary>同playlist_idの表参照交換後も外部検索はでき、旧表のpeerは失効します。実体化済みsummaryの既存表参照索引を使います。</summary>
    [TestMethod]
    public void PlaylistSummary_SameStoredIdDoesNotRescueReplacedTableLifetime()
    {
        var oldTable = new BMSTable { playlist_id = 7 };
        var newTable = new BMSTable { playlist_id = 7 };
        var rows = new PlaylistSummaryVersionedCollection();
        rows.ReplaceItemsWithoutNotification([new() { PlaylistId = 7, TableRef = oldTable }], 1);
        CustomTableAutomationItem old = Item(rows, 0);
        Assert.AreSame(oldTable, old.Identity); Assert.AreEqual(0, rows.IndexOfAutomationIdentity(old.Identity));
        rows.ReplaceItemsWithoutNotification([new() { PlaylistId = 7, TableRef = newTable }], 2);

        Assert.AreEqual(old.AutomationId, Item(rows, 0).AutomationId);
        Assert.AreEqual(-1, rows.IndexOfAutomationIdentity(old.Identity));
        Assert.AreEqual(0, rows.IndexOfAutomationIdentity(newTable));
        Assert.AreEqual(0, rows.FindAutomationItem(old.AutomationId, -1));
        Assert.AreEqual(-1, rows.FindAutomationItem(old.AutomationId, 0));
    }

    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static ChartFile Chart(string path) => ChartTestValues.Empty() with { Token = new OwnedChartToken(), Path = path, Md5 = Hash, Title = "same" };
    private static LibraryChartRow NeverMaterialize(ChartListSourceRow _) => throw new AssertFailedException("識別照会が行を実体化しています。");
    private static ChartListVirtualView Normal(CatalogChartCollectionSnapshot snapshot, object scope)
    {
        NormalLibrarySourceRows source = new MainChartRowProjectionOwner().BuildNormalSourceRows(null, snapshot, true);
        return new(source, ChartListOrder.CreateTitleAscending(source), NeverMaterialize, selectionScope: scope);
    }
    private static CustomTableAutomationItem Item(ICustomTableAutomationSource source, int index)
        => source.GetAutomationItem(index) ?? throw new AssertFailedException("対象の識別がありません。");
    private static Lr2PlayHistoryRecord Raw(long id) => new() { history_id = id, hash = Hash, played_at = 1000, finalized = 1 };
    private static PlayHistoryVirtualView History(string path, params Lr2PlayHistoryRecord[] records)
        => new(PlayHistoryRow.ProjectLr2Rows(new(PlayHistorySourceProfile.Lr2(path), records, [], Lr2PlayHistorySchemaStatus.Installed), PlayHistoryProjectionIndex.Empty).Rows);

}
