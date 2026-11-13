using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2NormalFolderSyncScopeBuilderTests
{
    /// <summary>
    /// 追加だけの変更事実から対象パスを作り、既存ディレクトリを削除候補へ混ぜる誤りを検出します。
    /// </summary>
    [TestMethod]
    public void CreateForCatalogMutation_AddOnly_UsesReceiptFacts()
    {
        string root = Path.Combine("C:\\BMS");
        ChartFile added = ChartTestValues.Empty() with
        {
            Path = Path.Combine(root, "Package", "chart.bms")
        };
        var receipt = new Lr2NormalFolderCatalogMutationReceipt(
            catalogRevision: 3,
            addedBmsChartPaths: [added.Path],
            removedBmsChartPaths: [],
            pathChanges: [],
            currentBmsFacts: null);
        Lr2NormalFolderSyncScope scope = Lr2NormalFolderSyncScopeBuilder.CreateForCatalogMutation(
            [root],
            receipt);

        CollectionAssert.AreEqual(new[] { added.Path }, scope.ChartPaths.ToList());
        Assert.AreEqual(0, scope.PruneScopeDirectories.Count);
        Assert.AreEqual(0, scope.PruneExactDirectories.Count);
    }

    /// <summary>
    /// 削除したディレクトリだけを削除範囲へ含め、現在の兄弟譜面を再追加対象や削除対象へ巻き込む誤りを検出します。
    /// </summary>
    [TestMethod]
    public void CreateForCatalogMutation_RemoveUsesCurrentSnapshotForPrunedDirectory()
    {
        string root = Path.Combine("C:\\BMS");
        string removedDirectory = Path.Combine(root, "Package", "Remove");
        string removedPath = Path.Combine(removedDirectory, "chart.bms");
        string siblingPath = Path.Combine(root, "Package", "Keep", "chart.bms");
        var receipt = new Lr2NormalFolderCatalogMutationReceipt(
            catalogRevision: 4,
            addedBmsChartPaths: [],
            removedBmsChartPaths: [removedPath],
            pathChanges: [],
            currentBmsFacts: Lr2NormalFolderCurrentBmsLookup.CreateFromScopedFacts(
                [],
                [new KeyValuePair<string, IReadOnlyList<string>>(
                    Path.GetDirectoryName(siblingPath)!,
                    [siblingPath])]));
        Lr2NormalFolderSyncScope scope = Lr2NormalFolderSyncScopeBuilder.CreateForCatalogMutation(
            [root],
            receipt);

        Assert.AreEqual(0, scope.ChartPaths.Count);
        CollectionAssert.AreEqual(new[] { removedDirectory }, scope.PruneScopeDirectories.ToList());
        CollectionAssert.AreEqual(new[] { removedDirectory }, scope.PruneExactDirectories.ToList());
    }

    /// <summary>
    /// ルート直下の譜面削除はルート行だけの完全一致削除に限定し、全配下を削除範囲にする誤りを検出します。
    /// </summary>
    [TestMethod]
    public void CreateForCatalogMutation_RootLevelRemovalDoesNotPruneWholeRoot()
    {
        string root = Path.Combine("C:\\BMS");
        string removedPath = Path.Combine(root, "chart.bms");

        var receipt = new Lr2NormalFolderCatalogMutationReceipt(
            catalogRevision: 5,
            addedBmsChartPaths: [],
            removedBmsChartPaths: [removedPath],
            pathChanges: [],
            currentBmsFacts: Lr2NormalFolderCurrentBmsLookup.CreateFromScopedFacts([], []));
        Lr2NormalFolderSyncScope scope = Lr2NormalFolderSyncScopeBuilder.CreateForCatalogMutation(
            [root],
            receipt);

        Assert.AreEqual(0, scope.ChartPaths.Count);
        Assert.AreEqual(0, scope.PruneScopeDirectories.Count);
        CollectionAssert.AreEqual(new[] { root }, scope.PruneExactDirectories.ToList());
    }

    /// <summary>
    /// 削除元の存続する直接の親と、変更元を含む登録ルートだけを書込み範囲へ入れることを確認します。
    /// 兄弟が残る親（Cat）は入り、BMSが残らない親（Empty）とルート外の削除元、変更と無関係な登録ルートは入りません。
    /// 変更元を含む登録ルート（BMSが残らないR2を含む）は、BMSの有無によらず入ります。中間の親の日時の取り残し、
    /// 消える親の誤った保持、無関係なルートの更新を検出します。
    /// </summary>
    [TestMethod]
    public void CreateForCatalogMutation_AddsSurvivingDirectParentsAndChangedRootsOnly()
    {
        string root = Path.Combine("C:\\BMS");
        string emptyRoot = Path.Combine("C:\\BMS2");
        string unrelatedRoot = Path.Combine("C:\\BMS3");
        string cat = Path.Combine(root, "Cat");
        string songA = Path.Combine(cat, "SongA");
        string songB = Path.Combine(cat, "SongB");
        string empty = Path.Combine(root, "Empty");
        string songC = Path.Combine(empty, "SongC");
        string songD = Path.Combine(emptyRoot, "SongD");
        string outside = Path.Combine("D:\\Other", "X");
        var receipt = new Lr2NormalFolderCatalogMutationReceipt(
            catalogRevision: 8,
            addedBmsChartPaths: [],
            removedBmsChartPaths:
            [
                Path.Combine(songA, "a.bms"),
                Path.Combine(songC, "c.bms"),
                Path.Combine(songD, "d.bms"),
                Path.Combine(outside, "x.bms")
            ],
            pathChanges: [],
            currentBmsFacts: Lr2NormalFolderCurrentBmsLookup.CreateFromScopedFacts(
                [
                    new KeyValuePair<string, int>(songA, 0),
                    new KeyValuePair<string, int>(cat, 1),
                    new KeyValuePair<string, int>(songC, 0),
                    new KeyValuePair<string, int>(empty, 0),
                    new KeyValuePair<string, int>(root, 1),
                    new KeyValuePair<string, int>(songD, 0),
                    new KeyValuePair<string, int>(emptyRoot, 0)
                ],
                [new KeyValuePair<string, IReadOnlyList<string>>(songB, [Path.Combine(songB, "b.bms")])]));

        Lr2NormalFolderSyncScope scope = Lr2NormalFolderSyncScopeBuilder.CreateForCatalogMutation(
            [root, emptyRoot, unrelatedRoot],
            receipt);

        CollectionAssert.AreEquivalent(new[] { root, cat, emptyRoot }, scope.DirectoryPaths.ToList());
        Assert.AreEqual(0, scope.ChartPaths.Count);
        CollectionAssert.AreEquivalent(new[] { songA, songC, songD }, scope.PruneScopeDirectories.ToList());
    }

    /// <summary>
    /// 捕捉後に元のパス一覧を変更しても受渡し済みの検索事実と世代を保ち、後続変更の混入を検出します。
    /// </summary>
    [TestMethod]
    public void CatalogMutationReceipt_CopiesCurrentBmsFacts()
    {
        string root = Path.Combine("C:\\BMS");
        string currentPath = Path.Combine(root, "Package", "Keep", "chart.bms");
        string laterPath = Path.Combine(root, "Package", "Later", "chart.bms");
        var sourcePaths = new List<string> { currentPath };
        var receipt = new Lr2NormalFolderCatalogMutationReceipt(
            catalogRevision: 6,
            addedBmsChartPaths: [],
            removedBmsChartPaths: [],
            pathChanges: [],
            currentBmsFacts: Lr2NormalFolderCurrentBmsLookup.CreateFromScopedFacts(
                [],
                [new KeyValuePair<string, IReadOnlyList<string>>(
                    Path.GetDirectoryName(currentPath)!,
                    sourcePaths)]));

        sourcePaths.Add(laterPath);

        Lr2NormalFolderCurrentBmsLookup lookup = receipt.CreateCurrentBmsLookup();
        Assert.IsTrue(lookup.HasBmsChartUnderDirectory(Path.GetDirectoryName(currentPath)));
        Assert.IsFalse(lookup.HasBmsChartUnderDirectory(Path.GetDirectoryName(laterPath)));
        CollectionAssert.AreEqual(
            new[] { currentPath },
            lookup.GetBmsChartPathsUnderDirectory(Path.GetDirectoryName(currentPath)).ToList());
        Assert.AreEqual(6, receipt.CatalogRevision);
    }

    /// <summary>
    /// 移動後の大文字小文字違いの二保存キーをともに同期し、空になった元ディレクトリだけを完全一致削除へ含めることを確認します。
    /// </summary>
    [TestMethod]
    public void CreateForCatalogMutation_PreservesExactChartPathsInScopedFacts()
    {
        string root = Path.Combine("C:\\BMS");
        string oldDirectory = Path.Combine(root, "Pack", "Old");
        string newDirectory = Path.Combine(root, "Pack", "New");
        string oldPath = Path.Combine(oldDirectory, "chart.bms");
        string newPath = Path.Combine(newDirectory, "chart.bms");
        string caseVariantPath = Path.Combine(newDirectory, "Chart.bms");
        var receipt = new Lr2NormalFolderCatalogMutationReceipt(
            catalogRevision: 7,
            addedBmsChartPaths: [],
            removedBmsChartPaths: [],
            pathChanges: [new Lr2NormalFolderPathChange(oldPath, newPath)],
            currentBmsFacts: Lr2NormalFolderCurrentBmsLookup.CreateFromScopedFacts(
                [
                    new KeyValuePair<string, int>(oldDirectory, 0),
                    new KeyValuePair<string, int>(newDirectory, 2),
                    new KeyValuePair<string, int>(root, 2)
                ],
                [new KeyValuePair<string, IReadOnlyList<string>>(newDirectory, [newPath, caseVariantPath])]));

        Lr2NormalFolderSyncScope scope = Lr2NormalFolderSyncScopeBuilder.CreateForCatalogMutation(
            [root],
            receipt);

        CollectionAssert.AreEquivalent(new[] { newPath, caseVariantPath }, scope.ChartPaths.ToList());
        CollectionAssert.AreEquivalent(new[] { oldDirectory, newDirectory }, scope.PruneScopeDirectories.ToList());
        CollectionAssert.AreEquivalent(new[] { oldDirectory }, scope.PruneExactDirectories.ToList());
    }

}
