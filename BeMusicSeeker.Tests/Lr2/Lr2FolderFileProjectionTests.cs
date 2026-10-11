using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2FolderFileProjectionTests
{
    /// <summary>
    /// 空白付き指示行とTAG後のCOMMANDを読み、各表示・検索・件数・画像指定を投影して後勝ち規則や項目対応の誤りを検出します。
    /// </summary>
    [TestMethod]
    public void ParseDefinition_ReadsSupportedLr2FolderDirectives()
    {
        Lr2FolderFileDefinition definition = Lr2FolderFileProjection.ParseDefinition(
        [
            "  #TITLE Main Title",
            "#SUBTITLE Sub Title",
            "#CATEGORY Category",
            "#INFORMATION_A Info A",
            "#INFORMATION_B Info B",
            "#TAG old command",
            "#COMMAND song.hash in ('abc')",
            "#MAXTRACKS 42",
            "#BANNER banner.png",
            "#CUSTOMFOLDER"
        ]);

        Assert.AreEqual("Main Title", definition.Title);
        Assert.AreEqual("Sub Title", definition.Subtitle);
        Assert.AreEqual("Category", definition.Category);
        Assert.AreEqual("Info A", definition.InformationA);
        Assert.AreEqual("Info B", definition.InformationB);
        Assert.AreEqual("song.hash in ('abc')", definition.Command);
        Assert.AreEqual(42, definition.MaxTracks);
        Assert.AreEqual("banner.png", definition.Banner);
        Assert.IsTrue(definition.HasCustomFolderDirective);
    }

    /// <summary>
    /// アプリが出力する検索SQL・最大件数・表名と空の情報行を読み、生成形式との接続不一致を検出します。
    /// </summary>
    [TestMethod]
    public void ParseDefinition_ReadsBeMusicSeekerGeneratedFolderText()
    {
        Lr2FolderFileDefinition definition = Lr2FolderFileProjection.ParseDefinition(
        [
            "#COMMAND song.hash in (SELECT md5 FROM playlist_entry)",
            "#MAXTRACKS 40",
            "#CATEGORY Playlist",
            "#TITLE MY BEST",
            "#INFORMATION_A ",
            "#INFORMATION_B "
        ]);

        Assert.AreEqual("song.hash in (SELECT md5 FROM playlist_entry)", definition.Command);
        Assert.AreEqual(40, definition.MaxTracks);
        Assert.AreEqual("Playlist", definition.Category);
        Assert.AreEqual("MY BEST", definition.Title);
        Assert.AreEqual(string.Empty, definition.InformationA);
        Assert.AreEqual(string.Empty, definition.InformationB);
    }

    /// <summary>
    /// GENREとPLAYLEVELをフォルダの分類・最大件数の別名として読み、BMS指示名を受理しない互換性退行を検出します。
    /// </summary>
    [TestMethod]
    public void ParseDefinition_TreatsGenreAndPlayLevelAsFolderAliases()
    {
        Lr2FolderFileDefinition definition = Lr2FolderFileProjection.ParseDefinition(
        [
            "#GENRE Genre Category",
            "#PLAYLEVEL 40"
        ]);

        Assert.AreEqual("Genre Category", definition.Category);
        Assert.AreEqual(40, definition.MaxTracks);
    }

    /// <summary>
    /// 明示定義をLR2のカスタム行へ投影し、外部種別整数・親CRC・更新日時を揃えて既存の登録日時を保持することを確認します。
    /// </summary>
    [TestMethod]
    public void TryCreateFolderRow_ProjectsDefinitionToCustomFolderRow()
    {
        DateTime timestamp = new(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc);
        DateTime generatedAt = timestamp.AddDays(1);
        string filePath = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker\0000.lr2folder");
        var existingRow = new LR2SongDB.folder
        {
            path = filePath,
            adddate = 12345
        };

        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = filePath,
            ExistingRow = existingRow,
            LastWriteTimeUtc = timestamp,
            GeneratedAtUtc = generatedAt,
            Definition = Lr2FolderFileProjection.ParseDefinition(
            [
                "#COMMAND song.hash in (SELECT md5 FROM playlist_entry)",
                "#MAXTRACKS 0",
                "#CATEGORY Playlist",
                "#TITLE Folder",
                "#INFORMATION_A Artist",
                "#INFORMATION_B Sub",
                "#BANNER banner.png"
            ])
        }, out LR2SongDB.folder row);

        Assert.IsTrue(created);
        Assert.AreEqual(filePath, row.path);
        Assert.AreEqual(2, row.type);
        Assert.AreEqual("Folder", row.title);
        Assert.AreEqual("Playlist", row.category);
        Assert.AreEqual("Artist", row.info_a);
        Assert.AreEqual("Sub", row.info_b);
        Assert.AreEqual("song.hash in (SELECT md5 FROM playlist_entry)", row.command);
        Assert.AreEqual("banner.png", row.banner);
        Assert.AreEqual(0, row.max);
        Assert.AreEqual(timestamp.ToUnixtime(), row.date);
        Assert.AreEqual(12345, row.adddate);
        Assert.AreEqual(
            Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(filePath)),
            row.parent);
    }

    /// <summary>
    /// 最大件数未指定の新規行は制限なしで投影し、生成時刻を登録日時に保存してファイル時刻との取り違えを検出します。
    /// </summary>
    [TestMethod]
    public void TryCreateFolderRow_DefaultsMaxAndAddDateForNewRow()
    {
        DateTime timestamp = new(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc);
        DateTime generatedAt = timestamp.AddDays(1);

        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = @"D:\BMS\#BeMusicSeeker\0001.lr2folder",
            LastWriteTimeUtc = timestamp,
            GeneratedAtUtc = generatedAt,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Folder"])
        }, out LR2SongDB.folder row);

        Assert.IsTrue(created);
        Assert.AreEqual(0, row.max);
        Assert.AreEqual(generatedAt.ToUnixtime(), row.adddate);
    }

    /// <summary>
    /// LR2ルート相対の保存パスと明示ROOT親を保持してカスタム行を作り、無断で絶対パスへ変換する誤りを検出します。
    /// </summary>
    [TestMethod]
    public void TryCreateFolderRow_AllowsKnownLr2RootRelativeDatabasePath()
    {
        DateTime timestamp = new(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc);
        string filePath = Path.GetFullPath(@"D:\LR2beta3\LR2files\CustomFolder\favorite.lr2folder");

        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = filePath,
            DatabasePath = @"LR2files\CustomFolder\favorite.lr2folder",
            LastWriteTimeUtc = timestamp,
            ParentHash = Lr2SongFolderParentNormalizer.RootParentHash,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Favorite"])
        }, out LR2SongDB.folder row);

        Assert.IsTrue(created);
        Assert.AreEqual(@"LR2files\CustomFolder\favorite.lr2folder", row.path);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, row.parent);
        Assert.AreEqual(2, row.type);
    }

    /// <summary>
    /// 外部DBが受理するカスタム種別整数を指定どおり保存し、正常種別の拒否や別種別への置換を検出します。
    /// </summary>
    /// <remarks>DataRowの2・3・4・6は外部DBのカスタム種別整数です。</remarks>
    [DataTestMethod]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(6)]
    public void TryCreateFolderRow_AllowsSupportedCustomFolderTypes(int folderType)
    {
        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = @"D:\BMS\#BeMusicSeeker\0000.lr2folder",
            LastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
            FolderType = folderType,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Folder"])
        }, out LR2SongDB.folder row);

        Assert.IsTrue(created);
        Assert.AreEqual(folderType, row.type);
    }

    /// <summary>
    /// 組込み配下の入れ子ファイルをLR2相対階層と実親CRCへ投影し、ROOT直下への平坦化を検出します。
    /// </summary>
    [TestMethod]
    public void SourceClassifier_UsesRelativePathWithoutFlatteningNestedBuiltinFolders()
    {
        string lr2Root = Path.GetFullPath(@"D:\LR2beta3");
        string builtinRoot = Path.Combine(lr2Root, "LR2files", "CustomFolder");
        string nestedFilePath = Path.Combine(builtinRoot, "RANDOM", "random.lr2folder");

        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = nestedFilePath,
            Lr2RootPath = lr2Root,
            BuiltinSourceDirectories = [builtinRoot]
        });

        Assert.AreEqual(@"LR2files\CustomFolder\RANDOM\random.lr2folder", classification.DatabasePath);
        Assert.AreEqual(2, classification.FolderType);
        Assert.IsNull(classification.ParentHash);

        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = nestedFilePath,
            DatabasePath = classification.DatabasePath,
            LastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
            ParentHash = classification.ParentHash,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Random"])
        }, out LR2SongDB.folder row);

        Assert.IsTrue(created);
        Assert.AreEqual(
            Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"LR2files\CustomFolder\RANDOM"),
            row.parent);
        Assert.AreNotEqual(Lr2SongFolderParentNormalizer.RootParentHash, row.parent);
    }

    /// <summary>
    /// Rival配下を組込みCustomFolderと混同せず絶対パスと実親へ投影し、既定探索範囲の誤拡張を検出します。
    /// </summary>
    [TestMethod]
    public void SourceClassifier_DoesNotTreatLr2FilesRivalAsDefaultBuiltinSource()
    {
        string lr2Root = Path.GetFullPath(@"D:\LR2beta3");
        string filePath = Path.Combine(lr2Root, "LR2files", "Rival", "rival.lr2folder");

        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = filePath,
            Lr2RootPath = lr2Root
        });

        Assert.AreEqual(filePath, classification.DatabasePath);
        Assert.AreEqual(2, classification.FolderType);
        Assert.IsNull(classification.ParentHash);

        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = filePath,
            DatabasePath = classification.DatabasePath,
            LastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
            ParentHash = classification.ParentHash,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Rival"])
        }, out LR2SongDB.folder row);

        Assert.IsTrue(created);
        Assert.AreEqual(filePath, row.path);
        Assert.AreEqual(
            Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(filePath)),
            row.parent);
    }

    /// <summary>
    /// 組込み探索と管理出力が重なるファイルは組込みの相対階層を優先し、出力設定で外部保存キーを変える誤りを検出します。
    /// </summary>
    [TestMethod]
    public void SourceClassifier_PrefersOverlappingBuiltinSourceOverRootCustomOutput()
    {
        string lr2Root = Path.GetFullPath(@"D:\LR2beta3");
        string builtinRoot = Path.Combine(lr2Root, "LR2files", "CustomFolder");
        string rootOutput = Path.Combine(builtinRoot, "BeMusicSeekerRoot");
        string filePath = Path.Combine(rootOutput, "0000.lr2folder");

        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = filePath,
            Lr2RootPath = lr2Root,
            RootCustomFolderOutputBaseDir = rootOutput,
            BuiltinSourceDirectories = [builtinRoot]
        });

        Assert.AreEqual(@"LR2files\CustomFolder\BeMusicSeekerRoot\0000.lr2folder", classification.DatabasePath);
        Assert.AreEqual(2, classification.FolderType);
        Assert.IsNull(classification.ParentHash);
    }

    /// <summary>
    /// ルート出力内の表サブフォルダを実親へ結び、子ファイルをROOT直下へ平坦化する誤りを検出します。
    /// </summary>
    [TestMethod]
    public void SourceClassifier_DoesNotFlattenNestedRootCustomOutputFolders()
    {
        string rootOutput = Path.GetFullPath(@"D:\BMS\ROOT");
        string tableDirectory = Path.Combine(rootOutput, "Table");
        string filePath = Path.Combine(tableDirectory, "0000.lr2folder");

        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = filePath,
            RootCustomFolderOutputBaseDir = rootOutput
        });

        Assert.AreEqual(filePath, classification.DatabasePath);
        Assert.AreEqual(2, classification.FolderType);
        Assert.IsNull(classification.ParentHash);

        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = filePath,
            DatabasePath = classification.DatabasePath,
            LastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
            ParentHash = classification.ParentHash,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Table Child"])
        }, out LR2SongDB.folder row);

        Assert.IsTrue(created);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), row.parent);
    }

    /// <summary>
    /// 更新日時が同じでも親が変わった実ファイルを再読込対象へ戻し、既存行だけを保存して古い親を残す判断を検出します。
    /// </summary>
    /// <remarks>実ファイルの時刻と読込みを使い、時刻だけの既存行再利用を検出します。</remarks>
    [TestMethod]
    public void CreateLr2FolderFileSyncItems_DoesNotPreserveExistingRowWhenParentDiffers()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "Lr2FolderFileProjectionTests", Guid.NewGuid().ToString("N"));
        string rootOutput = Path.Combine(tempDirectory, "ROOT");
        string tableDirectory = Path.Combine(rootOutput, "Table");
        Directory.CreateDirectory(tableDirectory);
        string filePath = Path.Combine(tableDirectory, "0000.lr2folder");
        var lastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc);
        try
        {
            File.WriteAllText(filePath, "#TITLE Table Child");
            File.SetLastWriteTimeUtc(filePath, lastWriteTimeUtc);
            var request = new Lr2SongDbSyncRequest
            {
                Lr2RootCustomFolderOutputBaseDir = rootOutput
            };

            Lr2SongDbSyncService.Lr2FolderFileSyncItemsResult result =
                Lr2SongDbSyncService.CreateLr2FolderFileSyncItems(
                    [filePath],
                    request,
                    existingRowResolver: path => new LR2SongDB.folder
                    {
                        path = path,
                        type = 2,
                        parent = Lr2SongFolderParentNormalizer.RootParentHash,
                        date = lastWriteTimeUtc.ToUnixtime()
                    });

            Lr2FolderFileSyncItem item = result.Items.Single();
            Assert.IsFalse(item.PreserveExistingRowOnly);
            Assert.IsNotNull(item.Definition);
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
    /// 既知の組込みファイル名から通常・新規曲・コースの外部種別を選び、相対保存パスを保って特殊種別の取り違えを検出します。
    /// </summary>
    /// <remarks>既知パスと期待種別のDataRowで通常2・新規曲3・コース6の条件差を示します。</remarks>
    [DataTestMethod]
    [DataRow(@"LR2files\CustomFolder\favorite.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\TOP10.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\PLAYLEVEL\1.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\CLEAR\clear.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\RANK\rank.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\ignore.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\INSANE01\01.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\INSANE02\02.lr2folder", 2)]
    [DataRow(@"LR2files\CustomFolder\newsong.lr2folder", 3)]
    [DataRow(@"LR2files\CustomFolder\course1.lr2folder", 6)]
    [DataRow(@"LR2files\CustomFolder\course2.lr2folder", 6)]
    [DataRow(@"LR2files\CustomFolder\course3.lr2folder", 6)]
    public void SourceClassifier_InfersOpenLr2SpecialFolderTypeFromKnownSourcePath(string relativePath, int expectedFolderType)
    {
        string lr2Root = Path.GetFullPath(@"D:\LR2beta3");
        string filePath = Path.Combine(lr2Root, relativePath);

        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = filePath,
            Lr2RootPath = lr2Root
        });

        Assert.AreEqual(relativePath, classification.DatabasePath);
        Assert.AreEqual(expectedFolderType, classification.FolderType);
    }

    /// <summary>
    /// CP932で表現できないパスのカスタム行だけを投影から省き、文字列を破損して外部DBへ登録する誤りを検出します。
    /// </summary>
    [TestMethod]
    public void TryCreateFolderRow_SkipsCp932UnsupportedPath()
    {
        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = @"D:\BMS\emoji_😀\0000.lr2folder",
            LastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Folder"])
        }, out LR2SongDB.folder row);

        Assert.IsFalse(created);
        Assert.IsNull(row);
    }

    /// <summary>
    /// 通常ディレクトリ専用の種別1をファイル行として拒否し、カスタム投影で予約種別を混入させる誤りを検出します。
    /// </summary>
    [TestMethod]
    public void TryCreateFolderRow_SkipsReservedNormalDirectoryType()
    {
        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = @"D:\BMS\#BeMusicSeeker\0000.lr2folder",
            LastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
            FolderType = 1,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Folder"])
        }, out LR2SongDB.folder row);

        Assert.IsFalse(created);
        Assert.IsNull(row);
    }

    /// <summary>
    /// 未知の外部種別整数を持つファイル行を作らず、意味不明な種別を正常カスタム行として保存する誤りを検出します。
    /// </summary>
    [TestMethod]
    public void TryCreateFolderRow_SkipsUnknownCustomFolderType()
    {
        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = @"D:\BMS\#BeMusicSeeker\0000.lr2folder",
            LastWriteTimeUtc = new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
            FolderType = 999,
            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Folder"])
        }, out LR2SongDB.folder row);

        Assert.IsFalse(created);
        Assert.IsNull(row);
    }

    /// <summary>
    /// 通常ディレクトリの実DB同期は不足行を追加してもカスタム行を除去せず、同期範囲を跨ぐ誤削除を検出します。
    /// </summary>
    /// <remarks>実SQLiteで通常行の追加と種別を跨がない削除範囲を観測します。</remarks>
    [TestMethod]
    public void NormalDirectorySync_DoesNotPruneCustomFolderRows()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileProjectionTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string customFolderPath = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker\0000.lr2folder");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = customFolderPath,
                type = 2,
                title = "Custom"
            }, typeof(LR2SongDB.folder));

            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Pack\chart.bms"],
                DirectoryLastWriteTimeUtcResolver = _ => new DateTime(2026, 6, 9, 1, 2, 3, DateTimeKind.Utc),
                AllowPrune = true
            });

            Assert.AreEqual(0, result.DeletedCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == customFolderPath));
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path.EndsWith(@"Pack\", StringComparison.Ordinal)));
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
