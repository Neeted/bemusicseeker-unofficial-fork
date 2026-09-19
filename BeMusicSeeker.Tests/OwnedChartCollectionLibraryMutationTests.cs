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
    /// <summary>確認済みフォルダだけを物理削除し、両形式のrecycle指定と一回のリソース世代公開を実DBで確認する。</summary>
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
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => null!),
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
                new Dictionary<uint, string[]> { [shared] = folders.ToArray() }, new Dictionary<uint, string[]>(), new Dictionary<uint, string[]>()));
            LibraryResourceIndexSnapshot before = owner.CaptureSnapshot();
            var entryMutations = new List<string>();
            before.DirectoryLookupCache.EntryStoreMutationObserver = path => entryMutations.Add(path);

            LibraryChartRemovalOutcome result = library.RemoveLibraryCharts(
                files.Select(LibraryChartRef.FromBmsFile), recycle, folders);

            Assert.AreEqual(2, result.ConfirmedChartCount);
            Assert.IsTrue(result.CatalogDurable);
            Assert.IsNotNull(result.SessionReceipt);
            Assert.AreEqual(2, result.SessionReceipt.ResourceDirectoryRemovalCount);
            Assert.AreSame(deletionFailure, result.Targets.Single(target => target.Path == files[1].path).Failure);
            Assert.IsFalse(Directory.Exists(folders[0]));
            Assert.IsTrue(Directory.Exists(folders[1]));
            Assert.IsFalse(Directory.Exists(folders[2]));
            Assert.IsTrue(File.Exists(Path.Combine(folders[1], "shared.wav")));
            Assert.IsTrue(filesystem.DirectoryRecycleOptions.All(option => option ==
                (recycle ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently)));
            LibraryResourceIndexSnapshot after = owner.CaptureSnapshot();
            Assert.AreEqual(before.Generation + 1, after.Generation);
            CollectionAssert.AreEqual(new[] { folders[0], folders[2] }, entryMutations);
            CollectionAssert.AreEqual(new[] { folders[1] }, after.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(shared).ToArray());
            CollectionAssert.AreEqual(folders, before.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(shared).ToArray());
            using var readback = new LR2SongDBExtended(songDbPath);
            CollectionAssert.AreEqual(new[] { files[1].path }, readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
        });
    }

    /// <summary>DB確定前後の失敗境界を分け、物理削除済み結果と未確定カタログの扱いを保持する。</summary>
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
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
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
            uint shared = ChartResourceKeyHash.GetLookupHash("shared");
            LibraryResourceIndexOwner resourceOwner = LibraryResourceIndexTestSupport.GetOwner(library);
            resourceOwner.Replace(LibraryResourceIndex.CreateFromNativeCanonicalArrays(
                [folder],
                [new[] { shared }],
                [[]], [[]],
                [new[] { shared }],
                [[]], [[]],
                new Dictionary<uint, string[]> { [shared] = [folder] },
                new Dictionary<uint, string[]>(),
                new Dictionary<uint, string[]>()));
            LibraryResourceIndexSnapshot resourceBefore = resourceOwner.CaptureSnapshot();

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(file)],
                false,
                [folder]);

            Assert.IsFalse(File.Exists(chartPath));
            Assert.IsFalse(Directory.Exists(folder));
            Assert.AreEqual(0, filesystem.FileDeleteCalls);
            Assert.AreEqual(1, filesystem.DirectoryDeleteCalls);
            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.AreEqual(chartPath, outcome.Targets.Single().Path);
            Assert.IsTrue(outcome.CatalogApplyAttempted);
            Assert.AreEqual(afterCommit, outcome.CatalogDurable);
            Assert.AreEqual(afterCommit, outcome.RequiredFinalizationFailed);
            Assert.IsNotNull(outcome.CatalogFailure);
            Assert.IsNotNull(outcome.SessionReceipt);
            Assert.AreEqual(1, outcome.SessionReceipt.ResourceDirectoryRemovalCount);
            LibraryResourceIndexSnapshot resourceAfter = resourceOwner.CaptureSnapshot();
            Assert.AreEqual(resourceBefore.Generation, resourceAfter.Generation);
            CollectionAssert.AreEqual(
                new[] { folder },
                resourceAfter.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(shared).ToArray());
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
            TestableBmsFile[] files = new[] { CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", success),
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
                foreach (TestableBmsFile? file in files)
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
    /// 承認済み親フォルダは子対象の結果を確認してから再帰削除します。
    /// 実DB・実ファイル境界で、成功・欠落・物理失敗と通常削除を確認します。
    /// 親フォルダ削除は子の実観測結果だけで決まり、欠落・失敗・選択解除を成功へ丸めません。
    /// </summary>
    [DataTestMethod]
    [DataRow(0, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
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
            TestableBmsFile parentChart = CreateFile(new string('a', 32), parentChartPath);
            TestableBmsFile independentChart = CreateFile(new string('c', 32), independentChartPath);
            TestableBmsFile siblingChart = CreateFile(new string('d', 32), siblingChartPath);
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

    /// <summary>未選択の子孫がある場合も、親全体の削除だけを止め、選択済み譜面は削除する。</summary>
    [TestMethod]
    public void RemoveLibraryCharts_UnselectedDescendantKeepsResourcesAndRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            string child = Path.Combine(folder, "Child");
            Directory.CreateDirectory(child);
            TestableBmsFile selected = CreateFile(new string('a', 32), Path.Combine(folder, "selected.bms"));
            TestableBmsFile kept = CreateFile(new string('b', 32), Path.Combine(child, "kept.bms"));
            File.WriteAllText(selected.path, "#PLAYER 1");
            File.WriteAllText(kept.path, "#PLAYER 1");
            string resource = Path.Combine(folder, "sound.wav");
            File.WriteAllText(resource, "keep");
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            { BMSFiles = [selected, kept], BmsonSongs = [] };
            using (var db = new LR2SongDBExtended(songDbPath))
                foreach (TestableBmsFile? file in new[] { selected, kept })
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

    /// <summary>path-only選択と同じexact pathの別instanceを、production ownerへ解決して削除します。</summary>
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
            TestableBmsFile canonical = CreateFile(new string('a', 32), path);
            LibraryChartRef selected = pathOnly
                ? LibraryChartRef.FromPath(LibraryChartKind.Bms, path, canonical.hash, canonical.sha256)
                : LibraryChartRef.FromBmsFile(CreateFile(canonical.hash, path));
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

    /// <summary>大小文字だけが異なる別の完全一致行を同時選択したとき、両方のカタログ対象を削除する。</summary>
    [TestMethod]
    public void RemoveLibraryCharts_CaseOnlyExactRowsAreBothRemoved()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            string lowerPath = Path.Combine(folder, "chart.bms");
            string upperPath = Path.Combine(folder, "CHART.bms");
            File.WriteAllText(lowerPath, "#PLAYER 1");
            TestableBmsFile lower = CreateFile(new string('a', 32), lowerPath);
            TestableBmsFile upper = CreateFile(new string('b', 32), upperPath);
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            {
                BMSFiles = [lower, upper],
                BmsonSongs = []
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(lower.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                db.InsertOrReplace(upper.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(lower), LibraryChartRef.FromBmsFile(upper)],
                false,
                [folder]);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(2, outcome.ConfirmedChartCount);
            CollectionAssert.AreEquivalent(
                new[] { lowerPath, upperPath },
                outcome.Targets.Where(target => target.State == LibraryChartRemovalState.Confirmed)
                    .Select(target => target.Path)
                    .ToArray());
            Assert.AreEqual(1, filesystem.DirectoryDeleteCalls);
            Assert.IsFalse(Directory.Exists(folder));
            Assert.AreEqual(0, library.BMSFiles.Count);
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
        });
    }

    /// <summary>個別削除でも同じ物理ファイルを指す選択済み完全一致行をすべて削除する。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoveLibraryCharts_PhysicalAliasExactRowsAreBothRemovedWithoutWholeFolderDelete(bool useDotAlias)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            string canonicalPath = Path.Combine(folder, "chart.bms");
            string aliasPath = useDotAlias
                ? Path.Combine(folder, ".", "chart.bms")
                : Path.Combine(folder, "CHART.bms");
            File.WriteAllText(canonicalPath, "#PLAYER 1");
            TestableBmsFile canonical = CreateFile(new string('a', 32), canonicalPath);
            TestableBmsFile alias = CreateFile(new string('b', 32), aliasPath);
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            {
                BMSFiles = [canonical, alias],
                BmsonSongs = []
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(canonical.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                db.InsertOrReplace(alias.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(canonical), LibraryChartRef.FromBmsFile(alias)],
                false,
                []);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(2, outcome.ConfirmedChartCount);
            CollectionAssert.AreEquivalent(
                new[] { canonicalPath, aliasPath },
                outcome.Targets.Where(target => target.State == LibraryChartRemovalState.Confirmed)
                    .Select(target => target.Path)
                    .ToArray());
            Assert.AreEqual(1, filesystem.FileDeleteCalls);
            Assert.AreEqual(0, filesystem.DirectoryDeleteCalls);
            Assert.IsFalse(File.Exists(canonicalPath));
            Assert.AreEqual(0, library.BMSFiles.Count);
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
        });
    }

    /// <summary>同じ物理ファイルの別完全一致別名を使った自動再試行を行わない。</summary>
    [TestMethod]
    public void RemoveLibraryCharts_PhysicalAliasDeleteFailureIsNotRetried()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            string canonicalPath = Path.Combine(folder, "chart.bms");
            string aliasPath = Path.Combine(folder, ".", "chart.bms");
            File.WriteAllText(canonicalPath, "#PLAYER 1");
            TestableBmsFile canonical = CreateFile(new string('a', 32), canonicalPath);
            TestableBmsFile alias = CreateFile(new string('b', 32), aliasPath);
            var failure = new IOException("selected physical file deletion failed");
            var filesystem = new TestFileMutationService
            {
                BeforeFileDelete = _ => throw failure
            };
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            {
                BMSFiles = [canonical, alias],
                BmsonSongs = []
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(canonical.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                db.InsertOrReplace(alias.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(canonical), LibraryChartRef.FromBmsFile(alias)],
                false,
                []);

            Assert.IsTrue(outcome.HasError);
            Assert.AreEqual(0, outcome.ConfirmedChartCount);
            Assert.AreEqual(1, filesystem.FileDeleteCalls);
            Assert.IsTrue(File.Exists(canonicalPath));
            Assert.AreEqual(2, library.BMSFiles.Count);
            Assert.IsTrue(outcome.Targets.All(target => target.State == LibraryChartRemovalState.Unconfirmed));
            Assert.IsTrue(outcome.Targets.All(target => ReferenceEquals(failure, target.Failure)));
            using var readback = new LR2SongDBExtended(songDbPath);
            CollectionAssert.AreEquivalent(
                new[] { canonicalPath, aliasPath },
                readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
        });
    }

    /// <summary>子フォルダの大小文字だけが異なる完全一致選択を畳まず、親フォルダも一括削除候補にする。</summary>
    [TestMethod]
    public void GetLibraryWholeFolderDeleteConfirmationPaths_PreservesCaseOnlyNestedSelections()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string parent = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            string child = Path.Combine(parent, "Child");
            Directory.CreateDirectory(child);
            string parentPath = Path.Combine(parent, "parent.bms");
            string lowerPath = Path.Combine(child, "chart.bms");
            string upperPath = Path.Combine(child, "CHART.bms");
            File.WriteAllText(parentPath, "#PLAYER 1");
            File.WriteAllText(lowerPath, "#PLAYER 1");
            TestableBmsFile parentChart = CreateFile(new string('a', 32), parentPath);
            TestableBmsFile lower = CreateFile(new string('b', 32), lowerPath);
            TestableBmsFile upper = CreateFile(new string('c', 32), upperPath);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(),
                new FileDbReportRecordingDialogs())
            {
                BMSFiles = [parentChart, lower, upper],
                BmsonSongs = []
            };

            List<string> confirmationPaths = library.GetLibraryWholeFolderDeleteConfirmationPaths(
                [LibraryChartRef.FromBmsFile(parentChart), LibraryChartRef.FromBmsFile(lower), LibraryChartRef.FromBmsFile(upper)]);

            CollectionAssert.AreEquivalent(new[] { child, parent }, confirmationPaths);
        });
    }

    /// <summary>未登録・同一hashの別path・path喪失・未登録のdot/case表記ではファイルを削除しません。</summary>
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void RemoveLibraryCharts_UnresolvedSelectionKeepsFilesystemAndDatabase(int selectionKind)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            string catalogPath = Path.Combine(folder, "catalog.bms");
            string selectedPath = selectionKind switch
            {
                2 => catalogPath,
                3 => Path.Combine(folder, ".", "catalog.bms"),
                4 => Path.Combine(folder, "CATALOG.bms"),
                _ => Path.Combine(folder, "outside-catalog.bms")
            };
            File.WriteAllText(catalogPath, "#PLAYER 1");
            File.WriteAllText(selectedPath, "#PLAYER 1");
            TestableBmsFile canonical = CreateFile(new string('a', 32), catalogPath);
            LibraryChartRef selected = selectionKind == 2
                ? LibraryChartRef.FromChartFile(ChartFileProjection.FromBmsFile(canonical))
                : LibraryChartRef.FromPath(LibraryChartKind.Bms, selectedPath,
                    selectionKind is 1 or 3 or 4 ? canonical.hash : null, null);
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
            TestableBmsFile chart = CreateFile(new string('a', 32), Path.Combine(folder, "chart.bms"));
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

    [TestMethod]
    public void RemoveLibraryCharts_UnregisterKeepsOwnedCollectionInitializedAndSynced()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string rootPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed");
            string firstDirectoryPath = Path.Combine(rootPath, "First");
            string secondDirectoryPath = Path.Combine(rootPath, "Second");
            Directory.CreateDirectory(firstDirectoryPath);
            Directory.CreateDirectory(secondDirectoryPath);
            string firstPath = Path.Combine(firstDirectoryPath, "chart.bms");
            string secondPath = Path.Combine(secondDirectoryPath, "chart.bms");
            File.WriteAllText(firstPath, "#PLAYER 1");
            File.WriteAllText(secondPath, "#PLAYER 1");
            TestableBmsFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", firstPath);
            TestableBmsFile second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", secondPath);
            var library = new TestBmsLibrary(songDbPath);
            using var initialBmsFilesNotification = new ManualResetEventSlim(false);
            System.ComponentModel.PropertyChangedEventHandler initialHandler = delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
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
            // 初期通知が終わってから解除後の通知を観測し、workerの実時間遅延は判定しない。
            initialBmsFilesNotification.Wait();
            library.PropertyChanged -= initialHandler;
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);
            int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
            int baselineDuplicateInvalidationVersion = library.DuplicateChartGroupsInvalidationVersion;
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int parentFolderVersionChanged = 0;
            int bmsFilesChanged = 0;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
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
            LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                [LibraryChartRef.FromChartFile(initialSnapshot[0])],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removal.HasError);
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
            TestableBmsFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
            TestableBmsFile replacement = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Replacement", "chart.bms"));
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

    /// <summary>保存主体の入力列をコピーしてread-onlyビューへ固定し、呼出元の後変更を受けない。</summary>
    [TestMethod]
    public void StorageRowPropertiesExposeReadOnlyViews()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            List<BMSFile> inputBmsFiles = [bmsFile];
            List<LR2SongDBExtended.bmson_song> inputBmsonSongs = [bmsonSong];
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = inputBmsFiles,
                BmsonSongs = inputBmsonSongs
            };

            Assert.AreEqual(2, InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library).Count);
            Assert.IsFalse(library.BMSFiles is List<BMSFile>);
            Assert.IsFalse(library.BmsonSongs is List<LR2SongDBExtended.bmson_song>);
            Assert.ThrowsException<NotSupportedException>(() => ((IList<BMSFile>)library.BMSFiles).Add(CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "chart.bms"))));
            Assert.ThrowsException<NotSupportedException>(() => ((IList<LR2SongDBExtended.bmson_song>)library.BmsonSongs).Clear());
            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreEqual(1, library.BmsonSongs.Count);
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
            TestableBmsFile bmsFile = CreateFile(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(
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
    public void RemoveLibraryCharts_UnregistersBmsonStorageRowsInLibraryBoundary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string rootPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed");
            string firstDirectoryPath = Path.Combine(rootPath, "First");
            string secondDirectoryPath = Path.Combine(rootPath, "Second");
            Directory.CreateDirectory(firstDirectoryPath);
            Directory.CreateDirectory(secondDirectoryPath);
            string firstPath = Path.Combine(firstDirectoryPath, "chart.bmson");
            string secondPath = Path.Combine(secondDirectoryPath, "chart.bmson");
            File.WriteAllText(firstPath, "{}");
            File.WriteAllText(secondPath, "{}");
            LR2SongDBExtended.bmson_song first = CreateBmsonSong(firstPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            LR2SongDBExtended.bmson_song second = CreateBmsonSong(secondPath, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, []);
            SetLibraryBmsonSongsWithoutNotification(library, [first, second]);
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int bmsonSongsChanged = 0;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.BmsonSongs))
                {
                    bmsonSongsChanged++;
                }
            };
            LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                [LibraryChartRef.FromChartFile(ChartFileProjection.FromBmsonSong(first))],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removal.HasError);
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

    /// <summary>
    /// 実削除を同じ library で二回続けても、warm な各 lookup は
    /// 旧snapshotを保ったまま対象差分だけを反映します。
    /// DB行、所持集合、重複キャッシュ、通知後の索引を同じ実操作で確認します。
    /// 同じlibraryの二回の削除で、残存所有者と重複キャッシュを局所差分で維持します。
    /// </summary>
    [DataTestMethod]
    [DataRow(16)]
    public void RemoveLibraryCharts_TwoWarmOperationsPreserveRemainingOwnersWithoutFullRebuild(int backgroundCount)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string sharedHash = new string('a', 32);
            string uniqueHash = new string('b', 32);
            List<TestableBmsFile> files = [];
            for (int index = 0; index < backgroundCount; index++)
            {
                files.Add(CreateFile(
                    (index + 100).ToString("x32"),
                    Path.Combine(root, "Background", index.ToString("D3") + ".bms")));
            }
            string remainingPath = Path.Combine(root, "Remaining", "shared.bms");
            string firstFolder = Path.Combine(root, "Remove1");
            string secondFolder = Path.Combine(root, "Remove2");
            string firstPath = Path.Combine(firstFolder, "shared.bms");
            string secondPath = Path.Combine(secondFolder, "unique.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(remainingPath)!);
            Directory.CreateDirectory(firstFolder);
            Directory.CreateDirectory(secondFolder);
            File.WriteAllText(remainingPath, "#PLAYER 1");
            File.WriteAllText(firstPath, "#PLAYER 1");
            File.WriteAllText(secondPath, "#PLAYER 1");
            TestableBmsFile remaining = CreateFile(sharedHash, remainingPath);
            TestableBmsFile first = CreateFile(sharedHash, firstPath);
            TestableBmsFile second = CreateFile(uniqueHash, secondPath);
            files.Add(remaining);
            files.Add(first);
            files.Add(second);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new FileDbReportRecordingDialogs())
            {
                BMSFiles = files,
                BmsonSongs = [],
                DuplicateChartGroups = []
            };
            // warm操作前のfixture seedは本番保存契約を検証しないため、一つのtransactionにまとめて共通DBロックの保持時間を短縮する。
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, db =>
            {
                foreach (TestableBmsFile file in files)
                {
                    db.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                }
            });

            BMSLibrary.InstalledPrimaryHashWarmupResult initialPrimary = library.WarmInstalledPrimaryHashLookup("u1_warm_remove");
            Assert.IsFalse(initialPrimary.FullDirectoryLookupInitialized);
            OwnedChartHashIndexVersionedSnapshot initialHash = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot initialInstalled = InvokeCreateInstalledChartLookupSnapshot(library);
            PlaylistLibraryResolveIndexSnapshot initialPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool initialPlaylistCacheHit,
                out int initialPlaylistStaleRetries);
            Assert.IsFalse(initialPlaylistCacheHit);
            Assert.AreEqual(0, initialPlaylistStaleRetries);
            Assert.IsTrue(initialHash.ContainsMd5(sharedHash));
            Assert.IsTrue(initialHash.ContainsMd5(uniqueHash));
            (LibraryChartKind Kind, string Path, string Md5, string Sha256)[] initialSharedCandidates =
                CapturePlaylistCandidateFacts(initialPlaylist.GetMd5Candidates(sharedHash));
            (LibraryChartKind Kind, string Path, string Md5, string Sha256)[] initialUniqueCandidates =
                CapturePlaylistCandidateFacts(initialPlaylist.GetMd5Candidates(uniqueHash));
            Assert.IsTrue(initialSharedCandidates.Length > 0);
            Assert.IsTrue(initialUniqueCandidates.Length > 0);
            Assert.IsTrue(initialSharedCandidates.Any(candidate =>
                string.Equals(candidate.Path, remainingPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Md5, sharedHash, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(initialSharedCandidates.Any(candidate =>
                string.Equals(candidate.Path, firstPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Md5, sharedHash, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(initialUniqueCandidates.Any(candidate =>
                string.Equals(candidate.Path, secondPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Md5, uniqueHash, StringComparison.OrdinalIgnoreCase)));
            LibraryChartRef initialSharedRepresentative = initialPlaylist.ResolveChartForPlaylistHash(sharedHash, null);
            LibraryChartRef initialUniqueRepresentative = initialPlaylist.ResolveChartForPlaylistHash(uniqueHash, null);
            Assert.IsNotNull(initialSharedRepresentative);
            Assert.IsNotNull(initialUniqueRepresentative);
            string initialSharedRepresentativePath = initialSharedRepresentative!.Path;
            string initialSharedRepresentativeMd5 = initialSharedRepresentative.Md5;
            string initialSharedRepresentativeSha256 = initialSharedRepresentative.Sha256;
            string initialUniqueRepresentativePath = initialUniqueRepresentative!.Path;
            string initialUniqueRepresentativeMd5 = initialUniqueRepresentative.Md5;
            string initialUniqueRepresentativeSha256 = initialUniqueRepresentative.Sha256;

            List<string> hashWork = [];
            List<string> playlistWork = [];
            List<string> installedWork = [];
            library.OwnedChartHashIndexStoreWorkObserver = hashWork.Add;
            library.PlaylistLibraryResolveIndexStoreWorkObserver = playlistWork.Add;
            library.InstalledChartLookupStoreWorkObserver = installedWork.Add;
            (TestableBmsFile File, string Folder)[] removals =
            [
                (first, firstFolder),
                (second, secondFolder)
            ];
            for (int removalIndex = 0; removalIndex < removals.Length; removalIndex++)
            {
                (TestableBmsFile file, string folder) = removals[removalIndex];
                string oldPath = file.path;
                string oldHash = file.hash;
                LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                    [LibraryChartRef.FromBmsFile(file)],
                    sendToRecycleBin: false,
                    approvedWholeFolderDeletePaths: [folder]);

                Assert.IsFalse(outcome.HasError);
                Assert.IsTrue(outcome.CatalogDurable);
                Assert.IsFalse(File.Exists(oldPath));
                Assert.IsFalse(Directory.Exists(folder));
                Assert.IsNull(library.DuplicateChartGroups);
                Assert.IsFalse(InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library)
                    .Any(chart => string.Equals(chart.Path, oldPath, StringComparison.OrdinalIgnoreCase)));
                // ここはSELECT専用の観測なので、writer接続を保持せずread-only入口を使う。
                using (LR2SongDBExtended verifyDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                {
                    string[] dbPaths = verifyDb.Table<LR2SongDB.song>().Select(row => row.path).ToArray();
                    Assert.IsFalse(dbPaths.Contains(oldPath, StringComparer.OrdinalIgnoreCase));
                    Assert.IsTrue(dbPaths.Contains(remainingPath, StringComparer.OrdinalIgnoreCase));
                    Assert.AreEqual(
                        removalIndex == 0,
                        dbPaths.Contains(secondPath, StringComparer.OrdinalIgnoreCase));
                    foreach (TestableBmsFile backgroundFile in files.Take(backgroundCount))
                    {
                        Assert.IsTrue(
                            dbPaths.Contains(backgroundFile.path, StringComparer.OrdinalIgnoreCase),
                            "削除後も未対象のbackground rowを保持します。");
                    }
                }
                OwnedChartHashIndexVersionedSnapshot updatedHash = library.GetOwnedChartHashIndexSnapshot();
                OwnedChartHashIndexVersionedSnapshot cachedHash = library.GetOwnedChartHashIndexSnapshot();
                InstalledChartLookupIndexSnapshot updatedInstalled = InvokeCreateInstalledChartLookupSnapshot(library);
                InstalledChartLookupIndexSnapshot cachedInstalled = InvokeCreateInstalledChartLookupSnapshot(library);
                BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary = library.WarmInstalledPrimaryHashLookup("u1_warm_remove");
                PlaylistLibraryResolveIndexSnapshot updatedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool updatedPlaylistCacheHit,
                    out int updatedPlaylistStaleRetries);
                PlaylistLibraryResolveIndexSnapshot cachedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool cachedPlaylistCacheHit,
                    out int cachedPlaylistStaleRetries);

                Assert.AreSame(updatedHash, cachedHash);
                Assert.AreSame(updatedInstalled, cachedInstalled);
                Assert.AreEqual("cached", updatedPrimary.Status);
                Assert.AreEqual(0L, updatedPrimary.BuildMs);
                Assert.IsTrue(updatedPlaylistCacheHit);
                Assert.AreEqual(0, updatedPlaylistStaleRetries);
                Assert.IsTrue(cachedPlaylistCacheHit);
                Assert.AreEqual(0, cachedPlaylistStaleRetries);
                Assert.AreSame(updatedPlaylist, cachedPlaylist);
                Assert.IsFalse(updatedPlaylist.ContainsCandidate(LibraryChartKind.Bms, oldPath));
                Assert.IsTrue(CapturePlaylistCandidateFacts(updatedPlaylist.GetMd5Candidates(sharedHash))
                    .Any(candidate =>
                        candidate.Kind == LibraryChartKind.Bms
                        && string.Equals(candidate.Path, remainingPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidate.Md5, sharedHash, StringComparison.OrdinalIgnoreCase)));
                Assert.AreEqual(
                    removalIndex == 0,
                    CapturePlaylistCandidateFacts(updatedPlaylist.GetMd5Candidates(uniqueHash)).Any(candidate =>
                        string.Equals(candidate.Path, secondPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidate.Md5, uniqueHash, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(updatedHash.ContainsMd5(sharedHash));
                Assert.AreEqual(removalIndex == 0, updatedHash.ContainsMd5(uniqueHash));
                Assert.IsTrue(initialHash.ContainsMd5(oldHash));
                Assert.IsNotNull(initialPlaylist.ResolveChartForPlaylistHash(oldHash, null));
                Assert.AreEqual(
                    initialSharedRepresentativePath,
                    initialPlaylist.ResolveChartForPlaylistHash(sharedHash, null)!.Path);
                Assert.AreEqual(
                    initialSharedRepresentativeMd5,
                    initialPlaylist.ResolveChartForPlaylistHash(sharedHash, null)!.Md5);
                Assert.AreEqual(
                    initialSharedRepresentativeSha256,
                    initialPlaylist.ResolveChartForPlaylistHash(sharedHash, null)!.Sha256);
                Assert.AreEqual(
                    initialUniqueRepresentativePath,
                    initialPlaylist.ResolveChartForPlaylistHash(uniqueHash, null)!.Path);
                Assert.AreEqual(
                    initialUniqueRepresentativeMd5,
                    initialPlaylist.ResolveChartForPlaylistHash(uniqueHash, null)!.Md5);
                Assert.AreEqual(
                    initialUniqueRepresentativeSha256,
                    initialPlaylist.ResolveChartForPlaylistHash(uniqueHash, null)!.Sha256);
                CollectionAssert.AreEqual(
                    initialSharedCandidates,
                    CapturePlaylistCandidateFacts(initialPlaylist.GetMd5Candidates(sharedHash)));
                CollectionAssert.AreEqual(
                    initialUniqueCandidates,
                    CapturePlaylistCandidateFacts(initialPlaylist.GetMd5Candidates(uniqueHash)));
            }

            Assert.IsTrue(library.BMSFiles.Contains(remaining));
            Assert.IsTrue(library.GetOwnedChartHashIndexSnapshot().ContainsMd5(sharedHash));
            Assert.IsFalse(library.GetOwnedChartHashIndexSnapshot().ContainsMd5(uniqueHash));
            Assert.AreEqual(0, hashWork.Count(operation => operation == "owned_hash_source_enumeration"));
            Assert.AreEqual(
                0,
                playlistWork.Count(operation => operation == "playlist_resolve_source_enumeration" || operation == "playlist_resolve_full_root_enumeration"));
            Assert.IsTrue(
                installedWork.Count(operation => operation == "installed_primary_hash_count_update") <= 4,
                "warm削除後にinstalled lookupを全件再構築しました。");
            Assert.IsTrue(
                installedWork.Count(operation => operation == "installed_primary_hash_count_update") > 0,
                "実削除のinstalled lookup差分更新を観測できませんでした。");
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_UnregisterAppliesCurrentResourceHealthIndexDelta()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string resourceDirectoryPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed", "Resource");
            Directory.CreateDirectory(resourceDirectoryPath);
            string resourcePath = Path.Combine(resourceDirectoryPath, "chart.bms");
            File.WriteAllText(resourcePath, "#PLAYER 1");
            TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", resourcePath);
            bmsFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(bmsFile)
            {
                hash = bmsFile.hash,
                wav_files_defined = 2,
                wav_files_existing = 1
            }, suppressPropertyChanged: true);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), null);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            EnsureCurrentResourceHealthIndex(library);
            ResourceHealthIndexSnapshot beforeSnapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            Assert.AreEqual(1, beforeSnapshot.TargetCount);
            Assert.IsTrue(beforeSnapshot.GetProjection(
                ChartFileKind.Bms,
                bmsFile.path,
                bmsFile.hash).HasIssues);
            LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(bmsFile)],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removal.HasError);

            ResourceHealthIndexSnapshot afterSnapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            Assert.AreEqual(0, afterSnapshot.TargetCount);
            Assert.AreEqual(1, beforeSnapshot.TargetCount);
            Assert.IsFalse(HasNoCurrentResourceHealthIndex(library));
        });
    }

    [TestMethod]
    public void RenameChartFolder_InvalidatesCurrentResourceHealthIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ResourceMutation_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
            string newDirectoryPath = Path.Combine(tempRootPath, "New");
            Directory.CreateDirectory(oldDirectoryPath);
            string oldBmsPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newBmsPath = Path.Combine(newDirectoryPath, "chart.bms");
            File.WriteAllText(oldBmsPath, "#PLAYER 1");
            try
            {
                TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath);
                bmsFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(bmsFile)
                {
                    hash = bmsFile.hash,
                    wav_files_defined = 2,
                    wav_files_existing = 1
                }, suppressPropertyChanged: true);
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), null);
                SetLibraryFilesWithoutNotification(library, [bmsFile]);
                SetLibraryBmsonSongsWithoutNotification(library, []);
                EnsureCurrentResourceHealthIndex(library);
                Assert.AreEqual(1, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(
                    oldDirectoryPath,
                    "New",
                    unregister: false,
                    renameRootFolder: false);
                Assert.IsTrue(receipt.DurableCommit, receipt.PrimaryFailure?.ToString());

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
    public void RenameChartFolder_DispatchesParentFolderOnceAndClearsDuplicateCache()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_OwnedMutationDispatch_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
            string newDirectoryPath = Path.Combine(tempRootPath, "New");
            Directory.CreateDirectory(oldDirectoryPath);
            string oldBmsPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newBmsPath = Path.Combine(newDirectoryPath, "chart.bms");
            string oldBmsonPath = Path.Combine(oldDirectoryPath, "chart.bmson");
            string newBmsonPath = Path.Combine(newDirectoryPath, "chart.bmson");
            File.WriteAllText(oldBmsPath, "#PLAYER 1");
            File.WriteAllText(oldBmsonPath, "{}");
            try
            {
                TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath);
                LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(oldBmsonPath, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), null);
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
                library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
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
                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(
                    oldDirectoryPath,
                    "New",
                    unregister: false,
                    renameRootFolder: false);
                Assert.IsTrue(receipt.DurableCommit, receipt.PrimaryFailure?.ToString());
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(baselineOwnedCollectionVersion + 1, library.OwnedChartCollectionVersion);
                Assert.AreEqual(1, ownedCollectionVersionChanged);
                Assert.AreEqual(0, bmsFilesChanged);
                Assert.AreEqual(0, bmsonSongsChanged);
                Assert.IsFalse(batch.NotifiesStorageRows);
                Assert.IsFalse(batch.NotifiesBmsFiles);
                Assert.IsFalse(batch.NotifiesBmsonSongs);
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

    private static (LibraryChartKind Kind, string Path, string Md5, string Sha256)[] CapturePlaylistCandidateFacts(
        IEnumerable<LibraryChartRef> candidates)
    {
        return [.. (candidates ?? [])
            .Where(candidate => candidate != null)
            .Select(candidate => (candidate.Kind, candidate.Path, candidate.Md5, candidate.Sha256))];
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
