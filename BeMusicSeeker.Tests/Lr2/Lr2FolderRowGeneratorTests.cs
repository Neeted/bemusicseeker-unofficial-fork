using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2FolderRowGeneratorTests
{
    /// <summary>通常・ドライブ・UNCの実ルートを停止境界と区別し、直下の子のparentをルートCRCへ揃えます。重なる実ルートも祖先包含で落としません。</summary>
    [DataTestMethod]
    // 字句上の階層判定だけを使い、実ドライブの割当や共有サーバーへ依存しません。
    [DataRow(@"D:\BMS")]
    [DataRow(@"D:\")]
    [DataRow(@"\\server\share\")]
    public void FolderHierarchy_RealRootsRemainIncludedAndChildrenUseRootCrc(string root)
    {
        string child = Path.Combine(root, "Child"), file = Path.Combine(child, "custom.lr2folder");
        IReadOnlySet<string> roots = Lr2FolderPhysicalParentDirectoryTargetHelper.CreateRoots([root, child], root, []);
        IReadOnlySet<string> boundaries = Lr2FolderPhysicalParentDirectoryTargetHelper.CreateBoundaries(roots, null, []);
        (string DatabaseDirectory, string PhysicalDirectory)[] parents = Lr2FolderPhysicalParentDirectoryTargetHelper.EnumerateNormalizedParents(file, file, boundaries, roots).ToArray();
        CollectionAssert.AreEquivalent(new[] { Normalize(root), Normalize(child) }, parents.Select(target => target.DatabaseDirectory).ToArray());
        Assert.AreEqual(2, roots.Count);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, Lr2FolderPhysicalParentDirectoryTargetHelper.ComputeNormalizedParentHash(Normalize(root), roots));
        // 既存song正規化器はPath.GetDirectoryNameの親表記へ区切りとNULを加えます。UNC共有名には余分な末尾区切りを加えません。
        Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(child)), Lr2FolderPhysicalParentDirectoryTargetHelper.ComputeNormalizedParentHash(Normalize(child), Lr2FolderPhysicalParentDirectoryTargetHelper.CreateRoots([root], null, [])));
        Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, Lr2FolderPhysicalParentDirectoryTargetHelper.ComputeNormalizedParentHash(Normalize(child), roots));
    }

    /// <summary>
    /// 譜面階層からルート・祖先・直近フォルダの行を作り、親CRC・保存種別・入力時刻が揃うことで階層欠落や時刻混同を検出する。
    /// </summary>
    [TestMethod]
    public void GenerateNormalDirectoryRows_CreatesRootAncestorAndChartDirectoryRows()
    {
        DateTime timestamp = new(2026, 6, 1, 1, 2, 3, DateTimeKind.Utc);
        var metadata = new Dictionary<string, Lr2FolderDirectoryMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            [Normalize(@"D:\BMS")] = new Lr2FolderDirectoryMetadata(timestamp),
            [Normalize(@"D:\BMS\Pack")] = new Lr2FolderDirectoryMetadata(timestamp.AddSeconds(1)),
            [Normalize(@"D:\BMS\Pack\Song")] = new Lr2FolderDirectoryMetadata(timestamp.AddSeconds(2))
        };

        Lr2FolderGenerationResult result = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\BMS"],
            ChartPaths = [@"D:\BMS\Pack\Song\chart.bms"],
            DirectoryMetadataResolver = path => metadata.TryGetValue(Normalize(path), out Lr2FolderDirectoryMetadata? value) ? value! : null!,
            GeneratedAtUtc = timestamp.AddDays(1)
        });

        LR2SongDB.folder root = result.Rows.Single(row => row.path == FolderPath(@"D:\BMS"));
        LR2SongDB.folder pack = result.Rows.Single(row => row.path == FolderPath(@"D:\BMS\Pack"));
        LR2SongDB.folder song = result.Rows.Single(row => row.path == FolderPath(@"D:\BMS\Pack\Song"));

        Assert.AreEqual(3, result.Rows.Count);
        Assert.AreEqual(FolderPath(@"D:\BMS"), root.path);
        Assert.AreEqual(FolderPath(@"D:\BMS\Pack"), pack.path);
        Assert.AreEqual(FolderPath(@"D:\BMS\Pack\Song"), song.path);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, root.parent);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"D:\BMS"), pack.parent);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"D:\BMS\Pack"), song.parent);
        Assert.AreEqual(1, root.type);
        Assert.AreEqual(timestamp.ToUnixtime(), root.date);
        Assert.AreEqual(timestamp.AddSeconds(2).ToUnixtime(), song.date);
        Assert.AreEqual(timestamp.AddDays(1).ToUnixtime(), root.adddate);
        Assert.IsTrue(result.ScopePaths.Contains(FolderPath(@"D:\BMS\Pack\Song")));
        Assert.AreEqual(0, result.SkippedUnsupportedPathCount);
        Assert.AreEqual(0, result.SkippedMissingMetadataCount);
    }

    /// <summary>
    /// folderinfoのタイトルを採用し、exact存続だけ追加時刻を保持する。別case旧行からの値継承と共存時の上書きを防ぐ。
    /// </summary>
    [DataTestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void GenerateNormalDirectoryRows_UsesFolderInfoTitleAndPreservesExistingAddDate(bool exactExists, bool aliasExists)
    {
        DateTime generatedAt = new(2026, 6, 2, 1, 2, 3, DateTimeKind.Utc);
        string folderPath = FolderPath(@"D:\BMS\Pack");

        Lr2FolderGenerationResult result = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\BMS"],
            ChartPaths = [@"D:\BMS\Pack\chart.bms"],
            ExistingRows = (exactExists ? new[] { new LR2SongDB.folder { path = folderPath, adddate = 12345 } } : [])
                .Concat(aliasExists ? new[] { new LR2SongDB.folder { path = folderPath.ToLowerInvariant(), adddate = 98765 } } : []).ToArray(),
            DirectoryMetadataResolver = path => string.Equals(Normalize(path), Normalize(@"D:\BMS\Pack"), StringComparison.OrdinalIgnoreCase)
                ? new Lr2FolderDirectoryMetadata(generatedAt, "Folder Info Title")
                : new Lr2FolderDirectoryMetadata(generatedAt),
            GeneratedAtUtc = generatedAt
        });

        LR2SongDB.folder row = result.Rows.Single(candidate => string.Equals(candidate.path, folderPath, StringComparison.Ordinal));

        Assert.AreEqual("Folder Info Title", row.title);
        Assert.AreEqual(exactExists ? 12345 : generatedAt.ToUnixtime(), row.adddate);
        Assert.AreEqual(Lr2FolderRowSourceKind.FolderInfoDirectory, result.SourceKinds[folderPath]);
    }

    /// <summary>
    /// 登録ルート外を除外し未対応文字の同一フォルダを一度だけ集計して、範囲拡張や重複診断を検出する。
    /// </summary>
    [TestMethod]
    public void GenerateNormalDirectoryRows_SkipsOutsideRootAndCp932UnsupportedDirectory()
    {
        Lr2FolderGenerationResult result = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\BMS"],
            ChartPaths =
            [
                @"E:\Other\chart.bms",
                @"D:\BMS\emoji_😀\chart1.bms",
                @"D:\BMS\emoji_😀\chart2.bms"
            ],
            DirectoryMetadataResolver = ConstantMetadataResolver()
        });

        Assert.AreEqual(1, result.Rows.Count);
        Assert.AreEqual(FolderPath(@"D:\BMS"), result.Rows[0].path);
        Assert.AreEqual(1, result.SkippedUnsupportedPathCount);
        Assert.AreEqual(0, result.SkippedMissingMetadataCount);
        Assert.IsFalse(result.ScopePaths.Contains(FolderPath(@"E:\Other")));
    }

    /// <summary>
    /// 重複ルートと同じフォルダの複数譜面を合流し、各フォルダのメタデータを一度だけ取得して重複処理を検出する。
    /// </summary>
    [TestMethod]
    public void GenerateNormalDirectoryRows_DeduplicatesDirectoriesBeforeMetadataLookup()
    {
        var metadataCalls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        Lr2FolderGenerationResult result = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\BMS", @"D:\BMS\"],
            ChartPaths =
            [
                @"D:\BMS\Pack\Song\chart1.bms",
                @"D:\BMS\Pack\Song\chart2.bms"
            ],
            DirectoryMetadataResolver = path =>
            {
                string key = Normalize(path);
                metadataCalls.TryGetValue(key, out int count);
                metadataCalls[key] = count + 1;
                return new Lr2FolderDirectoryMetadata(new DateTime(2026, 6, 3, 1, 2, 3, DateTimeKind.Utc));
            }
        });

        Assert.AreEqual(3, result.Rows.Count);
        Assert.AreEqual(1, metadataCalls[Normalize(@"D:\BMS")]);
        Assert.AreEqual(1, metadataCalls[Normalize(@"D:\BMS\Pack")]);
        Assert.AreEqual(1, metadataCalls[Normalize(@"D:\BMS\Pack\Song")]);
    }

    /// <summary>
    /// 明示したディレクトリ集合を採用し譜面由来の別フォルダを生成しないことで、指定対象の無断拡張を検出する。
    /// </summary>
    [TestMethod]
    public void GenerateNormalDirectoryRows_UsesProvidedDirectoryTargetsWhenPresent()
    {
        DateTime timestamp = new(2026, 6, 3, 1, 2, 3, DateTimeKind.Utc);

        Lr2FolderGenerationResult result = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\BMS"],
            ChartPaths = [@"D:\BMS\Ignored\chart.bms"],
            DirectoryPaths =
            [
                @"D:\BMS",
                @"D:\BMS\Pack",
                @"D:\BMS\Pack\Song"
            ],
            DirectoryMetadataResolver = _ => new Lr2FolderDirectoryMetadata(timestamp)
        });

        CollectionAssert.AreEquivalent(
            new[]
            {
                FolderPath(@"D:\BMS"),
                FolderPath(@"D:\BMS\Pack"),
                FolderPath(@"D:\BMS\Pack\Song")
            },
            result.Rows.Select(row => row.path).ToArray());
        Assert.IsFalse(result.Rows.Any(row => string.Equals(row.path, FolderPath(@"D:\BMS\Ignored"), StringComparison.Ordinal)));
    }

    /// <summary>
    /// ドライブ直下の区切りと子の親CRCを維持し、ルートを通常パスと同じに処理して壊す誤りを検出する。
    /// </summary>
    [TestMethod]
    public void GenerateNormalDirectoryRows_PreservesDriveRootFolderPath()
    {
        Lr2FolderGenerationResult result = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\"],
            ChartPaths = [@"D:\Pack\chart.bms"],
            DirectoryMetadataResolver = ConstantMetadataResolver()
        });

        LR2SongDB.folder root = result.Rows.Single(row => row.path == FolderPath(@"D:\"));
        LR2SongDB.folder pack = result.Rows.Single(row => row.path == FolderPath(@"D:\Pack"));
        Assert.AreEqual(2, result.Rows.Count);
        Assert.AreEqual(FolderPath(@"D:\"), root.path);
        Assert.AreEqual(FolderPath(@"D:\Pack"), pack.path);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, root.parent);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"D:\"), pack.parent);
        Assert.AreNotEqual(@"D:\\", root.path);
    }

    /// <summary>
    /// メタデータがない子フォルダを行・反映範囲へ入れず欠落数を報告して、推測時刻での反映を防ぐ。
    /// </summary>
    [TestMethod]
    public void GenerateNormalDirectoryRows_SkipsRowsWithoutDirectoryMetadata()
    {
        Lr2FolderGenerationResult result = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\BMS"],
            ChartPaths = [@"D:\BMS\Pack\chart.bms"],
            DirectoryMetadataResolver = path => string.Equals(Normalize(path), Normalize(@"D:\BMS"), StringComparison.OrdinalIgnoreCase)
                ? new Lr2FolderDirectoryMetadata(new DateTime(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc))
                : null
        });

        Assert.AreEqual(1, result.Rows.Count);
        Assert.AreEqual(FolderPath(@"D:\BMS"), result.Rows[0].path);
        Assert.AreEqual(1, result.SkippedMissingMetadataCount);
        Assert.IsFalse(result.ScopePaths.Contains(FolderPath(@"D:\BMS\Pack")));
    }

    /// <summary>
    /// 空白付きTITLEの最初の値を採用しSUBTITLEと後続TITLEを区別して、別指示や後続値による上書きを検出する。
    /// </summary>
    [TestMethod]
    public void ParseFolderInfoTitle_ReturnsFirstTitleValue()
    {
        string title = Lr2FolderRowGenerator.ParseFolderInfoTitle(
        [
            "  #SUBTITLE ignored",
            "\t#TITLE  Custom Folder  ",
            "#TITLE Later"
        ]);

        Assert.AreEqual("Custom Folder", title);
    }

    /// <summary>
    /// 同一のルート行を更新せずタイトルが異なる子行だけを更新対象にして、無変更行の不要更新を検出する。
    /// </summary>
    [TestMethod]
    public void PlanNormalDirectorySync_UpsertsOnlyNewOrChangedRows()
    {
        Lr2FolderGenerationResult generation = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\BMS"],
            ChartPaths = [@"D:\BMS\Pack\chart.bms"],
            DirectoryMetadataResolver = ConstantMetadataResolver(),
            GeneratedAtUtc = new DateTime(2026, 6, 5, 1, 2, 3, DateTimeKind.Utc)
        });
        LR2SongDB.folder root = generation.Rows.Single(row => string.Equals(row.path, FolderPath(@"D:\BMS"), StringComparison.Ordinal));
        LR2SongDB.folder pack = generation.Rows.Single(row => string.Equals(row.path, FolderPath(@"D:\BMS\Pack"), StringComparison.Ordinal));
        LR2SongDB.folder changedPack = Clone(pack);
        changedPack.title = "Old Title";

        Lr2FolderGenerationSyncPlan plan = Lr2FolderGenerationScopePlanner.PlanNormalDirectorySync(
            generation,
            [Clone(root), changedPack],
            [@"D:\BMS"]);

        Assert.AreEqual(1, plan.UpsertRows.Count);
        Assert.AreEqual(pack.path, plan.UpsertRows[0].path);
        Assert.AreEqual(0, plan.DeletePaths.Count);
    }

    /// <summary>
    /// 反映範囲内の古い通常行だけを削除し、カスタム行・他ルート・接頭辞だけ共通の隣接ルートを保持する。
    /// </summary>
    [TestMethod]
    public void PlanNormalDirectorySync_PrunesOnlyStaleNormalRowsInsideScope()
    {
        Lr2FolderGenerationResult generation = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\BMS"],
            ChartPaths = [@"D:\BMS\Pack\chart.bms"],
            DirectoryMetadataResolver = ConstantMetadataResolver()
        });
        LR2SongDB.folder root = generation.Rows.Single(row => string.Equals(row.path, FolderPath(@"D:\BMS"), StringComparison.Ordinal));
        LR2SongDB.folder pack = generation.Rows.Single(row => string.Equals(row.path, FolderPath(@"D:\BMS\Pack"), StringComparison.Ordinal));
        var staleNormal = new LR2SongDB.folder
        {
            path = FolderPath(@"D:\BMS\Removed"),
            type = 1
        };
        var customFolder = new LR2SongDB.folder
        {
            path = FolderPath(@"D:\BMS\Custom"),
            type = 2
        };
        var outsideScope = new LR2SongDB.folder
        {
            path = FolderPath(@"E:\Other"),
            type = 1
        };
        var rootSibling = new LR2SongDB.folder
        {
            path = FolderPath(@"D:\BMS2"),
            type = 1
        };

        Lr2FolderGenerationSyncPlan plan = Lr2FolderGenerationScopePlanner.PlanNormalDirectorySync(
            generation,
            [Clone(root), Clone(pack), staleNormal, customFolder, outsideScope, rootSibling],
            [@"D:\BMS"]);

        CollectionAssert.AreEqual(new[] { staleNormal.path }, plan.DeletePaths.ToArray());
        Assert.AreEqual(0, plan.UpsertRows.Count);
    }

    /// <summary>
    /// 明示した削除範囲内だけで古い行を消し、同じ登録ルート内の別範囲を削除する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void PlanNormalDirectorySync_PrunesOnlyRequestedPruneScope()
    {
        Lr2FolderGenerationResult generation = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\BMS"],
            ChartPaths = [@"D:\BMS\Pack\chart.bms"],
            DirectoryMetadataResolver = ConstantMetadataResolver()
        });
        LR2SongDB.folder root = generation.Rows.Single(row => string.Equals(row.path, FolderPath(@"D:\BMS"), StringComparison.Ordinal));
        LR2SongDB.folder pack = generation.Rows.Single(row => string.Equals(row.path, FolderPath(@"D:\BMS\Pack"), StringComparison.Ordinal));
        var staleInPruneScope = new LR2SongDB.folder
        {
            path = FolderPath(@"D:\BMS\Removed"),
            type = 1
        };
        var staleOutsidePruneScope = new LR2SongDB.folder
        {
            path = FolderPath(@"D:\BMS\Other"),
            type = 1
        };

        Lr2FolderGenerationSyncPlan plan = Lr2FolderGenerationScopePlanner.PlanNormalDirectorySync(
            generation,
            [Clone(root), Clone(pack), staleInPruneScope, staleOutsidePruneScope],
            [@"D:\BMS"],
            [@"D:\BMS\Removed"]);

        CollectionAssert.AreEqual(new[] { staleInPruneScope.path }, plan.DeletePaths.ToArray());
        Assert.AreEqual(0, plan.UpsertRows.Count);
    }

    /// <summary>
    /// 通常生成行と同じパスのカスタム行を更新・削除対象にせず、別種別の保存行を奪う誤りを検出する。
    /// </summary>
    [TestMethod]
    public void PlanNormalDirectorySync_DoesNotOverwriteNonNormalRowsWithSamePath()
    {
        Lr2FolderGenerationResult generation = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\BMS"],
            ChartPaths = [@"D:\BMS\Pack\chart.bms"],
            DirectoryMetadataResolver = ConstantMetadataResolver()
        });
        LR2SongDB.folder root = generation.Rows.Single(row => string.Equals(row.path, FolderPath(@"D:\BMS"), StringComparison.Ordinal));
        var existingCustomRoot = new LR2SongDB.folder
        {
            path = root.path,
            type = 2
        };

        Lr2FolderGenerationSyncPlan plan = Lr2FolderGenerationScopePlanner.PlanNormalDirectorySync(
            generation,
            [existingCustomRoot],
            [@"D:\BMS"]);

        Assert.IsFalse(plan.UpsertRows.Any(row => string.Equals(row.path, root.path, StringComparison.Ordinal)));
        Assert.IsFalse(plan.DeletePaths.Contains(existingCustomRoot.path));
    }

    /// <summary>
    /// 末尾区切りが欠けた保存キーを削除して正規キーを更新し、正規化一致だけで不正な実キーが残る誤りを検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PlanNormalDirectorySync_RepairsNormalRowExactKeyDriftWithDeleteAndUpsert(bool caseOnly)
    {
        Lr2FolderGenerationResult generation = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\BMS"],
            ChartPaths = [@"D:\BMS\Pack\chart.bms"],
            DirectoryMetadataResolver = ConstantMetadataResolver()
        });
        LR2SongDB.folder root = generation.Rows.Single(row => string.Equals(row.path, FolderPath(@"D:\BMS"), StringComparison.Ordinal));
        LR2SongDB.folder pack = generation.Rows.Single(row => string.Equals(row.path, FolderPath(@"D:\BMS\Pack"), StringComparison.Ordinal));
        LR2SongDB.folder driftedPack = Clone(pack);
        driftedPack.path = caseOnly ? pack.path.ToLowerInvariant() : Normalize(@"D:\BMS\Pack");

        Lr2FolderGenerationSyncPlan plan = Lr2FolderGenerationScopePlanner.PlanNormalDirectorySync(
            generation,
            [Clone(root), driftedPack],
            [@"D:\BMS"]);

        CollectionAssert.AreEqual(new[] { driftedPack.path }, plan.DeletePaths.ToArray());
        Assert.AreEqual(1, plan.UpsertRows.Count);
        Assert.AreEqual(pack.path, plan.UpsertRows[0].path);
    }

    /// <summary>
    /// 生成入力が欠ける場合は旧行削除を抑止し、metadataが確認済みの新exact行は通常追加する。別case旧行の存在を理由に追加だけを特別扱いしない。
    /// </summary>
    [TestMethod]
    public void PlanNormalDirectorySync_SuppressesPruneButUpsertsConfirmedNewExactRowWhenGenerationHasMissingMetadata()
    {
        Lr2FolderGenerationResult generation = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\BMS"],
            ChartPaths = [@"D:\BMS\Pack\chart.bms"],
            DirectoryMetadataResolver = path => string.Equals(Normalize(path), Normalize(@"D:\BMS"), StringComparison.OrdinalIgnoreCase)
                ? new Lr2FolderDirectoryMetadata(new DateTime(2026, 6, 6, 1, 2, 3, DateTimeKind.Utc))
                : null
        });
        LR2SongDB.folder root = generation.Rows.Single();
        LR2SongDB.folder caseDriftRoot = Clone(root);
        caseDriftRoot.path = root.path.ToLowerInvariant();
        var staleNormal = new LR2SongDB.folder
        {
            path = FolderPath(@"D:\BMS\Removed"),
            type = 1
        };

        Lr2FolderGenerationSyncPlan plan = Lr2FolderGenerationScopePlanner.PlanNormalDirectorySync(
            generation,
            [caseDriftRoot, staleNormal],
            [@"D:\BMS"]);

        Assert.AreEqual(1, plan.UpsertRows.Count);
        Assert.AreEqual(root.path, plan.UpsertRows.Single().path);
        Assert.AreEqual(0, plan.DeletePaths.Count);
    }

    /// <summary>
    /// 重複ディレクトリを合流しfolderinfoだけを読んで元ディレクトリ時刻とタイトルを保持し、一般ファイル誤読や重複取得を検出する。
    /// </summary>
    [TestMethod]
    public void DirectoryMetadataBuilder_DeduplicatesDirectoriesAndAppliesFolderInfoTitle()
    {
        DateTime rootTime = new(2026, 6, 7, 1, 2, 3, DateTimeKind.Utc);
        DateTime packTime = rootTime.AddMinutes(1);
        var timeByDirectory = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase)
        {
            [Normalize(@"D:\BMS")] = rootTime,
            [Normalize(@"D:\BMS\Pack")] = packTime
        };
        var readPaths = new List<string>();

        Lr2FolderDirectoryMetadataSnapshot snapshot = Lr2FolderDirectoryMetadataBuilder.Build(new Lr2FolderDirectoryMetadataBuildRequest
        {
            DirectoryPaths = [@"D:\BMS", @"D:\BMS\", @"D:\BMS\Pack"],
            FolderInfoFilePaths =
            [
                @"D:\BMS\Pack\folderinfo.txt",
                @"D:\BMS\Pack\notes.txt"
            ],
            DirectoryLastWriteTimeUtcResolver = path => timeByDirectory.TryGetValue(Normalize(path), out DateTime? timestamp) ? timestamp : null,
            FolderInfoLinesReader = path =>
            {
                readPaths.Add(path);
                return ["#TITLE Pack Title"];
            }
        });

        Assert.AreEqual(2, snapshot.RequestedDirectoryCount);
        Assert.AreEqual(2, snapshot.ResolvedDirectoryCount);
        Assert.AreEqual(0, snapshot.MissingDirectoryCount);
        Assert.AreEqual(1, snapshot.FolderInfoCandidateCount);
        Assert.AreEqual(1, snapshot.FolderInfoAppliedCount);
        Assert.AreEqual(0, snapshot.FolderInfoReadFailureCount);
        Assert.IsTrue(snapshot.TryGetMetadata(@"D:\BMS\Pack", out Lr2FolderDirectoryMetadata metadata));
        Assert.AreEqual(packTime, metadata.LastWriteTimeUtc);
        Assert.AreEqual("Pack Title", metadata.FolderInfoTitle);
        CollectionAssert.AreEqual(new[] { Path.GetFullPath(@"D:\BMS\Pack\folderinfo.txt") }, readPaths.ToArray());
    }

    /// <summary>
    /// 列挙済みfolderinfoエントリからタイトルを取得し、ファイル時刻とディレクトリ時刻を混同せず保持する。
    /// </summary>
    [TestMethod]
    public void DirectoryMetadataBuilder_AppliesFolderInfoEntryMap()
    {
        DateTime packTime = new(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc);
        string folderInfoPath = Path.GetFullPath(@"D:\BMS\Pack\folderinfo.txt");
        var readPaths = new List<string>();

        Lr2FolderDirectoryMetadataSnapshot snapshot = Lr2FolderDirectoryMetadataBuilder.Build(new Lr2FolderDirectoryMetadataBuildRequest
        {
            DirectoryPaths = [@"D:\BMS\Pack"],
            FolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, new DateTime(2026, 6, 8, 1, 2, 4, DateTimeKind.Utc), 99)
            },
            DirectoryLastWriteTimeUtcResolver = path => string.Equals(Normalize(path), Normalize(@"D:\BMS\Pack"), StringComparison.OrdinalIgnoreCase)
                ? packTime
                : null,
            FolderInfoLinesReader = path =>
            {
                readPaths.Add(path);
                return ["#TITLE Entry Map Title"];
            }
        });

        Assert.AreEqual(1, snapshot.FolderInfoCandidateCount);
        Assert.AreEqual(1, snapshot.FolderInfoAppliedCount);
        Assert.IsTrue(snapshot.TryGetMetadata(@"D:\BMS\Pack", out Lr2FolderDirectoryMetadata metadata));
        Assert.AreEqual(packTime, metadata.LastWriteTimeUtc);
        Assert.AreEqual("Entry Map Title", metadata.FolderInfoTitle);
        CollectionAssert.AreEqual(new[] { folderInfoPath }, readPaths.ToArray());
    }

    /// <summary>
    /// IO・権限エラーのディレクトリとfolderinfo読取り失敗を区別して数え、成功したディレクトリ情報だけを保持する。
    /// </summary>
    [TestMethod]
    public void DirectoryMetadataBuilder_TracksMissingDirectoryAndFolderInfoReadFailure()
    {
        Lr2FolderDirectoryMetadataSnapshot snapshot = Lr2FolderDirectoryMetadataBuilder.Build(new Lr2FolderDirectoryMetadataBuildRequest
        {
            DirectoryPaths = [@"D:\BMS", @"D:\BMS\Missing", @"D:\BMS\Security"],
            FolderInfoFilePaths = [@"D:\BMS\folderinfo.txt"],
            DirectoryLastWriteTimeUtcResolver = path =>
            {
                if (string.Equals(Normalize(path), Normalize(@"D:\BMS"), StringComparison.OrdinalIgnoreCase))
                {
                    return new DateTime(2026, 6, 7, 1, 2, 3, DateTimeKind.Utc);
                }
                if (string.Equals(Normalize(path), Normalize(@"D:\BMS\Security"), StringComparison.OrdinalIgnoreCase))
                {
                    throw new SecurityException("denied");
                }
                throw new IOException("missing");
            },
            FolderInfoLinesReader = _ => throw new IOException("locked")
        });

        Assert.AreEqual(3, snapshot.RequestedDirectoryCount);
        Assert.AreEqual(1, snapshot.ResolvedDirectoryCount);
        Assert.AreEqual(2, snapshot.MissingDirectoryCount);
        Assert.AreEqual(1, snapshot.FolderInfoCandidateCount);
        Assert.AreEqual(0, snapshot.FolderInfoAppliedCount);
        Assert.AreEqual(1, snapshot.FolderInfoReadFailureCount);
        Assert.IsTrue(snapshot.TryGetMetadata(@"D:\BMS", out Lr2FolderDirectoryMetadata metadata));
        Assert.IsFalse(metadata.HasFolderInfoTitle);
        Assert.IsFalse(snapshot.TryGetMetadata(@"D:\BMS\Missing", out _));
        Assert.IsFalse(snapshot.TryGetMetadata(@"D:\BMS\Security", out _));
    }

    /// <summary>
    /// 時刻取得窓口がない場合は実在する一時ディレクトリも未取得と扱い、無断のファイルシステム読取りへの切替えを検出する。
    /// </summary>
    [TestMethod]
    public void DirectoryMetadataBuilder_DoesNotReadDirectoryTimestampWithoutResolver()
    {
        Lr2FolderDirectoryMetadataSnapshot snapshot = Lr2FolderDirectoryMetadataBuilder.Build(new Lr2FolderDirectoryMetadataBuildRequest
        {
            DirectoryPaths = [Path.GetTempPath()]
        });

        Assert.AreEqual(1, snapshot.RequestedDirectoryCount);
        Assert.AreEqual(0, snapshot.ResolvedDirectoryCount);
        Assert.AreEqual(1, snapshot.MissingDirectoryCount);
    }

    /// <summary>
    /// 1601年とエポック付近の時刻を有効入力として保持し、古い時刻を未取得に読み替える誤りを検出する。
    /// </summary>
    [TestMethod]
    public void DirectoryMetadataBuilder_PreservesUnixEpochOrOlderTimestampAsValid()
    {
        Lr2FolderDirectoryMetadataSnapshot snapshot = Lr2FolderDirectoryMetadataBuilder.Build(new Lr2FolderDirectoryMetadataBuildRequest
        {
            DirectoryPaths = [@"D:\BMS", @"D:\BMS\Pack"],
            DirectoryLastWriteTimeUtcResolver = path => string.Equals(Normalize(path), Normalize(@"D:\BMS\Pack"), StringComparison.OrdinalIgnoreCase)
                ? new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc)
                : new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(2, snapshot.RequestedDirectoryCount);
        Assert.AreEqual(2, snapshot.ResolvedDirectoryCount);
        Assert.AreEqual(0, snapshot.MissingDirectoryCount);
        Assert.IsTrue(snapshot.TryGetMetadata(@"D:\BMS", out Lr2FolderDirectoryMetadata rootMetadata));
        Assert.AreEqual(new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc), rootMetadata.LastWriteTimeUtc);
        Assert.IsTrue(snapshot.TryGetMetadata(@"D:\BMS\Pack", out Lr2FolderDirectoryMetadata metadata));
        Assert.AreEqual(new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc), metadata.LastWriteTimeUtc);
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

    private static Func<string, Lr2FolderDirectoryMetadata> ConstantMetadataResolver()
    {
        DateTime timestamp = new(2026, 6, 3, 1, 2, 3, DateTimeKind.Utc);
        return _ => new Lr2FolderDirectoryMetadata(timestamp);
    }

    private static LR2SongDB.folder Clone(LR2SongDB.folder source)
    {
        return new LR2SongDB.folder
        {
            title = source.title,
            subtitle = source.subtitle,
            category = source.category,
            info_a = source.info_a,
            info_b = source.info_b,
            command = source.command,
            path = source.path,
            type = source.type,
            banner = source.banner,
            parent = source.parent,
            date = source.date,
            max = source.max,
            adddate = source.adddate
        };
    }
}
