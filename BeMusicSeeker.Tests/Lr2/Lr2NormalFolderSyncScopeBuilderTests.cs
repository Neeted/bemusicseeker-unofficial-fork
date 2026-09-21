using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2NormalFolderSyncScopeBuilderTests
{
    [TestMethod]
    public void CreateForCatalogMutation_AddOnly_UsesReceiptFacts()
    {
        string root = Path.Combine("C:\\BMS");
        var added = new TestableBmsFile
        {
            path = Path.Combine(root, "Package", "chart.bms")
        };
        var receipt = new Lr2NormalFolderCatalogMutationReceipt(
            ownedCollectionVersion: 3,
            addedBmsChartPaths: [added.path],
            removedBmsChartPaths: [],
            pathChanges: [],
            currentBmsFacts: null);
        Lr2NormalFolderSyncScope scope = Lr2NormalFolderSyncScopeBuilder.CreateForCatalogMutation(
            [root],
            receipt);

        CollectionAssert.AreEqual(new[] { added.path }, scope.ChartPaths.ToList());
        Assert.AreEqual(0, scope.PruneScopeDirectories.Count);
        Assert.AreEqual(0, scope.PruneExactDirectories.Count);
    }

    [TestMethod]
    public void CreateForCatalogMutation_RemoveUsesCurrentSnapshotForPrunedDirectory()
    {
        string root = Path.Combine("C:\\BMS");
        string removedDirectory = Path.Combine(root, "Package", "Remove");
        string removedPath = Path.Combine(removedDirectory, "chart.bms");
        string siblingPath = Path.Combine(root, "Package", "Keep", "chart.bms");
        var receipt = new Lr2NormalFolderCatalogMutationReceipt(
            ownedCollectionVersion: 4,
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

    [TestMethod]
    public void CreateForCatalogMutation_RootLevelRemovalDoesNotPruneWholeRoot()
    {
        string root = Path.Combine("C:\\BMS");
        string removedPath = Path.Combine(root, "chart.bms");

        var receipt = new Lr2NormalFolderCatalogMutationReceipt(
            ownedCollectionVersion: 5,
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

    [TestMethod]
    public void CatalogMutationReceipt_CopiesCurrentBmsFacts()
    {
        string root = Path.Combine("C:\\BMS");
        string currentPath = Path.Combine(root, "Package", "Keep", "chart.bms");
        string laterPath = Path.Combine(root, "Package", "Later", "chart.bms");
        var sourcePaths = new List<string> { currentPath };
        var receipt = new Lr2NormalFolderCatalogMutationReceipt(
            ownedCollectionVersion: 6,
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
        Assert.AreEqual(6, receipt.OwnedCollectionVersion);
    }

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
            ownedCollectionVersion: 7,
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

    private sealed class TestableBmsFile : BMSFile
    {
    }
}
