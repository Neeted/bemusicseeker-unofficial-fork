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
            DirectoryMetadataResolver = path => metadata.TryGetValue(Normalize(path), out Lr2FolderDirectoryMetadata value) ? value : null,
            GeneratedAtUtc = timestamp.AddDays(1)
        });

        Assert.AreEqual(3, result.Rows.Count);
        Assert.AreEqual(FolderPath(@"D:\BMS"), result.Rows[0].path);
        Assert.AreEqual(FolderPath(@"D:\BMS\Pack"), result.Rows[1].path);
        Assert.AreEqual(FolderPath(@"D:\BMS\Pack\Song"), result.Rows[2].path);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, result.Rows[0].parent);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"D:\BMS"), result.Rows[1].parent);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"D:\BMS\Pack"), result.Rows[2].parent);
        Assert.AreEqual(1, result.Rows[0].type);
        Assert.AreEqual(timestamp.ToUnixtime(), result.Rows[0].date);
        Assert.AreEqual(timestamp.AddSeconds(2).ToUnixtime(), result.Rows[2].date);
        Assert.AreEqual(timestamp.AddDays(1).ToUnixtime(), result.Rows[0].adddate);
        Assert.IsTrue(result.ScopePaths.Contains(FolderPath(@"D:\BMS\Pack\Song")));
        Assert.AreEqual(0, result.SkippedUnsupportedPathCount);
        Assert.AreEqual(0, result.SkippedMissingMetadataCount);
    }

    [TestMethod]
    public void GenerateNormalDirectoryRows_UsesFolderInfoTitleAndPreservesExistingAddDate()
    {
        DateTime generatedAt = new(2026, 6, 2, 1, 2, 3, DateTimeKind.Utc);
        string folderPath = FolderPath(@"D:\BMS\Pack");

        Lr2FolderGenerationResult result = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\BMS"],
            ChartPaths = [@"D:\BMS\Pack\chart.bms"],
            ExistingRows =
            [
                new LR2SongDB.folder
                {
                    path = folderPath,
                    adddate = 12345
                }
            ],
            DirectoryMetadataResolver = path => string.Equals(Normalize(path), Normalize(@"D:\BMS\Pack"), StringComparison.OrdinalIgnoreCase)
                ? new Lr2FolderDirectoryMetadata(generatedAt, "Folder Info Title")
                : new Lr2FolderDirectoryMetadata(generatedAt),
            GeneratedAtUtc = generatedAt
        });

        LR2SongDB.folder row = result.Rows.Single(candidate => string.Equals(candidate.path, folderPath, StringComparison.Ordinal));

        Assert.AreEqual("Folder Info Title", row.title);
        Assert.AreEqual(12345, row.adddate);
        Assert.AreEqual(Lr2FolderRowSourceKind.FolderInfoDirectory, result.SourceKinds[folderPath]);
    }

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

        CollectionAssert.AreEqual(
            new[]
            {
                FolderPath(@"D:\BMS"),
                FolderPath(@"D:\BMS\Pack"),
                FolderPath(@"D:\BMS\Pack\Song")
            },
            result.Rows.Select(row => row.path).ToArray());
        Assert.IsFalse(result.Rows.Any(row => string.Equals(row.path, FolderPath(@"D:\BMS\Ignored"), StringComparison.Ordinal)));
    }

    [TestMethod]
    public void GenerateNormalDirectoryRows_PreservesDriveRootFolderPath()
    {
        Lr2FolderGenerationResult result = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\"],
            ChartPaths = [@"D:\Pack\chart.bms"],
            DirectoryMetadataResolver = ConstantMetadataResolver()
        });

        Assert.AreEqual(FolderPath(@"D:\"), result.Rows[0].path);
        Assert.AreEqual(FolderPath(@"D:\Pack"), result.Rows[1].path);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, result.Rows[0].parent);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"D:\"), result.Rows[1].parent);
        Assert.AreNotEqual(@"D:\\", result.Rows[0].path);
    }

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
        var changedPack = Clone(pack);
        changedPack.title = "Old Title";

        Lr2FolderGenerationSyncPlan plan = Lr2FolderGenerationScopePlanner.PlanNormalDirectorySync(
            generation,
            [Clone(root), changedPack],
            [@"D:\BMS"]);

        Assert.AreEqual(1, plan.UpsertRows.Count);
        Assert.AreEqual(pack.path, plan.UpsertRows[0].path);
        Assert.AreEqual(0, plan.DeletePaths.Count);
    }

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

    [TestMethod]
    public void PlanNormalDirectorySync_RepairsNormalRowExactKeyDriftWithDeleteAndUpsert()
    {
        Lr2FolderGenerationResult generation = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = [@"D:\BMS"],
            ChartPaths = [@"D:\BMS\Pack\chart.bms"],
            DirectoryMetadataResolver = ConstantMetadataResolver()
        });
        LR2SongDB.folder root = generation.Rows.Single(row => string.Equals(row.path, FolderPath(@"D:\BMS"), StringComparison.Ordinal));
        LR2SongDB.folder pack = generation.Rows.Single(row => string.Equals(row.path, FolderPath(@"D:\BMS\Pack"), StringComparison.Ordinal));
        var driftedPack = Clone(pack);
        driftedPack.path = Normalize(@"D:\BMS\Pack");

        Lr2FolderGenerationSyncPlan plan = Lr2FolderGenerationScopePlanner.PlanNormalDirectorySync(
            generation,
            [Clone(root), driftedPack],
            [@"D:\BMS"]);

        CollectionAssert.AreEqual(new[] { driftedPack.path }, plan.DeletePaths.ToArray());
        Assert.AreEqual(1, plan.UpsertRows.Count);
        Assert.AreEqual(pack.path, plan.UpsertRows[0].path);
    }

    [TestMethod]
    public void PlanNormalDirectorySync_SuppressesPruneAndExactKeyRepairWhenGenerationHasMissingMetadata()
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
        var caseDriftRoot = Clone(root);
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

        Assert.AreEqual(0, plan.UpsertRows.Count);
        Assert.AreEqual(0, plan.DeletePaths.Count);
    }

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

    [TestMethod]
    public void DirectoryMetadataBuilder_TreatsUnixEpochOrOlderTimestampAsMissing()
    {
        Lr2FolderDirectoryMetadataSnapshot snapshot = Lr2FolderDirectoryMetadataBuilder.Build(new Lr2FolderDirectoryMetadataBuildRequest
        {
            DirectoryPaths = [@"D:\BMS", @"D:\BMS\Pack"],
            DirectoryLastWriteTimeUtcResolver = path => string.Equals(Normalize(path), Normalize(@"D:\BMS\Pack"), StringComparison.OrdinalIgnoreCase)
                ? new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc)
                : new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(2, snapshot.RequestedDirectoryCount);
        Assert.AreEqual(1, snapshot.ResolvedDirectoryCount);
        Assert.AreEqual(1, snapshot.MissingDirectoryCount);
        Assert.IsFalse(snapshot.TryGetMetadata(@"D:\BMS", out _));
        Assert.IsTrue(snapshot.TryGetMetadata(@"D:\BMS\Pack", out Lr2FolderDirectoryMetadata metadata));
        Assert.AreEqual(new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc), metadata.LastWriteTimeUtc);
    }

    private static string Normalize(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath);
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
