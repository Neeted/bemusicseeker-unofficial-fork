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

        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string[] folders = [Path.Combine(root, "A"), Path.Combine(root, "B"), Path.Combine(root, "C")];
            var files = new List<ChartFile>();
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
                BmsCharts = files,
                BmsonCharts = []
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                foreach (ChartFile file in files)
                {
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
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
                library.PrepareLibraryChartRemoval(files.Select(chart => LibraryChartRef.FromChartFile((chart)))), recycle, folders);

            Assert.AreEqual(2, result.ConfirmedChartCount);
            Assert.IsTrue(result.CatalogDurable);
            Assert.IsNotNull(result.SessionReceipt);
            Assert.AreEqual(2, result.SessionReceipt.ResourceDirectoryRemovalCount);
            Assert.AreSame(deletionFailure, result.Targets.Single(target => target.Path == files[1].Path).Failure);
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
            CollectionAssert.AreEqual(new[] { files[1].Path }, readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
        });
    }

    /// <summary>DB確定前後の失敗境界を分け、物理削除済み結果と未確定カタログの扱いを保持する。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoveLibraryCharts_CatalogFailureReturnsAfterConfirmedFilesystemDeletion(bool afterCommit)
    {

        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string folder = Path.Combine(root, "Pack");
            Directory.CreateDirectory(folder);
            string chartPath = Path.Combine(folder, "delete.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            string lr2Root = Path.Combine(root, "LR2");
            LR2Config config = BmsPlaylistTestSupport.CreateLr2Config(lr2Root, root);
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, () => config, null, filesystem, null,
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = afterCommit, LR2RootPath = lr2Root })
            {
                BmsCharts = [file],
                BmsonCharts = []
            };
            string folderRowPath = Lr2FolderPath.ToFolderPath(folder);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
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
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((file))]),
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
            ChartFile[] files = new[] { CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", success),
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
            { BmsCharts = files, BmsonCharts = [] };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                foreach (ChartFile? file in files)
                {
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                }
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval(files.Select(chart => LibraryChartRef.FromChartFile((chart)))), false, [partialDirectory]);

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
            ChartFile parentChart = CreateFile(new string('a', 32), parentChartPath);
            ChartFile independentChart = CreateFile(new string('c', 32), independentChartPath);
            ChartFile siblingChart = CreateFile(new string('d', 32), siblingChartPath);
            ChartFile childChart = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Token = new OwnedChartToken(),
                Path = childChartPath,
                Folder = child,
                Md5 = new string('b', 32),
                Sha256 = new string('b', 64)
            };
            var childFailure = new IOException("selected child deletion failed");
            var filesystem = new TestFileMutationService
            {
                BeforeDirectoryDelete = path =>
                {
                    if (childState == 1 && path == child)
                    {
                        throw childFailure;
                    }
                },
                BeforeFileDelete = path =>
                {
                    if (childState == 3 && path == childChartPath)
                    {
                        throw childFailure;
                    }
                }
            };
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            {
                BmsCharts = [parentChart, independentChart, siblingChart],
                BmsonCharts = [childChart]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(parentChart), typeof(LR2SongDB.song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(independentChart), typeof(LR2SongDB.song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(siblingChart), typeof(LR2SongDB.song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(childChart), typeof(LR2SongDBExtended.bmson_song));
            }
            if (childState == 2)
            {
                Directory.Delete(child, recursive: true);
            }

            LibraryChartRef[] selected = [LibraryChartRef.FromChartFile((parentChart)),
                LibraryChartRef.FromChartFile((childChart)), LibraryChartRef.FromChartFile((independentChart)),
                LibraryChartRef.FromChartFile((siblingChart))];
            var approvedFolders = library.PrepareLibraryChartRemoval(selected).WholeFolderCandidatePaths.ToList();
            CollectionAssert.AreEquivalent(new[] { parent, child, independent, sibling }, approvedFolders);
            if (childState == 3)
            {
                approvedFolders.Remove(child); // User chose chart-only deletion here.
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(library.PrepareLibraryChartRemoval(selected), recycle, approvedFolders);

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
            if (childState == 1 || childState == 3)
            {
                Assert.AreSame(childFailure, childResult.Failure);
            }

            Assert.AreEqual(0, library.BmsCharts.Count);
            Assert.AreEqual(childState == 0 ? 0 : 1, library.BmsonCharts.Count);
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

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            string child = Path.Combine(folder, "Child");
            Directory.CreateDirectory(child);
            ChartFile selected = CreateFile(new string('a', 32), Path.Combine(folder, "selected.bms"));
            ChartFile kept = CreateFile(new string('b', 32), Path.Combine(child, "kept.bms"));
            File.WriteAllText(selected.Path, "#PLAYER 1");
            File.WriteAllText(kept.Path, "#PLAYER 1");
            string resource = Path.Combine(folder, "sound.wav");
            File.WriteAllText(resource, "keep");
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            { BmsCharts = [selected, kept], BmsonCharts = [] };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                foreach (ChartFile? file in new[] { selected, kept })
                {
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                }
            }

            Assert.AreEqual(0, library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((selected))]).WholeFolderCandidatePaths.ToList().Count);

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((selected))]), false, []);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.AreEqual(0, filesystem.DirectoryDeleteCalls);
            Assert.IsFalse(File.Exists(selected.Path));
            Assert.IsTrue(File.Exists(kept.Path));
            Assert.AreEqual("keep", File.ReadAllText(resource));
            Assert.AreSame(kept, library.BmsCharts.Single());
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(kept.Path, readback.Table<LR2SongDB.song>().Single().path);
        });
    }

    /// <summary>path-only選択と本番所持化後に捕捉した同tokenの別値を、現在のownerへ解決して削除します。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RemoveLibraryCharts_ResolvesSelectionThroughProductionOwner(bool pathOnly)
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "chart.bms");
            File.WriteAllText(path, "#PLAYER 1");
            ChartFile canonical = CreateFile(new string('a', 32), path);
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            { BmsCharts = [canonical], BmsonCharts = [] };
            ChartFile owned = library.BmsCharts.Single();
            LibraryChartRef selected = pathOnly
                ? LibraryChartRef.FromPath(ChartFileKind.Bms, path, owned.Md5, owned.Sha256)
                : LibraryChartRef.FromChartFile(owned with { Title = "captured selection" });
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(canonical), typeof(LR2SongDB.song));
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(library.PrepareLibraryChartRemoval([selected]), pathOnly, [folder]);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.AreEqual(path, outcome.Targets.Single().Path);
            Assert.IsFalse(Directory.Exists(folder));
            Assert.AreEqual(pathOnly ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently,
                filesystem.DirectoryRecycleOptions.Single());
            Assert.AreEqual(0, library.BmsCharts.Count);
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
        });
    }

    /// <summary>大小文字だけが異なる別の完全一致行を同時選択したとき、両方のカタログ対象を削除する。</summary>
    [TestMethod]
    public void RemoveLibraryCharts_CaseOnlyExactRowsAreBothRemoved()
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            string lowerPath = Path.Combine(folder, "chart.bms");
            string upperPath = Path.Combine(folder, "CHART.bms");
            File.WriteAllText(lowerPath, "#PLAYER 1");
            ChartFile lower = CreateFile(new string('a', 32), lowerPath);
            ChartFile upper = CreateFile(new string('b', 32), upperPath);
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            {
                BmsCharts = [lower, upper],
                BmsonCharts = []
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(lower), typeof(LR2SongDB.song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(upper), typeof(LR2SongDB.song));
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((lower)), LibraryChartRef.FromChartFile((upper))]),
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
            Assert.AreEqual(0, library.BmsCharts.Count);
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

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            string canonicalPath = Path.Combine(folder, "chart.bms");
            string aliasPath = useDotAlias
                ? Path.Combine(folder, ".", "chart.bms")
                : Path.Combine(folder, "CHART.bms");
            File.WriteAllText(canonicalPath, "#PLAYER 1");
            ChartFile canonical = CreateFile(new string('a', 32), canonicalPath);
            ChartFile alias = CreateFile(new string('b', 32), aliasPath);
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            {
                BmsCharts = [canonical, alias],
                BmsonCharts = []
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(canonical), typeof(LR2SongDB.song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(alias), typeof(LR2SongDB.song));
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((canonical)), LibraryChartRef.FromChartFile((alias))]),
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
            Assert.AreEqual(0, library.BmsCharts.Count);
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
        });
    }

    /// <summary>同じ物理ファイルの別完全一致別名を使った自動再試行を行わない。</summary>
    [TestMethod]
    public void RemoveLibraryCharts_PhysicalAliasDeleteFailureIsNotRetried()
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            string canonicalPath = Path.Combine(folder, "chart.bms");
            string aliasPath = Path.Combine(folder, ".", "chart.bms");
            File.WriteAllText(canonicalPath, "#PLAYER 1");
            ChartFile canonical = CreateFile(new string('a', 32), canonicalPath);
            ChartFile alias = CreateFile(new string('b', 32), aliasPath);
            var failure = new IOException("selected physical file deletion failed");
            var filesystem = new TestFileMutationService
            {
                BeforeFileDelete = _ => throw failure
            };
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            {
                BmsCharts = [canonical, alias],
                BmsonCharts = []
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(canonical), typeof(LR2SongDB.song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(alias), typeof(LR2SongDB.song));
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((canonical)), LibraryChartRef.FromChartFile((alias))]),
                false,
                []);

            Assert.IsTrue(outcome.HasError);
            Assert.AreEqual(0, outcome.ConfirmedChartCount);
            Assert.AreEqual(1, filesystem.FileDeleteCalls);
            Assert.IsTrue(File.Exists(canonicalPath));
            Assert.AreEqual(2, library.BmsCharts.Count);
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
            ChartFile parentChart = CreateFile(new string('a', 32), parentPath);
            ChartFile lower = CreateFile(new string('b', 32), lowerPath);
            ChartFile upper = CreateFile(new string('c', 32), upperPath);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(),
                new FileDbReportRecordingDialogs())
            {
                BmsCharts = [parentChart, lower, upper],
                BmsonCharts = []
            };

            var confirmationPaths = library.PrepareLibraryChartRemoval(
                [LibraryChartRef.FromChartFile((parentChart)), LibraryChartRef.FromChartFile((lower)), LibraryChartRef.FromChartFile((upper))]).WholeFolderCandidatePaths.ToList();

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
            ChartFile canonical = CreateFile(new string('a', 32), catalogPath);
            LibraryChartRef selected = selectionKind == 2
                ? LibraryChartRef.FromChartFile((canonical))
                : LibraryChartRef.FromPath(ChartFileKind.Bms, selectedPath,
                    selectionKind is 1 or 3 or 4 ? canonical.Md5 : null, null);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(canonical), typeof(LR2SongDB.song));
            }

            if (selectionKind == 2)
            {
                canonical = canonical with { Path = null };
            }

            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            { BmsCharts = [canonical], BmsonCharts = [] };

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(library.PrepareLibraryChartRemoval([selected]), false, [folder]);

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

    /// <summary>事前解決は古いhashを許容し、選択後の移転・形式不一致・退役tokenを現在pathへ救済しません。</summary>
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void RemoveLibraryCharts_PreflightChecksCapturedKindAndExactPathButAllowsOldHash(int change)
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException(), "Pack");
            Directory.CreateDirectory(folder);
            string originalPath = Path.Combine(folder, "chart.bms");
            File.WriteAllText(originalPath, "#PLAYER 1");
            File.WriteAllText(Path.Combine(folder, "resource.wav"), "retain folder");
            var filesystem = new TestFileMutationService();
            var dialogs = new ConfirmingRemovalDialogs(false);
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, dialogs)
            { BmsCharts = [CreateFile(new string('a', 32), originalPath)], BmsonCharts = [] };
            try
            {
                ChartFile captured = library.BmsCharts.Single();
                var selected = LibraryChartRef.FromChartFile(captured);
                CatalogOwnedCollectionOwner owner = GetOwnedCollectionOwner(library);
                using (owner.WriteGate.GetWriterGuard())
                {
                    lock (owner.Gate)
                    {
                        if (change == 3)
                        {
                            owner.Collection.UpsertCharts([captured]);
                        }
                        else if (change != 2 && change != 4)
                        {
                            string currentPath = change == 1 ? Path.Combine(folder, "moved.bms") : originalPath;
                            if (change == 1)
                            {
                                File.Move(originalPath, currentPath);
                            }
                            ApplyCapturedCurrentValues(owner.Collection, captured with { Path = currentPath, Md5 = new string('b', 32) });
                        }
                    }
                }
                if (change == 2)
                {
                    selected = LibraryChartRef.FromChartFile(ChartTestValues.Empty(ChartFileKind.Bmson) with
                    { Token = captured.Token, Path = captured.Path, Md5 = captured.Md5 });
                }
                if (change == 4)
                {
                    selected = LibraryChartRef.FromChartFile(captured with { Path = captured.Path.ToUpperInvariant() });
                }
                ChartFile current = library.BmsCharts.Single();
                new BmsLibraryDbGateway(songDbPath).UpsertSongs([current]);
                Assert.AreEqual(change == 0 ? 1 : 0, library.PrepareLibraryChartRemoval([selected]).WholeFolderCandidatePaths.ToList().Count);
                LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts([selected], false);
                Assert.AreEqual(change == 0 ? 1 : 0, dialogs.ConfirmationCount);
                Assert.AreEqual(change == 0 ? 1 : 0, outcome.ConfirmedChartCount);
                Assert.AreEqual(0, filesystem.DirectoryDeleteCalls);
                Assert.AreEqual(change == 0 ? 1 : 0, filesystem.FileDeleteCalls);
                Assert.AreEqual(change != 0, File.Exists(current.Path));
                Assert.AreEqual(new string('a', 32), captured.Md5);
                Assert.AreEqual(change == 4 ? originalPath.ToUpperInvariant() : originalPath, selected.Path);
                using var readback = new LR2SongDBExtended(songDbPath);
                if (change == 0)
                {
                    Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
                    Assert.AreEqual(0, library.BmsCharts.Count);
                }
                else
                {
                    Assert.AreEqual(LibraryChartRemovalState.Unresolved, outcome.Targets.Single().State);
                    LR2SongDB.song row = readback.Table<LR2SongDB.song>().Single();
                    Assert.AreEqual(current.Path, row.path);
                    Assert.AreEqual(current.Md5, row.hash);
                    Assert.AreSame(current.Token, library.BmsCharts.Single().Token);
                }
            }
            finally
            {
                library.RequestShutdown("removal-preflight-test");
            }
        });
    }

    /// <summary>不正な後方対象を全件照合で拒否し、削除と複数batch renameの先行対象も変更しません。</summary>
    [TestMethod]
    public void PreparedRequests_RejectRetiredTargetBeforeAnyPhysicalOrCatalogMutation()
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException(), "Prepared");
            Directory.CreateDirectory(folder);
            string firstPath = Path.Combine(folder, "first.bms");
            string lastPath = Path.Combine(folder, "last.pms");
            File.WriteAllText(firstPath, "#PLAYER 1");
            File.WriteAllText(lastPath, "#PLAYER 1");
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            { BmsCharts = [CreateFile(new string('a', 32), firstPath), CreateFile(new string('b', 32), lastPath)], BmsonCharts = [] };
            try
            {
                ChartFile first = library.BmsCharts.Single(chart => chart.Path == firstPath);
                ChartFile retired = library.BmsCharts.Single(chart => chart.Path == lastPath);
                CatalogOwnedCollectionOwner owner = GetOwnedCollectionOwner(library);
                using (owner.WriteGate.GetWriterGuard())
                {
                    lock (owner.Gate) { owner.Collection.UpsertCharts([retired]); }
                }
                ChartFile[] before = library.BmsCharts.ToArray();
                ChartFile replacement = before.Single(chart => chart.Path == lastPath);
                Assert.AreNotSame(retired.Token, replacement.Token);
                Assert.AreEqual(retired.Md5, replacement.Md5);
                new BmsLibraryDbGateway(songDbPath).UpsertSongs(before);
                int version = owner.OwnedCollectionVersion;
                int notifications = library.NormalLibraryRefreshNotificationVersion;
                OwnedChartHashIndexSnapshot hashes = owner.Collection.CreateOwnedHashIndexSnapshot();
                var prepared = new LibraryChartRemovalPreflight([first, retired], [], [], 2, 0);

                ArgumentException deleteFailure = Assert.ThrowsException<ArgumentException>(() => library.RemoveLibraryCharts(prepared, false, []));
                ArgumentException renameFailure = Assert.ThrowsException<ArgumentException>(() => library.RenameBMSFilesExtensionsWithReceipt(
                    [new LibraryFileExtensionRenameBatch([first], ".bmx"), new LibraryFileExtensionRenameBatch([retired], ".pmx")]));

                StringAssert.Contains(deleteFailure.Message, BeMusicSeeker.Properties.Resources.Error_PreparedChartTargetMismatch);
                StringAssert.Contains(renameFailure.Message, BeMusicSeeker.Properties.Resources.Error_PreparedChartTargetMismatch);
                StringAssert.Contains(deleteFailure.Message, lastPath);
                StringAssert.Contains(renameFailure.Message, lastPath);
                Assert.AreEqual("targets", deleteFailure.ParamName);
                Assert.AreEqual("targets", renameFailure.ParamName);
                Assert.AreEqual(0, filesystem.FileDeleteCalls);
                Assert.AreEqual(0, filesystem.DirectoryDeleteCalls);
                Assert.AreEqual(0, filesystem.FileMoveCalls);
                Assert.IsTrue(File.Exists(firstPath));
                Assert.IsTrue(File.Exists(lastPath));
                AssertChartSnapshotParity(before, library.BmsCharts);
                Assert.AreEqual(version, owner.OwnedCollectionVersion);
                Assert.AreEqual(notifications, library.NormalLibraryRefreshNotificationVersion);
                CollectionAssert.AreEquivalent(hashes.Md5Hashes.ToArray(), owner.Collection.CreateOwnedHashIndexSnapshot().Md5Hashes.ToArray());
                using var readback = new LR2SongDBExtended(songDbPath);
                CollectionAssert.AreEquivalent(before.Select(chart => chart.Path + ":" + chart.Md5).ToArray(),
                    readback.Table<LR2SongDB.song>().Select(row => row.path + ":" + row.hash).ToArray());
                using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation("prepared_failure_released", showMessage: false);
                Assert.IsNotNull(probe);
            }
            finally { library.RequestShutdown("prepared_binding_test"); }
        });
    }

    /// <summary>通常renameは固定配置を維持し、古いprepared hashで現在hashのDB・current・索引を巻き戻しません。</summary>
    [TestMethod]
    public void PreparedRename_UsesCurrentDigestAndFixedSourceWithoutHashHistoryComparison()
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException(), "PreparedDigest");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "chart.bms");
            File.WriteAllText(path, "#PLAYER 1");
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            { BmsCharts = [CreateFile(new string('b', 32), path) with { Sha256 = new string('c', 64) }], BmsonCharts = [] };
            try
            {
                ChartFile current = library.BmsCharts.Single();
                new BmsLibraryDbGateway(songDbPath).UpsertSongs([current]);
                ChartFile oldDigest = current with { Md5 = new string('a', 32), Sha256 = new string('d', 64) };
                var prepared = new LibraryFileExtensionRenameBatch([oldDigest], ".bmx");

                LibraryMutationSessionReceipt receipt = library.RenameBMSFilesExtensionsWithReceipt([prepared], unregister: false);

                Assert.IsTrue(receipt.DurableCommit);
                Assert.AreEqual(1, receipt.ConfirmedChangeCount);
                Assert.AreEqual(path, filesystem.FileMoveSourcePaths.Single());
                Assert.AreEqual(1, filesystem.FileMoveCalls);
                Assert.AreEqual(0, filesystem.FileDeleteCalls);
                string destination = Path.ChangeExtension(path, ".bmx");
                Assert.IsFalse(File.Exists(path));
                Assert.IsTrue(File.Exists(destination));
                ChartFile moved = library.BmsCharts.Single();
                Assert.AreSame(current.Token, moved.Token);
                Assert.AreEqual(destination, moved.Path);
                Assert.AreEqual(current.Md5, moved.Md5);
                Assert.AreEqual(current.Sha256, moved.Sha256);
                Assert.AreEqual(new string('a', 32), prepared.Targets.Single().Md5);
                OwnedChartHashIndexSnapshot hashes = GetOwnedCollectionOwner(library).Collection.CreateOwnedHashIndexSnapshot();
                Assert.IsTrue(hashes.Md5Hashes.Contains(current.Md5, StringComparer.OrdinalIgnoreCase));
                Assert.IsTrue(hashes.Sha256Hashes.Contains(current.Sha256, StringComparer.OrdinalIgnoreCase));
                Assert.IsFalse(hashes.Md5Hashes.Contains(oldDigest.Md5, StringComparer.OrdinalIgnoreCase));
                Assert.IsFalse(hashes.Sha256Hashes.Contains(oldDigest.Sha256, StringComparer.OrdinalIgnoreCase));
                using var readback = new LR2SongDBExtended(songDbPath);
                LR2SongDB.song row = readback.Table<LR2SongDB.song>().Single();
                Assert.AreEqual(destination, row.path);
                Assert.AreEqual(current.Md5, row.hash);
                using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation("prepared_digest_released", showMessage: false);
                Assert.IsNotNull(probe);
            }
            finally { library.RequestShutdown("prepared_digest_test"); }
        });
    }

    /// <summary>Replaces the legacy helper's prompt test with real preflight confirmation and deletion.</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoveLibraryCharts_LastChartHonorsWholeFolderConfirmation(bool deleteWholeFolder)
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            ChartFile chart = CreateFile(new string('a', 32), Path.Combine(folder, "chart.bms"));
            File.WriteAllText(chart.Path, "#PLAYER 1");
            File.WriteAllText(Path.Combine(folder, "resource.wav"), "remove with the folder");
            var filesystem = new TestFileMutationService();
            var dialogs = new ConfirmingRemovalDialogs(deleteWholeFolder);
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, dialogs)
            { BmsCharts = [chart], BmsonCharts = [] };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(chart), typeof(LR2SongDB.song));
            }

            CollectionAssert.AreEqual(new[] { folder }, library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((chart))]).WholeFolderCandidatePaths.ToList());

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts([LibraryChartRef.FromChartFile((chart))], false);

            Assert.AreEqual(1, dialogs.ConfirmationCount);
            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(deleteWholeFolder ? 1 : 0, filesystem.DirectoryDeleteCalls);
            Assert.AreEqual(!deleteWholeFolder, Directory.Exists(folder));
            Assert.AreEqual(!deleteWholeFolder, File.Exists(Path.Combine(folder, "resource.wav")));
            Assert.AreEqual(0, library.BmsCharts.Count);
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_UnregisterKeepsOwnedCollectionInitializedAndSynced()
    {

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
            ChartFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", firstPath);
            ChartFile second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", secondPath);
            var library = new TestBmsLibrary(songDbPath);
            using var initialBmsFilesNotification = new ManualResetEventSlim(false);
            System.ComponentModel.PropertyChangedEventHandler initialHandler = delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    initialBmsFilesNotification.Set();
                }
            };
            library.PropertyChanged += initialHandler;
            library.BmsCharts = [first, second];
            library.BmsonCharts = [];
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
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    bmsFilesChanged++;
                }
            };
            LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile(initialSnapshot[0])]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removal.HasError);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

            Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
            Assert.AreEqual(1, parentFolderVersionChanged);
            Assert.AreEqual(1, bmsFilesChanged);
            Assert.IsTrue((batch.ChangedCharts.Count > 0 || batch.DeletedTokens.Count > 0 || batch.HasEffect(LibraryChartRefreshEffects.SourceChanged)));
            Assert.IsTrue(batch.DeletedTokens.Contains(first.Token));
            Assert.IsFalse(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bmson));
            Assert.IsNull(library.DuplicateChartGroups);
            Assert.AreEqual(baselineDuplicateInvalidationVersion + 1, library.DuplicateChartGroupsInvalidationVersion);
            Assert.AreEqual(1, library.BmsCharts.Count);
            Assert.AreSame(second, library.BmsCharts[0]);
            List<ChartFile> afterSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, afterSnapshot.Count);
            Assert.AreSame(second.Token, afterSnapshot[0].Token);
        });
    }

    [TestMethod]
    public void BMSFilesReplacement_InvalidatesOwnedCollectionVersionAndRebuildsOnNextView()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
            ChartFile replacement = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Replacement", "chart.bms"));
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [first],
                BmsonCharts = []
            };
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, initialSnapshot.Count);
            library.BmsCharts = [replacement];
            List<ChartFile> rebuiltSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);

            Assert.AreEqual(1, rebuiltSnapshot.Count);
            Assert.AreSame(replacement.Token, rebuiltSnapshot[0].Token);
        });
    }

    /// <summary>保存主体の入力列をコピーしてread-onlyビューへ固定し、呼出元の後変更を受けない。</summary>
    [TestMethod]
    public void StorageRowPropertiesExposeReadOnlyViews()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            ChartFile bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            List<ChartFile> inputBmsFiles = [bmsFile];
            List<ChartFile> inputBmsonSongs = [bmsonSong];
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = inputBmsFiles,
                BmsonCharts = inputBmsonSongs
            };

            Assert.AreEqual(2, InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library).Count);
            Assert.IsFalse(library.BmsCharts is List<ChartFile>);
            Assert.IsFalse(library.BmsonCharts is List<ChartFile>);
            Assert.ThrowsException<NotSupportedException>(() => ((IList<ChartFile>)library.BmsCharts).Add(CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "chart.bms"))));
            Assert.ThrowsException<NotSupportedException>(() => ((IList<ChartFile>)library.BmsonCharts).Clear());
            Assert.AreEqual(1, library.BmsCharts.Count);
            Assert.AreEqual(1, library.BmsonCharts.Count);
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);

            inputBmsFiles.Clear();
            inputBmsonSongs.Clear();
            List<ChartFile> afterInputMutationSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);

            Assert.AreEqual(1, library.BmsCharts.Count);
            Assert.AreEqual(1, library.BmsonCharts.Count);
            Assert.AreEqual(2, afterInputMutationSnapshot.Count);
            Assert.AreSame(bmsFile.Token, afterInputMutationSnapshot.Single(chart => chart.Kind == ChartFileKind.Bms).Token);
            Assert.AreSame(bmsonSong.Token, afterInputMutationSnapshot.Single(chart => chart.Kind == ChartFileKind.Bmson).Token);
        });
    }

    [TestMethod]
    public void HasOwnedChartUnderRealPath_UsesOwnedCollectionForBmsAndBmson()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile bmsFile = CreateFile(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            ChartFile bmsonSong = CreateBmsonSong(
                Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [bmsFile],
                BmsonCharts = [bmsonSong]
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
            ChartFile first = CreateBmsonSong(firstPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            ChartFile second = CreateBmsonSong(secondPath, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, []);
            SetLibraryBmsonSongsWithoutNotification(library, [first, second]);
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int bmsonSongsChanged = 0;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    bmsonSongsChanged++;
                }
            };
            LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((first))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removal.HasError);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

            Assert.AreEqual(1, library.BmsonCharts.Count);
            Assert.AreSame(second, library.BmsonCharts.Single());
            Assert.AreEqual(1, bmsonSongsChanged);
            Assert.IsTrue((batch.ChangedCharts.Count > 0 || batch.DeletedTokens.Count > 0 || batch.HasEffect(LibraryChartRefreshEffects.SourceChanged)));
            Assert.IsFalse(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bms));
            Assert.IsTrue(batch.DeletedTokens.Contains(first.Token));
            List<ChartFile> afterSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, afterSnapshot.Count);
            Assert.AreSame(second.Token, afterSnapshot[0].Token);
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

        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string sharedHash = new string('a', 32);
            string uniqueHash = new string('b', 32);
            List<ChartFile> files = [];
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
            ChartFile remaining = CreateFile(sharedHash, remainingPath);
            ChartFile first = CreateFile(sharedHash, firstPath);
            ChartFile second = CreateFile(uniqueHash, secondPath);
            files.Add(remaining);
            files.Add(first);
            files.Add(second);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new FileDbReportRecordingDialogs())
            {
                BmsCharts = files,
                BmsonCharts = [],
                DuplicateChartGroups = []
            };
            // warm操作前のfixture seedは本番保存契約を検証しないため、一つのtransactionにまとめて共通DBロックの保持時間を短縮する。
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, db =>
            {
                foreach (ChartFile file in files)
                {
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
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
            (ChartFileKind Kind, string Path, string Md5, string Sha256)[] initialSharedCandidates =
                CapturePlaylistCandidateFacts(initialPlaylist.GetMd5Candidates(sharedHash));
            (ChartFileKind Kind, string Path, string Md5, string Sha256)[] initialUniqueCandidates =
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
            (ChartFile File, string Folder)[] removals =
            [
                (first, firstFolder),
                (second, secondFolder)
            ];
            for (int removalIndex = 0; removalIndex < removals.Length; removalIndex++)
            {
                (ChartFile file, string folder) = removals[removalIndex];
                string oldPath = file.Path;
                string oldHash = file.Md5;
                LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                    library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((file))]),
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
                    foreach (ChartFile backgroundFile in files.Take(backgroundCount))
                    {
                        Assert.IsTrue(
                            dbPaths.Contains(backgroundFile.Path, StringComparer.OrdinalIgnoreCase),
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
                Assert.IsFalse(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, oldPath));
                Assert.IsTrue(CapturePlaylistCandidateFacts(updatedPlaylist.GetMd5Candidates(sharedHash))
                    .Any(candidate =>
                        candidate.Kind == ChartFileKind.Bms
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

            Assert.IsTrue(library.BmsCharts.Contains(remaining));
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

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string resourceDirectoryPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed", "Resource");
            Directory.CreateDirectory(resourceDirectoryPath);
            string resourcePath = Path.Combine(resourceDirectoryPath, "chart.bms");
            File.WriteAllText(resourcePath, "#PLAYER 1");
            ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", resourcePath);
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = bmsFile.Path, hash = bmsFile.Md5, wav_files_defined = 2, wav_files_existing = 1 }));
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), null);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            EnsureCurrentResourceHealthIndex(library);
            ResourceHealthIndexSnapshot beforeSnapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            Assert.AreEqual(1, beforeSnapshot.TargetCount);
            Assert.IsTrue(beforeSnapshot.GetProjection(
                ChartFileKind.Bms,
                bmsFile.Path,
                bmsFile.Md5).HasIssues);
            LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((bmsFile))]),
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
                ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath);
                bmsFile = ChartFileProjection.WithMaintenance(bmsFile, MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = bmsFile.Path, hash = bmsFile.Md5, wav_files_defined = 2, wav_files_existing = 1 }));
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
                ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath);
                ChartFile bmsonSong = CreateBmsonSong(oldBmsonPath, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), null);
                SetLibraryFilesWithoutNotification(library, [bmsFile]);
                SetLibraryBmsonSongsWithoutNotification(library, [bmsonSong]);
                SetDuplicateChartGroupsWithoutNotification(library, []);
                int baselineOwnedCollectionVersion = library.OwnedCollectionVersion;
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
                    if (args.PropertyName == "OwnedCollectionVersion")
                    {
                        ownedCollectionVersionChanged++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        bmsFilesChanged++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        bmsonSongsChanged++;
                    }
                    if (args.PropertyName == "OwnedCollectionVersion")
                    {
                        bmsonPathAvailableAtNotification = library.BmsonCharts.Any(song => song.Path == newBmsonPath);
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

                Assert.AreEqual(baselineOwnedCollectionVersion + 1, library.OwnedCollectionVersion);
                Assert.AreEqual(1, ownedCollectionVersionChanged);
                Assert.AreEqual(1, bmsFilesChanged);
                Assert.AreEqual(1, bmsonSongsChanged);
                Assert.AreEqual(0, batch.DeletedTokens.Count);
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bms && chart.Path == newBmsPath));
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bmson && chart.Path == newBmsonPath));
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

    private static (ChartFileKind Kind, string Path, string Md5, string Sha256)[] CapturePlaylistCandidateFacts(
        IEnumerable<LibraryChartRef> candidates)
    {
        return [.. (candidates ?? [])
            .Where(candidate => candidate != null)
            .Select(candidate => (candidate.Kind, candidate.Path, candidate.Md5, candidate.Sha256))];
    }

    /// <summary>Local confirmation terminal; it never opens a window or inspects translated copy.</summary>
    private sealed class ConfirmingRemovalDialogs(bool deleteWholeFolder, Action? onConfirmation = null) : IBmsLibraryDialogService
    {
        /// <summary>Counts the user's whole-folder decision, not diagnostic text.</summary>
        internal int ConfirmationCount { get; private set; }

        /// <summary>Returns the test's explicit folder decision without opening a dialog.</summary>
        public UiDialogDefaultResult Show(string messageBoxText, string caption, UiDialogButton button,
            UiDialogIcon icon, UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
        {
            if (button == UiDialogButton.YesNo)
            {
                ConfirmationCount++;
                onConfirmation?.Invoke();
            }

            return deleteWholeFolder ? UiDialogDefaultResult.Yes : UiDialogDefaultResult.No;
        }
    }

}
