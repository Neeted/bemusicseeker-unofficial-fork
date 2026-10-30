using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2NormalFolderDbSyncServiceTests
{
    /// <summary>削除候補の直接custom・別子custom・親欠落・customなしを限定読取りと生成へ接続し、必要親だけを生成保持します。custom全列と無関係ルートの不変性も観測します。</summary>
    [DataTestMethod]
    // 直接親の保持、祖先の別子による保持、欠落親の補完、依存なしのpruneをそれぞれ識別します。
    [DataRow(true, false, true)]
    [DataRow(true, true, true)]
    [DataRow(true, false, false)]
    [DataRow(false, false, true)]
    public void Sync_ConfirmedCustomDependenciesKeepOnlyRequiredAncestors(bool hasCustom, bool sibling, bool parentsExist)
    {
        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        using var db = new LR2SongDBExtended(scope.SongDbPath);
        db.CreateTable<LR2SongDB.folder>();
        string root = @"D:\BMS", parent = @"D:\BMS\Pack", removed = @"D:\BMS\Pack\Removed";
        string customParent = sibling ? @"D:\BMS\Pack\Sibling\Deep" : removed;
        var custom = new LR2SongDB.folder
        {
            path = Path.Combine(customParent, "external.lr2folder"),
            title = "Title",
            subtitle = "Subtitle",
            category = "Category",
            info_a = "A",
            info_b = "B",
            command = "song.hash = 'keep'",
            banner = "banner.png",
            type = 4,
            parent = "custom-parent",
            date = 12,
            max = 13,
            adddate = 14
        };
        var unrelated = new LR2SongDB.folder { path = FolderPath(@"E:\Other\Untouched"), type = 1, title = "Unrelated", date = 41, adddate = 42 };
        db.Insert(unrelated);
        if (hasCustom) { db.Insert(custom); }
        if (parentsExist)
        {
            foreach (string directory in new[] { parent, removed })
            { db.Insert(new LR2SongDB.folder { path = FolderPath(directory), type = 1, title = "Old", adddate = 77 }); }
        }
        IReadOnlyList<string> candidates = Lr2NormalFolderDbSyncService.CaptureCustomDependencyCandidates(db, [root], [removed], [parent]);
        CollectionAssert.AreEquivalent(hasCustom ? new[] { custom.path } : Array.Empty<string>(), candidates.ToArray());
        Lr2NormalFolderDbSyncService.Sync(db, new Lr2NormalFolderDbSyncRequest
        {
            RootDirectories = [root],
            PruneScopeDirectories = [removed],
            PruneExactDirectories = [parent],
            RequiredCustomParentDirectories = candidates.Select(path => Path.GetDirectoryName(path) ?? throw new AssertFailedException("Missing parent.")).ToArray(),
            DirectoryLastWriteTimeUtcResolver = _ => new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            AllowPrune = true,
            UseScopedExistingRows = true
        });
        Assert.AreEqual(hasCustom, db.Find<LR2SongDB.folder>(FolderPath(parent)) != null);
        Assert.AreEqual(hasCustom && !sibling, db.Find<LR2SongDB.folder>(FolderPath(removed)) != null);
        if (hasCustom)
        {
            LR2SongDB.folder required = db.Find<LR2SongDB.folder>(FolderPath(customParent));
            Assert.IsNotNull(required);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(customParent)), required.parent);
            if (parentsExist) { Assert.AreEqual(77, db.Find<LR2SongDB.folder>(FolderPath(parent)).adddate); }
            AssertFolderValues(custom, db.Find<LR2SongDB.folder>(custom.path));
        }
        AssertFolderValues(unrelated, db.Find<LR2SongDB.folder>(unrelated.path));
    }

    private static void AssertFolderValues(LR2SongDB.folder expected, LR2SongDB.folder actual)
    {
        Assert.IsNotNull(actual);
        Assert.AreEqual((expected.path, expected.title, expected.subtitle, expected.category, expected.info_a, expected.info_b,
            expected.command, expected.banner, expected.type, expected.parent, expected.date, expected.max, expected.adddate),
            (actual.path, actual.title, actual.subtitle, actual.category, actual.info_a, actual.info_b,
            actual.command, actual.banner, actual.type, actual.parent, actual.date, actual.max, actual.adddate));
    }

    /// <summary>
    /// 譜面階層とfolderinfoを通常行へ反映し、古い通常行だけを削除してカスタム行・既存追加時刻を保持する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_UpsertsNormalFolderRowsAndPrunesStaleNormalRows()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string stalePath = FolderPath(@"D:\BMS\Removed");
            string customPath = FolderPath(@"D:\BMS\Custom");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = FolderPath(@"D:\BMS"),
                type = 1,
                date = 1,
                adddate = 12345
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                type = 1
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = customPath,
                type = 2
            }, typeof(LR2SongDB.folder));

            DateTime rootTime = new(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc);
            DateTime songTime = rootTime.AddMinutes(2);
            var metadataTimes = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase)
            {
                [Normalize(@"D:\BMS")] = rootTime,
                [Normalize(@"D:\BMS\Pack")] = rootTime.AddMinutes(1),
                [Normalize(@"D:\BMS\Pack\Song")] = songTime
            };

            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Pack\Song\chart.bms"],
                FolderInfoFilePaths = [@"D:\BMS\Pack\Song\folderinfo.txt"],
                DirectoryLastWriteTimeUtcResolver = path => metadataTimes.TryGetValue(Normalize(path), out DateTime? timestamp) ? timestamp : null,
                FolderInfoLinesReader = _ => ["#TITLE Folder Info Song"],
                GeneratedAtUtc = rootTime.AddDays(1),
                AllowPrune = true
            });

            Assert.IsTrue(result.HasChanges);
            Assert.AreEqual(3, result.GeneratedCount);
            Assert.AreEqual(3, result.MetadataRequestedDirectoryCount);
            Assert.AreEqual(3, result.MetadataResolvedDirectoryCount);
            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(1, result.FolderInfoCandidateCount);
            Assert.AreEqual(1, result.FolderInfoAppliedCount);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == customPath));

            LR2SongDB.folder root = songDb.Table<LR2SongDB.folder>().Single(row => row.path == FolderPath(@"D:\BMS"));
            LR2SongDB.folder song = songDb.Table<LR2SongDB.folder>().Single(row => row.path == FolderPath(@"D:\BMS\Pack\Song"));
            Assert.AreEqual(12345, root.adddate);
            Assert.AreEqual(rootTime.ToUnixtime(), root.date);
            Assert.AreEqual("Folder Info Song", song.title);
            Assert.AreEqual(songTime.ToUnixtime(), song.date);
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
    /// 生成・削除範囲の保存行を扱い、範囲外行と同一パスのカスタム行を保持して狭い更新での過剰削除を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_ScopedExistingRows_LoadsGeneratedAndPruneScopeRowsOnly()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string packPath = FolderPath(@"D:\BMS\Pack");
            string childPath = FolderPath(@"D:\BMS\Pack\Song");
            string stalePath = FolderPath(@"D:\BMS\Removed");
            string untouchedPath = FolderPath(@"D:\BMS\Other");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = packPath.ToUpperInvariant(),
                type = 1,
                adddate = 777
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = childPath,
                type = 2
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                type = 1
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = untouchedPath,
                type = 1
            }, typeof(LR2SongDB.folder));

            DateTime timestamp = new(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc);
            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Pack\Song\chart.bms"],
                PruneScopeDirectories = [@"D:\BMS\Removed"],
                DirectoryLastWriteTimeUtcResolver = _ => timestamp,
                GeneratedAtUtc = timestamp.AddDays(1),
                AllowPrune = true,
                UseScopedExistingRows = true
            });

            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == untouchedPath));
            Assert.AreEqual(777, songDb.Table<LR2SongDB.folder>().Single(row => row.path == packPath.ToUpperInvariant()).adddate);
            Assert.AreEqual(2, songDb.Table<LR2SongDB.folder>().Single(row => row.path == childPath).type);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == childPath && row.type == 1));
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
    /// 完全一致の削除対象だけを消し大文字小文字が違う保存行を保持して、DB実キーの同一性を広げる誤りを検出する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_ExactPruneKeepsCaseVariantRowOutsideExactIdentity()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string exactStalePath = FolderPath(@"D:\BMS\Pack\Song");
            string caseVariantStalePath = FolderPath(@"D:\BMS\PACK\SONG");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = exactStalePath,
                type = 1
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = caseVariantStalePath,
                type = 1
            }, typeof(LR2SongDB.folder));

            DateTime timestamp = new(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc);
            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Keep\chart.bms"],
                PruneExactDirectories = [@"D:\BMS\Pack\Song"],
                DirectoryLastWriteTimeUtcResolver = _ => timestamp,
                GeneratedAtUtc = timestamp.AddDays(1),
                AllowPrune = true,
                UseScopedExistingRows = true
            });

            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == exactStalePath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == caseVariantStalePath));
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
    /// 指定した削除範囲の旧行だけを消し、他範囲を保持しながら新しい通常行を反映する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_PrunesOnlyRequestedNormalFolderScope()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string removedPath = FolderPath(@"D:\BMS\Removed");
            string untouchedPath = FolderPath(@"D:\BMS\Other");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = removedPath,
                type = 1
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = untouchedPath,
                type = 1
            }, typeof(LR2SongDB.folder));

            DateTime timestamp = new(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc);
            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Pack\chart.bms"],
                PruneScopeDirectories = [@"D:\BMS\Removed"],
                DirectoryLastWriteTimeUtcResolver = _ => timestamp,
                GeneratedAtUtc = timestamp.AddDays(1),
                AllowPrune = true
            });

            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == removedPath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == untouchedPath));
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == FolderPath(@"D:\BMS\Pack")));
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
    /// 削除範囲の親と子をまとめて消し別パッケージを残して、祖先指定での削除漏れや範囲拡張を検出する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_PrunesRequestedAncestorScope()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string stalePackagePath = FolderPath(@"D:\BMS\RemovedPackage");
            string staleSongPath = FolderPath(@"D:\BMS\RemovedPackage\Song");
            string untouchedPath = FolderPath(@"D:\BMS\OtherPackage");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePackagePath,
                type = 1
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = staleSongPath,
                type = 1
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = untouchedPath,
                type = 1
            }, typeof(LR2SongDB.folder));

            DateTime timestamp = new(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc);
            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\CurrentPackage\chart.bms"],
                PruneScopeDirectories = [@"D:\BMS\RemovedPackage"],
                DirectoryLastWriteTimeUtcResolver = _ => timestamp,
                GeneratedAtUtc = timestamp.AddDays(1),
                AllowPrune = true
            });

            Assert.AreEqual(2, result.DeletedCount);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePackagePath));
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == staleSongPath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == untouchedPath));
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
    /// 登録ルート外の削除指定を適用せず既存行を保持し、入力範囲外への削除拡張を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_IgnoresPruneScopeOutsideRootDirectories()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string stalePath = FolderPath(@"D:\BMS\Removed");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                type = 1
            }, typeof(LR2SongDB.folder));

            DateTime timestamp = new(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc);
            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Pack\chart.bms"],
                PruneScopeDirectories = [@"E:\Other"],
                DirectoryLastWriteTimeUtcResolver = _ => timestamp,
                GeneratedAtUtc = timestamp.AddDays(1),
                AllowPrune = true
            });

            Assert.AreEqual(0, result.DeletedCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
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
    /// 一部のディレクトリ情報が欠けると古い行削除を抑止し、不完全な生成結果から保存行を失う誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_SuppressesPruneWhenMetadataIsIncomplete()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string stalePath = FolderPath(@"D:\BMS\Removed");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                type = 1
            }, typeof(LR2SongDB.folder));

            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Pack\chart.bms"],
                DirectoryLastWriteTimeUtcResolver = path => string.Equals(Normalize(path), Normalize(@"D:\BMS"), StringComparison.OrdinalIgnoreCase)
                    ? new DateTime(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc)
                    : null,
                AllowPrune = true
            });

            Assert.AreEqual(1, result.SkippedMissingMetadataCount);
            Assert.AreEqual(0, result.DeletedCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
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
    /// 未対応文字のフォルダを生成源から除き登録ルート行だけを保存し、LR2で扱えない階層行の生成を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_DoesNotUseCp932UnsupportedDirectoryAsFolderSource()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();

            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\emoji_😀\chart.bms"],
                DirectoryLastWriteTimeUtcResolver = _ => new DateTime(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc)
            });

            Assert.AreEqual(1, result.GeneratedCount);
            Assert.AreEqual(0, result.SkippedIncompatibleChartPathCount);
            Assert.AreEqual(1, result.SkippedUnsupportedPathCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count());
            Assert.AreEqual(FolderPath(@"D:\BMS"), songDb.Table<LR2SongDB.folder>().Single().path);
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
    /// 未対応パスを診断・除外しても正常な範囲の旧行削除を進め、個別非互換と列挙不完全を混同しない。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_PrunesStaleRowsEvenWhenSomeChartPathsAreIncompatible()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string stalePath = FolderPath(@"D:\BMS\Removed");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                type = 1
            }, typeof(LR2SongDB.folder));

            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths =
                [
                    @"D:\BMS\Pack\chart.bms",
                    @"D:\BMS\emoji_😀\chart.bms"
                ],
                DirectoryLastWriteTimeUtcResolver = _ => new DateTime(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc),
                AllowPrune = true
            });

            Assert.AreEqual(0, result.SkippedIncompatibleChartPathCount);
            Assert.AreEqual(1, result.SkippedUnsupportedPathCount);
            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == FolderPath(@"D:\BMS\Pack")));
            Assert.IsFalse(songDb.Table<LR2SongDB.folder>().Any(row => row.path == FolderPath(@"D:\BMS\emoji_😀")));
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
    /// 完了指定がない走査では新しい行を反映しても古い行を残し、未完了入力から削除する誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_DoesNotPruneRowsUnlessCallerMarksScanComplete()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string stalePath = FolderPath(@"D:\BMS\Removed");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                type = 1
            }, typeof(LR2SongDB.folder));

            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Pack\chart.bms"],
                DirectoryLastWriteTimeUtcResolver = _ => new DateTime(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc)
            });

            Assert.AreEqual(0, result.DeletedCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == FolderPath(@"D:\BMS\Pack")));
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
    /// 譜面パスから登録ルート内のルート・祖先・直近フォルダを取得対象へ含め、範囲外を除外する。
    /// </summary>
    [TestMethod]
    public void CreateDirectoryMetadataTargets_IncludesRootsAncestorsAndChartDirectories()
    {
        IReadOnlyCollection<string> targets = Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargets(
            [@"D:\BMS"],
            [@"D:\BMS\Pack\Song\chart.bms", @"E:\Other\chart.bms"]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                Normalize(@"D:\BMS"),
                Normalize(@"D:\BMS\Pack"),
                Normalize(@"D:\BMS\Pack\Song")
            },
            targets.ToArray());
    }

    /// <summary>
    /// ディレクトリ入力から登録ルート内の祖先まで取得対象を広げ、別ルートの対象を混入させない。
    /// </summary>
    [TestMethod]
    public void CreateDirectoryMetadataTargetsFromDirectories_IncludesRootsAndAncestors()
    {
        IReadOnlyCollection<string> targets = Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargetsFromDirectories(
            [@"D:\BMS"],
            [@"D:\BMS\Pack\Song", @"E:\Other"]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                Normalize(@"D:\BMS"),
                Normalize(@"D:\BMS\Pack"),
                Normalize(@"D:\BMS\Pack\Song")
            },
            targets.ToArray());
    }

    /// <summary>
    /// 正規化済み入力から登録ルートと祖先を取得対象へ含め、別ルートの入力を除外する。
    /// </summary>
    [TestMethod]
    public void CreateDirectoryMetadataTargetsFromNormalizedDirectories_IncludesRootsAndAncestors()
    {
        IReadOnlyCollection<string> targets = Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargetsFromNormalizedDirectories(
            [@"D:\BMS"],
            [Normalize(@"D:\BMS\Pack\Song"), Normalize(@"E:\Other")]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                Normalize(@"D:\BMS"),
                Normalize(@"D:\BMS\Pack"),
                Normalize(@"D:\BMS\Pack\Song")
            },
            targets.ToArray());
    }

    /// <summary>
    /// 明示ディレクトリだけを保存行へ反映し譜面由来の別フォルダを追加せず、既に捕捉した対象の無断拡張を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void Sync_UsesProvidedDirectoryTargetsWithoutRewalkingChartPaths()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            DateTime timestamp = new(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc);

            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Ignored\chart.bms"],
                DirectoryPaths =
                [
                    @"D:\BMS",
                    @"D:\BMS\Pack",
                    @"D:\BMS\Pack\Song"
                ],
                DirectoryLastWriteTimeUtcResolver = _ => timestamp
            });

            Assert.AreEqual(3, result.GeneratedCount);
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == FolderPath(@"D:\BMS\Pack\Song")));
            Assert.IsFalse(songDb.Table<LR2SongDB.folder>().Any(row => row.path == FolderPath(@"D:\BMS\Ignored")));
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
    /// 捕捉済み列挙から範囲内の時刻を取得し追加読取りを呼ばず、別ルートの対象も混入させない。
    /// </summary>
    [TestMethod]
    public void DirectoryProjection_ResolvesTargetSetFromCapturedGroupedResultWithinRoots()
    {
        string rootDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), "BMS");
        string packDirectory = Path.Combine(rootDirectory, "Pack");
        string outsideDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), "Outside");
        DateTime rootTimestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
        DateTime packTimestamp = rootTimestamp.AddMinutes(1);
        RootFileEnumerationResult result = Lr2SongDbSyncTestSupport.CreateRootFileEnumerationResult(
            RootFileEnumerationService.DirectoriesGroupName,
            [
                new RootFileEnumerationEntry(rootDirectory, rootTimestamp),
                new RootFileEnumerationEntry(packDirectory, packTimestamp)
            ]);

        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries =
            Lr2FolderDirectoryEnumerationService.CreateEntriesFromGroupedResult(
                result,
                [rootDirectory],
                [rootDirectory, packDirectory, outsideDirectory],
                _ => throw new AssertFailedException("directory metadata reader must not run for a complete snapshot"));

        Assert.AreEqual(2, entries.Count);
        Assert.IsTrue(entries.TryGetValue(Normalize(rootDirectory), out RootFileEnumerationEntry? rootEntry));
        Assert.IsTrue(entries.TryGetValue(Normalize(packDirectory), out RootFileEnumerationEntry? packEntry));
        Assert.IsFalse(entries.ContainsKey(Normalize(outsideDirectory)));
        Assert.AreEqual(rootTimestamp, rootEntry!.LastWriteTimeUtc);
        Assert.AreEqual(packTimestamp, packEntry!.LastWriteTimeUtc);
    }

    /// <summary>
    /// 600件の欠落対象と範囲外が混在しても捕捉済みの一件だけを時刻付きで返し、大きな対象集合での範囲判定誤りを検出する。
    /// </summary>
    [TestMethod]
    public void DirectoryProjection_ResolvesLargeTargetSetFromCapturedGroupedResultWithinRoots()
    {
        string rootDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), "BMS");
        string existingDirectory = Path.Combine(rootDirectory, "Existing");
        string outsideDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), "Outside");
        DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
        List<string> targets = [existingDirectory, outsideDirectory];
        for (int index = 0; index < 600; index++)
        {
            targets.Add(Path.Combine(rootDirectory, "Missing" + index.ToString("D4")));
        }

        RootFileEnumerationResult result = Lr2SongDbSyncTestSupport.CreateRootFileEnumerationResult(
            RootFileEnumerationService.DirectoriesGroupName,
            [new RootFileEnumerationEntry(existingDirectory, timestamp)]);
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries =
            Lr2FolderDirectoryEnumerationService.CreateEntriesFromGroupedResult(
                result,
                [rootDirectory],
                targets,
                _ => null);

        Assert.AreEqual(1, entries.Count);
        Assert.IsTrue(entries.TryGetValue(Normalize(existingDirectory), out RootFileEnumerationEntry? entry));
        Assert.AreEqual(timestamp, entry!.LastWriteTimeUtc);
        Assert.IsFalse(entries.ContainsKey(Normalize(outsideDirectory)));
    }

    private static string Normalize(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        string trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(root)
            && string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return root.TrimEnd(Path.AltDirectorySeparatorChar);
        }
        return trimmed;
    }

    private static string FolderPath(string path)
    {
        string normalized = Normalize(path);
        return normalized.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            || normalized.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? normalized
                : normalized + Path.DirectorySeparatorChar;
    }
}
