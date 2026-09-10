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

using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OwnedChartCollectionLibraryMutationTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoveLibraryCharts_PublishesOneResourceGenerationForConfirmedFoldersOnly(bool recycle)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string[] folders = [Path.Combine(root, "A"), Path.Combine(root, "B"), Path.Combine(root, "C")];
            var files = new List<TestableBmsFile>();
            for (int i = 0; i < folders.Length; i++)
            {
                Directory.CreateDirectory(folders[i]);
                string path = Path.Combine(folders[i], "chart.bms");
                File.WriteAllText(path, "#PLAYER 1\r\n#TITLE Removal\r\n#BPM 120\r\n");
                File.WriteAllText(Path.Combine(folders[i], "shared.wav"), "scan-only resource");
                files.Add(CreateFile(new string((char)('a' + i), 32), path));
            }
            var deletionFailure = new IOException("B deletion failed before changing files");
            var filesystem = new TestFileMutationService
            {
                BeforeDirectoryDelete = path =>
                {
                    if (path == folders[1])
                    {
                        throw deletionFailure;
                    }
                }
            };
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem,
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => null),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
            {
                BMSFiles = files,
                BmsonSongs = []
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                foreach (TestableBmsFile file in files)
                {
                    db.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                }
            }
            uint shared = ChartResourceKeyHash.GetLookupHash("shared");
            LibraryResourceIndexOwner owner = LibraryResourceIndexTestSupport.GetOwner(library);
            owner.Replace(LibraryResourceIndex.CreateFromNativeCanonicalArrays(
                folders,
                folders.Select(_ => new[] { shared }).ToArray(),
                [[], [], []], [[], [], []],
                folders.Select(_ => new[] { shared }).ToArray(),
                [[], [], []], [[], [], []],
                new Dictionary<uint, string[]> { [shared] = folders.ToArray() }, [], []));
            LibraryResourceIndexSnapshot before = owner.CaptureSnapshot();
            var entryCopies = new List<int>();
            before.DirectoryLookupCache.EntriesRootCopiedObserver = count => entryCopies.Add(count);

            LibraryChartRemovalOutcome result = library.RemoveLibraryCharts(
                files.Select(LibraryChartRef.FromBmsFile), recycle, folders);

            Assert.AreEqual(2, result.ConfirmedChartCount);
            Assert.IsTrue(result.CatalogDurable);
            Assert.AreSame(deletionFailure, result.Targets.Single(target => target.Path == files[1].path).Failure);
            Assert.IsFalse(Directory.Exists(folders[0]));
            Assert.IsTrue(Directory.Exists(folders[1]));
            Assert.IsFalse(Directory.Exists(folders[2]));
            Assert.IsTrue(File.Exists(Path.Combine(folders[1], "shared.wav")));
            Assert.IsTrue(filesystem.DirectoryRecycleOptions.All(option => option ==
                (recycle ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently)));
            LibraryResourceIndexSnapshot after = owner.CaptureSnapshot();
            Assert.AreEqual(before.Generation + 1, after.Generation);
            CollectionAssert.AreEqual(new[] { 3 }, entryCopies);
            CollectionAssert.AreEqual(new[] { folders[1] }, after.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(shared).ToArray());
            CollectionAssert.AreEqual(folders, before.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(shared).ToArray());
            using var readback = new LR2SongDBExtended(songDbPath);
            CollectionAssert.AreEqual(new[] { files[1].path }, readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoveLibraryCharts_CatalogFailureReturnsAfterConfirmedFilesystemDeletion(bool afterCommit)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string folder = Path.Combine(root, "Pack");
            Directory.CreateDirectory(folder);
            string chartPath = Path.Combine(folder, "delete.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            var file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            string lr2Root = Path.Combine(root, "LR2");
            LR2Config config = BmsPlaylistTestSupport.CreateLr2Config(lr2Root, root);
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, () => config, null, filesystem, null,
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = afterCommit, LR2RootPath = lr2Root })
            {
                BMSFiles = [file],
                BmsonSongs = []
            };
            string folderRowPath = Lr2FolderPath.ToFolderPath(folder);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                db.InsertOrReplace(new LR2SongDB.folder { path = folderRowPath, title = "Pack", type = 1 }, typeof(LR2SongDB.folder));
                db.Execute(afterCommit
                    ? "CREATE TRIGGER fail_delete BEFORE DELETE ON folder BEGIN SELECT RAISE(ABORT, 'required-folder-prune-fault'); END;"
                    : "CREATE TRIGGER fail_delete BEFORE DELETE ON song BEGIN SELECT RAISE(ABORT, 'catalog-delete-fault'); END;");
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts([LibraryChartRef.FromBmsFile(file)], false, []);

            Assert.IsFalse(File.Exists(chartPath));
            Assert.AreEqual(1, filesystem.FileDeleteCalls);
            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.AreEqual(chartPath, outcome.Targets.Single().Path);
            Assert.IsTrue(outcome.CatalogApplyAttempted);
            Assert.AreEqual(afterCommit, outcome.CatalogDurable);
            Assert.AreEqual(afterCommit, outcome.RequiredFinalizationFailed);
            Assert.IsNotNull(outcome.CatalogFailure);
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(afterCommit ? 0 : 1, readback.Table<LR2SongDB.song>().Count());
            Assert.AreEqual(1, readback.Table<LR2SongDB.folder>().Count(row => row.path == folderRowPath));
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_OnlySuccessfulApiTargetsAreConfirmed()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string partialDirectory = Path.Combine(root, "Partial");
            Directory.CreateDirectory(partialDirectory);
            string success = Path.Combine(root, "success.bms");
            string missing = Path.Combine(root, "missing.bms");
            string partial = Path.Combine(partialDirectory, "partial.bms");
            File.WriteAllText(success, "#PLAYER 1");
            File.WriteAllText(partial, "#PLAYER 1");
            var files = new[] { CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", success),
                CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", missing),
                CreateFile("cccccccccccccccccccccccccccccccc", partial) };
            var partialFailure = new IOException("directory changed before failure");
            var filesystem = new TestFileMutationService
            {
                BeforeDirectoryDelete = path =>
            {
                Assert.AreEqual(partialDirectory, path);
                File.Delete(partial);
                throw partialFailure;
            }
            };
            var dialogs = new FileDbReportRecordingDialogs();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, dialogs)
            { BMSFiles = files, BmsonSongs = [] };
            using (var db = new LR2SongDBExtended(songDbPath))
                foreach (var file in files)
                    db.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                files.Select(LibraryChartRef.FromBmsFile), false, [partialDirectory]);

            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.AreEqual(LibraryChartRemovalState.Confirmed, outcome.Targets.Single(target => target.Path == success).State);
            Assert.AreEqual(LibraryChartRemovalState.NotExecuted, outcome.Targets.Single(target => target.Path == missing).State);
            Assert.AreEqual(LibraryChartRemovalState.Unconfirmed, outcome.Targets.Single(target => target.Path == partial).State);
            Assert.AreSame(partialFailure, outcome.Targets.Single(target => target.Path == partial).Failure);
            Assert.IsFalse(File.Exists(success));
            Assert.IsFalse(File.Exists(partial));
            Assert.AreEqual(1, filesystem.FileDeleteCalls);
            Assert.AreEqual(1, filesystem.DirectoryDeleteCalls);
            Assert.AreEqual(0, dialogs.ModelMessages);
            using var readback = new LR2SongDBExtended(songDbPath);
            CollectionAssert.AreEquivalent(new[] { missing, partial }, readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
        });
    }

    /// <summary>
    /// R2: an approved ancestor may only recurse after earlier descendants
    /// succeeded. Failed/missing descendants retain their rows; independent
    /// folders and the ancestor's own selected chart continue normally.
    /// </summary>
    [DataTestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    [DataRow(3, false)]
    [DataRow(3, true)]
    public void RemoveLibraryCharts_ParentDeletionDependsOnObservedChildResult(int childState, bool recycle)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string parent = Path.Combine(root, "Song");
            string child = Path.Combine(parent, "Child");
            string independent = Path.Combine(root, "SongOther");
            string sibling = Path.Combine(parent, "Sibling");
            Directory.CreateDirectory(sibling);
            Directory.CreateDirectory(child);
            Directory.CreateDirectory(independent);
            string parentChartPath = Path.Combine(parent, "parent.bms");
            string childChartPath = Path.Combine(child, "child.bmson");
            string independentChartPath = Path.Combine(independent, "independent.bms");
            string siblingChartPath = Path.Combine(sibling, "sibling.bms");
            string parentResource = Path.Combine(parent, "parent.wav");
            string childResource = Path.Combine(child, "child.wav");
            File.WriteAllText(parentChartPath, "#PLAYER 1");
            File.WriteAllText(childChartPath, "{}");
            File.WriteAllText(independentChartPath, "#PLAYER 1");
            File.WriteAllText(siblingChartPath, "#PLAYER 1");
            File.WriteAllText(parentResource, "parent resource");
            File.WriteAllText(childResource, "child resource");
            var parentChart = CreateFile(new string('a', 32), parentChartPath);
            var independentChart = CreateFile(new string('c', 32), independentChartPath);
            var siblingChart = CreateFile(new string('d', 32), siblingChartPath);
            var childChart = new LR2SongDBExtended.bmson_song
            {
                path = childChartPath,
                folder = child,
                md5 = new string('b', 32),
                sha256 = new string('b', 64)
            };
            var childFailure = new IOException("selected child deletion failed");
            var filesystem = new TestFileMutationService
            {
                BeforeDirectoryDelete = path =>
                {
                    if (childState == 1 && path == child) throw childFailure;
                },
                BeforeFileDelete = path =>
                {
                    if (childState == 3 && path == childChartPath) throw childFailure;
                }
            };
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            {
                BMSFiles = [parentChart, independentChart, siblingChart],
                BmsonSongs = [childChart]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(parentChart.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                db.InsertOrReplace(independentChart.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                db.InsertOrReplace(siblingChart.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                db.InsertOrReplace(childChart, typeof(LR2SongDBExtended.bmson_song));
            }
            if (childState == 2) Directory.Delete(child, recursive: true);
            LibraryChartRef[] selected = [LibraryChartRef.FromBmsFile(parentChart),
                LibraryChartRef.FromBmsonSong(childChart), LibraryChartRef.FromBmsFile(independentChart),
                LibraryChartRef.FromBmsFile(siblingChart)];
            List<string> approvedFolders = library.GetLibraryWholeFolderDeleteConfirmationPaths(selected);
            CollectionAssert.AreEquivalent(new[] { parent, child, independent, sibling }, approvedFolders);
            if (childState == 3) approvedFolders.Remove(child); // User chose chart-only deletion here.

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(selected, recycle, approvedFolders);

            Assert.IsNotNull(outcome);
            Assert.IsTrue(outcome.CatalogDurable);
            Assert.IsNull(outcome.CatalogFailure);
            Assert.AreEqual(childState == 0 ? 4 : 3, outcome.ConfirmedChartCount);
            Assert.AreEqual(childState != 0, outcome.HasError);
            Assert.IsFalse(File.Exists(parentChartPath));
            Assert.IsFalse(Directory.Exists(independent));
            Assert.IsFalse(Directory.Exists(sibling));
            Assert.AreEqual(childState != 0, Directory.Exists(parent));
            Assert.AreEqual(childState != 0, File.Exists(parentResource));
            Assert.AreEqual(childState == 1 || childState == 3, File.Exists(childChartPath));
            Assert.AreEqual(childState == 1 || childState == 3, File.Exists(childResource));
            Assert.AreEqual(childState == 0, filesystem.DirectoryDeletePaths.Contains(parent));
            Assert.IsTrue(filesystem.DirectoryDeletePaths.Contains(independent));
            Assert.IsTrue(filesystem.DirectoryRecycleOptions.All(option => option ==
                (recycle ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently)));
            LibraryChartRemovalTarget childResult = outcome.Targets.Single(target => target.Path == childChartPath);
            Assert.AreEqual(childState == 0 ? LibraryChartRemovalState.Confirmed
                : childState == 2 ? LibraryChartRemovalState.NotExecuted : LibraryChartRemovalState.Unconfirmed, childResult.State);
            if (childState == 1 || childState == 3) Assert.AreSame(childFailure, childResult.Failure);
            Assert.AreEqual(0, library.BMSFiles.Count);
            Assert.AreEqual(childState == 0 ? 0 : 1, library.BmsonSongs.Count);
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
            string[] retained = readback.Table<LR2SongDBExtended.bmson_song>().Select(row => row.path).ToArray();
            CollectionAssert.AreEqual(childState == 0 ? Array.Empty<string>() : new[] { childChartPath }, retained);
            using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation("deletion_completion_probe");
            Assert.IsNotNull(probe);
        });
    }

    /// <summary>R2: an unselected descendant prevents whole-folder deletion, not deletion of selected files.</summary>
    [TestMethod]
    public void RemoveLibraryCharts_UnselectedDescendantKeepsResourcesAndRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            string child = Path.Combine(folder, "Child");
            Directory.CreateDirectory(child);
            var selected = CreateFile(new string('a', 32), Path.Combine(folder, "selected.bms"));
            var kept = CreateFile(new string('b', 32), Path.Combine(child, "kept.bms"));
            File.WriteAllText(selected.path, "#PLAYER 1");
            File.WriteAllText(kept.path, "#PLAYER 1");
            string resource = Path.Combine(folder, "sound.wav");
            File.WriteAllText(resource, "keep");
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            { BMSFiles = [selected, kept], BmsonSongs = [] };
            using (var db = new LR2SongDBExtended(songDbPath))
                foreach (var file in new[] { selected, kept })
                    db.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
            Assert.AreEqual(0, library.GetLibraryWholeFolderDeleteConfirmationPaths([LibraryChartRef.FromBmsFile(selected)]).Count);

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts([LibraryChartRef.FromBmsFile(selected)], false, []);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.AreEqual(0, filesystem.DirectoryDeleteCalls);
            Assert.IsFalse(File.Exists(selected.path));
            Assert.IsTrue(File.Exists(kept.path));
            Assert.AreEqual("keep", File.ReadAllText(resource));
            Assert.AreSame(kept, library.BMSFiles.Single());
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(kept.path, readback.Table<LR2SongDB.song>().Single().path);
        });
    }

    /// <summary>Replaces old service-only exact-path and noncanonical-instance resolution tests.</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RemoveLibraryCharts_ResolvesSelectionThroughProductionOwner(bool pathOnly)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "chart.bms");
            File.WriteAllText(path, "#PLAYER 1");
            var canonical = CreateFile(new string('a', 32), path);
            LibraryChartRef selected = pathOnly
                ? LibraryChartRef.FromPath(LibraryChartKind.Bms, path, canonical.hash, canonical.sha256)
                : LibraryChartRef.FromBmsFile(CreateFile(canonical.hash, Path.Combine(folder, ".", "chart.bms")));
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            { BMSFiles = [canonical], BmsonSongs = [] };
            using (var db = new LR2SongDBExtended(songDbPath))
                db.InsertOrReplace(canonical.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts([selected], pathOnly, [folder]);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.AreEqual(path, outcome.Targets.Single().Path);
            Assert.IsFalse(Directory.Exists(folder));
            Assert.AreEqual(pathOnly ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently,
                filesystem.DirectoryRecycleOptions.Single());
            Assert.AreEqual(0, library.BMSFiles.Count);
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
        });
    }

    /// <summary>Noncatalog, same-hash/different-path, and no-longer-pathful selections never delete files.</summary>
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void RemoveLibraryCharts_UnresolvedSelectionKeepsFilesystemAndDatabase(int selectionKind)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            string catalogPath = Path.Combine(folder, "catalog.bms");
            string selectedPath = selectionKind == 2 ? catalogPath : Path.Combine(folder, "outside-catalog.bms");
            File.WriteAllText(catalogPath, "#PLAYER 1");
            File.WriteAllText(selectedPath, "#PLAYER 1");
            var canonical = CreateFile(new string('a', 32), catalogPath);
            LibraryChartRef selected = selectionKind == 2
                ? LibraryChartRef.FromChartFile(ChartFileProjection.FromBmsFile(canonical))
                : LibraryChartRef.FromPath(LibraryChartKind.Bms, selectedPath,
                    selectionKind == 1 ? canonical.hash : null, null);
            using (var db = new LR2SongDBExtended(songDbPath))
                db.InsertOrReplace(canonical.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
            if (selectionKind == 2) canonical.path = null;
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            { BMSFiles = [canonical], BmsonSongs = [] };

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts([selected], false, [folder]);

            Assert.AreEqual(0, outcome.ConfirmedChartCount);
            Assert.IsTrue(outcome.HasError);
            Assert.AreEqual(LibraryChartRemovalState.Unresolved, outcome.Targets.Single().State);
            Assert.AreEqual(selectedPath, outcome.Targets.Single().Path);
            Assert.AreEqual(0, filesystem.FileDeleteCalls);
            Assert.AreEqual(0, filesystem.DirectoryDeleteCalls);
            Assert.IsTrue(File.Exists(catalogPath));
            Assert.IsTrue(File.Exists(selectedPath));
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(catalogPath, readback.Table<LR2SongDB.song>().Single().path);
        });
    }

    /// <summary>Replaces the legacy helper's prompt test with real preflight confirmation and deletion.</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoveLibraryCharts_LastChartHonorsWholeFolderConfirmation(bool deleteWholeFolder)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            var chart = CreateFile(new string('a', 32), Path.Combine(folder, "chart.bms"));
            File.WriteAllText(chart.path, "#PLAYER 1");
            File.WriteAllText(Path.Combine(folder, "resource.wav"), "remove with the folder");
            var filesystem = new TestFileMutationService();
            var dialogs = new ConfirmingRemovalDialogs(deleteWholeFolder);
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, dialogs)
            { BMSFiles = [chart], BmsonSongs = [] };
            using (var db = new LR2SongDBExtended(songDbPath))
                db.InsertOrReplace(chart.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
            CollectionAssert.AreEqual(new[] { folder }, library.GetLibraryWholeFolderDeleteConfirmationPaths([LibraryChartRef.FromBmsFile(chart)]));

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts([LibraryChartRef.FromBmsFile(chart)], false);

            Assert.AreEqual(1, dialogs.ConfirmationCount);
            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(deleteWholeFolder ? 1 : 0, filesystem.DirectoryDeleteCalls);
            Assert.AreEqual(!deleteWholeFolder, Directory.Exists(folder));
            Assert.AreEqual(!deleteWholeFolder, File.Exists(Path.Combine(folder, "resource.wav")));
            Assert.AreEqual(0, library.BMSFiles.Count);
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
        });
    }

    /// <summary>Whole-folder success clears library/pending destinations only after canonical removal.</summary>
    [TestMethod]
    public void RemoveLibraryCharts_WholeFolderClearsInstallDestinationsForBothFormats()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string folder = Path.Combine(root, "Pack");
            string pendingFolder = Path.Combine(root, "Pending");
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(pendingFolder);
            var bms = CreateFile(new string('a', 32), Path.Combine(folder, "chart.bms"));
            var bmson = new LR2SongDBExtended.bmson_song
            { path = Path.Combine(folder, "chart.bmson"), folder = folder, md5 = new string('b', 32), sha256 = new string('b', 64) };
            var kept = CreateFile(new string('c', 32), Path.Combine(root, "kept.bms"));
            File.WriteAllText(bms.path, "#PLAYER 1");
            File.WriteAllText(bmson.path, "{}");
            File.WriteAllText(kept.path, "#PLAYER 1");
            File.WriteAllText(Path.Combine(folder, "sound.wav"), "resource");
            var pendingBms = CreateFile(new string('d', 32), Path.Combine(pendingFolder, "chart.bms"));
            pendingBms.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "pending estimation warning");
            PackageChartEntry pendingBmsEntry = ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                pendingBms, folder, "Title", "Artist", [Path.Combine(root, "Alternative")]);
            PackageChartEntry pendingBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(
                new LR2SongDBExtended.bmson_song
                { path = Path.Combine(pendingFolder, "chart.bmson"), folder = pendingFolder, md5 = new string('e', 32), sha256 = new string('e', 64) }));
            pendingBmsonEntry.ApplyInstallDestination(folder, "Title", "Artist");
            var pendingPackage = ChartPackage.FromChartEntries([pendingBmsEntry, pendingBmsonEntry]);
            pendingPackage.path = pendingFolder;
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            {
                BMSFiles = [bms, kept],
                BmsonSongs = [bmson],
                ChartPackagesPending = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>([pendingPackage])
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDBExtended.install>();
                db.InsertOrReplace(pendingPackage, typeof(LR2SongDBExtended.install));
                db.InsertOrReplace(bms.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                db.InsertOrReplace(kept.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                db.InsertOrReplace(bmson, typeof(LR2SongDBExtended.bmson_song));
            }
            ApplyInstallDestinationChange(library, kept, folder);

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(bms), LibraryChartRef.FromBmsonSong(bmson)], false, [folder]);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(2, outcome.ConfirmedChartCount);
            Assert.IsFalse(Directory.Exists(folder));
            Assert.AreSame(kept, library.BMSFiles.Single());
            Assert.AreEqual(0, library.BmsonSongs.Count);
            ChartFile keptProjection = library.CreateOwnedChartInfoFullBackfillTargetSnapshotWithInstallDestinationOverlayForDiagnostics().Single();
            Assert.AreEqual(string.Empty, keptProjection.InstallDestination);
            foreach (PackageChartEntry entry in pendingPackage.ChartEntries)
            {
                Assert.AreEqual(string.Empty, entry.Chart.InstallDestination);
                Assert.AreEqual(string.Empty, entry.Chart.InstallDestinationTitle);
                Assert.AreEqual(string.Empty, entry.Chart.InstallDestinationArtist);
                Assert.AreEqual(0, entry.Chart.InstallDestinationSuggestions.Count);
                Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
            }
            Assert.IsNull(pendingBmsonEntry.GetBmsOwnerForTest());
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(kept.path, readback.Table<LR2SongDB.song>().Single().path);
            Assert.AreEqual(0, readback.Table<LR2SongDBExtended.bmson_song>().Count());
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterKeepsOwnedCollectionInitializedAndSynced()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
            var second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Second", "chart.bms"));
            var library = new TestBmsLibrary(songDbPath);
            using var initialBmsFilesNotification = new ManualResetEventSlim(false);
            System.ComponentModel.PropertyChangedEventHandler initialHandler = delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.BMSFiles))
                {
                    initialBmsFilesNotification.Set();
                }
            };
            library.PropertyChanged += initialHandler;
            library.BMSFiles = [first, second];
            library.BmsonSongs = [];
            library.DuplicateChartGroups = [];
            Assert.IsTrue(initialBmsFilesNotification.Wait(TimeSpan.FromSeconds(5)));
            library.PropertyChanged -= initialHandler;
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);
            int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
            int baselineDuplicateInvalidationVersion = library.DuplicateChartGroupsInvalidationVersion;
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int parentFolderVersionChanged = 0;
            int bmsFilesChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSParentFolderListCacheVersion")
                {
                    parentFolderVersionChanged++;
                }
                if (args.PropertyName == nameof(BMSLibrary.BMSFiles))
                {
                    bmsFilesChanged++;
                }
            };
            var delta = new LibraryMutationDelta
            {
                InvalidateInstalledDirectoryIndex = true,
                InvalidateParentFolderCache = true,
                ClearDuplicatedCache = true
            };
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReferenceChart(initialSnapshot[0]));

            InvokeApplyLibraryMutationDelta(library, delta);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

            Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
            Assert.AreEqual(1, parentFolderVersionChanged);
            Assert.AreEqual(0, bmsFilesChanged);
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsTrue(batch.NotifiesBmsFiles);
            Assert.IsFalse(batch.NotifiesBmsonSongs);
            Assert.IsNull(library.DuplicateChartGroups);
            Assert.AreEqual(baselineDuplicateInvalidationVersion + 1, library.DuplicateChartGroupsInvalidationVersion);
            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreSame(second, library.BMSFiles[0]);
            List<ChartFile> afterSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, afterSnapshot.Count);
            Assert.AreSame(second, afterSnapshot[0].GetBmsStorageOwner());
        });
    }

    [TestMethod]
    public void BMSFilesReplacement_InvalidatesOwnedCollectionVersionAndRebuildsOnNextView()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
            var replacement = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Replacement", "chart.bms"));
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [first],
                BmsonSongs = []
            };
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, initialSnapshot.Count);
            library.BMSFiles = [replacement];
            List<ChartFile> rebuiltSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);

            Assert.AreEqual(1, rebuiltSnapshot.Count);
            Assert.AreSame(replacement, rebuiltSnapshot[0].GetBmsStorageOwner());
        });
    }

    [TestMethod]
    public void StorageRowPropertiesExposeReadOnlyViews()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };

            Assert.IsFalse(library.BMSFiles is List<BMSFile>);
            Assert.IsFalse(library.BmsonSongs is List<LR2SongDBExtended.bmson_song>);
            Assert.ThrowsException<NotSupportedException>(() => ((IList<BMSFile>)library.BMSFiles).Add(CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "chart.bms"))));
            Assert.ThrowsException<NotSupportedException>(() => ((IList<LR2SongDBExtended.bmson_song>)library.BmsonSongs).Clear());
            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreEqual(1, library.BmsonSongs.Count);
        });
    }

    [TestMethod]
    public void StorageRowSettersCopyInputLists()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            List<BMSFile> inputBmsFiles = [bmsFile];
            List<LR2SongDBExtended.bmson_song> inputBmsonSongs = [bmsonSong];
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = inputBmsFiles,
                BmsonSongs = inputBmsonSongs
            };
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);

            inputBmsFiles.Clear();
            inputBmsonSongs.Clear();
            List<ChartFile> afterInputMutationSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);

            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreEqual(1, library.BmsonSongs.Count);
            Assert.AreEqual(2, afterInputMutationSnapshot.Count);
            Assert.AreSame(bmsFile, afterInputMutationSnapshot.Single(chart => chart.Kind == ChartFileKind.Bms).GetBmsStorageOwner());
            Assert.AreSame(bmsonSong, afterInputMutationSnapshot.Single(chart => chart.Kind == ChartFileKind.Bmson).GetBmsonStorageOwner());
        });
    }

    [TestMethod]
    public void HasOwnedChartUnderRealPath_UsesOwnedCollectionForBmsAndBmson()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(
                Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };

            Assert.IsTrue(library.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Bms")));
            Assert.IsTrue(library.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Bmson")));
            Assert.IsTrue(library.HasOwnedChartUnderRealPath("C:\\Installed"));
            Assert.IsFalse(library.HasOwnedChartUnderRealPath("C:\\Install"));
            Assert.IsFalse(library.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Missing")));
            Assert.IsFalse(library.HasOwnedChartUnderRealPath(null));
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregistersBmsonStorageRowsInLibraryBoundary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var first = CreateBmsonSong(Path.Combine("C:\\Installed", "First", "chart.bmson"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var second = CreateBmsonSong(Path.Combine("C:\\Installed", "Second", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, []);
            SetLibraryBmsonSongsWithoutNotification(library, [first, second]);
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int bmsonSongsChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.BmsonSongs))
                {
                    bmsonSongsChanged++;
                }
            };
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(first));

            InvokeApplyLibraryMutationDelta(library, delta);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

            Assert.AreEqual(1, library.BmsonSongs.Count);
            Assert.AreSame(second, library.BmsonSongs.Single());
            Assert.AreEqual(0, bmsonSongsChanged);
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsFalse(batch.NotifiesBmsFiles);
            Assert.IsTrue(batch.NotifiesBmsonSongs);
            List<ChartFile> afterSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, afterSnapshot.Count);
            Assert.AreSame(second, afterSnapshot[0].GetBmsonStorageOwner());
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterPathlessStorageRowIsNoOpForOwnedBoundary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var pathless = CreateBmsonSong(null, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var kept = CreateBmsonSong(Path.Combine("C:\\Installed", "Kept", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [],
                BmsonSongs = [pathless, kept]
            };
            Assert.AreEqual(1, InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library).Count);
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(pathless));

            InvokeApplyLibraryMutationDelta(library, delta);
            List<ChartFile> afterSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);

            Assert.AreEqual(2, library.BmsonSongs.Count);
            Assert.IsTrue(library.BmsonSongs.Contains(pathless));
            Assert.IsTrue(library.BmsonSongs.Contains(kept));
            Assert.AreEqual(1, afterSnapshot.Count);
            Assert.AreSame(kept, afterSnapshot[0].GetBmsonStorageOwner());
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_RoutesUnregisterThroughOwnedMutationAndInstalledLookupDelta()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath), "DeleteTarget");
            Directory.CreateDirectory(chartDirectory);
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService())
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [],
                DuplicateChartGroups = []
            };
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, initialSnapshot.Count);
            InstalledChartLookupIndexSnapshot initialLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(bmsFile.hash));

            library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(bmsFile)],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: [chartDirectory]);

            Assert.IsFalse(File.Exists(chartPath));
            Assert.AreEqual(0, library.BMSFiles.Count);
            Assert.IsNull(library.DuplicateChartGroups);
            Assert.AreEqual(0, InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library).Count);
            InstalledChartLookupIndexSnapshot updatedLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsFalse(updatedLookup.ContainsPrimaryHash(bmsFile.hash));
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterAppliesCurrentResourceHealthIndexDelta()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Resource", "chart.bms"));
            bmsFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(bmsFile)
            {
                hash = bmsFile.hash,
                wav_files_defined = 2,
                wav_files_existing = 1
            }, suppressPropertyChanged: true);
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            EnsureCurrentResourceHealthIndex(library);
            Assert.AreEqual(1, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(bmsFile));

            InvokeApplyLibraryMutationDelta(library, delta);

            Assert.AreEqual(0, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
            Assert.IsFalse(HasNoCurrentResourceHealthIndex(library));
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_PathChangeInvalidatesCurrentResourceHealthIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ResourceMutation_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
            string newDirectoryPath = Path.Combine(tempRootPath, "New");
            Directory.CreateDirectory(oldDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            string oldBmsPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newBmsPath = Path.Combine(newDirectoryPath, "chart.bms");
            File.WriteAllText(newBmsPath, "#PLAYER 1");
            try
            {
                var oldSnapshotFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath);
                oldSnapshotFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(oldSnapshotFile)
                {
                    hash = oldSnapshotFile.hash,
                    wav_files_defined = 2,
                    wav_files_existing = 1
                }, suppressPropertyChanged: true);
                TestableBmsFile bmsFile = CreateFile(oldSnapshotFile.hash, newBmsPath);
                bmsFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(bmsFile)
                {
                    hash = bmsFile.hash,
                    wav_files_defined = 2,
                    wav_files_existing = 1
                }, suppressPropertyChanged: true);
                var library = new TestBmsLibrary(songDbPath);
                SetLibraryFilesWithoutNotification(library, [bmsFile]);
                SetLibraryBmsonSongsWithoutNotification(library, []);
                EnsureCurrentResourceHealthIndex(library);
                Assert.AreEqual(1, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
                var delta = new LibraryMutationDelta();
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(bmsFile),
                    OldPath = oldBmsPath,
                    NewPath = newBmsPath
                });

                InvokeApplyLibraryMutationDelta(library, delta);

                Assert.AreEqual(0, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
                Assert.IsTrue(HasNoCurrentResourceHealthIndex(library));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_DispatchesParentFolderOnceAndClearsDuplicateCache()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_OwnedMutationDispatch_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
            string newDirectoryPath = Path.Combine(tempRootPath, "New");
            Directory.CreateDirectory(oldDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            string oldBmsPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newBmsPath = Path.Combine(newDirectoryPath, "chart.bms");
            string oldBmsonPath = Path.Combine(oldDirectoryPath, "chart.bmson");
            string newBmsonPath = Path.Combine(newDirectoryPath, "chart.bmson");
            File.WriteAllText(newBmsPath, "#PLAYER 1");
            File.WriteAllText(newBmsonPath, "{}");
            try
            {
                TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", newBmsPath);
                LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(oldBmsonPath, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                var library = new TestBmsLibrary(songDbPath);
                SetLibraryFilesWithoutNotification(library, [bmsFile]);
                SetLibraryBmsonSongsWithoutNotification(library, [bmsonSong]);
                SetDuplicateChartGroupsWithoutNotification(library, []);
                int baselineOwnedCollectionVersion = library.OwnedChartCollectionVersion;
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
                int ownedCollectionVersionChanged = 0;
                int bmsFilesChanged = 0;
                int bmsonSongsChanged = 0;
                bool bmsonPathAvailableAtNotification = false;
                int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
                int baselineDuplicateInvalidationVersion = library.DuplicateChartGroupsInvalidationVersion;
                int parentFolderVersionChanged = 0;
                library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == "OwnedChartCollectionVersion")
                    {
                        ownedCollectionVersionChanged++;
                    }
                    if (args.PropertyName == "BMSFiles")
                    {
                        bmsFilesChanged++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.BmsonSongs))
                    {
                        bmsonSongsChanged++;
                    }
                    if (args.PropertyName == "OwnedChartCollectionVersion")
                    {
                        bmsonPathAvailableAtNotification = library.BmsonSongs.Any(song => song.path == newBmsonPath);
                    }
                    if (args.PropertyName == "BMSParentFolderListCacheVersion")
                    {
                        parentFolderVersionChanged++;
                    }
                };
                var delta = new LibraryMutationDelta
                {
                    InvalidateParentFolderCache = true,
                    ClearDuplicatedCache = true,
                    NotifyStorageRowPathChanges = true
                };
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(bmsFile),
                    OldPath = oldBmsPath,
                    NewPath = newBmsPath
                });
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsonSong(bmsonSong),
                    OldPath = oldBmsonPath,
                    NewPath = newBmsonPath
                });

                InvokeApplyLibraryMutationDelta(library, delta);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(baselineOwnedCollectionVersion + 1, library.OwnedChartCollectionVersion);
                Assert.AreEqual(1, ownedCollectionVersionChanged);
                Assert.AreEqual(0, bmsFilesChanged);
                Assert.AreEqual(0, bmsonSongsChanged);
                Assert.IsTrue(batch.NotifiesStorageRows);
                Assert.IsTrue(batch.NotifiesBmsFiles);
                Assert.IsTrue(batch.NotifiesBmsonSongs);
                Assert.IsTrue(bmsonPathAvailableAtNotification);
                Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
                Assert.AreEqual(1, parentFolderVersionChanged);
                Assert.IsNull(library.DuplicateChartGroups);
                Assert.AreEqual(baselineDuplicateInvalidationVersion + 1, library.DuplicateChartGroupsInvalidationVersion);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }
    /// <summary>Local confirmation terminal; it never opens a window or inspects translated copy.</summary>
    private sealed class ConfirmingRemovalDialogs(bool deleteWholeFolder) : IBmsLibraryDialogService
    {
        /// <summary>Counts the user's whole-folder decision, not diagnostic text.</summary>
        internal int ConfirmationCount { get; private set; }

        /// <summary>Returns the test's explicit folder decision without opening a dialog.</summary>
        public UiDialogDefaultResult Show(string messageBoxText, string caption, UiDialogButton button,
            UiDialogIcon icon, UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
        {
            if (button == UiDialogButton.YesNo) ConfirmationCount++;
            return deleteWholeFolder ? UiDialogDefaultResult.Yes : UiDialogDefaultResult.No;
        }
    }

}
