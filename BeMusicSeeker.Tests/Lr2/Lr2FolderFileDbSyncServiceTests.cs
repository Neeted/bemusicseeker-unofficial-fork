using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2FolderFileDbSyncServiceTests
{
    /// <summary>登録実ルートと停止境界を分け、通常差分と未変更custom再利用の両方で実ルートを生成し子の親CRCを修復します。字句上のdrive・UNC条件も実SQLiteで保存します。</summary>
    [DataTestMethod]
    // 物理列挙は不要な保存境界です。未変更再利用でも親生成を省略しないことと、custom既存列の保持を確認します。
    [DataRow(@"D:\BMS", false)]
    [DataRow(@"D:\BMS", true)]
    [DataRow(@"D:\", false)]
    [DataRow(@"D:\", true)]
    [DataRow(@"\\server\share", false)]
    [DataRow(@"\\server\share", true)]
    public void Sync_RegisteredRootChildrenUseRootCrcForChangedAndPreservedCustom(string root, bool preserve)
    {
        WithTemporarySongDb(songPath =>
        {
            using var db = new LR2SongDBExtended(songPath);
            db.CreateTable<LR2SongDB.folder>();
            string child = Path.Combine(root, "Child"), file = Path.Combine(child, "external.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
            if (preserve)
            {
                db.Insert(new LR2SongDB.folder { path = file, title = "Existing", command = "original", type = 4, parent = "original-parent", date = 123, adddate = 456 });
                db.Insert(new LR2SongDB.folder { path = Lr2FolderPath.ToFolderPath(child), type = 1, parent = Lr2SongFolderParentNormalizer.RootParentHash });
            }
            Lr2FolderFileDbSyncService.Sync(db, new Lr2FolderFileDbSyncRequest
            {
                Items = [new Lr2FolderFileSyncItem { FilePath = file, Definition = new Lr2FolderFileDefinition { Title = "New" }, LastWriteTimeUtc = timestamp, PreserveExistingRowOnly = preserve }],
                DirectoryRowGenerationScopeDirectories = Lr2FolderPhysicalParentDirectoryTargetHelper.CreateBoundaries([root], null, []),
                DirectoryRowRootDirectories = [root],
                DirectoryMetadataResolver = _ => new Lr2FolderDirectoryMetadata(timestamp)
            });
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, db.Find<LR2SongDB.folder>(Lr2FolderPath.ToFolderPath(root)).parent);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(child)), db.Find<LR2SongDB.folder>(Lr2FolderPath.ToFolderPath(child)).parent);
            if (preserve)
            {
                LR2SongDB.folder row = db.Find<LR2SongDB.folder>(file);
                Assert.AreEqual(("Existing", "original", (int?)4, "original-parent", (int?)123, (int?)456),
                    (row.title, row.command, row.type, row.parent, row.date, row.adddate));
            }
        });
    }

    /// <summary>
    /// 現在の定義を保存し既存追加時刻を保持して、範囲内の旧定義だけを削除し通常行と別範囲行を残す。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_UpsertsCurrentRowsAndPrunesStaleRowsInScope()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string currentPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string stalePath = Path.Combine(outputDirectory, "0001.lr2folder");
            string outOfScopePath = Path.GetFullPath(@"D:\Other\0000.lr2folder");
            string normalDirectoryPath = outputDirectory + Path.DirectorySeparatorChar;
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = currentPath,
                title = "Old",
                type = 2,
                adddate = 12345
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                title = "Stale",
                type = 2
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = outOfScopePath,
                title = "External",
                type = 2
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = normalDirectoryPath,
                title = "Normal",
                type = 1
            }, typeof(LR2SongDB.folder));

            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = currentPath,
                        LastWriteTimeUtc = timestamp,
                        Definition = Lr2FolderFileProjection.ParseDefinition(
                        [
                            "#TITLE Current",
                            "#COMMAND song.hash in ('abc')",
                            "#MAXTRACKS 40"
                        ])
                    }
                ],
                ScopeDirectories = [outputDirectory],
                GeneratedAtUtc = timestamp.AddDays(1),
                AllowPrune = true
            });

            Assert.AreEqual(1, result.ItemCount);
            Assert.AreEqual(1, result.GeneratedCount);
            Assert.AreEqual(1, result.UpsertedCount);
            Assert.AreEqual(1, result.DeletedCount);
            LR2SongDB.folder current = songDb.Table<LR2SongDB.folder>().Single(row => row.path == currentPath);
            Assert.AreEqual("Current", current.title);
            Assert.AreEqual("song.hash in ('abc')", current.command);
            Assert.AreEqual(40, current.max);
            Assert.AreEqual(timestamp.ToUnixtime(), current.date);
            Assert.AreEqual(12345, current.adddate);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == outOfScopePath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == normalDirectoryPath));
        });
    }

    /// <summary>
    /// 相対保存パスの明示保護行を残して同範囲の旧行だけを削除し、絶対パス前提で保護を失う誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_PruneExcludedPathsProtectsRelativeLr2FolderRows()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string protectedPath = @"LR2files\CustomFolder\ManagedRoot\0000.lr2folder";
            string stalePath = @"LR2files\CustomFolder\ManagedRoot\stale.lr2folder";
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = protectedPath,
                title = "Managed",
                type = 2
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                title = "Stale",
                type = 2
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items = [],
                ScopeDirectories = [@"LR2files\CustomFolder"],
                PruneExcludedPaths = [protectedPath],
                AllowPrune = true,
                ScopeReadLr2FolderRowsOnly = true
            });

            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == protectedPath));
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
        });
    }

    /// <summary>
    /// 広い範囲でも管理対象ディレクトリを読取り・削除から除外し、接頭辞が似た隣接先を誤って保護しない。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_PruneExcludedDirectoriesAreNotReadOrDeletedFromBroadScope()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string root = Path.Combine(Path.GetDirectoryName(songDbPath)!, "ROOT");
            string externalDirectory = Path.Combine(root, "External");
            string managedDirectory = Path.Combine(root, "Table");
            string managedPrefixSiblingDirectory = Path.Combine(root, "TableOther");
            string externalPath = Path.Combine(externalDirectory, "External.lr2folder");
            string managedPath = Path.Combine(managedDirectory, "Managed.lr2folder");
            string siblingPath = Path.Combine(managedPrefixSiblingDirectory, "Sibling.lr2folder");
            songDb.InsertOrReplace(new LR2SongDB.folder { path = externalPath, title = "External", type = 2 }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder { path = managedPath, title = "Managed", type = 2 }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder { path = siblingPath, title = "Sibling", type = 2 }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                ScopeDirectories = [root],
                PruneExcludedDirectories = [managedDirectory],
                AllowPrune = true,
                ScopeReadLr2FolderRowsOnly = true
            });

            Assert.AreEqual(2, result.ExistingReadCount);
            Assert.AreEqual(0, result.ExistingExactReadCount);
            Assert.AreEqual(2, result.ExistingScopeReadCount);
            Assert.AreEqual(2, result.DeletedCount);
            Assert.IsFalse(songDb.Table<LR2SongDB.folder>().Any(row => row.path == externalPath));
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == managedPath));
            Assert.IsFalse(songDb.Table<LR2SongDB.folder>().Any(row => row.path == siblingPath));
        });
    }

    /// <summary>
    /// 管理表の新旧行を保護しつつ隣接する外部表の現在行は保持・旧行は削除して、除外範囲の混同を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_PruneExcludedDirectoriesKeepUnmanagedOutputSiblingsInScope()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputBase = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Output");
            string managedDirectory = Path.Combine(outputBase, "ManagedTable");
            string unmanagedDirectory = Path.Combine(outputBase, "ExternalTable");
            string managedCurrentPath = Path.Combine(managedDirectory, "Current.lr2folder");
            string managedStalePath = Path.Combine(managedDirectory, "Stale.lr2folder");
            string unmanagedCurrentPath = Path.Combine(unmanagedDirectory, "Current.lr2folder");
            string unmanagedStalePath = Path.Combine(unmanagedDirectory, "Stale.lr2folder");
            songDb.InsertOrReplace(new LR2SongDB.folder { path = managedCurrentPath, title = "Managed Current", type = 2 }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder { path = managedStalePath, title = "Managed Stale", type = 2 }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder { path = unmanagedCurrentPath, title = "External Current", type = 2 }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder { path = unmanagedStalePath, title = "External Stale", type = 2 }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = unmanagedCurrentPath,
                        PreserveExistingRowOnly = true
                    }
                ],
                ScopeDirectories = [outputBase],
                ScopePaths = [unmanagedCurrentPath],
                PruneExcludedDirectories = [managedDirectory],
                AllowPrune = true,
                ScopeReadLr2FolderRowsOnly = true
            });

            Assert.AreEqual(2, result.ExistingReadCount);
            Assert.AreEqual(1, result.ExistingExactReadCount);
            Assert.AreEqual(2, result.ExistingScopeReadCount);
            Assert.AreEqual(1, result.PreservedCount);
            Assert.AreEqual(1, result.DeletedCount);
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == managedCurrentPath));
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == managedStalePath));
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == unmanagedCurrentPath));
            Assert.IsFalse(songDb.Table<LR2SongDB.folder>().Any(row => row.path == unmanagedStalePath));
        });
    }

    /// <summary>
    /// 日本語・全角・アクセント付き管理先を保護して類似接頭辞の外部先は削除し、SQLの比較順との不一致による範囲誤りを検出する。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_PruneExcludedDirectoriesUseSQLiteNoCaseOrderingForUnicodeSiblings()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputBase = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Output");
            string managedJapaneseDirectory = Path.Combine(outputBase, "管理表");
            string unmanagedJapaneseDirectory = Path.Combine(outputBase, "管理表外");
            string managedFullwidthDirectory = Path.Combine(outputBase, "Ａ表");
            string unmanagedFullwidthDirectory = Path.Combine(outputBase, "Ａ表外");
            string managedAccentDirectory = Path.Combine(outputBase, "Éclair");
            string unmanagedAccentDirectory = Path.Combine(outputBase, "Éclair外");
            string[] managedDirectories = [managedJapaneseDirectory, managedFullwidthDirectory, managedAccentDirectory];
            string[] unmanagedDirectories = [unmanagedJapaneseDirectory, unmanagedFullwidthDirectory, unmanagedAccentDirectory];
            foreach (string directory in managedDirectories)
            {
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = Path.Combine(directory, "Managed.lr2folder"),
                    title = "Managed",
                    type = 2
                }, typeof(LR2SongDB.folder));
            }
            foreach (string directory in unmanagedDirectories)
            {
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = Path.Combine(directory, "External.lr2folder"),
                    title = "External",
                    type = 2
                }, typeof(LR2SongDB.folder));
            }

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items = [],
                ScopeDirectories = [outputBase],
                PruneExcludedDirectories = managedDirectories,
                AllowPrune = true,
                ScopeReadLr2FolderRowsOnly = true
            });

            Assert.AreEqual(3, result.ExistingReadCount);
            Assert.AreEqual(0, result.ExistingExactReadCount);
            Assert.AreEqual(3, result.ExistingScopeReadCount);
            Assert.AreEqual(3, result.DeletedCount);
            foreach (string directory in managedDirectories)
            {
                Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == Path.Combine(directory, "Managed.lr2folder")));
            }
            foreach (string directory in unmanagedDirectories)
            {
                Assert.IsFalse(songDb.Table<LR2SongDB.folder>().Any(row => row.path == Path.Combine(directory, "External.lr2folder")));
            }
        });
    }

    /// <summary>
    /// 完全一致と削除範囲の二行だけを読んで現在行を保持・旧行を削除し、別範囲25行の全読取り・削除を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_ReadsOnlyExactAndPruneScopeRows()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            BmsLibraryDbGateway.EnsureFolderLookupIndexes(songDb);
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string currentPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string stalePath = Path.Combine(outputDirectory, "0001.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = currentPath,
                title = "Old",
                type = 2,
                date = timestamp.ToUnixtime()
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                title = "Stale",
                type = 2
            }, typeof(LR2SongDB.folder));
            for (int index = 0; index < 25; index++)
            {
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = Path.GetFullPath($@"D:\Other\Table{index:00}\0000.lr2folder"),
                    title = "Out of scope",
                    type = 2
                }, typeof(LR2SongDB.folder));
            }

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = currentPath,
                        LastWriteTimeUtc = timestamp,
                        PreserveExistingRowOnly = true
                    }
                ],
                ScopeDirectories = [outputDirectory],
                ScopePaths = [currentPath],
                AllowPrune = true
            });

            Assert.AreEqual(2, result.ExistingReadCount);
            Assert.AreEqual(1, result.PreservedCount);
            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(25, songDb.Table<LR2SongDB.folder>().ToList().Count(row => row.path.StartsWith(Path.GetFullPath(@"D:\Other"), StringComparison.OrdinalIgnoreCase)));
        });
    }

    /// <summary>
    /// 定義行限定の同期では同範囲の通常行を読まず残し、別種別行の過剰取得・削除を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_ScopeReadLr2FolderRowsOnlySkipsNormalFolderRowsInPruneScope()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            BmsLibraryDbGateway.EnsureFolderLookupIndexes(songDb);
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string currentPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string stalePath = Path.Combine(outputDirectory, "0001.lr2folder");
            string normalDirectoryPath = outputDirectory + Path.DirectorySeparatorChar;
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = currentPath,
                title = "Current",
                type = 2,
                date = timestamp.ToUnixtime()
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                title = "Stale",
                type = 2
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = normalDirectoryPath,
                title = "Normal",
                type = 1,
                date = timestamp.AddDays(-1).ToUnixtime()
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = currentPath,
                        LastWriteTimeUtc = timestamp,
                        PreserveExistingRowOnly = true
                    }
                ],
                ScopeDirectories = [outputDirectory],
                ScopePaths = [currentPath],
                AllowPrune = true,
                ScopeReadLr2FolderRowsOnly = true
            });

            Assert.AreEqual(2, result.ExistingReadCount);
            Assert.AreEqual(1, result.PreservedCount);
            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == normalDirectoryPath));
        });
    }

    /// <summary>
    /// 削除無効時は完全一致行だけを読んで保持し旧行を残して、不要な範囲読取りや削除を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_PruneDisabledReadsOnlyExactRows()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            BmsLibraryDbGateway.EnsureFolderLookupIndexes(songDb);
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string currentPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string stalePath = Path.Combine(outputDirectory, "0001.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = currentPath,
                title = "Old",
                type = 2,
                date = timestamp.ToUnixtime()
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                title = "Stale",
                type = 2
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = currentPath,
                        LastWriteTimeUtc = timestamp,
                        PreserveExistingRowOnly = true
                    }
                ],
                ScopeDirectories = [outputDirectory],
                ScopePaths = [currentPath],
                AllowPrune = false
            });

            Assert.AreEqual(1, result.ExistingReadCount);
            Assert.AreEqual(1, result.PreservedCount);
            Assert.AreEqual(0, result.DeletedCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
        });
    }

    /// <summary>
    /// 広い登録ルート内でも生成出力先を除外して外部旧行だけを削除し、他管理主体の行を削除する誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_PruneExcludedDirectoriesProtectRowsInsideBroadScope()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            BmsLibraryDbGateway.EnsureFolderLookupIndexes(songDb);
            string rootDirectory = Path.GetFullPath(@"D:\BMS");
            string outputDirectory = Path.Combine(rootDirectory, "#BeMusicSeeker");
            string currentPath = Path.Combine(rootDirectory, "External", "current.lr2folder");
            string stalePath = Path.Combine(rootDirectory, "External", "stale.lr2folder");
            string managedPath = Path.Combine(outputDirectory, "Table", "managed.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                title = "Stale",
                type = 2
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = managedPath,
                title = "Managed",
                type = 2
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = currentPath,
                        LastWriteTimeUtc = timestamp,
                        PreserveExistingRowOnly = true
                    }
                ],
                ScopeDirectories = [rootDirectory],
                ScopePaths = [currentPath],
                PruneExcludedDirectories = [outputDirectory],
                AllowPrune = true
            });

            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(1, result.ExistingReadCount);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == managedPath));
        });
    }

    /// <summary>
    /// NOCASE範囲取得用索引を実DBへ作り索引指定の範囲クエリを成立させ、必要索引の欠落を検出する。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void EnsureFolderLookupIndexes_AddsFolderNocaseIndexForPrefixScopedSync()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = Path.Combine(outputDirectory, "0000.lr2folder"),
                title = "Current",
                type = 2
            }, typeof(LR2SongDB.folder));

            BmsLibraryDbGateway.EnsureFolderLookupIndexes(songDb);

            Assert.AreEqual(1L, songDb.ExecuteScalar<long>(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = ?;",
                BmsLibraryDbGateway.FolderPathNocaseIndexName));
            string prefix = outputDirectory + Path.DirectorySeparatorChar;
            AssertQueryUsesFolderNocaseIndex(
                songDb,
                "EXPLAIN QUERY PLAN SELECT * FROM folder INDEXED BY " + BmsLibraryDbGateway.FolderPathNocaseIndexName
                + " WHERE path COLLATE NOCASE >= ? AND path COLLATE NOCASE < ?;",
                prefix,
                prefix.Substring(0, prefix.Length - 1) + (char)(prefix[prefix.Length - 1] + 1));
        });
    }

    /// <summary>
    /// 定義再解析を要しない既存行はタイトル・追加時刻を保持して更新せず、別の旧行だけを削除する。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_PreservesUnchangedRowsWithoutDefinitionParse()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string currentPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string stalePath = Path.Combine(outputDirectory, "0001.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = currentPath,
                title = "Current",
                type = 2,
                date = timestamp.ToUnixtime(),
                adddate = 12345
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                title = "Stale",
                type = 2
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = currentPath,
                        LastWriteTimeUtc = timestamp,
                        Definition = null,
                        PreserveExistingRowOnly = true
                    }
                ],
                ScopeDirectories = [outputDirectory],
                ScopePaths = [currentPath],
                GeneratedAtUtc = timestamp.AddDays(1),
                AllowPrune = true
            });

            Assert.AreEqual(1, result.ItemCount);
            Assert.AreEqual(0, result.GeneratedCount);
            Assert.AreEqual(1, result.PreservedCount);
            Assert.AreEqual(0, result.UpsertedCount);
            Assert.AreEqual(1, result.DeletedCount);
            LR2SongDB.folder current = songDb.Table<LR2SongDB.folder>().Single(row => row.path == currentPath);
            Assert.AreEqual("Current", current.title);
            Assert.AreEqual(12345, current.adddate);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
        });
    }

    /// <summary>
    /// 削除許可のない空入力では既存定義を残し、走査が空という理由で削除する誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_DoesNotPruneRowsUnlessAllowed()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string stalePath = Path.Combine(outputDirectory, "0001.lr2folder");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                title = "Stale",
                type = 2
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                ScopeDirectories = [outputDirectory]
            });

            Assert.AreEqual(0, result.DeletedCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
        });
    }

    /// <summary>
    /// 大文字小文字がずれた実保存キーを削除し、現在キーを通常の新規追加日時で保存して別名行の残留を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_ReplacesCaseDriftedExistingPath()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string currentPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string driftPath = Path.Combine(outputDirectory.ToUpperInvariant(), "0000.lr2folder");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = driftPath,
                title = "Old",
                type = 2,
                adddate = 12345
            }, typeof(LR2SongDB.folder));

            DateTime generatedAt = new(2026, 6, 11, 1, 2, 3, DateTimeKind.Utc);
            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = currentPath,
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Current"])
                    }
                ],
                ScopeDirectories = [outputDirectory],
                AllowPrune = true,
                GeneratedAtUtc = generatedAt
            });

            Assert.AreEqual(1, result.UpsertedCount);
            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(1, result.ExistingReadCount);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == driftPath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == currentPath));
            Assert.AreEqual(checked((int)new DateTimeOffset(generatedAt).ToUnixTimeSeconds()), songDb.Table<LR2SongDB.folder>().Single(row => row.path == currentPath).adddate);
        });
    }

    /// <summary>
    /// 未対応文字と時刻未取得を別々に診断しどちらも保存せず、不完全行の書込みを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_ReportsUnsupportedAndMissingRowsWithoutWriting()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = @"D:\BMS\emoji_😀\0000.lr2folder",
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Unsupported"])
                    },
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = @"D:\BMS\#BeMusicSeeker\0000.lr2folder",
                        LastWriteTimeUtc = null,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Missing"])
                    }
                ]
            });

            Assert.AreEqual(2, result.ItemCount);
            Assert.AreEqual(0, result.GeneratedCount);
            Assert.AreEqual(0, result.UpsertedCount);
            Assert.AreEqual(1, result.SkippedUnsupportedPathCount);
            Assert.AreEqual(1, result.SkippedMissingMetadataCount);
            Assert.AreEqual(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
        });
    }

    /// <summary>
    /// 明示された新曲・ライバル・コースのLR2保存種別3・4・6を維持して、通常カスタム種別への読み替えを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [DataTestMethod]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(6)]
    public void Sync_PreservesExplicitSupportedSpecialFolderType(int folderType)
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string path = Path.GetFullPath(@"D:\LR2beta3\LR2files\CustomFolder\special.lr2folder");

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = path,
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        FolderType = folderType,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Special"])
                    }
                ]
            });

            Assert.AreEqual(1, result.GeneratedCount);
            Assert.AreEqual(1, result.UpsertedCount);
            LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(candidate => candidate.path == path);
            Assert.AreEqual(folderType, row.type);
        });
    }

    /// <summary>
    /// 削除許可時は時刻欠落・未対応パスで生成できない旧行も除去し、無効入力の保存行残留を検出する。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_PrunesRowsForInputsThatCannotGenerateRows()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string missingPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string unsupportedPath = Path.GetFullPath(@"D:\BMS\emoji_😀\0001.lr2folder");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = missingPath,
                title = "Missing",
                type = 2
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = unsupportedPath,
                title = "Unsupported",
                type = 2
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = missingPath,
                        LastWriteTimeUtc = null,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Missing"])
                    },
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = unsupportedPath,
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Unsupported"])
                    }
                ],
                ScopeDirectories = [outputDirectory, Path.GetDirectoryName(unsupportedPath)],
                AllowPrune = true
            });

            Assert.AreEqual(0, result.GeneratedCount);
            Assert.AreEqual(2, result.DeletedCount);
            Assert.AreEqual(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
        });
    }

    /// <summary>
    /// 定義と同パスの予約通常行をタイトル・種別ごと保持し、別種別の保存行を上書きする誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_DoesNotReplaceReservedNormalDirectoryRow()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string path = Path.Combine(outputDirectory, "0000.lr2folder");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = path,
                title = "Normal",
                type = 1
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = path,
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Custom"])
                    }
                ],
                ScopeDirectories = [outputDirectory],
                AllowPrune = true
            });

            Assert.AreEqual(0, result.GeneratedCount);
            Assert.AreEqual(0, result.UpsertedCount);
            Assert.AreEqual(0, result.DeletedCount);
            LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(candidate => candidate.path == path);
            Assert.AreEqual(1, row.type);
            Assert.AreEqual("Normal", row.title);
        });
    }

    /// <summary>
    /// 組込み定義の相対保存キーへタイトルを更新して追加時刻・ROOT親を保持し、物理絶対パスの別行を作る誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [DataTestMethod]
    [DataRow(12345)]
    [DataRow(null)]
    public void Sync_MatchesExistingKnownRelativeLr2FolderRow(int? addDate)
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string filePath = Path.GetFullPath(@"D:\LR2beta3\LR2files\CustomFolder\favorite.lr2folder");
            string databasePath = @"LR2files\CustomFolder\favorite.lr2folder";
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = databasePath,
                title = "Old",
                type = 2,
                parent = Lr2SongFolderParentNormalizer.RootParentHash,
                adddate = addDate
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        DatabasePath = databasePath,
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        ParentHash = Lr2SongFolderParentNormalizer.RootParentHash,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Favorite"])
                    }
                ]
            });

            Assert.AreEqual(1, result.GeneratedCount);
            Assert.AreEqual(1, result.UpsertedCount);
            LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(candidate => candidate.path == databasePath);
            Assert.AreEqual("Favorite", row.title);
            Assert.AreEqual(addDate, row.adddate);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, row.parent);
        });
    }

    /// <summary>
    /// 旧絶対キーは削除し、相対キーの新規行は通常adddateで作る。相対exact共存時はtarget値を保持し、preserve-onlyでも旧絶対キーを退役する。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void Sync_MigratesExistingAbsoluteBuiltinRowToRelativePath(bool targetExists, bool preserveExistingOnly)
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string filePath = Path.GetFullPath(@"D:\LR2beta3\LR2files\CustomFolder\favorite.lr2folder");
            string databasePath = @"LR2files\CustomFolder\favorite.lr2folder";
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = filePath,
                title = "Old Absolute",
                type = 2,
                parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(filePath)),
                adddate = 12345
            }, typeof(LR2SongDB.folder));

            DateTime generatedAt = new(2026, 6, 20, 1, 2, 3, DateTimeKind.Utc);
            if (targetExists) { songDb.Insert(new LR2SongDB.folder { path = databasePath, title = "Exact target", type = 2, parent = Lr2SongFolderParentNormalizer.RootParentHash, adddate = 67890 }); }
            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                GeneratedAtUtc = generatedAt,
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        DatabasePath = databasePath,
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        ParentHash = Lr2SongFolderParentNormalizer.RootParentHash,
                        PreserveExistingRowOnly = preserveExistingOnly,
                        Definition = preserveExistingOnly ? null : Lr2FolderFileProjection.ParseDefinition(["#TITLE Favorite"])
                    }
                ]
            });

            Assert.AreEqual(preserveExistingOnly ? 0 : 1, result.GeneratedCount);
            Assert.AreEqual(preserveExistingOnly ? 0 : 1, result.UpsertedCount);
            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(candidate => candidate.path == filePath));
            LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(candidate => candidate.path == databasePath);
            Assert.AreEqual(preserveExistingOnly ? "Exact target" : "Favorite", row.title);
            Assert.AreEqual(targetExists ? 67890 : generatedAt.ToUnixtime(), row.adddate);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, row.parent);
        });
    }

    /// <summary>
    /// 組込み定義の親分類行に提供されたタイトル・ディレクトリ時刻・ROOT親を保存し、子定義の情報との混同を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_UsesDirectoryMetadataForBuiltinCustomFolderParentRow()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string customFolderRoot = Path.Combine(Path.GetDirectoryName(songDbPath)!, "LR2files", "CustomFolder");
            string randomDirectory = Path.Combine(customFolderRoot, "RANDOM");
            string filePath = Path.Combine(randomDirectory, "select.lr2folder");
            string databasePath = @"LR2files\CustomFolder\RANDOM\select.lr2folder";
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        DatabasePath = databasePath,
                        LastWriteTimeUtc = timestamp,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Random Select"])
                    }
                ],
                DirectoryRowScopeDirectories = [@"LR2files\CustomFolder"],
                DirectoryRowGenerationScopeDirectories = [@"LR2files\CustomFolder"],
                DirectoryMetadataResolver = directory => string.Equals(directory, randomDirectory, StringComparison.OrdinalIgnoreCase)
                    ? new Lr2FolderDirectoryMetadata(timestamp.AddMinutes(1), "Folder Info Random")
                    : null
            });

            Assert.AreEqual(2, result.UpsertedCount);
            LR2SongDB.folder parentRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == @"LR2files\CustomFolder\RANDOM\");
            Assert.AreEqual(2, parentRow.type);
            Assert.AreEqual("Folder Info Random", parentRow.title);
            Assert.AreEqual(timestamp.AddMinutes(1).ToUnixtime(), parentRow.date);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, parentRow.parent);
            LR2SongDB.folder childRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == databasePath);
            Assert.AreEqual("Random Select", childRow.title);
        });
    }

    /// <summary>
    /// 親情報窓口が未取得を返した場合は実在フォルダでも親行を作らず子定義だけを保存し、無断の追加読取りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_DoesNotFallbackToDirectoryInfoWhenDirectoryMetadataResolverMisses()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string customFolderRoot = Path.Combine(Path.GetDirectoryName(songDbPath)!, "LR2files", "CustomFolder");
            string randomDirectory = Path.Combine(customFolderRoot, "RANDOM");
            Directory.CreateDirectory(randomDirectory);
            string filePath = Path.Combine(randomDirectory, "select.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        DatabasePath = @"LR2files\CustomFolder\RANDOM\select.lr2folder",
                        LastWriteTimeUtc = timestamp,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Random Select"])
                    }
                ],
                DirectoryRowScopeDirectories = [@"LR2files\CustomFolder"],
                DirectoryRowGenerationScopeDirectories = [@"LR2files\CustomFolder"],
                DirectoryMetadataResolver = _ => null
            });

            Assert.AreEqual(1, result.UpsertedCount);
            Assert.IsFalse(songDb.Table<LR2SongDB.folder>().Any(row => row.path == @"LR2files\CustomFolder\RANDOM\"));
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == @"LR2files\CustomFolder\RANDOM\select.lr2folder"));
        });
    }

    /// <summary>
    /// 要求で捕捉したfolderinfoを実読取りして組込み分類タイトルへ採用し、提供された定義面の無視を検出する。
    /// </summary>
    /// <remarks>捕捉済み情報と実フォルダ情報を異ならせ、要求入力を優先する読取り境界を確認する。</remarks>
    [TestMethod]
    public void CreateLr2FolderParentDirectoryMetadataSnapshot_UsesRequestFolderInfoSurfaceForBuiltinFolderInfo()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        try
        {
            string lr2Root = Path.Combine(tempDirectory, "LR2beta3");
            string randomDirectory = Path.Combine(lr2Root, "LR2files", "CustomFolder", "RANDOM");
            Directory.CreateDirectory(randomDirectory);
            string folderInfoPath = Path.Combine(randomDirectory, "folderinfo.txt");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(folderInfoPath, "#TITLE Random Folder Info", Encoding.GetEncoding("shift_jis"));
            string filePath = Path.Combine(randomDirectory, "select.lr2folder");
            Lr2FolderDirectoryMetadataSnapshot snapshot = Lr2SongDbSyncService.CreateLr2FolderParentDirectoryMetadataSnapshot(
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        DatabasePath = @"LR2files\CustomFolder\RANDOM\select.lr2folder",
                        LastWriteTimeUtc = timestamp,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Random"])
                    }
                ],
                new Lr2SongDbSyncRequest
                {
                    Lr2FolderPruneDirectories = [@"LR2files\CustomFolder"],
                    FolderInfoFilePaths = [folderInfoPath],
                    FolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                    {
                        [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, timestamp)
                    },
                    DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                    {
                        [randomDirectory] = new RootFileEnumerationEntry(randomDirectory, timestamp)
                    }
                });

            Assert.IsTrue(snapshot.TryGetMetadata(randomDirectory, out Lr2FolderDirectoryMetadata metadata));
            Assert.AreEqual("Random Folder Info", metadata.FolderInfoTitle);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 実フォルダ時刻が変わっても要求に捕捉した時刻を親情報へ保持し、現在時刻の再取得を防ぐ。
    /// </summary>
    /// <remarks>捕捉済み情報と実フォルダ情報を異ならせ、要求入力を優先する読取り境界を確認する。</remarks>
    [TestMethod]
    public void CreateLr2FolderParentDirectoryMetadataSnapshot_UsesRequestDirectoryEntryTimestamp()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        try
        {
            string rootDirectory = Path.Combine(tempDirectory, "BMS");
            string tableDirectory = Path.Combine(rootDirectory, "Table");
            Directory.CreateDirectory(tableDirectory);
            string filePath = Path.Combine(tableDirectory, "select.lr2folder");
            DateTime enumeratedTimestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            DateTime liveTimestamp = enumeratedTimestamp.AddMinutes(10);
            Directory.SetLastWriteTimeUtc(tableDirectory, liveTimestamp);

            Lr2FolderDirectoryMetadataSnapshot snapshot = Lr2SongDbSyncService.CreateLr2FolderParentDirectoryMetadataSnapshot(
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        LastWriteTimeUtc = enumeratedTimestamp,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Table"])
                    }
                ],
                new Lr2SongDbSyncRequest
                {
                    RootDirectories = [rootDirectory],
                    DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                    {
                        [tableDirectory] = new RootFileEnumerationEntry(tableDirectory, enumeratedTimestamp)
                    }
                });

            Assert.IsTrue(snapshot.TryGetMetadata(tableDirectory, out Lr2FolderDirectoryMetadata metadata));
            Assert.AreEqual(enumeratedTimestamp, metadata.LastWriteTimeUtc);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 要求に時刻がない実在フォルダは未取得と扱い、実folderinfoの存在で時刻を追加取得する誤りを防ぐ。
    /// </summary>
    /// <remarks>捕捉済み情報と実フォルダ情報を異ならせ、要求入力を優先する読取り境界を確認する。</remarks>
    [TestMethod]
    public void CreateLr2FolderParentDirectoryMetadataSnapshot_DoesNotReadMissingDirectoryTimestamp()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        try
        {
            string lr2Root = Path.Combine(tempDirectory, "LR2beta3");
            string randomDirectory = Path.Combine(lr2Root, "LR2files", "CustomFolder", "RANDOM");
            Directory.CreateDirectory(randomDirectory);
            File.WriteAllText(Path.Combine(randomDirectory, "folderinfo.txt"), "#TITLE Random Folder Info", Encoding.GetEncoding("shift_jis"));
            string filePath = Path.Combine(randomDirectory, "select.lr2folder");

            Lr2FolderDirectoryMetadataSnapshot snapshot = Lr2SongDbSyncService.CreateLr2FolderParentDirectoryMetadataSnapshot(
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        DatabasePath = @"LR2files\CustomFolder\RANDOM\select.lr2folder",
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Random"])
                    }
                ],
                new Lr2SongDbSyncRequest
                {
                    Lr2BuiltinFolderSourceDirectories = [Path.Combine(lr2Root, "LR2files", "CustomFolder")],
                    Lr2FolderPruneDirectories = [@"LR2files\CustomFolder"]
                });

            Assert.IsFalse(snapshot.TryGetMetadata(randomDirectory, out _));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 全同期を通して実組込みfolderinfoのタイトルを相対分類行へ保存し、準備と保存の接続漏れを検出する。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Lr2SongDbSyncRun_UsesBuiltinFolderInfoForCategoryRow()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string rootDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "BMS");
            Directory.CreateDirectory(rootDirectory);
            string lr2Root = Path.Combine(Path.GetDirectoryName(songDbPath)!, "LR2beta3");
            string builtinRoot = Path.Combine(lr2Root, "LR2files", "CustomFolder");
            string randomDirectory = Path.Combine(builtinRoot, "RANDOM");
            Directory.CreateDirectory(randomDirectory);
            string folderInfoPath = Path.Combine(randomDirectory, "folderinfo.txt");
            File.WriteAllText(folderInfoPath, "#TITLE Random Folder Info", Encoding.GetEncoding("shift_jis"));
            string lr2FolderPath = Path.Combine(randomDirectory, "select.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Random", Encoding.GetEncoding("shift_jis"));
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);

            Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
            {
                Signature = "test",
                RunId = "run",
                RootDirectories = [rootDirectory],
                ChartPaths = [],
                NormalFolderDirectoryPaths = [rootDirectory],
                Lr2FolderFilePaths = [lr2FolderPath],
                Lr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, timestamp, null)
                },
                DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [randomDirectory] = RootFileEnumerationEntry.FromDirectoryInfo(randomDirectory)
                },
                FolderInfoFilePaths = [folderInfoPath],
                FolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, timestamp, null)
                },
                Lr2FolderDiscoveryDirectories = [builtinRoot],
                Lr2FolderPruneDirectories = [@"LR2files\CustomFolder"],
                Lr2RootPath = lr2Root,
                Lr2BuiltinFolderSourceDirectories = [builtinRoot],
                Lr2FolderFileDiscoveryComplete = true,
                SongRows = [],
                StartedAtUtc = timestamp
            });

            LR2SongDB.folder category = songDb.Table<LR2SongDB.folder>().ToList().Single(row => row.path == @"LR2files\CustomFolder\RANDOM\");
            Assert.AreEqual("Random Folder Info", category.title);
        });
    }

    /// <summary>
    /// ルート出力の表親と子定義を正しい種別・親CRCで保存し、隣接表の行を削除しない。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_GeneratesRootOutputDirectoryRowWithoutPruningSiblingTables()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string baseDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "ROOT");
            string tableDirectory = Path.Combine(baseDirectory, "Table");
            string siblingDirectory = Path.Combine(baseDirectory, "Sibling");
            string filePath = Path.Combine(tableDirectory, "0000.lr2folder");
            string siblingFolderPath = Lr2FolderPath.ToFolderPath(siblingDirectory);
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = siblingFolderPath,
                title = "Sibling",
                type = 1,
                parent = Lr2SongFolderParentNormalizer.RootParentHash,
                date = timestamp.ToUnixtime()
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        LastWriteTimeUtc = timestamp,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Level Folder"])
                    }
                ],
                ScopeDirectories = [tableDirectory],
                DirectoryRowScopeDirectories = [tableDirectory],
                DirectoryRowGenerationScopeDirectories = [baseDirectory],
                DirectoryRowRootDirectories = [tableDirectory],
                DirectoryMetadataResolver = CreateDirectoryMetadataResolver(timestamp, tableDirectory),
                AllowPrune = true
            });

            Assert.AreEqual(2, result.UpsertedCount);
            Assert.AreEqual(0, result.DeletedCount);
            LR2SongDB.folder parentRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual(1, parentRow.type);
            Assert.AreEqual("Table", parentRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, parentRow.parent);
            LR2SongDB.folder childRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == filePath);
            Assert.AreEqual(2, childRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), childRow.parent);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == siblingFolderPath));
        });
    }

    /// <summary>
    /// 通常出力先・表ディレクトリ・子定義の三階層を保存し、各親CRCと種別を区別して階層欠落を検出する。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_GeneratesNormalOutputBaseAndTableDirectoryRows()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string bmsRoot = Path.Combine(Path.GetDirectoryName(songDbPath)!, "BMS");
            string outputBase = Path.Combine(bmsRoot, "#BeMusicSeeker");
            string tableDirectory = Path.Combine(outputBase, "Table");
            string filePath = Path.Combine(tableDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        LastWriteTimeUtc = timestamp,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Level Folder"])
                    }
                ],
                ScopeDirectories = [tableDirectory],
                DirectoryRowScopeDirectories = [tableDirectory],
                DirectoryRowGenerationScopeDirectories = [bmsRoot],
                DirectoryRowRootDirectories = [outputBase],
                DirectoryMetadataResolver = CreateDirectoryMetadataResolver(timestamp, outputBase, tableDirectory),
                AllowPrune = true
            });

            Assert.AreEqual(3, result.UpsertedCount);
            LR2SongDB.folder outputBaseRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == Lr2FolderPath.ToFolderPath(outputBase));
            Assert.AreEqual(1, outputBaseRow.type);
            Assert.AreEqual("#BeMusicSeeker", outputBaseRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, outputBaseRow.parent);
            LR2SongDB.folder tableRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual(1, tableRow.type);
            Assert.AreEqual("Table", tableRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputBase), tableRow.parent);
            LR2SongDB.folder childRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == filePath);
            Assert.AreEqual(2, childRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), childRow.parent);
        });
    }

    /// <summary>
    /// 既存子定義を更新しない場合も欠落した親行を生成し、子保持によって必要な親補修まで省く誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_PreserveExistingChildStillCreatesMissingParentDirectoryRow()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string baseDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "ROOT");
            string tableDirectory = Path.Combine(baseDirectory, "Table");
            string filePath = Path.Combine(tableDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = filePath,
                title = "Preserved",
                type = 2,
                parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory),
                date = timestamp.ToUnixtime()
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        LastWriteTimeUtc = timestamp,
                        PreserveExistingRowOnly = true
                    }
                ],
                ScopeDirectories = [tableDirectory],
                ScopePaths = [filePath],
                DirectoryRowScopeDirectories = [tableDirectory],
                DirectoryRowGenerationScopeDirectories = [baseDirectory],
                DirectoryRowRootDirectories = [tableDirectory],
                DirectoryMetadataResolver = CreateDirectoryMetadataResolver(timestamp, tableDirectory),
                AllowPrune = true
            });

            Assert.AreEqual(1, result.PreservedCount);
            Assert.AreEqual(1, result.UpsertedCount);
            LR2SongDB.folder parentRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual(1, parentRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, parentRow.parent);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == filePath));
        });
    }

    /// <summary>
    /// 親更新を無効指定した場合は既存子だけを保持して親行を作らず、呼出元の指定を無視する誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_PreserveExistingChildCanSkipParentDirectoryRows()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string baseDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "ROOT");
            string tableDirectory = Path.Combine(baseDirectory, "Table");
            string filePath = Path.Combine(tableDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = filePath,
                title = "Preserved",
                type = 2,
                parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory),
                date = timestamp.ToUnixtime()
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        LastWriteTimeUtc = timestamp,
                        PreserveExistingRowOnly = true
                    }
                ],
                ScopeDirectories = [tableDirectory],
                ScopePaths = [filePath],
                DirectoryRowScopeDirectories = [tableDirectory],
                DirectoryRowGenerationScopeDirectories = [baseDirectory],
                DirectoryRowRootDirectories = [tableDirectory],
                DirectoryMetadataResolver = CreateDirectoryMetadataResolver(timestamp, tableDirectory),
                AllowPrune = true,
                UpdateParentDirectoryRowsForPreservedItems = false
            });

            Assert.AreEqual(1, result.PreservedCount);
            Assert.AreEqual(0, result.UpsertedCount);
            Assert.IsFalse(songDb.Table<LR2SongDB.folder>().Any(row => row.path == Lr2FolderPath.ToFolderPath(tableDirectory)));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == filePath));
        });
    }

    /// <summary>
    /// 親情報窓口がない場合は実在フォルダも親生成に使わず子だけを保存し、暗黙のファイルシステム読取りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの行・索引・反映件数を観測し、取得範囲と保存・削除の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_DoesNotFallbackToDirectoryInfoWhenDirectoryMetadataResolverMissing()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string baseDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "ROOT");
            string tableDirectory = Path.Combine(baseDirectory, "Table");
            Directory.CreateDirectory(tableDirectory);
            string filePath = Path.Combine(tableDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        LastWriteTimeUtc = timestamp,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Level Folder"])
                    }
                ],
                ScopeDirectories = [tableDirectory],
                DirectoryRowScopeDirectories = [tableDirectory],
                DirectoryRowGenerationScopeDirectories = [baseDirectory],
                DirectoryRowRootDirectories = [tableDirectory],
                AllowPrune = true
            });

            Assert.AreEqual(1, result.UpsertedCount);
            Assert.IsFalse(songDb.Table<LR2SongDB.folder>().Any(row => row.path == Lr2FolderPath.ToFolderPath(tableDirectory)));
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == filePath));
        });
    }

    private static Func<string, Lr2FolderDirectoryMetadata> CreateDirectoryMetadataResolver(
        DateTime timestamp,
        params string[] directories)
    {
        var normalizedDirectories = new HashSet<string>(
            (directories ?? []).Select(Lr2FolderPath.NormalizeDirectoryPath).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        return path => normalizedDirectories.Contains(Lr2FolderPath.NormalizeDirectoryPath(path))
            ? new Lr2FolderDirectoryMetadata(timestamp)
            : null!;
    }

    private static void AssertQueryUsesFolderNocaseIndex(
        LR2SongDBExtended songDb,
        string explainSql,
        params object[] args)
    {
        List<QueryPlanRow> planRows = songDb.Query<QueryPlanRow>(explainSql, args);
        string detail = string.Join(" | ", planRows.Select(row => row.detail ?? string.Empty));
        StringAssert.Contains(detail, BmsLibraryDbGateway.FolderPathNocaseIndexName);
    }

    private sealed class QueryPlanRow
    {
        public string detail { get; set; } = string.Empty;
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            testAction(songDbPath);
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
