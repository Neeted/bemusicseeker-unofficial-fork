#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncInputBuilderTests
{
    private enum EnumerationCategory
    {
        Lr2Folder,
        TextMetadata,
        Directories
    }

    /// <summary>
    /// 走査なしの入力を実管理出力設定へ接続し、三種類の列挙から旧管理候補だけを除いて準備済み出力と外部候補を混合することを確認します。
    /// </summary>
    /// <remarks>実管理設定のSQLite読込みと準備済みファイルのmetadataを使い、列挙portだけを固定入力へ差替えます。</remarks>
    [DataTestMethod]
    // 管理定義が揃う場合だけ全入力を採用し、物理metadataだけを管理定義へ読み替えません。
    [DataRow(true)]
    [DataRow(false)]
    public void CreateNoScanInput_EnumeratesThenExcludesManagedScopeAndOverlaysPreparedFile(bool supplyManagedDefinition)
    {
        using var scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "Library");
        string outputBaseDirectory = Path.Combine(scope.DirectoryPath, "Output");
        string managedDirectory = Path.Combine(outputBaseDirectory, "Managed");
        string externalDirectory = Path.Combine(scope.DirectoryPath, "External");
        string preparedPath = Path.Combine(managedDirectory, "0000.lr2folder");
        string stalePath = Path.Combine(managedDirectory, "stale.lr2folder");
        string externalPath = Path.Combine(externalDirectory, "external.lr2folder");
        string managedFolderInfoPath = Path.Combine(managedDirectory, "folderinfo.txt");
        string externalFolderInfoPath = Path.Combine(externalDirectory, "folderinfo.txt");

        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(managedDirectory);
        Directory.CreateDirectory(externalDirectory);
        File.WriteAllText(preparedPath, "#TITLE Prepared");
        File.WriteAllText(stalePath, "#TITLE Stale");
        File.WriteAllText(externalPath, "#TITLE External");
        File.WriteAllText(managedFolderInfoPath, "#TITLE Managed");
        File.WriteAllText(externalFolderInfoPath, "#TITLE External");

        PlaylistPersistenceRepository.EnsureSchema(scope.SongDbPath);
        using (var setup = new LR2SongDBExtended(scope.SongDbPath))
        {
            setup.InsertOrReplace(new BMSTable
            {
                playlist_id = 9601,
                name = "Managed playlist",
                symbol = "M",
                Output_dir = "Managed",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
            }, typeof(LR2SongDBExtended.playlist));
        }

        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            LR2RootPath = string.Empty,
            LR2CustomFolderOutputBaseDir = outputBaseDirectory,
            LR2CustomFolderAdditionalOutputBaseDirs = [],
            LR2CustomFolderOutputBaseDirRootType = string.Empty
        };
        var library = new TestBmsLibrary(
            scope.SongDbPath,
            null,
            null,
            "builder_no_scan",
            () => options);
        Lr2SongDbSyncAppManagedOutputScope appManagedScope =
            library.Lr2Synchronization.CreateLr2SongDbSyncAppManagedOutputScope();

        string managedDirectoryKey = Lr2FolderPath.NormalizeDirectoryPath(managedDirectory);
        Assert.AreEqual(1, appManagedScope.Directories.Count);
        Assert.AreEqual(managedDirectory, appManagedScope.Directories.Single(), ignoreCase: true);

        var enumerator = new DeterministicRootFileEnumerator(
            new Dictionary<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>>
            {
                [EnumerationCategory.Lr2Folder] =
                [
                    new RootFileEnumerationEntry(preparedPath),
                    new RootFileEnumerationEntry(stalePath),
                    new RootFileEnumerationEntry(externalPath)
                ],
                [EnumerationCategory.TextMetadata] =
                [
                    new RootFileEnumerationEntry(managedFolderInfoPath),
                    new RootFileEnumerationEntry(externalFolderInfoPath)
                ],
                [EnumerationCategory.Directories] =
                [
                    RootFileEnumerationEntry.FromDirectoryInfo(rootDirectory),
                    RootFileEnumerationEntry.FromDirectoryInfo(managedDirectory),
                    RootFileEnumerationEntry.FromDirectoryInfo(externalDirectory)
                ]
            });
        var preparedSurface = new Lr2SongDbSyncPreparedDataSurface(
            [managedDirectory],
            [preparedPath],
            CreateFileEntryMap(preparedPath),
            CreateDirectoryEntryMap(rootDirectory, managedDirectory, externalDirectory),
            [managedFolderInfoPath, externalFolderInfoPath],
            CreateFileEntryMap(managedFolderInfoPath, externalFolderInfoPath),
            [managedDirectory, externalDirectory],
            discoveryComplete: true,
            managedFolderDefinitions: supplyManagedDefinition ? new Dictionary<string, Lr2FolderFileDefinition>(StringComparer.OrdinalIgnoreCase)
            { [preparedPath] = new Lr2FolderFileDefinition { Title = "Prepared" } } : null);

        var builder = new Lr2SongDbSyncInputBuilder(
            _ => { },
            _ => { },
            new EverythingNative(TestBmsFactory.MissingEverythingBridge),
            enumerator);
        Lr2SongDbSyncInput input = builder.Create(
            new Lr2SongDbSyncInputRowSnapshot([], []),
            new Lr2SongDbSyncInputRootSnapshot(
                DateTime.UtcNow,
                [rootDirectory],
                [rootDirectory, managedDirectory, externalDirectory],
                string.Empty),
            new Lr2SongDbSyncInputSettingsSnapshot(
                new Lr2BuiltinCustomFolderSettings(0, 24, false),
                [],
                outputBaseDirectory,
                [],
                string.Empty,
                []),
            new Lr2SongDbSyncScanSurfaceSelection(null, "no_scan_surface"),
            new Lr2SongDbSyncPreparedSurfaceSelection(preparedSurface),
            appManagedScope,
            Stopwatch.StartNew(),
            Stopwatch.StartNew(),
            Stopwatch.StartNew(),
            Stopwatch.StartNew(),
            Stopwatch.StartNew(),
            Stopwatch.StartNew());

        if (!supplyManagedDefinition)
        {
            Assert.IsFalse(input.Lr2FolderFileDiscoveryComplete);
            CollectionAssert.AreEquivalent(new[] { preparedPath, externalPath }, input.Lr2FolderFilePaths.ToArray());
            Assert.IsNull(input.ManagedFolderDefinitions);
            CollectionAssert.AreEquivalent(new[] { EnumerationCategory.Lr2Folder, EnumerationCategory.TextMetadata, EnumerationCategory.Directories }, enumerator.RequestedCategories.ToArray());
            Assert.ThrowsException<Lr2FolderTableProjectionIncompleteException>(() => Lr2FolderTableReconciliationService.BuildProjection(new Lr2SongDbSyncRequest { Lr2FolderFilePaths = input.Lr2FolderFilePaths, Lr2FolderFileDiscoveryComplete = input.Lr2FolderFileDiscoveryComplete }));
            return;
        }
        Assert.AreSame(preparedSurface.ManagedFolderDefinitions, input.ManagedFolderDefinitions);
        CollectionAssert.AreEquivalent(
            new[] { preparedPath, externalPath },
            input.Lr2FolderFilePaths.ToArray());
        Assert.IsTrue(input.Lr2FolderFileEntries.ContainsKey(preparedPath));
        Assert.IsTrue(input.Lr2FolderFileEntries.ContainsKey(externalPath));
        Assert.IsFalse(input.Lr2FolderFileEntries.ContainsKey(stalePath));
        Assert.IsTrue(input.Lr2FolderFileDiscoveryComplete);
        CollectionAssert.Contains(input.FolderInfoFilePaths.ToArray(), managedFolderInfoPath);
        CollectionAssert.Contains(input.TextFileDirectories.ToArray(), managedDirectoryKey);
        Assert.IsTrue(input.DirectoryEntries.ContainsKey(managedDirectoryKey));
        CollectionAssert.AreEquivalent(
            new[]
            {
                EnumerationCategory.Lr2Folder,
                EnumerationCategory.TextMetadata,
                EnumerationCategory.Directories
            },
            enumerator.RequestedCategories.ToArray());
    }

    /// <summary>組込みのmetadata-only面と管理完全0件を区別し、Mergeでも0件の管理定義と組込みの物理入力をそれぞれ保持します。</summary>
    [TestMethod]
    public void PreparedSurface_CompleteEmptyManagedDefinitionsRemainDistinctFromBuiltinMetadata()
    {
        string path = Path.Combine(Path.GetTempPath(), "LR2", "LR2files", "CustomFolder", "favorite.lr2folder");
        var builtin = new Lr2SongDbSyncPreparedDataSurface([], [path], new Dictionary<string, RootFileEnumerationEntry>(), true);
        Assert.IsNull(builtin.ManagedFolderDefinitions);
        var empty = Lr2SongDbSyncPreparedDataSurface.FromSyncItems([], []);
        Assert.IsNotNull(empty.ManagedFolderDefinitions);
        Assert.AreEqual(0, empty.ManagedFolderDefinitions.Count);
        var merged = Lr2SongDbSyncPreparedDataSurface.Merge(builtin, empty);
        Assert.IsNotNull(merged.ManagedFolderDefinitions);
        Assert.AreEqual(0, merged.ManagedFolderDefinitions.Count);
        CollectionAssert.AreEqual(new[] { path }, merged.Lr2FolderFilePaths.ToArray());
    }

    /// <summary>通常候補にない深いfolderinfoを実入力構築から全体DB投影まで接続します。scanなしで組込み準備面も合流するとき、外部候補を失わず、双方のタイトル・親・相対DBpathと準備済みmetadataを保持することを確認します。</summary>
    [DataTestMethod]
    // scanあり/なしの既存保証に、列挙だけにある外部InfoOnlyと準備面だけにある組込みを混在させる条件を加えます。
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void CreateInput_FolderInfoOnlyHierarchyReachesWholeTableProjection(bool hasScanSurface, bool hasBuiltinPrepared)
    {
        using var scope = TestDatabaseScope.Create();
        string root = Path.Combine(scope.DirectoryPath, "Root"), first = Path.Combine(root, "A"), deep = Path.Combine(first, "B");
        Directory.CreateDirectory(deep);
        string info = Path.Combine(deep, "folderinfo.txt");
        File.WriteAllText(info, "#TITLE Input info");
        string lr2Directory = Path.Combine(scope.DirectoryPath, "LR2");
        string builtinRoot = Path.Combine(lr2Directory, "LR2files", "CustomFolder");
        string builtinDirectory = Path.Combine(builtinRoot, "Deep");
        string builtinInfo = Path.Combine(builtinDirectory, "folderinfo.txt");
        string builtinCustom = Path.Combine(builtinDirectory, "custom.lr2folder");
        DateTime preparedDirectoryTime = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
        DateTime preparedInfoTime = preparedDirectoryTime.AddMinutes(1), preparedCustomTime = preparedDirectoryTime.AddMinutes(2);
        var prepared = Lr2SongDbSyncPreparedDataSurface.FromSyncItems([], []);
        if (hasBuiltinPrepared)
        {
            Directory.CreateDirectory(builtinDirectory);
            File.WriteAllText(builtinInfo, "#TITLE Prepared builtin info");
            File.WriteAllText(builtinCustom, "#TITLE Prepared builtin custom");
            var builtin = new Lr2SongDbSyncPreparedDataSurface(
                [builtinRoot], [builtinCustom],
                new Dictionary<string, RootFileEnumerationEntry> { [builtinCustom] = new(builtinCustom, preparedCustomTime) },
                new Dictionary<string, RootFileEnumerationEntry> { [builtinDirectory] = new(builtinDirectory, preparedDirectoryTime) },
                [builtinInfo], new Dictionary<string, RootFileEnumerationEntry> { [builtinInfo] = new(builtinInfo, preparedInfoTime) },
                [builtinDirectory], discoveryComplete: true);
            prepared = Lr2SongDbSyncPreparedDataSurface.Merge(prepared, builtin);
        }
        // 外部InfoOnlyは列挙portだけから渡し、組込みのcustom・folderinfo・dir metadataを列挙結果へ混ぜません。
        var enumerator = new DeterministicRootFileEnumerator(new Dictionary<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>>
        {
            [EnumerationCategory.Lr2Folder] = [],
            [EnumerationCategory.TextMetadata] = [new RootFileEnumerationEntry(info)],
            [EnumerationCategory.Directories] = [RootFileEnumerationEntry.FromDirectoryInfo(root), RootFileEnumerationEntry.FromDirectoryInfo(first), RootFileEnumerationEntry.FromDirectoryInfo(deep)]
        });
        Lr2SongDbSyncScanSurfaceSnapshot scan = hasScanSurface ? new([root], [root], CreateDirectoryEntryMap(root, first, deep),
            CreateDirectoryEntryMap(root), [info], CreateFileEntryMap(info), [], [root], [], new Dictionary<string, RootFileEnumerationEntry>(), true) : null;
        IReadOnlyList<string> builtinSources = hasBuiltinPrepared ? [builtinRoot] : [];
        var builtinSettings = new Lr2BuiltinCustomFolderSettings(0, 24, false);
        var builder = new Lr2SongDbSyncInputBuilder(_ => { }, _ => { }, new EverythingNative(TestBmsFactory.MissingEverythingBridge), enumerator);
        Lr2SongDbSyncInput input = builder.Create(new([], []), new(DateTime.UtcNow, [root], [root, .. builtinSources], hasBuiltinPrepared ? lr2Directory : null),
            new(builtinSettings, builtinSources, null, [], null, []),
            new(scan, hasScanSurface ? "scan" : "no_scan"), new(prepared), new([], [], [], true),
            Stopwatch.StartNew(), Stopwatch.StartNew(), Stopwatch.StartNew(), Stopwatch.StartNew(), Stopwatch.StartNew(), Stopwatch.StartNew());
        CollectionAssert.AreEquivalent(new[] { root, first, deep }, input.NormalFolderDirectoryPaths.ToArray());
        CollectionAssert.AreEquivalent(hasBuiltinPrepared ? new[] { info, builtinInfo } : new[] { info }, input.FolderInfoFilePaths.ToArray());
        CollectionAssert.AreEquivalent(hasScanSurface ? Array.Empty<EnumerationCategory>()
            : new[] { EnumerationCategory.Lr2Folder, EnumerationCategory.TextMetadata, EnumerationCategory.Directories }, enumerator.RequestedCategories.ToArray());
        Assert.IsTrue(input.Lr2FolderFileDiscoveryComplete);
        Assert.IsNotNull(input.ManagedFolderDefinitions);
        Assert.AreEqual(0, input.ManagedFolderDefinitions.Count);
        if (hasBuiltinPrepared)
        {
            CollectionAssert.AreEqual(new[] { builtinCustom }, input.Lr2FolderFilePaths.ToArray());
            Assert.AreEqual(preparedDirectoryTime, input.DirectoryEntries[builtinDirectory].LastWriteTimeUtc);
            Assert.AreEqual(preparedInfoTime, input.FolderInfoFileEntries[builtinInfo].LastWriteTimeUtc);
            Assert.AreEqual(preparedCustomTime, input.Lr2FolderFileEntries[builtinCustom].LastWriteTimeUtc);
            Assert.AreSame(builtinSettings, input.Lr2BuiltinCustomFolderSettings);
        }
        using var db = new LR2SongDBExtended(scope.SongDbPath);
        // builderが返した候補・設定・metadataをそのまま渡し、失われた祖先や組込みを要求側で補いません。
        Lr2FolderTableReconciliationService.Reconcile(db, new Lr2SongDbSyncRequest
        {
            RootDirectories = input.RootDirectories,
            ChartPaths = input.ChartPaths,
            NormalFolderDirectoryPaths = input.NormalFolderDirectoryPaths,
            FolderInfoFilePaths = input.FolderInfoFilePaths,
            FolderInfoFileEntries = input.FolderInfoFileEntries,
            DirectoryEntries = input.DirectoryEntries,
            Lr2FolderFilePaths = input.Lr2FolderFilePaths,
            Lr2FolderFileEntries = input.Lr2FolderFileEntries,
            Lr2FolderDiscoveryDirectories = input.Lr2FolderDiscoveryDirectories,
            Lr2FolderPruneDirectories = input.Lr2FolderPruneDirectories,
            Lr2RootPath = input.Lr2RootPath,
            Lr2NormalCustomFolderOutputBaseDir = input.Lr2NormalCustomFolderOutputBaseDir,
            Lr2AdditionalNormalCustomFolderOutputBaseDirs = input.Lr2AdditionalNormalCustomFolderOutputBaseDirs,
            Lr2RootCustomFolderOutputBaseDir = input.Lr2RootCustomFolderOutputBaseDir,
            Lr2BuiltinFolderSourceDirectories = input.Lr2BuiltinFolderSourceDirectories,
            ManagedFolderDefinitions = input.ManagedFolderDefinitions,
            Lr2FolderFileDiscoveryComplete = input.Lr2FolderFileDiscoveryComplete,
            SongRows = input.SongRows,
            TextFileDirectories = input.TextFileDirectories
        });
        string builtinDirectoryPath = @"LR2files\CustomFolder\Deep\", builtinCustomPath = @"LR2files\CustomFolder\Deep\custom.lr2folder";
        string[] expectedPaths = hasBuiltinPrepared
            ? [ToFolderPath(root), ToFolderPath(first), ToFolderPath(deep), builtinDirectoryPath, builtinCustomPath]
            : [ToFolderPath(root), ToFolderPath(first), ToFolderPath(deep)];
        CollectionAssert.AreEquivalent(expectedPaths, db.Table<LR2SongDB.folder>().Select(row => row.path).ToArray());
        Assert.AreEqual("Input info", db.Find<LR2SongDB.folder>(ToFolderPath(deep)).title);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, db.Find<LR2SongDB.folder>(ToFolderPath(root)).parent);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(root), db.Find<LR2SongDB.folder>(ToFolderPath(first)).parent);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(first), db.Find<LR2SongDB.folder>(ToFolderPath(deep)).parent);
        if (hasBuiltinPrepared)
        {
            LR2SongDB.folder builtinDirectoryRow = db.Find<LR2SongDB.folder>(builtinDirectoryPath), builtinCustomRow = db.Find<LR2SongDB.folder>(builtinCustomPath);
            Assert.AreEqual("Prepared builtin info", builtinDirectoryRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, builtinDirectoryRow.parent);
            Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(preparedDirectoryTime), builtinDirectoryRow.date);
            Assert.AreEqual("Prepared builtin custom", builtinCustomRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"LR2files\CustomFolder\Deep"), builtinCustomRow.parent);
            Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(preparedCustomTime), builtinCustomRow.date);
            Assert.AreEqual(2, builtinCustomRow.type);
        }
    }

    private sealed class DeterministicRootFileEnumerator : IRootFileEnumerator
    {
        private readonly IReadOnlyDictionary<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>> entriesByCategory;

        internal DeterministicRootFileEnumerator(
            IReadOnlyDictionary<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>> entriesByCategory)
        {
            var snapshot = new Dictionary<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>>();
            foreach (KeyValuePair<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>> pair in entriesByCategory
                ?? new Dictionary<EnumerationCategory, IReadOnlyList<RootFileEnumerationEntry>>())
            {
                snapshot[pair.Key] = [.. (pair.Value ?? [])];
            }

            this.entriesByCategory = snapshot;
        }

        internal List<EnumerationCategory> RequestedCategories { get; } = [];

        public RootFileEnumerationResult EnumerateFiles(
            IEnumerable<string> rootDirectories,
            IEnumerable<RootFileEnumerationGroup> groups,
            bool verboseLog = false)
        {
            var result = new RootFileEnumerationResult
            {
                Success = true,
                IsComplete = true,
                BackendName = "deterministic-test"
            };
            foreach (RootFileEnumerationGroup group in groups ?? [])
            {
                EnumerationCategory category = Classify(group);
                RequestedCategories.Add(category);
                result.InitializeGroup(group.Name);
                if (!entriesByCategory.TryGetValue(category, out IReadOnlyList<RootFileEnumerationEntry> entries))
                {
                    continue;
                }

                foreach (RootFileEnumerationEntry entry in entries)
                {
                    result.AddEntry(group.Name, entry);
                }
            }

            return result;
        }

        private static EnumerationCategory Classify(RootFileEnumerationGroup group)
        {
            if (group.IncludeDirectories)
            {
                return EnumerationCategory.Directories;
            }

            if (group.Extensions.Any(extension => string.Equals(extension, ".lr2folder", StringComparison.OrdinalIgnoreCase)))
            {
                return EnumerationCategory.Lr2Folder;
            }

            if (group.Extensions.Length > 0)
            {
                return EnumerationCategory.TextMetadata;
            }

            throw new InvalidOperationException("Unsupported deterministic enumeration group.");
        }
    }
}
