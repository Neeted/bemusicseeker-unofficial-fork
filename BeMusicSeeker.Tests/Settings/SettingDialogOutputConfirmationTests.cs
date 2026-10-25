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
    /// <summary>有効な導入先の出力採用を実保存・XML・DB・配置変更前に拒否し、別の確定ルートへ導入先を変えた後は同じ操作と実出力が完了して初期化検査にも通ります。</summary>
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
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs,
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
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync("normal"), "reject-install-output-adoption");
                    StringAssert.Contains(dialogs.LastMessageText, string.Format(Resources.Validation_OutputBaseContainsInstallDirectoryFormat, install));
                    Assert.AreEqual(oldOutput, owner.SettingDialog.LR2CustomFolderOutputDir);
                    Assert.IsFalse(owner.SettingDialog.IsOutputSaveIncomplete);
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
                    owner.SettingDialog.LR2CustomFolderOutputDir = install;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync("normal"), "adopt-output-after-install-change");
                    Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.IsFalse(owner.SettingDialog.IsOutputSaveIncomplete);
                    Assert.AreEqual(install, PortableSettingsPersistenceTests.OpenSettings(settingsPath).LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(alternative, PortableSettingsPersistenceTests.OpenSettings(settingsPath).BMSInstallDir);
                    Assert.IsTrue(File.Exists(Path.Combine(install, "Table", "0000.lr2folder")));
                    Assert.IsFalse(File.Exists(oldFile));
                    Assert.IsTrue(owner.SettingDialog.CheckValidationForInitialization(out string error), error);
                }
                finally
                {
                    playlist.RequestShutdown("install-directory-output-fixture");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "install-directory-output-owner-cleanup");
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>追加基点の削除で既定先へ集まる表が包含する場合、設定・XML・DB・正本・ファイルを変更する前に拒否します。実設定portの接続と媒体保存順を確認します。</summary>
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
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                var config = new LR2Config(values.LR2ConfigXmlPath);
                Lr2ConfigOwner configOwner = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
                SetPrivateField(owner, "lr2config", configOwner);
                SetActiveLibraryProfile(owner, true);
                var parent = new BMSTable { playlist_id = 7196, name = "Parent" };
                var child = new BMSTable { playlist_id = 7197, name = "Child", Output_dir = @"Parent\Child", custom_folder_output_base_name = "Extra" };
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
                    string oldFile = Path.Combine(extra, "Parent", "Child", "0000.lr2folder");
                    Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
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

    /// <summary>二媒体の未了中は旧配置と表登録を保ち、明示修復後の実FS/DB移行・旧先整理失敗では新先を保ちます。同値は成功媒体と実投影を繰り返さず、候補修正と再表示後も成立出力を削除しません。</summary>
    [DataTestMethod]
    [DataRow(false, false, false)] // 通常A/Parent→BのXML保存失敗後、追加Extra/Parent/Child→Extra2も修正し両移行を完了します。
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)] // 実旧行DELETEだけをtriggerで失敗させ、成功先と媒体を保って未了DB整理だけを再試行します。
    [DataRow(false, true, true)] // 追加先の成立済み改名対応も、旧DB整理失敗で失いません。
    public void OutputConfirmation_RepairsPartialMediaAndCleanupWithoutRepeatingSuccessfulProjection(bool reuseOriginalDestination, bool renameAdditionalBase, bool failOldDatabaseCleanup)
    {
        RunPartialOutputRepair(reuseOriginalDestination, renameAdditionalBase, failOldDatabaseCleanup);
    }

    /// <summary>二媒体未了の修復候補をBusy・実登録検証で拒否しても元のTarget・完了段階を保ち、次の明示修復が受理済み配置で完了します。導入先保護と共用する候補復元の境界を確認します。</summary>
    [DataTestMethod]
    [DataRow("busy")]
    [DataRow("registration")]
    public void OutputConfirmation_RejectedRepairPreservesAcceptedPlacement(string rejection)
    {
        RunPartialOutputRepair(false, false, false, rejection);
    }

    private void RunPartialOutputRepair(bool reuseOriginalDestination, bool renameAdditionalBase, bool failOldDatabaseCleanup, string? rejectedRepair = null)
    {
        string root = CreateTemporaryRoot();
        try
        {
            string settingsPath = Path.Combine(root, "settings", "user.config");
            Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(settingsPath));
            bool changeAdditionalDuringRepair = !reuseOriginalDestination && !renameAdditionalBase && !failOldDatabaseCleanup;
            string outputDirectory = changeAdditionalDuringRepair ? "Parent" : "Table";
            string extra = Path.Combine(root, "Extra");
            string extraAfter = Path.Combine(root, "Extra2");
            string extraOldFile = Path.Combine(extra, "Parent", "Child", "0000.lr2folder");
            string extraNewFile = Path.Combine(extraAfter, "Parent", "Child", "0000.lr2folder");
            if (changeAdditionalDuringRepair)
            {
                Directory.CreateDirectory(Path.Combine(extra, "Parent", "Child"));
                File.WriteAllText(extraOldFile, "#TITLE extra old");
                values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([extra]);
            }
            string original = renameAdditionalBase ? Path.Combine(root, "OriginalAdditional") : values.LR2CustomFolderOutputBaseDir;
            if (renameAdditionalBase)
            {
                Directory.CreateDirectory(original);
                values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([original]);
            }
            values.Save();
            string destination = Path.Combine(root, "MovedOutput");
            string oldFile = Path.Combine(original, outputDirectory, "0000.lr2folder");
            string newFile = Path.Combine(destination, outputDirectory, "0000.lr2folder");
            Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
            File.WriteAllText(oldFile, "#TITLE old");
            PlaylistPersistenceRepository.EnsureSchema(values.LR2SongDBPath);
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) };
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                var config = new LR2Config(values.LR2ConfigXmlPath);
                Lr2ConfigOwner configOwner = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
                SetPrivateField(owner, "lr2config", configOwner);
                SetActiveLibraryProfile(owner, true);
                var table = new BMSTable
                {
                    playlist_id = 7194,
                    name = "Table",
                    symbol = "T",
                    Output_dir = outputDirectory,
                    custom_folder_output_base_name = renameAdditionalBase ? "OriginalAdditional" : string.Empty,
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                        & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                    entries = [BmsPlaylistTestSupport.CreateEntry(new string('a', 32), "Folder")],
                    Folder_order = ["Folder"]
                };
                var extraTable = new BMSTable
                {
                    playlist_id = 7193,
                    name = "Child",
                    Output_dir = Path.Combine("Parent", "Child"),
                    custom_folder_output_base_name = "Extra",
                    ignore_folder_output = table.ignore_folder_output,
                    entries = [BmsPlaylistTestSupport.CreateEntry(new string('c', 32), "Extra Folder")],
                    Folder_order = ["Extra Folder"]
                };
                var playlist = new TestBmsPlaylist(values.LR2SongDBPath, configOwner, null, null,
                    () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(values), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(values),
                    composition.CustomFolderOutputSettingsProvider,
                    BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(values.LR2SongDBPath, CustomFolderOutputPhysicalSurface.Empty),
                    mutationAdmission: composition.PlaylistOperationAdmission, settings: values)
                { BMSTables = new ObservableCollection<BMSTable>(changeAdditionalDuringRepair ? [table, extraTable] : [table]) };
                SetPrivateField(owner, "tables", playlist);
                owner.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);
                FileStream? oldBlocker = null;
                FileStream? xmlBlocker = null;
                FileStream? settingsBlocker = null;
                FileStream? outputBlocker = null;
                try
                {
                    if (!failOldDatabaseCleanup) { oldBlocker = new FileStream(oldFile, FileMode.Open, FileAccess.Read, FileShare.Read); }
                    xmlBlocker = new FileStream(values.LR2ConfigXmlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (renameAdditionalBase)
                    {
                        owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseDir = original;
                        owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseName = "MovedOutput";
                        owner.SettingDialog.RenameSelectedCustomFolderAdditionalOutputBaseDir();
                    }
                    else { owner.SettingDialog.LR2CustomFolderOutputDir = destination; }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync(renameAdditionalBase ? "additional" : "normal"), "output-partial-save");
                    Assert.AreEqual(1, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.IsTrue(owner.SettingDialog.IsOutputSaveIncomplete);
                    string SavedPath(Settings settings) => renameAdditionalBase
                        ? CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(settings.LR2CustomFolderAdditionalOutputBaseDirs)[0]
                        : settings.LR2CustomFolderOutputBaseDir;
                    Assert.AreEqual(original, SavedPath(values));
                    Assert.AreEqual(original, renameAdditionalBase
                        ? CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(composition.CustomFolderOutputSettingsProvider().LR2CustomFolderAdditionalOutputBaseDirs)[0]
                        : composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir);
                    Assert.AreEqual(destination, SavedPath(PortableSettingsPersistenceTests.OpenSettings(settingsPath)));
                    Assert.IsFalse(File.Exists(newFile), "必要媒体が未了の間は実移行を開始しません。");
                    Assert.AreEqual(renameAdditionalBase ? "OriginalAdditional" : string.Empty, table.custom_folder_output_base_name);
                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly())
                    { Assert.AreEqual(0L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'folder'"), "未了媒体の修復前に実出力用のfolder表も作りません。"); }
                    if (rejectedRepair != null)
                    {
                        // Extra/Parent/Childの削除候補は、受理済み通常B/Parentとの包含を実登録検査で拒否します。
                        byte[] beforeRejectedSettings = File.ReadAllBytes(settingsPath);
                        byte[] beforeRejectedXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
                        owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseDir = extra;
                        owner.SettingDialog.RemoveSelectedCustomFolderAdditionalOutputBaseDir();
                        if (rejectedRepair == "busy")
                        {
                            Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable held));
                            using (held) { TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync("additional"), "output-repair-busy-rejection"); }
                            Assert.AreEqual(1, failures.Count);
                        }
                        else
                        {
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync("additional"), "output-repair-registration-rejection");
                            Assert.AreEqual(2, failures.Count);
                            StringAssert.Contains(failures[^1].Message, Resources.Error_ManagedPlaylistOutputOverlap);
                        }
                        Assert.IsTrue(owner.SettingDialog.IsOutputSaveIncomplete);
                        Assert.AreEqual(destination, owner.SettingDialog.LR2CustomFolderOutputDir);
                        CollectionAssert.AreEqual(new[] { extra }, owner.SettingDialog.CustomFolderAdditionalOutputBaseDirList.ToArray());
                        CollectionAssert.AreEqual(beforeRejectedSettings, File.ReadAllBytes(settingsPath));
                        CollectionAssert.AreEqual(beforeRejectedXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                        Assert.AreEqual("#TITLE old", File.ReadAllText(oldFile));
                        Assert.IsFalse(File.Exists(newFile));
                        Assert.AreEqual("Extra", extraTable.custom_folder_output_base_name);
                    }
                    // 未受理の導入先採用で、媒体保存済みのTarget・改名対応を失わないことを後続の実retryで検出します。
                    byte[] pendingSettings = File.ReadAllBytes(settingsPath);
                    byte[] pendingXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
                    if (renameAdditionalBase)
                    {
                        owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseName = Path.GetFileName(values.BMSInstallDir);
                        owner.SettingDialog.RenameSelectedCustomFolderAdditionalOutputBaseDir();
                        CollectionAssert.AreEqual(new[] { destination }, owner.SettingDialog.CustomFolderAdditionalOutputBaseDirList.ToArray());
                    }
                    else
                    {
                        owner.SettingDialog.LR2CustomFolderOutputDir = values.BMSInstallDir;
                        Assert.AreEqual(destination, owner.SettingDialog.LR2CustomFolderOutputDir);
                    }
                    StringAssert.Contains(dialogs.LastMessageText, string.Format(Resources.Validation_OutputBaseContainsInstallDirectoryFormat, values.BMSInstallDir));
                    CollectionAssert.AreEqual(pendingSettings, File.ReadAllBytes(settingsPath));
                    CollectionAssert.AreEqual(pendingXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                    Assert.IsFalse(File.Exists(newFile));
                    owner.SettingDialog.ShowRecommUpdatedMsg = !owner.SettingDialog.ShowRecommUpdatedMsg;
                    xmlBlocker.Dispose(); xmlBlocker = null;
                    if (renameAdditionalBase && reuseOriginalDestination)
                    {
                        owner.SettingDialog.SetPresentationActive(false);
                        owner.SettingDialog.RequestOpen();
                        owner.SettingDialog.SetPresentationActive(true);
                        Assert.IsTrue(owner.SettingDialog.IsOutputSaveIncomplete);
                        owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseDir = original;
                        owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseName = "MovedOutput";
                        owner.SettingDialog.RenameSelectedCustomFolderAdditionalOutputBaseDir();
                    }
                    if (changeAdditionalDuringRepair)
                    {
                        owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseDir = extra;
                        owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseName = "Extra2";
                        owner.SettingDialog.RenameSelectedCustomFolderAdditionalOutputBaseDir();
                    }
                    // 同値はXMLだけを修復し、追加先も修正したケースは両媒体へ新候補を保存します。
                    if (failOldDatabaseCleanup)
                    {
                        using var db = new LR2SongDBExtended(values.LR2SongDBPath);
                        db.CreateTable<LR2SongDB.folder>();
                        db.InsertOrReplace(new LR2SongDB.folder { path = oldFile, title = "Old managed output", type = 2 }, typeof(LR2SongDB.folder));
                        string oldSql = oldFile.Replace("'", "''", StringComparison.Ordinal);
                        db.Execute($"CREATE TRIGGER reject_old_output_cleanup BEFORE DELETE ON folder WHEN OLD.path = '{oldSql}' BEGIN SELECT RAISE(ABORT, 'test_old_output_cleanup_failed'); END;");
                    }
                    if (!changeAdditionalDuringRepair) { settingsBlocker = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync(changeAdditionalDuringRepair ? "additional" : "pending"), "output-repair-required-media");
                    int expectedFailures = (failOldDatabaseCleanup ? 2 : 1) + (rejectedRepair == "registration" ? 1 : 0);
                    Assert.AreEqual(expectedFailures, failures.Count, string.Join(Environment.NewLine, failures));
                    if (failOldDatabaseCleanup) { StringAssert.Contains(failures[^1].ToString(), "test_old_output_cleanup_failed"); }
                    Assert.IsTrue(File.Exists(newFile));
                    Assert.AreEqual(!failOldDatabaseCleanup, File.Exists(oldFile));
                    Assert.AreEqual(destination, SavedPath(values));
                    Assert.AreEqual(renameAdditionalBase ? "MovedOutput" : string.Empty, table.custom_folder_output_base_name);
                    Assert.IsTrue(owner.SettingDialog.IsOutputSaveIncomplete, "実移行成立後の旧先整理失敗は新先を保って明示再試行を待ちます。");
                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly())
                    {
                        Assert.AreEqual(1L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", newFile));
                        if (failOldDatabaseCleanup) { Assert.AreEqual(1L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", oldFile)); }
                    }
                    oldBlocker?.Dispose(); oldBlocker = null;
                    settingsBlocker?.Dispose(); settingsBlocker = null;
                    string laterOldFile = Path.Combine(Path.GetDirectoryName(oldFile)!, "later-unmanaged.txt");
                    if (failOldDatabaseCleanup)
                    {
                        using var db = new LR2SongDBExtended(values.LR2SongDBPath);
                        db.Execute("DROP TRIGGER reject_old_output_cleanup;");
                        string newSql = newFile.Replace("'", "''", StringComparison.Ordinal);
                        db.Execute($"CREATE TRIGGER reject_completed_projection BEFORE INSERT ON folder WHEN NEW.path = '{newSql}' BEGIN SELECT RAISE(ABORT, 'test_completed_projection_repeated'); END;");
                        // 成立済み物理整理を繰り返すと、この後から現れた管理外入力を削除してしまいます。
                        Directory.CreateDirectory(Path.GetDirectoryName(laterOldFile)!);
                        File.WriteAllText(laterOldFile, "keep later input");
                        oldBlocker = new FileStream(laterOldFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                    owner.SettingDialog.ShowDiffBMSInstallConfirmMsg = !owner.SettingDialog.ShowDiffBMSInstallConfirmMsg;
                    if (failOldDatabaseCleanup)
                    {
                        using var db = new LR2SongDBExtended(values.LR2SongDBPath);
                        Assert.AreEqual(1L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", oldFile), "通常保存は可能になった旧DB行整理を暗黙再開しません。");
                    }
                    else { Assert.IsTrue(File.Exists(oldFile), "無関係な通常項目の保存は、可能になった旧先整理を暗黙再開しません。"); }
                    if (!failOldDatabaseCleanup && (reuseOriginalDestination || renameAdditionalBase))
                    {
                        if (renameAdditionalBase)
                        {
                            owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseName = reuseOriginalDestination ? "OriginalAdditional" : "RepairedAdditional";
                            owner.SettingDialog.RenameSelectedCustomFolderAdditionalOutputBaseDir();
                        }
                        else { owner.SettingDialog.LR2CustomFolderOutputDir = original; }
                    }
                    else
                    {
                        // 成立した両媒体と投影の再書込みを実FSで拒否し、未了整理だけが動くことを検査します。
                        settingsBlocker = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        xmlBlocker = new FileStream(values.LR2ConfigXmlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        outputBlocker = new FileStream(newFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync(failOldDatabaseCleanup || !reuseOriginalDestination && !renameAdditionalBase
                        ? "pending" : renameAdditionalBase ? "additional" : "normal"), "output-explicit-repair");
                    Assert.AreEqual(expectedFailures, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.IsFalse(owner.SettingDialog.IsOutputSaveIncomplete);
                    using (File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    { TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync("additional"), "output-completed-registration-noop"); }
                    Assert.AreEqual(expectedFailures, failures.Count, "完了した改名を次の同値操作へ残さず、追加保存・出力を始めません。");
                    string final = reuseOriginalDestination ? original : renameAdditionalBase && !failOldDatabaseCleanup ? Path.Combine(root, "RepairedAdditional") : destination;
                    Assert.AreEqual(final, SavedPath(values));
                    Assert.AreEqual(final, SavedPath(PortableSettingsPersistenceTests.OpenSettings(settingsPath)));
                    if (renameAdditionalBase)
                    {
                        Assert.AreEqual(failOldDatabaseCleanup ? "MovedOutput" : reuseOriginalDestination ? "OriginalAdditional" : "RepairedAdditional", table.custom_folder_output_base_name);
                        using LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
                        Assert.AreEqual(1L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", Path.Combine(final, outputDirectory, "0000.lr2folder")));
                    }
                    Assert.IsTrue(File.Exists(Path.Combine(final, outputDirectory, "0000.lr2folder")));
                    Assert.IsFalse(File.Exists(reuseOriginalDestination || (renameAdditionalBase && !failOldDatabaseCleanup) ? newFile : oldFile));
                    if (failOldDatabaseCleanup)
                    {
                        Assert.IsTrue(File.Exists(laterOldFile), "旧先の物理整理は成立済みなので、未了DB整理だけを実行します。");
                        using var db = new LR2SongDBExtended(values.LR2SongDBPath);
                        Assert.AreEqual(0L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", oldFile));
                        Assert.AreEqual(1L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", newFile));
                    }
                    if (changeAdditionalDuringRepair)
                    {
                        Assert.AreEqual("Extra2", extraTable.custom_folder_output_base_name);
                        Assert.IsTrue(File.Exists(extraNewFile));
                        Assert.IsFalse(File.Exists(extraOldFile));
                        using LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
                        Assert.AreEqual("Extra2", db.Table<BMSTable>().Single(item => item.playlist_id == extraTable.playlist_id).custom_folder_output_base_name);
                        Assert.AreEqual(1L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", extraNewFile));
                        Assert.AreEqual(0L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", extraOldFile));
                        CollectionAssert.Contains(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), extraAfter);
                    }
                    CollectionAssert.Contains(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), final);
                    xmlBlocker?.Dispose(); xmlBlocker = null;
                    string laterUserRoot = Path.Combine(root, "LaterUserRoot");
                    Directory.CreateDirectory(laterUserRoot);
                    composition.ResolveLr2Config(values.LR2ConfigXmlPath).SaveRoots(new Lr2RootChange([], [laterUserRoot]));
                    playlist.SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(values.LR2CustomFolderOutputBaseDirRootType,
                        CustomFolderOutputSettingsSnapshot.CreateCurrent(values));
                    CollectionAssert.Contains(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(), laterUserRoot,
                        "日常のroot同期も保持済み旧XDocumentで後の確定登録を上書きしません。");
                }
                finally
                {
                    outputBlocker?.Dispose(); settingsBlocker?.Dispose(); xmlBlocker?.Dispose(); oldBlocker?.Dispose();
                    playlist.RequestShutdown("output-settings-fixture");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "output-settings-owner-cleanup");
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// 追加先削除後の物理投影失敗と改名後のDB同期失敗で、確定ヘッダーと未了出力を保持します。
    /// 再表示・通常保存は暗黙修復せず、明示操作は同値または修正先Cを投影してA/Bの旧管理物だけを整理します。改名後のDB未了代表は復元拒否でも旧新入力と修復可能性を保持します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false, false)] // Extra削除後の新先物理書込みを共有拒否し、旧登録名がなくても再試行します。
    [DataRow(true, false)] // Extra改名後のfolder INSERTを拒否し、物理生成済み・DB未了を修復します。
    [DataRow(true, true)] // 未成立BからCへ修正し、通常先と包含名でも追加先の現在名を解決し、新旧receiptの成功共有でCの二重投影を防ぎます。
    public void OutputConfirmation_RepairsHeaderCommittedMigrationAfterProjectionFailure(bool renameAdditionalBase, bool correctPendingDestination)
    {
        string root = CreateTemporaryRoot();
        try
        {
            string settingsPath = Path.Combine(root, "settings", "user.config");
            Settings values = CreateValidCustomFolderSettings(root, [], PortableSettingsPersistenceTests.OpenSettings(settingsPath));
            string original = Path.Combine(root, "Extra");
            string destination = renameAdditionalBase ? Path.Combine(root, "MovedExtra") : values.LR2CustomFolderOutputBaseDir;
            string corrected = Path.Combine(root, "CorrectedExtra");
            Directory.CreateDirectory(original);
            values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([original]);
            values.Save();
            string outputDirectory = correctPendingDestination ? Path.Combine("Parent", "Child") : "Table";
            string oldFile = Path.Combine(original, outputDirectory, "0000.lr2folder");
            string newFile = Path.Combine(destination, outputDirectory, "0000.lr2folder");
            string correctedFile = Path.Combine(corrected, outputDirectory, "0000.lr2folder");
            string unmanagedFile = Path.Combine(original, "unmanaged.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
            File.WriteAllText(oldFile, "#TITLE old");
            File.WriteAllText(unmanagedFile, "keep unmanaged sibling");
            PlaylistPersistenceRepository.EnsureSchema(values.LR2SongDBPath);
            TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) };
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                    reportSettingsApplyFailure: failures.Add, settingsDialogService: dialogs,
                    uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
                var config = new LR2Config(values.LR2ConfigXmlPath);
                Lr2ConfigOwner configOwner = composition.ResolveLr2Config(values.LR2ConfigXmlPath);
                SetPrivateField(owner, "lr2config", configOwner);
                SetActiveLibraryProfile(owner, true);
                var table = new BMSTable
                {
                    playlist_id = 7195,
                    name = "Table",
                    symbol = "T",
                    Output_dir = outputDirectory,
                    custom_folder_output_base_name = "Extra",
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                        & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                    entries = [BmsPlaylistTestSupport.CreateEntry(new string('b', 32), "Folder")],
                    Folder_order = ["Folder"]
                };
                var normalTable = new BMSTable { playlist_id = 7196, name = "Parent", Output_dir = "Parent" };
                var playlist = new TestBmsPlaylist(values.LR2SongDBPath, configOwner, null, null,
                    () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(values), () => BeatorajaBmtOptionsSnapshot.CreateCurrent(values),
                    composition.CustomFolderOutputSettingsProvider,
                    BmsPlaylistTestSupport.CreateDeterministicLr2PlaylistFolderSynchronizationPort(values.LR2SongDBPath, CustomFolderOutputPhysicalSurface.Empty),
                    mutationAdmission: composition.PlaylistOperationAdmission, settings: values)
                { BMSTables = new ObservableCollection<BMSTable>(correctPendingDestination ? [table, normalTable] : [table]) };
                SetPrivateField(owner, "tables", playlist);
                owner.PlaylistWorkspace.RefreshPlaylistTreeTables(playlist);
                FileStream? outputBlocker = null;
                FileStream? settingsBlocker = null;
                try
                {
                    playlist.CommitBMSTableWithEntriesToDB(table);
                    if (correctPendingDestination) { playlist.CommitBMSTableHeadersToDB([normalTable]); }
                    using (var db = new LR2SongDBExtended(values.LR2SongDBPath))
                    {
                        db.CreateTable<LR2SongDB.folder>();
                        db.InsertOrReplace(new LR2SongDB.folder { path = oldFile, title = "Old managed output", type = 2 }, typeof(LR2SongDB.folder));
                        if (renameAdditionalBase)
                        {
                            string newSql = newFile.Replace("'", "''", StringComparison.Ordinal);
                            db.Execute($"CREATE TRIGGER reject_new_output_sync BEFORE INSERT ON folder WHEN NEW.path = '{newSql}' BEGIN SELECT RAISE(ABORT, 'test_new_output_sync_failed'); END;");
                        }
                    }
                    if (!renameAdditionalBase)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
                        File.WriteAllText(newFile, "blocked destination");
                        outputBlocker = new FileStream(newFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                    owner.SettingDialog.SetPresentationActive(true);
                    string stagedBms = Directory.CreateDirectory(Path.Combine(root, "StagedBms")).FullName;
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.AddBmsSearchRootPathsAsync([stagedBms]), "output-confirmation-unrelated-bms-candidate");
                    owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseDir = original;
                    if (renameAdditionalBase)
                    {
                        owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseName = "MovedExtra";
                        owner.SettingDialog.RenameSelectedCustomFolderAdditionalOutputBaseDir();
                    }
                    else { owner.SettingDialog.RemoveSelectedCustomFolderAdditionalOutputBaseDir(); }
                    bool closedDuringConfirmation = false;
                    dialogs.ConfirmationObserved = _ =>
                    {
                        owner.SettingDialog.SetPresentationActive(false);
                        owner.SettingDialog.SetPresentationActive(true);
                        closedDuringConfirmation = true;
                    };
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync("additional"), "output-header-committed-projection-failure");
                    dialogs.ConfirmationObserved = null;
                    Assert.IsTrue(closedDuringConfirmation, "出力先と改名対応は確認より先に捕捉し、通常閉鎖による候補リセットへ依存しません。");
                    Assert.AreEqual(1, failures.Count, string.Join(Environment.NewLine, failures));
                    if (renameAdditionalBase) { StringAssert.Contains(failures[0].ToString(), "test_new_output_sync_failed"); }
                    Assert.IsTrue(owner.SettingDialog.IsOutputSaveIncomplete);
                    string? expectedName = renameAdditionalBase ? "MovedExtra" : null;
                    Assert.AreEqual(expectedName, table.custom_folder_output_base_name);
                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly())
                    {
                        BMSTable persisted = db.Table<BMSTable>().Single(item => item.playlist_id == table.playlist_id);
                        Assert.AreEqual(expectedName, persisted.custom_folder_output_base_name,
                            "headerのDB確定は新先出力失敗でも戻さず、削除した追加先の参照はNULLにします。");
                        Assert.AreEqual(outputDirectory, persisted.Output_dir);
                        Assert.AreEqual(0L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", newFile));
                        Assert.AreEqual(1L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", oldFile));
                    }
                    string acceptedRegistrations = CustomFolderOutputBaseRegistry.SerializeBaseDirectories(renameAdditionalBase ? [destination] : []);
                    Assert.AreEqual(acceptedRegistrations, values.LR2CustomFolderAdditionalOutputBaseDirs);
                    CollectionAssert.AreEqual(renameAdditionalBase ? new[] { destination } : Array.Empty<string>(), owner.SettingDialog.CustomFolderAdditionalOutputBaseDirList.ToArray(),
                        "保存前の再表示で戻った旧候補は、保存採用時に新配置へ揃え、後段失敗でも戻しません。");
                    Assert.AreEqual(acceptedRegistrations, PortableSettingsPersistenceTests.OpenSettings(settingsPath).LR2CustomFolderAdditionalOutputBaseDirs);
                    Assert.IsTrue(File.Exists(oldFile));
                    Assert.IsTrue(File.Exists(newFile), "DB同期失敗は部分生成された物理出力を巻き戻しません。");
                    if (renameAdditionalBase && !correctPendingDestination)
                    {
                        byte[] pendingSettings = File.ReadAllBytes(settingsPath);
                        byte[] pendingXml = File.ReadAllBytes(values.LR2ConfigXmlPath);
                        byte[] oldOutput = File.ReadAllBytes(oldFile);
                        byte[] partialOutput = File.ReadAllBytes(newFile);
                        string selectedDestination = owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseDir;
                        Task<bool> restoreAllowed = owner.SettingDialog.CanStartRestoreBeforeExitAsync();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(restoreAllowed, "restore-refuses-pending-output-migration");
                        Assert.IsFalse(restoreAllowed.GetAwaiter().GetResult());
                        Assert.IsTrue(owner.SettingDialog.IsOutputSaveIncomplete);
                        Assert.AreEqual(selectedDestination, owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseDir);
                        Assert.AreEqual(acceptedRegistrations, values.LR2CustomFolderAdditionalOutputBaseDirs);
                        Assert.AreEqual(1, failures.Count, "復元拒否は出力修復を開始しません。");
                        CollectionAssert.AreEqual(pendingSettings, File.ReadAllBytes(settingsPath));
                        CollectionAssert.AreEqual(pendingXml, File.ReadAllBytes(values.LR2ConfigXmlPath));
                        CollectionAssert.AreEqual(oldOutput, File.ReadAllBytes(oldFile));
                        CollectionAssert.AreEqual(partialOutput, File.ReadAllBytes(newFile));
                        using LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly();
                        Assert.AreEqual(expectedName, db.Table<BMSTable>().Single(item => item.playlist_id == table.playlist_id).custom_folder_output_base_name);
                        Assert.AreEqual(0L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", newFile));
                        Assert.AreEqual(1L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", oldFile));
                    }
                    outputBlocker?.Dispose(); outputBlocker = null;
                    if (renameAdditionalBase)
                    {
                        using var db = new LR2SongDBExtended(values.LR2SongDBPath);
                        db.Execute("DROP TRIGGER reject_new_output_sync;");
                    }
                    owner.SettingDialog.SetPresentationActive(false);
                    owner.SettingDialog.RequestOpen();
                    owner.SettingDialog.SetPresentationActive(true);
                    owner.SettingDialog.ShowRecommUpdatedMsg = !owner.SettingDialog.ShowRecommUpdatedMsg;
                    Assert.IsTrue(owner.SettingDialog.IsOutputSaveIncomplete);
                    Assert.IsTrue(File.Exists(oldFile), "再表示・無関係な保存は未了整理を暗黙再開しません。");
                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly())
                    { Assert.AreEqual(0L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", newFile), "再表示・通常保存は未了DB同期も開始しません。"); }
                    if (correctPendingDestination)
                    {
                        owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseDir = destination;
                        owner.SettingDialog.SelectedCustomFolderAdditionalOutputBaseName = "CorrectedExtra";
                        owner.SettingDialog.RenameSelectedCustomFolderAdditionalOutputBaseDir();
                        using var db = new LR2SongDBExtended(values.LR2SongDBPath);
                        db.Execute("CREATE TABLE successful_projection_audit (path TEXT PRIMARY KEY);");
                        string correctedSql = correctedFile.Replace("'", "''", StringComparison.Ordinal);
                        db.Execute($"CREATE TRIGGER reject_repeated_corrected_projection BEFORE INSERT ON folder WHEN NEW.path = '{correctedSql}' AND EXISTS (SELECT 1 FROM successful_projection_audit WHERE path = NEW.path) BEGIN SELECT RAISE(ABORT, 'test_corrected_projection_repeated'); END;");
                        db.Execute($"CREATE TRIGGER record_corrected_projection AFTER INSERT ON folder WHEN NEW.path = '{correctedSql}' BEGIN INSERT INTO successful_projection_audit(path) VALUES (NEW.path); END;");
                    }
                    else
                    {
                        settingsBlocker = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.SettingDialog.CommitOutputSettingsAsync(correctPendingDestination ? "additional" : "pending"), "output-explicit-materialization-repair");
                    Assert.AreEqual(1, failures.Count, string.Join(Environment.NewLine, failures));
                    Assert.IsFalse(owner.SettingDialog.IsOutputSaveIncomplete);
                    if (renameAdditionalBase && !correctPendingDestination)
                    {
                        Task<bool> restoreAllowed = owner.SettingDialog.CanStartRestoreBeforeExitAsync();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(restoreAllowed, "restore-allows-repaired-output-migration");
                        Assert.IsTrue(restoreAllowed.GetAwaiter().GetResult());
                    }
                    string finalFile = correctPendingDestination ? correctedFile : newFile;
                    string finalRegistrations = correctPendingDestination
                        ? CustomFolderOutputBaseRegistry.SerializeBaseDirectories([corrected]) : acceptedRegistrations;
                    Assert.AreEqual(finalRegistrations, values.LR2CustomFolderAdditionalOutputBaseDirs);
                    Assert.AreEqual(finalRegistrations, PortableSettingsPersistenceTests.OpenSettings(settingsPath).LR2CustomFolderAdditionalOutputBaseDirs);
                    Assert.IsTrue(File.Exists(finalFile));
                    Assert.IsFalse(File.Exists(oldFile));
                    Assert.IsTrue(File.Exists(unmanagedFile));
                    if (correctPendingDestination) { Assert.IsFalse(File.Exists(newFile), "部分生成されたBの整理も新receiptが保持します。"); }
                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(values.LR2SongDBPath).OpenSongDbReadOnly())
                    {
                        Assert.AreEqual(correctPendingDestination ? "CorrectedExtra" : expectedName,
                            db.Table<BMSTable>().Single(item => item.playlist_id == table.playlist_id).custom_folder_output_base_name);
                        Assert.AreEqual(1L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", finalFile));
                        Assert.AreEqual(0L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", oldFile));
                        if (correctPendingDestination)
                        {
                            Assert.AreEqual(0L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?", newFile));
                            Assert.AreEqual(1L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM successful_projection_audit WHERE path = ?", correctedFile));
                        }
                    }
                    CollectionAssert.Contains(new LR2Config(values.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking(),
                        correctPendingDestination ? corrected : destination);
                }
                finally
                {
                    settingsBlocker?.Dispose(); outputBlocker?.Dispose();
                    playlist.RequestShutdown("output-header-migration-fixture");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "output-header-migration-owner-cleanup");
                    owner.SettingDialog.Dispose();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

}
