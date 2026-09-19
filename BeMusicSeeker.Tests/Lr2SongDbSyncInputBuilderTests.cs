#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncInputBuilderTests
{
    private enum EnumerationCategory
    {
        Lr2Folder,
        TextMetadata,
        Directories
    }

    [TestMethod]
    public void CreateNoScanInput_EnumeratesThenExcludesManagedScopeAndOverlaysPreparedFile()
    {
        using var scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "Library");
        string outputBaseDirectory = Path.Combine(scope.DirectoryPath, "Output");
        string managedDirectory = Path.Combine(outputBaseDirectory, "Managed");
        string externalDirectory = Path.Combine(scope.DirectoryPath, "External");
        string preparedPath = Path.Combine(managedDirectory, "0000.lr2folder");
        string stalePath = Path.Combine(managedDirectory, "stale.lr2folder");
        string externalPath = Path.Combine(externalDirectory, "external.lr2folder");
        string managedFolderInfoPath = Path.Combine(managedDirectory, "folderinfo.txt");
        string externalFolderInfoPath = Path.Combine(externalDirectory, "folderinfo.txt");

        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(managedDirectory);
        Directory.CreateDirectory(externalDirectory);
        File.WriteAllText(preparedPath, "#TITLE Prepared");
        File.WriteAllText(stalePath, "#TITLE Stale");
        File.WriteAllText(externalPath, "#TITLE External");
        File.WriteAllText(managedFolderInfoPath, "#TITLE Managed");
        File.WriteAllText(externalFolderInfoPath, "#TITLE External");

        PlaylistPersistenceRepository.EnsureSchema(scope.SongDbPath);
        using (var setup = new LR2SongDBExtended(scope.SongDbPath))
        {
            setup.InsertOrReplace(new BMSTable
            {
                playlist_id = 9601,
                name = "Managed playlist",
                symbol = "M",
                Output_dir = "Managed"
            }, typeof(LR2SongDBExtended.playlist));
        }

        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            LR2RootPath = string.Empty,
            LR2CustomFolderOutputBaseDir = outputBaseDirectory,
            LR2CustomFolderAdditionalOutputBaseDirs = [],
            LR2CustomFolderOutputBaseDirRootType = string.Empty
        };
        var library = new TestBmsLibrary(
            scope.SongDbPath,
            () => null,
            null,
            "builder_no_scan",
            () => options);
        Lr2SongDbSyncAppManagedOutputScope appManagedScope =
            library.Lr2Synchronization.CreateLr2SongDbSyncAppManagedOutputScope();

        string managedDirectoryKey = Lr2FolderPath.NormalizeDirectoryPath(managedDirectory);
        Assert.AreEqual(1, appManagedScope.Directories.Count);
        Assert.AreEqual(managedDirectory, appManagedScope.Directories.Single(), ignoreCase: true);

        var enumerator = new DeterministicRootFileEnumerator(
            new Dictionary<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>>
            {
                [EnumerationCategory.Lr2Folder] =
                [
                    new RootFileEnumerationEntry(preparedPath),
                    new RootFileEnumerationEntry(stalePath),
                    new RootFileEnumerationEntry(externalPath)
                ],
                [EnumerationCategory.TextMetadata] =
                [
                    new RootFileEnumerationEntry(managedFolderInfoPath),
                    new RootFileEnumerationEntry(externalFolderInfoPath)
                ],
                [EnumerationCategory.Directories] =
                [
                    RootFileEnumerationEntry.FromDirectoryInfo(rootDirectory),
                    RootFileEnumerationEntry.FromDirectoryInfo(managedDirectory),
                    RootFileEnumerationEntry.FromDirectoryInfo(externalDirectory)
                ]
            });
        var preparedSurface = new Lr2SongDbSyncPreparedDataSurface(
            [managedDirectory],
            [preparedPath],
            CreateFileEntryMap(preparedPath),
            CreateDirectoryEntryMap(rootDirectory, managedDirectory, externalDirectory),
            [managedFolderInfoPath, externalFolderInfoPath],
            CreateFileEntryMap(managedFolderInfoPath, externalFolderInfoPath),
            [managedDirectory, externalDirectory],
            discoveryComplete: true);

        var builder = new Lr2SongDbSyncInputBuilder(
            _ => { },
            _ => { },
            new EverythingNative(TestBmsFactory.MissingEverythingBridge),
            enumerator);
        Lr2SongDbSyncInput input = builder.Create(
            new Lr2SongDbSyncInputRowSnapshot([], [], 1, 1, 1),
            new Lr2SongDbSyncInputRootSnapshot(
                DateTime.UtcNow,
                [rootDirectory],
                [rootDirectory, managedDirectory, externalDirectory],
                string.Empty),
            new Lr2SongDbSyncInputSettingsSnapshot(
                new Lr2BuiltinCustomFolderSettings(0, 24, false),
                [],
                outputBaseDirectory,
                [],
                string.Empty,
                []),
            new Lr2SongDbSyncScanSurfaceSelection(null, "no_scan_surface"),
            new Lr2SongDbSyncPreparedSurfaceSelection(preparedSurface, preparedSurface, 0, false),
            appManagedScope,
            Stopwatch.StartNew(),
            Stopwatch.StartNew(),
            Stopwatch.StartNew(),
            Stopwatch.StartNew(),
            Stopwatch.StartNew(),
            Stopwatch.StartNew());

        CollectionAssert.AreEquivalent(
            new[] { preparedPath, externalPath },
            input.Lr2FolderFilePaths.ToArray());
        Assert.IsTrue(input.Lr2FolderFileEntries.ContainsKey(preparedPath));
        Assert.IsTrue(input.Lr2FolderFileEntries.ContainsKey(externalPath));
        Assert.IsFalse(input.Lr2FolderFileEntries.ContainsKey(stalePath));
        Assert.IsTrue(input.Lr2FolderFileDiscoveryComplete);
        CollectionAssert.Contains(input.FolderInfoFilePaths.ToArray(), managedFolderInfoPath);
        CollectionAssert.Contains(input.TextFileDirectories.ToArray(), managedDirectoryKey);
        Assert.IsTrue(input.DirectoryEntries.ContainsKey(managedDirectoryKey));
        CollectionAssert.AreEquivalent(
            new[]
            {
                EnumerationCategory.Lr2Folder,
                EnumerationCategory.TextMetadata,
                EnumerationCategory.Directories
            },
            enumerator.RequestedCategories.ToArray());
    }

    private sealed class DeterministicRootFileEnumerator : IRootFileEnumerator
    {
        private readonly IReadOnlyDictionary<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>> entriesByCategory;

        internal DeterministicRootFileEnumerator(
            IReadOnlyDictionary<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>> entriesByCategory)
        {
            var snapshot = new Dictionary<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>>();
            foreach (KeyValuePair<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>> pair in entriesByCategory
                ?? new Dictionary<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>>())
            {
                snapshot[pair.Key] = [.. (pair.Value ?? [])];
            }

            this.entriesByCategory = snapshot;
        }

        internal List<EnumerationCategory> RequestedCategories { get; } = [];

        public RootFileEnumerationResult EnumerateFiles(
            IEnumerable<string> rootDirectories,
            IEnumerable<RootFileEnumerationGroup> groups,
            bool verboseLog = false)
        {
            var result = new RootFileEnumerationResult
            {
                Success = true,
                IsComplete = true,
                BackendName = "deterministic-test"
            };
            foreach (RootFileEnumerationGroup group in groups ?? [])
            {
                EnumerationCategory category = Classify(group);
                RequestedCategories.Add(category);
                result.InitializeGroup(group.Name);
                if (!entriesByCategory.TryGetValue(category, out IReadOnlyList<RootFileEnumerationEntry> entries))
                {
                    continue;
                }

                foreach (RootFileEnumerationEntry entry in entries)
                {
                    result.AddEntry(group.Name, entry);
                }
            }

            return result;
        }

        private static EnumerationCategory Classify(RootFileEnumerationGroup group)
        {
            if (group.IncludeDirectories)
            {
                return EnumerationCategory.Directories;
            }

            if (group.Extensions.Any(extension => string.Equals(extension, ".lr2folder", StringComparison.OrdinalIgnoreCase)))
            {
                return EnumerationCategory.Lr2Folder;
            }

            if (group.Extensions.Length > 0)
            {
                return EnumerationCategory.TextMetadata;
            }

            throw new InvalidOperationException("Unsupported deterministic enumeration group.");
        }
    }
}
