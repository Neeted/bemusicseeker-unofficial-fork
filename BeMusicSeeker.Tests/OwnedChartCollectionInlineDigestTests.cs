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

    /// <summary>U4a-LocalWork: 背景16/128件で固定差分のinlineを2操作続け、各後続readbackを確認する。</summary>
    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
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

            string firstOldMd5 = first.hash;
            string firstOldSha256 = first.sha256;
            int firstNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            File.WriteAllText(
                firstPath,
                "#PLAYER 1\r\n#TITLE first after\r\n#BPM 120\r\n#00111:01\r\n",
                System.Text.Encoding.ASCII);
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

            string secondOldMd5 = second.hash;
            string secondOldSha256 = second.sha256;
            int secondNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            File.WriteAllText(
                secondPath,
                "#PLAYER 1\r\n#TITLE second after\r\n#BPM 120\r\n#00111:01\r\n",
                System.Text.Encoding.ASCII);
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
