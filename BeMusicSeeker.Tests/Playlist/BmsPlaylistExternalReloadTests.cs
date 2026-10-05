using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using static BeMusicSeeker.Tests.BmsPlaylistTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
// Arbitrary filtered Quick runs share one testhost. This fixture mutates the
// process-global playlist URL completion, LR2 mode/root/output paths, Beatoraja
// output settings, and IR flag in Settings.Default.
[DoNotParallelize]
public sealed class BmsPlaylistExternalReloadTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LocalRecommendations_KeepUserStateAndAvoidUnchangedWritesThenReachLr2AndBmt()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        try
        {
            string songPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            var repository = new PlaylistPersistenceRepository(songPath);
            int reads = 0;
            string selectedInput = "mixed";
            CustomFolderOutputSettingsSnapshot settings = new() { ShowRecommUpdatedMsg = true };
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(songPath, CustomFolderOutputPhysicalSurface.Empty);
            playlist = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => settings,
                synchronization, recommendationScoreReader: _ =>
                {
                    reads++;
                    return Task.FromResult(ReadWalkureInput(selectedInput));
                });
            using PlaylistOperationNotificationOwner.OperationNotificationSession session = playlist.OperationNotificationOwner.BeginSession();
            Uri[] uris = [new("bmseeker:table.recommended"), new("bmseeker:table.recommended?base=failed"), new("bmseeker:table.recommended?failed=noplay")];
            List<PlaylistExternalSyncOwner.PlaylistExternalTableLoadResult> loaded = await playlist.ExternalSyncOwner.LoadExternalTableSnapshotsAsync(uris.Select(uri => new BMSTable { Page_url = uri }), false);
            Assert.AreEqual(1, reads);
            await playlist.ExternalSyncOwner.RegistrateExternalTablesAsync(loaded.Select(item => item.ExternalTable), false, "local-recommendations");
            Assert.AreEqual(3, playlist.BMSTables.Count);
            Assert.IsTrue(session.TakeReceipt().IsEmpty, "取得・新規登録だけでは実力変化を通知しません。");
            BMSTable original = playlist.BMSTables.Single(table => table.Page_url == uris[0]);
            original.name = "My recommendations";
            original.last_update = new DateTime(2024, 6, 1, 10, 20, 30);
            original.symbol = "CUSTOM";
            original.compat_prefix = "KEEP ";
            original.Output_dir = "LocalRecommendations";
            original.custom_folder_output_base_name = "CustomName";
            original.bmt_sort = 42;
            original.is_bmt_output = false;
            original.entries[0].memo = "keep-user-memo";
            playlist.CommitBMSTableWithEntriesToDB(original);
            int? id = original.playlist_id;
            DateTime updatedAt = original.last_update;
            string hash = original.data_sha256;
            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> unchanged = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(playlist.BMSTables.ToArray());
            Assert.AreEqual(2, reads);
            Assert.IsTrue(unchanged.All(item => item.Succeeded && !item.Updated));
            Assert.IsTrue(unchanged.Single(item => item.ResultTable.playlist_id == id).StatePersisted);
            Assert.IsTrue(unchanged.Where(item => item.ResultTable.playlist_id != id).All(item => !item.StatePersisted));
            string mixedName = string.Format(Resources.RecommendFormat, Resources.Recommended_standard,
                ReadWalkureCase("mixed")["rating"]!.Value<double>("playerStarRating").ToString("F2", CultureInfo.InvariantCulture));
            BMSTable renamed = playlist.BMSTables.Single(table => table.playlist_id == id);
            Assert.AreEqual(mixedName, renamed.name);
            Assert.AreEqual(mixedName, renamed.org_name);
            Assert.AreEqual(0, synchronization.Operations.Count);
            Assert.IsTrue(session.TakeReceipt().IsEmpty);
            BMSTable persisted = repository.LoadPlaylistHeaders().Single(table => table.playlist_id == id);
            Assert.AreEqual(updatedAt, persisted.last_update);
            Assert.AreEqual(hash, persisted.data_sha256);
            Assert.AreEqual(mixedName, persisted.name);
            Assert.AreEqual(mixedName, persisted.org_name);
            Assert.AreEqual("CUSTOM", persisted.symbol);
            Assert.AreEqual("KEEP ", persisted.compat_prefix);
            Assert.AreEqual("LocalRecommendations", persisted.Output_dir);
            Assert.AreEqual("CustomName", persisted.custom_folder_output_base_name);
            Assert.AreEqual(42, persisted.bmt_sort);
            Assert.IsFalse(persisted.is_bmt_output);
            persisted.entries = repository.LoadPersistedPlaylistEntries(id, false).ToList();
            Assert.IsTrue(persisted.entries.Any(entry => entry.memo == "keep-user-memo"));

            // 数値を変えず org_name だけ旧表記にした場合もヘッダーへ保存する。
            renamed.org_name = "旧方針 ★3.87";
            playlist.CommitBMSTableWithEntriesToDB(renamed);
            unchanged = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(playlist.BMSTables.ToArray());
            Assert.IsTrue(unchanged.Single(item => item.ResultTable.playlist_id == id).StatePersisted);
            persisted = repository.LoadPlaylistHeaders().Single(table => table.playlist_id == id);
            Assert.AreEqual(mixedName, persisted.org_name);
            Assert.AreEqual(mixedName, playlist.BMSTables.Single(table => table.playlist_id == id).org_name);
            Assert.AreEqual(updatedAt, persisted.last_update);
            Assert.IsTrue(session.TakeReceipt().IsEmpty);

            unchanged = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(playlist.BMSTables.ToArray());
            Assert.IsTrue(unchanged.All(item => item.Succeeded && !item.Updated && !item.StatePersisted));
            Assert.AreEqual(0, synchronization.Operations.Count);
            Assert.IsTrue(session.TakeReceipt().IsEmpty);

            selectedInput = "standard";
            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> changed = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(playlist.BMSTables.ToArray());
            Assert.AreEqual(5, reads);
            Assert.IsTrue(changed.All(item => item.Succeeded && item.Updated && item.StatePersisted));
            Assert.AreEqual(3, session.TakeReceipt().Notifications.Count);
            BMSTable updated = playlist.BMSTables.Single(table => table.playlist_id == id);
            string standardName = string.Format(Resources.RecommendFormat, Resources.Recommended_standard,
                ReadWalkureCase("standard")["rating"]!.Value<double>("playerStarRating").ToString("F2", CultureInfo.InvariantCulture));
            Assert.AreEqual(standardName, updated.name);
            Assert.AreEqual(standardName, updated.org_name);
            Assert.AreEqual("CUSTOM", updated.symbol);
            Assert.AreEqual("KEEP ", updated.compat_prefix);
            Assert.AreEqual("LocalRecommendations", updated.Output_dir);
            Assert.AreEqual(42, updated.bmt_sort);
            Assert.IsFalse(updated.is_bmt_output);
            Assert.IsTrue(repository.LoadPersistedPlaylistEntries(id, false).Any(entry => entry.memo == "keep-user-memo"));

            // 通知設定を無効にした、成功反映の次回操作でも実力更新通知を出しません。
            settings = new CustomFolderOutputSettingsSnapshot();
            selectedInput = "mixed";
            changed = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(playlist.BMSTables.ToArray());
            Assert.IsTrue(changed.All(item => item.Succeeded && item.Updated));
            Assert.IsTrue(session.TakeReceipt().IsEmpty);
            updated = playlist.BMSTables.Single(table => table.playlist_id == id);
            settings = new CustomFolderOutputSettingsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = directory,
                LR2CustomFolderOutputBaseDir = Path.Combine(directory, "Output"),
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(directory, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([updated], "local-recommendations-output");
            string lr2Path = Path.Combine(settings.LR2CustomFolderOutputBaseDir, updated.Output_dir, "0000.lr2folder");
            Assert.IsTrue(File.Exists(lr2Path));
            StringAssert.Contains(ReadShiftJisText(lr2Path), "#TITLE");
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                Assert.IsTrue(verify.Table<LR2SongDB.folder>().Any(row => row.path == lr2Path));
            }
            string bmtDirectory = Path.Combine(directory, "Bmt");
            string bmtName = BmtTableExportService.ExportTableData(bmtDirectory, BmtTableExportService.BuildTableData(updated), id.ToString());
            Assert.IsTrue(File.Exists(Path.Combine(bmtDirectory, bmtName)));
            // 既存 BMT 契約はタイトルを要求し、外部表のフォルダへタグを付けます。
            // 同じ譜面の複数目標も有効行ごとに、実際の圧縮出力から照合します。
            string[] expectedBmtRows = updated.entries.Where(entry => !entry.is_removed && !string.IsNullOrWhiteSpace(entry.title))
                .Select(entry => entry.md5 + ":CUSTOM" + entry.folder).ToArray();
            JObject actualBmt = BeatorajaBmtTableImportService.ReadCachedTableData(Path.Combine(bmtDirectory, bmtName));
            Assert.AreEqual(updated.Page_url.OriginalString, actualBmt.Value<string>("url"));
            Assert.AreEqual(mixedName, actualBmt.Value<string>("name"));
            Assert.AreEqual("CUSTOM", actualBmt.Value<string>("tag"));
            CollectionAssert.AreEqual(updated.folder_list.Select(folder => "CUSTOM" + folder).ToArray(),
                ((JArray)(actualBmt["folder"] ?? throw new FormatException())).Select(folder => folder.Value<string>("name")).ToArray());
            CollectionAssert.AreEquivalent(expectedBmtRows,
                ((JArray)(actualBmt["folder"] ?? throw new FormatException())).SelectMany(folder =>
                    ((JArray)(folder["songs"] ?? throw new FormatException())).Select(song => song.Value<string>("md5") + ":" + folder.Value<string>("name"))).ToArray());
        }
        finally
        {
            playlist?.RequestShutdown("local-recommendations-test-complete");
            Directory.Delete(directory, true);
        }
    }

    [DataTestMethod]
    [DataRow("旧方針 ★3.87", "standard", true, false, 1)]
    [DataRow("旧方針 ★6.6", "standard", true, false, 0)]
    [DataRow("旧方針 ★6.60", "mixed", true, false, 1)]
    [DataRow("手動名のみ", "standard", true, false, 0)]
    [DataRow("旧方針 ★3.87", "standard", false, false, 0)]
    [DataRow("旧方針 ★3.87", "standard", true, true, 0)]
    [TestCategory("Playlist")]
    public async Task LocalRecommendations_HashInitializationAndSaveFailureKeepSkillNotificationBoundary(
        string oldOrgName, string inputName, bool showNotification, bool failSave, int notificationCount)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistExternalReloadTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        const string triggerName = "recommendation_test_write_failure";
        try
        {
            string songPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            var repository = new PlaylistPersistenceRepository(songPath);
            playlist = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { ShowRecommUpdatedMsg = showNotification },
                recommendationScoreReader: _ => Task.FromResult(ReadWalkureInput(inputName)));
            BMSTable original = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri("bmseeker:table.recommended"));
            original.playlist_id = 9071;
            original.name = "Manual name ★999";
            original.org_name = oldOrgName;
            original.header_sha256 = null;
            original.data_sha256 = null;
            original.last_update = new DateTime(2024, 6, 1, 10, 20, 30);
            playlist.BMSTables = new ObservableCollection<BMSTable>([original]);
            playlist.CommitBMSTableWithEntriesToDB(original);
            BMSTable before = repository.LoadPlaylistHeaders().Single();
            using PlaylistOperationNotificationOwner.OperationNotificationSession notifications = playlist.OperationNotificationOwner.BeginSession();
            if (failSave)
            {
                using var writeFailure = new LR2SongDBExtended(songPath);
                writeFailure.Execute("CREATE TRIGGER " + triggerName + " BEFORE INSERT ON playlist WHEN NEW.playlist_id = 9071 "
                    + "BEGIN SELECT RAISE(ABORT, 'recommendation DB write failure'); END;");
            }
            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> results;
            try
            {
                results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([original]);
            }
            finally
            {
                if (failSave)
                {
                    using var cleanup = new LR2SongDBExtended(songPath);
                    cleanup.Execute("DROP TRIGGER " + triggerName + ";");
                }
            }
            PlaylistExternalSyncOwner.PlaylistReloadTargetResult result = results.Single();
            Assert.IsFalse(result.Updated);
            Assert.AreEqual(!failSave, result.Succeeded);
            Assert.AreEqual(!failSave, result.StatePersisted);
            Assert.AreEqual(original.last_update, result.ResultTable.last_update);
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            BMSTable persisted = repository.LoadPlaylistHeaders().Single();
            Assert.AreEqual(original.last_update, persisted.last_update);
            PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = notifications.TakeReceipt();
            Assert.AreEqual(notificationCount, receipt.Notifications.Count);
            if (failSave)
            {
                Assert.AreSame(original, playlist.BMSTables.Single());
                Assert.AreEqual(before.name, persisted.name);
                Assert.AreEqual(before.org_name, persisted.org_name);
                Assert.AreEqual(before.header_sha256, persisted.header_sha256);
                Assert.AreEqual(before.data_sha256, persisted.data_sha256);
                // 障害を除いた別操作を受理でき、元の操作の再試行は行わない。
                Assert.IsTrue((await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([original])).Single().Succeeded);
            }
            else
            {
                double expectedSkill = ReadWalkureCase(inputName)["rating"]!.Value<double>("playerStarRating");
                string expectedName = string.Format(Resources.RecommendFormat, Resources.Recommended_standard,
                    expectedSkill.ToString("F2", CultureInfo.InvariantCulture));
                Assert.AreEqual(expectedName, persisted.name);
                Assert.AreEqual(expectedName, persisted.org_name);
                Assert.AreEqual(expectedName, playlist.BMSTables.Single().name);
                Assert.AreEqual(expectedName, playlist.BMSTables.Single().org_name);
                Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.header_sha256));
                Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.data_sha256));
                Assert.AreEqual(original.entries.Count, repository.LoadPersistedPlaylistEntries(original.playlist_id, false).Count());
                if (notificationCount == 1)
                {
                    double oldSkill = inputName == "mixed" ? 6.60 : 3.87;
                    Assert.AreEqual(string.Format(Resources.Recommend_SkillUpdatedMessage,
                        expectedSkill.ToString("F2"), (expectedSkill - oldSkill).ToString(" (+#0.00); (-#0.00);")),
                        receipt.Notifications.Single().Message);
                }
            }
        }
        finally
        {
            playlist?.RequestShutdown("recommendation-hash-test-complete");
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Lr2FolderSync_InvokesMutationGuardBeforeOpeningDatabase()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "Output");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                songDbPath,
                CustomFolderOutputPhysicalSurface.Empty);
            synchronization.Failure = new InvalidOperationException(Resources.Warn_LibraryOperationBusy);
            var table = new BMSTable
            {
                playlist_id = 9001,
                name = "SyncMutationGuard",
                symbol = "SMG",
                Output_dir = "SyncMutationGuard",
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder")],
                Folder_order = ["Folder"]
            };
            BMSPlaylist playlist = CreatePlaylist(songDbPath, synchronization);
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() =>
                playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_mutation_guard"));

            CollectionAssert.AreEqual(new[] { "playlist_lr2folder_batch_sync" }, synchronization.Operations);
            Assert.AreEqual(Resources.Warn_LibraryOperationBusy, exception.Message);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Lr2FolderSync_RethrowsCapabilityFailure()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "Output");
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            var originalException = new InvalidOperationException("forced LR2 folder sync failure");
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                songDbPath,
                CustomFolderOutputPhysicalSurface.Empty);
            synchronization.Failure = originalException;
            var table = new BMSTable
            {
                playlist_id = 9002,
                name = "SyncCapabilityFailure",
                symbol = "SCF",
                Output_dir = "SyncCapabilityFailure",
                entries = [CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder")],
                Folder_order = ["Folder"]
            };
            BMSPlaylist playlist = CreatePlaylist(songDbPath, synchronization);
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            File.Delete(songDbPath);
            Directory.CreateDirectory(songDbPath);

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() =>
                playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_capability_failure"));

            Assert.AreSame(originalException, exception);
            CollectionAssert.AreEqual(new[] { "playlist_lr2folder_batch_sync" }, synchronization.Operations);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_PassesOldAndNewEntrySnapshotsToCallback()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"SnapshotTable\",\r\n\"symbol\":\"S\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Snapshot Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt receipt = null!;
            playlist.ExternalSyncOwner.PlaylistTableUpdateReceiptPublished += (_, eventArgs) =>
            {
                receipt = eventArgs.Receipt;
                throw new InvalidOperationException("receipt consumer failure");
            };

            List<BMSTable> updated = await playlist.ExternalSyncOwner.UpdateBMSTablesInternalAsync(
                reloadExtPlaylist: true,
                syncResultCallback: _ => throw new InvalidOperationException("sync result callback failure"),
                publishReferenceReceipts: true);

            Assert.IsNotNull(receipt);
            Assert.AreSame(table, receipt!.OldTable);
            Assert.IsNotNull(receipt.NewTable);
            Assert.IsNotNull(receipt.OldEntriesSnapshot);
            Assert.IsNotNull(receipt.NewEntriesSnapshot);
            Assert.AreEqual(1, receipt.OldEntriesSnapshot.Count);
            Assert.AreEqual(1, receipt.NewEntriesSnapshot.Count);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", receipt.OldEntriesSnapshot[0].md5);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", receipt.NewEntriesSnapshot[0].md5);
            Assert.AreEqual(new string('b', 64), receipt.NewEntriesSnapshot[0].sha256);
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_HashInitializationPersistsWithoutUpdatedFlag()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"HashInitTable\",\r\n\"symbol\":\"H\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Hash Init Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            table.name = "Manual generic name";
            table.playlist_id = 900001;
            table.header_sha256 = null;
            table.data_sha256 = null;
            var existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
            table.last_update = existingLastUpdate;
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.RunInTransaction(() =>
                {
                    db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                    foreach (BMSTableEntry entry in table.entries)
                    {
                        db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                    }
                });
            }
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Hash Init Song\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Repaired Song\",\"artist\":\"Artist\",\"level\":\"2\"}]"));
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt receipt = null!;
            playlist.ExternalSyncOwner.PlaylistTableUpdateReceiptPublished += (_, eventArgs) =>
            {
                receipt = eventArgs.Receipt;
            };

            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [table],
                reason: "test_hash_initialization",
                publishReferenceReceipts: true);

            Assert.AreEqual(1, results.Count);
            Assert.IsFalse(results[0].Updated);
            Assert.IsTrue(results[0].StatePersisted);
            Assert.IsNotNull(receipt);
            Assert.IsFalse(receipt!.Updated);
            Assert.IsTrue(receipt.ReferenceEntriesChanged);
            Assert.AreEqual(existingLastUpdate, results[0].ResultTable.last_update);
            Assert.IsFalse(string.IsNullOrWhiteSpace(results[0].ResultTable.header_sha256));
            Assert.IsFalse(string.IsNullOrWhiteSpace(results[0].ResultTable.data_sha256));
            Assert.AreEqual(2, results[0].ResultTable.entries.Count(entry => !entry.is_removed));
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.playlist persisted = verify.Table<LR2SongDBExtended.playlist>().Single(row => row.playlist_id == 900001);
            Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.header_sha256));
            Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.data_sha256));
            Assert.AreEqual(existingLastUpdate, persisted.last_update);
            Assert.AreEqual("Manual generic name", persisted.name);
            Assert.AreEqual("Manual generic name", results[0].ResultTable.name);
            Assert.AreEqual(2L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = 900001 AND is_removed = 0;"));
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_ReoutputsLr2FolderProjectionAfterPersistedEntryChange()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ExternalFolderProjection\",\r\n\"symbol\":\"E\",\r\n\"tag\":\"LEVEL \",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Projection Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = tempDirectory,
                LR2CustomFolderOutputBaseDir = outputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootCustomFolder"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => outputSettings,
                synchronization);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            table.playlist_id = 900002;
            table.Output_dir = "ExternalReloadProjection";
            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder;
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries)
                {
                    db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            playlist.ReOutputCustomFolder(table);

            string outputPath = Path.Combine(outputBaseDir, table.Output_dir, "0000.lr2folder");
            StringAssert.Contains(ReadShiftJisText(outputPath), "#TITLE LEVEL 1");
            using (var verifyInitial = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual("LEVEL 1", verifyInitial.Table<LR2SongDB.folder>().Single(row => row.path == outputPath).title);
            }
            synchronization.Operations.Clear();
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Projection Song\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [table],
                reason: "test_external_reload_custom_folder_projection");

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Succeeded);
            Assert.IsTrue(results[0].Updated);
            Assert.IsTrue(results[0].StatePersisted);
            BMSTable reloadedTable = results[0].ResultTable;
            Assert.AreSame(reloadedTable, playlist.BMSTables.Single());
            Assert.AreEqual("LEVEL 2", reloadedTable.entries.Single(entry => !entry.is_removed).folder);
            string reloadedText = ReadShiftJisText(outputPath);
            StringAssert.Contains(reloadedText, "#TITLE LEVEL 2");
            Assert.IsFalse(reloadedText.Contains("#TITLE LEVEL 1", StringComparison.Ordinal));
            CollectionAssert.AreEqual(new[] { "playlist_lr2folder_batch_sync" }, synchronization.Operations);
            using (var verifyReloaded = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(
                    "LEVEL 2",
                    verifyReloaded.ExecuteScalar<string>(
                        "SELECT folder FROM playlist_entry WHERE playlist_id = ? AND md5 = ? AND is_removed = 0;",
                        900002,
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
                LR2SongDB.folder folderRow = verifyReloaded.Table<LR2SongDB.folder>().Single(row => row.path == outputPath);
                Assert.AreEqual("LEVEL 2", folderRow.title);
                StringAssert.Contains(folderRow.command, "playlist_entry");
            }

            synchronization.Operations.Clear();
            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> unchangedResults = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [reloadedTable],
                reason: "test_external_reload_custom_folder_projection_no_change");

            Assert.AreEqual(1, unchangedResults.Count);
            Assert.IsTrue(unchangedResults[0].Succeeded);
            Assert.IsFalse(unchangedResults[0].Updated);
            Assert.IsFalse(unchangedResults[0].StatePersisted);
            Assert.AreEqual(0, synchronization.Operations.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task LocalPlaylistMutations_RejectBusyLeaseBeforeMutationAndRetryToConvergence()
    {
        foreach (LocalPlaylistMutationCase mutationCase in CreateLocalPlaylistMutationCases())
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            LibraryFileMutationLease? incumbent = null;
            try
            {
                string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
                string songDbPath = CreateTempSongDbPath(tempDirectory);
                PlaylistPersistenceRepository.EnsureSchema(songDbPath);
                CustomFolderOutputSettingsSnapshot outputSettings = CreateLocalMutationOutputSettings(tempDirectory, outputBaseDir, operationModeLr2Db: true);
                TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                    songDbPath,
                    CustomFolderOutputPhysicalSurface.Empty);
                var leaseProvider = new CountingMutationLeaseProvider();
                var playlist = new TestBmsPlaylist(
                    songDbPath,
                    null,
                    null,
                    null,
                    () => new PlaylistUrlCompletionOptionsSnapshot(),
                    () => new BeatorajaBmtOptionsSnapshot(),
                    () => outputSettings,
                    synchronization,
                    mutationLeaseProvider: leaseProvider.TryBegin);
                BMSTable table = CreateLocalMutationTable();
                playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
                playlist.CommitBMSTableWithEntriesToDB(table);
                playlist.ReOutputCustomFolder(table);
                leaseProvider.ResetCallCount();
                synchronization.Operations.Clear();

                PlaylistModelSnapshot modelBefore = CapturePlaylistModel(table);
                byte[] databaseBefore = File.ReadAllBytes(songDbPath);
                Dictionary<string, byte[]> filesBefore = CaptureFileSurface(outputBaseDir);
                incumbent = leaseProvider.AcquireIncumbent();

                Task<Exception?> busyCall = Task.Run(() => CaptureMutationFailure(mutationCase, playlist, table));
                Exception? busyException = await busyCall.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(busyException, mutationCase.Name + " must reject while the lease is busy.");
                Assert.AreEqual(2, leaseProvider.CallCount, mutationCase.Name + " should make one incumbent and one rejected admission attempt.");
                AssertPlaylistModelUnchanged(modelBefore, CapturePlaylistModel(table));
                CollectionAssert.AreEqual(databaseBefore, File.ReadAllBytes(songDbPath));
                AssertFileSurfaceUnchanged(filesBefore, CaptureFileSurface(outputBaseDir));
                Assert.AreEqual(0, synchronization.Operations.Count, mutationCase.Name + " must not reach LR2 output while admission is rejected.");

                incumbent.Dispose();
                incumbent = null;
                object retryResult = mutationCase.Invoke(playlist, table, true);
                Assert.IsTrue(mutationCase.WasApplied(retryResult), mutationCase.Name + " retry should apply the requested mutation.");
                Assert.AreEqual(3, leaseProvider.CallCount, mutationCase.Name + " retry should be admitted exactly once.");
                CollectionAssert.AreEqual(new[] { "playlist_lr2folder_batch_sync" }, synchronization.Operations);
                AssertPersistedEntriesMatchModel(table, songDbPath);
                Assert.IsTrue(CaptureFileSurface(outputBaseDir).Count > 0, mutationCase.Name + " retry should leave an app-managed custom-folder surface.");
            }
            finally
            {
                incumbent?.Dispose();
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void LocalPlaylistMutations_DoNotConsultLeaseForNoCommitOrNonLr2()
    {
        foreach (LocalPlaylistMutationCase mutationCase in CreateLocalPlaylistMutationCases())
        {
            foreach ((bool CommitFlag, bool OperationModeLr2Db) in new[]
            {
                (false, true),
                (true, false)
            })
            {
                string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);
                try
                {
                    string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
                    string songDbPath = CreateTempSongDbPath(tempDirectory);
                    PlaylistPersistenceRepository.EnsureSchema(songDbPath);
                    CustomFolderOutputSettingsSnapshot outputSettings = CreateLocalMutationOutputSettings(
                        tempDirectory,
                        outputBaseDir,
                        OperationModeLr2Db);
                    TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                        songDbPath,
                        CustomFolderOutputPhysicalSurface.Empty);
                    var leaseProvider = new CountingMutationLeaseProvider
                    {
                        DenyAll = true
                    };
                    var playlist = new TestBmsPlaylist(
                        songDbPath,
                        null,
                        null,
                        null,
                        () => new PlaylistUrlCompletionOptionsSnapshot(),
                        () => new BeatorajaBmtOptionsSnapshot(),
                        () => outputSettings,
                        synchronization,
                        mutationLeaseProvider: leaseProvider.TryBegin);
                    BMSTable table = CreateLocalMutationTable();
                    playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
                    playlist.CommitBMSTableWithEntriesToDB(table);
                    PlaylistModelSnapshot modelBefore = CapturePlaylistModel(table);
                    byte[] databaseBefore = File.ReadAllBytes(songDbPath);
                    Dictionary<string, byte[]> filesBefore = CaptureFileSurface(outputBaseDir);

                    object result = mutationCase.Invoke(playlist, table, CommitFlag);
                    Assert.IsTrue(mutationCase.WasApplied(result), mutationCase.Name + " negative control should apply the in-memory mutation.");
                    Assert.AreEqual(0, leaseProvider.CallCount, mutationCase.Name + " must not consult the lease provider for the selected negative control.");
                    Assert.IsFalse(
                        string.Equals(
                            string.Join("\u001f", modelBefore.EntryStates),
                            string.Join("\u001f", CapturePlaylistModel(table).EntryStates),
                            StringComparison.Ordinal),
                        mutationCase.Name + " negative control should change the model.");
                    AssertFileSurfaceUnchanged(filesBefore, CaptureFileSurface(outputBaseDir));
                    Assert.AreEqual(0, synchronization.Operations.Count);
                    if (!CommitFlag)
                    {
                        CollectionAssert.AreEqual(databaseBefore, File.ReadAllBytes(songDbPath));
                    }
                    else
                    {
                        AssertPersistedEntriesMatchModel(table, songDbPath);
                    }
                }
                finally
                {
                    if (Directory.Exists(tempDirectory))
                    {
                        Directory.Delete(tempDirectory, recursive: true);
                    }
                }
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_ReportsPlaylistSyncProgressSnapshots()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ProgressTable\",\r\n\"symbol\":\"P\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Progress Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });

            List<PlaylistSyncProgressSnapshot> snapshots = [];

            List<BMSTable> updated = await playlist.ExternalSyncOwner.UpdateBMSTablesInternalAsync(
                reloadExtPlaylist: true,
                syncResultCallback: null,
                progressCallback: snapshots.Add);

            Assert.IsNotNull(updated);
            Assert.IsTrue(snapshots.Count >= 3);
            Assert.IsTrue(snapshots.Any(s => s.IsActive && s.TotalTableCount == 1 && s.CompletedTableCount == 0));
            Assert.IsTrue(snapshots.Any(s => s.IsActive && s.TotalTableCount == 1 && s.CompletedTableCount == 1 && string.Equals(s.CurrentTableName, "ProgressTable", StringComparison.Ordinal)));
            PlaylistSyncProgressSnapshot playlistSyncProgressSnapshot = snapshots.Last();
            Assert.IsFalse(playlistSyncProgressSnapshot.IsActive);
            Assert.AreEqual(1, playlistSyncProgressSnapshot.TotalTableCount);
            Assert.AreEqual(1, playlistSyncProgressSnapshot.CompletedTableCount);
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_ReloadsOnlyExternalSyncTargets()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string externalHeaderPath = Path.Combine(tempDirectory, "external-header.json");
            string externalScorePath = Path.Combine(tempDirectory, "external-score.json");
            string manualHeaderPath = Path.Combine(tempDirectory, "manual-header.json");
            File.WriteAllBytes(externalHeaderPath, CreateUtf8BomBytes("{\r\n\"name\":\"ExternalTarget\",\r\n\"symbol\":\"E\",\r\n\"data_url\":\"./external-score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(externalScorePath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"External Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));
            File.WriteAllBytes(manualHeaderPath, CreateUtf8BomBytes("{ invalid json"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty));
            BMSTable externalTable = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(externalHeaderPath));
            externalTable.EnableExternalSync();
            var manualTable = new BMSTable
            {
                name = "ManualTarget",
                symbol = "M",
                Header_url = new Uri(manualHeaderPath)
            };
            manualTable.DisableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { externalTable, manualTable });
            List<PlaylistSyncAttemptResult> syncResults = [];

            await playlist.ExternalSyncOwner.UpdateBMSTablesInternalAsync(
                reloadExtPlaylist: true,
                syncResultCallback: syncResults.Add);

            Assert.AreEqual(1, syncResults.Count);
            Assert.AreSame(externalTable, syncResults[0].SourceTable);
            Assert.IsTrue(syncResults[0].Succeeded);
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_ReloadsExplicitTargetRegardlessOfExternalSyncFlag()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ManualTarget\",\r\n\"symbol\":\"M\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 9001;
            table.DisableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> results = await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([table], reason: "test_explicit_reload");

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Succeeded);
            Assert.IsTrue(results[0].Updated);
            Assert.IsFalse(results[0].ResultTable.is_external_sync);
            using (results[0].ResultTable.ReaderWriterLock.GetReaderGuard())
            {
                Assert.AreEqual(2, results[0].ResultTable.entries.Count(entry => !entry.is_removed));
            }
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadAndApplySingleTable_UsesExplicitUriOverride()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        string firstDirectory = Path.Combine(tempDirectory, "first");
        string secondDirectory = Path.Combine(tempDirectory, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        try
        {
            string firstHeaderPath = Path.Combine(firstDirectory, "header.json");
            string firstScorePath = Path.Combine(firstDirectory, "score.json");
            string secondHeaderPath = Path.Combine(secondDirectory, "header.json");
            string secondScorePath = Path.Combine(secondDirectory, "score.json");
            const string headerJson = "{\r\n\"name\":\"ExplicitUriTarget\",\r\n\"symbol\":\"E\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}";
            File.WriteAllBytes(firstHeaderPath, CreateUtf8BomBytes(headerJson));
            File.WriteAllBytes(secondHeaderPath, CreateUtf8BomBytes(headerJson));
            File.WriteAllBytes(firstScorePath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));
            File.WriteAllBytes(secondScorePath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(firstHeaderPath));
            table.playlist_id = 9002;
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });

            BMSTable result = await playlist.ExternalSyncOwner.ReloadAndApplySingleTableAsync(
                table,
                new Uri(secondHeaderPath),
                "test_explicit_uri");

            using (result.ReaderWriterLock.GetReaderGuard())
            {
                Assert.AreEqual(2, result.entries.Count(entry => !entry.is_removed));
            }
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_AwaitsUiReplacementCompletionBeforeApplying(bool recommendation)
    {
        (string tempDirectory, TestBmsPlaylist playlist, BMSTable table, ControlledUiScheduler scheduler) =
            await CreateUiReplacementReloadFixtureAsync(UiScheduleOutcome.AcceptedPending, recommendation);
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        Task<List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult>>? reloadTask = null;
        Task? competingRestore = null;
        using PlaylistOperationNotificationOwner.OperationNotificationSession notifications = playlist.OperationNotificationOwner.BeginSession();
        try
        {
            reloadTask =
                playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                    [table],
                    reason: "test_ui_replacement_completion");

            Task firstTerminal = await Task.WhenAny(scheduler.Scheduled, reloadTask);
            Assert.AreSame(scheduler.Scheduled, firstTerminal);
            await scheduler.Scheduled;
            Assert.IsFalse(reloadTask.IsCompleted);
            Assert.AreSame(table, playlist.BMSTables.Single());
            Assert.AreEqual(0, scheduler.ExecutionCount);
            Assert.IsTrue(notifications.TakeReceipt().IsEmpty);
            // U2-R2: 個別 reload の実 pending publication がある間、復元は DB 更新前に拒否する。
            competingRestore = playlist.RestorePlaylistDumpAsync(
                PlaylistWorkspaceTestDataSupport.CreatePlaylistRestoreDump(77, "Rejected restore", "X"));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                competingRestore);
            using (var db = new LR2SongDBExtended(Path.Combine(tempDirectory, "song.db")))
            {
                Assert.AreEqual(table.playlist_id, db.Table<BMSTable>().Single().playlist_id);
            }

            scheduler.ReleasePending();
            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> results = await reloadTask;

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Succeeded);
            Assert.AreEqual(1, scheduler.ExecutionCount);
            Assert.AreSame(results[0].ResultTable, playlist.BMSTables.Single());
            Assert.AreNotSame(table, results[0].ResultTable);
            Assert.AreEqual(recommendation ? 1 : 0, notifications.TakeReceipt().Notifications.Count);
            if (recommendation)
            {
                Assert.IsFalse(results[0].Updated);
                Assert.AreEqual(table.last_update, results[0].ResultTable.last_update);
            }
        }
        finally
        {
            if (reloadTask != null && !reloadTask.IsCompleted)
            {
                try
                {
                    scheduler.ReleasePending();
                    await reloadTask;
                }
                catch
                {
                }
            }
            if (competingRestore != null)
            {
                try { await competingRestore; }
                catch (InvalidOperationException) { }
            }
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    // U2-R4/R2: 既存 operation scheduler で UI rejection / cancel / fault を区別し、DB 保持と予約解放を確認する。
    [DataTestMethod]
    [DataRow("Rejected")]
    [DataRow("Aborted")]
    [DataRow("Faulted")]
    [TestCategory("Playlist")]
    public async Task PlaylistRestore_UiTerminalFailureKeepsDatabaseAndReleasesReservation(string outcomeName)
    {
        UiScheduleOutcome outcome = Enum.Parse<UiScheduleOutcome>(outcomeName);
        (string directory, TestBmsPlaylist playlist, BMSTable oldTable, ControlledUiScheduler scheduler) =
            await CreateUiReplacementReloadFixtureAsync(outcome);
        bool previousUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        bool previousLr2Mode = Settings.Default.OperationModeLR2DB;
        Settings.Default.OperationModeLR2DB = false;
        playlist.StartupBackgroundTaskScheduler = (_, _, _, _) => false;
        try
        {
            Exception? failure = null;
            try
            {
                await playlist.RestorePlaylistDumpAsync(
                    PlaylistWorkspaceTestDataSupport.CreatePlaylistRestoreDump(77, "Committed despite UI failure", "R"));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            Assert.IsNotNull(failure);
            Assert.AreSame(oldTable, playlist.BMSTables.Single());
            Assert.AreEqual(0, scheduler.ExecutionCount);
            using (var verify = new LR2SongDBExtended(Path.Combine(directory, "song.db")))
            {
                Assert.AreEqual(77, verify.Table<BMSTable>().Single().playlist_id);
            }
            // 独立した次の復元を受理する。失敗操作自身の repost / retry ではない。
            await playlist.RestorePlaylistDumpAsync(
                PlaylistWorkspaceTestDataSupport.CreatePlaylistRestoreDump(88, "Next independent restore", "N"));
            Assert.AreEqual(88, playlist.BMSTables.Single().playlist_id);
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousUrlCompletion;
            Settings.Default.OperationModeLR2DB = previousLr2Mode;
            Directory.Delete(directory, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public Task ReloadPlaylistTargetsAsync_ReportsRejectedUiReplacement(bool recommendation)
        => AssertUiReplacementFailureAsync(
            UiScheduleOutcome.Rejected,
            typeof(InvalidOperationException),
            "rejected", recommendation);

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public Task ReloadPlaylistTargetsAsync_ReportsAbortedUiReplacement(bool recommendation)
        => AssertUiReplacementFailureAsync(
            UiScheduleOutcome.Aborted,
            typeof(OperationCanceledException),
            "aborted", recommendation);

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public Task ReloadPlaylistTargetsAsync_ReportsFaultedUiReplacement(bool recommendation)
        => AssertUiReplacementFailureAsync(
            UiScheduleOutcome.Faulted,
            typeof(InvalidOperationException),
            "faulted", recommendation);

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_SkipsRemovedTargetBeforeApply()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        IDisposable? tableWriterGuard = null;
        Task<List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult>>? reloadTask = null;
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"RemovedTarget\",\r\n\"symbol\":\"R\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty));
            BMSTable table = playlist.ExternalSyncOwner.LoadExternalTable(new Uri(headerJsonPath));
            table.playlist_id = 9021;
            table.DisableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries ?? [])
                {
                    setup.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            int syncResultCount = 0;
            tableWriterGuard = table.ReaderWriterLock.GetWriterGuard();
            reloadTask = playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                [table],
                _ => syncResultCount++,
                reason: "test_removed_target",
                requireCurrentTargetForApply: true);
            bool reloadReachedMerge = SpinWait.SpinUntil(
                () => table.ReaderWriterLock.WaitingWriteCount > 0,
                TimeSpan.FromSeconds(10));
            playlist.RemoveBMSTable(table);
            tableWriterGuard.Dispose();
            tableWriterGuard = null;

            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> results = await reloadTask;

            Assert.IsTrue(reloadReachedMerge);
            Assert.AreEqual(1, results.Count);
            Assert.IsFalse(results[0].Succeeded);
            Assert.IsFalse(results[0].Updated);
            Assert.AreSame(table, results[0].ResultTable);
            Assert.IsNull(results[0].UpdateReceipt);
            Assert.AreEqual(1, syncResultCount);
            Assert.AreEqual(0, playlist.BMSTables.Count);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist WHERE playlist_id = 9021;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = 9021;"));
        }
        finally
        {
            tableWriterGuard?.Dispose();
            if (reloadTask != null && !reloadTask.IsCompleted)
            {
                try
                {
                    await reloadTask;
                }
                catch
                {
                }
            }
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static async Task AssertUiReplacementFailureAsync(
        UiScheduleOutcome outcome,
        Type expectedInnerExceptionType,
        string reason, bool recommendation)
    {
        (string tempDirectory, TestBmsPlaylist playlist, BMSTable table, ControlledUiScheduler scheduler) =
            await CreateUiReplacementReloadFixtureAsync(outcome, recommendation);
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        using PlaylistOperationNotificationOwner.OperationNotificationSession notifications = playlist.OperationNotificationOwner.BeginSession();
        try
        {
            Task<List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult>> reloadTask =
                playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                    [table],
                    reason: "test_ui_replacement_" + reason);

            Task firstTerminal = await Task.WhenAny(scheduler.Scheduled, reloadTask);
            Assert.AreSame(scheduler.Scheduled, firstTerminal);
            await scheduler.Scheduled;
            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> results = await reloadTask;

            Assert.AreEqual(1, results.Count);
            Assert.IsFalse(results[0].Succeeded);
            Assert.AreSame(table, results[0].ResultTable);
            Assert.AreSame(table, playlist.BMSTables.Single());
            Exception failure = results[0].Exception
                ?? throw new AssertFailedException("The UI replacement failure was not reported.");
            Assert.IsInstanceOfType(failure, typeof(PlaylistAggregatePersistenceOwner.PlaylistReloadApplyException));
            Assert.IsNotNull(failure.InnerException);
            Assert.IsInstanceOfType(failure.InnerException, expectedInnerExceptionType);
            StringAssert.Contains(failure.InnerException!.Message, reason);
            Assert.AreEqual(0, scheduler.ExecutionCount);
            Assert.IsTrue(notifications.TakeReceipt().IsEmpty);
            BMSTable persisted = new PlaylistPersistenceRepository(Path.Combine(tempDirectory, "song.db"))
                .LoadPlaylistHeaders().Single();
            Assert.AreEqual(table.name, persisted.name);
            Assert.AreEqual(table.org_name, persisted.org_name);
            Assert.AreEqual(table.header_sha256, persisted.header_sha256);
            Assert.AreEqual(table.data_sha256, persisted.data_sha256);
            Assert.AreEqual(table.last_update, persisted.last_update);
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static async Task<(string TempDirectory, TestBmsPlaylist Playlist, BMSTable Table, ControlledUiScheduler Scheduler)> CreateUiReplacementReloadFixtureAsync(
        UiScheduleOutcome outcome, bool recommendation = false)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(
                headerJsonPath,
                CreateUtf8BomBytes(
                    "{\r\n\"name\":\"AsyncReplacementTable\",\r\n\"symbol\":\"A\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(
                scoreJsonPath,
                CreateUtf8BomBytes(
                    "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var scheduler = new ControlledUiScheduler(TestUiDispatcherHost.Dispatcher);
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot { ShowRecommUpdatedMsg = true },
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty),
                recommendationScoreReader: _ => Task.FromResult(ReadWalkureInput("standard")), uiScheduler: scheduler);
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(
                recommendation ? new Uri("bmseeker:table.recommended") : new Uri(headerJsonPath));
            if (recommendation)
            {
                table.name = "Manual recommendation ★999";
                table.org_name = "旧方針 ★3.87";
                table.header_sha256 = null;
                table.data_sha256 = null;
                table.last_update = new DateTime(2024, 6, 1, 10, 20, 30);
            }
            table.playlist_id = 9051;
            table.DisableExternalSync();
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                    foreach (BMSTableEntry entry in table.entries ?? [])
                    {
                        setup.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                    }
                });
            }
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            File.WriteAllBytes(
                scoreJsonPath,
                CreateUtf8BomBytes(
                    "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));
            scheduler.Arm(outcome);
            return (tempDirectory, playlist, table, scheduler);
        }
        catch
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            throw;
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistWorkspaceManualResync_UsesActiveTableAndPublishesLifecycle()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "workspace-header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "workspace-score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"WorkspaceTarget\",\r\n\"symbol\":\"W\",\r\n\"data_url\":\"./workspace-score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 9011;
            table.DisableExternalSync();
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries ?? [])
                {
                    setup.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            var library = new TestBmsLibrary(songDbPath);
            var lifecycleLogs = new List<string>();
            var failureLogs = new List<(Exception Exception, string Message)>();
            var workspace = new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                () => new CustomFolderOutputSettingsSnapshot(),
                PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
                PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
                PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
                PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
                PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
                PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
                PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
                () => playlist,
                PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
                () => library,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                message => lifecycleLogs.Add(message),
                (exception, message) => failureLogs.Add((exception, message)), (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
            workspace.ConfigureCatalogNotificationQueue(action => action());
            PlaylistWorkspaceTestPorts.AttachImmediatePlaylistPresentationRouter(workspace);
            workspace.RefreshPlaylistTreeTables(playlist);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            var progress = new ConcurrentQueue<PlaylistSyncProgressSnapshot>();
            int referenceSortInvalidationCount = 0;
            long summaryDataGenerationBeforeResync = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
            workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder(string.Empty));
            workspace.PlaylistSyncProgressChanged += (_, request) => progress.Enqueue(request.Snapshot);
            workspace.PlaylistReferenceSortInvalidationRequested += (_, _) => referenceSortInvalidationCount++;
            Assert.IsTrue(workspace.ContainsActivePlaylistTable(table));
            Assert.IsTrue(workspace.ContainsActivePlaylistSummaryRows([new PlaylistSummaryRow { TableRef = table }]));

            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));
            BMSTable firstSelectionTarget = await workspace.ResyncPlaylistTableAsync(table);

            Assert.AreEqual(
                1,
                lifecycleLogs.Count(log => log.StartsWith(
                    "playlist_reload_operation started operationKind=single reason=manual_resync tableCount=1",
                    StringComparison.Ordinal)));
            AssertManualResyncProgress(progress, new Uri(headerJsonPath));
            progress.Clear();
            Assert.AreEqual(1, referenceSortInvalidationCount);
            Assert.IsTrue(lifecycleLogs.Any(log => log.StartsWith(
                "playlist_reload_operation completed operationKind=single reason=manual_resync tableCount=1 processedCount=1",
                StringComparison.Ordinal)));
            Assert.IsTrue(workspace.CurrentPlaylistSummaryDataRebuildGeneration > summaryDataGenerationBeforeResync);

            BMSTable reloadedTable = playlist.BMSTables.Single();
            Assert.AreNotSame(table, reloadedTable);
            Assert.AreSame(reloadedTable, firstSelectionTarget);
            PlaylistDetailSelection reloadedSelection = workspace.CapturePlaylistDetailSelection()
                ?? throw new AssertFailedException("Replacement detail selection was not retained.");
            Assert.AreSame(reloadedTable, reloadedSelection.Table);
            Assert.IsFalse(workspace.ContainsActivePlaylistTable(table));
            Assert.IsFalse(workspace.ContainsActivePlaylistSummaryRows([new PlaylistSummaryRow { TableRef = table }]));
            Assert.IsTrue(workspace.ContainsActivePlaylistTable(reloadedTable));
            Assert.IsTrue(workspace.ContainsActivePlaylistSummaryRows([new PlaylistSummaryRow { TableRef = reloadedTable }]));

            reloadedTable.header_sha256 = null;
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"WorkspaceTarget\",\r\n\"symbol\":\"W\",\r\n\"tag\":\"header-refresh\",\r\n\"data_url\":\"./workspace-score.json\",\r\n\"level_order\":[1]\r\n}"));
            BMSTable secondSelectionTarget = await workspace.ResyncPlaylistTableAsync(reloadedTable);
            AssertManualResyncProgress(progress, new Uri(headerJsonPath));
            progress.Clear();
            Assert.AreEqual(2, referenceSortInvalidationCount);
            BMSTable headerRefreshedTable = playlist.BMSTables.Single();
            Assert.AreNotSame(reloadedTable, headerRefreshedTable);
            Assert.AreSame(headerRefreshedTable, secondSelectionTarget);
            Assert.AreSame(headerRefreshedTable, workspace.CapturePlaylistDetailSelection().Table);
            Assert.IsFalse(workspace.ContainsActivePlaylistTable(reloadedTable));
            Assert.IsTrue(workspace.ContainsActivePlaylistTable(headerRefreshedTable));

            Assert.AreEqual(
                2,
                lifecycleLogs.Count(log => log.StartsWith(
                    "playlist_reload_operation started operationKind=single reason=manual_resync tableCount=1",
                    StringComparison.Ordinal)));

            headerRefreshedTable.header_sha256 = null;
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{"));
            Uri failureUri = headerRefreshedTable.Page_url ?? headerRefreshedTable.Header_url;
            BMSTable failureSelectionTarget = await workspace.ResyncPlaylistTableAsync(headerRefreshedTable);
            AssertManualResyncProgress(progress, new Uri(headerJsonPath));
            Assert.AreEqual(1, failureLogs.Count);
            Assert.AreEqual(2, referenceSortInvalidationCount);
            Assert.AreSame(headerRefreshedTable, workspace.CapturePlaylistDetailSelection().Table);
            Assert.AreSame(headerRefreshedTable, failureSelectionTarget);
            Assert.IsNotNull(failureLogs[0].Exception);
            Assert.AreEqual(
                "playlist_manual_resync_failed table=WorkspaceTarget uri=" + failureUri,
                failureLogs[0].Message);
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static void AssertManualResyncProgress(ConcurrentQueue<PlaylistSyncProgressSnapshot> progress, Uri targetUri)
    {
        PlaylistSyncProgressSnapshot[] notifications = progress.ToArray();
        Assert.IsTrue(notifications.Length > 0);
        Assert.IsTrue(notifications.All(snapshot => snapshot.Source == "playlist_manual_reload"));
        Assert.IsTrue(notifications.All(snapshot => snapshot.OperationId == 0));
        Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive
            && snapshot.TotalTableCount == 1 && snapshot.CurrentUri == targetUri));
        Assert.IsTrue(notifications.Any(snapshot => snapshot.IsActive && snapshot.CompletedTableCount == 1));
        Assert.IsFalse(notifications[^1].IsActive);
    }

    private static IReadOnlyList<LocalPlaylistMutationCase> CreateLocalPlaylistMutationCases()
    {
        return
        [
            new LocalPlaylistMutationCase(
                "rename-folder",
                (playlist, table, commitFlag) => playlist.RenameFolderBMSTable(table, "Folder A", "Folder B", commitFlag),
                result => result is true),
            new LocalPlaylistMutationCase(
                "remove-folder",
                (playlist, table, commitFlag) => playlist.RemoveFolderBMSTable(table, "Folder A", commitFlag),
                result => result is true),
            new LocalPlaylistMutationCase(
                "create-folder",
                (playlist, table, commitFlag) => playlist.CreateNewFolderBMSTable(table, "Folder B", commitFlag),
                result => result is string folderName && !string.IsNullOrWhiteSpace(folderName)),
            new LocalPlaylistMutationCase(
                "add-entries-to-folder",
                (playlist, table, commitFlag) => playlist.AddPlaylistEntriesToFolderBMSTable(
                    [table.entries.Single(entry => entry.md5 == LocalMutationSecondHash)],
                    table,
                    "Folder B",
                    commitFlag),
                result => result is true),
            new LocalPlaylistMutationCase(
                "remove-entries",
                (playlist, table, commitFlag) => playlist.RemoveEntriesBMSTable(
                    [table.entries.Single(entry => entry.md5 == LocalMutationSecondHash)],
                    table,
                    commitFlag),
                result => result is true)
        ];
    }

    private static CustomFolderOutputSettingsSnapshot CreateLocalMutationOutputSettings(
        string tempDirectory,
        string outputBaseDirectory,
        bool operationModeLr2Db)
    {
        return new CustomFolderOutputSettingsSnapshot
        {
            OperationModeLR2DB = operationModeLr2Db,
            LR2RootPath = tempDirectory,
            LR2CustomFolderOutputBaseDir = outputBaseDirectory,
            LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootCustomFolder"),
            LR2CustomFolderAdditionalOutputBaseDirs = "[]"
        };
    }

    private static BMSTable CreateLocalMutationTable()
    {
        BMSTableEntry firstEntry = CreateEntry(LocalMutationFirstHash, "Folder A");
        firstEntry.folder = "Folder A";
        firstEntry.level = 1;
        firstEntry.title = "Local admission first";
        BMSTableEntry secondEntry = CreateEntry(LocalMutationSecondHash, "Folder A");
        secondEntry.folder = "Folder A";
        secondEntry.level = 2;
        secondEntry.title = "Local admission second";
        return new BMSTable
        {
            playlist_id = 990001,
            name = "LocalAdmissionTable",
            symbol = "LAT",
            Output_dir = "LocalAdmissionOutput",
            ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
            entries = [firstEntry, secondEntry],
            Folder_order = ["Folder A"]
        };
    }

    private static Exception? CaptureMutationFailure(
        LocalPlaylistMutationCase mutationCase,
        TestBmsPlaylist playlist,
        BMSTable table)
    {
        try
        {
            mutationCase.Invoke(playlist, table, true);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static PlaylistModelSnapshot CapturePlaylistModel(BMSTable table)
    {
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            return new PlaylistModelSnapshot(
                table.last_update,
                [.. (table.Folder_order ?? [])],
                [.. (table.entries ?? []).Select(CreateEntryState)]);
        }
    }

    private static void AssertPlaylistModelUnchanged(
        PlaylistModelSnapshot expected,
        PlaylistModelSnapshot actual)
    {
        Assert.AreEqual(expected.LastUpdate, actual.LastUpdate);
        CollectionAssert.AreEqual(expected.FolderOrder.ToArray(), actual.FolderOrder.ToArray());
        CollectionAssert.AreEqual(expected.EntryStates.ToArray(), actual.EntryStates.ToArray());
    }

    private static void AssertPersistedEntriesMatchModel(BMSTable table, string songDbPath)
    {
        string[] expected;
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            expected = [.. (table.entries ?? []).Select(CreateEntryState).OrderBy(state => state, StringComparer.Ordinal)];
        }

        using var db = new LR2SongDBExtended(songDbPath);
        string[] actual =
        [
            .. db.Table<LR2SongDBExtended.playlist_entry>()
                .Where(entry => entry.playlist_id == table.playlist_id)
                .Select(CreatePersistedEntryState)
                .OrderBy(state => state, StringComparer.Ordinal)
        ];
        CollectionAssert.AreEqual(expected, actual);
    }

    private static string CreateEntryState(BMSTableEntry entry)
    {
        return string.Join(
            "\u001f",
            entry?.md5 ?? string.Empty,
            entry?.sha256 ?? string.Empty,
            entry?.folder ?? string.Empty,
            entry?.is_removed == true ? "1" : "0");
    }

    private static string CreatePersistedEntryState(LR2SongDBExtended.playlist_entry entry)
    {
        return string.Join(
            "\u001f",
            entry?.md5 ?? string.Empty,
            entry?.sha256 ?? string.Empty,
            entry?.folder ?? string.Empty,
            entry?.is_removed == true ? "1" : "0");
    }

    private static Dictionary<string, byte[]> CaptureFileSurface(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(rootDirectory, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertFileSurfaceUnchanged(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (string path in expected.Keys)
        {
            CollectionAssert.AreEqual(expected[path], actual[path], path);
        }
    }

    private const string LocalMutationFirstHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string LocalMutationSecondHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private sealed class LocalPlaylistMutationCase
    {
        internal LocalPlaylistMutationCase(
            string name,
            Func<TestBmsPlaylist, BMSTable, bool, object> invoke,
            Func<object, bool> wasApplied)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
            WasApplied = wasApplied ?? throw new ArgumentNullException(nameof(wasApplied));
        }

        internal string Name { get; }

        internal Func<TestBmsPlaylist, BMSTable, bool, object> Invoke { get; }

        internal Func<object, bool> WasApplied { get; }
    }

    private sealed class PlaylistModelSnapshot
    {
        internal PlaylistModelSnapshot(
            DateTime lastUpdate,
            IReadOnlyList<string> folderOrder,
            IReadOnlyList<string> entryStates)
        {
            LastUpdate = lastUpdate;
            FolderOrder = folderOrder ?? [];
            EntryStates = entryStates ?? [];
        }

        internal DateTime LastUpdate { get; }

        internal IReadOnlyList<string> FolderOrder { get; }

        internal IReadOnlyList<string> EntryStates { get; }
    }

    private enum UiScheduleOutcome
    {
        AcceptedPending,
        Rejected,
        Aborted,
        Faulted
    }

    private sealed class ControlledUiScheduler : IUiScheduler
    {
        private readonly TestUiScheduler inner;

        private readonly object synchronization = new();

        private readonly TaskCompletionSource<IUiScheduledOperation> scheduled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private UiScheduleOutcome? armedOutcome;

        private PendingUiScheduledOperation? pendingOperation;

        internal ControlledUiScheduler(Dispatcher dispatcher)
        {
            inner = new TestUiScheduler(() => dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)));
        }

        internal Task<IUiScheduledOperation> Scheduled => scheduled.Task;

        internal int ExecutionCount
        {
            get
            {
                lock (synchronization)
                {
                    return pendingOperation?.ExecutionCount ?? 0;
                }
            }
        }

        internal void Arm(UiScheduleOutcome outcome)
        {
            lock (synchronization)
            {
                if (armedOutcome.HasValue)
                {
                    throw new InvalidOperationException("The controlled UI scheduler is already armed.");
                }
                armedOutcome = outcome;
            }
        }

        internal void ReleasePending()
        {
            PendingUiScheduledOperation operation;
            lock (synchronization)
            {
                operation = pendingOperation
                    ?? throw new InvalidOperationException("No pending UI operation is available.");
            }

            IUiScheduledOperation dispatch = inner.Schedule(operation.Run);
            if (dispatch == null || !dispatch.IsAccepted)
            {
                operation.Fail(new InvalidOperationException(
                    "The controlled UI scheduler could not release the pending operation."));
            }
        }

        public bool IsAvailable => inner.IsAvailable;

        public bool CanExecuteInline => inner.CanExecuteInline;

        public bool CheckAccess() => inner.CheckAccess();

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            UiScheduleOutcome? outcome;
            lock (synchronization)
            {
                outcome = armedOutcome;
                armedOutcome = null;
            }
            if (!outcome.HasValue)
            {
                return inner.Schedule(action, priority);
            }

            IUiScheduledOperation operation = outcome.Value switch
            {
                UiScheduleOutcome.AcceptedPending => CreatePendingOperation(action),
                UiScheduleOutcome.Rejected => new CompletedUiScheduledOperation(
                    accepted: false,
                    aborted: true,
                    completion: Task.CompletedTask,
                    rejectionReason: "controlled scheduler rejection"),
                UiScheduleOutcome.Aborted => new CompletedUiScheduledOperation(
                    accepted: true,
                    aborted: true,
                    completion: Task.FromCanceled(new CancellationToken(canceled: true)),
                    rejectionReason: "controlled scheduler abort"),
                UiScheduleOutcome.Faulted => new CompletedUiScheduledOperation(
                    accepted: true,
                    aborted: false,
                    completion: Task.FromException(new InvalidOperationException("controlled scheduler faulted")),
                    rejectionReason: null),
                _ => throw new ArgumentOutOfRangeException(nameof(outcome))
            };
            scheduled.TrySetResult(operation);
            return operation;
        }

        private IUiScheduledOperation CreatePendingOperation(Action action)
        {
            var operation = new PendingUiScheduledOperation(action);
            lock (synchronization)
            {
                pendingOperation = operation;
            }
            return operation;
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => inner.Invoke(action, priority);

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => inner.Invoke(action, priority);

        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => inner.InvokeAsync(action, priority);

        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => inner.InvokeAsync(action, priority);
    }

    private sealed class PendingUiScheduledOperation : IUiScheduledOperation
    {
        private readonly Action action;

        private readonly TaskCompletionSource<object?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int executionCount;

        private int aborted;

        internal PendingUiScheduledOperation(Action action)
        {
            this.action = action ?? throw new ArgumentNullException(nameof(action));
        }

        internal int ExecutionCount => Volatile.Read(ref executionCount);

        public bool IsAccepted => true;

        public bool IsCompleted => completion.Task.IsCompleted;

        public bool IsAborted => Volatile.Read(ref aborted) != 0;

        public string? RejectionReason => null;

        public Task Completion => completion.Task;

        public void Abort()
        {
            if (Interlocked.Exchange(ref aborted, 1) == 0)
            {
                completion.TrySetCanceled();
            }
        }

        internal void Run()
        {
            if (IsAborted || Interlocked.Increment(ref executionCount) != 1)
            {
                return;
            }
            try
            {
                action();
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        internal void Fail(Exception exception)
        {
            completion.TrySetException(exception ?? throw new ArgumentNullException(nameof(exception)));
        }
    }

    private sealed class CompletedUiScheduledOperation : IUiScheduledOperation
    {
        private readonly bool accepted;

        private readonly bool aborted;

        internal CompletedUiScheduledOperation(
            bool accepted,
            bool aborted,
            Task completion,
            string? rejectionReason)
        {
            this.accepted = accepted;
            this.aborted = aborted;
            Completion = completion ?? throw new ArgumentNullException(nameof(completion));
            RejectionReason = rejectionReason;
        }

        public bool IsAccepted => accepted;

        public bool IsCompleted => Completion.IsCompleted;

        public bool IsAborted => aborted;

        public string? RejectionReason { get; }

        public Task Completion { get; }

        public void Abort()
        {
        }
    }

    private sealed class CountingMutationLeaseProvider
    {
        private readonly object ownerIdentity = new();

        private int activeLease;

        private int callCount;

        internal bool DenyAll { get; set; }

        internal int CallCount => Volatile.Read(ref callCount);

        internal LibraryFileMutationLease TryBegin(string _)
        {
            Interlocked.Increment(ref callCount);
            if (DenyAll || Interlocked.CompareExchange(ref activeLease, 1, 0) != 0)
            {
                return null!;
            }

            return new LibraryFileMutationLease(
                ownerIdentity,
                () => Volatile.Read(ref activeLease) != 0,
                () => Volatile.Write(ref activeLease, 0));
        }

        internal LibraryFileMutationLease AcquireIncumbent()
        {
            return TryBegin("test_incumbent")
                ?? throw new InvalidOperationException("The test mutation lease provider did not admit the incumbent.");
        }

        internal void ResetCallCount()
        {
            Interlocked.Exchange(ref callCount, 0);
        }
    }
}
