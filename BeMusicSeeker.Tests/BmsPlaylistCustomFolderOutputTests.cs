using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Ribbit.Util.Extensions;

using static BeMusicSeeker.Tests.BmsPlaylistTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
// Arbitrary filtered Quick runs share one testhost. This fixture mutates the
// process-global playlist URL completion, LR2 mode/root/output paths, Beatoraja
// output settings, and IR flag in Settings.Default.
[DoNotParallelize]
public sealed class BmsPlaylistCustomFolderOutputTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_SyncsLr2FolderRowsFromGeneratedText()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7301,
                name = "FolderTable",
                symbol = "FT",
                Output_dir = "FolderTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            string outputPath = Path.Combine(outputBaseDir, "FolderTable", "0001.lr2folder");
            CustomFolderOutputPhysicalSurface physicalSurface = CustomFolderOutputPhysicalSurface.FromEntries(
                [new RootFileEnumerationEntry(outputPath, DateTime.UtcNow)],
                discoveryComplete: true);
            var synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, physicalSurface);
            var playlist = new TestBmsPlaylist(songDbPath, synchronization)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            Assert.IsTrue(File.Exists(outputPath));
            string text = File.ReadAllText(outputPath, Encoding.GetEncoding("shift_jis"));
            StringAssert.Contains(text, "#TITLE Folder A");
            StringAssert.Contains(text, "#CATEGORY FolderTable");
            StringAssert.Contains(text, "#COMMAND song.hash");

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder folder = verify.Table<LR2SongDB.folder>().Single(row => row.path == outputPath);
            Assert.AreEqual(2, folder.type);
            Assert.AreEqual("Folder A", folder.title);
            Assert.AreEqual("FolderTable", folder.category);
            StringAssert.Contains(folder.command, "playlist_entry");
            Assert.AreEqual(0, folder.max);
            Assert.IsTrue(folder.date.HasValue);
            Assert.IsTrue(folder.adddate.HasValue);
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
    public void ReOutputCustomFolder_FailureRequiresNotificationScopeOrQueuesWarning()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var table = new BMSTable
            {
                playlist_id = 7302,
                name = "FailureTable",
                symbol = "FTF",
                Output_dir = "FailureTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")],
                Folder_order = ["Folder A"]
            };
            var synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                songDbPath,
                CustomFolderOutputPhysicalSurface.Empty);
            var expectedFailure = new InvalidOperationException("forced custom-folder sync failure");
            synchronization.Failure = expectedFailure;
            var playlist = new TestBmsPlaylist(songDbPath, synchronization)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            Assert.AreSame(
                expectedFailure,
                Assert.ThrowsException<InvalidOperationException>(() => playlist.ReOutputCustomFolder(table)));

            using (PlaylistOperationNotificationOwner.OperationNotificationSession session = playlist.OperationNotificationOwner.BeginSession())
            {
                Assert.AreSame(
                    expectedFailure,
                    Assert.ThrowsException<InvalidOperationException>(() => playlist.ReOutputCustomFolder(table)));
                PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = session.TakeReceipt();
                Assert.AreEqual(1, receipt.Notifications.Count);
                Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning, receipt.Notifications[0].Severity);
                StringAssert.Contains(receipt.Notifications[0].Message, table.name);
            }
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
    public void ReOutputCustomFolderAndCommitToDB_UsesInjectedCustomFolderOutputSettings()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        bool previousEnableUnsent = Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Settings.Default.OperationModeLR2DB = false;
            string settingsOutputBaseDir = Path.Combine(tempDirectory, "SettingsOutput");
            string providerOutputBaseDir = Path.Combine(tempDirectory, "ProviderOutput");
            Settings.Default.LR2CustomFolderOutputBaseDir = settingsOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "SettingsRootOutput");
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent = false;
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]",
                EnableDownloadLr2IrScoreAndDetectUnsent = true
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                providerCallCount++;
                return outputSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7302,
                name = "InjectedOutputTable",
                symbol = "IOT",
                Output_dir = "InjectedOutputTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.OtherFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Injected Folder")
                ],
                Folder_order = ["Injected Folder"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOutputSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            Assert.IsTrue(File.Exists(Path.Combine(providerOutputBaseDir, "InjectedOutputTable", "0001.lr2folder")));
            Assert.IsFalse(File.Exists(Path.Combine(settingsOutputBaseDir, "InjectedOutputTable", "0001.lr2folder")));
            Assert.IsTrue(Directory.GetFiles(Path.Combine(providerOutputBaseDir, "InjectedOutputTable"), "*.lr2folder")
                .Any(path => File.ReadAllText(path, Encoding.GetEncoding("shift_jis")).IndexOf("#TITLE UNSENT SONGS", StringComparison.Ordinal) >= 0));
            Assert.AreEqual(1, providerCallCount);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent = previousEnableUnsent;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void SyncCustomFolderOutputSearchRootsAfterSettingsChange_UsesInjectedOutputSettings()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Settings.Default.OperationModeLR2DB = false;
            string settingsNormalOutputBaseDir = Path.Combine(tempDirectory, "SettingsNormalOutput");
            string settingsRootOutputBaseDir = Path.Combine(tempDirectory, "SettingsRootOutput");
            string providerNormalOutputBaseDir = Path.Combine(tempDirectory, "ProviderNormalOutput");
            string providerRootOutputBaseDir = Path.Combine(tempDirectory, "ProviderRootOutput");
            Settings.Default.LR2CustomFolderOutputBaseDir = settingsNormalOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = settingsRootOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerNormalOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = providerRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            LR2Config config = CreateLr2Config(tempDirectory, Path.Combine(tempDirectory, "ManualBmsRoot"));
            var table = new BMSTable
            {
                playlist_id = 7303,
                is_root_folder = true,
                Output_dir = "InjectedRootOutput"
            };
            var playlist = new TestBmsPlaylist(
                songDbPath,
                () => config,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => outputSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            Assert.IsTrue(playlist.SyncCustomFolderOutputSearchRootsAfterSettingsChange(settingsRootOutputBaseDir, config));

            string providerRootOutput = Path.Combine(providerRootOutputBaseDir, table.Output_dir);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), providerRootOutput);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), providerNormalOutputBaseDir);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), settingsRootOutputBaseDir);
            Assert.IsTrue(Directory.Exists(providerRootOutput));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_WritesLr2FolderUnderLongPath()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        LongPathFileSystem.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = BuildLongDirectoryPath(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7319,
                name = "LongFolderTable",
                symbol = "LFT",
                Output_dir = "LongFolderTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder B")
                ],
                Folder_order = ["Folder B"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputPath = Path.Combine(outputBaseDir, "LongFolderTable", "0001.lr2folder");
            Assert.IsTrue(LongPathFileSystem.FileExists(outputPath));
            StringAssert.Contains(ReadShiftJisText(outputPath), "#TITLE Folder B");
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", outputPath));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (LongPathFileSystem.DirectoryExists(tempDirectory))
            {
                LongPathFileSystem.DeleteDirectory(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_GeneratesAdditionalOutputBaseParentRow()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string defaultOutputBaseDir = Path.Combine(tempDirectory, "DefaultCustomFolder");
            string additionalOutputBaseDir = Path.Combine(tempDirectory, "Additional");
            Directory.CreateDirectory(additionalOutputBaseDir);
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = defaultOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBaseDir]);
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7303,
                name = "AdditionalTable",
                symbol = "AT",
                Output_dir = "AdditionalTable",
                custom_folder_output_base_name = "Additional",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("cccccccccccccccccccccccccccccccc", "Folder C")
                ],
                Folder_order = ["Folder C"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDirectory = Path.Combine(additionalOutputBaseDir, "AdditionalTable");
            string outputPath = Path.Combine(outputDirectory, "0001.lr2folder");
            Assert.IsTrue(File.Exists(outputPath));
            using var verify = new LR2SongDBExtended(songDbPath);
            List<LR2SongDB.folder> rows = verify.Table<LR2SongDB.folder>().ToList();
            string rowSummary = string.Join(" | ", rows.Select(row => $"{row.type}:{row.parent}:{row.path}").Take(20));
            LR2SongDB.folder outputBaseRow = rows.SingleOrDefault(row => row.path == Lr2FolderPath.ToFolderPath(additionalOutputBaseDir));
            Assert.IsNotNull(outputBaseRow, rowSummary);
            Assert.AreEqual(1, outputBaseRow.type);
            Assert.AreEqual("Additional", outputBaseRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, outputBaseRow.parent);
            LR2SongDB.folder tableRow = rows.SingleOrDefault(row => row.path == Lr2FolderPath.ToFolderPath(outputDirectory));
            Assert.IsNotNull(tableRow, rowSummary);
            Assert.AreEqual(1, tableRow.type);
            Assert.AreEqual("AdditionalTable", tableRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(additionalOutputBaseDir), tableRow.parent);
            LR2SongDB.folder folderFileRow = rows.SingleOrDefault(row => row.path == outputPath);
            Assert.IsNotNull(folderFileRow, rowSummary);
            Assert.AreEqual(2, folderFileRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputDirectory), folderFileRow.parent);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void PlaylistPropertyDialogApplyPostSaveUpdates_UsesOpenCustomFolderSettingsSnapshot()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Task operation = PlaylistPropertyDialogApplyPostSaveUpdatesCoreAsync();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                operation,
                nameof(PlaylistPropertyDialogApplyPostSaveUpdates_UsesOpenCustomFolderSettingsSnapshot));
        });
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_PostSaveFailureReconcilesDialogWithDurableActiveState()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7313,
                name = "PostSaveFailure",
                symbol = "OLD",
                Output_dir = "PostSaveFailure",
                entries = [CreateEntry("ffffffffffffffffffffffffffffffff", "Folder")]
            };
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                seed.InsertOrReplace(table.entries.Single(), typeof(LR2SongDBExtended.playlist_entry));
            }
            var playlist = new TestBmsPlaylist(
                songDbPath,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            int libraryProviderCallCount = 0;
            var library = new TestBmsLibrary(songDbPath);
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => Interlocked.Increment(ref libraryProviderCallCount) == 1
                    ? throw new InvalidOperationException("test post-save failure")
                    : library,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            service.PlaylistPropertyReferenceSortInvalidationRequested += (_, _) => { };
            service.PlaylistPropertySummaryDataRefreshRequested += (_, _) => { };
            PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            dialog = new PlaylistPropertyDialogViewModel(service, session)
            {
                symbol = "NEW"
            };

            InvalidOperationException failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await dialog.SaveAndApplyAsync());

            Assert.AreEqual("test post-save failure", failure.Message);
            Assert.AreEqual("NEW", table.symbol);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await dialog.ResetPropertiesAsync());
            Assert.AreEqual("NEW", dialog.symbol);
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.ResetPropertiesAsync());
            Assert.AreEqual(2, libraryProviderCallCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                "NEW",
                verify.Table<BMSTable>().Single(row => row.playlist_id == table.playlist_id).symbol);
        }
        finally
        {
            dialog?.Dispose();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_PostSaveRetryDoesNotRewriteCompatiblePrefixTwice()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTableEntry entry = CreateEntry("abababababababababababababababab", "Alpha");
            entry.folder = "Alpha";
            var table = new BMSTable
            {
                playlist_id = 7314,
                name = "PrefixRetry",
                symbol = "PR",
                Output_dir = "PrefixRetry",
                compat_prefix = string.Empty,
                entries = [entry],
                Folder_order = ["Alpha"]
            };
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
            }
            var playlist = new TestBmsPlaylist(
                songDbPath,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var library = new TestBmsLibrary(songDbPath);
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => library,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            int remapRequestCount = 0;
            service.PlaylistPropertyFolderSelectionRemapped += (_, _) =>
            {
                if (Interlocked.Increment(ref remapRequestCount) == 1)
                {
                    throw new InvalidOperationException("test remap presentation failure");
                }
            };
            service.PlaylistPropertyReferenceSortInvalidationRequested += (_, _) => { };
            service.PlaylistPropertyEntriesChanged += (_, _) => { };
            PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            dialog = new PlaylistPropertyDialogViewModel(service, session)
            {
                compat_prefix = "★"
            };

            InvalidOperationException failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await dialog.SaveAndApplyAsync());

            Assert.AreEqual("test remap presentation failure", failure.Message);
            Assert.AreEqual("★Alpha", table.entries.Single().folder);
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            Assert.AreEqual("★Alpha", table.entries.Single().folder);
            Assert.AreEqual(2, remapRequestCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                "★Alpha",
                verify.Table<BMSTableEntry>().Single(row => row.playlist_id == table.playlist_id).folder);
        }
        finally
        {
            dialog?.Dispose();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_PrefixRewriteNotificationFailureRetriesTheFixedMap()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTableEntry entry = CreateEntry("acacacacacacacacacacacacacacacac", "Alpha");
            entry.folder = "Alpha";
            BMSTableEntry prefixedEntry = CreateEntry("aeaeaeaeaeaeaeaeaeaeaeaeaeaeaeae", "★Alpha");
            prefixedEntry.folder = "★Alpha";
            var table = new BMSTable
            {
                playlist_id = 7316,
                name = "PrefixNotificationRetry",
                symbol = "PNR",
                Output_dir = "PrefixNotificationRetry",
                compat_prefix = string.Empty,
                entries = [entry, prefixedEntry],
                Folder_order = ["Alpha", "★Alpha"]
            };
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                seed.InsertOrReplace(prefixedEntry, typeof(LR2SongDBExtended.playlist_entry));
            }
            var playlist = new TestBmsPlaylist(
                songDbPath,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => new TestBmsLibrary(songDbPath),
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            service.PlaylistPropertyFolderSelectionRemapped += (_, _) => { };
            service.PlaylistPropertyReferenceSortInvalidationRequested += (_, _) => { };
            service.PlaylistPropertyEntriesChanged += (_, _) => { };
            bool failRewriteNotification = true;
            table.PropertyChanged += (_, change) =>
            {
                if (failRewriteNotification
                    && string.Equals(change.PropertyName, "Folder_order", StringComparison.Ordinal))
                {
                    failRewriteNotification = false;
                    throw new InvalidOperationException("test prefix rewrite notification failure");
                }
            };
            PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            dialog = new PlaylistPropertyDialogViewModel(service, session)
            {
                compat_prefix = "★"
            };

            InvalidOperationException failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await dialog.SaveAndApplyAsync());

            Assert.AreEqual("test prefix rewrite notification failure", failure.Message);
            Assert.AreEqual("★Alpha", table.entries.Single(candidate => candidate.md5 == entry.md5).folder);
            Assert.AreEqual("★★Alpha", table.entries.Single(candidate => candidate.md5 == prefixedEntry.md5).folder);
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            Assert.AreEqual("★Alpha", table.entries.Single(candidate => candidate.md5 == entry.md5).folder);
            Assert.AreEqual("★★Alpha", table.entries.Single(candidate => candidate.md5 == prefixedEntry.md5).folder);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                "★Alpha",
                verify.Table<BMSTableEntry>().Single(row => row.playlist_id == table.playlist_id && row.md5 == entry.md5).folder);
            Assert.AreEqual(
                "★★Alpha",
                verify.Table<BMSTableEntry>().Single(row => row.playlist_id == table.playlist_id && row.md5 == prefixedEntry.md5).folder);
        }
        finally
        {
            dialog?.Dispose();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_PostSaveRetryRejectsAConcurrentLivePropertyChange()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTableEntry entry = CreateEntry("adadadadadadadadadadadadadadadad", "Alpha");
            entry.folder = "Alpha";
            var table = new BMSTable
            {
                playlist_id = 7317,
                name = "PrefixStaleRetry",
                symbol = "PSR",
                Output_dir = "PrefixStaleRetry",
                compat_prefix = string.Empty,
                entries = [entry],
                Folder_order = ["Alpha"]
            };
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
            }
            var playlist = new TestBmsPlaylist(
                songDbPath,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => new TestBmsLibrary(songDbPath),
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            int remapCount = 0;
            service.PlaylistPropertyFolderSelectionRemapped += (_, _) =>
            {
                if (Interlocked.Increment(ref remapCount) == 1)
                {
                    throw new InvalidOperationException("test pending follow-up failure");
                }
            };
            service.PlaylistPropertyReferenceSortInvalidationRequested += (_, _) => { };
            service.PlaylistPropertyEntriesChanged += (_, _) => { };
            PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            dialog = new PlaylistPropertyDialogViewModel(service, session)
            {
                compat_prefix = "★"
            };

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await dialog.SaveAndApplyAsync());
            table.symbol = "CONCURRENT";

            InvalidOperationException stale = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await dialog.SaveAndApplyAsync());

            StringAssert.Contains(stale.Message, "changed while save follow-up was pending");
            Assert.AreEqual("CONCURRENT", table.symbol);
            Assert.AreEqual("★Alpha", table.entries.Single().folder);
            Assert.AreEqual(1, remapCount);
        }
        finally
        {
            dialog?.Dispose();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistPropertyDialog_PostSaveRetryDoesNotReloadExternalSourceTwice()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ExternalRetry\",\r\n\"symbol\":\"ER\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd\",\"title\":\"External Retry Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty));
            BMSTable table = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 7315;
            table.Page_url = new Uri(headerJsonPath);
            table.DisableExternalSync();
            foreach (BMSTableEntry entry in table.entries)
            {
                entry.playlist_id = table.playlist_id;
            }
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries)
                {
                    seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            var library = new TestBmsLibrary(songDbPath);
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => library,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            service.ExternalSyncConfirmationRequested += (_, request) => request.Confirmed = true;
            int activeSyncOperationCount = 0;
            service.PlaylistPropertySyncStarted += (_, _) =>
                Interlocked.Increment(ref activeSyncOperationCount);
            int completionProgressCount = 0;
            service.PlaylistPropertySyncProgressChanged += (_, request) =>
            {
                if (request.Snapshot.CompletedTableCount == 1
                    && Interlocked.Increment(ref completionProgressCount) == 1)
                {
                    throw new InvalidOperationException("test post-reload completion presentation failure");
                }
            };
            service.PlaylistPropertySyncFinished += (_, _) =>
                Interlocked.Decrement(ref activeSyncOperationCount);
            int referenceReplacementPresentationCount = 0;
            service.PlaylistPropertyReferenceTableReplaced += (_, _) =>
                Interlocked.Increment(ref referenceReplacementPresentationCount);
            service.PlaylistPropertyFolderSelectionRemapped += (_, _) => { };
            service.PlaylistPropertyReferenceSortInvalidationRequested += (_, _) => { };
            service.PlaylistPropertySyncResultReported += (_, _) => { };
            service.PlaylistPropertyExternalSyncFailed += (_, _) => { };
            service.PlaylistPropertyEntriesChanged += (_, _) => { };
            service.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            int summaryRefreshCount = 0;
            service.PlaylistPropertySummaryDataRefreshRequested += (_, request) =>
            {
                if (string.Equals(request.Reason, "playlist_property_resync", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref summaryRefreshCount);
                }
            };
            PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            dialog = new PlaylistPropertyDialogViewModel(service, session)
            {
                is_external_sync = true
            };

            InvalidOperationException failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await dialog.SaveAndApplyAsync());

            Assert.AreEqual("test post-reload completion presentation failure", failure.Message);
            Assert.AreEqual(0, activeSyncOperationCount);
            File.Delete(headerJsonPath);
            File.Delete(scoreJsonPath);
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            Assert.AreEqual(2, referenceReplacementPresentationCount);
            Assert.AreEqual(2, completionProgressCount);
            Assert.AreEqual(0, activeSyncOperationCount);
            Assert.AreEqual(1, summaryRefreshCount);
            Assert.IsTrue(dialog.is_external_sync);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.IsTrue(
                verify.Table<BMSTable>()
                    .Single(row => row.playlist_id == table.playlist_id)
                    .is_external_sync);
            Assert.AreEqual(
                1,
                verify.Table<BMSTableEntry>()
                    .Count(row => row.playlist_id == table.playlist_id && !row.is_removed));
        }
        finally
        {
            dialog?.Dispose();
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_SyncsRootOutputRowsUnderTableDirectory()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string rootOutputBaseDir = Path.Combine(tempDirectory, "RootCustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir;
            Settings.Default.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7302,
                name = "RootFolderTable",
                symbol = "RFT",
                Output_dir = "RootFolderTable",
                is_root_folder = true,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Root Folder")
                ],
                Folder_order = ["Root Folder"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputPath = Path.Combine(rootOutputBaseDir, "RootFolderTable", "0001.lr2folder");
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder folder = verify.Table<LR2SongDB.folder>().Single(row => row.path == outputPath);
            Assert.AreEqual(2, folder.type);
            Assert.AreEqual("Root Folder", folder.title);
            Assert.AreEqual(
                Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(outputPath)),
                folder.parent);
            LR2SongDB.folder parentFolder = verify.Table<LR2SongDB.folder>().Single(row => row.path == Lr2FolderPath.ToFolderPath(Path.GetDirectoryName(outputPath)));
            Assert.AreEqual(1, parentFolder.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, parentFolder.parent);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_WritesHierarchicalRandomAndSortFolders()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder
                | LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder
                | LR2SongDBExtended.playlist.CustomFolderType.RandomFolder
                | LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder
                | LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder
                | LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder;
            var table = new BMSTable
            {
                playlist_id = 7305,
                name = "Stella",
                symbol = "ST",
                Output_dir = "Stella",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "st0"),
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "st1")
                ],
                Folder_order = ["st0", "st1"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "Stella");
            string allText = ReadShiftJisText(Path.Combine(outputDir, "0000.lr2folder"));
            StringAssert.Contains(allText, "#TITLE Stella ALL");
            string st0Text = ReadShiftJisText(Path.Combine(outputDir, "0001.lr2folder"));
            StringAssert.Contains(st0Text, "#TITLE st0");
            string randomText = ReadShiftJisText(Path.Combine(outputDir, "0003.lr2folder"));
            StringAssert.Contains(randomText, "#TITLE Stella ALL RANDOM");
            StringAssert.Contains(randomText, "#MAXTRACKS 1");
            StringAssert.Contains(randomText, "ORDER BY random()");

            string noPlayText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder"));
            StringAssert.Contains(noPlayText, "#TITLE Stella ALL NO PLAY");
            StringAssert.Contains(noPlayText, "score.clear IS NULL");
            string noPlayRandomText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0003.lr2folder"));
            StringAssert.Contains(noPlayRandomText, "#TITLE Stella ALL NO PLAY RANDOM");
            string assistText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "2 ASSIST", "0000.lr2folder"));
            StringAssert.Contains(assistText, "score.clear = 2");
            StringAssert.Contains(assistText, "NOT (score.clear = 2");
            StringAssert.Contains(assistText, "(IFNULL(score.op_history, 0) & 8) != 0");
            Assert.IsFalse(assistText.Contains("16777216"));
            Assert.IsFalse(assistText.Contains("score.rank"));
            string easyText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "3 EASY", "0000.lr2folder"));
            StringAssert.Contains(easyText, "score.clear = 2");
            StringAssert.Contains(easyText, "(IFNULL(score.op_history, 0) & 8) != 0");
            Assert.IsFalse(easyText.Contains("16777216"));
            Assert.IsFalse(easyText.Contains("score.rank"));
            AssertClearFolderCommandMatchesAssistAndEasyRows(songDbPath, assistText, easyText);
            string fcText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "6 FC", "0000.lr2folder"));
            StringAssert.Contains(fcText, "(IFNULL(score.op_history, 0) & 16) = 0");
            string paText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "7 P.A", "0000.lr2folder"));
            StringAssert.Contains(paText, "(IFNULL(score.op_history, 0) & 16) != 0");
            string underAText = ReadShiftJisText(Path.Combine(outputDir, "DJ LEVEL", "UNDER A", "0000.lr2folder"));
            StringAssert.Contains(underAText, "score.rank < 6 OR score.rank IS NULL");
            string bpmSortText = ReadShiftJisText(Path.Combine(outputDir, "BPM SORT", "0000.lr2folder"));
            StringAssert.Contains(bpmSortText, "FROM chart_info");
            StringAssert.Contains(bpmSortText, "mainbpm");
            StringAssert.Contains(bpmSortText, "IS NULL ASC");
            string bpSortText = ReadShiftJisText(Path.Combine(outputDir, "BP SORT", "0000.lr2folder"));
            StringAssert.Contains(bpSortText, "score.minbp IS NULL ASC");
            string playCountSortText = ReadShiftJisText(Path.Combine(outputDir, "PLAY COUNT SORT", "0000.lr2folder"));
            StringAssert.Contains(playCountSortText, "score.playcount IS NULL ASC");
            StringAssert.Contains(playCountSortText, "score.playcount DESC");

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder")));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(Path.Combine(outputDir, "CLEAR FOLDER"))));
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
    public void ReOutputCustomFolderAndCommitToDB_WritesLastPlaySortFolderWhenSchemaInstalled()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            string scoreDbPath = CreateInstalledPlayHistoryScoreDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                | LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder;
            var table = new BMSTable
            {
                playlist_id = 7306,
                name = "LastPlay",
                symbol = "LP",
                Output_dir = "LastPlay",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A"),
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder B")
                ],
                Folder_order = ["Folder A", "Folder B"]
            };
            var playlist = new TestBmsPlaylist(songDbPath, scoreDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "LastPlay");
            string lastPlayText = ReadShiftJisText(Path.Combine(outputDir, "LAST PLAY SORT", "0000.lr2folder"));
            StringAssert.Contains(lastPlayText, "#TITLE LastPlay ALL");
            StringAssert.Contains(lastPlayText, "ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC, (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC");
            StringAssert.Contains(lastPlayText, "#INFORMATION_A Sort: LAST PLAY DESC");
            Assert.IsFalse(File.Exists(Path.Combine(outputDir, "PLAY COUNT SORT", "0000.lr2folder")));

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder row = verify.Table<LR2SongDB.folder>().Single(item => item.path == Path.Combine(outputDir, "LAST PLAY SORT", "0000.lr2folder"));
            StringAssert.Contains(row.command, "bms_lr2_last_play");
            StringAssert.Contains(row.command, "ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC, (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC");
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
    public void ReOutputCustomFoldersAndCommitHeadersToDB_WritesLastPlaySortWhenSchemaNotInstalled()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            string scoreDbPath = CreateBaseScoreDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7307,
                name = "MissingLastPlaySchema",
                symbol = "ML",
                Output_dir = "MissingLastPlaySchema",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new TestBmsPlaylist(songDbPath, scoreDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_last_play_schema_missing");

            string outputDir = Path.Combine(outputBaseDir, "MissingLastPlaySchema");
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "LAST PLAY SORT", "0000.lr2folder")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(2L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE command LIKE '%bms_lr2_last_play%';"));
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
    public void ReOutputCustomFolderAndCommitToDB_PrunesLastPlaySortWhenOutputBitTurnsOff()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            string scoreDbPath = CreateInstalledPlayHistoryScoreDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                | LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder;
            var table = new BMSTable
            {
                playlist_id = 7308,
                name = "LastPlayPrune",
                symbol = "LPP",
                Output_dir = "LastPlayPrune",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new TestBmsPlaylist(songDbPath, scoreDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "LastPlayPrune");
            string lastPlayDir = Path.Combine(outputDir, "LAST PLAY SORT");
            string generatedPath = Path.Combine(lastPlayDir, "0000.lr2folder");
            string stalePath = Path.Combine(lastPlayDir, "stale.lr2folder");
            Assert.IsTrue(File.Exists(generatedPath));
            File.WriteAllText(stalePath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(new LR2SongDB.folder { path = stalePath, title = "stale", type = 2, command = "song.hash IS NOT NULL ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC, (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC" }, typeof(LR2SongDB.folder));
            }

            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder;
            playlist.ReOutputCustomFolderAndCommitToDB(table);

            Assert.IsFalse(File.Exists(generatedPath));
            Assert.IsFalse(File.Exists(stalePath));
            Assert.IsFalse(Directory.Exists(lastPlayDir));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "0000.lr2folder")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path LIKE ?;", lastPlayDir + "%"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE command LIKE '%bms_lr2_last_play%';"));
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
    public void ReOutputCustomFolderAndCommitToDB_WritesRootRandomFoldersAfterNormalFolders()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                | LR2SongDBExtended.playlist.CustomFolderType.LevelFolder
                | LR2SongDBExtended.playlist.CustomFolderType.RandomFolder;
            BMSTableEntry first = CreateEntryWithLevel("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1);
            BMSTableEntry second = CreateEntryWithLevel("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 2);
            var table = new BMSTable
            {
                playlist_id = 7308,
                name = "RootRandomOrder",
                symbol = "RRO",
                Output_dir = "RootRandomOrder",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries = [first, second],
                Folder_order = ["1", "2"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "RootRandomOrder");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0000.lr2folder")), "#TITLE RootRandomOrder ALL");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0001.lr2folder")), "#TITLE 1");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0002.lr2folder")), "#TITLE 2");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0003.lr2folder")), "#TITLE LEVEL 1");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0004.lr2folder")), "#TITLE LEVEL 2");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0005.lr2folder")), "#TITLE RootRandomOrder ALL RANDOM");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0008.lr2folder")), "#TITLE LEVEL 1 RANDOM");
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
    public void ReOutputCustomFolderAndCommitToDB_AllSongsScopeDoesNotRequireUserFolder()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7311,
                name = "AllScopeOnly",
                symbol = "ASO",
                Output_dir = "AllScopeOnly",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.ClearFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "AllScopeOnly");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0000.lr2folder")), "#TITLE AllScopeOnly ALL");
            Assert.IsFalse(File.Exists(Path.Combine(outputDir, "0001.lr2folder")));
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder")), "#TITLE AllScopeOnly ALL NO PLAY");
            Assert.IsFalse(File.Exists(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0001.lr2folder")));
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
    public void ReOutputCustomFolderAndCommitToDB_UserFolderScopeDoesNotWriteAllSongs()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7312,
                name = "FolderScopeOnly",
                symbol = "FSO",
                Output_dir = "FolderScopeOnly",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.ClearFolder,
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "FolderScopeOnly");
            string folderText = ReadShiftJisText(Path.Combine(outputDir, "0000.lr2folder"));
            StringAssert.Contains(folderText, "#TITLE Folder A");
            Assert.IsFalse(folderText.Contains("#TITLE FolderScopeOnly ALL"));
            Assert.IsFalse(File.Exists(Path.Combine(outputDir, "0001.lr2folder")));
            string clearText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder"));
            StringAssert.Contains(clearText, "#TITLE Folder A NO PLAY");
            Assert.IsFalse(clearText.Contains("#TITLE FolderScopeOnly ALL NO PLAY"));
            Assert.IsFalse(File.Exists(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0001.lr2folder")));
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
    public void ReOutputCustomFolderAndCommitToDB_DoesNotExpandOldAllFoldersMaskToNewTypes()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7305,
                name = "LegacyDisabled",
                symbol = "LD",
                Output_dir = "LegacyDisabled",
                ignore_folder_output = (LR2SongDBExtended.playlist.CustomFolderType)0x7F,
                entries =
                [
                    CreateEntry("dddddddddddddddddddddddddddddddd", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "LegacyDisabled");
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "BPM SORT", "0000.lr2folder")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "BP SORT", "0000.lr2folder")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "PLAY COUNT SORT", "0000.lr2folder")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "LAST PLAY SORT", "0000.lr2folder")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE command LIKE '%bms_lr2_last_play%';"));
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
    public void ReOutputCustomFolderAndCommitToDB_PrunesExtraLr2FolderRowsInSameOutputDirectory()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7306,
                name = "SameOutputPrune",
                symbol = "SOP",
                Output_dir = "SameOutputPrune",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            string outputDir = Path.Combine(outputBaseDir, "SameOutputPrune");
            string stalePath = Path.Combine(outputDir, "external.lr2folder");
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(stalePath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = stalePath, title = "stale", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string expectedPath = Path.Combine(outputDir, "0001.lr2folder");
            Assert.IsTrue(File.Exists(expectedPath));
            Assert.IsFalse(File.Exists(stalePath));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", expectedPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
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
    public void ReOutputCustomFolderAndCommitToDB_PrunesDbOnlyStaleHierarchicalRowsInSameOutputDirectory()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder;
            var table = new BMSTable
            {
                playlist_id = 7307,
                name = "HierarchyPrune",
                symbol = "HP",
                Output_dir = "HierarchyPrune",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries =
                [
                    CreateEntry("ffffffffffffffffffffffffffffffff", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "HierarchyPrune");
            string clearDir = Path.Combine(outputDir, "CLEAR FOLDER");
            string noPlayDir = Path.Combine(clearDir, "0 NO PLAY");
            string noPlayFile = Path.Combine(noPlayDir, "0000.lr2folder");
            using (var verifyInitial = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(1L, verifyInitial.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", noPlayFile));
                Assert.AreEqual(1L, verifyInitial.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(clearDir)));
                Assert.AreEqual(1L, verifyInitial.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(noPlayDir)));
            }
            Directory.Delete(clearDir, recursive: true);
            Assert.IsFalse(Directory.Exists(clearDir));

            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder;
            playlist.ReOutputCustomFolderAndCommitToDB(table);

            Assert.IsFalse(Directory.Exists(clearDir));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", noPlayFile));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(clearDir)));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(noPlayDir)));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Path.Combine(outputDir, "0000.lr2folder")));
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
    public void ReOutputCustomFoldersAndCommitHeadersToDB_RewritesExistingLr2FolderFiles()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            string outputDir = Path.Combine(outputBaseDir, "BulkRewrite");
            string outputFile = Path.Combine(outputDir, "0000.lr2folder");
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(outputFile, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            DateTime oldTimestamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(outputFile, oldTimestamp);
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = outputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]",
                EnableDownloadLr2IrScoreAndDetectUnsent = Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                providerCallCount++;
                return outputSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7612,
                name = "BulkRewrite",
                symbol = "BR",
                Output_dir = "BulkRewrite",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new TestBmsPlaylist(
                songDbPath,
                () => CreateLr2Config(lr2RootPath, bmsRoot),
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOutputSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_bulk_rewrite");

            string text = ReadShiftJisText(outputFile);
            Assert.AreNotEqual(oldTimestamp, File.GetLastWriteTimeUtc(outputFile));
            Assert.IsTrue(text.IndexOf("#TITLE stale", StringComparison.Ordinal) < 0);
            StringAssert.Contains(text, "#TITLE BulkRewrite ALL");
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder row = verify.Table<LR2SongDB.folder>().Single(item => item.path == outputFile);
            Assert.AreEqual(File.GetLastWriteTimeUtc(outputFile).ToUnixtime(), row.date);
            Assert.AreEqual("BulkRewrite ALL", row.title);
            Assert.AreEqual(1, providerCallCount);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_ProgressUsesFolderTableSyncLabel()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7616,
                name = "ProgressLabel",
                symbol = "PL",
                Output_dir = "ProgressLabel",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty);
            var playlist = CreatePlaylist(songDbPath, synchronization, () => CreateLr2Config(lr2RootPath, bmsRoot));
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            var progressLabels = new List<string>();

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB(
                [table],
                "test_progress_label",
                (_, _, label) => progressLabels.Add(label));

            CollectionAssert.Contains(progressLabels, Resources.Custom_folder_db_sync_progress_single_label);
            Assert.IsFalse(progressLabels.Contains(Resources.Lr2_song_db_sync_status_running));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReloadTables_RepairsRootOutputDirectoryRowWhenJukeboxRootWasMissing()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string settingsRootOutputBaseDir = Path.Combine(tempDirectory, "SettingsRootOutput");
            string providerRootOutputBaseDir = Path.Combine(tempDirectory, "ProviderRootOutput");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = settingsRootOutputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7619,
                name = "ReloadRootStatusCurrent",
                symbol = "RRSC",
                Output_dir = "ReloadRootStatusCurrent",
                is_root_folder = true,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries)
                {
                    entry.playlist_id = table.playlist_id;
                    db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            LR2Config config = CreateLr2Config(lr2RootPath, bmsRoot);
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "ProviderNormalOutput"),
                LR2CustomFolderOutputBaseDirRootType = providerRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            var synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                () => config,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                 () => new BeatorajaBmtOptionsSnapshot(),
                 () => outputSettings,
                 synchronization)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_reload_root_status_current");
            string outputDirectory = Path.Combine(providerRootOutputBaseDir, "ReloadRootStatusCurrent");
            string outputFile = Path.Combine(outputDirectory, "0001.lr2folder");
            DateTime outputFileTimestamp = File.GetLastWriteTimeUtc(outputFile);
            string outputDirectoryRowPath = Lr2FolderPath.ToFolderPath(outputDirectory);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Execute("DELETE FROM folder WHERE path = ?;", outputDirectoryRowPath);
            }
            config.SetBMSSearchDirectories([bmsRoot]);
            config.Save();
            var scheduledTasks = new List<Task>();
            playlist.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                scheduledTasks.Add(work());
                return true;
            };

            playlist.ReloadTables(queueBeatorajaBmtExportAfterHydration: false);
            Task.WaitAll([.. scheduledTasks]);

            CollectionAssert.Contains(config.GetBMSSearchDirectories(), outputDirectory);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), settingsRootOutputBaseDir);
            Assert.AreEqual(outputFileTimestamp, File.GetLastWriteTimeUtc(outputFile));
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder outputDirectoryRow = verify.Table<LR2SongDB.folder>().Single(row => row.path == outputDirectoryRowPath);
            Assert.AreEqual(1, outputDirectoryRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, outputDirectoryRow.parent);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void RemoveCustomFolder_ClearsCustomFolderOutputStatus()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7618,
                name = "StatusDelete",
                symbol = "SD",
                Output_dir = "StatusDelete",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);
            using (var before = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(
                    1L,
                    before.ExecuteScalar<long>(
                        "SELECT COUNT(1) FROM playlist_custom_folder_output_status WHERE playlist_id = ?;",
                        table.playlist_id.Value));
            }

            playlist.RemoveCustomFolder(table);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                0L,
                verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM playlist_custom_folder_output_status WHERE playlist_id = ?;",
                    table.playlist_id.Value));
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
    public void ReOutputCustomFoldersAndCommitHeadersToDB_DisablingAllOutputPrunesFilesAndRows()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7613,
                name = "DisableAllOutput",
                symbol = "DAO",
                Output_dir = "DisableAllOutput",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A"),
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder B")
                ],
                Folder_order = ["Folder A", "Folder B"]
            };
            var playlist = new TestBmsPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot), CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_disable_all_output");
            string outputDir = Path.Combine(outputBaseDir, "DisableAllOutput");
            Assert.IsTrue(Directory.Exists(outputDir));
            Assert.IsTrue(Directory.EnumerateFiles(outputDir, "*.lr2folder", System.IO.SearchOption.AllDirectories).Any());
            using (var verifySeed = new LR2SongDBExtended(songDbPath))
            {
                Assert.IsTrue(verifySeed.Table<LR2SongDB.folder>().ToList()
                    .Any(row => row.path != null && row.path.StartsWith(outputDir, StringComparison.OrdinalIgnoreCase)));
            }

            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders;
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_disable_all_output");

            Assert.IsFalse(Directory.Exists(outputDir)
                && Directory.EnumerateFiles(outputDir, "*.lr2folder", System.IO.SearchOption.AllDirectories).Any());
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList()
                .Count(row => row.path != null && row.path.StartsWith(outputDir, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_DisablingAllOutputPrunesDbOnlyRootRows()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string rootOutputBaseDir = Path.Combine(lr2RootPath, "LR2files", "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7614,
                name = "DisableAllRootOutput",
                symbol = "DAR",
                Output_dir = "DisableAllRootOutput",
                is_root_folder = true,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_disable_all_root_output");
            string outputDir = Path.Combine(rootOutputBaseDir, "DisableAllRootOutput");
            string relativeOutputDir = Path.Combine("LR2files", "CustomFolder", "DisableAllRootOutput");
            using (var verifySeed = new LR2SongDBExtended(songDbPath))
            {
                Assert.IsTrue(verifySeed.Table<LR2SongDB.folder>().ToList()
                    .Any(row => row.path != null && row.path.StartsWith(relativeOutputDir, StringComparison.OrdinalIgnoreCase)));
            }
            Directory.Delete(outputDir, recursive: true);

            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders;
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_disable_all_root_output");

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList()
                .Count(row => row.path != null && row.path.StartsWith(relativeOutputDir, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_DoesNotCommitHeaderWhenOutputFails()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            LR2SongDBExtended.playlist.CustomFolderType initialMask =
                LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder;
            var table = new BMSTable
            {
                playlist_id = 7615,
                name = "CommitFailure",
                symbol = "CF",
                Output_dir = "CommitFailure",
                ignore_folder_output = initialMask,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new TestBmsPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot), CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_commit_failure");
            using (var verifySeed = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(
                    (int)initialMask,
                    verifySeed.ExecuteScalar<int>("SELECT ignore_folder_output FROM playlist WHERE playlist_id = 7615;"));
            }

            string outputBaseFile = Path.Combine(tempDirectory, "output-base-file");
            File.WriteAllText(outputBaseFile, "not a directory", Encoding.UTF8);
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseFile;
            table.symbol = "CF2";

            Exception? exception = null;
            try
            {
                playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_commit_failure");
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            Assert.IsNotNull(exception);
            Assert.IsTrue(
                exception is IOException
                || exception is UnauthorizedAccessException
                || exception is ArgumentException
                || exception is NotSupportedException
                || exception is PathTooLongException,
                exception.GetType().FullName);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                (int)initialMask,
                verify.ExecuteScalar<int>("SELECT ignore_folder_output FROM playlist WHERE playlist_id = 7615;"));
            Assert.AreEqual(
                "CF",
                verify.ExecuteScalar<string>("SELECT symbol FROM playlist WHERE playlist_id = 7615;"));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputAllCustomFoldersForLr2SongDbSync_EmptyTablesReleasePreparationReservation()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousRootPath = Settings.Default.LR2RootPath;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "ProviderOutput"),
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                providerCallCount++;
                return outputSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var library = new TestBmsLibrary(songDbPath);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOutputSettings,
                library.Lr2PlaylistFolderSynchronization)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            var runtime = new BmsLr2SongDbSyncWorkflowRuntime(
                () => library,
                () => playlist,
                () => true);

            Lr2SongDbSyncPreparedDataSurface surface = null!;
            Assert.IsTrue(runtime.TryRunDataPreparation(
                "test_empty_snapshot",
                includeBuiltinGeneratedData: false));
            surface = ((BMSLibrary.Lr2SynchronizationOwner)library.Lr2Synchronization)
                .TakeLr2SongDbSyncPreparedDataSurface(out _);

            Assert.AreEqual(1, providerCallCount);
            Assert.IsNotNull(surface);
            using LibraryFileMutationLease fresh = library.Lr2Synchronization.TryBeginMutation(
                "test_empty_snapshot_after_release",
                showMessage: false);
            Assert.IsNotNull(fresh);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2RootPath = previousRootPath;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Lr2SongDbSyncCustomFolderPreparation_RemovesExtraLr2FolderInManagedOutputDirectory()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7604,
                name = "ExternalCoLocated",
                symbol = "EC",
                Output_dir = "ExternalCoLocated",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("cccccccccccccccccccccccccccccccc", "Folder C")
                ],
                Folder_order = ["Folder C"]
            };
            Func<LR2Config> lr2ConfigProvider = () => CreateLr2Config(lr2RootPath, bmsRoot);
            var library = new TestBmsLibrary(songDbPath, lr2ConfigProvider);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                lr2ConfigProvider,
                null,
                null,
                null,
                () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(Settings.Default),
                () => BeatorajaBmtOptionsSnapshot.CreateCurrent(Settings.Default),
                () => CustomFolderOutputSettingsSnapshot.CreateCurrent(Settings.Default),
                library.Lr2PlaylistFolderSynchronization,
                mutationLeaseProvider: operation => library.Lr2Synchronization.TryBeginMutation(
                    operation,
                    showMessage: false))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_external_colocated");
            string outputDir = Path.Combine(outputBaseDir, "ExternalCoLocated");
            string expectedFile = Path.Combine(outputDir, "0000.lr2folder");
            string extraFile = Path.Combine(outputDir, "external.lr2folder");
            File.WriteAllText(extraFile, "#TITLE External", Encoding.GetEncoding("shift_jis"));
            DateTime extraTimestamp = new(2026, 6, 2, 1, 2, 3, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(extraFile, extraTimestamp);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Insert(new LR2SongDB.folder
                {
                    path = extraFile,
                    title = "External",
                    type = 2,
                    parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputDir),
                    date = extraTimestamp.ToUnixtime(),
                    adddate = extraTimestamp.ToUnixtime()
                });
            }

            var runtime = new BmsLr2SongDbSyncWorkflowRuntime(
                () => library,
                () => playlist,
                () => true);
            Lr2SongDbSyncPreparedDataSurface surface = null!;
            Assert.IsTrue(runtime.TryRunDataPreparation(
                "test_external_colocated",
                includeBuiltinGeneratedData: false));
            surface = ((BMSLibrary.Lr2SynchronizationOwner)library.Lr2Synchronization)
                .TakeLr2SongDbSyncPreparedDataSurface(out _);

            Assert.IsFalse(File.Exists(extraFile));
            CollectionAssert.Contains(surface.Lr2FolderFilePaths.ToList(), expectedFile);
            Assert.AreEqual(0, surface.Lr2FolderScopeDirectories.Count);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", extraFile));
            using LibraryFileMutationLease fresh = library.Lr2Synchronization.TryBeginMutation(
                "test_external_colocated_after_release",
                showMessage: false);
            Assert.IsNotNull(fresh);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_ProtectsNestedManagedOutputDirectory()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var parentTable = new BMSTable
            {
                playlist_id = 7607,
                name = "NestedParent",
                symbol = "NP",
                Output_dir = "NestedParent",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Parent Folder")
                ],
                Folder_order = ["Parent Folder"]
            };
            var childTable = new BMSTable
            {
                playlist_id = 7608,
                name = "NestedChild",
                symbol = "NC",
                Output_dir = Path.Combine("NestedParent", "NestedChild"),
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Child Folder")
                ],
                Folder_order = ["Child Folder"]
            };
            var playlist = new TestBmsPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot), CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { parentTable, childTable })
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB(
                [parentTable, childTable],
                "test_seed_nested_outputs");
            string childOutputDir = Path.Combine(outputBaseDir, "NestedParent", "NestedChild");
            string childOutputFile = Path.Combine(childOutputDir, "0000.lr2folder");
            Assert.IsTrue(File.Exists(childOutputFile));
            DateTime childTimestamp = File.GetLastWriteTimeUtc(childOutputFile);

            playlist.ReOutputCustomFolderAndCommitToDB(parentTable);

            Assert.IsTrue(File.Exists(childOutputFile));
            Assert.AreEqual(childTimestamp, File.GetLastWriteTimeUtc(childOutputFile));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", childOutputFile));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(childOutputDir)));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableEntry_ReoutputsLr2FolderRowsForLevelProjection()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string providerOutputBaseDir = Path.Combine(tempDirectory, "ProviderCustomFolder");
            string globalOutputBaseDir = Path.Combine(tempDirectory, "GlobalCustomFolder");
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = globalOutputBaseDir;
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootCustomFolder"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                providerCallCount++;
                return outputSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTableEntry entry = CreateEntryWithLevel("cccccccccccccccccccccccccccccccc", 1);
            entry.playlist_id = 7303;
            var table = new BMSTable
            {
                playlist_id = 7303,
                name = "LevelTable",
                symbol = "LT",
                Output_dir = "LevelTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.LevelFolder,
                entries = [entry]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOutputSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            playlist.ReOutputCustomFolderAndCommitToDB(table);
            providerCallCount = 0;
            entry.level = 12;

            playlist.CommitBMSTableEntry(entry);

            using var verify = new LR2SongDBExtended(songDbPath);
            List<LR2SongDB.folder> folders = verify.Table<LR2SongDB.folder>().ToList();
            Assert.AreEqual(1, providerCallCount);
            Assert.IsTrue(Directory.Exists(Path.Combine(providerOutputBaseDir, table.Output_dir)));
            Assert.IsFalse(Directory.Exists(Path.Combine(globalOutputBaseDir, table.Output_dir)));
            Assert.AreEqual(0, folders.Count(row => row.title == "LEVEL 1"));
            LR2SongDB.folder levelFolder = folders.Single(row => row.title == "LEVEL 12");
            Assert.AreEqual(2, levelFolder.type);
            StringAssert.Contains(levelFolder.command, "playlist_entry");
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

    private static string CreateBaseScoreDbPath(string tempDirectory)
    {
        string scoreDbPath = Path.Combine(tempDirectory, "score.db");
        using (var db = new LR2ScoreDBExtended(scoreDbPath))
        {
            db.CreateTable<LR2ScoreDB.score>();
            db.CreateTable<LR2ScoreDB.player>();
        }
        return scoreDbPath;
    }

    private static string CreateInstalledPlayHistoryScoreDbPath(string tempDirectory)
    {
        string scoreDbPath = CreateBaseScoreDbPath(tempDirectory);
        Lr2PlayHistorySchemaCheckResult result = new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);
        Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, result.Status);
        return scoreDbPath;
    }

    private static string ReadShiftJisText(string path)
    {
        using FileStream stream = LongPathFileSystem.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.GetEncoding("shift_jis"), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string BuildLongDirectoryPath(string root, string leaf)
    {
        string path = root;
        while (Path.Combine(path, leaf).Length <= 270)
        {
            path = Path.Combine(path, "segment-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        }
        return Path.Combine(path, leaf);
    }

    private static void AssertClearFolderCommandMatchesAssistAndEasyRows(string songDbPath, string assistFolderText, string easyFolderText)
    {
        const string AssistOnlyHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string AssistEasyHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string AssistNoEasyHash = "cccccccccccccccccccccccccccccccc";
        const string AssistNullHistoryHash = "dddddddddddddddddddddddddddddddd";
        string assistCommand = ReadCustomFolderCommand(assistFolderText);
        string easyCommand = ReadCustomFolderCommand(easyFolderText);

        using var db = new LR2SongDBExtended(songDbPath);
        db.Execute("CREATE TABLE IF NOT EXISTS song(hash TEXT PRIMARY KEY, title TEXT);");
        db.Execute("CREATE TABLE IF NOT EXISTS score(hash TEXT PRIMARY KEY, clear INTEGER, rank INTEGER, op_history INTEGER, minbp INTEGER);");
        db.Execute("DELETE FROM playlist_entry WHERE playlist_id = 7305 AND md5 IN (?, ?, ?, ?);", AssistOnlyHash, AssistEasyHash, AssistNoEasyHash, AssistNullHistoryHash);
        InsertCustomFolderClassificationRow(db, AssistOnlyHash, clear: 2, rank: 8, opHistory: ClearTypeStorageConverter.OptionHistoryAssist);
        InsertCustomFolderClassificationRow(db, AssistEasyHash, clear: 2, rank: 0, opHistory: ClearTypeStorageConverter.OptionHistoryAssist | ClearTypeStorageConverter.OptionHistoryEasy);
        InsertCustomFolderClassificationRow(db, AssistNoEasyHash, clear: 2, rank: 0, opHistory: 0);
        InsertCustomFolderClassificationRow(db, AssistNullHistoryHash, clear: 2, rank: 0, opHistory: null);

        Assert.AreEqual(1L, CountCustomFolderCommandMatches(db, assistCommand, AssistOnlyHash));
        Assert.AreEqual(0L, CountCustomFolderCommandMatches(db, assistCommand, AssistEasyHash));
        Assert.AreEqual(1L, CountCustomFolderCommandMatches(db, assistCommand, AssistNoEasyHash));
        Assert.AreEqual(1L, CountCustomFolderCommandMatches(db, assistCommand, AssistNullHistoryHash));
        Assert.AreEqual(0L, CountCustomFolderCommandMatches(db, easyCommand, AssistOnlyHash));
        Assert.AreEqual(1L, CountCustomFolderCommandMatches(db, easyCommand, AssistEasyHash));
        Assert.AreEqual(0L, CountCustomFolderCommandMatches(db, easyCommand, AssistNoEasyHash));
        Assert.AreEqual(0L, CountCustomFolderCommandMatches(db, easyCommand, AssistNullHistoryHash));
    }

    private static void InsertCustomFolderClassificationRow(LR2SongDBExtended db, string hash, int clear, int rank, int? opHistory)
    {
        db.Execute("INSERT OR REPLACE INTO song(hash, title, path) VALUES (?, ?, ?);", hash, hash, hash + ".bms");
        db.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, is_removed) VALUES (7305, ?, ?, 0);", hash, hash);
        db.Execute("INSERT OR REPLACE INTO score(hash, clear, rank, op_history, minbp) VALUES (?, ?, ?, ?, 0);", hash, clear, rank, opHistory);
    }

    private static long CountCustomFolderCommandMatches(LR2SongDBExtended db, string command, string hash)
    {
        return db.ExecuteScalar<long>("SELECT COUNT(1) FROM song LEFT JOIN score ON song.hash = score.hash WHERE song.hash = ? AND " + command, hash);
    }

    private static string ReadCustomFolderCommand(string folderText)
    {
        string line = folderText
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .FirstOrDefault(value => value.StartsWith("#COMMAND ", StringComparison.Ordinal));
        Assert.IsFalse(string.IsNullOrWhiteSpace(line));
        return line.Substring("#COMMAND ".Length);
    }

    private async Task PlaylistPropertyDialogApplyPostSaveUpdatesCoreAsync()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        Dispatcher previousDispatcher = DispatcherHelper.UIDispatcher;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            DispatcherHelper.UIDispatcher = Dispatcher.CurrentDispatcher;
            string initialNormalOutputBaseDir = Path.Combine(tempDirectory, "InitialNormalCustomFolder");
            string initialRootOutputBaseDir = Path.Combine(tempDirectory, "InitialRootCustomFolder");
            string changedNormalOutputBaseDir = Path.Combine(tempDirectory, "ChangedNormalCustomFolder");
            string changedRootOutputBaseDir = Path.Combine(tempDirectory, "ChangedRootCustomFolder");
            string additionalOutputBaseDir = Path.Combine(tempDirectory, "AdditionalCustomFolder");
            string additionalOutputBaseName = Path.GetFileName(additionalOutputBaseDir);
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "GlobalNormalCustomFolder");
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "GlobalRootCustomFolder");
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            CustomFolderOutputSettingsSnapshot initialSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = tempDirectory,
                LR2CustomFolderOutputBaseDir = initialNormalOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = initialRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            CustomFolderOutputSettingsSnapshot changedSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = tempDirectory,
                LR2CustomFolderOutputBaseDir = changedNormalOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = changedRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            CustomFolderOutputSettingsSnapshot additionalSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = tempDirectory,
                LR2CustomFolderOutputBaseDir = changedNormalOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = changedRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs =
                    CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBaseDir])
            };
            CustomFolderOutputSettingsSnapshot currentSettings = initialSettings;
            int viewModelProviderCallCount = 0;
            int playlistProviderCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getViewModelSettings = () =>
            {
                viewModelProviderCallCount++;
                return currentSettings;
            };
            Func<CustomFolderOutputSettingsSnapshot> getPlaylistSettings = () =>
            {
                playlistProviderCallCount++;
                return new CustomFolderOutputSettingsSnapshot
                {
                    OperationModeLR2DB = false
                };
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            BMSTableEntry extraEntry = CreateEntry("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "Folder E");
            extraEntry.folder = "Folder E";
            var table = new BMSTable
            {
                playlist_id = 7312,
                name = "DialogSnapshotSettings",
                symbol = "DSS",
                Output_dir = "DialogSnapshotSettings",
                entries =
                [
                    CreateEntry("dddddddddddddddddddddddddddddddd", "Folder D"),
                    extraEntry
                ],
                Folder_order = ["Folder D"]
            };
            string oldOutputPath = Path.Combine(initialNormalOutputBaseDir, table.Output_dir, "0000.lr2folder");
            Directory.CreateDirectory(Path.GetDirectoryName(oldOutputPath));
            File.WriteAllText(oldOutputPath, "#TITLE stale normal output", Encoding.GetEncoding("shift_jis"));
            LR2Config config = CreateLr2Config(tempDirectory, Path.Combine(tempDirectory, "ManualBmsRoot"));
            var playlist = new TestBmsPlaylist(
                songDbPath,
                () => config,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getPlaylistSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty))
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            var library = new TestBmsLibrary(songDbPath);
            PlaylistWorkspaceViewModel workspace = CreatePlaylistWorkspace(
                playlist,
                library,
                getViewModelSettings,
                () => config);

            dialog = await workspace.OpenPropertyDialogAsync(table);
            Assert.IsFalse(playlist.IsWriteLockHeldBMSTables);
            CollectionAssert.AreEquivalent(
                new[] { "Folder D", "Folder E" },
                dialog.folder_order.ToArray());
            currentSettings = changedSettings;
            dialog.is_root_folder = true;
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            PlaylistPropertyDialogViewModel savedDialog = dialog;
            savedDialog.Dispose();
            Assert.IsFalse(playlist.IsWriteLockHeldBMSTables);
            dialog = null;

            string newOutputDirectory = Path.Combine(initialRootOutputBaseDir, table.Output_dir);
            Assert.AreEqual(1, viewModelProviderCallCount);
            Assert.AreEqual(0, playlistProviderCallCount);
            Assert.IsTrue(table.is_root_folder);
            Assert.IsFalse(File.Exists(oldOutputPath));
            Assert.IsTrue(File.Exists(Path.Combine(newOutputDirectory, "0001.lr2folder")));
            Assert.IsFalse(Directory.Exists(Path.Combine(changedRootOutputBaseDir, table.Output_dir)));
            CollectionAssert.Contains(config.GetBMSSearchDirectoriesForChangeTracking(), newOutputDirectory);
            var reloadedConfig = new LR2Config(Path.Combine(tempDirectory, "LR2files", "Config", "config.xml"));
            CollectionAssert.Contains(reloadedConfig.GetBMSSearchDirectoriesForChangeTracking(), newOutputDirectory);

            currentSettings = additionalSettings;
            dialog = await workspace.OpenPropertyDialogAsync(table);
            PlaylistCustomFolderOutputBaseOption additionalBaseOption = dialog.OutputBaseOptions
                .Single(option => string.Equals(option.BaseName, additionalOutputBaseName, StringComparison.OrdinalIgnoreCase));
            dialog.is_root_folder = false;
            dialog.custom_folder_output_base_option = additionalBaseOption;
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            dialog.Dispose();
            dialog = null;

            string additionalOutputDirectory = Path.Combine(
                additionalOutputBaseDir,
                table.Output_dir ?? BMSTable.CreateDefaultOutputDirectoryName(table.name));
            Assert.IsTrue(Directory.Exists(additionalOutputDirectory));
            Assert.IsTrue(Directory.GetFiles(additionalOutputDirectory, "*.lr2folder", SearchOption.AllDirectories).Length > 0);
            using (var verifyAdditional = new LR2SongDBExtended(songDbPath))
            {
                LR2SongDBExtended.playlist persistedAdditional = verifyAdditional.Table<LR2SongDBExtended.playlist>()
                    .Single(row => row.playlist_id == table.playlist_id);
                Assert.AreEqual(additionalOutputBaseName, persistedAdditional.custom_folder_output_base_name);
                Assert.IsTrue(verifyAdditional.Table<LR2SongDB.folder>().Any(row =>
                    row.path.StartsWith(additionalOutputDirectory, StringComparison.OrdinalIgnoreCase)));
            }

            currentSettings = initialSettings;
            bool inlineSaved = (await workspace.CompleteSummaryPropertyEditAsync(
                new PlaylistSummaryRow { TableRef = table },
                nameof(PlaylistSummaryRow.Symbol),
                "DSS2",
                commit: true)).IsApplied;
            Assert.IsTrue(inlineSaved);
            Assert.AreEqual("DSS2", table.symbol);
            using (var verifyInline = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(
                    "DSS2",
                    verifyInline.Table<LR2SongDBExtended.playlist>()
                        .Single(row => row.playlist_id == table.playlist_id)
                        .symbol);
            }

            currentSettings = additionalSettings;
            dialog = await workspace.OpenPropertyDialogAsync(table);
            dialog.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.UserFolder;
            dialog.symbol = "DSS3";
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.SaveAndApplyAsync());
            dialog.Dispose();
            dialog = null;
            Assert.AreEqual(
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                table.ignore_folder_output);
            using (var verifyMask = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(
                    (int)LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                    verifyMask.ExecuteScalar<int>(
                        "SELECT ignore_folder_output FROM playlist WHERE playlist_id = ?;",
                        table.playlist_id));
            }
        }
        finally
        {
            dialog?.Dispose();
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            DispatcherHelper.UIDispatcher = previousDispatcher;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

}
