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
    /// <summary>DB確定後に所持集合、MD5索引、リソース健全性、譜面情報通知を同じdigest差分で更新する。</summary>
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

    /// <summary>背景譜面を含む2回の導入後補完で、局所差分・索引のreadback・通知前のMD5/SHA-256解決を確認する。</summary>
    [DataTestMethod]
    [DataRow(16)]
    public void BuildInlineChartInfo_WarmDigestDeltaStaysLocalAcrossTwoOperations(int backgroundCount)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            new BmsLibraryDbGateway(songDbPath).EnsureChartInfoSchema();
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "InlineLocalWork");
            Directory.CreateDirectory(chartDirectory);
            string firstPath = Path.Combine(chartDirectory, "first.bms");
            string secondPath = Path.Combine(chartDirectory, "second.bms");
            File.WriteAllText(
                firstPath,
                "#PLAYER 1\r\n#TITLE first before\r\n#BPM 120\r\n#00111:01\r\n",
                System.Text.Encoding.ASCII);
            File.WriteAllText(
                secondPath,
                "#PLAYER 1\r\n#TITLE second before\r\n#BPM 120\r\n#00111:01\r\n",
                System.Text.Encoding.ASCII);
            BMSFile first = BMSFile.CreateBMSFileFromFile(firstPath);
            BMSFile second = BMSFile.CreateBMSFileFromFile(secondPath);
            List<BMSFile> files = [first, second];
            for (int index = 0; index < backgroundCount; index++)
            {
                string backgroundPath = Path.Combine(chartDirectory, "background-" + index + ".bms");
                File.WriteAllText(
                    backgroundPath,
                    "#PLAYER 1\r\n#TITLE background " + index + "\r\n#BPM 120\r\n#00111:01\r\n",
                    System.Text.Encoding.ASCII);
                files.Add(BMSFile.CreateBMSFileFromFile(backgroundPath));
            }

            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, files);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            SetDuplicateChartGroupsWithoutNotification(library, []);
            InstalledChartLookupIndexSnapshot initialInstalled = InvokeCreateInstalledChartLookupSnapshot(library);
            OwnedChartHashIndexVersionedSnapshot initialHash = library.GetOwnedChartHashIndexSnapshot();
            PlaylistLibraryResolveIndexSnapshot initialPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out _,
                out _);
            EnsureCurrentResourceHealthIndex(library);

            List<string> hashWork = [];
            List<string> installedWork = [];
            List<string> playlistWork = [];
            library.OwnedChartHashIndexStoreWorkObserver = hashWork.Add;
            library.InstalledChartLookupStoreWorkObserver = installedWork.Add;
            library.PlaylistLibraryResolveIndexStoreWorkObserver = playlistWork.Add;
            string expectedNotificationMd5 = string.Empty;
            string expectedNotificationSha256 = string.Empty;
            int notificationPhase = 0;
            bool firstDigestVisibleAtNotification = false;
            bool secondDigestVisibleAtNotification = false;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion)
                    || string.IsNullOrWhiteSpace(expectedNotificationMd5))
                {
                    return;
                }

                PlaylistLibraryResolveIndexSnapshot notifiedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool notifiedCacheHit,
                    out int notifiedStaleRetries);
                LibraryChartRef? notifiedByMd5 = notifiedPlaylist.ResolveChartForPlaylistHash(expectedNotificationMd5, null);
                LibraryChartRef? notifiedBySha256 = notifiedPlaylist.ResolveChartForPlaylistHash(null, expectedNotificationSha256);
                bool visible = string.Equals(notifiedByMd5?.Path, notificationPhase == 1 ? firstPath : secondPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(notifiedBySha256?.Path, notificationPhase == 1 ? firstPath : secondPath, StringComparison.OrdinalIgnoreCase);
                if (notificationPhase == 1)
                {
                    firstDigestVisibleAtNotification |= visible;
                }
                else if (notificationPhase == 2)
                {
                    secondDigestVisibleAtNotification |= visible;
                }
            };

            string firstOldMd5 = first.hash;
            string firstOldSha256 = first.sha256;
            int firstNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            File.WriteAllText(
                firstPath,
                "#PLAYER 1\r\n#TITLE first after\r\n#BPM 120\r\n#00111:01\r\n",
                System.Text.Encoding.ASCII);
            ChartFileSnapshot firstAfterSnapshot = ChartFileContentReader.ReadSnapshot(firstPath);
            expectedNotificationMd5 = firstAfterSnapshot.Md5;
            expectedNotificationSha256 = firstAfterSnapshot.Sha256;
            notificationPhase = 1;
            ChartInfoInlineBuildResult firstResult = InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
                library,
                "test_inline_local_first",
                [ChartFileProjection.FromBmsFile(first, includeWarningSnapshot: false)]);
            OwnedChartHashIndexVersionedSnapshot firstHash = library.GetOwnedChartHashIndexSnapshot();
            OwnedChartHashIndexVersionedSnapshot firstHashReadback = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot firstInstalled = InvokeCreateInstalledChartLookupSnapshot(library);
            InstalledChartLookupIndexSnapshot firstInstalledReadback = InvokeCreateInstalledChartLookupSnapshot(library);
            PlaylistLibraryResolveIndexSnapshot firstPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool firstCacheHit,
                out int firstStaleRetries);
            PlaylistLibraryResolveIndexSnapshot firstPlaylistReadback = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool firstReadbackCacheHit,
                out int firstReadbackStaleRetries);
            NormalLibraryRefreshNotificationBatch firstBatch = library.GetNormalLibraryRefreshNotificationsAfter(firstNotificationVersion);
            string firstNewMd5 = first.hash;
            string firstNewSha256 = first.sha256;

            Assert.AreEqual(1, firstResult.DigestChanges.Count);
            Assert.AreEqual(firstOldMd5, firstResult.DigestChanges.Single().OldMd5);
            Assert.AreEqual(firstOldSha256, firstResult.DigestChanges.Single().OldSha256);
            Assert.AreEqual(firstNewMd5, firstResult.DigestChanges.Single().NewMd5);
            Assert.AreNotEqual(firstOldMd5, firstNewMd5);
            Assert.AreNotEqual(firstOldSha256, firstNewSha256);
            Assert.IsFalse(firstHash.ContainsMd5(firstOldMd5));
            Assert.IsTrue(firstHash.ContainsMd5(firstNewMd5));
            Assert.AreEqual(firstHash.Version, firstHashReadback.Version);
            Assert.IsFalse(firstInstalled.ContainsPrimaryHash(firstOldMd5));
            Assert.IsTrue(firstInstalled.ContainsPrimaryHash(firstNewMd5));
            Assert.AreEqual(firstInstalled.HashCount, firstInstalledReadback.HashCount);
            Assert.IsTrue(firstCacheHit);
            Assert.IsTrue(firstReadbackCacheHit);
            Assert.AreEqual(0, firstStaleRetries);
            Assert.AreEqual(0, firstReadbackStaleRetries);
            Assert.IsNull(firstPlaylist.ResolveChartForPlaylistHash(firstOldMd5, null));
            Assert.AreEqual(firstPath, firstPlaylist.ResolveChartForPlaylistHash(firstNewMd5, null).Path);
            Assert.AreEqual(firstPlaylist.Version, firstPlaylistReadback.Version);
            Assert.IsTrue(firstBatch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(firstDigestVisibleAtNotification, "通知時点でMD5とSHA-256の両方から新しい譜面へ解決できること。");

            string secondOldMd5 = second.hash;
            string secondOldSha256 = second.sha256;
            int secondNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            File.WriteAllText(
                secondPath,
                "#PLAYER 1\r\n#TITLE second after\r\n#BPM 120\r\n#00111:01\r\n",
                System.Text.Encoding.ASCII);
            ChartFileSnapshot secondAfterSnapshot = ChartFileContentReader.ReadSnapshot(secondPath);
            expectedNotificationMd5 = secondAfterSnapshot.Md5;
            expectedNotificationSha256 = secondAfterSnapshot.Sha256;
            notificationPhase = 2;
            ChartInfoInlineBuildResult secondResult = InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
                library,
                "test_inline_local_second",
                [ChartFileProjection.FromBmsFile(second, includeWarningSnapshot: false)]);
            OwnedChartHashIndexVersionedSnapshot secondHash = library.GetOwnedChartHashIndexSnapshot();
            OwnedChartHashIndexVersionedSnapshot secondHashReadback = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot secondInstalled = InvokeCreateInstalledChartLookupSnapshot(library);
            InstalledChartLookupIndexSnapshot secondInstalledReadback = InvokeCreateInstalledChartLookupSnapshot(library);
            PlaylistLibraryResolveIndexSnapshot secondPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool secondCacheHit,
                out int secondStaleRetries);
            PlaylistLibraryResolveIndexSnapshot secondPlaylistReadback = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool secondReadbackCacheHit,
                out int secondReadbackStaleRetries);
            NormalLibraryRefreshNotificationBatch secondBatch = library.GetNormalLibraryRefreshNotificationsAfter(secondNotificationVersion);
            string secondNewMd5 = second.hash;
            string secondNewSha256 = second.sha256;

            Assert.AreEqual(1, secondResult.DigestChanges.Count);
            Assert.AreEqual(secondOldMd5, secondResult.DigestChanges.Single().OldMd5);
            Assert.AreEqual(secondOldSha256, secondResult.DigestChanges.Single().OldSha256);
            Assert.AreEqual(secondNewMd5, secondResult.DigestChanges.Single().NewMd5);
            Assert.AreNotEqual(secondOldMd5, secondNewMd5);
            Assert.AreNotEqual(secondOldSha256, secondNewSha256);
            Assert.IsTrue(secondHash.ContainsMd5(firstNewMd5));
            Assert.IsTrue(secondHash.ContainsMd5(secondNewMd5));
            Assert.IsFalse(secondHash.ContainsMd5(secondOldMd5));
            Assert.AreEqual(secondHash.Version, secondHashReadback.Version);
            Assert.IsTrue(secondInstalled.ContainsPrimaryHash(firstNewMd5));
            Assert.IsTrue(secondInstalled.ContainsPrimaryHash(secondNewMd5));
            Assert.IsFalse(secondInstalled.ContainsPrimaryHash(secondOldMd5));
            Assert.AreEqual(secondInstalled.HashCount, secondInstalledReadback.HashCount);
            Assert.IsTrue(secondCacheHit);
            Assert.IsTrue(secondReadbackCacheHit);
            Assert.AreEqual(0, secondStaleRetries);
            Assert.AreEqual(0, secondReadbackStaleRetries);
            Assert.AreEqual(firstPath, secondPlaylist.ResolveChartForPlaylistHash(firstNewMd5, null).Path);
            Assert.AreEqual(secondPath, secondPlaylist.ResolveChartForPlaylistHash(secondNewMd5, null).Path);
            Assert.IsNull(secondPlaylist.ResolveChartForPlaylistHash(secondOldMd5, null));
            Assert.AreEqual(secondPlaylist.Version, secondPlaylistReadback.Version);
            Assert.IsTrue(secondBatch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(secondDigestVisibleAtNotification, "通知時点でMD5とSHA-256の両方から新しい譜面へ解決できること。");

            Assert.IsTrue(initialHash.ContainsMd5(firstOldMd5));
            Assert.IsTrue(initialHash.ContainsMd5(secondOldMd5));
            Assert.IsTrue(initialInstalled.ContainsPrimaryHash(firstOldMd5));
            Assert.IsTrue(initialInstalled.ContainsPrimaryHash(secondOldMd5));
            Assert.AreEqual(firstPath, initialPlaylist.ResolveChartForPlaylistHash(firstOldMd5, null).Path);
            Assert.AreEqual(secondPath, initialPlaylist.ResolveChartForPlaylistHash(secondOldMd5, null).Path);
            Assert.IsTrue(firstHash.ContainsMd5(secondOldMd5));
            Assert.IsTrue(firstInstalled.ContainsPrimaryHash(secondOldMd5));
            Assert.AreEqual(secondPath, firstPlaylist.ResolveChartForPlaylistHash(secondOldMd5, null).Path);

            Assert.AreEqual(2, hashWork.Count(operation => operation == "owned_hash_delta_apply"));
            Assert.AreEqual(0, hashWork.Count(operation => operation == "owned_hash_full_invalidate"));
            Assert.AreEqual(0, installedWork.Count(operation => operation == "installed_root_map_enumeration"));
            Assert.AreEqual(0, installedWork.Count(operation => operation == "installed_root_map_key_visited"));
            Assert.IsTrue(installedWork.Contains("installed_directory_bucket_update"));
            Assert.AreEqual(2, playlistWork.Count(operation => operation == "playlist_resolve_delta_apply"));
            Assert.AreEqual(0, playlistWork.Count(operation => operation == "playlist_resolve_source_enumeration"));
            Assert.AreEqual(0, playlistWork.Count(operation => operation == "playlist_resolve_full_root_enumeration"));
            Assert.AreEqual(0, playlistWork.Count(operation => operation == "playlist_resolve_full_root_key_visited"));
            Assert.AreEqual(0, playlistWork.Count(operation => operation == "playlist_resolve_source_entry_visited"));
        });
    }

    /// <summary>SHA-256だけが変わる既存MD5では、主索引を再構築せずSHA参照と健全性だけを更新する。</summary>
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

    /// <summary>実DB保存の失敗時はdigest差分、各索引、譜面情報、警告を一つも公開しない。</summary>
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

}
