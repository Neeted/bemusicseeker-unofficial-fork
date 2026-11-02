using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.TestSupport.Wpf.Lr2SettingsOwnerTestFixture;

namespace BeMusicSeeker.Tests;

/// <summary>LR2設定と実ライブラリ・出力ownerの接続を検証します。</summary>
/// <remarks>共有 culture・theme・Bass 受付と composition 購読を使うため非並列にします。</remarks>
[TestClass]
[DoNotParallelize]
public sealed class SettingDialogLr2OwnerTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>初期化済みのケース専有媒体だけを返し、constructorでは領域を作りません。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>ケースのconstructorと既存field準備が成功した後に可変媒体を所有します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>ケース固有の処理と既存finallyが終わった後に、開始済みの所有媒体だけを回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    /// <summary>ルート出力への有効導入先の取込みを副作用前に拒否し、別の確定先への保存後は管理先を候補・保存から除外して通常導入と表削除を接続します。</summary>
    /// <remarks>専用ドライブ別名、実settings/XML/SQLite、標準compositionの生成・導入・削除を通し、候補表示だけの成功や存在しない導入先による偶然の拒否を区別します。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void Lr2DriveRootOutput_PreservesConfirmedInstallThroughGenerationInstallationAndTableRemoval()
    {
        OwnedTestDrive? drive = null;
        WithInitializedLr2Owner(CaseWorkspace, (root, values) =>
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
                entries = [PlaylistTestFixture.CreateEntry(new string('d', 32), "Folder")],
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
            Assert.AreEqual(owned.Root, TestSettingsFactory.OpenSettings(settingsPath).LR2CustomFolderOutputBaseDirRootType);
            Assert.AreEqual(songs, TestSettingsFactory.OpenSettings(settingsPath).BMSInstallDir);
            Assert.IsTrue(File.Exists(Path.Combine(managed, "0000.lr2folder")));
            Assert.IsFalse(File.Exists(oldFile));
            CollectionAssert.Contains(ReadRegisteredRoots(), managed);
            CollectionAssert.Contains(ReadFolderPaths(), Path.Combine(managed, "0000.lr2folder"));
            AssertCandidates();
            dialog.LR2CustomFolderOutputDir = Path.Combine(root, "UnconfirmedNormal");
            dialog.CloseCommand.Execute();
            dialog.CancelBmsRoots();
            AssertCandidates();
            Assert.AreEqual(Path.Combine(root, "NormalOutput"), TestSettingsFactory.OpenSettings(settingsPath).LR2CustomFolderOutputBaseDir);

            oldSettings = File.ReadAllBytes(settingsPath);
            oldXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
            Assert.IsTrue(Directory.Exists(managed));
            TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.SelectBmsInstallDirectoryAsync(managed), "drive-root-reject-managed-install");
            Assert.AreEqual(songs, values.BMSInstallDir);
            Assert.AreEqual(songs, TestSettingsFactory.OpenSettings(settingsPath).BMSInstallDir);
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
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath).OpenSongDbReadOnly())
            { Assert.AreEqual(0, db.Table<BMSTable>().Count(row => row.playlist_id == table.playlist_id)); }
            AssertSong();
            Assert.AreEqual("inside volume, outside table", File.ReadAllText(Path.Combine(owned.Root, "outside-table.txt")));
            Assert.AreEqual("outside volume", File.ReadAllText(Path.Combine(root, "outside-volume.txt")));
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));

            string[] ReadRegisteredRoots() => new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking().ToArray();
            string[] ReadFolderPaths()
            {
                using LR2SongDBExtended db = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath).OpenSongDbReadOnly();
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
                using LR2SongDBExtended db = new BmsLibraryDbGateway(composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), values.LR2SongDBPath).OpenSongDbReadOnly();
                Assert.AreEqual(installedChart, db.Table<LR2SongDB.song>().Single().path);
            }
        }, ownedResource: root => drive = OwnedTestDrive.Create(root, 'Z'));
    }

    /// <summary>導入の進捗通知だけを終端判定から切り離し、実導入Taskを待つ代表用の受信先です。</summary>
    private sealed class SettingsInstallProgressWriter : IPackageInstallProgressWriter
    {
        public void TryWrite(PackageInstallProgressUpdate update) { }
    }
}
