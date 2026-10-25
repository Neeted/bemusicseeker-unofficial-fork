using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class SettingDialogEditCompletionTests
{
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
        Action<string, Settings, ApplicationComposition, MainWindowViewModel, StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory, List<Exception>, RecordingRootDialogService> run, Action<string, Settings, MainWindowViewModel>? beforeInitialize = null)
    {
        string root = CreateTemporaryRoot();
        try
        {
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
        finally { Directory.Delete(root, recursive: true); }
    }
}
