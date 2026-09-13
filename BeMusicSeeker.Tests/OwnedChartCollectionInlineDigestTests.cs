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
public sealed class OwnedChartCollectionInlineDigestTests
{
    [TestMethod]
    public void BuildInlineChartInfo_DispatchesDigestMutationToOwnedAdjacentIndexes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            new BmsLibraryDbGateway(songDbPath).EnsureChartInfoSchema();
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "InlineDigest");
            Directory.CreateDirectory(chartDirectory);
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE hash update\r\n#BPM 120\r\n#00111:01\r\n", System.Text.Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath, null);
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            SetDuplicateChartGroupsWithoutNotification(library, []);
            InstalledChartLookupIndexSnapshot initialLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            OwnedChartHashIndexVersionedSnapshot initialSummary = library.GetOwnedChartHashIndexSnapshot();
            EnsureCurrentResourceHealthIndex(library);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int bmsFilesChanged = 0;
            int ownedCollectionVersionChanged = 0;
            bool digestVisibleAtOwnedCollectionNotification = false;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSFiles")
                {
                    bmsFilesChanged++;
                }
                if (args.PropertyName == "OwnedChartCollectionVersion")
                {
                    ownedCollectionVersionChanged++;
                    digestVisibleAtOwnedCollectionNotification = library
                        .GetOwnedChartHashIndexSnapshot()
                        .ContainsMd5(snapshot.Md5);
                }
            };

            ChartInfoInlineBuildResult result = InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
                library,
                "test_inline_digest",
                [ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false)]);

            InstalledChartLookupIndexSnapshot updatedLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            OwnedChartHashIndexVersionedSnapshot updatedSummary = library.GetOwnedChartHashIndexSnapshot();
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.AreEqual(1, result.DigestChanges.Count);
            Assert.AreEqual(snapshot.Md5, bmsFile.hash);
            Assert.AreEqual(snapshot.Sha256, bmsFile.sha256);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
            Assert.IsTrue(initialLookup.ContainsPrimaryHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.IsFalse(updatedLookup.ContainsPrimaryHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(snapshot.Md5));
            CollectionAssert.AreEqual(
                new[] { chartDirectory },
                initialLookup.Md5Directories["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].ToArray());
            CollectionAssert.AreEqual(
                new[] { chartDirectory },
                updatedLookup.Md5Directories[snapshot.Md5].ToArray());
            Assert.AreEqual(1, initialLookup.GetPrimaryHashCount("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.AreEqual(1, updatedLookup.GetPrimaryHashCount(snapshot.Md5));
            Assert.AreEqual(1, initialLookup.DirectoryReferenceCount);
            Assert.AreEqual(2, updatedLookup.DirectoryReferenceCount);
            Assert.IsTrue(initialSummary.Md5Hashes.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.IsFalse(updatedSummary.Md5Hashes.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.IsTrue(updatedSummary.Md5Hashes.Contains(snapshot.Md5));
            Assert.AreEqual(1, initialSummary.GetMd5OwnerCount("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.AreEqual(0, updatedSummary.GetMd5OwnerCount("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.AreEqual(1, updatedSummary.GetMd5OwnerCount(snapshot.Md5));
            Assert.AreNotEqual(initialSummary.Version, updatedSummary.Version);
            Assert.AreEqual(0, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
            Assert.IsNull(library.DuplicateChartGroups);
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsTrue(batch.NotifiesBmsFiles);
            Assert.IsFalse(batch.NotifiesBmsonSongs);
            Assert.AreEqual(0, bmsFilesChanged);
            Assert.AreEqual(1, ownedCollectionVersionChanged);
            Assert.IsTrue(digestVisibleAtOwnedCollectionNotification);
        });
    }

    [TestMethod]
    public void BuildInlineChartInfo_UpdatesWarmPlaylistResolveIndexBeforeNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            new BmsLibraryDbGateway(songDbPath).EnsureChartInfoSchema();
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "InlineResolve");
            Directory.CreateDirectory(chartDirectory);
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE warm resolve update\r\n#BPM 120\r\n#00111:01\r\n",
                System.Text.Encoding.ASCII);
            ChartFileSnapshot content = ChartFileContentReader.ReadSnapshot(chartPath);
            const string oldMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var bmsFile = CreateFile(oldMd5, chartPath, null);
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            List<string> resolveWork = [];
            library.PlaylistLibraryResolveIndexStoreWorkObserver = resolveWork.Add;

            PlaylistLibraryResolveIndexSnapshot initialResolve = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool initialCacheHit,
                out int initialStaleRetries);
            Assert.IsFalse(initialCacheHit);
            Assert.AreEqual(0, initialStaleRetries);
            Assert.AreEqual(chartPath, initialResolve.ResolveChartForPlaylistHash(oldMd5, null).Path);
            resolveWork.Clear();

            InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
                library,
                "test_inline_playlist_resolve",
                [ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false)]);

            PlaylistLibraryResolveIndexSnapshot updatedResolve = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool updatedCacheHit,
                out int updatedStaleRetries);
            PlaylistLibraryResolveIndexSnapshot cachedResolve = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool cachedCacheHit,
                out int cachedStaleRetries);

            Assert.IsTrue(updatedCacheHit);
            Assert.IsTrue(cachedCacheHit);
            Assert.AreEqual(0, updatedStaleRetries);
            Assert.AreEqual(0, cachedStaleRetries);
            Assert.AreEqual(content.Md5, bmsFile.hash);
            Assert.AreEqual(content.Sha256, bmsFile.sha256);
            Assert.IsNull(updatedResolve.ResolveChartForPlaylistHash(oldMd5, null));
            Assert.AreEqual(chartPath, updatedResolve.ResolveChartForPlaylistHash(content.Md5, null).Path);
            Assert.AreEqual(chartPath, updatedResolve.ResolveChartForPlaylistHash(null, content.Sha256).Path);
            Assert.AreEqual(updatedResolve.Version, cachedResolve.Version);
            Assert.IsNotNull(initialResolve.ResolveChartForPlaylistHash(oldMd5, null));
            Assert.IsNull(initialResolve.ResolveChartForPlaylistHash(content.Md5, null));
            Assert.AreEqual(0, resolveWork.Count(operation => operation == "playlist_resolve_source_enumeration"));
            Assert.AreEqual(0, resolveWork.Count(operation => operation == "playlist_resolve_full_root_enumeration"));
            Assert.AreEqual(0, resolveWork.Count(operation => operation == "playlist_resolve_source_entry_visited"));
            Assert.IsTrue(resolveWork.Contains("playlist_resolve_exact_path_query"));
            Assert.IsTrue(resolveWork.Contains("playlist_resolve_delta_apply"));
            Assert.IsTrue(resolveWork.Contains("playlist_resolve_root_capture"));
        });
    }

    [TestMethod]
    public void BuildInlineChartInfo_ShaOnlyChangeUpdatesShaLookupAndResourceHealthWithoutPrimaryLookupRebuild()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            new BmsLibraryDbGateway(songDbPath).EnsureChartInfoSchema();
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "InlineShaOnly");
            Directory.CreateDirectory(chartDirectory);
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE sha-only update\r\n#BPM 120\r\n#00111:01\r\n",
                System.Text.Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            string staleSha256 = new string('b', 64);
            var bmsFile = CreateFile(snapshot.Md5, chartPath, staleSha256);
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            InstalledChartLookupIndexSnapshot initialLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            OwnedChartHashIndexVersionedSnapshot initialSummary = library.GetOwnedChartHashIndexSnapshot();
            EnsureCurrentResourceHealthIndex(library);

            ChartInfoInlineBuildResult result = InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
                library,
                "test_inline_sha_only",
                [ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false)]);

            InstalledChartLookupIndexSnapshot updatedLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            OwnedChartHashIndexVersionedSnapshot updatedSummary = library.GetOwnedChartHashIndexSnapshot();
            LibraryChartDigestChange digestChange = result.DigestChanges.Single();
            Assert.IsFalse(digestChange.PrimaryHashChanged);
            Assert.IsFalse(digestChange.Md5Changed);
            Assert.IsTrue(digestChange.Sha256Changed);
            Assert.AreEqual(snapshot.Md5, digestChange.OldMd5);
            Assert.AreEqual(snapshot.Md5, digestChange.NewMd5);
            Assert.AreEqual(staleSha256, digestChange.OldSha256);
            Assert.AreEqual(snapshot.Sha256, digestChange.NewSha256);
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(snapshot.Md5));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(snapshot.Md5));
            Assert.IsFalse(updatedLookup.Sha256Directories.ContainsKey(staleSha256));
            Assert.IsTrue(updatedLookup.Sha256Directories.ContainsKey(snapshot.Sha256));
            CollectionAssert.AreEqual(new[] { chartDirectory }, initialLookup.Sha256Directories[staleSha256].ToArray());
            CollectionAssert.AreEqual(new[] { chartDirectory }, updatedLookup.Sha256Directories[snapshot.Sha256].ToArray());
            Assert.AreEqual(1, initialLookup.GetPrimaryHashCount(snapshot.Md5));
            Assert.AreEqual(1, updatedLookup.GetPrimaryHashCount(snapshot.Md5));
            Assert.AreEqual(2, initialLookup.DirectoryReferenceCount);
            Assert.AreEqual(2, updatedLookup.DirectoryReferenceCount);
            Assert.IsTrue(initialLookup.Sha256Directories.ContainsKey(staleSha256));
            Assert.IsTrue(initialSummary.Md5Hashes.Contains(snapshot.Md5));
            Assert.IsTrue(updatedSummary.Md5Hashes.Contains(snapshot.Md5));
            Assert.IsFalse(updatedSummary.Sha256Hashes.Contains(staleSha256));
            Assert.IsTrue(updatedSummary.Sha256Hashes.Contains(snapshot.Sha256));
            Assert.AreEqual(1, initialSummary.GetMd5OwnerCount(snapshot.Md5));
            Assert.AreEqual(1, updatedSummary.GetMd5OwnerCount(snapshot.Md5));
            Assert.AreEqual(1, initialSummary.GetSha256OwnerCount(staleSha256));
            Assert.AreEqual(0, updatedSummary.GetSha256OwnerCount(staleSha256));
            Assert.AreEqual(1, updatedSummary.GetSha256OwnerCount(snapshot.Sha256));
            Assert.AreNotEqual(initialSummary.Version, updatedSummary.Version);
            Assert.AreEqual(1, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
        });
    }

    [TestMethod]
    public void BuildInlineChartInfo_StorageFailureDoesNotPublishDigestIndexSessionIndexOrWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            new BmsLibraryDbGateway(songDbPath).EnsureChartInfoSchema();
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "InlineFailure");
            Directory.CreateDirectory(chartDirectory);
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE rollback\r\n#BPM 120\r\n#00111:01\r\n",
                System.Text.Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath, null);
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            InstalledChartLookupIndexSnapshot initialLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            OwnedChartHashIndexVersionedSnapshot initialSummary = library.GetOwnedChartHashIndexSnapshot();
            int initialNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int initialCollectionVersion = library.OwnedChartCollectionVersion;
            int initialChartInfoIndexVersion = library.ChartInfoIndexVersion;
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.Execute("CREATE TRIGGER fail_inline_chart_info BEFORE INSERT ON chart_info BEGIN SELECT RAISE(ABORT, 'forced inline chart-info failure'); END;");
            }

            Assert.ThrowsException<SQLite.SQLiteException>(() =>
                InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
                    library,
                    "test_inline_storage_failure",
                    [ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false)]));

            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsFile.hash);
            Assert.IsTrue(string.IsNullOrWhiteSpace(bmsFile.sha256));
            Assert.AreEqual(initialNotificationVersion, library.NormalLibraryRefreshNotificationVersion);
            Assert.AreEqual(initialCollectionVersion, library.OwnedChartCollectionVersion);
            Assert.AreEqual(initialChartInfoIndexVersion, library.ChartInfoIndexVersion);
            Assert.IsNull(library.ResolveChartInfo(snapshot.Sha256, snapshot.Md5));
            CollectionAssert.AreEquivalent(
                initialSummary.Md5Hashes.ToArray(),
                library.GetOwnedChartHashIndexSnapshot().Md5Hashes.ToArray());
            Assert.IsTrue(initialLookup.ContainsPrimaryHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.IsTrue(InvokeCreateInstalledChartLookupSnapshot(library)
                .ContainsPrimaryHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verify.Table<LR2SongDB.song>().Count(row => row.path == chartPath));
            Assert.AreEqual(0, verify.Table<LR2SongDBExtended.chart_info>().Count());
        });
    }

    [TestMethod]
    public void BuildInlineChartInfo_UpsertsSongRowsWithAppliedChartInfoColumns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            new BmsLibraryDbGateway(songDbPath).EnsureChartInfoSchema();
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "InlineChartInfo");
            Directory.CreateDirectory(chartDirectory);
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE inline chart info\r\n#ARTIST artist\r\n#BPM 180\r\n#PLAYLEVEL 12\r\n#DIFFICULTY 4\r\n#00111:01\r\n",
                System.Text.Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            var bmsFile = CreateFile(snapshot.Md5, chartPath, snapshot.Sha256);
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            SetDuplicateChartGroupsWithoutNotification(library, []);

            ChartInfoInlineBuildResult result = InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
                library,
                "test_inline_chart_info_song_row",
                [ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false)]);

            Assert.AreEqual(1, result.AppliedRows.Count);
            Assert.AreEqual(12, bmsFile.level);
            Assert.AreEqual(4, bmsFile.difficulty);
            Assert.AreEqual(180, bmsFile.maxbpm);
            Assert.AreEqual(180, bmsFile.minbpm);
            Assert.IsTrue(bmsFile.karinotes > 0);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single(candidate => candidate.path == chartPath);
            Assert.AreEqual(12, row.level);
            Assert.AreEqual(4, row.difficulty);
            Assert.AreEqual(180, row.maxbpm);
            Assert.AreEqual(180, row.minbpm);
            Assert.IsTrue(row.karinotes > 0);
        });
    }

    [TestMethod]
    public void LibraryChartDigestChange_PathlessOwnedEventThrows()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            new LibraryChartDigestChange(
                LibraryChartKind.Bms,
                null,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                null,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                null));
    }

    [TestMethod]
    public void LibraryChartDigestChange_Md5lessExplicitOwnedEventThrows()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            new LibraryChartDigestChange(
                LibraryChartKind.Bms,
                Path.Combine("C:\\Installed", "Bms", "chart.bms"),
                null,
                new string('a', 64),
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                new string('b', 64)));
        Assert.ThrowsException<InvalidOperationException>(() =>
            new LibraryChartDigestChange(
                LibraryChartKind.Bmson,
                Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                new string('a', 64),
                null,
                new string('b', 64)));
    }

    [TestMethod]
    public void LibraryChartDigestChange_FactorySkipsInvalidProjectionEvent()
    {
        string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var bmsFile = CreateFile(null, Path.Combine("C:\\Installed", "Bms", "chart.bms"), new string('b', 64));
        var pathlessBmsFile = CreateFile(md5, null, new string('d', 64));
        var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), null);
        bmsonSong.sha256 = new string('c', 64);
        var pathlessBmsonSong = CreateBmsonSong(null, md5);
        pathlessBmsonSong.sha256 = new string('e', 64);

        Assert.IsNull(LibraryChartDigestChange.FromBms(bmsFile, null, null));
        Assert.IsNull(LibraryChartDigestChange.FromBms(pathlessBmsFile, md5, null));
        Assert.IsNull(LibraryChartDigestChange.FromBmson(bmsonSong, null, null));
        Assert.IsNull(LibraryChartDigestChange.FromBmson(pathlessBmsonSong, md5, null));
    }

    [TestMethod]
    public void LibraryChartDigestChange_PrimaryHashChangedUsesMd5Only()
    {
        var digestChange = new LibraryChartDigestChange(
            LibraryChartKind.Bms,
            Path.Combine("C:\\Installed", "Bms", "chart.bms"),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            new string('a', 64),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            new string('b', 64));

        Assert.IsFalse(digestChange.PrimaryHashChanged);
        Assert.IsTrue(digestChange.Sha256Changed);
    }


}
