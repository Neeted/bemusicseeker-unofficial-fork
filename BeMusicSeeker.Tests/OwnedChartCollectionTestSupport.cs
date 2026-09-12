using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

internal static class OwnedChartCollectionTestSupport
{
    internal static void AssertChartSnapshotParity(IReadOnlyList<ChartFile> expected, IReadOnlyList<ChartFile> actual)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.AreEqual(expected[i].Kind, actual[i].Kind);
            Assert.AreEqual(expected[i].Path, actual[i].Path);
            Assert.AreEqual(expected[i].Md5, actual[i].Md5);
            Assert.AreEqual(expected[i].Sha256, actual[i].Sha256);
            Assert.AreSame(expected[i].GetBmsStorageOwner(), actual[i].GetBmsStorageOwner());
            Assert.AreSame(expected[i].GetBmsonStorageOwner(), actual[i].GetBmsonStorageOwner());
        }
    }

    internal static List<ChartFile> InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(BMSLibrary library)
    {
        return library.CreateOwnedChartInfoFullBackfillTargetSnapshotForDiagnostics();
    }

    internal static InstallDestinationOverlayChartRefSnapshot InvokeCreateInstallDestinationOverlayChartRefSnapshot(BMSLibrary library)
    {
        return library.CreateInstallDestinationOverlayChartRefSnapshotForDiagnostics();
    }

    internal static InstalledChartLookupIndexSnapshot InvokeCreateInstalledChartLookupSnapshot(BMSLibrary library)
    {
        return library.CreateInstalledChartLookupSnapshotForDiagnostics();
    }

    internal static IPrimaryHashLookup InvokeCreateInstalledChartKeySnapshotExcludingCharts(BMSLibrary library, IEnumerable<ChartFile> excluded)
    {
        return library.CreateInstalledChartKeySnapshotExcludingChartsForDiagnostics(excluded);
    }

    internal static int GetInstallDestinationRuntimeStateCount(BMSLibrary library)
    {
        return library.CreateInstallDestinationOverlayChartRefSnapshotForDiagnostics().ChartCount;
    }

    internal static bool IsInstalledChartLookupIndexInitialized(BMSLibrary library)
    {
        return library.IsInstalledChartLookupIndexInitializedForDiagnostics();
    }

    internal static bool IsInstalledPrimaryHashLookupInitialized(BMSLibrary library)
    {
        return library.IsInstalledPrimaryHashLookupInitializedForDiagnostics();
    }

    internal static void InvokeApplyLibraryMutationDelta(BMSLibrary library, LibraryMutationDelta delta)
    {
        library.ApplyLibraryMutationDelta(delta);
    }

    internal static void ApplyInstallDestinationChange(BMSLibrary library, BMSFile bmsFile, string installDestination)
    {
        var delta = new LibraryMutationDelta();
        delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
        {
            Chart = ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false, includeResourceReferences: false),
            NewInstallDestination = installDestination
        });
        InvokeApplyLibraryMutationDelta(library, delta);
    }

    internal static void InvokeApplyInstalledChartStorageTargets(BMSLibrary library, ChartStorageTargetSet addedTargets)
    {
        library.ApplyInstalledChartStorageTargets(addedTargets, "install_package");
    }

    internal static ChartInfoInlineBuildResult InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
        BMSLibrary library,
        string reason,
        IEnumerable<ChartFile> charts)
    {
        using LibraryFileMutationLease mutationLease = library.TryBeginLibraryFileMutation(reason);
        Assert.IsNotNull(mutationLease);
        return library.BuildAndPersistInlineChartInfoForInstalledCharts(
            reason,
            charts);
    }

    internal static void SetLibraryFilesWithoutNotification(BMSLibrary library, IEnumerable<BMSFile> files)
    {
        int previousVersion = library.CatalogStorageRowsVersion.BmsRowsVersion;
        using var published = new ManualResetEventSlim(false);
        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(BMSLibrary.BMSFiles))
            {
                published.Set();
            }
        };
        library.PropertyChanged += handler;
        try
        {
            library.BMSFiles = [.. files ?? []];
            if (library.CatalogStorageRowsVersion.BmsRowsVersion != previousVersion)
            {
                Assert.IsTrue(
                    published.Wait(TimeSpan.FromSeconds(5)),
                    "BMSFiles setup publication did not complete before the test subscribed to mutation notifications.");
            }
        }
        finally
        {
            library.PropertyChanged -= handler;
        }
    }

    internal static void SetLibraryBmsonSongsWithoutNotification(BMSLibrary library, IEnumerable<LR2SongDBExtended.bmson_song> songs)
    {
        int previousVersion = library.CatalogStorageRowsVersion.BmsonRowsVersion;
        using var published = new ManualResetEventSlim(false);
        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(BMSLibrary.BmsonSongs))
            {
                published.Set();
            }
        };
        library.PropertyChanged += handler;
        try
        {
            library.BmsonSongs = [.. songs ?? []];
            if (library.CatalogStorageRowsVersion.BmsonRowsVersion != previousVersion)
            {
                Assert.IsTrue(
                    published.Wait(TimeSpan.FromSeconds(5)),
                    "BmsonSongs setup publication did not complete before the test subscribed to mutation notifications.");
            }
        }
        finally
        {
            library.PropertyChanged -= handler;
        }
    }

    internal static void SetDuplicateChartGroupsWithoutNotification(BMSLibrary library, IEnumerable<DuplicateGroup> groups)
    {
        library.DuplicateChartGroups = groups.ToList();
    }

    internal static void EnsureCurrentResourceHealthIndex(BMSLibrary library)
    {
        _ = library.GetResourceHealthIndexSnapshotForView("test_resource_health");
    }

    internal static bool HasNoCurrentResourceHealthIndex(BMSLibrary library)
    {
        return library.TryGetCurrentResourceHealthIndexSnapshotForView() == ResourceHealthIndexSnapshot.Empty;
    }

    internal static string ResolveExistingDataFixtureDirectory()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo current = new(startPath);
            for (int depth = 0; current != null && depth < 10; depth++, current = current.Parent)
            {
                string fixtureDirectory = Path.Combine(current.FullName, "devdocs", "acceptance", "net10-existing-data");
                if (File.Exists(Path.Combine(fixtureDirectory, "fixture.bms")))
                {
                    return fixtureDirectory;
                }
            }
        }

        throw new InvalidOperationException("The tracked net10-existing-data fixture directory could not be located.");
    }

    internal static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_OwnedChartCollection_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    internal static TestableBmsFile CreateFile(string? hash, string? path, string? sha256 = null)
    {
        var file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(hash);
        file.SetSha256(sha256);
        return file;
    }

    internal static LR2SongDBExtended.bmson_song CreateBmsonSong(string? path, string? md5)
    {
        return new LR2SongDBExtended.bmson_song
        {
            path = path,
            md5 = md5
        };
    }

    internal sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string? value)
        {
            hash = value;
        }

        public void SetSha256(string? value)
        {
            sha256 = value;
        }

        public void SetTitle(string? value)
        {
            Title = value;
        }

        public void SetArtist(string? value)
        {
            Artist = value;
        }
    }

    internal sealed class TestFileMutationService : IFileMutationService
    {
        internal Action<string>? BeforeDirectoryDelete { get; set; }
        /// <summary>Injects a local file-delete failure before modifying the test file.</summary>
        internal Action<string>? BeforeFileDelete { get; set; }
        /// <summary>Attempted directory operations, including calls that subsequently fail.</summary>
        internal List<string> DirectoryDeletePaths { get; } = [];
        /// <summary>Requested recycle policies, without accessing the machine's recycle bin.</summary>
        internal List<RecycleOption> DirectoryRecycleOptions { get; } = [];
        internal int FileDeleteCalls { get; private set; }
        internal int DirectoryDeleteCalls { get; private set; }
        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string destinationDirectory = Path.GetDirectoryName(destinationPath)!;
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            if (File.Exists(destinationPath))
            {
                if (!overwrite)
                {
                    throw new IOException("Destination file already exists.");
                }
                File.Delete(destinationPath);
            }
            File.Move(sourcePath, destinationPath);
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string destinationParent = Path.GetDirectoryName(destinationPath)!;
            if (!string.IsNullOrWhiteSpace(destinationParent))
            {
                Directory.CreateDirectory(destinationParent);
            }
            if (Directory.Exists(destinationPath))
            {
                if (!overwrite)
                {
                    throw new IOException("Destination directory already exists.");
                }
                Directory.Delete(destinationPath, recursive: true);
            }
            Directory.Move(sourcePath, destinationPath);
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            File.Copy(sourcePath, destinationPath, overwrite);
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            CopyDirectoryTree(sourcePath, destinationPath, overwrite);
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            FileDeleteCalls++;
            BeforeFileDelete?.Invoke(filePath);
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DirectoryDeleteCalls++;
            DirectoryDeletePaths.Add(directoryPath);
            DirectoryRecycleOptions.Add(recycleOption);
            BeforeDirectoryDelete?.Invoke(directoryPath);
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }

        private static void CopyDirectoryTree(string sourcePath, string destinationPath, bool overwrite)
        {
            Directory.CreateDirectory(destinationPath);
            foreach (string directoryPath in Directory.GetDirectories(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directoryPath.Replace(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase));
            }
            foreach (string filePath in Directory.GetFiles(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                string destinationFilePath = filePath.Replace(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
                File.Copy(filePath, destinationFilePath, overwrite);
            }
        }
    }

}
