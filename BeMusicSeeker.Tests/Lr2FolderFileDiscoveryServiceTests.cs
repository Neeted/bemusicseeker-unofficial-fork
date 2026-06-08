using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2FolderFileDiscoveryServiceTests
{
    [TestMethod]
    public void CreateBuiltinFolderSourceDirectories_IncludesPhysicalLr2CustomFolder()
    {
        using TestDirectoryScope scope = TestDirectoryScope.Create();
        string lr2Root = Path.Combine(scope.DirectoryPath, "LR2");
        string customFolder = Path.Combine(lr2Root, "LR2files", "CustomFolder");
        Directory.CreateDirectory(customFolder);

        List<string> directories = Lr2FolderFileDiscoveryService.CreateBuiltinFolderSourceDirectories(lr2Root);

        CollectionAssert.AreEqual(new[] { Path.GetFullPath(customFolder) }, directories);
    }

    [TestMethod]
    public void CreateBuiltinFolderSourceDirectories_IgnoresMissingLr2CustomFolder()
    {
        using TestDirectoryScope scope = TestDirectoryScope.Create();
        string lr2Root = Path.Combine(scope.DirectoryPath, "LR2");
        Directory.CreateDirectory(lr2Root);

        List<string> directories = Lr2FolderFileDiscoveryService.CreateBuiltinFolderSourceDirectories(lr2Root);

        Assert.AreEqual(0, directories.Count);
    }

    [TestMethod]
    public void CreateDiscoveryDirectories_IncludesExistingBuiltinSourceAndOutputScopes()
    {
        using TestDirectoryScope scope = TestDirectoryScope.Create();
        string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
        string normalOutput = Path.Combine(scope.DirectoryPath, "Output");
        string rootOutput = Path.Combine(scope.DirectoryPath, "RootOutput");
        string builtinSource = Path.Combine(scope.DirectoryPath, "LR2", "LR2files", "CustomFolder");
        Directory.CreateDirectory(bmsRoot);
        Directory.CreateDirectory(normalOutput);
        Directory.CreateDirectory(builtinSource);

        List<string> directories = Lr2FolderFileDiscoveryService.CreateDiscoveryDirectories(
            [bmsRoot],
            normalOutput,
            rootOutput,
            [builtinSource]);

        CollectionAssert.Contains(directories, Path.GetFullPath(bmsRoot));
        CollectionAssert.Contains(directories, Path.GetFullPath(normalOutput));
        CollectionAssert.Contains(directories, Path.GetFullPath(builtinSource));
        CollectionAssert.DoesNotContain(directories, Path.GetFullPath(rootOutput));
    }

    [TestMethod]
    public void CreateDiscoveryDirectoriesForEnumeration_ExcludesPreparedOutputScopes()
    {
        using TestDirectoryScope scope = TestDirectoryScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string outputBase = Path.Combine(scope.DirectoryPath, "Output");
        string outputBasePrefixSibling = Path.Combine(scope.DirectoryPath, "OutputOther");
        string rootOutputBase = Path.Combine(scope.DirectoryPath, "RootOutput");
        var preparedSurface = new Lr2FullGenerationPreparedDataSurface(
            [outputBase],
            [],
            new Dictionary<string, RootFileEnumerationEntry>(System.StringComparer.OrdinalIgnoreCase),
            discoveryComplete: true);

        List<string> directories = Lr2FolderFileDiscoveryService.CreateDiscoveryDirectoriesForEnumeration(
            [rootDirectory, outputBase, outputBasePrefixSibling, rootOutputBase],
            preparedSurface).ToList();

        CollectionAssert.Contains(directories, rootDirectory);
        CollectionAssert.Contains(directories, outputBasePrefixSibling);
        CollectionAssert.Contains(directories, rootOutputBase);
        CollectionAssert.DoesNotContain(directories, outputBase);
    }

    [TestMethod]
    public void MergeCandidateSurface_ReplacesOnlyPreparedScopeCandidates()
    {
        using TestDirectoryScope scope = TestDirectoryScope.Create();
        string outputBase = Path.Combine(scope.DirectoryPath, "Output");
        string preparedDirectory = Path.Combine(outputBase, "Table");
        string preparedPrefixSibling = Path.Combine(outputBase, "TableOther");
        string oldManagedPath = Path.Combine(preparedDirectory, "old.lr2folder");
        string preparedPath = Path.Combine(preparedDirectory, "new.lr2folder");
        string siblingPath = Path.Combine(preparedPrefixSibling, "keep.lr2folder");
        string externalOutputPath = Path.Combine(outputBase, "external.lr2folder");
        var baseEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [oldManagedPath] = new RootFileEnumerationEntry(oldManagedPath),
            [siblingPath] = new RootFileEnumerationEntry(siblingPath),
            [externalOutputPath] = new RootFileEnumerationEntry(externalOutputPath)
        };
        var baseCandidates = new Lr2FolderFileCandidateSnapshot(
            [oldManagedPath, siblingPath, externalOutputPath],
            baseEntries,
            discoveryComplete: true);
        var preparedSurface = new Lr2FullGenerationPreparedDataSurface(
            [preparedDirectory],
            [preparedPath],
            new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [preparedPath] = new RootFileEnumerationEntry(preparedPath)
            },
            discoveryComplete: true);

        Lr2FolderFileCandidateSnapshot merged = Lr2FolderFileDiscoveryService.MergeCandidateSurface(
            baseCandidates,
            preparedSurface);

        CollectionAssert.Contains(merged.Paths.ToList(), preparedPath);
        CollectionAssert.Contains(merged.Paths.ToList(), siblingPath);
        CollectionAssert.Contains(merged.Paths.ToList(), externalOutputPath);
        CollectionAssert.DoesNotContain(merged.Paths.ToList(), oldManagedPath);
        Assert.IsTrue(merged.DiscoveryComplete);
    }

    [TestMethod]
    public void ExcludeAppManagedOutputCandidates_RemovesNestedOutputFilesOnly()
    {
        using TestDirectoryScope scope = TestDirectoryScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
        string managedDirectory = Path.Combine(outputBase, "Table");
        string managedPath = Path.Combine(managedDirectory, "managed.lr2folder");
        string externalOutputPath = Path.Combine(outputBase, "external.lr2folder");
        string externalPath = Path.Combine(rootDirectory, "External", "external.lr2folder");
        string prefixSiblingPath = Path.Combine(rootDirectory, "#BeMusicSeekerOutputOther", "keep.lr2folder");
        DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
        var entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [managedPath] = new RootFileEnumerationEntry(managedPath, timestamp),
            [externalOutputPath] = new RootFileEnumerationEntry(externalOutputPath, timestamp),
            [externalPath] = new RootFileEnumerationEntry(externalPath, timestamp),
            [prefixSiblingPath] = new RootFileEnumerationEntry(prefixSiblingPath, timestamp)
        };

        Lr2FolderFileCandidateSnapshot filtered =
            Lr2FolderFileDiscoveryService.ExcludeAppManagedOutputCandidates(
                [managedPath, externalOutputPath, externalPath, prefixSiblingPath],
                entries,
                [managedDirectory],
                discoveryComplete: true,
                out int excludedCount);

        Assert.AreEqual(1, excludedCount);
        CollectionAssert.DoesNotContain(filtered.Paths.ToList(), Path.GetFullPath(managedPath));
        CollectionAssert.Contains(filtered.Paths.ToList(), Path.GetFullPath(externalOutputPath));
        CollectionAssert.Contains(filtered.Paths.ToList(), Path.GetFullPath(externalPath));
        CollectionAssert.Contains(filtered.Paths.ToList(), Path.GetFullPath(prefixSiblingPath));
        Assert.IsTrue(filtered.DiscoveryComplete);
    }

    [TestMethod]
    public void CreatePruneDirectories_AddsRelativeBuiltinScopeOnlyWhenBuiltinSourceExists()
    {
        using TestDirectoryScope scope = TestDirectoryScope.Create();
        string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
        string builtinSource = Path.Combine(scope.DirectoryPath, "LR2", "LR2files", "CustomFolder");

        List<string> withBuiltin = Lr2FolderFileDiscoveryService.CreatePruneDirectories(
            [bmsRoot],
            null,
            null,
            [builtinSource]);
        List<string> withoutBuiltin = Lr2FolderFileDiscoveryService.CreatePruneDirectories(
            [bmsRoot],
            null,
            null,
            []);

        CollectionAssert.Contains(withBuiltin, @"LR2files\CustomFolder");
        CollectionAssert.DoesNotContain(withoutBuiltin, @"LR2files\CustomFolder");
    }

    private sealed class TestDirectoryScope : IDisposable
    {
        private TestDirectoryScope(string directoryPath)
        {
            DirectoryPath = directoryPath;
            Directory.CreateDirectory(directoryPath);
        }

        public string DirectoryPath { get; }

        public static TestDirectoryScope Create()
        {
            return new TestDirectoryScope(Path.Combine(Path.GetTempPath(), "BMS_TEST_" + Guid.NewGuid().ToString("N")));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
