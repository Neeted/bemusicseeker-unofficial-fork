using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class SettingDialogEditCompletionTests
{
    /// <summary>無変更の二周期・Greekのlive表記訂正・ASCIIの次回起動での初検出を分担し、管理表/CLEAR FOLDERの全親とCRCを同じ操作で確定します。fresh起動は全体同期や任意修復による救済と区別します。</summary>
    [DataTestMethod]
    [DataRow("NormalOutput", "NormalOutput", false)]
    [DataRow("normalOutput", "NormalOutput", true)]
    [DataRow("αOutput", "ΑOutput", false)]
    [DoNotParallelize]
    public void NormalOutputScan_PreservesManagedHierarchyAndConvergesConfirmedPaths(string initialName, string observedName, bool detectOnStartup)
    {
        SettingsChartFileScanner? scanner = null;
        string hash = string.Empty;
        string outsideRow = string.Empty;
        string completedSignature = string.Empty;
        var restartProgress = new System.Collections.Concurrent.ConcurrentQueue<PlaylistSyncProgressSnapshot>();
        string[] stableRows = [];
        Dictionary<string, DateTime> stableFiles = new(StringComparer.Ordinal);
        WithInitializedLr2Owner((root, values) =>
        {
            string initialBase = Path.Combine(root, initialName);
            // WindowsではCreateDirectoryだけで既存名の大文字小文字が変わらないため、初回の物理表記も揃えます。
            if (!string.Equals(values.LR2CustomFolderOutputBaseDir, initialBase, StringComparison.Ordinal))
            { RenameOutputBase(root, values.LR2CustomFolderOutputBaseDir, initialBase); }
            values.LR2CustomFolderOutputBaseDir = initialBase;
            string pack = Directory.CreateDirectory(Path.Combine(values.LR2CustomFolderOutputBaseDir, "Pack")).FullName;
            File.WriteAllText(Path.Combine(pack, "normal.bms"), BmsLibraryInitializationTestSupport.CreateValidBmsText("Normal output BMS"));
            File.WriteAllText(Path.Combine(values.BMSInstallDir, "song.bms"), BmsLibraryInitializationTestSupport.CreateValidBmsText("Separate BMS root"));
            var config = new LR2Config(values.LR2ConfigXmlPath);
            config.AddBMSSearchDirectories([values.LR2CustomFolderOutputBaseDir]);
            config.Save(config.ConfigFilePath);
        }, (root, values, composition, shell, factory, failures, dialogs) =>
        {
            BMSLibrary library = factory.CreatedLibrary ?? throw new AssertFailedException();
            BMSPlaylist playlist = factory.CreatedPlaylist ?? throw new AssertFailedException();
            string previousBase = values.LR2CustomFolderOutputBaseDir;
            Assert.AreEqual(Path.Combine(root, initialName), previousBase);
            string currentBase = Path.Combine(root, observedName);
            string previousTable = Path.Combine(previousBase, "Table");
            string currentTable = Path.Combine(currentBase, "Table");
            string chart = Path.Combine(previousBase, "Pack", "normal.bms");
            hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(chart))).ToLowerInvariant();
            var table = new BMSTable
            {
                playlist_id = 8292,
                name = "Table",
                symbol = "T",
                Output_dir = "Table",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.ClearFolder,
                entries = [BmsPlaylistTestSupport.CreateEntry(hash, "Folder")],
                Folder_order = ["Folder"]
            };
            playlist.BMSTables.Add(table);
            playlist.CommitBMSTableWithEntriesToDB(table);
            playlist.ReOutputCustomFolder(table);
            string outsidePath = Lr2FolderPath.ToFolderPath(Path.Combine(root, "Outside"));
            string obsoleteParent = Lr2FolderPath.ToFolderPath(Path.Combine(previousBase, "GoneExternal"));
            string obsoleteCustom = Path.Combine(previousBase, "GoneExternal", "external.lr2folder");
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDb())
            {
                foreach (LR2SongDB.folder row in db.Table<LR2SongDB.folder>().ToList().Where(row => IsUnder(row.path, previousTable)))
                { row.adddate = 321; db.InsertOrReplace(row); }
                var outside = new LR2SongDB.folder { path = outsidePath, type = 1, title = "untouched", subtitle = "outside", parent = "outside-parent", date = 10, adddate = null, info_a = "keep" };
                db.InsertOrReplace(outside);
                outsideRow = Newtonsoft.Json.JsonConvert.SerializeObject(outside);
                db.InsertOrReplace(new LR2SongDB.folder { path = obsoleteParent, type = 1, title = "gone parent", date = 10, adddate = 111 });
                db.InsertOrReplace(new LR2SongDB.folder { path = obsoleteCustom, type = 4, title = "gone custom", date = 10, adddate = 222 });
                db.Execute("UPDATE song SET favorite = ?, tag = ?, adddate = ? WHERE hash = ?;", 7, "user-tag", 123, hash);
                completedSignature = Lr2SongDbSyncSignatureBuilder.Build(composition.BmsLibraryOptionsProvider());
                Lr2SongDbSyncStatusService.MarkCompleted(db, completedSignature,
                    "generated-complete", library.Lr2SongDbSyncRequestedVersion, DateTime.UtcNow);
            }
            string[] originalManaged = ReadManaged(values, previousTable);
            Assert.IsTrue(originalManaged.Length > 3);
            AssertHierarchy(values, previousBase);
            Assert.IsFalse(library.Lr2Synchronization.EvaluateLr2SongDbSyncStatus(true,
                Lr2SongDbSyncSignatureBuilder.Build(composition.BmsLibraryOptionsProvider()), DateTime.UtcNow).IsNeeded);
            if (detectOnStartup)
            {
                // 稼働中には再走査・保存・再出力せず、物理表記だけを変えてこの寿命を終えます。
                stableRows = originalManaged;
                RenameOutputBase(root, previousBase, currentBase);
                stableFiles = CaptureFileTimes(currentTable);
                return;
            }
            string candidate = Path.Combine(root, "Unconfirmed");
            shell.SettingDialog.BmsRootPathList.Add(candidate);
            BMSLibrary.ScoreSnapshot beforeScore = library.CaptureScoreSnapshotForView();
            long cacheVersion = library.BMSParentFolderListCacheVersion;
            int publications = 0;
            int confirmedNotifications = 0;
            composition.BmsRootRegistrations.Published += (_, _) => publications++;
            shell.SettingDialog.PropertyChanged += (_, args) =>
            { if (args.PropertyName == nameof(SettingsDialogViewModel.ConfirmedBMSDirectories)) { confirmedNotifications++; } };
            bool changed = !string.Equals(previousBase, currentBase, StringComparison.Ordinal);
            if (changed) { RenameOutputBase(root, previousBase, currentBase); }
            (scanner ?? throw new AssertFailedException()).Current = Scan(values, currentBase);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(shell.ReloadFileDiffAsync(), "normal-output-file-diff");
            AssertHierarchy(values, currentBase);
            string[] afterFirst = ReadManaged(values, currentTable);
            if (!changed) { CollectionAssert.AreEqual(originalManaged, afterFirst); }
            AssertConverged(root, values, composition);
            CollectionAssert.Contains(shell.SettingDialog.BmsRootPathList.ToArray(), candidate);
            Assert.AreEqual(beforeScore.SourceGeneration, library.GetScoreRuntimeStateForDiagnostics().SourceGeneration);
            Assert.AreSame(beforeScore.PlayHistorySnapshot, library.CaptureScoreSnapshotForView().PlayHistorySnapshot);
            Assert.AreEqual(changed ? 1 : 0, publications);
            if (changed) { Assert.IsTrue(confirmedNotifications > 0); Assert.IsTrue(library.BMSParentFolderListCacheVersion > cacheVersion); }
            TestUiDispatcherHost.AwaitTaskOnDispatcher(shell.ReloadFileDiffAsync(), "normal-output-second-diff");
            CollectionAssert.AreEqual(afterFirst, ReadManaged(values, currentTable));
            Assert.AreEqual(changed ? 1 : 0, publications);
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            stableRows = afterFirst;
            stableFiles = CaptureFileTimes(currentTable);
            using LR2SongDBExtended statusDb = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
            Assert.AreEqual(1, statusDb.ExecuteScalar<int>("SELECT COUNT(1) FROM playlist_custom_folder_output_status WHERE playlist_id = ? AND output_directory = ?;", table.playlist_id, currentTable));
        }, scannerFactory: (root, values) =>
        {
            string physicalBase = Directory.GetDirectories(root).Single(path =>
                string.Equals(Path.GetFileName(path), observedName, StringComparison.OrdinalIgnoreCase));
            return scanner = new SettingsChartFileScanner(Scan(values, physicalBase));
        }, afterRestart: (root, values, composition, shell, factory, failures, dialogs) =>
        {
            string tableDirectory = Path.Combine(root, observedName, "Table");
            AssertHierarchy(values, Path.Combine(root, observedName));
            if (detectOnStartup)
            {
                AssertConverged(root, values, composition);
                Assert.AreEqual(stableRows.Length, ReadManaged(values, tableDirectory).Length);
            }
            else
            {
                CollectionAssert.AreEqual(stableRows, ReadManaged(values, tableDirectory), "保存済み出力状態が現行のfresh起動でも、初回生成で欠損を隠しません。");
            }
            AssertCompletedRun(values);
            PlaylistSyncProgressSnapshot[] repair = [.. restartProgress.Where(progress => progress.Source == "custom_folder_repair")];
            Assert.IsTrue(repair.Length > 0, "起動の修復終端通知を実際に受け取ります。");
            Assert.IsTrue(repair.All(progress => !progress.IsActive && progress.TotalTableCount == 0 && progress.CompletedTableCount == 0),
                "表記訂正を任意repairで救済せず、必要な反映の後では修復対象0になります。");
            foreach ((string path, DateTime timestamp) in stableFiles) { Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(path)); }
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        }, playlistFactory: (composition, profile, library) => new BMSPlaylist(
            new BmsPlaylistLibraryBindings(library), profile.SongDbPath, profile.Lr2ConfigOwner, profile.Lr2ScoreDbPath,
            composition.PlaylistUrlCompletionOptionsProvider, composition.BeatorajaBmtOptionsProvider,
            composition.CustomFolderOutputSettingsProvider, composition.ApplicationPathSnapshot, composition.UiScheduler,
            lr2PlaylistFolderSynchronization: new FixturePlaylistFolderSynchronizationPort(library.Lr2PlaylistFolderSynchronization,
                () => composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir)),
        beforeRestart: (root, values, shell) =>
        {
            shell.PlaylistWorkspace.PlaylistSyncProgressChanged += (_, change) => restartProgress.Enqueue(change.Snapshot);
            if (!detectOnStartup) { return; }
            string previousBase = Path.Combine(root, initialName);
            Assert.AreEqual(previousBase, values.LR2CustomFolderOutputBaseDir);
            Assert.AreEqual(previousBase, PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "settings", "user.config")).LR2CustomFolderOutputBaseDir);
            CollectionAssert.Contains(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), previousBase);
            CollectionAssert.AreEqual(stableRows, ReadManaged(values, Path.Combine(previousBase, "Table")));
            using LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
            LR2SongDB.song song = db.Table<LR2SongDB.song>().Single(row => row.hash == hash);
            Assert.AreEqual(Path.Combine(previousBase, "Pack", "normal.bms"), song.path);
            Assert.AreEqual(((int?)7, "user-tag", (int?)123), (song.favorite, song.tag, song.adddate));
            AssertCompletedRun(values);
        });

        void AssertConverged(string root, Settings values, ApplicationComposition composition)
        {
            string previousBase = Path.Combine(root, initialName);
            string currentBase = Path.Combine(root, observedName);
            string currentTable = Path.Combine(currentBase, "Table");
            string obsoleteParent = Lr2FolderPath.ToFolderPath(Path.Combine(previousBase, "GoneExternal"));
            string obsoleteCustom = Path.Combine(previousBase, "GoneExternal", "external.lr2folder");
            string outsidePath = Lr2FolderPath.ToFolderPath(Path.Combine(root, "Outside"));
            bool changed = !string.Equals(previousBase, currentBase, StringComparison.Ordinal);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly())
            {
                var rows = db.Table<LR2SongDB.folder>().ToList();
                Assert.IsFalse(rows.Any(row => row.path == obsoleteParent || row.path == obsoleteCustom));
                Assert.AreEqual(outsideRow, Newtonsoft.Json.JsonConvert.SerializeObject(rows.Single(row => row.path == outsidePath)));
                if (changed)
                {
                    Assert.IsFalse(rows.Any(row => row.path.StartsWith(previousBase + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
                    Assert.IsTrue(rows.Where(row => IsUnder(row.path, currentTable)).All(row => row.adddate > 321));
                }
                LR2SongDB.song song = db.Table<LR2SongDB.song>().Single(row => row.hash == hash);
                Assert.AreEqual(Path.Combine(currentBase, "Pack", "normal.bms"), song.path);
                Assert.AreEqual(((int?)7, "user-tag", (int?)123), (song.favorite, song.tag, song.adddate));
            }
            Assert.AreEqual(currentBase, values.LR2CustomFolderOutputBaseDir);
            Assert.AreEqual(currentBase, PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "settings", "user.config")).LR2CustomFolderOutputBaseDir);
            Assert.AreEqual(currentBase, composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir);
            Assert.AreEqual(currentBase, composition.ResolveLr2Config(values.LR2ConfigXmlPath).Capture().Placement.LR2CustomFolderOutputBaseDir);
            CollectionAssert.Contains(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), currentBase);
            CollectionAssert.Contains(composition.ResolveLr2Config(values.LR2ConfigXmlPath).Capture().RegisteredRoots.ToArray(), currentBase);
            AssertCompletedRun(values);
        }

        void AssertCompletedRun(Settings values)
        {
            using LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status status = db.Table<LR2SongDBExtended.lr2_song_db_sync_status>().Single();
            Assert.AreEqual("generated-complete", status.run_id, "強制全体同期で表記訂正を救済しません。");
            Assert.AreEqual(completedSignature, status.signature);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed.ToString(), status.status);
        }

        static Dictionary<string, DateTime> CaptureFileTimes(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.GetLastWriteTimeUtc, StringComparer.Ordinal);

        static void RenameOutputBase(string root, string previousBase, string currentBase)
        {
            var directoryTimes = Directory.GetDirectories(previousBase, "*", SearchOption.AllDirectories).Prepend(previousBase)
                .ToDictionary(path => Path.GetRelativePath(previousBase, path), Directory.GetLastWriteTimeUtc, StringComparer.Ordinal);
            Dictionary<string, DateTime> fileTimes = CaptureFileTimes(previousBase);
            string intermediate = Path.Combine(root, "rename-owned-output");
            Directory.Move(previousBase, intermediate);
            Directory.Move(intermediate, currentBase);
            foreach ((string relative, DateTime timestamp) in directoryTimes)
            { Directory.SetLastWriteTimeUtc(Path.Combine(currentBase, relative), timestamp); }
            foreach ((string path, DateTime timestamp) in fileTimes)
            { Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(Path.Combine(currentBase, Path.GetRelativePath(previousBase, path)))); }
        }

        static bool IsUnder(string path, string directory) => path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        static IChartFileScanner Scan(Settings values, string outputBase) => CapturedChartFileScanner.FromFixture(
            [Path.Combine(values.BMSInstallDir, "song.bms"), Path.Combine(outputBase, "Pack", "normal.bms")],
            new Dictionary<string, IEnumerable<string>>(), [values.BMSInstallDir, outputBase]);
        static string[] ReadManaged(Settings values, string directory)
        {
            using LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
            return db.Table<LR2SongDB.folder>().ToList().Where(row => IsUnder(row.path, directory)).OrderBy(row => row.path, StringComparer.Ordinal)
                .Select(Newtonsoft.Json.JsonConvert.SerializeObject).ToArray();
        }
        static void AssertHierarchy(Settings values, string outputBase)
        {
            using LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
            var rows = db.Table<LR2SongDB.folder>().ToDictionary(row => row.path, StringComparer.Ordinal);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, rows[Lr2FolderPath.ToFolderPath(outputBase)].parent);
            string table = Path.Combine(outputBase, "Table");
            Assert.IsTrue(rows.ContainsKey(Lr2FolderPath.ToFolderPath(table)));
            Assert.IsTrue(rows.ContainsKey(Lr2FolderPath.ToFolderPath(Path.Combine(table, "CLEAR FOLDER"))));
            Assert.IsTrue(rows.ContainsKey(Path.Combine(table, "0000.lr2folder")));
            foreach (LR2SongDB.folder row in rows.Values.Where(row => IsUnder(row.path, table)))
            {
                string parent = Path.GetDirectoryName(row.path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? throw new AssertFailedException();
                Assert.IsTrue(rows.ContainsKey(Lr2FolderPath.ToFolderPath(parent)), row.path);
                Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(parent), row.parent, row.path);
            }
        }
    }

    /// <summary>物理列挙の環境依存だけを所有fixtureの実FSへ置き換え、受付検査とDB保存は同じLibraryの実portへ渡します。</summary>
    private sealed class FixturePlaylistFolderSynchronizationPort(ILr2PlaylistFolderSynchronizationPort actual,
        Func<string> outputBase) : ILr2PlaylistFolderSynchronizationPort
    {
        public CustomFolderOutputPhysicalSurface GetCurrentAppManagedCustomFolderOutputPhysicalSurface()
        {
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles([outputBase()],
                [new RootFileEnumerationGroup("managed", [".lr2folder"])]);
            Assert.IsTrue(result.Success && result.IsComplete, result.ErrorReason);
            return CustomFolderOutputPhysicalSurface.FromEntries(result.GetEntries("managed"), discoveryComplete: true);
        }

        public Lr2FolderFileDbSyncResult SyncPlaylistLr2FolderFileRows(string operation, Lr2FolderFileDbSyncRequest request,
            LibraryFileMutationCapability mutationCapability)
            => actual.SyncPlaylistLr2FolderFileRows(operation, request, mutationCapability);
    }

    /// <summary>ルート出力への有効導入先の取込みを副作用前に拒否し、別の確定先への保存後は管理先を候補・保存から除外して通常導入と表削除を接続します。</summary>
    /// <remarks>専用ドライブ別名、実settings/XML/SQLite、標準compositionの生成・導入・削除を通し、候補表示だけの成功や存在しない導入先による偶然の拒否を区別します。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void Lr2DriveRootOutput_PreservesConfirmedInstallThroughGenerationInstallationAndTableRemoval()
    {
        OwnedTestDrive? drive = null;
        WithInitializedLr2Owner((root, values) =>
        {
            OwnedTestDrive owned = drive ?? throw new AssertFailedException("専用ドライブが未準備です。");
            owned.AssertOwned();
            string existing = Directory.CreateDirectory(Path.Combine(owned.Root, "ExistingSongs")).FullName;
            values.BMSInstallDir = existing;
            values.FolderNameFormat = "%TITLE%";
            values.KeepInstallablePackagesPending = false;
            LR2Config config = new(values.LR2ConfigXmlPath);
            config.AddBMSSearchDirectories([existing]);
            config.Save(config.ConfigFilePath);
            File.WriteAllText(Path.Combine(owned.Root, "outside-table.txt"), "inside volume, outside table");
            File.WriteAllText(Path.Combine(root, "outside-volume.txt"), "outside volume");
        }, (root, values, composition, shell, factory, failures, dialogs) =>
        {
            OwnedTestDrive owned = drive ?? throw new AssertFailedException();
            owned.AssertOwned();
            SettingsDialogViewModel dialog = shell.SettingDialog;
            BMSPlaylist playlist = factory.CreatedPlaylist ?? throw new AssertFailedException();
            BMSLibrary library = factory.CreatedLibrary ?? throw new AssertFailedException();
            Lr2ConfigOwner config = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
            string settingsPath = Path.Combine(root, "settings", "user.config");
            string originalRoot = values.LR2CustomFolderOutputBaseDirRootType;
            string originalInstall = values.BMSInstallDir;
            string songs = Path.Combine(root, "Songs");
            string managed = Path.Combine(owned.Root, "Table");
            var table = new BMSTable
            {
                playlist_id = 8291,
                name = "Table",
                symbol = "T",
                Output_dir = "Table",
                is_root_folder = true,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries = [BmsPlaylistTestSupport.CreateEntry(new string('d', 32), "Folder")],
                Folder_order = ["Folder"]
            };
            playlist.BMSTables.Add(table);
            playlist.CommitBMSTableWithEntriesToDB(table);
            playlist.ReOutputCustomFolder(table);
            playlist.SyncCustomFolderOutputSearchRootsAfterSettingsChange(originalRoot);
            string oldFile = Path.Combine(originalRoot, "Table", "0000.lr2folder");
            byte[] oldOutput = File.ReadAllBytes(oldFile);
            byte[] oldSettings = File.ReadAllBytes(settingsPath);
            byte[] oldXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
            string[] oldFolderRows = ReadFolderPaths();
            Assert.IsTrue(Directory.Exists(originalInstall));
            CollectionAssert.Contains(dialog.ConfirmedBMSDirectories.ToArray(), originalInstall);
            Assert.AreEqual(string.Empty, dialog.BmsInstallDirValidationMessage);

            dialog.LR2CustomFolderAsRootOutputDir = owned.Root;
            StringAssert.Contains(dialogs.LastMessageText, string.Format(Resources.Validation_OutputBaseContainsInstallDirectoryFormat, originalInstall));
            Assert.AreEqual(originalRoot, dialog.LR2CustomFolderAsRootOutputDir);
            Assert.AreEqual(originalRoot, values.LR2CustomFolderOutputBaseDirRootType);
            Assert.AreEqual(originalInstall, values.BMSInstallDir);
            Assert.AreEqual(0, dialogs.ConfirmationCount);
            CollectionAssert.AreEqual(oldSettings, File.ReadAllBytes(settingsPath));
            CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
            CollectionAssert.AreEqual(oldOutput, File.ReadAllBytes(oldFile));
            CollectionAssert.AreEquivalent(oldFolderRows, ReadFolderPaths());
            Assert.IsFalse(Directory.Exists(managed));

            TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectBmsInstallDirectoryAsync(songs), "drive-root-select-install");
            dialog.LR2CustomFolderAsRootOutputDir = owned.Root;
            Assert.AreEqual(owned.Root, dialog.LR2CustomFolderAsRootOutputDir);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("root"), "drive-root-output");
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            Assert.AreEqual(owned.Root, PortableSettingsPersistenceTests.OpenSettings(settingsPath).LR2CustomFolderOutputBaseDirRootType);
            Assert.AreEqual(songs, PortableSettingsPersistenceTests.OpenSettings(settingsPath).BMSInstallDir);
            Assert.IsTrue(File.Exists(Path.Combine(managed, "0000.lr2folder")));
            Assert.IsFalse(File.Exists(oldFile));
            CollectionAssert.Contains(ReadRegisteredRoots(), managed);
            CollectionAssert.Contains(ReadFolderPaths(), Path.Combine(managed, "0000.lr2folder"));
            AssertCandidates();
            dialog.LR2CustomFolderOutputDir = Path.Combine(root, "UnconfirmedNormal");
            dialog.CloseCommand.Execute();
            dialog.CancelBmsRoots();
            AssertCandidates();
            Assert.AreEqual(Path.Combine(root, "NormalOutput"), PortableSettingsPersistenceTests.OpenSettings(settingsPath).LR2CustomFolderOutputBaseDir);

            oldSettings = File.ReadAllBytes(settingsPath);
            oldXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
            Assert.IsTrue(Directory.Exists(managed));
            TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectBmsInstallDirectoryAsync(managed), "drive-root-reject-managed-install");
            Assert.AreEqual(songs, values.BMSInstallDir);
            Assert.AreEqual(songs, PortableSettingsPersistenceTests.OpenSettings(settingsPath).BMSInstallDir);
            CollectionAssert.AreEqual(oldSettings, File.ReadAllBytes(settingsPath));
            CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
            CollectionAssert.Contains(ReadRegisteredRoots(), managed);

            string package = Directory.CreateDirectory(Path.Combine(root, "Package")).FullName;
            string sourceChart = Path.Combine(package, "Song.bms");
            File.WriteAllText(sourceChart, BmsLibraryInitializationTestSupport.CreateValidBmsText("Song"));
            byte[] chart = File.ReadAllBytes(sourceChart);
            byte[] audio = [1, 2, 3];
            File.WriteAllBytes(Path.Combine(package, "sound.wav"), audio);
            Task<PackageInstallCommandResult> install = library.InstallChartPackagesAutoWithProgressAsync([package], CancellationToken.None, new SettingsInstallProgressWriter());
            TestUiDispatcherHost.AwaitTaskOnDispatcher(install, "drive-root-normal-install");
            PackageInstallCommandResult result = install.GetAwaiter().GetResult();
            Assert.IsTrue(result.HasDurableCommit);
            Assert.IsFalse(result.HasRequiredFailure);
            string installedChart = Path.Combine(songs, "Song", "Song.bms");
            AssertSong();

            var removal = new PlaylistRemovalWorkflowOwner(dialogs, () => playlist, () => library, () => config, composition.CustomFolderOutputSettingsProvider);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(removal.RemoveTreeTableAsync(table, () => { }), "drive-root-remove-table");
            Assert.IsFalse(Directory.Exists(managed));
            Assert.IsFalse(playlist.BMSTables.Contains(table));
            CollectionAssert.DoesNotContain(ReadRegisteredRoots(), managed);
            CollectionAssert.DoesNotContain(config.Capture().RegisteredRoots.ToArray(), managed);
            Assert.IsFalse(ReadFolderPaths().Any(path => path.StartsWith(managed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly())
            { Assert.AreEqual(0, db.Table<BMSTable>().Count(row => row.playlist_id == table.playlist_id)); }
            AssertSong();
            Assert.AreEqual("inside volume, outside table", File.ReadAllText(Path.Combine(owned.Root, "outside-table.txt")));
            Assert.AreEqual("outside volume", File.ReadAllText(Path.Combine(root, "outside-volume.txt")));
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));

            string[] ReadRegisteredRoots() => new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking().ToArray();
            string[] ReadFolderPaths()
            {
                using LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
                return db.Table<LR2SongDB.folder>().Select(row => row.path).ToArray();
            }
            void AssertCandidates()
            {
                CollectionAssert.Contains(dialog.AvailableBMSDirectories, songs);
                CollectionAssert.Contains(dialog.ConfirmedBMSDirectories.ToArray(), songs);
                CollectionAssert.DoesNotContain(dialog.AvailableBMSDirectories, managed);
                CollectionAssert.DoesNotContain(dialog.ConfirmedBMSDirectories.ToArray(), managed);
                CollectionAssert.DoesNotContain(dialog.AvailableBMSDirectories, originalInstall);
            }
            void AssertSong()
            {
                CollectionAssert.AreEqual(chart, File.ReadAllBytes(installedChart));
                CollectionAssert.AreEqual(audio, File.ReadAllBytes(Path.Combine(songs, "Song", "sound.wav")));
                Assert.AreEqual(installedChart, library.BmsCharts.Single().Path);
                Assert.AreEqual(installedChart, library.ChartPackagesInstalled.Single().ChartEntries.Single().Chart.Path);
                using LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
                Assert.AreEqual(installedChart, db.Table<LR2SongDB.song>().Single().path);
            }
        }, ownedResource: root => drive = OwnedTestDrive.Create(root));
    }

    /// <summary>導入の進捗通知だけを終端判定から切り離し、実導入Taskを待つ代表用の受信先です。</summary>
    private sealed class SettingsInstallProgressWriter : IPackageInstallProgressWriter
    {
        public void TryWrite(PackageInstallProgressUpdate update) { }
    }

    /// <summary>起動XMLに登録済みの管理AをBへ移す前に未確定のユーザー追加・削除の破棄を確認し、その後のC確定でAをユーザー登録へ復活させません。</summary>
    /// <remarks>旧Aの実在だけを条件差とし、実設定保存・出力port・共有profileを動かします。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Lr2OutputConfirmationDiscardsApprovedCandidatesWithoutResurrectingOldOutput(bool keepOldDirectory)
    {
        WithInitializedLr2Owner((root, values) =>
        {
            LR2Config initial = new(values.LR2ConfigXmlPath);
            initial.AddBMSSearchDirectories([values.LR2CustomFolderOutputBaseDir]);
            initial.Save(initial.ConfigFilePath);
        }, (root, values, composition, shell, factory, failures, dialogs) =>
        {
            SettingsDialogViewModel dialog = shell.SettingDialog;
            string original = values.LR2CustomFolderOutputBaseDir;
            string songs = values.BMSInstallDir;
            string staged = Directory.CreateDirectory(Path.Combine(root, "Staged")).FullName;
            dialog.BmsRootPathList.Remove(songs);
            dialog.AddBmsSearchRootPathsAsync([staged]).GetAwaiter().GetResult();
            string destination = Path.Combine(root, "MovedOutput");
            dialog.LR2CustomFolderOutputDir = destination;
            dialog.SelectedBmsSearchRootPath = staged;
            TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("normal"), "lr2-output-confirm");
            Assert.AreEqual(1, dialogs.ConfirmationCount, "候補破棄と出力確認を一回で案内します。");
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            if (keepOldDirectory) { Directory.CreateDirectory(original); }
            else if (Directory.Exists(original)) { Directory.Delete(original); }
            CollectionAssert.AreEqual(new[] { songs }, dialog.AvailableBMSDirectories);
            CollectionAssert.AreEqual(new[] { songs }, dialog.ConfirmedBMSDirectories.ToArray());
            CollectionAssert.DoesNotContain(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), staged);
            dialog.CancelBmsRoots();
            CollectionAssert.AreEqual(new[] { songs }, dialog.AvailableBMSDirectories);
            string added = Directory.CreateDirectory(Path.Combine(root, "C")).FullName;
            dialog.AddBmsSearchRootPathsAsync([added]).GetAwaiter().GetResult();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitBmsRootsAsync(), "lr2-user-confirm");
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            string[] saved = new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking().ToArray();
            CollectionAssert.DoesNotContain(saved, original);
            CollectionAssert.Contains(saved, destination);
            CollectionAssert.Contains(saved, added);
            CollectionAssert.DoesNotContain(dialog.AvailableBMSDirectories, original);
            CollectionAssert.Contains((factory.CreatedLibrary ?? throw new AssertFailedException()).Lr2Synchronization.CaptureBmsDirectories().RequestedRoots.ToArray(), added);
        });
    }

    /// <summary>ユーザー登録Uを通常出力へ採用するときに競合候補を破棄し、Bへ移した後の新しいBMS編集・確定・取消でも旧Uを復活させません。</summary>
    /// <remarks>Uの一般ファイルを実移行後も残し、ディレクトリが消えたために偶然登録されない準備を避けます。</remarks>
    [TestMethod]
    public void Lr2UserRootAdoptionDiscardsCandidatesBeforeMovingOutputAndConfirmingBms()
    {
        WithInitializedLr2Owner((root, values) =>
        {
            string user = Directory.CreateDirectory(Path.Combine(root, "UserU")).FullName;
            string removed = Directory.CreateDirectory(Path.Combine(root, "RemovedUser")).FullName;
            File.WriteAllText(Path.Combine(user, "general.txt"), "keep general file");
            LR2Config initial = new(values.LR2ConfigXmlPath);
            initial.AddBMSSearchDirectories([user, removed]);
            initial.Save(initial.ConfigFilePath);
        }, (root, values, composition, shell, factory, failures, dialogs) =>
        {
            SettingsDialogViewModel dialog = shell.SettingDialog;
            Lr2ConfigOwner owner = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
            string songs = values.BMSInstallDir;
            string user = Path.Combine(root, "UserU");
            string removed = Path.Combine(root, "RemovedUser");
            string added = Directory.CreateDirectory(Path.Combine(root, "AddedUser")).FullName;
            CollectionAssert.Contains(dialog.ConfirmedBMSDirectories.ToArray(), user);
            dialog.BmsRootPathList.Remove(removed);
            dialog.AddBmsSearchRootPathsAsync([added]).GetAwaiter().GetResult();
            dialog.SelectedBmsSearchRootPath = added;
            dialog.LR2CustomFolderOutputDir = user;
            TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("normal"), "lr2-adopt-user-output");
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            Assert.AreEqual(user, values.LR2CustomFolderOutputBaseDir);
            CollectionAssert.AreEqual(new[] { songs, removed }, dialog.AvailableBMSDirectories);
            CollectionAssert.AreEqual(new[] { songs, removed }, dialog.ConfirmedBMSDirectories.ToArray());
            CollectionAssert.Contains(owner.Capture().RegisteredRoots.ToArray(), user);
            CollectionAssert.Contains(owner.CaptureSaved().RegisteredRoots.ToArray(), removed);
            CollectionAssert.DoesNotContain(owner.CaptureSaved().RegisteredRoots.ToArray(), added);
            CollectionAssert.DoesNotContain(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), added);

            string destination = Path.Combine(root, "OutputB");
            dialog.LR2CustomFolderOutputDir = destination;
            TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("normal"), "lr2-move-adopted-user-output");
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            Assert.AreEqual(destination, values.LR2CustomFolderOutputBaseDir);
            Assert.AreEqual("keep general file", File.ReadAllText(Path.Combine(user, "general.txt")));
            CollectionAssert.AreEqual(new[] { songs, removed }, dialog.AvailableBMSDirectories);
            CollectionAssert.AreEqual(new[] { songs, removed }, dialog.ConfirmedBMSDirectories.ToArray());
            CollectionAssert.DoesNotContain(owner.Capture().RegisteredRoots.ToArray(), user);
            CollectionAssert.DoesNotContain(owner.CaptureSaved().RegisteredRoots.ToArray(), user);
            CollectionAssert.Contains(owner.CaptureSaved().RegisteredRoots.ToArray(), removed);
            CollectionAssert.DoesNotContain(owner.CaptureSaved().RegisteredRoots.ToArray(), added);
            CollectionAssert.DoesNotContain(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), user);

            dialog.BmsRootPathList.Remove(removed);
            dialog.AddBmsSearchRootPathsAsync([added]).GetAwaiter().GetResult();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitBmsRootsAsync(), "lr2-confirm-new-user-roots");
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            CollectionAssert.AreEqual(new[] { songs, added }, dialog.ConfirmedBMSDirectories.ToArray());
            CollectionAssert.AreEqual(new[] { songs, added }, dialog.AvailableBMSDirectories);
            foreach (IReadOnlyList<string> roots in new[] { owner.Capture().RegisteredRoots, owner.CaptureSaved().RegisteredRoots,
                new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking() })
            {
                CollectionAssert.DoesNotContain(roots.ToArray(), user);
                CollectionAssert.DoesNotContain(roots.ToArray(), removed);
                CollectionAssert.Contains(roots.ToArray(), destination);
                CollectionAssert.Contains(roots.ToArray(), added);
            }
            string cancelled = Directory.CreateDirectory(Path.Combine(root, "CancelledUser")).FullName;
            dialog.BmsRootPathList.Remove(songs);
            dialog.AddBmsSearchRootPathsAsync([cancelled]).GetAwaiter().GetResult();
            dialog.CancelBmsRoots();
            CollectionAssert.AreEqual(new[] { songs, added }, dialog.AvailableBMSDirectories);
            CollectionAssert.DoesNotContain(dialog.AvailableBMSDirectories, user);
            CollectionAssert.DoesNotContain(owner.CaptureSaved().RegisteredRoots.ToArray(), cancelled);
            Assert.AreEqual("keep general file", File.ReadAllText(Path.Combine(user, "general.txt")));
        });
    }

    /// <summary>XMLだけBMS Bへ成功した後の実ROOT表G削除が公開Aを保ち、最新候補の両媒体再保存でBを公開してGを復活させません。</summary>
    [TestMethod]
    public void Lr2PartialBmsSaveRetainsDeletedManagedRootThroughExplicitRetry()
    {
        WithInitializedLr2Owner(null, (root, values, composition, shell, factory, failures, dialogs) =>
        {
            BMSPlaylist playlist = factory.CreatedPlaylist ?? throw new AssertFailedException();
            BMSLibrary library = factory.CreatedLibrary ?? throw new AssertFailedException();
            Lr2ConfigOwner owner = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
            string original = values.BMSInstallDir;
            string added = Directory.CreateDirectory(Path.Combine(root, "B")).FullName;
            var table = new BMSTable { playlist_id = 8281, name = "Managed", Output_dir = "Managed", is_root_folder = true, entries = [] };
            playlist.BMSTables.Add(table);
            playlist.CommitBMSTableHeadersToDB([table]);
            playlist.SyncCustomFolderOutputSearchRootsAfterSettingsChange(values.LR2CustomFolderOutputBaseDirRootType);
            string managed = Path.Combine(values.LR2CustomFolderOutputBaseDirRootType, "Managed");
            CollectionAssert.Contains(owner.Capture().RegisteredRoots.ToArray(), managed);
            SettingsDialogViewModel dialog = shell.SettingDialog;
            dialog.BmsRootPathList.Remove(original);
            dialog.AddBmsSearchRootPathsAsync([added]).GetAwaiter().GetResult();
            dialog.ReplacementBmsInstallDir = added;
            string settingsPath = Path.Combine(root, "settings", "user.config");
            using (File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            { TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitBmsRootsAsync(), "lr2-xml-only"); }
            Assert.AreEqual(1, failures.Count);
            CollectionAssert.Contains(owner.CaptureSaved().RegisteredRoots.ToArray(), added);
            CollectionAssert.Contains(owner.Capture().RegisteredRoots.ToArray(), original);
            CollectionAssert.DoesNotContain(owner.Capture().RegisteredRoots.ToArray(), added);
            var removal = new PlaylistRemovalWorkflowOwner(dialogs, () => playlist, () => library, () => owner, composition.CustomFolderOutputSettingsProvider);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(removal.RemoveTreeTableAsync(table, () => { }), "lr2-managed-remove");
            CollectionAssert.DoesNotContain(owner.CaptureSaved().RegisteredRoots.ToArray(), managed);
            CollectionAssert.DoesNotContain(owner.Capture().RegisteredRoots.ToArray(), managed);
            CollectionAssert.Contains(owner.CaptureSaved().RegisteredRoots.ToArray(), added);
            CollectionAssert.Contains(owner.Capture().RegisteredRoots.ToArray(), original);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitBmsRootsAsync(), "lr2-explicit-retry");
            Assert.AreEqual(1, failures.Count, string.Join(Environment.NewLine, failures));
            CollectionAssert.Contains(owner.Capture().RegisteredRoots.ToArray(), added);
            CollectionAssert.DoesNotContain(owner.Capture().RegisteredRoots.ToArray(), original);
            CollectionAssert.DoesNotContain(owner.Capture().RegisteredRoots.ToArray(), managed);
            CollectionAssert.DoesNotContain(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), managed);
        });
    }

    /// <summary>ライブラリ未構築で保存したBMS登録を、外部XML破損後の初回開始も同じ採用正本から引き継ぎます。</summary>
    [TestMethod]
    public void Lr2InitialSettingsSaveIsInheritedByFirstLibraryConstruction()
    {
        WithInitializedLr2Owner(null, (root, values, composition, shell, factory, failures, dialogs) =>
        {
            string added = Path.Combine(root, "InitialB");
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            CollectionAssert.Contains((factory.CreatedLibrary ?? throw new AssertFailedException()).Lr2Synchronization.CaptureBmsDirectories().RequestedRoots.ToArray(), added);
            CollectionAssert.Contains(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), added);
        }, (root, values, shell) =>
        {
            string added = Directory.CreateDirectory(Path.Combine(root, "InitialB")).FullName;
            shell.SettingDialog.AddBmsSearchRootPathsAsync([added]).GetAwaiter().GetResult();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(shell.SettingDialog.CommitBmsRootsAsync(), "lr2-initial-settings-save");
            CollectionAssert.Contains(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), added);
            File.WriteAllText(values.LR2ConfigXmlPath, "<external-broken");
        });
    }

    /// <summary>同じルート・XMLの再選択、取消、再表示は、外部XML内容とxml/xmhの新しい更新日時を採用しません。</summary>
    [TestMethod]
    public void Lr2AdoptedEnvironmentIgnoresExternalXmlAndTimestampChanges()
    {
        WithInitializedLr2Owner(null, (root, values, composition, shell, factory, failures, dialogs) =>
        {
            string adopted = values.LR2ConfigXmlPath;
            string alternative = Path.Combine(Path.GetDirectoryName(adopted) ?? throw new AssertFailedException(), "config.xmh");
            string externalRoot = Directory.CreateDirectory(Path.Combine(root, "External")).FullName;
            string external = "<config><player><id>external</id></player><jukebox><path>" + System.Security.SecurityElement.Escape(externalRoot) + "</path></jukebox></config>";
            File.WriteAllText(adopted, external);
            File.WriteAllText(alternative, external);
            File.SetLastWriteTimeUtc(adopted, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(alternative, new DateTime(2002, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            SettingsDialogViewModel dialog = shell.SettingDialog;
            dialog.LR2RootPath = values.LR2RootPath + Path.DirectorySeparatorChar;
            Assert.AreEqual(adopted, dialog.LR2ConfigXmlPath);
            Assert.IsFalse(dialog.HasEnvironmentChanges);
            dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), adopted).GetAwaiter().GetResult();
            dialog.CancelEnvironment();
            dialog.CancelBmsRoots();
            dialog.SetPresentationActive(false);
            dialog.RequestOpen();
            dialog.SetPresentationActive(true);
            CollectionAssert.DoesNotContain(dialog.AvailableBMSDirectories, externalRoot);
            CollectionAssert.Contains(dialog.AvailableBMSDirectories, values.BMSInstallDir);
            Assert.AreNotEqual("external", composition.ResolveLr2Config(adopted).Capture().PlayerId);
            Assert.AreEqual(adopted, dialog.LR2ConfigXmlPath);
            Assert.IsFalse(dialog.HasLr2PathSelectionError);
        });
    }

    private static void WithInitializedLr2Owner(Action<string, Settings>? prepare,
        Action<string, Settings, ApplicationComposition, MainWindowViewModel, StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory, List<Exception>, RecordingRootDialogService> run, Action<string, Settings, MainWindowViewModel>? beforeInitialize = null,
        Func<string, IDisposable>? ownedResource = null,
        Func<string, Settings, IChartFileScanner>? scannerFactory = null,
        Action<string, Settings, ApplicationComposition, MainWindowViewModel, StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory, List<Exception>, RecordingRootDialogService>? afterRestart = null,
        Func<ApplicationComposition, LibraryProfile, BMSLibrary, BMSPlaylist>? playlistFactory = null,
        Action<string, Settings, MainWindowViewModel>? beforeRestart = null)
    {
        string root = CreateTemporaryRoot();
        IDisposable? resource = null;
        Exception? primaryFailure = null;
        try
        {
            resource = ownedResource?.Invoke(root);
            Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "settings", "user.config")));
            prepare?.Invoke(root, values);
            values.ScanBmsFilesOnStartup = true;
            values.StartupSelectInstallPending = false;
            values.Save();
            int launches = afterRestart == null ? 1 : 2;
            for (int launch = 0; launch < launches; launch++)
            {
                if (launch > 0) { values = PortableSettingsPersistenceTests.OpenSettings(Path.Combine(root, "settings", "user.config")); }
                TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) };
                IChartFileScanner scanner = scannerFactory?.Invoke(root, values) ?? new SettingsChartFileScanner(CapturedChartFileScanner.FromFixture([], new Dictionary<string, IEnumerable<string>>(), [values.BMSInstallDir]));
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values), reportSettingsApplyFailure: failures.Add,
                    settingsDialogService: dialogs, uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationPathSnapshot: ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe")),
                    chartFileScanner: scanner, rootFileEnumerator: new FastRootFileEnumerator(), defaultBmsPlayerFactory: () => new RecordingPlaybackPlayer(),
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, [])
                { PlaylistCreator = playlistFactory == null ? null : (profile, library) => playlistFactory(composition, profile, library) };
                var shell = new MainWindowViewModel(composition, factory);
                try
                {
                    if (launch == 0) { beforeInitialize?.Invoke(root, values, shell); }
                    else { beforeRestart?.Invoke(root, values, shell); }
                    Task<bool> initialized = shell.InitializeAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initialized, "lr2-owner-startup");
                    Assert.IsTrue(initialized.GetAwaiter().GetResult());
                    if (launch == 0) { AwaitStartupBackgroundCompletionOnDispatcher(shell, "lr2-owner-background"); }
                    (launch == 0 ? run : afterRestart ?? throw new AssertFailedException())(root, values, composition, shell, factory, failures, dialogs);
                    if (launch > 0) { AwaitStartupBackgroundCompletionOnDispatcher(shell, "lr2-owner-background"); }
                }
                finally
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(shell.ShellShutdownWorkflow.RequestWindowCloseAsync(), "lr2-owner-cleanup");
                    shell.SettingDialog.Dispose();
                }
            });
            }
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            try
            {
                resource?.Dispose();
                // root はこのfixtureが生成した実パスです。別名ルートを再帰削除しません。
                Directory.Delete(root, recursive: true);
            }
            catch (Exception cleanupFailure) when (primaryFailure != null)
            { throw new AggregateException(primaryFailure, cleanupFailure); }
        }
    }
}
