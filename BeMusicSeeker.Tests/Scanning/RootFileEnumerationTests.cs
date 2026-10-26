using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RootFileEnumerationTests
{
    /// <summary>要求・復号と共通のsource-root正規化で照会し、絶対drive rootと通常/UNCの従来canonicalを保持する。</summary>
    [DataTestMethod]
    [DataRow(@"C:\", @"C:\")]
    [DataRow("C:/", @"C:\")]
    [DataRow(@"C:\Songs", @"C:\Songs")]
    [DataRow(@"C:\Songs\", @"C:\Songs")]
    [DataRow(@"\\server\share", @"\\server\share")]
    [DataRow(@"\\server\share\", @"\\server\share")]
    public void SourceRootLookup_PreservesDriveRootAndExistingCanonicalKeys(string request, string canonical)
    {
        var expected = new EverythingNative.BridgeSourceRootEntryResult { RootPath = canonical };
        var result = new EverythingNative.BridgeSourceRootScanResult();
        result.Entries.Add(canonical, expected);
        Assert.IsTrue(result.TryGetEntry(request, out EverythingNative.BridgeSourceRootEntryResult actual));
        Assert.AreSame(expected, actual);
        Assert.AreEqual(canonical, actual.RootPath);
    }

    /// <summary>
    /// 実ファイルのパス・更新日時・サイズを同じ列挙で返し、パス互換入口とメタデータ入口の不一致を検出する。
    /// </summary>
    [TestMethod]
    public void FastEnumerator_ReturnsFileMetadataEntriesAndPathCompatibility()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnum_" + Guid.NewGuid().ToString("N"));
        string chartPath = Path.Combine(tempRoot, "song.bms");
        DateTime lastWriteTimeUtc = new(2026, 6, 5, 1, 2, 3, DateTimeKind.Utc);
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(chartPath, "#PLAYER 1");
        File.SetLastWriteTimeUtc(chartPath, lastWriteTimeUtc);

        try
        {
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles(
                [tempRoot],
                [new RootFileEnumerationGroup(ChartDirectoryScanBuilder.ChartGroupName, [".bms"])]);

            Assert.IsTrue(result.Success);
            CollectionAssert.Contains(result.GetPaths(ChartDirectoryScanBuilder.ChartGroupName).ToList(), chartPath);
            RootFileEnumerationEntry entry = result.GetEntry(ChartDirectoryScanBuilder.ChartGroupName, chartPath);
            Assert.IsNotNull(entry);
            Assert.AreEqual(chartPath, entry.Path);
            Assert.IsTrue(entry.LastWriteTimeUtc.HasValue);
            Assert.AreEqual(
                ToUnixSeconds(File.GetLastWriteTimeUtc(chartPath)),
                entry.LastWriteTimeUnixSeconds);
            Assert.AreEqual(new FileInfo(chartPath).Length, entry.FileSize);
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
    /// 長い実パスから譜面・説明文・LR2フォルダファイル・ディレクトリを拾い、パス長による一部群の欠落を検出する。
    /// </summary>
    [TestMethod]
    public void FastEnumerator_ReturnsLongPathChartTextLr2FolderAndDirectoryEntries()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumLong_" + Guid.NewGuid().ToString("N"));
        string longDirectoryPath = BuildLongDirectoryPath(tempRoot, "charts");
        string chartPath = Path.Combine(longDirectoryPath, "long-chart.bms");
        string textPath = Path.Combine(longDirectoryPath, "folderinfo.txt");
        string lr2FolderPath = Path.Combine(longDirectoryPath, "custom.lr2folder");
        DateTime lastWriteTimeUtc = new(2026, 6, 24, 1, 2, 3, DateTimeKind.Utc);
        LongPathFileSystem.CreateDirectory(longDirectoryPath);
        WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE LongRoot\r\n");
        WriteAllText(textPath, "#TITLE Long Folder\r\n");
        WriteAllText(lr2FolderPath, "#TITLE Long Custom\r\n");
        File.SetLastWriteTimeUtc(LongPathFileSystem.ToExtendedPath(chartPath), lastWriteTimeUtc);

        try
        {
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles(
                [tempRoot],
                [
                    new RootFileEnumerationGroup(ChartDirectoryScanBuilder.ChartGroupName, [".bms"]),
                    new RootFileEnumerationGroup(ChartDirectoryScanBuilder.TextGroupName, ChartDirectoryScanBuilder.TextExtensions),
                    new RootFileEnumerationGroup("lr2folder", [".lr2folder"]),
                    new RootFileEnumerationGroup(RootFileEnumerationService.DirectoriesGroupName, [], includeDirectories: true)
                ]);

            Assert.IsTrue(result.Success);
            Assert.IsTrue(result.IsComplete);
            CollectionAssert.Contains(result.GetPaths(ChartDirectoryScanBuilder.ChartGroupName).ToList(), chartPath);
            CollectionAssert.Contains(result.GetPaths(ChartDirectoryScanBuilder.TextGroupName).ToList(), textPath);
            CollectionAssert.Contains(result.GetPaths("lr2folder").ToList(), lr2FolderPath);
            CollectionAssert.Contains(result.GetPaths(RootFileEnumerationService.DirectoriesGroupName).ToList(), longDirectoryPath);
            RootFileEnumerationEntry entry = result.GetEntry(ChartDirectoryScanBuilder.ChartGroupName, chartPath);
            Assert.IsNotNull(entry);
            Assert.AreEqual(chartPath, entry.Path);
            Assert.AreEqual(ToUnixSeconds(lastWriteTimeUtc), entry.LastWriteTimeUnixSeconds);
            Assert.IsTrue(entry.FileSize > 0);
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
    /// 高速・上限付き列挙ともに初めから存在しないルートを成功や権威ある完了にせず、完全な空結果へ置き換える誤りを検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OsEnumerator_MissingRootIsIncompleteAndNotSuccessful(bool bounded)
    {
        string missingRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumMissing_" + Guid.NewGuid().ToString("N"));

        IRootFileEnumerator enumerator = bounded
            ? new BoundedSourceSurfaceEnumerator(maxVisitedFileSystemEntryCount: 100)
            : new FastRootFileEnumerator();
        RootFileEnumerationResult result = enumerator.EnumerateFiles(
            [missingRoot],
            [new RootFileEnumerationGroup(ChartDirectoryScanBuilder.ChartGroupName, [".bms"])]);

        Assert.IsFalse(result.Success);
        Assert.IsFalse(result.IsComplete);
        StringAssert.Contains(result.IncompleteReason, "root_missing_or_unavailable:");
        StringAssert.Contains(result.IncompleteReason, missingRoot);
        Assert.IsFalse(result.ScanLimitExceeded);
        Assert.IsFalse(RootFileEnumerationService.IsAuthoritativeComplete(result));
    }

    /// <summary>
    /// 存在確認後に所有ルートを削除して実列挙を失敗させ、原因の段階・パスと不完全性を保ったまま独立ルートの譜面・音声を返すことを確認する。
    /// </summary>
    [TestMethod]
    public void BoundedEnumerator_DisappearingRootPreservesIndependentPathsAndReportsIncomplete()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BoundedRootDisappearance_" + Guid.NewGuid().ToString("N"));
        string survivingRoot = Path.Combine(tempRoot, "Surviving");
        string disappearingRoot = Path.Combine(tempRoot, "Gone");
        string chartPath = Path.Combine(survivingRoot, "chart.bms");
        string audioPath = Path.Combine(survivingRoot, "00.wav");
        try
        {
            Directory.CreateDirectory(survivingRoot);
            Directory.CreateDirectory(disappearingRoot);
            File.WriteAllText(chartPath, "#PLAYER 1");
            File.WriteAllText(audioPath, "audio");
            RootFileEnumerationResult result = new BoundedSourceSurfaceEnumerator(maxVisitedFileSystemEntryCount: 100).EnumerateFiles(
                EnumerateRoots(),
                [
                    new RootFileEnumerationGroup(ChartDirectoryScanBuilder.ChartGroupName, [".bms"]),
                    new RootFileEnumerationGroup(ChartDirectoryScanBuilder.AudioGroupName, [".wav"])
                ]);

            Assert.IsFalse(result.Success);
            Assert.IsFalse(result.IsComplete);
            Assert.IsFalse(result.ScanLimitExceeded);
            Assert.IsFalse(RootFileEnumerationService.IsAuthoritativeComplete(result));
            Assert.IsTrue(result.IncompleteReasons.Any(reason => reason.Contains("file_enumeration_failed:") && reason.Contains(disappearingRoot)));
            Assert.IsTrue(result.IncompleteReasons.Any(reason => reason.Contains("directory_enumeration_failed:") && reason.Contains(disappearingRoot)));
            StringAssert.Contains(RootFileEnumerationService.GetNonAuthoritativeReason(result), disappearingRoot);
            CollectionAssert.Contains(result.GetPaths(ChartDirectoryScanBuilder.ChartGroupName).ToList(), chartPath);
            CollectionAssert.Contains(result.GetPaths(ChartDirectoryScanBuilder.AudioGroupName).ToList(), audioPath);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }

        IEnumerable<string> EnumerateRoots()
        {
            yield return disappearingRoot;
            // NormalizeExecutionRootsが直前のルートを確認した次のMoveNextで削除し、実走査前の外部消失順序を固定する。
            Directory.Delete(disappearingRoot, recursive: true);
            yield return survivingRoot;
        }
    }

    /// <summary>
    /// Everythingの空結果を再試行する要求だけが高速列挙の結果を採用し、明示された再試行方針の無視を検出する。
    /// </summary>
    [TestMethod]
    public void SelectFallbackResult_RetriesFastForEmptyEverythingResult()
    {
        var everythingResult = new RootFileEnumerationResult
        {
            Success = true,
            BackendName = EverythingNative.GroupedEnumerationBackendName,
            TotalFileCount = 0
        };
        everythingResult.InitializeGroup("lr2folder");
        var fastResult = new RootFileEnumerationResult
        {
            Success = true,
            BackendName = "fast",
            TotalFileCount = 1
        };
        fastResult.AddEntry("lr2folder", new RootFileEnumerationEntry(@"C:\temp\table.lr2folder"));

        RootFileEnumerationResult selected = RootFileEnumerationService.SelectFallbackResult(
            everythingResult,
            () => fastResult,
            retryEmptyEverythingResultWithFastEnumerator: true);

        Assert.AreSame(fastResult, selected);
        CollectionAssert.Contains(selected.GetPaths("lr2folder").ToList(), @"C:\temp\table.lr2folder");
        Assert.AreSame(
            everythingResult,
            RootFileEnumerationService.SelectFallbackResult(
                everythingResult,
                () => fastResult,
                retryEmptyEverythingResultWithFastEnumerator: false));
    }

    /// <summary>
    /// 走査上限到達の結果を成功・完了フラグだけで権威ある全件結果とせず、不完全な走査による削除判定を検出する。
    /// </summary>
    [TestMethod]
    public void RootFileEnumerationResult_WithScanLimitExceededIsNotAuthoritativeComplete()
    {
        var result = new RootFileEnumerationResult
        {
            Success = true,
            IsComplete = true,
            ScanLimitExceeded = true
        };

        Assert.IsFalse(RootFileEnumerationService.IsAuthoritativeComplete(result));
        Assert.AreEqual("scan_limit_exceeded", RootFileEnumerationService.GetNonAuthoritativeReason(result));
    }

    /// <summary>
    /// ローカル時刻の入力をUTCとUnix秒へ変換して保存用時刻を揃え、タイムゾーン分のずれを検出する。
    /// </summary>
    [TestMethod]
    public void RootFileEnumerationEntry_NormalizesLocalTimestampToUtcUnixSeconds()
    {
        DateTime utc = new(2026, 6, 5, 1, 2, 3, DateTimeKind.Utc);
        DateTime local = utc.ToLocalTime();

        var entry = new RootFileEnumerationEntry(@"D:\song.bms", local);

        Assert.AreEqual(utc, entry.LastWriteTimeUtc);
        Assert.AreEqual(ToUnixSeconds(utc), entry.LastWriteTimeUnixSeconds);
    }

    /// <summary>
    /// ドライブのルート末尾区切りを維持して絶対パスのまま子孫を照合し、相対パス化する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void TrimTrailingDirectorySeparators_PreservesDriveRootAsAbsolutePath()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        string trimmed = LongPathFileSystem.TrimTrailingDirectorySeparators(root);

        Assert.AreEqual(root.TrimEnd(Path.AltDirectorySeparatorChar), trimmed);
        Assert.AreNotEqual(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), trimmed);
        Assert.IsTrue(LongPathFileSystem.IsSameOrDescendantNormalizedDirectoryPath(Path.Combine(trimmed, "child"), trimmed));
    }

    /// <summary>
    /// 大文字小文字だけが異なる譜面パスを別件として保持し、辞書と走査結果で片方を失う誤りを検出する。
    /// </summary>
    [TestMethod]
    public void RootFileEnumerationResult_KeepsCaseOnlyChartPathsDistinct()
    {
        string upperPath = @"D:\BMS\Pack\Song\Chart.bms";
        string lowerPath = @"D:\BMS\Pack\Song\chart.bms";
        var upperEntry = new RootFileEnumerationEntry(upperPath);
        var lowerEntry = new RootFileEnumerationEntry(lowerPath);
        var enumeration = new RootFileEnumerationResult();
        enumeration.InitializeGroup(ChartDirectoryScanBuilder.ChartGroupName);

        enumeration.AddEntry(ChartDirectoryScanBuilder.ChartGroupName, upperEntry);
        enumeration.AddEntry(ChartDirectoryScanBuilder.ChartGroupName, lowerEntry);

        Assert.AreEqual(2, enumeration.GetPaths(ChartDirectoryScanBuilder.ChartGroupName).Count);
        Assert.AreSame(upperEntry, enumeration.GetEntry(ChartDirectoryScanBuilder.ChartGroupName, upperPath));
        Assert.AreSame(lowerEntry, enumeration.GetEntry(ChartDirectoryScanBuilder.ChartGroupName, lowerPath));

        ChartScanResult scanResult = ChartDirectoryScanBuilder.BuildFromAbsolutePaths(
            [upperEntry, lowerEntry],
            [],
            [],
            [],
            []);
        Assert.AreEqual(2, scanResult.ChartFilePaths.Count);
        Assert.IsTrue(scanResult.ChartFileEntriesByPath.ContainsKey(upperPath));
        Assert.IsTrue(scanResult.ChartFileEntriesByPath.ContainsKey(lowerPath));
    }

    /// <summary>
    /// 実ルートと子ディレクトリの更新日時を列挙結果へ渡し、フォルダのメタデータ欠落を検出する。
    /// </summary>
    [TestMethod]
    public void FastEnumerator_ReturnsDirectoryMetadataEntries()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumDirs_" + Guid.NewGuid().ToString("N"));
        string nestedDirectory = Path.Combine(tempRoot, "Pack", "Song");
        DateTime rootWriteTimeUtc = new(2026, 6, 5, 3, 0, 0, DateTimeKind.Utc);
        DateTime nestedWriteTimeUtc = new(2026, 6, 5, 4, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(nestedDirectory);
        Directory.SetLastWriteTimeUtc(tempRoot, rootWriteTimeUtc);
        Directory.SetLastWriteTimeUtc(nestedDirectory, nestedWriteTimeUtc);

        try
        {
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles(
                [tempRoot],
                [new RootFileEnumerationGroup(RootFileEnumerationService.DirectoriesGroupName, [], includeDirectories: true)]);

            Assert.IsTrue(result.Success);
            RootFileEnumerationEntry rootEntry = result.GetEntry(RootFileEnumerationService.DirectoriesGroupName, tempRoot);
            RootFileEnumerationEntry nestedEntry = result.GetEntry(RootFileEnumerationService.DirectoriesGroupName, nestedDirectory);
            Assert.IsNotNull(rootEntry);
            Assert.IsNotNull(nestedEntry);
            Assert.AreEqual(ToUnixSeconds(rootWriteTimeUtc), rootEntry.LastWriteTimeUnixSeconds);
            Assert.AreEqual(ToUnixSeconds(nestedWriteTimeUtc), nestedEntry.LastWriteTimeUnixSeconds);
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
    /// Everythingのディレクトリ検索には末尾区切りを除いたルートを渡し、外部検索の範囲が変わる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void BuildDirectoriesQuery_UsesRootPathWithoutTrailingSeparator()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumDirsQuery_" + Guid.NewGuid().ToString("N"));

        string query = EverythingNative.BuildDirectoriesQuery([root + Path.DirectorySeparatorChar]);

        StringAssert.Contains(query, "folder:");
        StringAssert.Contains(query, "path:\"" + root.Replace("\"", "\"\"") + "\"");
        Assert.IsFalse(query.Contains("path:\"" + (root + Path.DirectorySeparatorChar).Replace("\"", "\"\"") + "\""));
    }

    /// <summary>
    /// Everythingのファイル検索では除外ディレクトリを末尾区切り付きの部分木として表し、同接頭辞の兄弟まで除外する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void BuildFilesQuery_IncludesExcludedDirectoriesWithTrailingSeparator()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumFilesQuery_" + Guid.NewGuid().ToString("N"));
        string excluded = Path.Combine(root, "Output", "Table");

        string query = EverythingNative.BuildFilesQuery([root], ["lr2folder"], [excluded]);

        StringAssert.Contains(query, "file:");
        StringAssert.Contains(query, "!<path:\"" + (excluded + Path.DirectorySeparatorChar).Replace("\"", "\"\"") + "\">");
    }

    /// <summary>
    /// 設定された実部分木だけを除外し、同接頭辞の兄弟と別ルートを残すことで走査範囲の過剰除外を検出する。
    /// </summary>
    [TestMethod]
    public void FastEnumerator_ExcludesConfiguredSubtreeAndKeepsPrefixSibling()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumExclude_" + Guid.NewGuid().ToString("N"));
        string excludedDirectory = Path.Combine(tempRoot, "Output", "Table");
        string prefixSiblingDirectory = Path.Combine(tempRoot, "Output", "TableOther");
        string externalPath = Path.Combine(tempRoot, "External.lr2folder");
        string excludedPath = Path.Combine(excludedDirectory, "Managed.lr2folder");
        string siblingPath = Path.Combine(prefixSiblingDirectory, "Sibling.lr2folder");
        Directory.CreateDirectory(excludedDirectory);
        Directory.CreateDirectory(prefixSiblingDirectory);
        File.WriteAllText(externalPath, "#TITLE External");
        File.WriteAllText(excludedPath, "#TITLE Managed");
        File.WriteAllText(siblingPath, "#TITLE Sibling");

        try
        {
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles(
                [tempRoot],
                [new RootFileEnumerationGroup("lr2folder", [".lr2folder"], excludedDirectories: [excludedDirectory])]);

            Assert.IsTrue(result.Success);
            CollectionAssert.Contains(result.GetPaths("lr2folder").ToList(), externalPath);
            CollectionAssert.Contains(result.GetPaths("lr2folder").ToList(), siblingPath);
            CollectionAssert.DoesNotContain(result.GetPaths("lr2folder").ToList(), excludedPath);
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
    /// 走査ルート自体が除外対象ならディレクトリも返さず、除外設定を部分的に無視する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void FastEnumerator_ExcludedRootReturnsNoDirectoryEntries()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumExcludeRoot_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "Nested"));

        try
        {
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles(
                [tempRoot],
                [new RootFileEnumerationGroup(RootFileEnumerationService.DirectoriesGroupName, [], includeDirectories: true, excludedDirectories: [tempRoot])]);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(0, result.GetPaths(RootFileEnumerationService.DirectoriesGroupName).Count);
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
    /// エクスポート・DLL不足をブリッジ契約失敗として分類し、検索失敗や空結果との混同を検出する。
    /// </summary>
    [TestMethod]
    public void RootFileEnumerationService_ClassifiesBridgeContractFailures()
    {
        Assert.IsTrue(RootFileEnumerationService.IsBridgeContractFailure("bridge_grouped_enumeration_export_missing"));
        Assert.IsTrue(RootFileEnumerationService.IsBridgeContractFailure("bridge_dll_not_found:D:\\app\\native\\EverythingBridge_x64.dll"));
        Assert.IsFalse(RootFileEnumerationService.IsBridgeContractFailure("bridge_grouped_query_failed:2"));
        Assert.IsFalse(RootFileEnumerationService.IsBridgeContractFailure("empty_results_with_roots"));
    }

    /// <summary>
    /// 説明文・LR2フォルダファイル・ディレクトリの実時刻とファイルサイズを同じ結果に揃え、群ごとの情報欠落を検出する。
    /// </summary>
    [TestMethod]
    public void FastEnumerator_ReturnsUnifiedMetadataSurfaceForTextLr2FolderAndDirectories()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumUnified_" + Guid.NewGuid().ToString("N"));
        string packDirectory = Path.Combine(tempRoot, "Pack");
        string folderInfoPath = Path.Combine(packDirectory, "folderinfo.txt");
        string lr2FolderPath = Path.Combine(tempRoot, "Custom.lr2folder");
        DateTime folderInfoTimeUtc = new(2026, 6, 5, 5, 0, 0, DateTimeKind.Utc);
        DateTime lr2FolderTimeUtc = new(2026, 6, 5, 6, 0, 0, DateTimeKind.Utc);
        DateTime directoryTimeUtc = new(2026, 6, 5, 7, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(packDirectory);
        File.WriteAllText(folderInfoPath, "#TITLE Pack");
        File.WriteAllText(lr2FolderPath, "#TITLE Custom");
        File.SetLastWriteTimeUtc(folderInfoPath, folderInfoTimeUtc);
        File.SetLastWriteTimeUtc(lr2FolderPath, lr2FolderTimeUtc);
        Directory.SetLastWriteTimeUtc(packDirectory, directoryTimeUtc);

        try
        {
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles(
                [tempRoot],
                [
                    new RootFileEnumerationGroup(ChartDirectoryScanBuilder.TextGroupName, ChartDirectoryScanBuilder.TextExtensions),
                    new RootFileEnumerationGroup("lr2folder", [".lr2folder"]),
                    new RootFileEnumerationGroup(RootFileEnumerationService.DirectoriesGroupName, [], includeDirectories: true)
                ]);

            Assert.IsTrue(result.Success);
            RootFileEnumerationEntry folderInfoEntry = result.GetEntry(ChartDirectoryScanBuilder.TextGroupName, folderInfoPath);
            RootFileEnumerationEntry lr2FolderEntry = result.GetEntry("lr2folder", lr2FolderPath);
            RootFileEnumerationEntry directoryEntry = result.GetEntry(RootFileEnumerationService.DirectoriesGroupName, packDirectory);
            Assert.IsNotNull(folderInfoEntry);
            Assert.IsNotNull(lr2FolderEntry);
            Assert.IsNotNull(directoryEntry);
            Assert.AreEqual(ToUnixSeconds(folderInfoTimeUtc), folderInfoEntry.LastWriteTimeUnixSeconds);
            Assert.AreEqual(ToUnixSeconds(lr2FolderTimeUtc), lr2FolderEntry.LastWriteTimeUnixSeconds);
            Assert.AreEqual(ToUnixSeconds(directoryTimeUtc), directoryEntry.LastWriteTimeUnixSeconds);
            Assert.IsTrue(folderInfoEntry.FileSize > 0);
            Assert.IsTrue(lr2FolderEntry.FileSize > 0);
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
    /// 要求したディレクトリ情報を高速走査へ接続し、実行していないnative検索や問い合わせ時間の捏造を検出する。
    /// </summary>
    [TestMethod]
    public void FastDirectoryFileScanner_ReturnsDirectorySurfaceWhenRequested()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FastScannerDirs_" + Guid.NewGuid().ToString("N"));
        string nestedDirectory = Path.Combine(tempRoot, "Pack", "Song");
        string chartPath = Path.Combine(nestedDirectory, "chart.bms");
        DateTime rootWriteTimeUtc = new(2026, 6, 7, 1, 0, 0, DateTimeKind.Utc);
        DateTime nestedWriteTimeUtc = new(2026, 6, 7, 2, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(chartPath, "#TITLE Test");
        Directory.SetLastWriteTimeUtc(tempRoot, rootWriteTimeUtc);
        Directory.SetLastWriteTimeUtc(nestedDirectory, nestedWriteTimeUtc);

        try
        {
            ChartScanExecutionResult result = new FastDirectoryFileScanner().Scan(
                [tempRoot],
                ChartDirectoryScanBuilder.ChartExtensions,
                includeTextSurface: true,
                includeDirectorySurface: true);

            Assert.IsTrue(result.Success);
            Assert.IsNull(result.ScanSource);
            Assert.IsTrue(result.DirectoryQueryHitCount >= 2UL);
            Assert.IsFalse(result.NativeBridgeUsed);
            Assert.AreEqual(0L, result.NativeBridgeMs);
            Assert.AreEqual(0L, result.DirectoryQueryMs);
            CollectionAssert.Contains(result.Result.ChartFilePaths.ToList(), chartPath);
            Assert.IsTrue(result.Result.DirectoryEntriesByPath.TryGetValue(tempRoot, out RootFileEnumerationEntry? rootEntry));
            Assert.IsTrue(result.Result.DirectoryEntriesByPath.TryGetValue(nestedDirectory, out RootFileEnumerationEntry? nestedEntry));
            Assert.AreEqual(ToUnixSeconds(rootWriteTimeUtc), rootEntry!.LastWriteTimeUnixSeconds);
            Assert.AreEqual(ToUnixSeconds(nestedWriteTimeUtc), nestedEntry!.LastWriteTimeUnixSeconds);
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
    /// 高速列挙と上限付き列挙では実列挙時間だけを測り、ディレクトリ問い合わせ時間と取り違える誤りを検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OsEnumerator_MeasuresEnumerationWithoutInventingDirectoryQuery(bool bounded)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_EnumerationTiming_" + Guid.NewGuid().ToString("N"));
        string nestedDirectory = Path.Combine(tempRoot, "Song");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(Path.Combine(nestedDirectory, "chart.bms"), "#TITLE Timing");
        try
        {
            IRootFileEnumerator enumerator = bounded
                ? new BoundedSourceSurfaceEnumerator(maxVisitedFileSystemEntryCount: 100)
                : new FastRootFileEnumerator();
            RootFileEnumerationResult enumeration = enumerator.EnumerateFiles(
                [tempRoot],
                [new RootFileEnumerationGroup(RootFileEnumerationService.DirectoriesGroupName, [], includeDirectories: true)]);

            Assert.IsTrue(RootFileEnumerationService.IsAuthoritativeComplete(enumeration));
            CollectionAssert.Contains(enumeration.GetPaths(RootFileEnumerationService.DirectoriesGroupName).ToList(), nestedDirectory);
            Assert.IsTrue(enumeration.MeasuredEnumerationMs.HasValue);
            Assert.IsTrue(enumeration.MeasuredEnumerationMs >= 0L);
            Assert.IsNull(enumeration.GetMeasuredQueryMs(RootFileEnumerationService.DirectoriesGroupName));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// 高速・上限付き・Everythingの測定入力を引き継ぎ、問い合わせ未測定とゼロを区別してバックエンド情報の混同を検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow("fast", null, false)]
    [DataRow("bounded_fast_source_surface", null, false)]
    [DataRow(EverythingNative.GroupedEnumerationBackendName, null, true)]
    [DataRow(EverythingNative.GroupedEnumerationBackendName, 0, true)]
    [DataRow(EverythingNative.GroupedEnumerationBackendName, 7, true)]
    public void ChartScannerResult_PreservesBackendAndMeasuredQueryIntervals(string backend, int? queryMs, bool nativeBridgeUsed)
    {
        var enumeration = new RootFileEnumerationResult
        {
            Success = true,
            BackendName = backend,
            EnumerationMs = 13L
        };
        enumeration.InitializeGroup(RootFileEnumerationService.DirectoriesGroupName);
        enumeration.AddEntry(RootFileEnumerationService.DirectoriesGroupName, new RootFileEnumerationEntry(@"C:\BMS\Song"));
        if (queryMs.HasValue)
        {
            enumeration.QueryMsByGroup[RootFileEnumerationService.DirectoriesGroupName] = queryMs.Value;
        }

        ChartScanExecutionResult result = ChartFileScannerResultBuilder.Build(enumeration);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(nativeBridgeUsed, result.NativeBridgeUsed);
        Assert.AreEqual(nativeBridgeUsed ? 13L : 0L, result.NativeBridgeMs);
        Assert.AreEqual(queryMs.HasValue ? (long?)queryMs.Value : null,
            enumeration.GetMeasuredQueryMs(RootFileEnumerationService.DirectoriesGroupName));
        Assert.AreEqual((long)queryMs.GetValueOrDefault(), result.DirectoryQueryMs);
        Assert.AreEqual(13L, enumeration.MeasuredEnumerationMs);
        Assert.IsTrue(result.Result.DirectoryEntriesByPath.ContainsKey(@"C:\BMS\Song"));
    }

    /// <summary>
    /// 重なる親子ルートから各譜面のパスを重複なく返し、結果集合の欠落や重複を検出する。
    /// </summary>
    [TestMethod]
    public void FastEnumerator_CollapsesDescendantExecutionRoots()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumCollapse_" + Guid.NewGuid().ToString("N"));
        string nestedDirectory = Path.Combine(tempRoot, "Output", "Table");
        string rootChartPath = Path.Combine(tempRoot, "root.bms");
        string nestedChartPath = Path.Combine(nestedDirectory, "nested.bms");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(rootChartPath, "#PLAYER 1");
        File.WriteAllText(nestedChartPath, "#PLAYER 1");

        try
        {
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles(
                [nestedDirectory, tempRoot, Path.Combine(tempRoot, "Output")],
                [new RootFileEnumerationGroup(ChartDirectoryScanBuilder.ChartGroupName, [".bms"])]);

            Assert.IsTrue(result.Success);
            CollectionAssert.Contains(result.GetPaths(ChartDirectoryScanBuilder.ChartGroupName).ToList(), rootChartPath);
            CollectionAssert.Contains(result.GetPaths(ChartDirectoryScanBuilder.ChartGroupName).ToList(), nestedChartPath);
            Assert.AreEqual(2, result.GetPaths(ChartDirectoryScanBuilder.ChartGroupName).Count);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static int ToUnixSeconds(DateTime timestampUtc)
    {
        DateTime utc = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime();
        return (int)Math.Floor((utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
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
