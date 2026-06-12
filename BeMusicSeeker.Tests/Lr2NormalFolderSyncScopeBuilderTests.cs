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
    public void CreateForStorageMutation_AddOnly_DoesNotQueryCurrentLookup()
    {
        string root = Path.Combine("C:\\BMS");
        var added = new TestableBmsFile
        {
            path = Path.Combine(root, "Package", "chart.bms")
        };
        var throwingLookup = new Lr2NormalFolderCurrentBmsLookup(
            _ => throw new InvalidOperationException("Add-only mutation should not query current BMS directories."),
            _ => throw new InvalidOperationException("Add-only mutation should not enumerate current BMS paths."));

        Lr2NormalFolderSyncScope scope = Lr2NormalFolderSyncScopeBuilder.CreateForStorageMutation(
            [root],
            [added],
            [],
            [],
            throwingLookup);

        CollectionAssert.AreEqual(new[] { added.path }, scope.ChartPaths.ToList());
        Assert.AreEqual(0, scope.PruneScopeDirectories.Count);
        Assert.AreEqual(0, scope.PruneExactDirectories.Count);
    }

    [TestMethod]
    public void CreateForStorageMutation_RemoveUsesCurrentLookupForPrunedDirectory()
    {
        string root = Path.Combine("C:\\BMS");
        string removedDirectory = Path.Combine(root, "Package", "Remove");
        string removedPath = Path.Combine(removedDirectory, "chart.bms");
        string siblingPath = Path.Combine(root, "Package", "Keep", "chart.bms");
        var enumeratedDirectories = new List<string>();
        var lookup = new Lr2NormalFolderCurrentBmsLookup(
            directory => ContainsPathUnderDirectory(siblingPath, directory),
            directory =>
            {
                enumeratedDirectories.Add(directory);
                return ContainsPathUnderDirectory(siblingPath, directory)
                    ? [siblingPath]
                    : [];
            });

        Lr2NormalFolderSyncScope scope = Lr2NormalFolderSyncScopeBuilder.CreateForStorageMutation(
            [root],
            [],
            [],
            [OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, removedPath)],
            lookup);

        Assert.AreEqual(0, scope.ChartPaths.Count);
        CollectionAssert.AreEqual(new[] { removedDirectory }, scope.PruneScopeDirectories.ToList());
        CollectionAssert.AreEqual(new[] { removedDirectory }, scope.PruneExactDirectories.ToList());
        CollectionAssert.AreEqual(new[] { removedDirectory }, enumeratedDirectories);
    }

    [TestMethod]
    public void CreateForStorageMutation_RootLevelRemovalDoesNotPruneWholeRoot()
    {
        string root = Path.Combine("C:\\BMS");
        string removedPath = Path.Combine(root, "chart.bms");

        Lr2NormalFolderSyncScope scope = Lr2NormalFolderSyncScopeBuilder.CreateForStorageMutation(
            [root],
            [],
            [],
            [OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, removedPath)],
            Lr2NormalFolderCurrentBmsLookup.Empty);

        Assert.AreEqual(0, scope.ChartPaths.Count);
        Assert.AreEqual(0, scope.PruneScopeDirectories.Count);
        CollectionAssert.AreEqual(new[] { root }, scope.PruneExactDirectories.ToList());
    }

    private static bool ContainsPathUnderDirectory(string chartPath, string directoryPath)
    {
        string chartDirectory = Lr2FolderPath.NormalizeDirectoryPath(Path.GetDirectoryName(chartPath));
        string directory = Lr2FolderPath.NormalizeDirectoryPath(directoryPath);
        return !string.IsNullOrWhiteSpace(chartDirectory)
            && !string.IsNullOrWhiteSpace(directory)
            && Lr2FolderPath.IsSameOrDescendant(chartDirectory, directory);
    }

    private sealed class TestableBmsFile : BMSFile
    {
    }
}
