using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

using static BeMusicSeeker.Tests.BmsPlaylistTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>保存済み表の出力先移行、実登録公開とBMT終端の確定状態を確認します。</summary>
/// <remarks>削除範囲と旧配置、SQL保存順は実ファイル・SQLiteで観測します。Windowを作らず実ownerまで接続し、描画・ダイアログ所有は上位の代表へ分担します。</remarks>
[TestClass]
public sealed class BmsPlaylistMigrationAndRegistrationTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();

    /// <summary>通常型からルート型へ実移行し、旧ファイル・旧親DB行を除去して新しい表ルート階層を保存します。型変更で旧投影を残す誤りと親hashの混同を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB_PrunesOldRootFlagOutputRows()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string rootOutputBaseDir = Path.Combine(tempDirectory, "RootCustomFolder");
            testSettings.OperationModeLR2DB = false;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir;
            testSettings.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            CustomFolderOutputSettingsSnapshot migrationSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = testSettings.LR2RootPath,
                LR2CustomFolderOutputBaseDir = outputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]",
                EnableDownloadLr2IrScoreAndDetectUnsent = testSettings.EnableDownloadLr2IrScoreAndDetectUnsent
            };
            Func<CustomFolderOutputSettingsSnapshot> getMigrationSettings = () =>
            {
                return migrationSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7304,
                name = "ToggleTable",
                symbol = "TT",
                Output_dir = "ToggleTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("dddddddddddddddddddddddddddddddd", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            string oldOutputDir = Path.Combine(outputBaseDir, "ToggleTable");
            string oldPath = Path.Combine(oldOutputDir, "0000.lr2folder");
            string oldParentPath = Lr2FolderPath.ToFolderPath(oldOutputDir);
            Directory.CreateDirectory(oldOutputDir);
            File.WriteAllText(oldPath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = oldPath, title = "stale", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = oldParentPath, title = "stale parent", type = 1 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getMigrationSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            table.is_root_folder = true;
            string newOutputDir = Path.Combine(rootOutputBaseDir, "ToggleTable");

            playlist.MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
                [table], new Dictionary<BMSTable, string> { [table] = oldOutputDir }, "test_root_migration",
                outputBaseDirPathBeforeByTable: new Dictionary<BMSTable, string> { [table] = outputBaseDir });

            string newPath = Path.Combine(newOutputDir, "0001.lr2folder");
            Assert.IsFalse(File.Exists(oldPath));
            Assert.IsTrue(File.Exists(newPath));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == oldPath));
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == oldParentPath));
            LR2SongDB.folder generated = verify.Table<LR2SongDB.folder>().Single(row => row.path == newPath);
            Assert.AreEqual("Folder A", generated.title);
            Assert.AreEqual(
                Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(newPath)),
                generated.parent);
            LR2SongDB.folder parentFolder = verify.Table<LR2SongDB.folder>().Single(row => row.path == Lr2FolderPath.ToFolderPath(Path.GetDirectoryName(newPath)));
            Assert.AreEqual(1, parentFolder.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, parentFolder.parent);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>ルート型から通常型へ移行し、旧物理出力とLR2相対パスのファイル・親DB行を除去します。絶対パスの整理だけで旧ルート行を残す誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB_PrunesOldRootRelativeRowsWhenMovingToNormalOutput()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string rootOutputBaseDir = Path.Combine(lr2RootPath, "LR2files", "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir;
            testSettings.LR2RootPath = lr2RootPath;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7314,
                name = "RootToNormal",
                symbol = "RTN",
                Output_dir = "RootToNormal",
                is_root_folder = false,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            string oldOutputDir = Path.Combine(rootOutputBaseDir, "RootToNormal");
            string oldPhysicalPath = Path.Combine(oldOutputDir, "0000.lr2folder");
            string oldRelativeOutputDir = Path.Combine("LR2files", "CustomFolder", "RootToNormal");
            string oldRelativePath = Path.Combine(oldRelativeOutputDir, "0000.lr2folder");
            string oldRelativeParentPath = oldRelativeOutputDir + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(oldOutputDir);
            File.WriteAllText(oldPhysicalPath, "#TITLE stale root", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = oldRelativePath, title = "stale root", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = oldRelativeParentPath, title = "stale root parent", type = 1 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            string newOutputDir = Path.Combine(outputBaseDir, "RootToNormal");

            playlist.MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
                [table], new Dictionary<BMSTable, string> { [table] = oldOutputDir }, "test_root_to_normal",
                wasRootFolderBeforeByTable: new Dictionary<BMSTable, bool> { [table] = true },
                rootOutputBaseDirBefore: rootOutputBaseDir);

            string newPath = Path.Combine(newOutputDir, "0000.lr2folder");
            Assert.IsFalse(Directory.Exists(oldOutputDir));
            Assert.IsTrue(File.Exists(newPath));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", oldRelativePath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", oldRelativeParentPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", newPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>一表の出力先を移し、旧管理先内の階層ファイルと通常ファイルをディレクトリごと除去して新先へ生成します。直下のlr2folderだけを整理する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB_DeletesOldOutputDirectoryRecursively()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string oldOutputBaseDir = Path.Combine(tempDirectory, "OldNormal");
            string parentOldOutputDir = Path.Combine(oldOutputBaseDir, "Parent");
            string parentNewOutputDir = Path.Combine(tempDirectory, "NewNormal", "Parent");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "NewNormal");
            testSettings.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var parentTable = new BMSTable
            {
                playlist_id = 7313,
                name = "Parent",
                symbol = "P",
                Output_dir = "Parent",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Parent Folder")
                ],
                Folder_order = ["Parent Folder"]
            };
            string parentOldPath = Path.Combine(parentOldOutputDir, "0000.lr2folder");
            string nestedOldPath = Path.Combine(parentOldOutputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder");
            string unmanagedOldPath = Path.Combine(parentOldOutputDir, "manual-note.txt");
            Directory.CreateDirectory(parentOldOutputDir);
            Directory.CreateDirectory(Path.GetDirectoryName(nestedOldPath)!);
            File.WriteAllText(parentOldPath, "#TITLE stale parent", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(nestedOldPath, "#TITLE nested stale", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(unmanagedOldPath, "old custom folder note", Encoding.UTF8);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = parentOldPath, title = "stale parent", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = nestedOldPath, title = "nested stale", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { parentTable })
            };

            playlist.MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
                [parentTable], new Dictionary<BMSTable, string> { [parentTable] = parentOldOutputDir }, "test_directory_migration",
                outputBaseDirPathBeforeByTable: new Dictionary<BMSTable, string> { [parentTable] = oldOutputBaseDir });

            string parentNewPath = Path.Combine(parentNewOutputDir, "0000.lr2folder");
            Assert.IsFalse(Directory.Exists(parentOldOutputDir));
            Assert.IsTrue(File.Exists(parentNewPath));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", parentOldPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", nestedOldPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", parentNewPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>注入配置へ実一括移行して旧folder行を整理しても、モデルに載らない保存済みplaylist_entryを保持します。出力移行で正本行を置換する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB_DoesNotRewritePlaylistEntries()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string oldOutputBaseDir = Path.Combine(tempDirectory, "OldCustomFolder");
            string newOutputBaseDir = Path.Combine(tempDirectory, "NewCustomFolder");
            testSettings.OperationModeLR2DB = false;
            testSettings.LR2CustomFolderOutputBaseDir = newOutputBaseDir;
            testSettings.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            CustomFolderOutputSettingsSnapshot migrationSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = testSettings.LR2RootPath,
                LR2CustomFolderOutputBaseDir = newOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]",
                EnableDownloadLr2IrScoreAndDetectUnsent = testSettings.EnableDownloadLr2IrScoreAndDetectUnsent
            };
            Func<CustomFolderOutputSettingsSnapshot> getMigrationSettings = () =>
            {
                return migrationSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7310,
                name = "BulkMoveTable",
                symbol = "BMT",
                Output_dir = "BulkMoveTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            string oldOutputDir = Path.Combine(oldOutputBaseDir, "BulkMoveTable");
            string oldPath = Path.Combine(oldOutputDir, "0000.lr2folder");
            string oldParentPath = Lr2FolderPath.ToFolderPath(oldOutputDir);
            Directory.CreateDirectory(oldOutputDir);
            File.WriteAllText(oldPath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            BMSTableEntry sentinelEntry = CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Sentinel Folder");
            sentinelEntry.playlist_id = table.playlist_id;
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(sentinelEntry, typeof(LR2SongDBExtended.playlist_entry));
                db.InsertOrReplace(new LR2SongDB.folder { path = oldPath, title = "stale", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = oldParentPath, title = "stale parent", type = 1 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getMigrationSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
                [table],
                new Dictionary<BMSTable, string> { [table] = oldOutputDir },
                "test_bulk_output_base_move",
                outputBaseDirPathBeforeByTable: new Dictionary<BMSTable, string> { [table] = oldOutputBaseDir });

            string newOutputDir = Path.Combine(newOutputBaseDir, "BulkMoveTable");
            string newPath = Path.Combine(newOutputDir, "0000.lr2folder");
            Assert.IsFalse(File.Exists(oldPath));
            Assert.IsTrue(File.Exists(newPath));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", oldPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", oldParentPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", newPath));
            Assert.AreEqual(
                1L,
                verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = ? AND md5 = ?;",
                    table.playlist_id,
                    sentinelEntry.md5));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>二表を一括移行し、それぞれの旧管理ディレクトリを階層ごと除去して新先のファイル・DB行を生成します。バッチの一部だけ整理・生成する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB_DeletesOldOutputDirectoriesRecursively()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string oldOutputBaseDir = Path.Combine(tempDirectory, "OldNormal");
            string newOutputBaseDir = Path.Combine(tempDirectory, "NewNormal");
            string parentOldOutputDir = Path.Combine(oldOutputBaseDir, "Parent");
            string childOldOutputDir = Path.Combine(oldOutputBaseDir, "Child");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = newOutputBaseDir;
            testSettings.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var parentTable = new BMSTable
            {
                playlist_id = 7311,
                name = "Parent",
                symbol = "P",
                Output_dir = "Parent",
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
                playlist_id = 7312,
                name = "Child",
                symbol = "C",
                Output_dir = "Child",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Child Folder")
                ],
                Folder_order = ["Child Folder"]
            };
            string parentOldPath = Path.Combine(parentOldOutputDir, "0000.lr2folder");
            string parentOldNestedPath = Path.Combine(parentOldOutputDir, "DJ LEVEL", "AAA", "0000.lr2folder");
            string parentOldUnmanagedPath = Path.Combine(parentOldOutputDir, "manual-note.txt");
            string childOldPath = Path.Combine(childOldOutputDir, "0000.lr2folder");
            Directory.CreateDirectory(parentOldOutputDir);
            Directory.CreateDirectory(Path.GetDirectoryName(parentOldNestedPath)!);
            Directory.CreateDirectory(childOldOutputDir);
            File.WriteAllText(parentOldPath, "#TITLE stale parent", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(parentOldNestedPath, "#TITLE nested stale parent", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(parentOldUnmanagedPath, "old custom folder note", Encoding.UTF8);
            File.WriteAllText(childOldPath, "#TITLE stale child", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = parentOldPath, title = "stale parent", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = parentOldNestedPath, title = "nested stale parent", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = childOldPath, title = "stale child", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { parentTable, childTable })
            };

            playlist.MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
                [parentTable, childTable],
                new Dictionary<BMSTable, string>
                {
                    [parentTable] = parentOldOutputDir,
                    [childTable] = childOldOutputDir
                },
                "test_bulk_output_base_nested_move",
                outputBaseDirPathBeforeByTable: new Dictionary<BMSTable, string>
                {
                    [parentTable] = oldOutputBaseDir,
                    [childTable] = oldOutputBaseDir
                });

            string parentNewPath = Path.Combine(newOutputBaseDir, "Parent", "0000.lr2folder");
            string childNewPath = Path.Combine(newOutputBaseDir, "Child", "0000.lr2folder");
            Assert.IsFalse(Directory.Exists(parentOldOutputDir));
            Assert.IsFalse(Directory.Exists(childOldOutputDir));
            Assert.IsTrue(File.Exists(parentNewPath));
            Assert.IsTrue(File.Exists(childNewPath));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", parentOldPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", parentOldNestedPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", childOldPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", parentNewPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", childNewPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>旧保存値の包含配置から移行を要求しても、DB・ファイル変更前に拒否します。新先だけを検査して旧親を整理する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void MigrateCustomFolderOutputDirectory_RejectsSavedNestedLayout()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var parentTable = new BMSTable
            {
                playlist_id = 7316,
                name = "BulkMigrationParent",
                symbol = "BMP",
                Output_dir = "BulkMigrationParent",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Parent Folder")],
                Folder_order = ["Parent Folder"]
            };
            var childTable = new BMSTable
            {
                playlist_id = 7317,
                name = "BulkMigrationChild",
                symbol = "BMC",
                Output_dir = Path.Combine("BulkMigrationParent", "BulkMigrationChild"),
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries = [CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Child Folder")],
                Folder_order = ["Child Folder"]
            };
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { parentTable, childTable })
            };
            playlist.CommitBMSTableHeadersToDB([parentTable, childTable]);
            string oldParentDirectory = Path.Combine(outputBaseDir, "BulkMigrationParent");
            string childDirectory = Path.Combine(oldParentDirectory, "BulkMigrationChild");
            string childPath = Path.Combine(childDirectory, "0000.lr2folder");
            Directory.CreateDirectory(childDirectory);
            File.WriteAllText(childPath, "old child");
            using (var db = new LR2SongDBExtended(songDbPath))
            { db.Insert(new LR2SongDB.folder { path = childPath, title = "old child", type = 2 }); }
            string newParentDirectory = Path.Combine(outputBaseDir, "BulkMigrationParentMoved");
            Assert.ThrowsException<InvalidOperationException>(() => playlist.MigrateCustomFolderOutputDirectory(
                parentTable, oldParentDirectory, newParentDirectory));
            Assert.IsFalse(Directory.Exists(newParentDirectory));
            Assert.AreEqual("old child", File.ReadAllText(childPath));
            Assert.AreEqual("BulkMigrationParent", parentTable.Output_dir);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual("BulkMigrationParent", verify.Table<BMSTable>().Single(row => row.playlist_id == 7316).Output_dir);
            Assert.AreEqual(1, verify.Table<LR2SongDB.folder>().Count());
            Assert.AreEqual("old child", verify.Table<LR2SongDB.folder>().Single().title);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>Folderの出力DB行だけを除去し、名前が接頭辞で重なるFolder2の行を保持します。区切りを見ない範囲削除を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void RemoveCustomFolder_PrunesOnlyExactOutputDirectoryRows()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7401,
                name = "Folder",
                symbol = "F",
                Output_dir = "Folder"
            };
            string targetPath = Path.Combine(outputBaseDir, "Folder", "0000.lr2folder");
            string siblingPath = Path.Combine(outputBaseDir, "Folder2", "0000.lr2folder");
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = targetPath, title = "target", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = siblingPath, title = "sibling", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.RemoveCustomFolder(table);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            CollectionAssert.AreEquivalent(
                new[] { siblingPath },
                verify.Table<LR2SongDB.folder>()
                    .ToList()
                    .Select(row => row.path)
                    .Where(path => path.StartsWith(outputBaseDir, StringComparison.OrdinalIgnoreCase))
                    .ToArray());
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>保存値と異なる注入先の出力を物理・DBから削除します。注入配置を無視して旧保存値の先だけを対象にする誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void RemoveCustomFolder_UsesInjectedSettingsForPhysicalAndDatabaseRemoval()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string providerOutputBaseDir = Path.Combine(tempDirectory, "ProviderCustomFolder");
            string globalOutputBaseDir = Path.Combine(tempDirectory, "GlobalCustomFolder");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            testSettings.OperationModeLR2DB = false;
            testSettings.LR2CustomFolderOutputBaseDir = globalOutputBaseDir;
            CustomFolderOutputSettingsSnapshot operationSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootCustomFolder"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            Func<CustomFolderOutputSettingsSnapshot> getOperationSettings = () =>
            {
                return operationSettings;
            };
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7407,
                name = "ProviderSettings",
                symbol = "PS",
                Output_dir = "ProviderSettings"
            };
            string targetDir = Path.Combine(providerOutputBaseDir, table.Output_dir);
            string targetPath = Path.Combine(targetDir, "0000.lr2folder");
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(targetPath, "#TITLE provider target", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = targetPath, title = "provider target", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOperationSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.RemoveCustomFolder(table);

            Assert.IsFalse(Directory.Exists(targetDir));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", targetPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>確認済みの実表削除をworkspaceから注入配置へ配送し、物理・DB・configを整理してsummary更新と通知を届けます。表削除の接続漏れと共有設定への戻りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistWorkspaceRemoveTable_UsesInjectedCustomFolderSettingsSnapshot()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string providerOutputBaseDir = Path.Combine(tempDirectory, "ProviderCustomFolder");
            string providerRootOutputBaseDir = Path.Combine(tempDirectory, "ProviderRootCustomFolder");
            string globalOutputBaseDir = Path.Combine(tempDirectory, "GlobalCustomFolder");
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            testSettings.OperationModeLR2DB = false;
            testSettings.LR2CustomFolderOutputBaseDir = globalOutputBaseDir;
            testSettings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "GlobalRootCustomFolder");
            CustomFolderOutputSettingsSnapshot operationSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = providerOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = providerRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            Func<CustomFolderOutputSettingsSnapshot> getOperationSettings = () =>
            {
                return operationSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7408,
                name = "ViewModelProviderSettings",
                symbol = "VMPS",
                Output_dir = "ViewModelProviderSettings",
                is_root_folder = true,
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Provider")]
            };
            table.entries[0].playlist_id = table.playlist_id;
            string targetDir = Path.Combine(providerRootOutputBaseDir, table.Output_dir);
            string targetPath = Path.Combine(targetDir, "0000.lr2folder");
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(targetPath, "#TITLE provider target", Encoding.GetEncoding("shift_jis"));
            LR2Config config = CreateLr2Config(lr2RootPath, targetDir);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = targetPath, title = "provider target", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries)
                {
                    db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOperationSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };
            var library = new TestBmsLibrary(songDbPath, settings: testSettings);
            var workspace = new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                getOperationSettings,
                PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
                PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
                PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
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
                () => configOwner,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck,
                new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
                {
                    ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
                });
            workspace.ConfigureCatalogNotificationQueue(action => action());
            var notifications = new List<PlaylistOperationNotificationPresentationRequestedEventArgs>();
            workspace.PlaylistOperationNotificationPresentationRequested += (_, request) => notifications.Add(request);
            long summaryDataGenerationBeforeRemoval = workspace.CurrentPlaylistSummaryDataRebuildGeneration;

            await workspace.PlaylistRemovalWorkflow.RemoveSummaryRowsAsync(
                [new PlaylistSummaryRow { TableRef = table }]);

            PlaylistOperationNotificationPresentationRequestedEventArgs notification =
                notifications.Single(request => request.RouteName == "playlist remove custom folder notification");
            Assert.IsNotNull(notification.Receipt);
            Assert.IsTrue(workspace.CurrentPlaylistSummaryDataRebuildGeneration > summaryDataGenerationBeforeRemoval);
            Assert.IsFalse(playlist.ContainsBMSTable(table));
            Assert.IsFalse(Directory.Exists(targetDir));
            Assert.IsFalse(Directory.Exists(Path.Combine(globalOutputBaseDir, table.Output_dir)));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", targetPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist WHERE playlist_id = ?;", table.playlist_id));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = ?;", table.playlist_id));
            // 保存結果は新しいXML readerから確認します。
            var reloadedConfig = new LR2Config(Path.Combine(lr2RootPath, "LR2files", "Config", "config.xml"));
            CollectionAssert.DoesNotContain(
                reloadedConfig.GetBMSSearchDirectoriesForChangeTracking(),
                targetDir);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>ルート型の物理ディレクトリと相対パスのファイル・親DB行を除去し、似た名前の兄弟行を保持します。相対形式の整理漏れと範囲の広がりを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void RemoveCustomFolder_PrunesRootRelativeRows()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string rootOutputBaseDir = Path.Combine(lr2RootPath, "LR2files", "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7402,
                name = "RootDelete",
                symbol = "RD",
                Output_dir = "RootDelete",
                is_root_folder = true
            };
            string targetDir = Path.Combine(rootOutputBaseDir, "RootDelete");
            string targetPhysicalPath = Path.Combine(targetDir, "0000.lr2folder");
            string targetRelativeDir = Path.Combine("LR2files", "CustomFolder", "RootDelete");
            string targetRelativePath = Path.Combine(targetRelativeDir, "0000.lr2folder");
            string targetRelativeParentPath = targetRelativeDir + Path.DirectorySeparatorChar;
            string siblingRelativePath = Path.Combine("LR2files", "CustomFolder", "RootDeleteSibling", "0000.lr2folder");
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(targetPhysicalPath, "#TITLE target", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = targetRelativePath, title = "target", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = targetRelativeParentPath, title = "target parent", type = 1 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = siblingRelativePath, title = "sibling", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.RemoveCustomFolder(table);

            Assert.IsFalse(Directory.Exists(targetDir));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", targetRelativePath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", targetRelativeParentPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", siblingRelativePath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>保存された相対親参照が管理基点の外へ出ても、その外の実ファイルとDB行を削除しません。基点確認を省いた削除を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void RemoveCustomFolder_DoesNotDeletePathEscapingOutputBase()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string siblingDir = Path.Combine(tempDirectory, "Sibling");
            string siblingPath = Path.Combine(siblingDir, "0000.lr2folder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7403,
                name = "UnsafeDelete",
                symbol = "UD",
                Output_dir = ".." + Path.DirectorySeparatorChar + "Sibling"
            };
            Directory.CreateDirectory(siblingDir);
            File.WriteAllText(siblingPath, "#TITLE sibling", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = siblingPath, title = "sibling", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.RemoveCustomFolder(table);

            Assert.IsTrue(Directory.Exists(siblingDir));
            Assert.IsTrue(File.Exists(siblingPath));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", siblingPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>保存済み追加先名を解決できない場合、既定先の同名出力とDB行を保持します。別配置へ代用して利用者データを削除する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void RemoveCustomFolder_DoesNotFallbackToDefaultBaseWhenSavedAdditionalBaseIsMissing()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string targetDir = Path.Combine(outputBaseDir, "FallbackTarget");
            string targetPath = Path.Combine(targetDir, "0000.lr2folder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7404,
                name = "MissingBase",
                symbol = "MB",
                Output_dir = "FallbackTarget",
                custom_folder_output_base_name = "MissingAdditionalBase"
            };
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(targetPath, "#TITLE default target", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = targetPath, title = "default target", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.RemoveCustomFolder(table);

            Assert.IsTrue(Directory.Exists(targetDir));
            Assert.IsTrue(File.Exists(targetPath));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", targetPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>注入された新基点へ実移行し、旧先削除の成功・失敗を変えても新先statusを保持します。旧先失敗では元警告と残余を保ち、次の通常出力で旧先整理を再開する誤りを検出します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public void ChangeCustomFolderBaseDirectory_UsesInjectedSettingsAndPreservesCommittedDestinationAfterOldCleanupFailure(bool failCleanup)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string oldBaseDir = Path.Combine(tempDirectory, "OldBase");
            string providerNewBaseDir = Path.Combine(tempDirectory, "ProviderNewBase");
            string globalOutputBaseDir = Path.Combine(tempDirectory, "GlobalOutput");
            string oldDirectory = Path.Combine(oldBaseDir, "ProviderDefaults");
            string oldPath = Path.Combine(oldDirectory, "0000.lr2folder");
            testSettings.OperationModeLR2DB = false;
            testSettings.LR2CustomFolderOutputBaseDir = globalOutputBaseDir;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[\"GlobalAdditional\"]";
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerNewBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                return outputSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7408,
                name = "ProviderDefaults",
                symbol = "PDS",
                Output_dir = "ProviderDefaults",
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")],
                Folder_order = ["Folder A"]
            };
            Directory.CreateDirectory(oldDirectory);
            File.WriteAllText(oldPath, "#TITLE old", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = oldPath, title = "old", type = 2 }, typeof(LR2SongDB.folder));
            }
            var library = new TestBmsLibrary(songDbPath, settings: testSettings);
            var playlist = new TestBmsPlaylist(
                new BmsPlaylistLibraryBindings(library), songDbPath, getOutputSettings, settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.CommitBMSTableWithEntriesToDB(table);
            using (PlaylistOperationNotificationOwner.OperationNotificationSession notifications = playlist.OperationNotificationOwner.BeginSession())
            {
                using (FileStream? deletionBlocker = failCleanup ? new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null)
                { playlist.ChangeCustomFolderBaseDirectory(oldBaseDir, providerNewBaseDir); }
                PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = notifications.TakeReceipt();
                if (failCleanup)
                {
                    Assert.IsTrue(File.Exists(oldPath));
                    Assert.IsTrue(receipt.Notifications.Any(notification => notification.Message.Contains(oldDirectory, StringComparison.Ordinal)), "元の旧先削除失敗警告を保持します。");
                }
            }
            using (LR2SongDBExtended status = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(Path.Combine(providerNewBaseDir, table.Output_dir),
                    status.ExecuteScalar<string>("SELECT output_directory FROM playlist_custom_folder_output_status WHERE playlist_id = ?", table.playlist_id),
                    "旧先の回収失敗後もBの確定済み出力statusを保持します。");
            }
            if (failCleanup)
            {
                byte[] residual = File.ReadAllBytes(oldPath);
                // 次の明示編集もownerへ渡し、DB確定と所属索引・live・必要出力を同じ操作で揃えます。
                Assert.IsTrue(playlist.RenameFolderBMSTable(table, "Folder A", "Changed Folder"));
                CollectionAssert.AreEqual(residual, File.ReadAllBytes(oldPath), "後続routineは旧Aのcleanupを再開しません。");
                string output = Path.Combine(providerNewBaseDir, table.Output_dir, "0001.lr2folder");
                StringAssert.Contains(File.ReadAllText(output, Encoding.GetEncoding("shift_jis")), "#TITLE Changed Folder");
                using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                Assert.AreEqual("Changed Folder", table.entries.Single().folder);
                Assert.AreEqual("Changed Folder", verify.Table<LR2SongDBExtended.playlist_entry>().Single(row => row.playlist_id == table.playlist_id).folder);
                Assert.AreEqual("Changed Folder", verify.Table<LR2SongDB.folder>().Single(row => row.path == output).title);
            }
            else { Assert.IsFalse(Directory.Exists(oldDirectory)); }
            Assert.IsTrue(File.Exists(Path.Combine(providerNewBaseDir, table.Output_dir, "0000.lr2folder")));
            Assert.IsFalse(Directory.Exists(Path.Combine(globalOutputBaseDir, table.Output_dir)));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>追加先名を解決できない表は既定基点の変更対象にせず、旧既定先のファイルとDB行を保持します。未解決の配置を既定配置と推測して移行する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ChangeCustomFolderBaseDirectory_DoesNotFallbackToDefaultBaseWhenSavedAdditionalBaseIsMissing()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string oldDefaultBaseDir = Path.Combine(tempDirectory, "OldDefault");
            string newDefaultBaseDir = Path.Combine(tempDirectory, "NewDefault");
            string targetDir = Path.Combine(oldDefaultBaseDir, "FallbackTarget");
            string targetPath = Path.Combine(targetDir, "0000.lr2folder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = newDefaultBaseDir;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7405,
                name = "MissingBaseMigration",
                symbol = "MBM",
                Output_dir = "FallbackTarget",
                custom_folder_output_base_name = "MissingAdditionalBase",
                entries =
                [
                    CreateEntry("ffffffffffffffffffffffffffffffff", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(targetPath, "#TITLE default target", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = targetPath, title = "default target", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ChangeCustomFolderBaseDirectory(
                oldDefaultBaseDir,
                newDefaultBaseDir,
                additionalOutputBaseDirsBefore: "[]",
                additionalOutputBaseDirsAfter: "[]");

            Assert.IsTrue(Directory.Exists(targetDir));
            Assert.IsTrue(File.Exists(targetPath));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", targetPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>旧追加先の解決失敗を明示して新先へ生成し、旧先として渡した既定側のファイル・DB行を削除しません。旧基点を推測して整理範囲を広げる誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void MigrateCustomFolderOutputDirectory_DoesNotInferDefaultBaseWhenPreviousAdditionalBaseResolutionFailed()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string oldOutputDir = Path.Combine(outputBaseDir, "OldFallback");
            string oldPath = Path.Combine(oldOutputDir, "0000.lr2folder");
            string newOutputDir = Path.Combine(outputBaseDir, "NewFallback");
            testSettings.OperationModeLR2DB = false;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            CustomFolderOutputSettingsSnapshot migrationSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = outputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            Func<CustomFolderOutputSettingsSnapshot> getMigrationSettings = () =>
            {
                return migrationSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7406,
                name = "MissingBasePropertySave",
                symbol = "MBP",
                Output_dir = "NewFallback",
                custom_folder_output_base_name = null,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("99999999999999999999999999999999", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            Directory.CreateDirectory(oldOutputDir);
            File.WriteAllText(oldPath, "#TITLE old fallback", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = oldPath, title = "old fallback", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getMigrationSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.MigrateCustomFolderOutputDirectory(
                table,
                oldOutputDir,
                newOutputDir,
                wasRootFolderBefore: false,
                outputBaseDirBefore: null,
                inferOutputBaseDirBeforeWhenMissing: false);

            Assert.IsTrue(Directory.Exists(oldOutputDir));
            Assert.IsTrue(File.Exists(oldPath));
            Assert.IsTrue(File.Exists(Path.Combine(newOutputDir, "0000.lr2folder")));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", oldPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>全種類を除外した表のDBだけに残る出力行を整理し、同じ先の通常ファイルは保持します。空の期待集合で整理を省く誤りと拡張子を越えた削除を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_PrunesRowsWhenNoFolderFilesAreGenerated()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7402,
                name = "EmptyTable",
                symbol = "ET",
                Output_dir = "EmptyTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
            };
            string stalePath = Path.Combine(outputBaseDir, "EmptyTable", "0000.lr2folder");
            string outputDir = Path.GetDirectoryName(stalePath)!;
            Directory.CreateDirectory(outputDir);
            string preservedPath = Path.Combine(outputDir, "keep.txt");
            File.WriteAllText(preservedPath, "not managed by BeMusicSeeker");
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = stalePath, title = "stale", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
            Assert.IsTrue(File.Exists(preservedPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>単独移行入口から旧管理ディレクトリと階層DB行を除去し、新先のファイルと指定タイトルのDB行を生成します。一括入口だけに整理を実装する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void MigrateCustomFolderOutputDirectory_DeletesOldOutputDirectoryRecursively()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7403,
                name = "NestedTable",
                symbol = "NT",
                Output_dir = "NestedTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("cccccccccccccccccccccccccccccccc", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            string oldOutputDir = Path.Combine(tempDirectory, "CustomFolder", "NestedTable");
            string newOutputDir = Path.Combine(tempDirectory, "MovedCustomFolder", "NestedTable");
            string oldPath = Path.Combine(oldOutputDir, "0000.lr2folder");
            string oldNestedPath = Path.Combine(oldOutputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder");
            string oldUnmanagedPath = Path.Combine(oldOutputDir, "manual-note.txt");
            string newPath = Path.Combine(newOutputDir, "0000.lr2folder");
            Directory.CreateDirectory(oldOutputDir);
            Directory.CreateDirectory(Path.GetDirectoryName(oldNestedPath)!);
            File.WriteAllText(oldPath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(oldNestedPath, "#TITLE nested stale", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(oldUnmanagedPath, "old custom folder note", Encoding.UTF8);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = oldPath, title = "stale", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = oldNestedPath, title = "nested stale", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            playlist.MigrateCustomFolderOutputDirectory(table, oldOutputDir, newOutputDir);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == oldPath));
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == oldNestedPath));
            LR2SongDB.folder generated = verify.Table<LR2SongDB.folder>().Single(row => row.path == newPath);
            Assert.AreEqual("NestedTable ALL", generated.title);
            Assert.IsFalse(Directory.Exists(oldOutputDir));
            Assert.IsTrue(File.Exists(newPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>同一表の末尾区切りだけ異なる同一先と、旧親から新子への移行で、更新したファイルとDB行を保持します。旧先整理が成立した新出力を削除する誤りを検出します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public void MigrateCustomFolderOutputDirectory_PreservesSameTableDestination(bool moveIntoChild)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7404,
                name = "TrailingTable",
                symbol = "TT",
                Output_dir = "TrailingTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("dddddddddddddddddddddddddddddddd", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            string outputDir = Path.Combine(tempDirectory, "CustomFolder", "TrailingTable");
            string outputPath = Path.Combine(outputDir, "0001.lr2folder");
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(outputPath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = outputPath, title = "stale", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new TestBmsPlaylist(songDbPath, CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[] { table })
            };

            string destination = moveIntoChild ? Path.Combine(outputDir, "New", "Child") : outputDir;
            playlist.MigrateCustomFolderOutputDirectory(table, outputDir + Path.DirectorySeparatorChar, destination);
            if (moveIntoChild)
            {
                Assert.IsFalse(File.Exists(outputPath));
                outputPath = Path.Combine(destination, "0001.lr2folder");
            }

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder generated = verify.Table<LR2SongDB.folder>().Single(row => row.path == outputPath);
            Assert.AreEqual("Folder A", generated.title);
            Assert.IsTrue(File.Exists(outputPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実上書きでは対象BMSの既存songのlevelだけを変え、対象外BMS・BMSON・未登録行と他列を保ちます。続く文字コード補正と再読込みでも上書き値を保持し、元ファイル・詳細値への巻戻りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistTableLevelOverwriteWorkflow_UpdatesOnlyExistingSongLevel()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            new BmsLibraryDbGateway(songDbPath).EnsureAppOwnedSchema();
            string chartPath = Path.Combine(tempDirectory, "chart.bms");
            string missingPath = Path.Combine(tempDirectory, "missing.bms");
            const string missingMd5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            const string correctedTitle = "文字コード補正後のタイトル";
            File.WriteAllText(chartPath,
                "#TITLE " + correctedTitle + "\r\n#ARTIST artist\r\n#BPM 120\r\n#PLAYLEVEL 3\r\n#00111:01\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            byte[] originalBytes = File.ReadAllBytes(chartPath);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = BmsChartFileParser.ParseSnapshot(snapshot) with
            {
                Date = 123456,
                AddDate = 98765,
                Tag = "keep-tag"
            };
            file = ChartFileProjection.WithChartInfo(file,
                ChartInfoParser.ParseBytes(snapshot.Bytes, chartPath, snapshot.Md5, snapshot.Sha256));
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot
            {
                Path = chartPath,
                Hash = file.Md5,
                Encoding = "shift_jis",
                EncodingFixed = false
            });
            Assert.AreEqual(3, file.ChartInfo.level);
            Assert.AreNotEqual(correctedTitle, file.RawTitle);
            ChartFile missingFile = ChartTestValues.Empty() with
            {
                Path = missingPath,
                Level = 1
            };
            missingFile = missingFile with { Md5 = missingMd5 };
            missingFile = missingFile with { Title = "Missing", RawTitle = "Missing" };
            ChartFile otherBms = file with { Path = Path.Combine(tempDirectory, "other.bms"), Md5 = new string('c', 32), Level = 5, LevelText = "5" };
            ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = Path.Combine(tempDirectory, "other.bmson"),
                Md5 = new string('d', 32),
                Level = 6,
                LevelText = "6",
                RawTitle = "BMSON preserved"
            };
            new BmsLibraryDbGateway(songDbPath).UpsertBmsonSongs([bmson]);
            JObject expectedSong;
            JObject expectedOther;
            JObject expectedBmson;
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(otherBms), typeof(LR2SongDB.song));
                setup.Execute("DELETE FROM song WHERE path = ?;", missingPath);
                expectedSong = JObject.FromObject(setup.Table<LR2SongDB.song>().Single(row => row.path == chartPath));
                expectedSong["level"] = 12;
                expectedOther = JObject.FromObject(setup.Table<LR2SongDB.song>().Single(row => row.path == otherBms.Path));
                expectedBmson = JObject.FromObject(setup.Table<LR2SongDBExtended.bmson_song>().Single());
            }
            var library = new TestBmsLibrary(songDbPath, settings: testSettings)
            {
                BmsCharts = [file, missingFile, otherBms],
                BmsonCharts = [bmson]
            };
            var table = new BMSTable
            {
                entries =
                [
                    CreateEntryWithLevel(file.Md5, 12),
                    CreateEntryWithLevel(missingMd5, 11),
                    CreateEntryWithLevel(bmson.Md5, 14)
                ]
            };

            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            var workflow = new PlaylistTableLevelOverwriteWorkflowOwner(dialogs, () => library);
            await workflow.OverwriteAsync(table);

            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single(candidate => candidate.path == chartPath);
                Assert.AreEqual(12, row.level);
                Assert.AreEqual(file.RawTitle, row.title);
                Assert.AreEqual(123456, row.date);
                Assert.AreEqual(98765, row.adddate);
                Assert.AreEqual("keep-tag", row.tag);
                Assert.IsTrue(JToken.DeepEquals(expectedSong, JObject.FromObject(row)), "対象BMSのlevel以外の全song列を保持します。");
                Assert.IsTrue(JToken.DeepEquals(expectedOther, JObject.FromObject(verify.Table<LR2SongDB.song>().Single(candidate => candidate.path == otherBms.Path))));
                Assert.IsTrue(JToken.DeepEquals(expectedBmson, JObject.FromObject(verify.Table<LR2SongDBExtended.bmson_song>().Single())));
                Assert.AreEqual(0, verify.Table<LR2SongDB.song>().Count(candidate => candidate.path == missingPath));
            }
            Assert.AreEqual(5.0, library.BmsCharts.Single(chart => chart.Path == otherBms.Path).Level);
            Assert.AreEqual(6.0, library.BmsonCharts.Single().Level);

            library.SetBMSFilesEncoding(library.BmsCharts.Where(chart => chart.Path == chartPath), "utf-8");

            ChartFile current = library.BmsCharts.Single(chart => chart.Path == chartPath);
            Assert.AreEqual(correctedTitle, current.RawTitle);
            Assert.AreEqual(12.0, current.Level);
            Assert.AreEqual("12", current.LevelText);
            Assert.AreEqual(3, current.ChartInfo.level);
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single(candidate => candidate.path == chartPath);
                ChartFile reloaded = ChartSongStorageMapping.FromBmsRow(row);
                Assert.AreEqual(correctedTitle, row.title);
                Assert.AreEqual(12, row.level);
                Assert.AreEqual(12.0, reloaded.Level);
                Assert.AreEqual("12", reloaded.LevelText);
                Assert.AreEqual(98765, row.adddate);
                Assert.AreEqual("keep-tag", row.tag);
                Assert.AreEqual(0, verify.Table<LR2SongDB.song>().Count(candidate => candidate.path == missingPath));
            }
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(chartPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実旧形式DBからplaylist_idのある行を読取り専用でhydrateし、削除履歴を含めて孤立行を除きます。削除済み行の脱落と未所属行の混入を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadStartupPlaylistEntries_UsesPlaylistIdProjectionAndKeepsRemovedRows()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.Execute("CREATE TABLE playlist_entry (playlist_id INTEGER NULL, md5 TEXT NULL, sha256 TEXT NULL, level REAL NULL, title TEXT, artist TEXT, folder TEXT, lr2_bmsid TEXT, url TEXT, url_diff TEXT, name_diff TEXT, org_md5 TEXT, adddate TEXT, comment TEXT, memo TEXT, is_removed INTEGER NOT NULL DEFAULT 0);");
                songDb.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, is_removed) VALUES (?, ?, ?, ?);", 10, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "active", 0);
                songDb.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, is_removed) VALUES (?, ?, ?, ?);", 10, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "removed", 1);
                songDb.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, is_removed) VALUES (?, ?, ?, ?);", null, "cccccccccccccccccccccccccccccccc", "orphan", 0);
            }

            PlaylistEntriesHydrationLoadResult result = new PlaylistPersistenceRepository(songDbPath).LoadStartupPlaylistEntries();

            Assert.AreEqual(2, result.RowCount);
            Assert.IsTrue(result.Entries.Any(entry => entry.title == "active" && !entry.is_removed));
            Assert.IsTrue(result.Entries.Any(entry => entry.title == "removed" && entry.is_removed));
            Assert.IsFalse(result.Entries.Any(entry => entry.title == "orphan"));
            Assert.IsTrue(result.ReadOnly);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>単独・二表バッチの実登録で注入先へ必要出力を生成し、保存値側には生成しません。バッチだけ別の設定元へ戻る誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ExternalTableRegistration_UsesInjectedSettingsForSingleAndBatchOutputs()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string providerOutputBaseDir = Path.Combine(tempDirectory, "ProviderOutput");
            string globalOutputBaseDir = Path.Combine(tempDirectory, "GlobalOutput");
            testSettings.OperationModeLR2DB = false;
            testSettings.LR2CustomFolderOutputBaseDir = globalOutputBaseDir;
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                return outputSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOutputSettings,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            BMSTable singleTable = new()
            {
                playlist_id = 7801,
                name = "ProviderSingle",
                symbol = "PSS",
                Output_dir = "ProviderSingle",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Single Folder")],
                Folder_order = ["Single Folder"]
            };

            await playlist.ExternalSyncOwner.RegistrateExternalTableAsync(singleTable, false, "test_external_single");

            BMSTable batchTableA = new()
            {
                playlist_id = 7802,
                name = "ProviderBatchA",
                symbol = "PBA",
                Output_dir = "ProviderBatchA",
                ignore_folder_output = singleTable.ignore_folder_output,
                entries = [CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Batch Folder A")],
                Folder_order = ["Batch Folder A"]
            };
            BMSTable batchTableB = new()
            {
                playlist_id = 7803,
                name = "ProviderBatchB",
                symbol = "PBB",
                Output_dir = "ProviderBatchB",
                ignore_folder_output = singleTable.ignore_folder_output,
                entries = [CreateEntry("cccccccccccccccccccccccccccccccc", "Batch Folder B")],
                Folder_order = ["Batch Folder B"]
            };

            PlaylistExternalSyncOwner.RegisteredExternalTableBatchResult batchResult = await playlist.ExternalSyncOwner.RegistrateExternalTablesAsync(
                [batchTableA, batchTableB],
                false,
                "test_external_batch");

            Assert.AreEqual(2, batchResult.RegisteredTables.Count);
            foreach (BMSTable table in new[] { singleTable, batchTableA, batchTableB })
            {
                string outputDirectory = Path.Combine(providerOutputBaseDir, table.Output_dir);
                Assert.IsTrue(Directory.Exists(outputDirectory));
                Assert.IsTrue(Directory.EnumerateFiles(outputDirectory, "*.lr2folder").Any());
                Assert.IsFalse(Directory.Exists(Path.Combine(globalOutputBaseDir, table.Output_dir)));
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

    /// <summary>外部新規登録は既存表と全候補の配置を副作用前に検査し、一括の先頭が安全でも衝突時は全件を拒否します。終端後の安全な登録で受付解放を確認します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public async Task ExternalTableRegistration_RejectsOverlappingCandidateBeforeMutation(bool multiple)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBase = Path.Combine(tempDirectory, "Output");
            testSettings.OperationModeLR2DB = true;
            testSettings.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            testSettings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput");
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            LR2Config config = CreateLr2Config(testSettings.LR2RootPath, outputBase);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            byte[] oldXml = File.ReadAllBytes(config.ConfigFilePath);
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var existing = new BMSTable { playlist_id = 7821, name = "Child", Output_dir = Path.Combine("Parent", "Child") };
            var playlist = new TestBmsPlaylist(songDbPath, configOwner,
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([existing])
            };
            playlist.CommitBMSTableHeadersToDB([existing]);
            string oldFile = Path.Combine(outputBase, existing.Output_dir, "0000.lr2folder");
            Directory.CreateDirectory(Path.Combine(outputBase, existing.Output_dir));
            File.WriteAllText(oldFile, "old child output");
            DateTime oldTimestamp = File.GetLastWriteTimeUtc(oldFile);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.Insert(new LR2SongDB.folder { path = oldFile, title = "old child", type = 2 });
            }
            var safe = new BMSTable
            {
                playlist_id = 7822,
                name = "Safe",
                symbol = "S",
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Safe Folder")]
            };
            var conflicting = new BMSTable { playlist_id = 7823, name = "Parent" };
            BMSTable[] candidates = multiple ? [safe, conflicting] : [conflicting];
            int notices = 0;
            playlist.BMSTables.CollectionChanged += (_, _) => notices++;
            foreach (BMSTable table in candidates) { table.PropertyChanged += (_, _) => notices++; }
            Dictionary<BMSTable, (string name, DateTime last_update, int? bmt_sort, bool? is_bmt_output)> oldCandidates = candidates.ToDictionary(table => table,
                table => (table.name, table.last_update, table.bmt_sort, table.is_bmt_output));

            if (multiple)
            {
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => playlist.ExternalSyncOwner.RegistrateExternalTablesAsync(candidates, false, "invalid_placement"));
            }
            else
            {
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => playlist.ExternalSyncOwner.RegistrateExternalTableAsync(conflicting, false, "invalid_placement"));
            }

            Assert.AreEqual(0, notices);
            CollectionAssert.AreEqual(new[] { existing }, playlist.BMSTables.ToArray());
            foreach (BMSTable table in candidates)
            {
                Assert.AreEqual(oldCandidates[table], (table.name, table.last_update, table.bmt_sort, table.is_bmt_output));
            }
            CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(config.ConfigFilePath));
            Assert.AreEqual("old child output", File.ReadAllText(oldFile));
            Assert.AreEqual(oldTimestamp, File.GetLastWriteTimeUtc(oldFile));
            CollectionAssert.AreEqual(new[] { oldFile }, Directory.GetFiles(outputBase, "*", SearchOption.AllDirectories));
            Assert.IsFalse(Directory.Exists(Path.Combine(outputBase, "Safe")));
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1, verify.Table<BMSTable>().Count());
                Assert.AreEqual(existing.Output_dir, verify.Table<BMSTable>().Single().Output_dir);
                Assert.AreEqual(0, verify.Table<BMSTableEntry>().Count());
                Assert.AreEqual("old child", verify.Table<LR2SongDB.folder>().Single().title);
            }

            await playlist.ExternalSyncOwner.RegistrateExternalTableAsync(safe, false, "safe_after_rejection");
            Assert.IsTrue(playlist.BMSTables.Contains(safe));
            using LR2SongDBExtended after = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(2, after.Table<BMSTable>().Count());
        }
        finally
        {
            if (Directory.Exists(tempDirectory)) { Directory.Delete(tempDirectory, recursive: true); }
        }
    }

    /// <summary>実登録の一覧追加通知内でDB確定を読戻し、通知中の復元は拒否します。永続化前の公開と公開終端前の予約解放を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ExternalTableRegistration_PersistsBeforeVisibleCollectionNotification()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot(),
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            BMSTable table = new()
            {
                playlist_id = 7811,
                name = "Durable before visible",
                symbol = "DBV",
                Output_dir = "DurableBeforeVisible",
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder")]
            };
            Task? rejectedRestore = null;
            bool visibleNotificationObserved = false;
            bool durableAtVisibleNotification = false;
            playlist.BMSTables.CollectionChanged += (_, change) =>
            {
                if (change.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                {
                    return;
                }

                visibleNotificationObserved = true;
                // 実 registration publication 内の復元は DB 前に拒否する。
                rejectedRestore = playlist.RestorePlaylistDumpAsync(
                    PlaylistWorkspaceTestDataSupport.CreatePlaylistRestoreDump(77, "Rejected registration restore", "X"));
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(rejectedRestore, "restore rejection during publication");
                }
                catch (InvalidOperationException)
                {
                    // 予約による明示失敗は event 終了後に対象 Task で検証する。
                }
                using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                durableAtVisibleNotification = verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM playlist WHERE playlist_id = ?;",
                    table.playlist_id) == 1;
            };

            await playlist.ExternalSyncOwner.RegistrateExternalTableAsync(
                table,
                renameDuplicateName: false,
                reason: "durable_before_visible");

            Assert.IsNotNull(rejectedRestore);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => rejectedRestore!);
            Assert.IsTrue(visibleNotificationObserved);
            Assert.IsTrue(durableAtVisibleNotification);
            Assert.IsTrue(playlist.BMSTables.Contains(table));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>単表・複数表の一覧追加通知で取消を要求しても、DB確定を保持して必要参照とBMT出力を終端します。DB後取消による中途終端を検出します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public async Task ExternalTableRegistration_CancellationDuringVisiblePublishDoesNotUndoCommit(bool multiple)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            string output = Path.Combine(tempDirectory, "table");
            var library = new TestBmsLibrary(songDbPath, settings: testSettings);
            var playlist = new TestBmsPlaylist(
                new BmsPlaylistLibraryBindings(library),
                songDbPath,
                () => new CustomFolderOutputSettingsSnapshot(),
                beatorajaBmtOptionsProvider: () => new BeatorajaBmtOptionsSnapshot
                {
                    EnableBeatorajaBmtOutput = true,
                    BeatorajaBmtTablePath = output
                }, settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            BMSTable table = new()
            {
                playlist_id = 7812,
                name = "Cancellation during visible publish",
                symbol = "CDV",
                Output_dir = "CancellationDuringVisiblePublish",
                entries = [CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder")]
            };
            BMSTable second = new()
            {
                playlist_id = 7813,
                name = "Second cancellation during visible publish",
                symbol = "SECOND",
                Output_dir = "SecondCancellationDuringVisiblePublish",
                entries = [CreateEntry("cccccccccccccccccccccccccccccccc", "Folder")]
            };
            BMSTable[] registered = multiple ? [table, second] : [table];
            int handled = library.NormalLibraryRefreshNotificationVersion;
            using var cancellation = new CancellationTokenSource();
            bool visibleNotificationObserved = false;
            playlist.BMSTables.CollectionChanged += (_, change) =>
            {
                if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                {
                    visibleNotificationObserved = true;
                    cancellation.Cancel();
                }
            };

            if (multiple)
            {
                await playlist.ExternalSyncOwner.RegistrateExternalTablesAsync(
                    registered, renameDuplicateName: false, reason: "cancel_during_visible_publish", cancellation.Token);
            }
            else
            {
                await playlist.ExternalSyncOwner.RegistrateExternalTableAsync(
                    table, renameDuplicateName: false, reason: "cancel_during_visible_publish", cancellation.Token);
            }

            Assert.IsTrue(visibleNotificationObserved);
            Assert.IsTrue(cancellation.IsCancellationRequested);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handled);
            PlaylistReferenceCommitReceipt receipt = batch.ReferenceCommits.Single();
            PlaylistReferenceReadSnapshot references = library.CapturePlaylistReferenceReadSnapshot(
                registered.Select(current => PlaylistEntryLookupKey.FromEntry(current.entries.Single())));
            Assert.AreEqual(receipt.Revision, references.Revision);
            foreach (BMSTable current in registered)
            {
                Assert.IsTrue(playlist.BMSTables.Contains(current));
                Assert.AreEqual(1L, verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM playlist WHERE playlist_id = ?;", current.playlist_id));
                var key = PlaylistEntryLookupKey.FromEntry(current.entries.Single());
                Assert.AreEqual(current.symbol, references.Values[key].Symbols);
                Assert.IsTrue(receipt.Changes.ContainsKey(key));
            }
            Assert.AreEqual(registered.Length, Directory.GetFiles(output, "*.bmt").Length);
            Assert.IsTrue(File.Exists(Path.Combine(output, BmtTableExportService.ManifestFileName)));
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable next));
            next.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>先の表writerを保持して逆順バッチ保存を開始し、後の表writerを取らず待機して解放後に二表と二行を保存します。表ロックの順序逆転を検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task PlaylistEntryBatchCommit_ConcurrentReverseOrderCompletesWithoutDeadlock()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable first = new()
            {
                playlist_id = 7813,
                name = "Concurrent first",
                symbol = "CF",
                Output_dir = "ConcurrentFirst",
                entries = [CreateEntry("cccccccccccccccccccccccccccccccc", "First")]
            };
            BMSTable second = new()
            {
                playlist_id = 7814,
                name = "Concurrent second",
                symbol = "CS",
                Output_dir = "ConcurrentSecond",
                entries = [CreateEntry("dddddddddddddddddddddddddddddddd", "Second")]
            };
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot(),
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([first, second])
            };
            using var releaseHeldWriter = new ManualResetEventSlim();
            var writerAcquired = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var heldFirstWriter = Task.Run(() =>
            {
                using (first.ReaderWriterLock.GetWriterGuard())
                {
                    writerAcquired.SetResult(true);
                    releaseHeldWriter.Wait();
                }
            });
            Task? reverseOrderCommit = null;
            try
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(writerAcquired.Task, heldFirstWriter, "playlist-entry.writer");
                var commitStarted = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                reverseOrderCommit = Task.Run(() =>
                {
                    commitStarted.SetResult(true);
                    playlist.CommitBMSTablesWithEntriesToDB([second, first]);
                });

                await TestUiDispatcherHost.AwaitNotificationAsync(commitStarted.Task, reverseOrderCommit, "playlist-entry.commit-start");
                Assert.IsTrue(
                    SpinWait.SpinUntil(
                        () => first.ReaderWriterLock.WaitingWriteCount > 0,
                        TimeSpan.FromSeconds(5)),
                    "The reverse-order commit did not reach the held first-table writer lock.");
                Assert.AreEqual(0u, second.ReaderWriterLock.LockingWriteCount);
            }
            finally
            {
                releaseHeldWriter.Set();
                await heldFirstWriter;
                if (reverseOrderCommit != null)
                {
                    await reverseOrderCommit;
                }
            }

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(2L, verify.ExecuteScalar<long>(
                "SELECT COUNT(1) FROM playlist WHERE playlist_id IN (?, ?);",
                first.playlist_id,
                second.playlist_id));
            Assert.AreEqual(2L, verify.ExecuteScalar<long>(
                "SELECT COUNT(1) FROM playlist_entry WHERE playlist_id IN (?, ?) AND is_removed = 0;",
                first.playlist_id,
                second.playlist_id));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>空の実BMT全量出力は表進捗を出さず、親要求を保持した実行開始・終端を一組届けて次予約を受理します。空入力での終端通知欠落と予約漏れを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task BeatorajaBmtExport_EmptyPlaylistEndsExecutionWithoutTableProgress()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            string tablePath = Path.Combine(tempDirectory, "beatoraja", "table.json");
            Directory.CreateDirectory(Path.GetDirectoryName(tablePath)!);
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot
                {
                    EnableBeatorajaBmtOutput = true,
                    BeatorajaBmtTablePath = tablePath
                },
                () => new CustomFolderOutputSettingsSnapshot(),
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            playlist.BmtOutput.ProgressRequestFactory = (source, version) => new(4, 11, source, version);
            var execution = new List<(OperationProgressRequest Request, bool Running)>();
            playlist.BmtOutput.RequestProgressReporter = (request, running) => execution.Add((request, running));
            var progress = new List<PlaylistSyncProgressSnapshot>();
            playlist.BmtOutput.ExportProgressReporter = snapshot => progress.Add(snapshot);

            await playlist.BmtOutput.ExportAllAsync("empty_playlist", originatingRequest: new(5, 22, "playlist_entries_hydration", 3));
            Assert.AreEqual(0, progress.Count);
            var expected = new OperationProgressRequest(5, 22, "playlist_bmt_output", 1);
            CollectionAssert.AreEqual(new[] { (expected, true), (expected, false) }, execution);
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable next));
            next.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>実登録のDB確定からBMT・台帳まで同じPを保持し、後処理失敗でも確定した表・譜面を残します。</summary>
    [DataTestMethod]
    // 両条件で進捗・失敗通知を故障させる。false: 実BMT・台帳・config成功、true: 実台帳の読取り拒否。
    [DataRow(false)]
    [DataRow(true)]
    public async Task RegisteredBatch_AwaitsActualBmtOutputAndPreservesCommittedDatabaseOnOutputFailure(bool outputFails)
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistMigrationAndRegistrationTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PlaylistExternalSyncOwner.RegisteredExternalTableBatchResult>? registration = null;
        FileStream? fileBlocker = null;
        try
        {
            string db = CreateTempSongDbPath(root);
            PlaylistPersistenceRepository.EnsureSchema(db);
            string output = Path.Combine(root, "table");
            Directory.CreateDirectory(output);
            string manifest = Path.Combine(output, BmtTableExportService.ManifestFileName);
            File.WriteAllText(manifest, "{\"files\":[],\"playlists\":{}}");
            if (outputFails) { fileBlocker = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None); }
            string config = Path.Combine(root, BeatorajaConfigService.ConfigFileName);
            Directory.CreateDirectory(Path.Combine(root, "player"));
            File.WriteAllText(Path.Combine(root, "beatoraja.jar"), string.Empty);
            File.WriteAllText(config, "{\"tablepath\":\"table\",\"playerpath\":\"player\",\"tableURL\":[\"https://example.com/unmanaged\"]}");
            var playlist = new TestBmsPlaylist(db, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () =>
                {
                    entered.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                    return new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = output, BeatorajaRootPath = root, RegisterBeatorajaBmtUrls = true };
                },
                () => new CustomFolderOutputSettingsSnapshot(),
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(db, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            { BMSTables = [] };
            int requestNotifications = 0;
            int exportNotifications = 0;
            int failureNotifications = 0;
            playlist.BmtOutput.ProgressRequestFactory = (_, _) => throw new IOException("progress request unavailable");
            playlist.BmtOutput.RequestProgressReporter = (_, _) => { requestNotifications++; throw new IOException("request notification unavailable"); };
            playlist.BmtOutput.ExportProgressReporter = _ => { exportNotifications++; throw new IOException("export notification unavailable"); };
            playlist.BmtOutput.FailureReporter = _ => { failureNotifications++; throw new IOException("failure notification unavailable"); };
            BMSTable[] inputs = [new() { name = "first", Output_dir = "first", entries = [new() { md5 = new string('a', 32), title = "A", folder = "1" }] },
                new() { name = "second", Output_dir = "second", entries = [new() { md5 = new string('b', 32), title = "B", folder = "2" }] }];
            registration = Task.Run(() => playlist.ExternalSyncOwner.RegistrateExternalTablesAsync(inputs, false, "register_batch"));
            Task reached = await Task.WhenAny(entered.Task, registration);
            if (reached == registration) { await registration; }
            Assert.AreSame(entered.Task, reached);
            Assert.IsFalse(registration.IsCompleted);
            Assert.IsFalse(playlist.TryEnterPlaylistMutation(out _));
            using (LR2SongDBExtended read = new BmsLibraryDbGateway(db).OpenSongDbReadOnly())
            {
                Assert.AreEqual(2, read.Table<BMSTable>().Count());
                Assert.AreEqual(2, read.Table<BMSTableEntry>().Count());
            }
            release.TrySetResult();
            if (outputFails)
            {
                await Assert.ThrowsExceptionAsync<PlaylistMutationPostCommitException>(() => registration);
            }
            else
            {
                Assert.AreEqual(2, (await registration).RegisteredTables.Count);
                Assert.AreEqual(2, Directory.GetFiles(output, "*.bmt").Length);
                Assert.AreEqual(2, ((JObject)JObject.Parse(File.ReadAllText(manifest))["playlists"]!).Count);
                Assert.AreEqual(3, BeatorajaConfigService.ReadTableUrls(root).Count);
                Assert.IsTrue(exportNotifications > 0);
                Assert.AreEqual(0, failureNotifications);
            }
            Assert.AreEqual(2, requestNotifications);
            if (outputFails) { Assert.AreEqual(1, failureNotifications); }
            Assert.AreEqual(2, playlist.BMSTables.Count);
            using (LR2SongDBExtended read = new BmsLibraryDbGateway(db).OpenSongDbReadOnly())
            { Assert.AreEqual(2, read.Table<BMSTableEntry>().Count()); }
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable next));
            next.Dispose();
        }
        finally
        {
            release.TrySetResult();
            try { if (registration != null) { try { await registration; } catch (PlaylistMutationPostCommitException) when (outputFails) { } } }
            finally { fileBlocker?.Dispose(); Directory.Delete(root, true); }
        }
    }

    /// <summary>変更元のPで生成・同期の実失敗と対象原因を待ち、破損・読取不能を置換せず次操作へ解放します。</summary>
    [TestMethod]
    // 個別削除・台帳読取り/破損・台帳公開・二重失敗を、実共有拒否とfailureKindで区別する。
    [DataRow("delete")]
    [DataRow("read")]
    [DataRow("sync-read")]
    [DataRow("sync-corrupt")]
    [DataRow("publish")]
    [DataRow("delete-and-publish")]
    public async Task BeatorajaBmtFailure_ReportsTargetAndCauseWithinAcceptedOperation(string failureKind)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistMigrationAndRegistrationTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string songDbPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            string output = Path.Combine(directory, "table");
            Directory.CreateDirectory(output);
            string stalePath = Path.Combine(output, "stale.bmt");
            File.WriteAllText(stalePath, "owned");
            string manifestPath = Path.Combine(output, BmtTableExportService.ManifestFileName);
            File.WriteAllText(manifestPath, "{\"files\":[\"stale.bmt\"],\"playlists\":{\"1\":{\"file\":\"stale.bmt\",\"url\":\"https://example.com/old\"}}}");
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaBmtTablePath = output },
                () => new CustomFolderOutputSettingsSnapshot(),
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            { BMSTables = new ObservableCollection<BMSTable>() };
            if (failureKind == "sync-corrupt") { File.WriteAllText(manifestPath, "{broken"); }
            byte[] originalManifest = File.ReadAllBytes(manifestPath);
            IReadOnlyList<BmtTableExportService.FileOperationFailure>? reported = null;
            playlist.BmtOutput.FailureReporter = failures => reported = failures;
            using (var held = new FileStream(failureKind == "delete" ? stalePath : manifestPath,
                FileMode.Open, FileAccess.Read, failureKind is "read" or "sync-read" ? FileShare.None : FileShare.ReadWrite))
            using (FileStream? heldStale = failureKind == "delete-and-publish"
                ? new FileStream(stalePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null)
            {
                using LibraryFileMutationLease admission = playlist.AcquirePlaylistMutationLease("failure_contract");
                using LibraryFileMutationCapability capability = admission.CreateMutationCapability();
                await Assert.ThrowsExceptionAsync<PlaylistMutationPostCommitException>(() =>
                    failureKind.StartsWith("sync", StringComparison.Ordinal)
                        ? playlist.BmtOutput.SyncUrlsAsync("failure_contract", capability)
                        : playlist.BmtOutput.ExportAllAsync("failure_contract", capability: capability));
            }
            if (failureKind.StartsWith("sync", StringComparison.Ordinal))
            {
                CollectionAssert.AreEqual(originalManifest, File.ReadAllBytes(manifestPath));
                Assert.AreEqual("owned", File.ReadAllText(stalePath));
            }
            Assert.IsNotNull(reported);
            IReadOnlyList<BmtTableExportService.FileOperationFailure> capturedReports = reported!;
            Assert.AreEqual(1, capturedReports.Count);
            Assert.IsFalse(string.IsNullOrWhiteSpace(capturedReports[0].Cause));
            if (failureKind == "delete")
            {
                Assert.AreEqual(stalePath, capturedReports[0].Path);
            }
            else
            {
                StringAssert.Contains(capturedReports[0].Cause, manifestPath);
            }

            if (failureKind == "delete-and-publish")
            {
                StringAssert.Contains(capturedReports[0].Cause, stalePath);
            }

            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable next));
            next.Dispose();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>空行の表はBMTファイルを作らず実configへURLだけを登録し、管理外URLを保持して無警告で終端します。物理変更ゼロをURL同期不要と誤認する処理を検出します。</summary>
    [TestMethod]
    public async Task BeatorajaBmtUrlOnlyOutput_SynchronizesUrlsWithZeroPhysicalChanges()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistMigrationAndRegistrationTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string songDbPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            string root = Path.Combine(directory, "beatoraja");
            Directory.CreateDirectory(Path.Combine(root, "table"));
            Directory.CreateDirectory(Path.Combine(root, "player"));
            File.WriteAllText(Path.Combine(root, "beatoraja.jar"), string.Empty);
            string configPath = Path.Combine(root, BeatorajaConfigService.ConfigFileName);
            File.WriteAllText(configPath, "{\"tablepath\":\"table\",\"playerpath\":\"player\",\"tableURL\":[\"https://example.com/unmanaged\"]}");
            BMSTable table = new() { playlist_id = 8123, name = "Empty", Page_url = new Uri("https://example.com/empty"), entries = [] };
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaRootPath = root, RegisterBeatorajaBmtUrls = true },
                () => new CustomFolderOutputSettingsSnapshot(),
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songDbPath, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            { BMSTables = new ObservableCollection<BMSTable>([table]) };
            var failures = new List<BmtTableExportService.FileOperationFailure>();
            playlist.BmtOutput.FailureReporter = items => failures.AddRange(items);
            await playlist.BmtOutput.ExportTablesAsync([table], "url_only_contract");
            CollectionAssert.AreEquivalent(new[] { "https://example.com/unmanaged", "https://example.com/empty" },
                JObject.Parse(File.ReadAllText(configPath))["tableURL"]!.Values<string>().ToArray());
            Assert.AreEqual(0, Directory.GetFiles(Path.Combine(root, "table"), "*.bmt").Length);
            Assert.AreEqual(0, failures.Count);
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable next));
            next.Dispose();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>実DB・台帳・BMT・configへ接続し、全量、部分変更、削除、同期、無効保持と明示cleanupの意味境界を確認します。</summary>
    [TestMethod]
    public async Task BeatorajaBmtOperations_PreserveUnchangedOwnershipAndSeparateUrlSyncFromOutput()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsPlaylistMigrationAndRegistrationTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string db = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(db);
            string root = Path.Combine(directory, "beatoraja");
            string output = Path.Combine(root, "table");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(root, "beatoraja.jar"), string.Empty);
            string config = Path.Combine(root, BeatorajaConfigService.ConfigFileName);
            File.WriteAllText(config, "{\"tablepath\":\"table\",\"tableURL\":[\"https://example.com/unmanaged\"]}");
            var options = new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaRootPath = root, RegisterBeatorajaBmtUrls = true };
            BMSTable first = new() { playlist_id = 8101, name = "A", bmt_sort = 1, last_update = new DateTime(2024, 1, 1), Folder_order = ["Folder"], entries = [new BMSTableEntry { title = "A song", md5 = new string('a', 32), folder = "Folder" }] };
            BMSTable second = new() { playlist_id = 8102, name = "B", bmt_sort = 2, last_update = new DateTime(2024, 1, 1), Folder_order = ["Folder"], entries = [new BMSTableEntry { title = "B song", md5 = new string('b', 32), folder = "Folder" }] };
            var playlist = new TestBmsPlaylist(db, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => options, () => new CustomFolderOutputSettingsSnapshot(),
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(db, CustomFolderOutputPhysicalSurface.Empty), settings: testSettings)
            { BMSTables = new ObservableCollection<BMSTable>([first, second]) };
            playlist.CommitBMSTablesWithEntriesToDB([first, second]);
            await playlist.BmtOutput.ExportAllAsync("initial");
            string secondPath = Path.Combine(output, BMSTable.ComputeSha256Hex("bemusicseeker://playlist/8102") + ".bmt");
            byte[] bytes = File.ReadAllBytes(secondPath);
            DateTime time = File.GetLastWriteTimeUtc(secondPath);
            string unmanaged = Path.Combine(output, "unmanaged.bmt");
            File.WriteAllText(unmanaged, "unmanaged-content");
            void AssertPreserved()
            {
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(secondPath));
                Assert.AreEqual(time, File.GetLastWriteTimeUtc(secondPath));
                Assert.AreEqual("bemusicseeker://playlist/8102", BmtTableExportService.ReadManagedTableUrls(output).Single(entry => entry.PlaylistIdentity == "8102").Url);
                Assert.AreEqual("unmanaged-content", File.ReadAllText(unmanaged));
            }
            first.name = "A changed";
            await playlist.BmtOutput.ExportTablesAsync([first], "partial");
            AssertPreserved();
            first.is_external_sync = true;
            first.Page_url = new Uri("https://example.com/a");
            await playlist.BmtOutput.ExportTablesAsync([first], "url-change");
            AssertPreserved();
            await playlist.BmtOutput.ExportTablesAsync([], "empty");
            AssertPreserved();
            await playlist.BmtOutput.RemoveTablesAsync([first], "delete");
            AssertPreserved();
            await playlist.BmtOutput.ExportAllAsync("restore-current");
            AssertPreserved();
            string manifest = Path.Combine(output, BmtTableExportService.ManifestFileName);
            byte[] manifestBytes = File.ReadAllBytes(manifest);
            DateTime manifestTime = File.GetLastWriteTimeUtc(manifest);
            first.bmt_sort = 2; second.bmt_sort = 1;
            playlist.CommitBMSTableHeadersToDB([first, second]);
            // 読取りを許し置換を拒否する実台帳でも、順序同期は成立する。
            using (var held = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                await playlist.BmtOutput.SyncUrlsAsync("order-only");
            }
            CollectionAssert.AreEqual(new[] { "https://example.com/unmanaged", "bemusicseeker://playlist/8102", "https://example.com/a" }, BeatorajaConfigService.ReadTableUrls(root).ToArray());
            CollectionAssert.AreEqual(manifestBytes, File.ReadAllBytes(manifest));
            Assert.AreEqual(manifestTime, File.GetLastWriteTimeUtc(manifest));
            AssertPreserved();
            options = new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = false, BeatorajaRootPath = root, KeepBeatorajaBmtFilesWhenOutputDisabled = true, RegisterBeatorajaBmtUrls = true };
            // 無効の操作は台帳とconfigの両方が読取不能でも、保持要求を完了する。
            using (var held = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var heldConfig = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await playlist.BmtOutput.ExportTablesAsync([first], "disabled-edit");
                await playlist.BmtOutput.RemoveTablesAsync([first], "disabled-delete");
                await playlist.BmtOutput.SyncUrlsAsync("disabled-order");
                await playlist.BmtOutput.ExportAllAsync("disabled-keep");
            }
            CollectionAssert.AreEqual(manifestBytes, File.ReadAllBytes(manifest));
            AssertPreserved();
            options = new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaRootPath = root, RegisterBeatorajaBmtUrls = true };
            playlist.BMSTables.Remove(first);
            await playlist.BmtOutput.ExportAllAsync("full-current-set");
            Assert.AreEqual(1, BmtTableExportService.ReadManagedTableUrls(output).Count);
            AssertPreserved();
            options = new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = false, BeatorajaRootPath = root, KeepBeatorajaBmtFilesWhenOutputDisabled = false, RegisterBeatorajaBmtUrls = true };
            await playlist.BmtOutput.ExportAllAsync("explicit-cleanup");
            Assert.IsFalse(File.Exists(secondPath));
            Assert.IsFalse(File.Exists(manifest));
            Assert.AreEqual("unmanaged-content", File.ReadAllText(unmanaged));
            CollectionAssert.AreEqual(new[] { "https://example.com/unmanaged" }, BeatorajaConfigService.ReadTableUrls(root).ToArray());
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable next));
            next.Dispose();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

}
