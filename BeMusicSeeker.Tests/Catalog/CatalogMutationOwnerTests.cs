using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Tests.Helpers;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CatalogMutationOwnerTests
{
    /// <summary>
    /// 背景BMSが16件・128件でもbmsonの列挙・コピー・検索はbmson範囲だけを訪問し、入力順とコピー先の境界を保つ。
    /// </summary>
    [TestMethod]
    public void FormatRangeOperations_VisitOnlyRequestedBmsonEntriesRegardlessOfBmsBackground()
    {
        StorageWorkCounts? firstContainsWork = null;
        StorageWorkCounts? firstIndexOfWork = null;
        foreach (int bmsCount in new[] { 16, 128 })
        {
            var observer = new RecordingCatalogStorageSequenceWorkObserver();
            var owner = new CatalogOwnedCollectionOwner(observer);
            ChartFile[] bms = Enumerable.Range(0, bmsCount)
                .Select(index => CreateBms($"bms-{index:D3}.bms", $"{index + 1:x32}")).ToArray();
            ChartFile[] bmson = Enumerable.Range(0, 64)
                .Select(index => CreateBmson($"bmson-{index:D3}.bmson", $"{index + 1000:x32}")).ToArray();
            owner.ReplaceChartsAndCaptureSnapshot(bms, bmson);
            var view = (CatalogStorageReadOnlyView<ChartFile>)owner.BmsonRows;

            observer.Reset();
            CollectionAssert.AreEqual(bmson, view.ToArray());
            AssertRangeWork(observer, 64, 64);

            observer.Reset();
            CollectionAssert.AreEqual(bmson, ReadEnumeratedValues(view));
            AssertRangeWork(observer, 64, 0);

            observer.Reset();
            var genericCopy = new ChartFile[66];
            view.CopyTo(genericCopy, 1);
            CollectionAssert.AreEqual(bmson, genericCopy.Skip(1).Take(64).ToArray());
            Assert.IsNull(genericCopy[0]);
            Assert.IsNull(genericCopy[65]);
            AssertRangeWork(observer, 64, 64);

            observer.Reset();
            object[] nonGenericCopy = new object[66];
            ((ICollection)view).CopyTo(nonGenericCopy, 1);
            CollectionAssert.AreEqual(bmson, nonGenericCopy.Skip(1).Take(64).ToArray());
            Assert.IsNull(nonGenericCopy[0]);
            Assert.IsNull(nonGenericCopy[65]);
            AssertRangeWork(observer, 64, 64);

            observer.Reset();
            Assert.IsTrue(view.Contains(bmson[0]));
            StorageWorkCounts containsWork = observer.Capture();
            Assert.IsTrue(containsWork.VisitedEntryCount > 0 && containsWork.VisitedEntryCount <= 64);
            Assert.AreEqual(0, containsWork.AccessCount);
            Assert.AreEqual(0, containsWork.MaterializationCount);
            if (firstContainsWork.HasValue)
            {
                Assert.AreEqual(firstContainsWork.Value, containsWork);
            }
            firstContainsWork = containsWork;

            observer.Reset();
            Assert.AreEqual(17, view.IndexOf(bmson[17]));
            StorageWorkCounts indexOfWork = observer.Capture();
            Assert.IsTrue(indexOfWork.VisitedEntryCount >= 18 && indexOfWork.VisitedEntryCount <= 64);
            Assert.AreEqual(0, indexOfWork.AccessCount);
            Assert.AreEqual(0, indexOfWork.MaterializationCount);
            if (firstIndexOfWork.HasValue)
            {
                Assert.AreEqual(firstIndexOfWork.Value, indexOfWork);
            }
            firstIndexOfWork = indexOfWork;

            observer.Reset();
            Assert.IsFalse(view.Contains(bms[^1]));
            AssertRangeWork(observer, 64, 0);
            observer.Reset();
            Assert.AreEqual(-1, view.IndexOf(bms[0]));
            AssertRangeWork(observer, 64, 0);
        }
    }

    /// <summary>
    /// 空範囲・先頭と途中・255から257件・末尾余白の各入力で、列挙と捕捉が範囲順を保ち別集合の置換で以前の値を変えない。
    /// </summary>
    [TestMethod]
    [DataRow(0, 0, 0)]
    [DataRow(0, 0, 1)]
    [DataRow(3, 0, 1)]
    [DataRow(0, 255, 1)]
    [DataRow(3, 255, 1)]
    [DataRow(3, 256, 1)]
    [DataRow(3, 257, 1)]
    [DataRow(3, 257, 0)]
    public void RangeEnumerationAndCapture_PreserveBoundsOrderAndCapturedValues(int start, int count, int trailingCount)
    {
        var observer = new RecordingCatalogStorageSequenceWorkObserver();
        CatalogStorageSequenceEntry<int>[] entries = Enumerable.Range(0, start + count + trailingCount)
            .Select(index => new CatalogStorageSequenceEntry<int>(index, index.ToString(), index.ToString(), index)).ToArray();
        var sequence = CatalogStorageIndexedSequence<int>.FromEntries(
            entries, (left, right) => left.Ordinal.CompareTo(right.Ordinal), observer);
        var view = new CatalogStorageReadOnlyView<int>(sequence, start, count);
        int[] expected = Enumerable.Range(start, count).ToArray();

        observer.Reset();
        CollectionAssert.AreEqual(expected, ReadEnumeratedValues(view));
        AssertRangeWork(observer, count, 0);

        observer.Reset();
        IReadOnlyList<CatalogStorageSequenceEntry<int>> captured = sequence.CaptureRange(start, count);
        CollectionAssert.AreEqual(expected, captured.Select(entry => entry.Value).ToArray());
        AssertRangeWork(observer, count, count);

        if (count > 0)
        {
            CatalogStorageIndexedSequence<int> changed = sequence.ReplaceAt(start,
                new CatalogStorageSequenceEntry<int>(-1, "changed", "changed", start));
            Assert.AreEqual(-1, new CatalogStorageReadOnlyView<int>(changed, start, count)[0]);
            observer.Reset();
            CollectionAssert.AreEqual(expected, ReadEnumeratedValues(view));
            AssertRangeWork(observer, count, 0);
            CollectionAssert.AreEqual(expected, captured.Select(entry => entry.Value).ToArray());
        }
    }

    /// <summary>
    /// 背景BMSの件数とbmson包含の有無に応じて通常一覧を捕捉順で射影し、含めた行だけを一度ずつ訪問する。
    /// 同じ一巡でhash依存も準備し、準備済み入力の再準備では追加訪問・投影をしない。
    /// </summary>
    [TestMethod]
    [DataRow(16, true)]
    [DataRow(128, true)]
    [DataRow(16, false)]
    [DataRow(128, false)]
    public void NormalSourceCaptureAndProjection_VisitEachIncludedEntryOnce(int bmsCount, bool includeBmsonRows)
    {
        var observer = new RecordingCatalogStorageSequenceWorkObserver();
        var owner = new CatalogOwnedCollectionOwner(observer);
        ChartFile[] bms = Enumerable.Range(0, bmsCount)
            .Select(index => CreateBms($"bms-{bmsCount - index:D3}.bms", $"{index + 1:x32}")).ToArray();
        ChartFile[] bmson = Enumerable.Range(0, 64)
            .Select(index => CreateBmson($"bmson-{63 - index:D3}.bmson", $"{index + 1000:x32}")).ToArray();
        owner.ReplaceChartsAndCaptureSnapshot(bms, bmson);
        observer.Reset();

        CatalogChartCollectionSnapshot source = owner.CaptureSnapshot(includeBmsonRows);
        var work = new Dictionary<string, int>();
        var projectionOwner = new MainChartRowProjectionOwner
        {
            NormalInputWorkObserver = (kind, count) => work[kind] = work.GetValueOrDefault(kind) + count
        };
        NormalLibrarySourceRows rows = projectionOwner.BuildNormalSourceRows(null, source, includeBmsonRows);

        ChartFile[] expected = includeBmsonRows
            ? [.. bms, .. bmson] : bms;
        CollectionAssert.AreEqual(expected.Select(chart => chart.Path).ToArray(), rows.Select(row => row.Chart.Path).ToArray());
        rows.PrepareForOrder();
        Assert.AreEqual(expected.Length, observer.VisitedEntryCount);
        Assert.AreEqual(0, observer.AccessCount);
        Assert.AreEqual(0, observer.MaterializationCount);
        Assert.AreEqual(expected.Length, work.GetValueOrDefault("projection"));
        Assert.AreEqual(expected.Length, work.GetValueOrDefault("hash_dependency_visit"));
        Assert.AreEqual(includeBmsonRows ? 64 : 0, source.BmsonRows.Count);
    }

    private static void AssertRangeWork(RecordingCatalogStorageSequenceWorkObserver observer, int visitedCount, int materializedCount)
    {
        Assert.AreEqual(visitedCount, observer.VisitedEntryCount);
        Assert.AreEqual(0, observer.AccessCount);
        Assert.AreEqual(materializedCount, observer.MaterializationCount);
        Assert.IsTrue(observer.EnumerationCount > 0);
    }

    private static T[] ReadEnumeratedValues<T>(IEnumerable<T> source)
    {
        // Select(...).ToArray() の IList 向け最適化では indexer が使われるため、列挙器を直接検査します。
        var values = new List<T>();
        foreach (T value in source)
        {
            values.Add(value);
        }
        return values.ToArray();
    }

    /// <summary>
    /// 所有tokenのない直接保存要求から旧パスと移転先を準備し、保存する新パスと捕捉した旧現在値を取り違えない。
    /// </summary>
    [TestMethod]
    public void CreateRelocationRequest_PreparesTokenlessDirectStoragePathChange()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogRelocationRequest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string oldPath = Path.Combine(tempRootPath, "old.bms");
        string newPath = Path.Combine(tempRootPath, "new.bms");
        File.WriteAllText(newPath, "#PLAYER 1");
        try
        {
            ChartFile movedBms = CreateBms(Path.GetFileName(oldPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            movedBms = movedBms with { Path = oldPath, Token = null };
            LibraryChartPathChange pathChange = new()
            {
                Chart = (movedBms),
                OldPath = oldPath,
                NewPath = newPath
            };
            LibraryCatalogMutationFacts catalogFacts = new([], [pathChange], []);

            var owner = new CatalogMutationOwner(new CatalogOwnedCollectionOwner(), null);
            CatalogRelocationRequest request = owner.CreateRelocationRequest(catalogFacts);

            Assert.AreEqual(1, request.BmsPathReplacements.Count);
            Assert.AreEqual(oldPath, request.BmsPathReplacements[0].OldPath);
            Assert.AreEqual(newPath, request.BmsPathReplacements[0].Song.Path);
            Assert.AreEqual(oldPath, request.BmsPathReplacements[0].LiveOwner.Path);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 同パス・同hashへ置換された退役tokenの移転はDB確定前に拒否し、通知・集合世代・新しい所有行を変更しない。
    /// </summary>
    /// <remarks>実DBの確定・読戻しと所有現在値を接続し、保存の成功前に公開する誤りや一部だけのrollbackを確認する。</remarks>
    [TestMethod]
    public void ApplyCatalogMutation_RetiredRelocationTokenFailsBeforeDurableCommit()
    {
        OwnedChartCollectionTestSupport.WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string oldPath = Path.Combine(folder, "old.bms");
            string newPath = Path.Combine(folder, "new.bms");
            File.WriteAllText(newPath, "#PLAYER 1");
            ChartFile selected = CreateBms("old.bms", new string('a', 32)) with { Path = oldPath };
            var collection = new CatalogOwnedCollectionOwner();
            collection.ReplaceChartsAndCaptureSnapshot([selected], []);
            collection.Collection.UpsertCharts([selected]);
            ChartFile replacement = collection.Collection.ResolveCurrentChart(LibraryChartRef.FromPath(ChartFileKind.Bms, oldPath, null, null));
            Assert.IsNotNull(replacement);
            Assert.AreNotSame(selected.Token, replacement.Token);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([replacement]);
            int version = collection.CatalogRevision;
            int durableNotifications = 0;
            var owner = new CatalogMutationOwner(collection, gateway);
            var facts = new LibraryCatalogMutationFacts([], [new LibraryChartPathChange
            { Chart = selected, OldPath = oldPath, NewPath = newPath }], []);

            Assert.ThrowsException<InvalidCastException>(() => owner.ApplyCatalogMutation(facts, () => durableNotifications++));

            Assert.AreEqual(0, durableNotifications);
            Assert.AreEqual(version, collection.CatalogRevision);
            Assert.AreSame(replacement.Token, collection.Collection.ResolveCurrentChart(LibraryChartRef.FromPath(ChartFileKind.Bms, oldPath, null, null)).Token);
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.song row = readback.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(oldPath, row.path);
            Assert.AreEqual(replacement.Md5, row.hash);
        });
    }

    /// <summary>
    /// BMS・bmson・フォルダを一括移転したとき、全件フォルダ走査をせず実DBと所有現在値を確定して以前の捕捉一覧を保つ。
    /// </summary>
    /// <remarks>実DBの確定・読戻しと所有現在値を接続し、保存の成功前に公開する誤りや一部だけのrollbackを確認する。</remarks>
    [TestMethod]
    public void ApplyRelocation_CombinedBmsBmsonAndFolderEmitsDurableReceipt()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogRelocation_" + Guid.NewGuid().ToString("N"));
        string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
        string newDirectoryPath = Path.Combine(tempRootPath, "New");
        Directory.CreateDirectory(oldDirectoryPath);
        Directory.CreateDirectory(newDirectoryPath);
        string oldBmsPath = Path.Combine(oldDirectoryPath, "chart.bms");
        string newBmsPath = Path.Combine(newDirectoryPath, "chart.bms");
        string oldBmsonPath = Path.Combine(oldDirectoryPath, "chart.bmson");
        string newBmsonPath = Path.Combine(newDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllText(newBmsPath, "#PLAYER 1");
        File.WriteAllText(newBmsonPath, "{}");
        try
        {
            ChartFile bms = CreateBms(Path.GetFileName(oldBmsPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            bms = bms with { Path = oldBmsPath };
            ChartFile bmson = CreateBmson(Path.GetFileName(oldBmsonPath), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            bmson = bmson with { Path = oldBmsonPath };
            bmson = bmson with { Folder = oldDirectoryPath };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.RunInTransaction(() =>
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDB.folder>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(bms), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = oldDirectoryPath + Path.DirectorySeparatorChar,
                        title = "Old",
                        parent = "stale-parent"
                    }, typeof(LR2SongDB.folder));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(bmson), typeof(LR2SongDBExtended.bmson_song));
                });
            }

            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            storageRowsOwner.ReplaceChartsAndCaptureSnapshot([bms], [bmson]);
            CatalogChartCollectionSnapshot capturedRows = storageRowsOwner.CaptureSnapshot();
            var owner = new CatalogMutationOwner(storageRowsOwner, new BmsLibraryDbGateway(songDbPath));
            var folderPathChanges = new List<LibraryFolderPathChange>();
            folderPathChanges.Add(new LibraryFolderPathChange
            {
                OldFolderPath = oldDirectoryPath,
                NewFolderPath = newDirectoryPath
            });
            var pathChanges = new List<LibraryChartPathChange>();
            pathChanges.Add(new LibraryChartPathChange
            {
                Chart = (bms),
                OldPath = oldBmsPath,
                NewPath = newBmsPath
            });
            pathChanges.Add(new LibraryChartPathChange
            {
                Chart = (bmson),
                OldPath = oldBmsonPath,
                NewPath = newBmsonPath
            });

            CatalogMutationReceipt receipt = owner.ApplyCatalogMutation(
                new LibraryCatalogMutationFacts([], pathChanges, folderPathChanges));

            Assert.IsTrue(receipt.Applied);
            Assert.AreEqual(1, receipt.FolderDbTargetRows);
            Assert.AreEqual(0, receipt.FolderDbFullScanCount);
            Assert.AreEqual(2, receipt.PathFacts.Count);
            Assert.AreEqual(capturedRows.CatalogRevision, receipt.StorageRowsVersion.PreviousCatalogRevision);
            Assert.AreEqual(capturedRows.CatalogRevision + 1, receipt.StorageRowsVersion.CatalogRevision);
            Assert.AreEqual(oldBmsPath, bms.Path);
            Assert.AreEqual(newBmsPath, storageRowsOwner.BmsRows.Single().Path);
            Assert.AreSame(bms.Token, storageRowsOwner.BmsRows.Single().Token);
            Assert.AreEqual(oldBmsonPath, bmson.Path);
            Assert.AreEqual(newBmsonPath, storageRowsOwner.BmsonRows.Single().Path);
            Assert.AreSame(bmson.Token, storageRowsOwner.BmsonRows.Single().Token);
            CollectionAssert.AreEqual(new[] { bms.Token }, storageRowsOwner.BmsRows.Select(chart => chart.Token).ToArray());
            CollectionAssert.AreEqual(new[] { bmson.Token }, storageRowsOwner.BmsonRows.Select(chart => chart.Token).ToArray());
            CollectionAssert.AreEqual(new[] { bms }, capturedRows.BmsRows.ToArray());
            CollectionAssert.AreEqual(new[] { bmson }, capturedRows.BmsonRows.ToArray());
            Assert.AreEqual(oldBmsPath, capturedRows.BmsRows[0].Path);
            Assert.AreEqual(oldBmsonPath, capturedRows.BmsonRows[0].Path);
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.IsTrue(verifySongDb.Table<LR2SongDB.song>().Any(row => row.path == newBmsPath));
            Assert.IsTrue(verifySongDb.Table<LR2SongDBExtended.bmson_song>().Any(row => row.path == newBmsonPath));
            Assert.IsTrue(verifySongDb.Table<LR2SongDB.folder>().Any(row => row.path == newDirectoryPath + Path.DirectorySeparatorChar));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 移転とBMS・bmson削除をまとめて一度確定し、所持集合・保守行・不要digestを同じ結果へ更新する。
    /// </summary>
    [TestMethod]
    public void ApplyCatalogMutation_CombinesRelocationRemovalAndOwnedProjectionAfterOneCommit()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogMutation_" + Guid.NewGuid().ToString("N"));
        string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
        string newDirectoryPath = Path.Combine(tempRootPath, "New");
        Directory.CreateDirectory(oldDirectoryPath);
        Directory.CreateDirectory(newDirectoryPath);
        string movedOldPath = Path.Combine(oldDirectoryPath, "moved.bms");
        string movedNewPath = Path.Combine(newDirectoryPath, "moved.bms");
        string removedBmsPath = Path.Combine(oldDirectoryPath, "removed.bms");
        string removedBmsonPath = Path.Combine(oldDirectoryPath, "removed.bmson");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllText(movedNewPath, "#PLAYER 1");
        File.WriteAllText(removedBmsPath, "#PLAYER 1");
        File.WriteAllText(removedBmsonPath, "{}");
        try
        {
            ChartFile movedBms = CreateBms(Path.GetFileName(movedOldPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            movedBms = movedBms with { Path = movedOldPath };
            ChartFile removedBms = CreateBms(Path.GetFileName(removedBmsPath), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            removedBms = removedBms with { Path = removedBmsPath };
            ChartFile removedBmson = CreateBmson(Path.GetFileName(removedBmsonPath), "cccccccccccccccccccccccccccccccc");
            removedBmson = removedBmson with { Path = removedBmsonPath };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.RunInTransaction(() =>
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(movedBms), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(removedBms), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = removedBmsPath }, typeof(LR2SongDBExtended.maintenance));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(removedBmson), typeof(LR2SongDBExtended.bmson_song));
                    BmsLibraryDbGateway.EnsureAppOwnedSchema(songDb);
                    songDb.InsertOrReplace(
                        new LR2SongDBExtended.chart_digest_map
                        {
                            md5 = removedBms.Md5,
                            sha256 = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
                        },
                        typeof(LR2SongDBExtended.chart_digest_map));
                });
            }

            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot(
                [movedBms, removedBms],
                [removedBmson]);
            CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([movedBms, removedBms], [removedBmson]))));
            var owner = new CatalogMutationOwner(storageRowsOwner, new BmsLibraryDbGateway(songDbPath));
            var pathChanges = new List<LibraryChartPathChange>();
            pathChanges.Add(new LibraryChartPathChange
            {
                Chart = (movedBms),
                OldPath = movedOldPath,
                NewPath = movedNewPath
            });
            var removeRequests = new List<OwnedChartRemoveRequest>
            {
                OwnedChartRemoveRequest.FromChart(removedBms),
                OwnedChartRemoveRequest.FromChart(removedBmson)
            };
            LibraryCatalogMutationFacts catalogFacts = new(removeRequests, pathChanges, []);

            CatalogMutationReceipt receipt = owner.ApplyCatalogMutation(catalogFacts);

            Assert.IsTrue(receipt.Applied);
            Assert.AreEqual(CatalogMutationApplyKind.GenericMutation, receipt.Kind);
            Assert.AreEqual(0, receipt.AddedCharts.Count);
            CollectionAssert.AreEquivalent(
                new[] { removedBmsPath, removedBmsonPath },
                receipt.RemovedCharts.Select(fact => fact.Path).ToArray());
            Assert.AreEqual(1, receipt.MovedCharts.Count);
            Assert.AreEqual(movedOldPath, receipt.MovedCharts[0].OldPath);
            Assert.AreEqual(movedNewPath, receipt.MovedCharts[0].NewPath);
            Assert.AreEqual(movedBms.Md5, receipt.MovedCharts[0].Md5);
            Assert.IsTrue(receipt.OwnedCollectionApplied);
            Assert.AreEqual(ownedCollectionOwner.CatalogRevision, receipt.CatalogRevision);
            Assert.AreEqual(initialRows.CatalogRevision + 1, receipt.StorageRowsVersion.CatalogRevision);
            Assert.AreEqual(movedNewPath, storageRowsOwner.BmsRows.Single().Path);
            Assert.AreEqual(1, storageRowsOwner.BmsRows.Count);
            Assert.AreSame(movedBms.Token, storageRowsOwner.BmsRows.Single().Token);
            Assert.AreEqual(0, storageRowsOwner.BmsonRows.Count);
            Assert.AreEqual(1, ownedCollectionOwner.Collection.CreateSnapshot().Count);
            CollectionAssert.Contains(ownedCollectionOwner.Collection.CreatePathSnapshot(), movedNewPath);
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.IsTrue(verifySongDb.Table<LR2SongDB.song>().Any(row => row.path == movedNewPath));
            Assert.IsFalse(verifySongDb.Table<LR2SongDB.song>().Any(row => row.path == removedBmsPath));
            Assert.IsFalse(verifySongDb.Table<LR2SongDBExtended.bmson_song>().Any(row => row.path == removedBmsonPath));
            Assert.IsFalse(verifySongDb.Table<LR2SongDBExtended.maintenance>().Any(row => row.path == removedBmsPath));
            Assert.IsFalse(verifySongDb.Table<LR2SongDBExtended.chart_digest_map>().Any(row => row.md5 == removedBms.Md5));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>通常フォルダ（type=1）の保存中に実RAISE(ROLLBACK)で保存点が失効しても元SQLite失敗と未変更DB/現在値を保持し、接続解放後の次の明示保存が成功する。</summary>
    /// <remarks>既存CatalogMutationOwnerの専用短命接続と実folder writerを通す。共有transaction状態やprivate counterを改変せず、失効savepointを共有状態不整合に格上げしない。</remarks>
    [TestMethod]
    public void ApplyLr2NormalFolderSync_RaiseRollbackKeepsPrimaryFailureAndAllowsNextExplicitSave()
    {
        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        string root = Path.Combine(scope.DirectoryPath, "FolderRollback");
        Directory.CreateDirectory(root);
        string folderPath = Lr2SongDbSyncTestSupport.ToFolderPath(root);
        string folderInfoPath = Path.Combine(root, "folderinfo.txt");
        File.WriteAllText(folderInfoPath, "#TITLE next explicit folder\r\n");
        using (var setup = new LR2SongDBExtended(scope.SongDbPath))
        {
            setup.CreateTable<LR2SongDB.folder>();
            setup.InsertOrReplace(new LR2SongDB.folder { path = folderPath, type = 1, title = "durable folder", date = 1 }, typeof(LR2SongDB.folder));
            setup.Execute("CREATE TRIGGER folder_force_rollback BEFORE INSERT ON folder"
                + " WHEN NEW.path = '" + folderPath.Replace("'", "''") + "'"
                + " BEGIN SELECT RAISE(ROLLBACK, 'forced folder rollback'); END;");
        }
        var rows = new CatalogOwnedCollectionOwner();
        ChartFile chart = CreateBms("retained.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        CatalogChartCollectionSnapshot snapshot = rows.ReplaceChartsAndCaptureSnapshot([chart], []);
        rows.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts([chart]));
        var owner = new CatalogMutationOwner(rows, new BmsLibraryDbGateway(scope.SongDbPath));
        var request = new Lr2NormalFolderDbSyncRequest
        {
            RootDirectories = [root],
            DirectoryPaths = [root],
            FolderInfoFilePaths = [folderInfoPath],
            DirectoryLastWriteTimeUtcResolver = _ => new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        };
        SQLite.SQLiteException failure = Assert.ThrowsException<SQLite.SQLiteException>(() => owner.ApplyLr2NormalFolderSync(request));
        Assert.AreEqual(SQLite.SQLite3.Result.Constraint, failure.Result);
        StringAssert.Contains(failure.Message, "forced folder rollback");
        Assert.AreEqual(snapshot.CatalogRevision, rows.CatalogRevision);
        Assert.AreEqual(chart.Path, rows.BmsRows.Single().Path);
        CollectionAssert.AreEqual(new[] { chart.Path }, rows.Collection.CreatePathSnapshot().ToArray());
        using (LR2SongDBExtended readback = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly())
        {
            Assert.AreEqual("durable folder", readback.ExecuteScalar<string>("SELECT title FROM folder WHERE path = ?;", folderPath));
            Assert.AreEqual(1, readback.Table<LR2SongDB.folder>().Count());
        }
        using (var setup = new LR2SongDBExtended(scope.SongDbPath)) { setup.Execute("DROP TRIGGER folder_force_rollback;"); }
        Lr2NormalFolderDbSyncResult result = owner.ApplyLr2NormalFolderSync(request);
        Assert.AreEqual(1, result.UpsertedCount);
        using LR2SongDBExtended finalReadback = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly();
        Assert.AreEqual("next explicit folder", finalReadback.ExecuteScalar<string>("SELECT title FROM folder WHERE path = ?;", folderPath));
        Assert.AreEqual(snapshot.CatalogRevision, rows.CatalogRevision);
    }

    /// <summary>
    /// 移転後の削除がDB例外になれば一括変更をrollbackし、旧行と削除対象を残して現在値・集合世代を進めない。
    /// </summary>
    /// <remarks>実DBの確定・読戻しと所有現在値を接続し、保存の成功前に公開する誤りや一部だけのrollbackを確認する。</remarks>
    [TestMethod]
    public void ApplyCatalogMutation_WhenRemovalFailsRollsBackRelocationAndLiveVersions()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogMutationRollback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string oldPath = Path.Combine(tempRootPath, "moved.bms");
        string newPath = Path.Combine(tempRootPath, "moved-new.bms");
        string removedPath = Path.Combine(tempRootPath, "removed.bms");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllText(newPath, "#PLAYER 1");
        File.WriteAllText(removedPath, "#PLAYER 1");
        try
        {
            ChartFile movedBms = CreateBms(Path.GetFileName(oldPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            movedBms = movedBms with { Path = oldPath };
            ChartFile removedBms = CreateBms(Path.GetFileName(removedPath), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            removedBms = removedBms with { Path = removedPath };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.RunInTransaction(() =>
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(movedBms), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(removedBms), typeof(LR2SongDB.song));
                    string escapedRemovedPath = removedPath.Replace("'", "''");
                    songDb.Execute("CREATE TRIGGER fail_catalog_remove BEFORE DELETE ON song WHEN OLD.path = '" + escapedRemovedPath + "' BEGIN SELECT RAISE(ABORT, 'forced removal failure'); END;");
                });
            }

            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot([movedBms, removedBms], []);
            CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([movedBms, removedBms], []))));
            var owner = new CatalogMutationOwner(storageRowsOwner, new BmsLibraryDbGateway(songDbPath));
            var pathChanges = new List<LibraryChartPathChange>();
            pathChanges.Add(new LibraryChartPathChange
            {
                Chart = (movedBms),
                OldPath = oldPath,
                NewPath = newPath
            });
            var removeRequests = new List<OwnedChartRemoveRequest>
            {
                OwnedChartRemoveRequest.FromChart(removedBms)
            };
            LibraryCatalogMutationFacts catalogFacts = new(removeRequests, pathChanges, []);

            Assert.ThrowsException<SQLite.SQLiteException>(() => owner.ApplyCatalogMutation(catalogFacts));

            Assert.AreEqual(oldPath, movedBms.Path);
            Assert.AreEqual(initialRows.CatalogRevision, storageRowsOwner.CatalogRevision);
            Assert.AreEqual(2, storageRowsOwner.BmsRows.Count);
            Assert.AreEqual(2, ownedCollectionOwner.Collection.CreateSnapshot().Count);
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.IsTrue(verifySongDb.Table<LR2SongDB.song>().Any(row => row.path == oldPath));
            Assert.IsTrue(verifySongDb.Table<LR2SongDB.song>().Any(row => row.path == removedPath));
            Assert.IsFalse(verifySongDb.Table<LR2SongDB.song>().Any(row => row.path == newPath));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// ある所有を削除して同じパスへ別所有を移転するとき、移転した行とtokenを保持して削除した保守情報を引き継がない。
    /// </summary>
    [TestMethod]
    public void ApplyCatalogMutation_SamePathDifferentOwnersKeepsRelocatedOwnerRow()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogMutationSamePath_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string removedPath = Path.Combine(tempRootPath, "existing.bms");
        string movedOldPath = Path.Combine(tempRootPath, "moved-old.bms");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllText(removedPath, "#PLAYER 1");
        File.WriteAllText(movedOldPath, "#PLAYER 1");
        try
        {
            ChartFile removedBms = CreateBms(Path.GetFileName(removedPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            removedBms = removedBms with { Path = removedPath };
            ChartFile movedBms = CreateBms(Path.GetFileName(movedOldPath), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            movedBms = movedBms with { Path = movedOldPath };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.RunInTransaction(() =>
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(removedBms), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(movedBms), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(
                        new LR2SongDBExtended.maintenance { path = removedPath, hash = removedBms.Md5 },
                        typeof(LR2SongDBExtended.maintenance));
                });
            }

            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot([removedBms, movedBms], []);
            CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([removedBms, movedBms], []))));
            var owner = new CatalogMutationOwner(storageRowsOwner, new BmsLibraryDbGateway(songDbPath));
            var pathChanges = new List<LibraryChartPathChange>();
            pathChanges.Add(new LibraryChartPathChange
            {
                Chart = (movedBms),
                OldPath = movedOldPath,
                NewPath = removedPath
            });
            var removeRequests = new List<OwnedChartRemoveRequest>
            {
                OwnedChartRemoveRequest.FromChart(removedBms)
            };
            LibraryCatalogMutationFacts catalogFacts = new(removeRequests, pathChanges, []);

            owner.ApplyCatalogMutation(catalogFacts);

            Assert.AreEqual(removedPath, storageRowsOwner.BmsRows.Single().Path);
            Assert.AreEqual(1, storageRowsOwner.BmsRows.Count);
            Assert.AreSame(movedBms.Token, storageRowsOwner.BmsRows.Single().Token);
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.song persisted = verifySongDb.Table<LR2SongDB.song>().Single(row => row.path == removedPath);
            Assert.AreEqual(movedBms.Md5, persisted.hash);
            Assert.IsFalse(verifySongDb.Table<LR2SongDBExtended.maintenance>().Any(row => row.path == removedPath));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 大小文字だけ異なる移転先へ別所有を置換するとき、削除対象の完全一致旧行を残さず新しい所有のhashを保存する。
    /// </summary>
    [TestMethod]
    public void ApplyCatalogMutation_CaseVariantPathCollisionRemovesOldExactRow()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogMutationCaseCollision_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string removedPath = Path.Combine(tempRootPath, "existing.bms");
        string movedOldPath = Path.Combine(tempRootPath, "moved-old.bms");
        string movedNewPath = Path.Combine(tempRootPath, "EXISTING.bms");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllText(removedPath, "#PLAYER 1");
        File.WriteAllText(movedOldPath, "#PLAYER 1");
        try
        {
            ChartFile removedBms = CreateBms(Path.GetFileName(removedPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            removedBms = removedBms with { Path = removedPath };
            ChartFile movedBms = CreateBms(Path.GetFileName(movedOldPath), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            movedBms = movedBms with { Path = movedOldPath };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.RunInTransaction(() =>
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(removedBms), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(movedBms), typeof(LR2SongDB.song));
                });
            }

            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot([removedBms, movedBms], []);
            CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([removedBms, movedBms], []))));
            var owner = new CatalogMutationOwner(storageRowsOwner, new BmsLibraryDbGateway(songDbPath));
            var pathChanges = new List<LibraryChartPathChange>();
            pathChanges.Add(new LibraryChartPathChange
            {
                Chart = (movedBms),
                OldPath = movedOldPath,
                NewPath = movedNewPath
            });
            var removeRequests = new List<OwnedChartRemoveRequest>
            {
                OwnedChartRemoveRequest.FromChart(removedBms)
            };
            LibraryCatalogMutationFacts catalogFacts = new(removeRequests, pathChanges, []);

            owner.ApplyCatalogMutation(catalogFacts);

            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.IsFalse(verifySongDb.Table<LR2SongDB.song>().Any(row => row.path == removedPath));
            Assert.AreEqual(movedBms.Md5, verifySongDb.Table<LR2SongDB.song>().Single(row => row.path == movedNewPath).hash);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 移転と同時のパス清掃が移転先を指定しても、移転した所有・token・保存行を誤削除しない。
    /// </summary>
    [TestMethod]
    public void ApplyCatalogMutation_PathCleanupDoesNotRemoveRelocatedDestination()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogMutationProtectedDestination_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string oldPath = Path.Combine(tempRootPath, "old.bms");
        string newPath = Path.Combine(tempRootPath, "new.bms");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllText(newPath, "#PLAYER 1");
        try
        {
            ChartFile movedBms = CreateBms(Path.GetFileName(oldPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            movedBms = movedBms with { Path = oldPath };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.RunInTransaction(() =>
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(movedBms), typeof(LR2SongDB.song));
                });
            }

            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot([movedBms], []);
            CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([movedBms], []))));
            var owner = new CatalogMutationOwner(storageRowsOwner, new BmsLibraryDbGateway(songDbPath));
            var pathChanges = new List<LibraryChartPathChange>();
            pathChanges.Add(new LibraryChartPathChange
            {
                Chart = (movedBms),
                OldPath = oldPath,
                NewPath = newPath
            });
            var removeRequests = new List<OwnedChartRemoveRequest>
            {
                OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, newPath)
            };
            LibraryCatalogMutationFacts catalogFacts = new(removeRequests, pathChanges, []);

            CatalogMutationReceipt receipt = owner.ApplyCatalogMutation(catalogFacts);

            Assert.IsTrue(receipt.Applied);
            Assert.AreEqual(1, receipt.PathFacts.Count);
            Assert.AreEqual(newPath, receipt.PathFacts.Single().NewPath);
            Assert.AreEqual(newPath, storageRowsOwner.BmsRows.Single().Path);
            Assert.AreSame(movedBms.Token, storageRowsOwner.BmsRows.Single().Token);
            Assert.AreEqual(newPath, ownedCollectionOwner.Collection.CreatePathSnapshot().Single());
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1, verifySongDb.Table<LR2SongDB.song>().Count());
            Assert.AreEqual(newPath, verifySongDb.Table<LR2SongDB.song>().Single().path);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 背景16件・128件で対象四件だけを実DBへ清掃し、返却行・全表走査・SQL実行量を増やさず共有MD5とbmsonのdigest所有を保つ。
    /// </summary>
    /// <remarks>実DBの確定・読戻しと所有現在値を接続し、保存の成功前に公開する誤りや一部だけのrollbackを確認する。</remarks>
    [TestMethod]
    public void ApplyCatalogMutation_PathCleanupUsesBoundedExactSetAndPreservesDigestOwnership()
    {
        const int removalCount = 4;
        const int smallBackgroundCount = 16;
        const int largeBackgroundCount = 128;
        // temp/schemaの固定scanの揺れだけを許容し、背景差112件のscan増加は通さない。
        const int allowedFixedScanGrowth = 32;
        BoundedCleanupRun smallRun = ExecuteBoundedCleanupCase(smallBackgroundCount);
        BoundedCleanupRun largeRun = ExecuteBoundedCleanupCase(largeBackgroundCount);

        Assert.AreEqual(removalCount, smallRun.RemovalCount);
        Assert.AreEqual(removalCount, largeRun.RemovalCount);
        Assert.AreEqual(
            smallRun.ReturnedCatalogRows,
            largeRun.ReturnedCatalogRows,
            "背景行数を増やしても対象SQLの返却行数が変わりました。");
        Assert.IsTrue(smallRun.ProfiledStatementCount > 0, "16行背景のPROFILE callbackを観測できませんでした。");
        Assert.IsTrue(largeRun.ProfiledStatementCount > 0, "128行背景のPROFILE callbackを観測できませんでした。");
        Assert.IsTrue(
            largeRun.FullScanSteps <= smallRun.FullScanSteps + allowedFixedScanGrowth,
            "背景行の増加に伴って全SQLのFULLSCAN_STEPが増えました。16="
            + smallRun.FullScanSteps + ", 128=" + largeRun.FullScanSteps
            + ", statements=" + largeRun.ProfiledStatementCount);
        // VM_STEPはindex range traversalも含むため、FULLSCAN_STEPやROW callbackが不変でも
        // 背景表を広く読む集合SQLの増加を検出できる。対象件数とtemp表の大きさは両ケースで固定する。
        Assert.AreEqual(
            smallRun.VmSteps,
            largeRun.VmSteps,
            "背景行の増加に伴って全SQLのVM_STEPが増えました。16=" + smallRun.VmSteps
            + ", 128=" + largeRun.VmSteps + Environment.NewLine
            + FormatStatementMetrics(largeRun.Statements));
        Assert.IsTrue(smallRun.QueryPlans.Count > 0, "16行背景の補助query planを取得できませんでした。");
        Assert.IsTrue(largeRun.QueryPlans.Count > 0, "128行背景の補助query planを取得できませんでした。");
    }

    private static BoundedCleanupRun ExecuteBoundedCleanupCase(int backgroundCount)
    {
        const string sharedMd5 = "11111111111111111111111111111111";
        const string orphanMd5 = "22222222222222222222222222222222";
        const string bmsonMd5 = "33333333333333333333333333333333";
        string tempRootPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_CatalogMutationBoundedCleanup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string targetBmsSharedPath = Path.Combine(tempRootPath, "target-shared.bms");
        string targetBmsOrphanPath = Path.Combine(tempRootPath, "target-orphan.bms");
        string targetBmsonPath = Path.Combine(tempRootPath, "target.bmson");
        string maintenanceOnlyPath = Path.Combine(tempRootPath, "maintenance-only.bms");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        try
        {
            ChartFile targetBmsShared = CreateBms("target-shared.bms", sharedMd5);
            targetBmsShared = targetBmsShared with { Path = targetBmsSharedPath };
            ChartFile targetBmsOrphan = CreateBms("target-orphan.bms", orphanMd5);
            targetBmsOrphan = targetBmsOrphan with { Path = targetBmsOrphanPath };
            ChartFile targetBmson = CreateBmson("target.bmson", bmsonMd5);
            targetBmson = targetBmson with { Path = targetBmsonPath };
            ChartFile survivingShared = CreateBms("surviving-shared.bms", sharedMd5);
            survivingShared = survivingShared with { Path = Path.Combine(tempRootPath, "surviving-shared.bms") };
            ChartFile[] backgroundBmsRows = Enumerable.Range(0, backgroundCount - 1)
                .Select(index =>
                {
                    ChartFile row = CreateBms(
                        "background-" + index.ToString("D3") + ".bms",
                        (index + 100).ToString("x32"));
                    row = row with { Path = Path.Combine(tempRootPath, row.RawTitle) };
                    return row;
                })
                .Append(survivingShared)
                .ToArray();
            ChartFile[] backgroundBmsonRows = Enumerable.Range(0, backgroundCount)
                .Select(index =>
                {
                    string fileName = "background-" + index.ToString("D3") + ".bmson";
                    ChartFile row = CreateBmson(
                        fileName,
                        (index + 500).ToString("x32"));
                    row = row with { Path = Path.Combine(tempRootPath, fileName) };
                    return row;
                })
                .ToArray();
            ChartFile[] allBmsRows = [targetBmsShared, targetBmsOrphan, .. backgroundBmsRows];
            ChartFile[] allBmsonRows = [targetBmson, .. backgroundBmsonRows];

            // schemaとfixture投入は本番の保存経路を検証する箇所ではないため、一つのtransactionにまとめてautocommit反復による共通DBロックの保持時間を減らす。
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, setup =>
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(setup);
                BmsLibraryDbGateway.EnsureMaintenanceSchema(setup);
                BmsLibraryDbGateway.EnsureSongLookupIndexes(setup);
                foreach (ChartFile row in allBmsRows)
                {
                    setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(row), typeof(LR2SongDB.song));
                }
                foreach (ChartFile row in allBmsonRows)
                {
                    setup.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(row), typeof(LR2SongDBExtended.bmson_song));
                }
                foreach (string path in new[] { targetBmsSharedPath, targetBmsOrphanPath, targetBmsonPath, maintenanceOnlyPath }
                    .Concat(backgroundBmsRows.Select(row => row.Path))
                    .Concat(backgroundBmsonRows.Select(row => row.Path)))
                {
                    setup.InsertOrReplace(
                        new LR2SongDBExtended.maintenance { path = path },
                        typeof(LR2SongDBExtended.maintenance));
                }
                foreach ((string md5, string sha256) in new[]
                {
                    (sharedMd5, new string('a', 64)),
                    (orphanMd5, new string('b', 64)),
                    (bmsonMd5, new string('c', 64))
                })
                {
                    setup.InsertOrReplace(
                        new LR2SongDBExtended.chart_digest_map { md5 = md5, sha256 = sha256 },
                        typeof(LR2SongDBExtended.chart_digest_map));
                }
            });

            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot(
                allBmsRows.Cast<ChartFile>().ToList(),
                allBmsonRows.ToList());
            CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts(ChartTestValues.Combine(allBmsRows, allBmsonRows))));
            OwnedChartRemoveRequest[] removeRequests = new[]
            {
                OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, targetBmsSharedPath),
                OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, targetBmsOrphanPath),
                OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, maintenanceOnlyPath),
                OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bmson, targetBmsonPath)
            };
            LibraryCatalogMutationFacts catalogFacts = new(removeRequests, [], []);

            using var observation = new SqliteStatementObservation();
            var owner = new CatalogMutationOwner(storageRowsOwner, new BmsLibraryDbGateway(songDbPath, songDbFactory: observation.OpenSongDb));

            CatalogMutationReceipt receipt = owner.ApplyCatalogMutation(catalogFacts);
            observation.ThrowIfCallbackFailed();

            Assert.IsTrue(receipt.Applied);
            CollectionAssert.AreEquivalent(
                removeRequests.Select(request => request.Path).ToArray(),
                receipt.RemovedCharts.Select(fact => fact.Path).ToArray());
            string[] expectedBmsPaths = backgroundBmsRows.Select(row => row.Path).ToArray();
            string[] expectedBmsonPaths = backgroundBmsonRows.Select(row => row.Path).ToArray();
            string[] expectedOwnedPaths = expectedBmsPaths.Concat(expectedBmsonPaths).ToArray();
            CollectionAssert.AreEquivalent(
                expectedBmsPaths,
                storageRowsOwner.BmsRows.Select(row => row.Path).ToArray());
            CollectionAssert.AreEquivalent(
                expectedBmsonPaths,
                storageRowsOwner.BmsonRows.Select(row => row.Path).ToArray());
            CollectionAssert.AreEquivalent(
                expectedOwnedPaths,
                ownedCollectionOwner.Collection.CreatePathSnapshot());
            // ここはSELECT専用の観測なので、writer接続を保持せず既存のread-only入口を使う。
            using (LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                List<LR2SongDB.song> remainingBmsRows = [.. verifySongDb.Table<LR2SongDB.song>()];
                var remainingBmsonRows = verifySongDb.Table<LR2SongDBExtended.bmson_song>().ToList();
                List<LR2SongDBExtended.maintenance> remainingMaintenanceRows = [.. verifySongDb.Table<LR2SongDBExtended.maintenance>()];
                List<LR2SongDBExtended.chart_digest_map> remainingDigests = [.. verifySongDb.Table<LR2SongDBExtended.chart_digest_map>()];
                Assert.AreEqual(backgroundCount, remainingBmsRows.Count);
                Assert.AreEqual(backgroundCount, remainingBmsonRows.Count);
                Assert.AreEqual(backgroundCount + backgroundCount, remainingMaintenanceRows.Count);
                CollectionAssert.AreEquivalent(
                    expectedBmsPaths,
                    remainingBmsRows.Select(row => row.path).ToArray());
                CollectionAssert.AreEquivalent(
                    expectedBmsonPaths,
                    remainingBmsonRows.Select(row => row.path).ToArray());
                CollectionAssert.DoesNotContain(
                    remainingMaintenanceRows.Select(row => row.path).ToArray(),
                    targetBmsSharedPath);
                CollectionAssert.DoesNotContain(
                    remainingMaintenanceRows.Select(row => row.path).ToArray(),
                    targetBmsOrphanPath);
                CollectionAssert.DoesNotContain(
                    remainingMaintenanceRows.Select(row => row.path).ToArray(),
                    targetBmsonPath);
                CollectionAssert.DoesNotContain(
                    remainingMaintenanceRows.Select(row => row.path).ToArray(),
                    maintenanceOnlyPath);
                Assert.IsTrue(remainingDigests.Any(row => row.md5 == sharedMd5));
                Assert.IsFalse(remainingDigests.Any(row => row.md5 == orphanMd5));
                Assert.IsTrue(remainingDigests.Any(row => row.md5 == bmsonMd5));
            }

            IReadOnlyList<SqliteStatementObservation.SqliteObservedStatement> statements = observation.Statements;
            int returnedCatalogRows = observation.CountReturnedRows(IsCatalogResultStatement);
            Assert.IsTrue(
                returnedCatalogRows <= removeRequests.Length,
                "対象集合を超えるcatalog返却行: " + returnedCatalogRows + Environment.NewLine
                + string.Join(
                    Environment.NewLine,
                    statements.Select(statement => statement.RowCount + " rows, fullscan="
                        + statement.FullScanSteps + ", vm=" + statement.VmSteps + " " + statement.Sql)));
            Assert.IsFalse(statements.Any(IsPerHashOrphanQuery));
            IReadOnlyList<string> queryPlans = observation.ExplainCatalogQueryPlans(songDbPath);
            Assert.IsTrue(queryPlans.Count > 0);
            return new BoundedCleanupRun(
                removeRequests.Length,
                returnedCatalogRows,
                observation.CountFullScanSteps(),
                observation.CountVmSteps(),
                statements.Count(statement => statement.ProfileCount > 0),
                queryPlans,
                statements);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private sealed record BoundedCleanupRun(
        int RemovalCount,
        int ReturnedCatalogRows,
        int FullScanSteps,
        int VmSteps,
        int ProfiledStatementCount,
        IReadOnlyList<string> QueryPlans,
        IReadOnlyList<SqliteStatementObservation.SqliteObservedStatement> Statements);

    private static string FormatStatementMetrics(
        IEnumerable<SqliteStatementObservation.SqliteObservedStatement> statements)
    {
        return string.Join(
            Environment.NewLine,
            statements
                .Where(statement => statement.ProfileCount > 0)
                .Select(statement => "fullscan=" + statement.FullScanSteps + ", vm="
                    + statement.VmSteps + ", sql=" + statement.Sql));
    }

    /// <summary>
    /// song行が欠落した削除でも同パスの保守行を除き、保存行の不一致を残さない。
    /// </summary>
    [TestMethod]
    public void ApplyCatalogMutation_RemovalDeletesMaintenanceWhenSongRowIsMissing()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogMutationMaintenanceDrift_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string removedPath = Path.Combine(tempRootPath, "missing-song.bms");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        try
        {
            ChartFile removedBms = CreateBms(Path.GetFileName(removedPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            removedBms = removedBms with { Path = removedPath };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.RunInTransaction(() =>
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    songDb.InsertOrReplace(
                        new LR2SongDBExtended.maintenance { path = removedPath, hash = removedBms.Md5 },
                        typeof(LR2SongDBExtended.maintenance));
                });
            }

            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot([removedBms], []);
            CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([removedBms], []))));
            LibraryCatalogMutationFacts catalogFacts = new(
                [OwnedChartRemoveRequest.FromChart(removedBms)],
                [],
                []);

            new CatalogMutationOwner(storageRowsOwner, new BmsLibraryDbGateway(songDbPath))
                .ApplyCatalogMutation(catalogFacts);

            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.IsFalse(verifySongDb.Table<LR2SongDBExtended.maintenance>().Any(row => row.path == removedPath));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 走査によるBMS・bmson置換は両種別の追加・削除factsと一回の集合世代変更を受領結果へ含める。
    /// </summary>
    [TestMethod]
    public void Apply_EmitsCanonicalReceiptForBmsAndBmsonReplacement()
    {
        ChartFile oldBms = CreateBms("old.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartFile oldBmson = CreateBmson("old.bmson", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile nextBms = CreateBms("next.bms", "cccccccccccccccccccccccccccccccc");
        ChartFile nextBmson = CreateBmson("next.bmson", "dddddddddddddddddddddddddddddddd");
        var storageRowsOwner = new CatalogOwnedCollectionOwner();
        CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot([oldBms], [oldBmson]);
        CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
        ownedCollectionOwner.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([oldBms], [oldBmson])));
        int initialCatalogRevision = ownedCollectionOwner.CatalogRevision;
        var owner = new CatalogMutationOwner(storageRowsOwner, null);

        CatalogFileScanStorageReplacementRequest request = owner.CreateFileScanStorageReplacementRequest(
            hasDbDiff: true,
            nextBmsRows: [nextBms],
            nextBmsonRows: [nextBmson],
            deletedBmsPaths: [oldBms.Path],
            deletedBmsonPaths: [oldBmson.Path],
            addedBmsFiles: [nextBms],
            addedBmsonSongs: [nextBmson]);

        CatalogFileScanStorageReplacementReceipt receipt = owner.ApplyFileScanStorageReplacement(request);

        Assert.IsTrue(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.FileScanStorageReplacement, receipt.Kind);
        Assert.IsTrue(receipt.OwnedCollectionApplied);
        Assert.AreEqual(initialRows.CatalogRevision, receipt.StorageRowsVersion.PreviousCatalogRevision);
        Assert.AreEqual(initialRows.CatalogRevision + 1, receipt.StorageRowsVersion.CatalogRevision);
        Assert.AreEqual(2, receipt.AddedCharts.Count);
        Assert.AreEqual(2, receipt.RemovedCharts.Count);
        Assert.AreEqual(nextBms.Path, receipt.AddedCharts[0].Path);
        Assert.AreEqual(nextBmson.Path, receipt.AddedCharts[1].Path);
        Assert.AreEqual(oldBms.Path, receipt.RemovedCharts[0].Path);
        Assert.AreEqual(oldBmson.Path, receipt.RemovedCharts[1].Path);
        Assert.AreEqual(initialCatalogRevision + 1, receipt.CatalogRevision);
        Assert.AreEqual(ownedCollectionOwner.CatalogRevision, receipt.CatalogRevision);
    }

    /// <summary>
    /// DB差分のない走査置換は行と集合世代を変更せず、追加・削除・集合変更のfactsを出さない。
    /// </summary>
    [TestMethod]
    public void Apply_WithoutDbDiffDoesNotChangeRowsOrEmitMutationFacts()
    {
        ChartFile bms = CreateBms("current.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var storageRowsOwner = new CatalogOwnedCollectionOwner();
        CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot([bms], []);
        CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
        int initialCatalogRevision = ownedCollectionOwner.CatalogRevision;
        var owner = new CatalogMutationOwner(storageRowsOwner, null);

        CatalogFileScanStorageReplacementRequest request = owner.CreateFileScanStorageReplacementRequest(
            hasDbDiff: false,
            nextBmsRows: [bms],
            nextBmsonRows: [],
            deletedBmsPaths: [],
            deletedBmsonPaths: [],
            addedBmsFiles: [],
            addedBmsonSongs: []);

        CatalogFileScanStorageReplacementReceipt receipt = owner.ApplyFileScanStorageReplacement(request);

        Assert.IsFalse(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.NoOp, receipt.Kind);
        Assert.IsFalse(receipt.OwnedCollectionApplied);
        Assert.AreEqual(initialRows.CatalogRevision, receipt.StorageRowsVersion.CatalogRevision);
        Assert.AreEqual(0, receipt.AddedCharts.Count);
        Assert.AreEqual(0, receipt.RemovedCharts.Count);
        Assert.AreEqual(initialCatalogRevision, receipt.CatalogRevision);
        Assert.AreEqual(initialCatalogRevision, ownedCollectionOwner.CatalogRevision);
        Assert.AreSame(bms, storageRowsOwner.BmsRows[0]);
    }

    /// <summary>
    /// BMS・bmsonの明示置換は両種別の変更と所持集合失効を返し、集合世代を一度進める。
    /// </summary>
    [TestMethod]
    public void ApplyStorageRowsReplacement_EmitsChangedKindsAndVersions()
    {
        ChartFile oldBms = CreateBms("old.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartFile oldBmson = CreateBmson("old.bmson", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile newBms = CreateBms("new.bms", "cccccccccccccccccccccccccccccccc");
        ChartFile newBmson = CreateBmson("new.bmson", "dddddddddddddddddddddddddddddddd");
        var storageRowsOwner = new CatalogOwnedCollectionOwner();
        CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot([oldBms], [oldBmson]);
        CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
        Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([oldBms], [oldBmson]))));
        int initialCatalogRevision = ownedCollectionOwner.CatalogRevision;
        var owner = new CatalogMutationOwner(storageRowsOwner, null);

        CatalogStorageRowsReplacementRequest request = owner.CreateStorageRowsReplacementRequest(
            [newBms],
            [newBmson],
            replaceBmsRows: true,
            replaceBmsonRows: true);
        CatalogStorageRowsReplacementReceipt receipt = owner.ApplyStorageRowsReplacement(request);

        Assert.IsTrue(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.StorageRowsReplacement, receipt.Kind);
        Assert.IsTrue(receipt.BmsRowsChanged);
        Assert.IsTrue(receipt.BmsonRowsChanged);
        Assert.IsTrue(receipt.OwnedCollectionInvalidated);
        Assert.AreEqual(initialCatalogRevision + 1, receipt.CatalogRevision);
        Assert.AreEqual(receipt.CatalogRevision, ownedCollectionOwner.CatalogRevision);
        Assert.AreEqual(initialRows.CatalogRevision, receipt.StorageRowsVersion.PreviousCatalogRevision);
        Assert.AreEqual(initialRows.CatalogRevision + 1, receipt.StorageRowsVersion.CatalogRevision);
        Assert.AreSame(newBms, storageRowsOwner.BmsRows.Single());
        Assert.AreSame(newBmson, storageRowsOwner.BmsonRows.Single());
    }

    /// <summary>
    /// BMSだけを選んだ置換はbmson入力を適用せず、未選択の行と種別変更flagを保つ。
    /// </summary>
    [TestMethod]
    public void ApplyStorageRowsReplacement_LeavesUnselectedRowsUntouched()
    {
        ChartFile oldBms = CreateBms("old.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartFile oldBmson = CreateBmson("old.bmson", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile newBms = CreateBms("new.bms", "cccccccccccccccccccccccccccccccc");
        ChartFile newBmson = CreateBmson("new.bmson", "dddddddddddddddddddddddddddddddd");
        var storageRowsOwner = new CatalogOwnedCollectionOwner();
        CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot([oldBms], [oldBmson]);
        var owner = new CatalogMutationOwner(storageRowsOwner, null);

        CatalogStorageRowsReplacementReceipt receipt = owner.ApplyStorageRowsReplacement(
            owner.CreateStorageRowsReplacementRequest(
                [newBms],
                [newBmson],
                replaceBmsRows: true,
                replaceBmsonRows: false));

        Assert.IsTrue(receipt.Applied);
        Assert.IsTrue(receipt.BmsRowsChanged);
        Assert.IsFalse(receipt.BmsonRowsChanged);
        Assert.IsTrue(receipt.OwnedCollectionInvalidated);
        Assert.AreEqual(initialRows.CatalogRevision + 1, receipt.StorageRowsVersion.CatalogRevision);
        Assert.AreSame(newBms.Token, storageRowsOwner.BmsRows.Single().Token);
        Assert.AreSame(oldBmson, storageRowsOwner.BmsonRows.Single());
    }

    /// <summary>
    /// 全再読込みでtokenを再発行するときだけ新版へ進み、明示token継承の同値入力と識別実変更を区別する。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApplyStorageRowsReplacement_DistinguishesTokenInheritanceFromReload(bool inheritToken)
    {
        ChartFile bms = CreateBms("same-input.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var storageRowsOwner = new CatalogOwnedCollectionOwner();
        CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot([bms], []);
        CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
        var owner = new CatalogMutationOwner(storageRowsOwner, null);

        CatalogStorageRowsReplacementRequest request = owner.CreateStorageRowsReplacementRequest(
            storageRowsOwner.BmsRows.Select(chart => inheritToken ? chart : chart with { Token = null }),
            [],
            replaceBmsRows: true,
            replaceBmsonRows: false);
        CatalogStorageRowsReplacementReceipt receipt = owner.ApplyStorageRowsReplacement(request);

        Assert.IsTrue(receipt.Applied);
        Assert.IsTrue(receipt.BmsRowsChanged);
        Assert.IsFalse(receipt.BmsonRowsChanged);
        Assert.AreEqual(initialRows.CatalogRevision + (inheritToken ? 0 : 1), receipt.StorageRowsVersion.CatalogRevision);
        Assert.AreEqual(bms.Path, storageRowsOwner.BmsRows.Single().Path);
        Assert.AreEqual(inheritToken, ReferenceEquals(bms.Token, storageRowsOwner.BmsRows.Single().Token));
    }

    /// <summary>
    /// 背景16件・128件に固定二回の差分を適用しても、捕捉とgetterで全件列挙・再実体化をせず以前の捕捉値を保つ。
    /// </summary>
    [TestMethod]
    public void StorageRowsOwner_Background16And128WithFixedDeltaKeepsWarmCaptureAndGetterBounded()
    {
        StorageWorkScenario small = RunStorageWorkScenario(16);
        StorageWorkScenario large = RunStorageWorkScenario(128);

        Assert.IsTrue(small.ColdEnumerationCount > 0);
        Assert.IsTrue(small.ColdVisitedEntryCount > 0);
        Assert.IsTrue(small.ColdMaterializationCount > 0);
        Assert.IsTrue(small.ColdAccessCount > 0);
        Assert.IsTrue(large.ColdEnumerationCount > 0);
        Assert.IsTrue(large.ColdVisitedEntryCount > 0);
        Assert.IsTrue(large.ColdMaterializationCount > 0);
        Assert.IsTrue(large.ColdAccessCount > 0);

        Assert.AreEqual(0, small.WarmEnumerationCount);
        Assert.AreEqual(0, small.WarmVisitedEntryCount);
        Assert.AreEqual(0, small.WarmMaterializationCount);
        Assert.AreEqual(0, large.WarmEnumerationCount);
        Assert.AreEqual(0, large.WarmVisitedEntryCount);
        Assert.AreEqual(0, large.WarmMaterializationCount);
        Assert.AreEqual(small.WarmEnumerationCount, large.WarmEnumerationCount);
        Assert.AreEqual(small.WarmVisitedEntryCount, large.WarmVisitedEntryCount);
        Assert.AreEqual(small.WarmMaterializationCount, large.WarmMaterializationCount);
        Assert.IsTrue(small.WarmAccessCount > 0);
        Assert.IsTrue(large.WarmAccessCount > 0);
        Assert.IsTrue(small.WarmAccessCount <= CalculateWarmAccessUpperBound(16), $"small access={small.WarmAccessCount}");
        Assert.IsTrue(large.WarmAccessCount <= CalculateWarmAccessUpperBound(128), $"large access={large.WarmAccessCount}");
    }

    /// <summary>
    /// bmsonの初回差分適用でパス順へ正規化し、大小文字を区別する同パス置換後の順序を維持する。
    /// </summary>
    [TestMethod]
    public void OwnedCurrent_FirstUpsertNormalizesBmsonAndReplacementKeepsOrdinalPathOrder()
    {
        ChartFile z = CreateBmson("z.bmson", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartFile upper = CreateBmson("A.bmson", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ChartFile lower = CreateBmson("a.bmson", "cccccccccccccccccccccccccccccccc");
        var storageRowsOwner = new CatalogOwnedCollectionOwner();
        storageRowsOwner.ReplaceCharts(null, [z, upper, lower], replaceBms: false);
        ChartFile addedBms = CreateBms("added.bms", "dddddddddddddddddddddddddddddddd");

        storageRowsOwner.Collection.UpsertCharts(ChartTestValues.Combine([addedBms], []));

        CollectionAssert.AreEqual(
            new[] { upper.Md5, lower.Md5, z.Md5 },
            storageRowsOwner.BmsonRows.Select(chart => chart.Md5).ToArray());

        ChartFile replacement = CreateBmson("A.bmson", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        storageRowsOwner.Collection.UpsertCharts(ChartTestValues.Combine([], [replacement]));

        CollectionAssert.AreEqual(
            new[] { lower.Md5, replacement.Md5, z.Md5 },
            storageRowsOwner.BmsonRows.Select(chart => chart.Md5).ToArray());
    }

    /// <summary>
    /// 導入済みBMS・bmsonの同パス置換を実DBへ保存し、新しい所有tokenと追加factsを同じ受領結果へ反映する。
    /// </summary>
    /// <remarks>実DBの確定・読戻しと所有現在値を接続し、保存の成功前に公開する誤りや一部だけのrollbackを確認する。</remarks>
    [TestMethod]
    public void ApplyInstalledTargetUpsert_EmitsReceiptAndReplacesSamePathRows()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogInstalledTarget_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        try
        {
            ChartFile oldBms = CreateBms("same.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            ChartFile oldBmson = CreateBmson("same.bmson", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            ChartFile newBms = CreateBms("same.bms", "cccccccccccccccccccccccccccccccc");
            ChartFile newBmson = CreateBmson("same.bmson", "dddddddddddddddddddddddddddddddd");
            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot([oldBms], [oldBmson]);
            CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([oldBms], [oldBmson]))));
            var owner = new CatalogMutationOwner(storageRowsOwner, new BmsLibraryDbGateway(songDbPath));

            CatalogInstalledTargetUpsertRequest request = owner.CreateInstalledTargetUpsertRequest([newBms], [newBmson]);
            CatalogInstalledTargetUpsertReceipt receipt = owner.ApplyInstalledTargetUpsert(request);

            Assert.IsTrue(receipt.Applied);
            Assert.AreEqual(CatalogMutationApplyKind.InstalledTargetUpsert, receipt.Kind);
            Assert.IsTrue(receipt.OwnedCollectionApplied);
            Assert.AreEqual(initialRows.CatalogRevision, receipt.StorageRowsVersion.PreviousCatalogRevision);
            Assert.AreEqual(initialRows.CatalogRevision + 1, receipt.StorageRowsVersion.CatalogRevision);
            Assert.AreEqual(2, receipt.AddedCharts.Count);
            CollectionAssert.AreEquivalent(
                new[] { newBms.Path, newBmson.Path },
                receipt.AddedCharts.Select(fact => fact.Path).ToArray());
            Assert.AreEqual(ownedCollectionOwner.CatalogRevision, receipt.CatalogRevision);
            Assert.AreEqual(newBms.Md5, storageRowsOwner.BmsRows.Single().Md5);
            Assert.AreNotSame(oldBms.Token, storageRowsOwner.BmsRows.Single().Token);
            Assert.AreNotSame(newBms.Token, storageRowsOwner.BmsRows.Single().Token);
            Assert.AreEqual(newBmson.Md5, storageRowsOwner.BmsonRows.Single().Md5);
            Assert.AreNotSame(oldBmson.Token, storageRowsOwner.BmsonRows.Single().Token);
            Assert.AreNotSame(newBmson.Token, storageRowsOwner.BmsonRows.Single().Token);
            OwnedChartCollectionView view = ownedCollectionOwner.Collection.CreateCollectionView();
            Assert.IsTrue(view.ContainsOwnerPath(newBms.Path));
            Assert.IsTrue(view.ContainsOwnerPath(newBmson.Path));
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(newBms.Path, verifySongDb.Table<LR2SongDB.song>().Single(row => row.path == newBms.Path).path);
            Assert.AreEqual(newBmson.Path, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Single(row => row.path == newBmson.Path).path);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 導入済み追加では大小文字・別名・ドット別表記の完全一致keyを畳まず、未指定のBMS利用者列とbmson行を保存する。
    /// </summary>
    [DataTestMethod]
    [DataRow("CHART")]
    [DataRow("other")]
    [DataRow(".\\chart")]
    public void ApplyInstalledTargetUpsert_PreservesEveryExactKey(string siblingName)
    {
        BmsLibraryStateApplierTestSupport.WithTemporarySongDb(songDbPath =>
        {
            ChartFile oldBms = CreateBms("chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            ChartFile keptBms = CreateBms(siblingName + ".bms", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            keptBms = keptBms with { Favorite = 7 };
            keptBms = keptBms with { Tag = "preserved" };
            ChartFile oldBmson = CreateBmson("chart.bmson", "cccccccccccccccccccccccccccccccc");
            ChartFile keptBmson = CreateBmson(siblingName + ".bmson", "dddddddddddddddddddddddddddddddd");
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.RunInTransaction(() =>
                {
                    db.CreateTable<LR2SongDB.song>();
                    db.CreateTable<LR2SongDBExtended.bmson_song>();
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(oldBms), typeof(LR2SongDB.song));
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(keptBms), typeof(LR2SongDB.song));
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(oldBmson), typeof(LR2SongDBExtended.bmson_song));
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(keptBmson), typeof(LR2SongDBExtended.bmson_song));
                });
            }
            var storage = new CatalogOwnedCollectionOwner();
            CatalogChartCollectionSnapshot initial = storage.ReplaceChartsAndCaptureSnapshot([oldBms, keptBms], [oldBmson, keptBmson]);
            var owned = new CatalogOwnedCollectionOwner();
            Assert.IsTrue(owned.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([oldBms, keptBms], [oldBmson, keptBmson]))));
            var owner = new CatalogMutationOwner(storage, new BmsLibraryDbGateway(songDbPath));
            ChartFile addedBms = CreateBms("chart.bms", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
            ChartFile addedBmson = CreateBmson("chart.bmson", "ffffffffffffffffffffffffffffffff");
            ChartFile[] bmsInput = [addedBms];
            ChartFile[] bmsonInput = [addedBmson];

            CatalogInstalledTargetUpsertRequest request =
                owner.CreateInstalledTargetUpsertRequest(bmsInput, bmsonInput);
            Assert.IsTrue(owner.ApplyInstalledTargetUpsert(request).Applied);

            CollectionAssert.AreEquivalent(new[] { addedBms.Md5, keptBms.Md5 }, storage.BmsRows.Select(chart => chart.Md5).ToArray());
            Assert.AreNotSame(oldBms.Token, storage.BmsRows.Single(chart => chart.Path == addedBms.Path).Token);
            CollectionAssert.AreEquivalent(new[] { addedBmson.Md5, keptBmson.Md5 }, storage.BmsonRows.Select(chart => chart.Md5).ToArray());
            Assert.AreNotSame(oldBmson.Token, storage.BmsonRows.Single(chart => chart.Path == addedBmson.Path).Token);
            CollectionAssert.AreEquivalent(new[] { addedBms.Path, keptBms.Path, addedBmson.Path, keptBmson.Path }, owned.Collection.CreatePathSnapshot());
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(2, readback.Table<LR2SongDB.song>().Count());
            Assert.AreEqual(2, readback.Table<LR2SongDBExtended.bmson_song>().Count());
            Assert.AreEqual(addedBms.Md5, readback.Find<LR2SongDB.song>(addedBms.Path).hash);
            Assert.AreEqual(keptBms.Md5, readback.Find<LR2SongDB.song>(keptBms.Path).hash);
            Assert.AreEqual(7, readback.Find<LR2SongDB.song>(keptBms.Path).favorite);
            Assert.AreEqual("preserved", readback.Find<LR2SongDB.song>(keptBms.Path).tag);
            Assert.AreEqual(addedBmson.Md5, readback.Find<LR2SongDBExtended.bmson_song>(addedBmson.Path).md5);
            Assert.AreEqual(keptBmson.Md5, readback.Find<LR2SongDBExtended.bmson_song>(keptBmson.Path).md5);
        });
    }

    /// <summary>導入先の識別・digestの変更を保存前に拒否し、無関係なTitle・healthの確定だけなら導入を続行する。</summary>
    [DataTestMethod]
    [DataRow("title")]
    [DataRow("health")]
    [DataRow("target_replaced")]
    [DataRow("target_digest")]
    [DataRow("target_created")]
    public void ApplyInstalledTargetUpsert_ValidatesOnlyCapturedTargetInputs(string interveningChange)
    {
        BmsLibraryStateApplierTestSupport.WithTemporarySongDb(songDbPath =>
        {
            ChartFile original = CreateBms("target.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            ChartFile unrelated = CreateBms("other.bms", "cccccccccccccccccccccccccccccccc");
            var catalog = new CatalogOwnedCollectionOwner();
            catalog.ReplaceChartsAndCaptureSnapshot(interveningChange == "target_created" ? [unrelated] : [original, unrelated], []);
            var owner = new CatalogMutationOwner(catalog, new BmsLibraryDbGateway(songDbPath));
            ChartFile replacement = CreateBms("target.bms", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            CatalogInstalledTargetUpsertRequest request = owner.CreateInstalledTargetUpsertRequest([replacement], []);
            ChartFile captured = catalog.BmsRows.Last();
            if (interveningChange == "title")
            {
                catalog.CommitCurrentValues([captured], (current, _) => current with { Title = "latest title" });
            }
            else if (interveningChange == "health")
            {
                catalog.CommitCurrentValues([captured], (current, _) => current with { WAVHealth = 50 });
            }
            else if (interveningChange == "target_digest")
            {
                catalog.CommitCurrentValues([catalog.BmsRows[0]], (current, _) => current with { Md5 = "dddddddddddddddddddddddddddddddd" });
            }
            else
            {
                catalog.ApplyCommittedChartUpsert([CreateBms("target.bms", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")], out _);
            }
            int beforeApply = catalog.CatalogRevision;
            if (interveningChange is "title" or "health")
            {
                Assert.IsTrue(owner.ApplyInstalledTargetUpsert(request).Applied);
                Assert.AreEqual(beforeApply + 1, catalog.CatalogRevision);
                Assert.AreEqual(replacement.Md5, catalog.BmsRows.Single(chart => chart.Path == replacement.Path).Md5);
                ChartFile kept = catalog.BmsRows.Single(chart => chart.Path == unrelated.Path);
                Assert.AreEqual(interveningChange == "title" ? "latest title" : unrelated.Title, kept.Title);
                Assert.AreEqual(interveningChange == "health" ? 50 : (int?)null, kept.WAVHealth);
                using LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(replacement.Md5, db.Find<LR2SongDB.song>(replacement.Path).hash);
            }
            else
            {
                bool validationPassed = false;
                Assert.ThrowsException<InvalidOperationException>(() => owner.ApplyInstalledTargetUpsert(request, () => validationPassed = true));
                Assert.IsFalse(validationPassed);
                Assert.AreEqual(beforeApply, catalog.CatalogRevision);
                Assert.AreNotEqual(replacement.Md5, catalog.BmsRows.Single(chart => chart.Path == replacement.Path).Md5);
            }
        });
    }

    /// <summary>
    /// 導入済み追加のDB接続が失敗したとき、元の所持行・集合世代・所有パスを変更しない。
    /// </summary>
    /// <remarks>実DBの確定・読戻しと所有現在値を接続し、保存の成功前に公開する誤りや一部だけのrollbackを確認する。</remarks>
    [TestMethod]
    public void ApplyInstalledTargetUpsert_DatabaseFailureLeavesLiveRowsUnchanged()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogInstalledTargetFailure_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        try
        {
            ChartFile original = CreateBms("original.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            ChartFile replacement = CreateBms("replacement.bms", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot([original], []);
            CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([original], []))));
            var owner = new CatalogMutationOwner(storageRowsOwner, new BmsLibraryDbGateway(tempRootPath));

            CatalogInstalledTargetUpsertRequest request = owner.CreateInstalledTargetUpsertRequest([replacement], []);
            Assert.ThrowsException<SQLite.SQLiteException>(() => owner.ApplyInstalledTargetUpsert(request));

            Assert.AreEqual(initialRows.CatalogRevision, storageRowsOwner.CatalogRevision);
            Assert.AreSame(original, storageRowsOwner.BmsRows.Single());
            Assert.IsTrue(ownedCollectionOwner.Collection.CreateCollectionView().ContainsOwnerPath(original.Path));
            Assert.IsFalse(ownedCollectionOwner.Collection.CreateCollectionView().ContainsOwnerPath(replacement.Path));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 空の導入済み追加は未適用として返し、追加facts・集合世代・BMSとbmson行を増やさない。
    /// </summary>
    [TestMethod]
    public void ApplyInstalledTargetUpsert_EmptyRequestIsNoOp()
    {
        var storageRowsOwner = new CatalogOwnedCollectionOwner();
        CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
        var owner = new CatalogMutationOwner(storageRowsOwner, null);

        CatalogInstalledTargetUpsertRequest request = owner.CreateInstalledTargetUpsertRequest([], []);
        CatalogInstalledTargetUpsertReceipt receipt = owner.ApplyInstalledTargetUpsert(request);

        Assert.IsFalse(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.NoOp, receipt.Kind);
        Assert.AreEqual(0, receipt.AddedCharts.Count);
        Assert.AreEqual(0, receipt.StorageRowsVersion.CatalogRevision);
        Assert.AreEqual(0, storageRowsOwner.BmsRows.Count);
        Assert.AreEqual(0, storageRowsOwner.BmsonRows.Count);
    }

    /// <summary>
    /// MD5・SHA-256の差分適用は同じ集合世代で受領factsと現在の重複hashを更新する。
    /// </summary>
    [TestMethod]
    public void ApplyDigestMutation_EmitsImmutableReceiptAndUpdatesOwnedDigestRows()
    {
        ChartFile bms = CreateBms("digest.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        bms = bms with { Sha256 = new string('b', 64) };
        var storageRowsOwner = new CatalogOwnedCollectionOwner();
        CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot([bms], []);
        CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
        Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bms], []))));
        ownedCollectionOwner.Collection.CreateDuplicateChartRowSnapshot();
        var owner = new CatalogMutationOwner(storageRowsOwner, null);
        string newMd5 = "cccccccccccccccccccccccccccccccc";
        string newSha256 = new string('d', 64);
        var change = new LibraryChartDigestChange(
            ChartFileKind.Bms,
            bms.Path,
            bms.Md5,
            bms.Sha256,
            newMd5,
            newSha256);

        CatalogDigestMutationRequest request = owner.CreateDigestMutationRequest([change]);
        CatalogDigestMutationReceipt receipt = owner.ApplyDigestMutation(request);

        Assert.IsTrue(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.DigestMutation, receipt.Kind);
        Assert.IsTrue(receipt.OwnedCollectionApplied);
        Assert.AreEqual(initialRows.CatalogRevision + 1, receipt.StorageRowsVersion.CatalogRevision);
        Assert.AreEqual(1, receipt.DigestChanges.Count);
        Assert.AreSame(change, receipt.DigestChanges[0]);
        Assert.AreEqual(newMd5, ownedCollectionOwner.Collection.CreateDuplicateChartRowSnapshot().Rows.Single().LookupHash);
        Assert.AreEqual(ownedCollectionOwner.CatalogRevision, receipt.CatalogRevision);
    }

    /// <summary>
    /// MD5・SHA-256が変わらない入力は未適用とし、digest factsと集合変更を生まない。
    /// </summary>
    [TestMethod]
    public void ApplyDigestMutation_EmptyRequestIsNoOpAndSnapshotsInput()
    {
        var storageRowsOwner = new CatalogOwnedCollectionOwner();
        CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
        var owner = new CatalogMutationOwner(storageRowsOwner, null);
        var ignored = new LibraryChartDigestChange(
            ChartFileKind.Bms,
            Path.Combine("C:\\Library", "unchanged.bms"),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            null,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            null);

        CatalogDigestMutationRequest request = owner.CreateDigestMutationRequest([ignored]);
        CatalogDigestMutationReceipt receipt = owner.ApplyDigestMutation(request);

        Assert.IsFalse(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.NoOp, receipt.Kind);
        Assert.IsFalse(receipt.OwnedCollectionApplied);
        Assert.AreEqual(0, receipt.DigestChanges.Count);
        Assert.AreEqual(0, receipt.StorageRowsVersion.CatalogRevision);
    }

    /// <summary>
    /// SHA-256だけの変更は差分として受理する一方、主重複hashに用いるMD5を変更しない。
    /// </summary>
    [TestMethod]
    public void ApplyDigestMutation_ShaOnlyChangeKeepsDuplicateLookupHash()
    {
        ChartFile bms = CreateBms("sha-only.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        bms = bms with { Sha256 = new string('b', 64) };
        var storageRowsOwner = new CatalogOwnedCollectionOwner();
        CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot([bms], []);
        CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
        Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bms], []))));
        ownedCollectionOwner.Collection.CreateDuplicateChartRowSnapshot();
        var owner = new CatalogMutationOwner(storageRowsOwner, null);
        var change = new LibraryChartDigestChange(
            ChartFileKind.Bms,
            bms.Path,
            bms.Md5,
            bms.Sha256,
            bms.Md5,
            new string('c', 64));

        CatalogDigestMutationReceipt receipt = owner.ApplyDigestMutation(
            owner.CreateDigestMutationRequest([change]));

        Assert.IsTrue(receipt.Applied);
        Assert.AreEqual(1, receipt.DigestChanges.Count);
        Assert.AreEqual(bms.Md5, ownedCollectionOwner.Collection.CreateDuplicateChartRowSnapshot().Rows.Single().LookupHash);
    }

    /// <summary>
    /// 保守保存要求はBMS・bmsonの値を受け取り、空白と大小文字の差がある同じ古い保守パスを一件へ正規化する。
    /// </summary>
    [TestMethod]
    public void MaintenanceWriteRequest_SnapshotsPersistenceInputs()
    {
        ChartFile song = CreateBms("snapshot.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        song = ChartFileProjection.WithMaintenance(song, MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = song.Path, hash = song.Md5, encoding = "shift_jis", is_encoding_fixed = true }));
        ChartFile bmson = CreateBmson("snapshot.bmson", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        bmson = bmson with { ResourceHealthMaintenanceSnapshot = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = bmson.Path, hash = bmson.Md5, encoding = "utf-8" }) };

        CatalogMaintenanceWriteRequest request = new(
            [song.ResourceHealthMaintenanceSnapshot],
            [song, bmson],
            [" C:\\Library\\stale.maintenance ", "c:\\library\\STALE.MAINTENANCE"]);


        Assert.AreEqual(1, request.MaintenanceInfos.Count);
        Assert.AreEqual("C:\\Library\\snapshot.bms", request.MaintenanceInfos[0].Path);
        Assert.AreEqual(2, request.Songs.Count);
        Assert.AreEqual("C:\\Library\\snapshot.bms", request.Songs[0].Path);
        Assert.AreEqual(1, request.Songs.Count(chart => chart.Kind == ChartFileKind.Bmson));
        Assert.AreEqual("snapshot.bmson", request.Songs.Single(chart => chart.Kind == ChartFileKind.Bmson).RawTitle);
        Assert.AreEqual(1, request.StaleMaintenancePaths.Count);
        Assert.AreEqual("C:\\Library\\stale.maintenance", request.StaleMaintenancePaths[0]);
    }

    /// <summary>
    /// playlist難易度保存がDB例外になれば元例外を返し、段階を識別した表示可能な失敗factsを一度通知する。
    /// </summary>
    /// <remarks>実DBの確定・読戻しと所有現在値を接続し、保存の成功前に公開する誤りや一部だけのrollbackを確認する。</remarks>
    [TestMethod]
    public void ApplyPlaylistLevelRows_RethrowsAndPublishesFailureFact()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogSongCommandFailure_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        try
        {
            string songPath = Path.Combine("C:\\Library", "command-failure.bms");
            string[] rawValues = new string[29];
            rawValues[0] = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            rawValues[1] = "command-failure.bms";
            rawValues[7] = songPath;
            rawValues[14] = "4";
            rawValues[18] = "7";
            ChartFile song = ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(rawValues));
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.song>();
                    setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(song), typeof(LR2SongDB.song));
                });
            }
            var failureFacts = new System.Collections.Generic.List<CatalogWriteFailureFact>();
            var owner = new CatalogMutationOwner(new CatalogOwnedCollectionOwner(), new BmsLibraryDbGateway(songDbPath));
            owner.CatalogWriteFailurePublished += (sender, fact) => failureFacts.Add(fact);
            owner.ApplyModeChangeSongRows([song]);
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.Execute("CREATE TRIGGER catalog_song_command_failure BEFORE UPDATE OF level ON song BEGIN SELECT RAISE(ABORT, 'forced level failure'); END;");
            }

            Assert.ThrowsException<SQLite.SQLiteException>(() => owner.ApplyPlaylistLevelRows([song]));
            Assert.AreEqual(1, failureFacts.Count);
            Assert.AreEqual("lr2_song_db_playlist_level_update_failed", failureFacts[0].Stage);
            Assert.IsFalse(string.IsNullOrWhiteSpace(failureFacts[0].DisplayedMessage));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 譜面情報・解析失敗の保存要求は指定値を保持し、空白と大小文字だけ違う失敗削除MD5を重複させない。
    /// </summary>
    [TestMethod]
    public void ChartInfoWriteRequest_SnapshotsRowsAndDeleteKeys()
    {
        var chartInfo = new BeMusicSeeker.Models.ChartDetails
        {
            sha256 = new string('a', 64),
            md5 = new string('b', 32),
            charthash = new string('c', 64),
            notes = 123,
            parser_version = 7,
            updated_at = DateTime.UtcNow
        };
        var failure = new BeMusicSeeker.Models.ChartParseFailure
        {
            md5 = chartInfo.md5,
            sha256 = chartInfo.sha256,
            path = "C:\\Library\\snapshot.bms",
            failure_kind = "parse_failed",
            parser_version = 7,
            message = "original"
        };
        CatalogChartInfoWriteRequest request = new(
            [new ChartDigestBackfillEntry(chartInfo.md5, chartInfo.sha256)],
            [chartInfo],
            [failure],
            ["  " + chartInfo.md5, chartInfo.md5.ToUpperInvariant()]);


        Assert.AreEqual(1, request.DigestEntries.Count);
        Assert.AreEqual(123, request.ChartInfoRows[0].notes);
        Assert.AreEqual("original", request.ParseFailureRows[0].message);
        Assert.AreEqual(1, request.ParseFailureDeleteMd5s.Count);
    }

    /// <summary>
    /// 生成song行と譜面情報の一括保存で後段が失敗したら、元の難易度・利用者列を保って新しいdigest表と譜面情報行をrollbackする。
    /// </summary>
    /// <remarks>実DBの確定・読戻しと所有現在値を接続し、保存の成功前に公開する誤りや一部だけのrollbackを確認する。</remarks>
    [TestMethod]
    public void ApplyChartInfoStorageWrite_WhenChartInfoInsertFailsRollsBackGeneratedSongRowsAndFacts()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoStorageRollback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        try
        {
            ChartFile stored = CreateBms("atomic.bms", new string('c', 32));
            stored = stored with { Path = Path.Combine(tempRootPath, "atomic.bms") };
            stored = stored with { Level = 2 };
            stored = stored with { Favorite = 7 };
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.song>();
                    BmsLibraryDbGateway.EnsureChartInfoSchema(setup);
                    setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(stored), typeof(LR2SongDB.song));
                    setup.Execute("CREATE TRIGGER fail_chart_info_storage BEFORE INSERT ON chart_info BEGIN SELECT RAISE(ABORT, 'forced chart-info failure'); END;");
                });
            }
            ChartFile generated = CreateBms("atomic.bms", stored.Md5);
            generated = generated with { Path = stored.Path };
            generated = generated with { Level = 12 };
            var chartInfo = new BeMusicSeeker.Models.ChartDetails
            {
                sha256 = new string('d', 64),
                md5 = stored.Md5,
                level = 12,
                parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                updated_at = DateTime.UtcNow
            };
            var owner = new CatalogMutationOwner(new CatalogOwnedCollectionOwner(), new BmsLibraryDbGateway(songDbPath));
            var request = new CatalogChartInfoStorageWriteRequest(
            ChartTestValues.Combine([generated], []),
                new CatalogChartInfoWriteRequest(
                    [new ChartDigestBackfillEntry(stored.Md5, chartInfo.sha256)],
                    [chartInfo]));

            Assert.ThrowsException<SQLite.SQLiteException>(() => owner.ApplyChartInfoStorageWrite(request));

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.song song = verify.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?;", stored.Path).Single();
            Assert.AreEqual(2, song.level);
            Assert.AreEqual(7, song.favorite);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'chart_digest_map';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 既存songへの限定射影と譜面情報の一括保存で後段が失敗したら、元の難易度・モード・利用者列を保ってdigestと詳細行をrollbackする。
    /// </summary>
    /// <remarks>実DBの確定・読戻しと所有現在値を接続し、保存の成功前に公開する誤りや一部だけのrollbackを確認する。</remarks>
    [TestMethod]
    public void ApplyChartInfoStorageWrite_WhenChartInfoInsertFailsRollsBackNarrowSongProjection()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoProjectionRollback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        try
        {
            ChartFile stored = CreateBms("projection-atomic.bms", new string('c', 32));
            stored = stored with { Path = Path.Combine(tempRootPath, "projection-atomic.bms") };
            stored = stored with { Level = 2 };
            stored = stored with { Mode = 11 };
            stored = stored with { Favorite = 7 };
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.song>();
                    BmsLibraryDbGateway.EnsureChartInfoSchema(setup);
                    setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(stored), typeof(LR2SongDB.song));
                    setup.Execute("CREATE TRIGGER fail_chart_info_projection BEFORE INSERT ON chart_info BEGIN SELECT RAISE(ABORT, 'forced chart-info failure'); END;");
                });
            }
            var chartInfo = new BeMusicSeeker.Models.ChartDetails
            {
                sha256 = new string('d', 64),
                md5 = stored.Md5,
                level = 12,
                difficulty = 4,
                mode = 14,
                parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                updated_at = DateTime.UtcNow
            };
            var owner = new CatalogMutationOwner(new CatalogOwnedCollectionOwner(), new BmsLibraryDbGateway(songDbPath));
            var request = new CatalogChartInfoStorageWriteRequest(
            ChartTestValues.Combine([], []),
                new CatalogChartInfoWriteRequest(
                    [new ChartDigestBackfillEntry(stored.Md5, chartInfo.sha256)],
                    [chartInfo]),
                [Lr2ChartInfoSongProjection.Create(stored.Path, stored.Md5, chartInfo)]);

            Assert.ThrowsException<SQLite.SQLiteException>(() => owner.ApplyChartInfoStorageWrite(request));

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.song song = verify.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?;", stored.Path).Single();
            Assert.AreEqual(2, song.level);
            Assert.AreEqual(11, song.mode);
            Assert.AreEqual(7, song.favorite);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'chart_digest_map';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 同MD5の譜面情報追加後は決定的なSHA-256候補へ解決し、SHA-256を明示した検索はその一致行を優先する。
    /// </summary>
    [TestMethod]
    public void ChartInfoOwner_UpsertUsesSha256ThenDeterministicMd5Candidate()
    {
        var owner = new CatalogChartInfoOwner(
            _ => { },
            () => false,
            (_, _) => false,
            () => null,
            _ => { });
        var first = new BeMusicSeeker.Models.ChartDetails
        {
            sha256 = new string('b', 64),
            md5 = new string('a', 32)
        };
        var second = new BeMusicSeeker.Models.ChartDetails
        {
            sha256 = new string('a', 64),
            md5 = first.md5
        };

        owner.ReplaceIndex([first], hydrated: true);
        owner.UpsertIndex([second], "test");

        Assert.AreSame(second, owner.ResolveChartInfo(null, first.md5, null, null));
        Assert.AreSame(first, owner.ResolveChartInfo(first.sha256, null, null, null));
    }

    /// <summary>
    /// 譜面情報hydrationのscheduler拒否でも要求を終端し、完了世代とrunning解放を反映して処理中状態を取り残さない。
    /// </summary>
    [TestMethod]
    public void ChartInfoOwner_QueueHydration_WhenSchedulerRejects_CompletesCurrentRequest()
    {
        var notifications = new System.Collections.Generic.List<string>();
        Func<string, string, string, Func<Task>, bool> rejectScheduler = (_, _, _, _) => false;
        var owner = new CatalogChartInfoOwner(
            notifications.Add,
            () => false,
            (_, _) => false,
            () => rejectScheduler,
            _ => { });

        owner.QueueHydration("test_scheduler_rejected", queueBackfillAfterHydration: true, () => Task.CompletedTask);

        Assert.AreEqual(1, owner.ChartInfoHydrationRequestedVersion);
        Assert.AreEqual(1, owner.ChartInfoHydrationCompletedVersion);
        Assert.IsFalse(owner.ChartInfoHydrationRunning);
        CollectionAssert.Contains(notifications, nameof(BMSLibrary.ChartInfoHydrationRunning));
    }

    /// <summary>
    /// 旧parser版の譜面情報行はLR2用のresolverへ渡さず、現在のhashが一致しても古い解析結果を返さない。
    /// </summary>
    [TestMethod]
    public void ChartInfoOwner_ResolverSnapshot_ExcludesStaleParserRows()
    {
        var owner = new CatalogChartInfoOwner(
            _ => { },
            () => false,
            (_, _) => false,
            () => null,
            _ => { });
        string sha256 = new string('a', 64);
        string md5 = new string('b', 32);
        owner.ReplaceIndex(
        [
            new BeMusicSeeker.Models.ChartDetails
            {
                sha256 = sha256,
                md5 = md5,
                parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1
            }
        ],
        hydrated: true);
        ChartFile row = ChartTestValues.Empty();
        row = row with { Sha256 = sha256 };
        row = row with { Md5 = md5 };

        Func<ChartFile, BeMusicSeeker.Models.ChartDetails> resolver = owner.CreateLr2ResolverSnapshot();

        Assert.IsNull(resolver(row));
    }

    private static ChartFile CreateBms(string fileName, string hash)
    {
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = Path.Combine("C:\\Library", fileName)
        };
        file = file with { Md5 = hash };
        file = file with { Title = fileName, RawTitle = fileName };
        file = file with { Artist = "artist", RawArtist = "artist" };
        return file with { Token = file.Token ?? new OwnedChartToken() };
    }

    private static StorageWorkScenario RunStorageWorkScenario(int backgroundCount)
    {
        var workObserver = new RecordingCatalogStorageSequenceWorkObserver();
        var storageRowsOwner = new CatalogOwnedCollectionOwner(workObserver);
        var bmsRows = new List<ChartFile>(backgroundCount);
        var bmsonRows = new List<ChartFile>(backgroundCount);
        for (int i = 0; i < backgroundCount; i++)
        {
            bmsRows.Add(CreateBms($"background-{i}.bms", i.ToString("x32")));
            bmsonRows.Add(CreateBmson($"background-{i}.bmson", i.ToString("x32")));
        }

        storageRowsOwner.ReplaceChartsAndCaptureSnapshot(bmsRows, bmsonRows);
        var materializedBmsRows = new ChartFile[storageRowsOwner.BmsRows.Count];
        ((ICollection<ChartFile>)storageRowsOwner.BmsRows).CopyTo(materializedBmsRows, 0);
        var materializedBmsonRows = new ChartFile[storageRowsOwner.BmsonRows.Count];
        ((ICollection<ChartFile>)storageRowsOwner.BmsonRows)
            .CopyTo(materializedBmsonRows, 0);
        _ = storageRowsOwner.BmsRows[0];
        _ = storageRowsOwner.BmsonRows[0];
        StorageWorkCounts coldMaterialization = workObserver.Capture();

        storageRowsOwner.Collection.UpsertCharts(ChartTestValues.Combine([], [CreateBmson($"cold-{backgroundCount}.bmson", $"{backgroundCount:x32}")]));
        StorageWorkCounts coldNormalization = workObserver.Capture();
        workObserver.Reset();

        CatalogChartCollectionSnapshot firstDeltaSnapshot = null!;
        ChartFile firstDeltaBms = null!;
        ChartFile firstDeltaBmson = null!;
        for (int command = 0; command < 2; command++)
        {
            string suffix = $"{backgroundCount}";
            ChartFile deltaBms = CreateBms(
                $"delta-{suffix}.bms",
                $"{(backgroundCount + command + 1):x32}");
            ChartFile deltaBmson = CreateBmson(
                $"delta-{suffix}.bmson",
                $"{(backgroundCount + command + 1):x32}");
            storageRowsOwner.Collection.UpsertCharts(ChartTestValues.Combine([deltaBms], [deltaBmson]));

            CatalogChartCollectionSnapshot captured = storageRowsOwner.CaptureSnapshot();
            deltaBms = captured.BmsRows[backgroundCount];
            deltaBmson = captured.BmsonRows[backgroundCount + 1];
            _ = captured.BmsRows.Count;
            _ = captured.BmsonRows.Count;
            IReadOnlyList<ChartFile> bmsView = storageRowsOwner.BmsRows;
            IReadOnlyList<ChartFile> bmsonView = storageRowsOwner.BmsonRows;
            _ = bmsView.Count;
            _ = bmsonView.Count;
            Assert.AreEqual(backgroundCount + 1, captured.BmsRows.Count);
            Assert.AreEqual(backgroundCount + 2, captured.BmsonRows.Count);
            Assert.AreEqual(backgroundCount + 1, bmsView.Count);
            Assert.AreEqual(backgroundCount + 2, bmsonView.Count);

            int bmsDeltaIndex = backgroundCount;
            int bmsonDeltaIndex = backgroundCount + 1;
            if (command == 0)
            {
                firstDeltaSnapshot = captured;
                firstDeltaBms = deltaBms;
                firstDeltaBmson = deltaBmson;
                Assert.AreSame(deltaBms, captured.BmsRows[bmsDeltaIndex]);
                Assert.AreSame(deltaBmson, captured.BmsonRows[bmsonDeltaIndex]);
                Assert.AreSame(deltaBms, bmsView[bmsDeltaIndex]);
                Assert.AreSame(deltaBmson, bmsonView[bmsonDeltaIndex]);
            }
            else
            {
                Assert.IsNotNull(firstDeltaSnapshot);
                Assert.AreSame(deltaBms, captured.BmsRows[bmsDeltaIndex]);
                Assert.AreSame(deltaBmson, captured.BmsonRows[bmsonDeltaIndex]);
                Assert.AreSame(firstDeltaBms, firstDeltaSnapshot.BmsRows[bmsDeltaIndex]);
                Assert.AreSame(firstDeltaBmson, firstDeltaSnapshot.BmsonRows[bmsonDeltaIndex]);
            }
        }

        StorageWorkCounts warm = workObserver.Capture();
        Assert.AreEqual(backgroundCount + 1, storageRowsOwner.BmsRows.Count);
        Assert.AreEqual(backgroundCount + 2, storageRowsOwner.BmsonRows.Count);
        Assert.IsTrue(coldNormalization.EnumerationCount > coldMaterialization.EnumerationCount);
        Assert.IsTrue(coldNormalization.MaterializationCount > coldMaterialization.MaterializationCount);
        return new StorageWorkScenario(
            coldMaterialization.EnumerationCount + coldNormalization.EnumerationCount,
            coldMaterialization.VisitedEntryCount + coldNormalization.VisitedEntryCount,
            coldMaterialization.MaterializationCount + coldNormalization.MaterializationCount,
            coldMaterialization.AccessCount + coldNormalization.AccessCount,
            warm.EnumerationCount,
            warm.VisitedEntryCount,
            warm.MaterializationCount,
            warm.AccessCount);
    }

    private static int CalculateWarmAccessUpperBound(int backgroundCount)
    {
        // 共通 sequence は BMS と BMSON を同じ木へ格納します。二つの置換検索を二回行います。
        int target = (backgroundCount * 2) + 3;
        int powerOfTwo = 1;
        int ceilingLog2 = 0;
        while (powerOfTwo < target)
        {
            powerOfTwo <<= 1;
            ceilingLog2++;
        }
        return 12 + (4 * (ceilingLog2 + 1));
    }

    private static bool IsCatalogResultStatement(string sql)
    {
        return sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && (ContainsCatalogTable(sql, "song")
                || ContainsCatalogTable(sql, "bmson_song")
                || ContainsCatalogTable(sql, "maintenance"));
    }

    private static bool IsPerHashOrphanQuery(SqliteStatementObservation.SqliteObservedStatement statement)
    {
        return statement.Sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && statement.Sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase)
            && ContainsCatalogTable(statement.Sql, "song")
            && statement.Sql.Contains("hash", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsCatalogTable(string sql, string tableName)
    {
        return sql.Contains("FROM " + tableName, StringComparison.OrdinalIgnoreCase)
            || sql.Contains("FROM \"" + tableName + "\"", StringComparison.OrdinalIgnoreCase);
    }

    private static ChartFile CreateBmson(string fileName, string hash)
    {
        return ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Token = new OwnedChartToken(),
            Path = Path.Combine("C:\\Library", fileName),
            Md5 = hash,
            RawTitle = fileName,
            RawArtist = "artist"
        };
    }


    private sealed class RecordingCatalogStorageSequenceWorkObserver : ICatalogStorageSequenceWorkObserver
    {
        internal int AccessCount { get; private set; }

        internal int EnumerationCount { get; private set; }

        internal int VisitedEntryCount { get; private set; }

        internal int MaterializationCount { get; private set; }

        public void ObserveAccess() => AccessCount++;

        public void ObserveEnumeration() => EnumerationCount++;

        public void ObserveEntryVisit() => VisitedEntryCount++;

        public void ObserveMaterialization(int count) => MaterializationCount += count;

        internal StorageWorkCounts Capture()
        {
            return new StorageWorkCounts(
                EnumerationCount,
                VisitedEntryCount,
                MaterializationCount,
                AccessCount);
        }

        internal void Reset()
        {
            AccessCount = 0;
            EnumerationCount = 0;
            VisitedEntryCount = 0;
            MaterializationCount = 0;
        }
    }

    private readonly record struct StorageWorkCounts(
        int EnumerationCount,
        int VisitedEntryCount,
        int MaterializationCount,
        int AccessCount);

    private readonly record struct StorageWorkScenario(
        int ColdEnumerationCount,
        int ColdVisitedEntryCount,
        int ColdMaterializationCount,
        int ColdAccessCount,
        int WarmEnumerationCount,
        int WarmVisitedEntryCount,
        int WarmMaterializationCount,
        int WarmAccessCount);
}
