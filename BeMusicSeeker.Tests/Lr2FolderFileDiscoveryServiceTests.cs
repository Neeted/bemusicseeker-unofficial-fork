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
        string rootOutputBase = Path.Combine(scope.DirectoryPath, "RootOutput");
        var preparedSurface = new Lr2FullGenerationPreparedDataSurface(
            [outputBase],
            [],
            new Dictionary<string, RootFileEnumerationEntry>(System.StringComparer.OrdinalIgnoreCase),
            discoveryComplete: true);

        List<string> directories = Lr2FolderFileDiscoveryService.CreateDiscoveryDirectoriesForEnumeration(
            [rootDirectory, outputBase, rootOutputBase],
            preparedSurface).ToList();

        CollectionAssert.Contains(directories, rootDirectory);
        CollectionAssert.Contains(directories, rootOutputBase);
        CollectionAssert.DoesNotContain(directories, outputBase);
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
