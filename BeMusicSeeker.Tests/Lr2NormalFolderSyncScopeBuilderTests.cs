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
            currentBmsChartPaths: null);
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
            currentBmsChartPaths: [siblingPath]);
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
            currentBmsChartPaths: []);
        Lr2NormalFolderSyncScope scope = Lr2NormalFolderSyncScopeBuilder.CreateForCatalogMutation(
            [root],
            receipt);

        Assert.AreEqual(0, scope.ChartPaths.Count);
        Assert.AreEqual(0, scope.PruneScopeDirectories.Count);
        CollectionAssert.AreEqual(new[] { root }, scope.PruneExactDirectories.ToList());
    }

    [TestMethod]
    public void CatalogMutationReceipt_CopiesCurrentBmsChartPaths()
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
            currentBmsChartPaths: sourcePaths);

        sourcePaths.Add(laterPath);

        Lr2NormalFolderCurrentBmsLookup lookup = receipt.CreateCurrentBmsLookup();
        Assert.IsTrue(lookup.HasBmsChartUnderDirectory(Path.GetDirectoryName(currentPath)));
        Assert.IsFalse(lookup.HasBmsChartUnderDirectory(Path.GetDirectoryName(laterPath)));
        Assert.AreEqual(6, receipt.OwnedCollectionVersion);
    }

    private sealed class TestableBmsFile : BMSFile
    {
    }
}
