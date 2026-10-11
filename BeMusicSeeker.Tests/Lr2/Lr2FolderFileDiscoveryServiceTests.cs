using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2FolderFileDiscoveryServiceTests
{
    /// <summary>
    /// 実在するLR2のCustomFolderだけを組込み探索元へ含め、正常な外部フォルダの探索漏れを検出します。
    /// </summary>
    /// <remarks>探索元の存在確認を観測するため専用の実フォルダを使います。</remarks>
    [TestMethod]
    public void CreateBuiltinFolderSourceDirectories_IncludesPhysicalLr2CustomFolder()
    {
        using var scope = TestDirectoryScope.Create();
        string lr2Root = Path.Combine(scope.DirectoryPath, "LR2");
        string customFolder = Path.Combine(lr2Root, "LR2files", "CustomFolder");
        Directory.CreateDirectory(customFolder);

        List<string> directories = Lr2FolderFileDiscoveryService.CreateBuiltinFolderSourceDirectories(lr2Root);

        CollectionAssert.AreEqual(new[] { Path.GetFullPath(customFolder) }, directories);
    }

    /// <summary>
    /// LR2ルートだけが実在する場合は未存在のCustomFolderを探索元へ含めず、存在確認を飛ばす誤りを検出します。
    /// </summary>
    /// <remarks>探索元の存在確認を観測するため専用の実フォルダを使います。</remarks>
    [TestMethod]
    public void CreateBuiltinFolderSourceDirectories_IgnoresMissingLr2CustomFolder()
    {
        using var scope = TestDirectoryScope.Create();
        string lr2Root = Path.Combine(scope.DirectoryPath, "LR2");
        Directory.CreateDirectory(lr2Root);

        List<string> directories = Lr2FolderFileDiscoveryService.CreateBuiltinFolderSourceDirectories(lr2Root);

        Assert.AreEqual(0, directories.Count);
    }

    /// <summary>
    /// 組込み表示と新曲表示を無効にしてもコース定義は残し、無効な表示の混入とコースの誤除外を検出します。
    /// </summary>
    [TestMethod]
    public void ShouldIncludeCustomFolderFile_ExcludesDisabledBuiltinsButKeepsCourses()
    {
        string lr2Root = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileDiscoveryServiceTests), "LR2");
        string customFolder = Path.Combine(lr2Root, "LR2files", "CustomFolder");
        var settings = new Lr2BuiltinCustomFolderSettings(0, titleFlashHours: 24, includeNewSongFolder: false);

        Assert.IsFalse(settings.ShouldIncludeCustomFolderFile(
            Path.Combine(customFolder, "favorite.lr2folder"),
            lr2Root));
        Assert.IsFalse(settings.ShouldIncludeCustomFolderFile(
            Path.Combine(customFolder, "RANDOM", "random.lr2folder"),
            lr2Root));
        Assert.IsFalse(settings.ShouldIncludeCustomFolderFile(
            Path.Combine(customFolder, "newsong.lr2folder"),
            lr2Root));
        Assert.IsTrue(settings.ShouldIncludeCustomFolderFile(
            Path.Combine(customFolder, "course1.lr2folder"),
            lr2Root));
    }

    /// <summary>
    /// 実在するBMS・通常出力・組込み元を探索し、未存在のルート出力先を追加する誤りを検出します。
    /// </summary>
    /// <remarks>探索元の存在確認を観測するため専用の実フォルダを使います。</remarks>
    [TestMethod]
    public void CreateDiscoveryDirectories_IncludesExistingBuiltinSourceAndOutputScopes()
    {
        using var scope = TestDirectoryScope.Create();
        string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
        string normalOutput = Path.Combine(scope.DirectoryPath, "Output");
        string rootOutput = Path.Combine(scope.DirectoryPath, "RootOutput");
        string builtinSource = Path.Combine(scope.DirectoryPath, "LR2", "LR2files", "CustomFolder");
        Directory.CreateDirectory(bmsRoot);
        Directory.CreateDirectory(normalOutput);
        Directory.CreateDirectory(builtinSource);

        List<string> directories = Lr2FolderFileDiscoveryService.CreateDiscoveryDirectories(
            [bmsRoot],
            normalOutput,
            rootOutput,
            [builtinSource]);

        CollectionAssert.Contains(directories, Path.GetFullPath(bmsRoot));
        CollectionAssert.Contains(directories, Path.GetFullPath(normalOutput));
        CollectionAssert.Contains(directories, Path.GetFullPath(builtinSource));
        CollectionAssert.DoesNotContain(directories, Path.GetFullPath(rootOutput));
    }

    /// <summary>
    /// 準備済みの出力範囲だけを再列挙から外し、名前の似た兄弟範囲や別ルートまで除く誤りを検出します。
    /// </summary>
    [TestMethod]
    public void CreateDiscoveryDirectoriesForEnumeration_ExcludesPreparedOutputScopes()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileDiscoveryServiceTests), Guid.NewGuid().ToString("N"));
        string rootDirectory = Path.Combine(testDirectory, "BMS");
        string outputBase = Path.Combine(testDirectory, "Output");
        string outputBasePrefixSibling = Path.Combine(testDirectory, "OutputOther");
        string rootOutputBase = Path.Combine(testDirectory, "RootOutput");
        var preparedSurface = new Lr2SongDbSyncPreparedDataSurface(
            [outputBase],
            [],
            new Dictionary<string, RootFileEnumerationEntry>(System.StringComparer.OrdinalIgnoreCase),
            discoveryComplete: true);

        var directories = Lr2FolderFileDiscoveryService.CreateDiscoveryDirectoriesForEnumeration(
            [rootDirectory, outputBase, outputBasePrefixSibling, rootOutputBase],
            preparedSurface).ToList();

        CollectionAssert.Contains(directories, rootDirectory);
        CollectionAssert.Contains(directories, outputBasePrefixSibling);
        CollectionAssert.Contains(directories, rootOutputBase);
        CollectionAssert.DoesNotContain(directories, outputBase);
    }

    /// <summary>
    /// 準備済み範囲の古い候補だけを新出力へ置換し、同じ基点の外部ファイルや似た名前の兄弟対象を失う誤りを検出します。
    /// </summary>
    [TestMethod]
    public void MergeCandidateSurface_ReplacesOnlyPreparedScopeCandidates()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileDiscoveryServiceTests), Guid.NewGuid().ToString("N"));
        string outputBase = Path.Combine(testDirectory, "Output");
        string preparedDirectory = Path.Combine(outputBase, "Table");
        string preparedPrefixSibling = Path.Combine(outputBase, "TableOther");
        string oldManagedPath = Path.Combine(preparedDirectory, "old.lr2folder");
        string preparedPath = Path.Combine(preparedDirectory, "new.lr2folder");
        string siblingPath = Path.Combine(preparedPrefixSibling, "keep.lr2folder");
        string externalOutputPath = Path.Combine(outputBase, "external.lr2folder");
        var baseEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [oldManagedPath] = new RootFileEnumerationEntry(oldManagedPath),
            [siblingPath] = new RootFileEnumerationEntry(siblingPath),
            [externalOutputPath] = new RootFileEnumerationEntry(externalOutputPath)
        };
        var baseCandidates = new Lr2FolderFileCandidateSnapshot(
            [oldManagedPath, siblingPath, externalOutputPath],
            baseEntries,
            discoveryComplete: true);
        var preparedSurface = new Lr2SongDbSyncPreparedDataSurface(
            [preparedDirectory],
            [preparedPath],
            new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [preparedPath] = new RootFileEnumerationEntry(preparedPath)
            },
            discoveryComplete: true);

        Lr2FolderFileCandidateSnapshot merged = Lr2FolderFileDiscoveryService.MergeCandidateSurface(
            baseCandidates,
            preparedSurface);

        CollectionAssert.Contains(merged.Paths.ToList(), preparedPath);
        CollectionAssert.Contains(merged.Paths.ToList(), siblingPath);
        CollectionAssert.Contains(merged.Paths.ToList(), externalOutputPath);
        CollectionAssert.DoesNotContain(merged.Paths.ToList(), oldManagedPath);
        Assert.IsTrue(merged.DiscoveryComplete);
    }

    /// <summary>
    /// 管理表ディレクトリの列挙済み・未登録ファイルだけを除き、基点直下の外部候補と兄弟範囲を保持することを確認します。
    /// </summary>
    [TestMethod]
    public void ExcludeAppManagedOutputCandidates_RemovesManagedOutputDirectoryFilesOnly()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileDiscoveryServiceTests), Guid.NewGuid().ToString("N"));
        string rootDirectory = Path.Combine(testDirectory, "BMS");
        string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
        string managedDirectory = Path.Combine(outputBase, "Table");
        string managedPath = Path.Combine(managedDirectory, "managed.lr2folder");
        string managedExtraPath = Path.Combine(managedDirectory, "extra.lr2folder");
        string externalOutputPath = Path.Combine(outputBase, "external.lr2folder");
        string externalPath = Path.Combine(rootDirectory, "External", "external.lr2folder");
        string prefixSiblingPath = Path.Combine(rootDirectory, "#BeMusicSeekerOutputOther", "keep.lr2folder");
        DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
        var entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [managedPath] = new RootFileEnumerationEntry(managedPath, timestamp),
            [managedExtraPath] = new RootFileEnumerationEntry(managedExtraPath, timestamp),
            [externalOutputPath] = new RootFileEnumerationEntry(externalOutputPath, timestamp),
            [externalPath] = new RootFileEnumerationEntry(externalPath, timestamp),
            [prefixSiblingPath] = new RootFileEnumerationEntry(prefixSiblingPath, timestamp)
        };

        Lr2FolderFileCandidateSnapshot filtered =
            Lr2FolderFileDiscoveryService.ExcludeAppManagedOutputCandidates(
                [managedPath, managedExtraPath, externalOutputPath, externalPath, prefixSiblingPath],
                entries,
                [managedPath],
                discoveryComplete: true,
                excludedCount: out int excludedCount,
                appManagedOutputDirectories: [managedDirectory]);

        Assert.AreEqual(2, excludedCount);
        CollectionAssert.DoesNotContain(filtered.Paths.ToList(), Path.GetFullPath(managedPath));
        CollectionAssert.DoesNotContain(filtered.Paths.ToList(), Path.GetFullPath(managedExtraPath));
        CollectionAssert.Contains(filtered.Paths.ToList(), Path.GetFullPath(externalOutputPath));
        CollectionAssert.Contains(filtered.Paths.ToList(), Path.GetFullPath(externalPath));
        CollectionAssert.Contains(filtered.Paths.ToList(), Path.GetFullPath(prefixSiblingPath));
        Assert.IsTrue(filtered.DiscoveryComplete);
    }

    /// <summary>
    /// パス一覧にない外部entryも候補に含め、管理範囲のentryだけを除いて捕捉入口の違いによる探索漏れを検出します。
    /// </summary>
    [TestMethod]
    public void ExcludeAppManagedOutputCandidates_IncludesEntryOnlyExternalCandidates()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileDiscoveryServiceTests), Guid.NewGuid().ToString("N"));
        string rootDirectory = Path.Combine(testDirectory, "BMS");
        string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
        string managedDirectory = Path.Combine(outputBase, "Table");
        string managedEntryOnlyPath = Path.Combine(managedDirectory, "managed.lr2folder");
        string externalEntryOnlyPath = Path.Combine(rootDirectory, "External", "entry_only.lr2folder");
        string listedPath = Path.Combine(rootDirectory, "Listed", "listed.lr2folder");
        var entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [managedEntryOnlyPath] = new RootFileEnumerationEntry(managedEntryOnlyPath),
            [externalEntryOnlyPath] = new RootFileEnumerationEntry(externalEntryOnlyPath)
        };

        Lr2FolderFileCandidateSnapshot filtered =
            Lr2FolderFileDiscoveryService.ExcludeAppManagedOutputCandidates(
                [listedPath],
                entries,
                [managedEntryOnlyPath],
                discoveryComplete: true,
                excludedCount: out int excludedCount,
                appManagedOutputDirectories: [managedDirectory]);

        Assert.AreEqual(1, excludedCount);
        CollectionAssert.Contains(filtered.Paths.ToList(), Path.GetFullPath(listedPath));
        CollectionAssert.Contains(filtered.Paths.ToList(), Path.GetFullPath(externalEntryOnlyPath));
        CollectionAssert.DoesNotContain(filtered.Paths.ToList(), Path.GetFullPath(managedEntryOnlyPath));
    }

    /// <summary>
    /// 組込み探索元があるときだけLR2相対の削除範囲を加え、探索していない組込み行を誤削除する範囲指定を検出します。
    /// </summary>
    [TestMethod]
    public void CreatePruneDirectories_AddsRelativeBuiltinScopeOnlyWhenBuiltinSourceExists()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileDiscoveryServiceTests), Guid.NewGuid().ToString("N"));
        string bmsRoot = Path.Combine(testDirectory, "BMS");
        string builtinSource = Path.Combine(testDirectory, "LR2", "LR2files", "CustomFolder");

        List<string> withBuiltin = Lr2FolderFileDiscoveryService.CreatePruneDirectories(
            [bmsRoot],
            string.Empty,
            string.Empty,
            [builtinSource]);
        List<string> withoutBuiltin = Lr2FolderFileDiscoveryService.CreatePruneDirectories(
            [bmsRoot],
            string.Empty,
            string.Empty,
            []);

        CollectionAssert.Contains(withBuiltin, @"LR2files\CustomFolder");
        CollectionAssert.DoesNotContain(withoutBuiltin, @"LR2files\CustomFolder");
    }

    private sealed class TestDirectoryScope : IDisposable
    {
        private TestDirectoryScope(string directoryPath)
        {
            DirectoryPath = directoryPath;
            Directory.CreateDirectory(directoryPath);
        }

        public string DirectoryPath { get; }

        public static TestDirectoryScope Create()
        {
            return new TestDirectoryScope(Path.Combine(Path.GetTempPath(), "BMS_TEST_" + Guid.NewGuid().ToString("N")));
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
