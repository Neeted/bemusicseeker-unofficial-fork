using BeMusicSeeker.TestSupport.Components;
using static BeMusicSeeker.TestSupport.Components.Lr2SongDbSyncInputBuilderTestSupport;
#nullable disable

using System.Diagnostics;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using static BeMusicSeeker.TestSupport.Components.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncInputBuilderTests
{
    /// <summary>
    /// 走査なしの入力を実管理出力設定へ接続し、三種類の列挙から旧管理候補だけを除いて準備済み出力と外部候補を混合することを確認します。
    /// </summary>
    /// <remarks>実管理設定のSQLite読込みと準備済みファイルのmetadataを使い、列挙portだけを固定入力へ差替えます。</remarks>
    [TestMethod]
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

        PlaylistPersistenceRepository.EnsureSchema(scope.DatabaseScope, scope.SongDbPath);
        using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
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
        var library = new TestBmsLibrary(scope.DatabaseScope,
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
            Assert.ThrowsExactly<Lr2FolderTableProjectionIncompleteException>(() => Lr2FolderTableReconciliationService.BuildProjection(new Lr2SongDbSyncRequest { Lr2FolderFilePaths = input.Lr2FolderFilePaths, Lr2FolderFileDiscoveryComplete = input.Lr2FolderFileDiscoveryComplete }));
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

}
