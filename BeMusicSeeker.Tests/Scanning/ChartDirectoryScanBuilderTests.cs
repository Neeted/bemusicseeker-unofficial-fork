using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartDirectoryScanBuilderTests
{
    /// <summary>
    /// 譜面直下から入れ子の音声・画像を走査し、所有フォルダへ各一件だけ集約して誤分類や重複を検出する。
    /// </summary>
    /// <remarks>実ファイルシステムの列挙結果とメタデータを観測するため、一時フォルダ内の実入力を使用する。</remarks>
    [TestMethod]
    public void BuildFromRoots_AggregatesResourcesIntoOwningChartDirectory()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartDirScan_" + Guid.NewGuid().ToString("N"));
        string chartDir = Path.Combine(tempRoot, "song");
        string nestedResourceDir = Path.Combine(chartDir, "sound");
        string imageDir = Path.Combine(chartDir, "image");
        Directory.CreateDirectory(nestedResourceDir);
        Directory.CreateDirectory(imageDir);
        File.WriteAllText(Path.Combine(chartDir, "chart.bms"), "#PLAYER 1");
        File.WriteAllText(Path.Combine(nestedResourceDir, "00.wav"), "audio");
        File.WriteAllText(Path.Combine(imageDir, "bg.jpg"), "image");
        try
        {
            ChartScanResult result = ChartDirectoryScanBuilder.BuildFromRoots([tempRoot]);

            CollectionAssert.Contains(result.ChartDirectories.ToList(), chartDir);
            Assert.AreEqual(1, result.ChartFilePaths.Count);
            Assert.IsTrue(result.AudioRelativePathHashesByChartDirectory.TryGetValue(chartDir, out uint[]? audioRelativeHashes));
            Assert.IsTrue(result.ImageRelativePathHashesByChartDirectory.TryGetValue(chartDir, out uint[]? imageRelativeHashes));
            Assert.AreEqual(1, audioRelativeHashes!.Length);
            Assert.AreEqual(1, imageRelativeHashes!.Length);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// 親子双方に譜面があるとき集約キーは各祖先、自己所有キーは最寄りの譜面フォルダだけに属し、所有先の混同を検出する。
    /// </summary>
    /// <remarks>実ファイルシステムの列挙結果とメタデータを観測するため、一時フォルダ内の実入力を使用する。</remarks>
    [TestMethod]
    public void BuildFromRoots_AssignsAggregateOwnershipToAllAncestorChartDirectories_AndSelfOwnedToNearestChartDirectory()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartDirNearest_" + Guid.NewGuid().ToString("N"));
        string rootChartDir = Path.Combine(tempRoot, "rootchart");
        string nestedChartDir = Path.Combine(rootChartDir, "subchart");
        string nestedSoundDir = Path.Combine(nestedChartDir, "sound");
        Directory.CreateDirectory(nestedSoundDir);
        File.WriteAllText(Path.Combine(rootChartDir, "root.bms"), "#PLAYER 1");
        File.WriteAllText(Path.Combine(nestedChartDir, "sub.bmson"), "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Sub\",\"artist\":\"Artist\"},\"lines\":[{\"y\":0}]}");
        File.WriteAllText(Path.Combine(nestedSoundDir, "01.ogg"), "audio");
        try
        {
            ChartScanResult result = ChartDirectoryScanBuilder.BuildFromRoots([tempRoot]);

            Assert.IsTrue(result.AudioRelativePathHashesByChartDirectory.TryGetValue(rootChartDir, out uint[]? rootRelativeHashes));
            Assert.IsTrue(result.AudioRelativePathHashesByChartDirectory.TryGetValue(nestedChartDir, out uint[]? nestedRelativeHashes));
            CollectionAssert.Contains(rootRelativeHashes!, ChartResourceKeyHash.GetLookupHash(Path.Combine("subchart", "sound", "01")));
            CollectionAssert.Contains(nestedRelativeHashes!, ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "01")));
            CollectionAssert.Contains(result.SelfOwnedAudioRelativePathHashesByChartDirectory[nestedChartDir], ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "01")));
            Assert.AreEqual(0, result.SelfOwnedAudioRelativePathHashesByChartDirectory[rootChartDir].Length);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// 同名音声が直下と子フォルダにあるとき相対パスを区別し、画像も譜面基準のキーで引けることから同名衝突を検出する。
    /// </summary>
    /// <remarks>実ファイルシステムの列挙結果とメタデータを観測するため、一時フォルダ内の実入力を使用する。</remarks>
    [TestMethod]
    public void BuildFromRoots_PathAwareResourcesUseChartRelativeKeys()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartDirRelative_" + Guid.NewGuid().ToString("N"));
        string chartDir = Path.Combine(tempRoot, "song");
        string nestedAudioDir = Path.Combine(chartDir, "sound");
        string nestedImageDir = Path.Combine(chartDir, "clock");
        Directory.CreateDirectory(nestedAudioDir);
        Directory.CreateDirectory(nestedImageDir);
        File.WriteAllText(Path.Combine(chartDir, "chart.bms"), "#PLAYER 1");
        File.WriteAllText(Path.Combine(chartDir, "bgm1.wav"), "flat");
        File.WriteAllText(Path.Combine(nestedAudioDir, "bgm1.wav"), "nested");
        File.WriteAllText(Path.Combine(nestedImageDir, "00_001_00.bmp"), "image");
        try
        {
            ChartScanResult result = ChartDirectoryScanBuilder.BuildFromRoots([tempRoot]);

            Assert.IsTrue(result.AudioRelativePathHashesByChartDirectory.TryGetValue(chartDir, out uint[]? audioRelativeHashes));
            Assert.IsTrue(result.ImageRelativePathHashesByChartDirectory.TryGetValue(chartDir, out uint[]? imageRelativeHashes));

            uint flatRelativeHash = ChartResourceKeyHash.GetLookupHash("bgm1");
            uint nestedRelativeHash = ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "bgm1"));
            uint imageRelativeHash = ChartResourceKeyHash.GetLookupHash(Path.Combine("clock", "00_001_00"));

            CollectionAssert.AreEquivalent(new[] { flatRelativeHash, nestedRelativeHash }, audioRelativeHashes!);
            CollectionAssert.Contains(imageRelativeHashes!.ToList(), imageRelativeHash);
            Assert.AreNotEqual(flatRelativeHash, nestedRelativeHash);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// 直下の説明文だけを譜面フォルダの有無判定に使い、深い階層のfolderinfoと更新日時も保持して説明文との混同を検出する。
    /// </summary>
    /// <remarks>実ファイルシステムの列挙結果とメタデータを観測するため、一時フォルダ内の実入力を使用する。</remarks>
    [TestMethod]
    public void BuildFromRoots_TextGroupUsesOnlyDirectChartDirectoryTxtFiles()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartDirText_" + Guid.NewGuid().ToString("N"));
        string directChartDir = Path.Combine(tempRoot, "direct");
        string nestedChartDir = Path.Combine(tempRoot, "nested");
        Directory.CreateDirectory(directChartDir);
        Directory.CreateDirectory(Path.Combine(nestedChartDir, "docs"));
        string rootFolderInfo = Path.Combine(tempRoot, "folderinfo.txt");
        string nestedFolderInfo = Path.Combine(nestedChartDir, "docs", "folderinfo.txt");
        File.WriteAllText(Path.Combine(directChartDir, "chart.bms"), "#PLAYER 1");
        File.WriteAllText(Path.Combine(directChartDir, "readme.txt"), "text");
        File.WriteAllText(Path.Combine(nestedChartDir, "chart.bms"), "#PLAYER 1");
        File.WriteAllText(Path.Combine(nestedChartDir, "docs", "readme.txt"), "text");
        File.WriteAllText(rootFolderInfo, "#TITLE Root");
        File.WriteAllText(nestedFolderInfo, "#TITLE Nested");
        DateTime rootFolderInfoTimestamp = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(rootFolderInfo, rootFolderInfoTimestamp);
        try
        {
            ChartScanResult result = ChartDirectoryScanBuilder.BuildFromRoots([tempRoot]);

            CollectionAssert.Contains(result.ChartDirectoriesWithTextFiles.ToList(), directChartDir);
            CollectionAssert.DoesNotContain(result.ChartDirectoriesWithTextFiles.ToList(), nestedChartDir);
            CollectionAssert.Contains(result.FolderInfoFilePaths.ToList(), rootFolderInfo);
            CollectionAssert.Contains(result.FolderInfoFilePaths.ToList(), nestedFolderInfo);
            CollectionAssert.DoesNotContain(result.FolderInfoFilePaths.ToList(), Path.Combine(directChartDir, "readme.txt"));
            Assert.IsTrue(result.FolderInfoFileEntriesByPath.TryGetValue(rootFolderInfo, out RootFileEnumerationEntry? rootFolderInfoEntry));
            Assert.AreEqual(rootFolderInfoTimestamp, rootFolderInfoEntry!.LastWriteTimeUtc);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// 存在しない走査ルートは未完了の失敗理由と結果なしを返し、空の正常走査として扱う誤りを検出する。
    /// </summary>
    [TestMethod]
    public void TryBuildFromRoots_MissingRootReturnsIncompleteFailure()
    {
        string missingRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartDirMissing_" + Guid.NewGuid().ToString("N"));

        bool succeeded = ChartDirectoryScanBuilder.TryBuildFromRoots([missingRoot], out ChartScanResult result, out string failureReason);

        Assert.IsFalse(succeeded);
        Assert.IsNull(result);
        StringAssert.Contains(failureReason, "root_not_found:");
    }

    /// <summary>
    /// 長い実パスの譜面・音声・画像・説明文を走査結果へ揃え、通常長のパス制約による欠落を検出する。
    /// </summary>
    /// <remarks>実ファイルシステムの列挙結果とメタデータを観測するため、一時フォルダ内の実入力を使用する。</remarks>
    [TestMethod]
    public void TryBuildFromRoots_LongPathChartResourcesAndTextUseLongPathFileSystem()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartDirLong_" + Guid.NewGuid().ToString("N"));
        string chartDir = BuildLongDirectoryPath(tempRoot, "song");
        string audioDir = Path.Combine(chartDir, "sound");
        string imageDir = Path.Combine(chartDir, "image");
        string chartPath = Path.Combine(chartDir, "chart.bms");
        string audioPath = Path.Combine(audioDir, "hit.wav");
        string imagePath = Path.Combine(imageDir, "bg.png");
        string textPath = Path.Combine(chartDir, "readme.txt");
        LongPathFileSystem.CreateDirectory(audioDir);
        LongPathFileSystem.CreateDirectory(imageDir);
        WriteAllText(chartPath, "#PLAYER 1");
        WriteAllText(audioPath, "audio");
        WriteAllText(imagePath, "image");
        WriteAllText(textPath, "text");

        try
        {
            bool succeeded = ChartDirectoryScanBuilder.TryBuildFromRoots([tempRoot], out ChartScanResult result, out string failureReason);

            Assert.IsTrue(succeeded, failureReason);
            CollectionAssert.Contains(result.ChartFilePaths.ToList(), chartPath);
            CollectionAssert.Contains(result.ChartDirectories.ToList(), chartDir);
            CollectionAssert.Contains(result.ChartDirectoriesWithTextFiles.ToList(), chartDir);
            Assert.IsTrue(result.AudioRelativePathHashesByChartDirectory.TryGetValue(chartDir, out uint[]? audioHashes));
            Assert.IsTrue(result.ImageRelativePathHashesByChartDirectory.TryGetValue(chartDir, out uint[]? imageHashes));
            CollectionAssert.Contains(audioHashes!, ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "hit")));
            CollectionAssert.Contains(imageHashes!, ChartResourceKeyHash.GetLookupHash(Path.Combine("image", "bg")));
        }
        finally
        {
            if (LongPathFileSystem.DirectoryExists(tempRoot))
            {
                LongPathFileSystem.DeleteDirectory(tempRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// 列挙済み入力の譜面・説明文・folderinfo・親フォルダの時刻とサイズを引き継ぎ、再分類時のメタデータ欠落を検出する。
    /// </summary>
    [TestMethod]
    public void BuildFromGroupedPaths_PreservesTextAndFolderInfoEntries()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartDirGroupedText_" + Guid.NewGuid().ToString("N"));
        string chartDirectory = Path.Combine(tempRoot, "song");
        string chartPath = Path.Combine(chartDirectory, "chart.bms");
        string readmePath = Path.Combine(chartDirectory, "readme.txt");
        string folderInfoPath = Path.Combine(chartDirectory, "folderinfo.txt");
        string parentDirectory = tempRoot;
        DateTime chartTimestamp = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        DateTime readmeTimestamp = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        DateTime folderInfoTimestamp = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        DateTime directoryTimestamp = new(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc);

        var enumerationResult = new RootFileEnumerationResult { Success = true };
        enumerationResult.AddEntry(ChartDirectoryScanBuilder.ChartGroupName, new RootFileEnumerationEntry(chartPath, chartTimestamp, 789));
        enumerationResult.AddEntry(ChartDirectoryScanBuilder.TextGroupName, new RootFileEnumerationEntry(readmePath, readmeTimestamp, 123));
        enumerationResult.AddEntry(ChartDirectoryScanBuilder.TextGroupName, new RootFileEnumerationEntry(folderInfoPath, folderInfoTimestamp, 456));
        enumerationResult.AddEntry(RootFileEnumerationService.DirectoriesGroupName, new RootFileEnumerationEntry(parentDirectory + Path.DirectorySeparatorChar, directoryTimestamp));

        ChartScanResult result = ChartDirectoryScanBuilder.BuildFromGroupedPaths(enumerationResult);

        Assert.IsTrue(result.ChartFileEntriesByPath.TryGetValue(chartPath, out RootFileEnumerationEntry? chartEntry));
        RootFileEnumerationEntry loadedChartEntry = chartEntry!;
        Assert.AreEqual(chartTimestamp, loadedChartEntry.LastWriteTimeUtc);
        Assert.AreEqual((long?)789, loadedChartEntry.FileSize);
        CollectionAssert.Contains(result.ChartDirectoriesWithTextFiles.ToList(), chartDirectory);
        CollectionAssert.Contains(result.FolderInfoFilePaths.ToList(), folderInfoPath);
        Assert.IsTrue(result.TextFileEntriesByPath.TryGetValue(readmePath, out RootFileEnumerationEntry? readmeEntry));
        RootFileEnumerationEntry loadedReadmeEntry = readmeEntry!;
        Assert.AreEqual(readmeTimestamp, loadedReadmeEntry.LastWriteTimeUtc);
        Assert.AreEqual((long?)123, loadedReadmeEntry.FileSize);
        Assert.IsTrue(result.FolderInfoFileEntriesByPath.TryGetValue(folderInfoPath, out RootFileEnumerationEntry? folderInfoEntry));
        RootFileEnumerationEntry loadedFolderInfoEntry = folderInfoEntry!;
        Assert.AreEqual(folderInfoTimestamp, loadedFolderInfoEntry.LastWriteTimeUtc);
        Assert.AreEqual((long?)456, loadedFolderInfoEntry.FileSize);
        Assert.IsTrue(result.DirectoryEntriesByPath.TryGetValue(parentDirectory, out RootFileEnumerationEntry? directoryEntry));
        Assert.AreEqual(directoryTimestamp, directoryEntry!.LastWriteTimeUtc);
    }

    /// <summary>
    /// 説明文の列挙を明示的に省略しても譜面とリソースの群は残り、不要な説明文走査と必要群の欠落を検出する。
    /// </summary>
    [TestMethod]
    public void CreateEnumerationGroups_CanOmitTextGroup()
    {
        IReadOnlyList<RootFileEnumerationGroup> withText = ChartDirectoryScanBuilder.CreateEnumerationGroups(
            ChartDirectoryScanBuilder.ChartExtensions,
            includeTextFiles: true);
        IReadOnlyList<RootFileEnumerationGroup> withoutText = ChartDirectoryScanBuilder.CreateEnumerationGroups(
            ChartDirectoryScanBuilder.ChartExtensions,
            includeTextFiles: false);

        CollectionAssert.Contains(withText.Select(group => group.Name).ToList(), ChartDirectoryScanBuilder.TextGroupName);
        CollectionAssert.DoesNotContain(withoutText.Select(group => group.Name).ToList(), ChartDirectoryScanBuilder.TextGroupName);
        CollectionAssert.Contains(withoutText.Select(group => group.Name).ToList(), ChartDirectoryScanBuilder.ChartGroupName);
        CollectionAssert.Contains(withoutText.Select(group => group.Name).ToList(), ChartDirectoryScanBuilder.AudioGroupName);
        CollectionAssert.Contains(withoutText.Select(group => group.Name).ToList(), ChartDirectoryScanBuilder.ImageGroupName);
        CollectionAssert.Contains(withoutText.Select(group => group.Name).ToList(), ChartDirectoryScanBuilder.MovieGroupName);
    }

    /// <summary>
    /// ディレクトリメタデータの取得要求を有効・無効で切り替え、要求した群の欠落や不要な列挙を検出する。
    /// </summary>
    [TestMethod]
    public void CreateEnumerationGroups_CanIncludeDirectoryMetadataGroup()
    {
        IReadOnlyList<RootFileEnumerationGroup> withoutDirectories = ChartDirectoryScanBuilder.CreateEnumerationGroups(
            ChartDirectoryScanBuilder.ChartExtensions,
            includeDirectoryMetadata: false);
        IReadOnlyList<RootFileEnumerationGroup> withDirectories = ChartDirectoryScanBuilder.CreateEnumerationGroups(
            ChartDirectoryScanBuilder.ChartExtensions,
            includeDirectoryMetadata: true);

        CollectionAssert.DoesNotContain(withoutDirectories.Select(group => group.Name).ToList(), RootFileEnumerationService.DirectoriesGroupName);
        CollectionAssert.Contains(withDirectories.Select(group => group.Name).ToList(), RootFileEnumerationService.DirectoriesGroupName);
    }

    /// <summary>
    /// 一度だけ列挙できる説明文入力から説明文とfolderinfoの両方を保持し、二度目の列挙に依存する欠落を検出する。
    /// </summary>
    [TestMethod]
    public void BuildFromAbsolutePaths_PreservesFolderInfoPathsFromSingleUseTextEnumerable()
    {
        string chartDirectory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartDirTextOnce_" + Guid.NewGuid().ToString("N"));
        string folderInfoPath = Path.Combine(chartDirectory, "folderinfo.txt");
        string readmePath = Path.Combine(chartDirectory, "readme.txt");
        string chartPath = Path.Combine(chartDirectory, "chart.bms");

        ChartScanResult result = ChartDirectoryScanBuilder.BuildFromAbsolutePaths(
            [chartPath],
            [],
            [],
            [],
            new SingleUseEnumerable<string>([folderInfoPath, readmePath]));

        CollectionAssert.Contains(result.ChartDirectoriesWithTextFiles.ToList(), chartDirectory);
        CollectionAssert.Contains(result.FolderInfoFilePaths.ToList(), folderInfoPath);
        CollectionAssert.DoesNotContain(result.FolderInfoFilePaths.ToList(), readmePath);
        Assert.IsTrue(result.TextFileEntriesByPath.ContainsKey(folderInfoPath));
        Assert.IsTrue(result.FolderInfoFileEntriesByPath.ContainsKey(folderInfoPath));
    }

    private sealed class SingleUseEnumerable<T>(IEnumerable<T> values) : IEnumerable<T>
    {
        private bool enumerated;

        public IEnumerator<T> GetEnumerator()
        {
            if (enumerated)
            {
                return Enumerable.Empty<T>().GetEnumerator();
            }

            enumerated = true;
            return (values ?? []).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private static string BuildLongDirectoryPath(string tempDirectoryPath, string leafName)
    {
        string path = tempDirectoryPath;
        for (int i = 0; path.Length < 285; i++)
        {
            path = Path.Combine(path, "segment_" + i.ToString("00") + "_" + new string('a', 32));
        }
        return Path.Combine(path, leafName);
    }

    private static void WriteAllText(string path, string contents)
    {
        using FileStream stream = LongPathFileSystem.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(contents);
    }
}
