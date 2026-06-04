using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
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
}
