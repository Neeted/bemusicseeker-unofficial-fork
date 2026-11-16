using BeMusicSeeker.Models;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryFolderRenameRefreshTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryFolderRenameRefreshTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>背景Entryと同一親の譜面を別々に増やしても、必要祖先の取得・置換を譜面数倍にせず対象外を維持します。</summary>
    [TestMethod, TestCategory("Suite.folder-mutation")]
    [DataRow(4096, 1)]
    [DataRow(0, 256)]
    public void FolderMutation_WorkScalesWithAffectedAncestors(int backgroundEntries, int parentCharts)
        => AssertFinalFilesystemAncestors(databaseScope, true, backgroundEntries, parentCharts,
            elapsed => Console.WriteLine(FormattableString.Invariant($"route=folder_mutation backgroundEntries={backgroundEntries} parentCharts={parentCharts} bodyElapsedMs={elapsed.TotalMilliseconds:F3}")));

}
