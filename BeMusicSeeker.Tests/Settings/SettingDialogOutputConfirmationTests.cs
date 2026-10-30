using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class SettingDialogEditCompletionTests
{
    /// <summary>有効な導入先の出力採用と確認中のP先着を実保存・XML・DB・配置変更前に拒否し、P解放だけでは再実行しません。別の確定ルートを導入先にした後の次の明示操作で実出力を完了し、初期化検査にも通ります。</summary>
    [TestMethod]
    public void OutputConfirmation_ProtectsInstallDirectoryUntilAnotherConfirmedRootIsSelected()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string settingsPath = Path.Combine(root, "settings", "user.config");
            string alternative = Path.Combine(root, "OtherSongs");
            Settings values = CreateValidCustomFolderSettings(root, [alternative], PortableSettingsPersistenceTests.OpenSettings(settingsPath));
            values.Save();
            PlaylistPersistenceRepository.EnsureSchema(values.LR2SongDBPath);
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) };
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs, playlistWorkspaceDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                Lr2ConfigOwner configOwner = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
                SetPrivateField(owner, "lr2config", configOwner);
                SetActiveLibraryProfile(owner, true);
                var table = new BMSTable
                {
                    playlist_id = 7198,
                    name = "Table",
                    symbol = "T",
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                    entries = [BmsPlaylistTestSupport.CreateEntry(new string('d', 32), "Folder")],
                    Folder_order = ["Folder"]
                };
                var playlist = new TestBmsPlaylist(values.LR2SongDBPath, configOwner, null, null,
                    () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(values), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(values),
                    composition.CustomFolderOutputSettingsProvider,
                    BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(values.LR2SongDBPath, CustomFolderOutputPhysicalSurface.Empty),
                    mutationAdmission: composition.PlaylistOperationAdmission, settings: values)
                { BMSTables = new ObservableCollection<BMSTable>([table]) };
                SetPrivateField(owner, "tables", playlist);
                owner.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);
                var confirmationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseConfirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task? rejectedOutput = null;
                IDisposable? confirmationLease = null;
                try
                {
                    playlist.CommitBMSTableWithEntriesToDB(table);
                    string oldOutput = values.LR2CustomFolderOutputBaseDir;
                    string install = values.BMSInstallDir;
                    string oldFile = Path.Combine(oldOutput, "Table", "0000.lr2folder");
                    Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
                    File.WriteAllText(oldFile, "preserved output");
                    byte[] oldSettings = File.ReadAllBytes(settingsPath);
                    byte[] oldXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
                    owner.SettingDialog.LR2CustomFolderOutputDir = install;
                    StringAssert.Contains(dialogs.LastMessageText, string.Format(Resources.Validation_OutputBaseContainsInstallDirectoryFormat, install));
                    Assert.AreEqual(oldOutput, owner.SettingDialog.LR2CustomFolderOutputDir);
                    Assert.AreEqual(0, failures.Count);
                    Assert.AreEqual(0, dialogs.ConfirmationCount);
                    CollectionAssert.AreEqual(oldSettings, File.ReadAllBytes(settingsPath));
                    CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                    Assert.AreEqual("preserved output", File.ReadAllText(oldFile));
                    Assert.IsFalse(Directory.Exists(Path.Combine(install, "Table")));
                    using (LR2SongDBExtended reader = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly())
                    {
                        Assert.AreEqual(1, reader.Table<BMSTable>().Count());
                        Assert.AreEqual(0L, reader.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'folder'"));
                    }
                    owner.SettingDialog.SelectBmsInstallDirectoryAsync(alternative).GetAwaiter().GetResult();
                    oldSettings = File.ReadAllBytes(settingsPath);
                    oldXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
                    LR2SongDBExtended.playlist previousHeader;
                    using (LR2SongDBExtended reader = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly())
                    { previousHeader = reader.Table<LR2SongDBExtended.playlist>().Single(); }
                    dialogs.ConfirmationObservedAsync = () => { confirmationEntered.TrySetResult(); return releaseConfirmation.Task; };
                    owner.SettingDialog.LR2CustomFolderOutputDir = install;
                    rejectedOutput = owner.SettingDialog.CommitOutputSettingsAsync("normal");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAny(confirmationEntered.Task, rejectedOutput), "output-adoption-confirmation-entered");
                    Assert.IsTrue(confirmationEntered.Task.IsCompletedSuccessfully);
                    Assert.IsFalse(rejectedOutput.IsCompleted);
                    Assert.IsTrue(composition.PlaylistOperationAdmission.TryEnter(out IDisposable acquired));
                    confirmationLease = acquired;
                    releaseConfirmation.TrySetResult();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(rejectedOutput, "output-adoption-P-rejected");
                    void AssertOutputUnchanged()
                    {
                        CollectionAssert.AreEqual(oldSettings, File.ReadAllBytes(settingsPath));
                        CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                        Assert.AreEqual(oldOutput, values.LR2CustomFolderOutputBaseDir);
                        Assert.AreEqual("preserved output", File.ReadAllText(oldFile));
                        Assert.IsFalse(Directory.Exists(Path.Combine(install, "Table")));
                        using LR2SongDBExtended reader = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
                        LR2SongDBExtended.playlist currentHeader = reader.Table<LR2SongDBExtended.playlist>().Single();
                        Assert.AreEqual(previousHeader.output_dir, currentHeader.output_dir);
                        Assert.AreEqual(previousHeader.custom_folder_output_base_name, currentHeader.custom_folder_output_base_name);
                        Assert.AreEqual(0L, reader.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'folder'"));
                    }
                    AssertOutputUnchanged();
                    Assert.AreEqual(1, dialogs.ConfirmationCount);
                    Assert.AreEqual(2, dialogs.MessageCount, "導入先保護とP受付拒否をそれぞれ一回通知します。");
                    Assert.AreEqual(oldOutput, owner.SettingDialog.LR2CustomFolderOutputDir);
                    confirmationLease.Dispose();
                    confirmationLease = null;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    AssertOutputUnchanged();
                    Assert.AreEqual(1, dialogs.ConfirmationCount, "P解放だけでは再確認・再実行を予約しません。");
                    owner.SettingDialog.LR2CustomFolderOutputDir = install;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync("normal"), "adopt-output-after-install-change");
                    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(install, PortableSettingsPersistenceTests.OpenSettings(settingsPath).LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(alternative, PortableSettingsPersistenceTests.OpenSettings(settingsPath).BMSInstallDir);
                    Assert.IsTrue(File.Exists(Path.Combine(install, "Table", "0000.lr2folder")));
                    Assert.IsFalse(File.Exists(oldFile));
                    Assert.IsTrue(owner.SettingDialog.CheckValidationForInitialization(out string error), error);
                }
                finally
                {
                    releaseConfirmation.TrySetResult();
                    confirmationLease?.Dispose();
                    if (rejectedOutput != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(rejectedOutput, "output-adoption-rejection-cleanup"); }
                    playlist.RequestShutdown("install-directory-output-fixture");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "install-directory-output-owner-cleanup");
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>追加基点の削除で既定先へ集まる表が同一配置となる場合、設定・XML・DB・正本・ファイルを変更する前に拒否します。実設定portの接続と媒体保存順を確認します。</summary>
    [TestMethod]
    public void OutputConfirmation_RejectsOverlapAfterAdditionalBaseRemovalBeforeSaving()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string settingsPath = Path.Combine(root, "settings", "user.config");
            Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(settingsPath));
            string extra = Path.Combine(root, "Extra");
            Directory.CreateDirectory(extra);
            values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([extra]);
            values.Save();
            PlaylistPersistenceRepository.EnsureSchema(values.LR2SongDBPath);
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) };
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs, playlistWorkspaceDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                var config = new LR2Config(values.LR2ConfigXmlPath);
                Lr2ConfigOwner configOwner = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
                SetPrivateField(owner, "lr2config", configOwner);
                SetActiveLibraryProfile(owner, true);
                var parent = new BMSTable { playlist_id = 7196, name = "Parent" };
                var child = new BMSTable { playlist_id = 7197, name = "Child", Output_dir = "Parent", custom_folder_output_base_name = "Extra" };
                var playlist = new TestBmsPlaylist(values.LR2SongDBPath, configOwner, null, null,
                    () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(values), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(values),
                    composition.CustomFolderOutputSettingsProvider,
                    BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(values.LR2SongDBPath, CustomFolderOutputPhysicalSurface.Empty),
                    mutationAdmission: composition.PlaylistOperationAdmission, settings: values)
                { BMSTables = new ObservableCollection<BMSTable>([parent, child]) };
                SetPrivateField(owner, "tables", playlist);
                owner.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);
                try
                {
                    playlist.CommitBMSTableHeadersToDB([parent, child]);
                    byte[] oldSettings = File.ReadAllBytes(settingsPath);
                    byte[] oldXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
                    string oldFile = Path.Combine(extra, "Parent", "0000.lr2folder");
                    Directory.CreateDirectory(Path.GetDirectoryName(oldFile) ?? throw new AssertFailedException());
                    File.WriteAllText(oldFile, "preserved output");
                    int notices = 0;
                    child.PropertyChanged += (_, _) => notices++;
                    owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseDir = extra;
                    owner.SettingDialog.RemoveSelectedCustomFolderAdditionalOutputBaseDir();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync("additional"), "reject-output-overlap");
                    Assert.AreEqual(1, failures.Count);
                    StringAssert.Contains(failures[0].Message, Resources.Error_ManagedPlaylistOutputOverlap);
                    CollectionAssert.AreEqual(oldSettings, File.ReadAllBytes(settingsPath));
                    CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                    Assert.AreEqual(0, notices);
                    Assert.AreEqual("Extra", child.custom_folder_output_base_name);
                    Assert.AreEqual("preserved output", File.ReadAllText(oldFile));
                    using LR2SongDBExtended reader = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
                    Assert.AreEqual("Extra", reader.Table<BMSTable>().Single(table => table.playlist_id == 7197).custom_folder_output_base_name);
                    Assert.IsFalse(Directory.Exists(Path.Combine(values.LR2CustomFolderOutputBaseDir, "Parent")));
                }
                finally
                {
                    playlist.RequestShutdown("output-overlap-fixture");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "output-overlap-owner-cleanup");
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>user.config失敗ではXMLへ進まず、部分保存後の同じ入力でも成功済みconfigを再保存します。Closeと別dirty保存は先行保存を戻さず、次の同区分・別区分選択は公開値と今回入力だけを保存します。</summary>
    [DataTestMethod]
    [DataRow("normal")]
    [DataRow("root")]
    [DataRow("additional")]
    public void OutputConfirmation_LatestSelectionResavesBothMediaAfterPartialFailure(string section)
    {
        string directory = CreateTemporaryRoot();
        try
        {
            string settingsPath = Path.Combine(directory, "settings", "user.config");
            Settings values = CreateValidCustomFolderSettings(directory, [], PortableSettingsPersistenceTests.OpenSettings(settingsPath));
            values.Save();
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add,
                    settingsDialogService: new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) },
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                SetActiveLibraryProfile(owner, false);
                SettingsDialogViewModel dialog = owner.SettingDialog;
                Lr2ConfigOwner xml = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
                string normal = values.LR2CustomFolderOutputBaseDir, root = values.LR2CustomFolderOutputBaseDirRootType;
                string first = Path.Combine(directory, "First"), latest = Path.Combine(directory, "Latest");
                void Choose(string path)
                {
                    if (section == "normal") { dialog.LR2CustomFolderOutputDir = path; }
                    else if (section == "root") { dialog.LR2CustomFolderAsRootOutputDir = path; }
                    else { dialog.AddCustomFolderAdditionalOutputBaseDir(path); }
                }
                string Read(Settings settings) => section == "normal" ? settings.LR2CustomFolderOutputBaseDir
                    : section == "root" ? settings.LR2CustomFolderOutputBaseDirRootType : settings.LR2CustomFolderAdditionalOutputBaseDirs;
                string Expected(string path) => section == "additional" ? CustomFolderOutputBaseRegistry.SerializeBaseDirectories([path]) : path;
                try
                {
                    byte[] oldXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
                    string old = Read(values);
                    if (section != "normal") { dialog.LR2CustomFolderOutputDir = Path.Combine(directory, "NormalCandidate"); }
                    if (section != "root") { dialog.LR2CustomFolderAsRootOutputDir = Path.Combine(directory, "RootCandidate"); }
                    Choose(first);
                    using (File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    { TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync(section), "output-config-failure"); }
                    Assert.AreEqual(1, failures.Count);
                    Assert.AreEqual(old, Read(values));
                    Assert.AreEqual(old, Read(PortableSettingsPersistenceTests.OpenSettings(settingsPath)));
                    CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                    Choose(first);
                    using (File.Open(values.LR2ConfigXmlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync(section), "output-partial-save");
                    }
                    Assert.AreEqual(2, failures.Count);
                    Assert.AreEqual(old, Read(values));
                    Settings partial = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
                    Assert.AreEqual(Expected(first), Read(partial));
                    if (section != "normal") { Assert.AreEqual(normal, partial.LR2CustomFolderOutputBaseDir); }
                    if (section != "root") { Assert.AreEqual(root, partial.LR2CustomFolderOutputBaseDirRootType); }
                    CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                    Choose(first);
                    using (File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    { TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync(section), "output-config-resave-failure"); }
                    Assert.AreEqual(3, failures.Count, "先行成功したuser.configも同じ入力で再保存します。");
                    Assert.AreEqual(old, Read(values));
                    CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                    values.TreeViewWidth = 321d; values.Save();
                    Assert.AreEqual(Expected(first), Read(PortableSettingsPersistenceTests.OpenSettings(settingsPath)));
                    byte[] saved = File.ReadAllBytes(settingsPath);
                    dialog.CloseCommand.Execute();
                    CollectionAssert.AreEqual(saved, File.ReadAllBytes(settingsPath));
                    CollectionAssert.AreEqual(oldXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                    Assert.AreEqual(normal, dialog.LR2CustomFolderOutputDir);
                    Assert.AreEqual(root, dialog.LR2CustomFolderAsRootOutputDir);
                    Assert.AreEqual(0, dialog.CustomFolderAdditionalOutputBaseDirList.Count);
                    if (section == "normal")
                    {
                        string other = Path.Combine(directory, "OtherAdditional");
                        dialog.AddCustomFolderAdditionalOutputBaseDir(other);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("additional"), "output-other-section-after-partial");
                        Settings otherSaved = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
                        Assert.AreEqual(first, otherSaved.LR2CustomFolderOutputBaseDir, "別区分の保存は、先行成功した未公開のnormal保存値を上書きしません。");
                        Assert.AreEqual(normal, values.LR2CustomFolderOutputBaseDir);
                        Assert.AreEqual(normal, composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir);
                        Assert.AreEqual(normal, composition.BmsLibraryOptionsProvider().LR2CustomFolderOutputBaseDir);
                        string expectedAdditional = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([other]);
                        Assert.AreEqual(expectedAdditional, otherSaved.LR2CustomFolderAdditionalOutputBaseDirs);
                        Assert.AreEqual(expectedAdditional, values.LR2CustomFolderAdditionalOutputBaseDirs);
                        Assert.AreEqual(expectedAdditional, composition.CustomFolderOutputSettingsProvider().LR2CustomFolderAdditionalOutputBaseDirs);
                        CollectionAssert.DoesNotContain(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), first);
                    }
                    Choose(latest);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync(section), "output-latest-save");
                    Assert.AreEqual(3, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(Expected(latest), Read(values));
                    Assert.AreEqual(Expected(latest), Read(PortableSettingsPersistenceTests.OpenSettings(settingsPath)));
                    CollectionAssert.DoesNotContain(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), first);
                    CollectionAssert.DoesNotContain(xml.Capture().RegisteredRoots.ToArray(), first);
                    if (section != "additional")
                    {
                        // 同値候補のsetter通知は保存せず、明示確定だけが共有違反を検出します。
                        Choose(latest);
                        Assert.AreEqual(3, failures.Count);
                        using (File.Open(values.LR2ConfigXmlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        { TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync(section), "output-explicit-same-save"); }
                        Assert.AreEqual(4, failures.Count);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync(section), "output-explicit-same-success");
                        Assert.AreEqual(Expected(latest), Read(PortableSettingsPersistenceTests.OpenSettings(settingsPath)));
                    }
                }
                finally
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "output-partial-owner-cleanup");
                    dialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>改名のDB失敗後、同じruntimeは旧公開名から内部空白を含む新名へ保存・出力します。fresh ownerはdiskの新定義だけを読み、保存されなかった旧名を過去の改名対応で補わず、表の再選択で解消します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OutputConfirmation_HeaderFailureUsesPublishedNamesOrFreshSavedDefinitions(bool reopenOwner)
    {
        string directory = CreateTemporaryRoot();
        try
        {
            string settingsPath = Path.Combine(directory, "settings", "user.config");
            string original = Path.Combine(directory, "Original"), failed = Path.Combine(directory, "Failed"), latest = Path.Combine(directory, "My Charts");
            Settings values = CreateValidCustomFolderSettings(directory, [original], PortableSettingsPersistenceTests.OpenSettings(settingsPath));
            values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([original]);
            values.Save();
            PlaylistPersistenceRepository.EnsureSchema(values.LR2SongDBPath);
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                (MainWindowViewModel Owner, TestBmsPlaylist Playlist, ApplicationComposition Composition) OpenOwner(Settings settings)
                {
                    var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) };
                    var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(settings),
                        reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs, playlistWorkspaceDialogService: dialogs,
                        uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                        applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                    MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                    Lr2ConfigOwner config = composition.ResolveLr2Config(settings.LR2ConfigXmlPath);
                    SetPrivateField(owner, "lr2config", config);
                    SetActiveLibraryProfile(owner, true);
                    var playlist = new TestBmsPlaylist(settings.LR2SongDBPath, config, null, null,
                        () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(settings), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(settings),
                        composition.CustomFolderOutputSettingsProvider,
                        BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(settings.LR2SongDBPath, CustomFolderOutputPhysicalSurface.Empty),
                        mutationAdmission: composition.PlaylistOperationAdmission, settings: settings)
                    { BMSTables = new ObservableCollection<BMSTable>(new PlaylistPersistenceRepository(settings.LR2SongDBPath).LoadPlaylistHeaders()) };
                    SetPrivateField(owner, "tables", playlist);
                    owner.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);
                    return (owner, playlist, composition);
                }
                void CloseOwner(MainWindowViewModel owner, TestBmsPlaylist playlist)
                {
                    playlist.RequestShutdown("output-header-owner");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "output-header-owner-cleanup");
                    owner.SettingDialog.Dispose();
                }
                (MainWindowViewModel Owner, TestBmsPlaylist Playlist, ApplicationComposition Composition) current = OpenOwner(values);
                try
                {
                    var table = new BMSTable
                    {
                        playlist_id = 7199,
                        name = "Table",
                        symbol = "T",
                        custom_folder_output_base_name = "Original",
                        ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                        entries = [BmsPlaylistTestSupport.CreateEntry(new string('d', 32), "Folder")],
                        Folder_order = ["Folder"]
                    };
                    current.Playlist.BMSTables.Add(table);
                    current.Playlist.CommitBMSTableWithEntriesToDB(table);
                    current.Owner.PlaylistWorkspace.RefreshPlaylistTreeTables(current.Playlist);
                    using (var db = new LR2SongDBExtended(values.LR2SongDBPath))
                    { db.Execute("CREATE TRIGGER fail_output_header BEFORE INSERT ON playlist WHEN NEW.playlist_id = 7199 BEGIN SELECT RAISE(ABORT, 'header failed'); END;"); }
                    SettingsDialogViewModel dialog = current.Owner.SettingDialog;
                    dialog.SelectedCustomFolderAdditionalOutputBaseDir = original;
                    dialog.SelectedCustomFolderAdditionalOutputBaseName = "Failed";
                    dialog.RenameSelectedCustomFolderAdditionalOutputBaseDir();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("additional"), "output-header-failure");
                    Assert.AreEqual(1, failures.Count);
                    Assert.IsInstanceOfType<SQLite.SQLiteException>(failures[0]);
                    Assert.AreEqual("Original", table.custom_folder_output_base_name);
                    CollectionAssert.AreEqual(new[] { original }, dialog.CustomFolderAdditionalOutputBaseDirList.ToArray());
                    Assert.AreEqual(CustomFolderOutputBaseRegistry.SerializeBaseDirectories([original]), values.LR2CustomFolderAdditionalOutputBaseDirs);
                    Assert.AreEqual(CustomFolderOutputBaseRegistry.SerializeBaseDirectories([failed]), PortableSettingsPersistenceTests.OpenSettings(settingsPath).LR2CustomFolderAdditionalOutputBaseDirs);
                    using (LR2SongDBExtended read = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly())
                    { Assert.AreEqual("Original", read.Table<BMSTable>().Single().custom_folder_output_base_name); }
                    using (var db = new LR2SongDBExtended(values.LR2SongDBPath)) { db.Execute("DROP TRIGGER fail_output_header;"); }
                    if (reopenOwner)
                    {
                        CloseOwner(current.Owner, current.Playlist);
                        values = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
                        current = OpenOwner(values);
                        dialog = current.Owner.SettingDialog;
                        table = current.Playlist.BMSTables.Single();
                    }
                    dialog.SelectedCustomFolderAdditionalOutputBaseDir = reopenOwner ? failed : original;
                    dialog.SelectedCustomFolderAdditionalOutputBaseName = "My Charts";
                    dialog.RenameSelectedCustomFolderAdditionalOutputBaseDir();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(dialog.CommitOutputSettingsAsync("additional"), "output-header-next-request");
                    Assert.AreEqual(1, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(reopenOwner ? "Original" : "My Charts", table.custom_folder_output_base_name);
                    Assert.AreEqual(CustomFolderOutputBaseRegistry.SerializeBaseDirectories([latest]), values.LR2CustomFolderAdditionalOutputBaseDirs);
                    Assert.AreEqual(CustomFolderOutputBaseRegistry.SerializeBaseDirectories([latest]), PortableSettingsPersistenceTests.OpenSettings(settingsPath).LR2CustomFolderAdditionalOutputBaseDirs);
                    Assert.AreEqual(!reopenOwner, File.Exists(Path.Combine(latest, "Table", "0000.lr2folder")));
                    Assert.IsFalse(Directory.Exists(Path.Combine(values.LR2CustomFolderOutputBaseDir, "Table")));
                    if (reopenOwner)
                    {
                        Task<PlaylistPropertyDialogViewModel> opening = current.Owner.PlaylistWorkspace.OpenPropertyDialogAsync(table);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(opening, "output-header-open-property");
                        PlaylistPropertyDialogViewModel property = opening.GetAwaiter().GetResult() ?? throw new AssertFailedException("Missing property dialog.");
                        using (property)
                        {
                            Assert.AreEqual("Original", property.custom_folder_output_base_option.BaseName);
                            property.custom_folder_output_base_option = property.OutputBaseOptions.Single(option => option.BaseName == "My Charts");
                            Task<PlaylistPropertyDialogOperationResult> saving = property.SaveAndApplyAsync();
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(saving, "output-header-reselect");
                            Assert.AreEqual(PlaylistPropertyDialogOperationResult.Completed, saving.GetAwaiter().GetResult());
                        }
                        Assert.IsTrue(File.Exists(Path.Combine(latest, "Table", "0000.lr2folder")));
                    }
                    using LR2SongDBExtended verify = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
                    Assert.AreEqual("My Charts", verify.Table<BMSTable>().Single().custom_folder_output_base_name);
                }
                finally
                {
                    using (var db = new LR2SongDBExtended(values.LR2SongDBPath)) { db.Execute("DROP TRIGGER IF EXISTS fail_output_header;"); }
                    CloseOwner(current.Owner, current.Playlist);
                }
            });
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>生成失敗と旧先のファイル・DB行整理失敗を区別し、採用済み設定を保ちます。整理失敗は対象と原因を通知して後続同期へ進み、次の明示選択で過去旧先を全回収しません。</summary>
    [DataTestMethod]
    [DataRow("generation")]
    [DataRow("file-cleanup")]
    [DataRow("row-cleanup")]
    public void OutputConfirmation_GenerationAndCleanupFailuresAllowLatestRequest(string failureKind)
    {
        bool cleanupOnly = failureKind != "generation", rowCleanup = failureKind == "row-cleanup";
        string root = CreateTemporaryRoot();
        try
        {
            string settingsPath = Path.Combine(root, "settings", "user.config");
            string alternative = Path.Combine(root, "OtherSongs");
            Settings values = CreateValidCustomFolderSettings(root, [alternative], PortableSettingsPersistenceTests.OpenSettings(settingsPath));
            values.Save();
            PlaylistPersistenceRepository.EnsureSchema(values.LR2SongDBPath);
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) };
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs, playlistWorkspaceDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                Lr2ConfigOwner configOwner = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
                SetPrivateField(owner, "lr2config", configOwner);
                SetActiveLibraryProfile(owner, true);
                var table = new BMSTable
                {
                    playlist_id = 7198,
                    name = "Table",
                    symbol = "T",
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                    entries = [BmsPlaylistTestSupport.CreateEntry(new string('d', 32), "Folder")],
                    Folder_order = ["Folder"]
                };
                var playlist = new TestBmsPlaylist(values.LR2SongDBPath, configOwner, null, null,
                    () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(values), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(values),
                    composition.CustomFolderOutputSettingsProvider,
                    BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(values.LR2SongDBPath, CustomFolderOutputPhysicalSurface.Empty),
                    mutationAdmission: composition.PlaylistOperationAdmission, settings: values)
                { BMSTables = new ObservableCollection<BMSTable>([table]) };
                SetPrivateField(owner, "tables", playlist);
                owner.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);
                var warnings = new List<string>();
                owner.PlaylistWorkspace.PlaylistOperationNotificationPresentationRequested += (_, request) =>
                    warnings.AddRange(request.Receipt.Notifications.Select(notification => notification.Message));
                var externalSync = new RecordingOutputSettingsSyncRuntime();
                SetPrivateField(owner.SettingDialog, "lr2SongDbSyncWorkflow", new Lr2SongDbSyncWorkflowOwner(externalSync));
                try
                {
                    playlist.CommitBMSTableWithEntriesToDB(table);
                    string oldFile = Path.Combine(values.LR2CustomFolderOutputBaseDir, "Table", "0000.lr2folder");
                    Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
                    File.WriteAllText(oldFile, "old output");
                    string selected = Path.Combine(root, "SelectedOutput"), latest = Path.Combine(root, "LatestOutput");
                    string selectedFile = Path.Combine(selected, "Table", "0000.lr2folder");
                    string blockedFile = cleanupOnly ? oldFile : selectedFile;
                    if (rowCleanup)
                    {
                        using var db = new LR2SongDBExtended(values.LR2SongDBPath);
                        db.CreateTable<LR2SongDB.folder>();
                        db.InsertOrReplace(new LR2SongDB.folder { path = oldFile, title = "old output", type = 2 }, typeof(LR2SongDB.folder));
                        db.Execute("CREATE TRIGGER fail_old_output_row BEFORE DELETE ON folder WHEN OLD.path = '" + oldFile.Replace("'", "''", StringComparison.Ordinal)
                            + "' BEGIN SELECT RAISE(ABORT, 'old-output-row-delete-fault'); END;");
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(blockedFile)!);
                        File.WriteAllText(blockedFile, "blocked output");
                    }
                    using (rowCleanup ? null : File.Open(blockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    {
                        owner.SettingDialog.LR2CustomFolderOutputDir = selected;
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync("normal"), "output-selected-failure");
                    }
                    Assert.AreEqual(cleanupOnly ? 0 : 1, failures.Count);
                    Assert.AreEqual(selected, values.LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(selected, PortableSettingsPersistenceTests.OpenSettings(settingsPath).LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(selected, composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir);
                    CollectionAssert.Contains(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), selected);
                    Assert.AreEqual(cleanupOnly, File.ReadAllText(selectedFile) != "blocked output");
                    Assert.AreEqual(!rowCleanup, File.Exists(oldFile));
                    Assert.AreEqual(cleanupOnly ? 1 : 0, externalSync.ExternalSyncCount, "旧先整理だけの失敗でも必要な外部同期へ進みます。");
                    if (cleanupOnly) { Assert.IsTrue(warnings.Any(warning => warning.Contains(Path.GetDirectoryName(oldFile)!, StringComparison.Ordinal))); }
                    if (rowCleanup)
                    {
                        Assert.IsTrue(warnings.Any(warning => warning.Contains("old-output-row-delete-fault", StringComparison.Ordinal)));
                        using LR2SongDBExtended read = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
                        Assert.AreEqual(1, read.Table<LR2SongDB.folder>().Count(row => row.path == oldFile));
                        Assert.AreEqual(1, read.Table<LR2SongDB.folder>().Count(row => row.path == selectedFile));
                        using var db = new LR2SongDBExtended(values.LR2SongDBPath);
                        db.Execute("DROP TRIGGER fail_old_output_row;");
                    }
                    owner.SettingDialog.LR2CustomFolderOutputDir = latest;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync("normal"), "output-latest-after-failure");
                    Assert.AreEqual(cleanupOnly ? 0 : 1, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(latest, values.LR2CustomFolderOutputBaseDir);
                    Assert.IsTrue(File.Exists(Path.Combine(latest, "Table", "0000.lr2folder")));
                    Assert.AreEqual(cleanupOnly ? 2 : 1, externalSync.ExternalSyncCount);
                    if (rowCleanup)
                    {
                        using LR2SongDBExtended read = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
                        Assert.AreEqual(1, read.Table<LR2SongDB.folder>().Count(row => row.path == oldFile), "次操作は過去の旧行整理を再開しません。");
                    }
                    else { Assert.IsTrue(File.Exists(oldFile), "次操作は過去の旧先回収を再開しません。"); }

                }
                finally
                {
                    using (var db = new LR2SongDBExtended(values.LR2SongDBPath)) { db.Execute("DROP TRIGGER IF EXISTS fail_old_output_row;"); }
                    playlist.RequestShutdown("install-directory-output-fixture");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "install-directory-output-owner-cleanup");
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }


    /// <summary>設定操作から必要外部同期への接続を観測し、生成と旧行整理の実DB検証には関与しません。</summary>
    private sealed class RecordingOutputSettingsSyncRuntime : ILr2SongDbSyncWorkflowRuntime
    {
        public bool IsLr2ModeEnabled => true;
        public bool IsLibraryAvailable => true;
        internal int ExternalSyncCount { get; private set; }

        public Task<bool> QueueAsync(string reason, bool force, bool prepareGeneratedData = false, bool allowIncompleteToQueue = true,
            LibraryFileInitializationResult? initializationResult = null, LibraryFileMutationCapability? capability = null,
            bool acceptedBackground = false, bool includeBuiltinGeneratedData = false, LibraryFileMutationCapability? playlistCapability = null,
            Lr2SongDbSyncPreparedDataSurface? preparedSurface = null, OperationProgressRequest? originatingRequest = null,
            BmsLibraryOptionsSnapshot? optionsSnapshot = null, Action<bool>? admissionResult = null)
            => throw new AssertFailedException("Output settings must use the external-folder continuation.");

        public Task SyncExternalFolderRowsForCustomFolderOutputBaseChangeAsync(string reason,
            LibraryFileMutationCapability? capability = null, LibraryFileMutationCapability? playlistCapability = null)
        {
            Assert.IsNotNull(capability);
            Assert.IsNotNull(playlistCapability);
            ExternalSyncCount++;
            return Task.CompletedTask;
        }
    }
}
