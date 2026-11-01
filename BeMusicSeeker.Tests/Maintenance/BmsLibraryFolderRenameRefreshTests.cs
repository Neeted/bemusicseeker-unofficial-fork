using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryFolderRenameRefreshTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryFolderRenameRefreshTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>背景Entryと同一親の譜面を別々に増やしても、必要祖先の取得・置換を譜面数倍にせず対象外を維持します。</summary>
    [TestMethod, TestCategory("Performance")]
    [DataRow(4096, 1)]
    [DataRow(0, 256)]
    public void FolderMutation_WorkScalesWithAffectedAncestors(int backgroundEntries, int parentCharts)
        => AssertFinalFilesystemAncestors(databaseScope, true, backgroundEntries, parentCharts);

}
