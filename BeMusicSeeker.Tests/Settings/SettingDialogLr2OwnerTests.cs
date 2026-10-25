using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    /// <summary>ルート出力への有効導入先の取込みを副作用前に拒否し、別の確定先への保存後は管理先を候補・保存から除外して通常導入と表削除を接続します。</summary>
    /// <remarks>専用ドライブ別名、実settings/XML/SQLite、標準compositionの生成・導入・削除を通し、候補表示だけの成功や存在しない導入先による偶然の拒否を区別します。</remarks>
    [TestMethod]
    public void Lr2DriveRootOutput_PreservesConfirmedInstallThroughGenerationInstallationAndTableRemoval()
    {
        OwnedSettingsDrive? drive = null;
        WithInitializedLr2Owner((root, values) =>
        {
            OwnedSettingsDrive owned = drive ?? throw new AssertFailedException("専用ドライブが未準備です。");
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
            OwnedSettingsDrive owned = drive ?? throw new AssertFailedException();
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
        }, ownedResource: root => drive = OwnedSettingsDrive.Create(root));
    }

    /// <summary>導入の進捗通知だけを終端判定から切り離し、実導入Taskを待つ代表用の受信先です。</summary>
    private sealed class SettingsInstallProgressWriter : IPackageInstallProgressWriter
    {
        public void TryWrite(PackageInstallProgressUpdate update) { }
    }

    /// <summary>この設定fixtureの一時実パスだけへ専用ドライブ別名を割り当て、全owner終端後に自分の定義だけを解除します。</summary>
    private sealed class OwnedSettingsDrive(string device, string target) : IDisposable
    {
        internal string Root => device + @"\";

        internal static OwnedSettingsDrive Create(string root)
        {
            string directory = Directory.CreateDirectory(Path.Combine(root, "ManagedVolume")).FullName;
            string target = @"\??\" + directory;
            foreach (char letter in "ZYXWVUTSRQPONMLKJIHGFED")
            {
                string device = letter + ":";
                if (ReadTargets(device).Length != 0 || Environment.GetLogicalDrives().Contains(device + @"\", StringComparer.OrdinalIgnoreCase)) { continue; }
                if (!DefineDosDevice(0x1 | 0x8, device, target)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                var owned = new OwnedSettingsDrive(device, target);
                try { owned.AssertOwned(); return owned; }
                catch { owned.Dispose(); throw; }
            }
            throw new AssertFailedException("専用ドライブ別名を取得できません。");
        }

        /// <summary>書込みを始める前に唯一の定義がケース所有の実パスであることを確認します。</summary>
        internal void AssertOwned() => CollectionAssert.AreEqual(new[] { target }, ReadTargets(device));

        public void Dispose()
        {
            // EXACT_MATCH_ON_REMOVE により、別の定義が追加されても自分の固有target以外を解除しません。
            if (!DefineDosDevice(0x1 | 0x2 | 0x4 | 0x8, device, target)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            CollectionAssert.DoesNotContain(ReadTargets(device), target);
        }

        private static string[] ReadTargets(string device)
        {
            char[] buffer = new char[32768];
            uint length = QueryDosDevice(device, buffer, buffer.Length);
            if (length != 0) { return new string(buffer, 0, checked((int)length)).Split('\0', StringSplitOptions.RemoveEmptyEntries); }
            int error = Marshal.GetLastWin32Error();
            if (error == 2) { return []; }
            throw new Win32Exception(error);
        }

        [DllImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DefineDosDevice(uint flags, string device, string target);

        [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint QueryDosDevice(string device, [Out] char[] target, int maximumLength);
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
        Func<string, IDisposable>? ownedResource = null)
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
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) };
                var scanner = new SettingsChartFileScanner(CapturedChartFileScanner.FromFixture([], new Dictionary<string, IEnumerable<string>>(), [values.BMSInstallDir]));
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values), reportSettingsApplyFailure: failures.Add,
                    settingsDialogService: dialogs, uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationPathSnapshot: ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe")),
                    chartFileScanner: scanner, rootFileEnumerator: new FastRootFileEnumerator(), defaultBmsPlayerFactory: () => new RecordingPlaybackPlayer(),
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, []);
                var shell = new MainWindowViewModel(composition, factory);
                try
                {
                    beforeInitialize?.Invoke(root, values, shell);
                    Task<bool> initialized = shell.InitializeAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initialized, "lr2-owner-startup");
                    Assert.IsTrue(initialized.GetAwaiter().GetResult());
                    AwaitStartupBackgroundCompletionOnDispatcher(shell, "lr2-owner-background");
                    run(root, values, composition, shell, factory, failures, dialogs);
                }
                finally
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(shell.ShellShutdownWorkflow.RequestWindowCloseAsync(), "lr2-owner-cleanup");
                    shell.SettingDialog.Dispose();
                }
            });
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
