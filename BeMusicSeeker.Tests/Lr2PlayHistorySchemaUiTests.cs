using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Lr2PlayHistorySchemaUiTests
{
    [TestMethod]
    public void Lr2ScoreDbPathResolver_ResolvesExistingPlayerScoreDbOnly()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2ScoreDbPathResolver_" + Guid.NewGuid().ToString("N"));
        try
        {
            string scoreDirectoryPath = Path.Combine(directoryPath, "LR2files", "Database", "Score");
            Directory.CreateDirectory(scoreDirectoryPath);
            string scoreDbPath = Path.Combine(scoreDirectoryPath, "player1.db");
            File.WriteAllText(scoreDbPath, string.Empty);

            Assert.AreEqual(scoreDbPath, Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(directoryPath, "player1"));
            Assert.AreEqual(Path.Combine(scoreDirectoryPath, "missing.db"), Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(directoryPath, "missing"));
            Assert.AreEqual(scoreDbPath, Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, "player1"));
            Assert.IsNull(Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, "missing"));
            Assert.IsNull(Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, (string)null!));
            Assert.IsNull(Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, () => throw new InvalidOperationException("config")));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Lr2PlayHistorySchemaStatusPresentation_MapsActionsAndLabels()
    {
        Lr2PlayHistorySchemaStatusPresentation notInstalled = CreatePresentation(Lr2PlayHistorySchemaStatus.NotInstalled);
        Assert.IsTrue(notInstalled.CanInstall);
        Assert.IsFalse(notInstalled.CanRepair);
        Assert.AreEqual(Resources.Lr2_play_history_schema_status_not_installed, notInstalled.StatusText);

        Lr2PlayHistorySchemaStatusPresentation repairable = CreatePresentation(Lr2PlayHistorySchemaStatus.Repairable);
        Assert.IsFalse(repairable.CanInstall);
        Assert.IsTrue(repairable.CanRepair);
        Assert.AreEqual(Resources.Lr2_play_history_schema_status_repairable, repairable.StatusText);

        foreach (Lr2PlayHistorySchemaStatus status in new[]
        {
            Lr2PlayHistorySchemaStatus.Installed,
            Lr2PlayHistorySchemaStatus.ManualRepairRequired,
            Lr2PlayHistorySchemaStatus.Unreadable,
            Lr2PlayHistorySchemaStatus.SkippedProfile
        })
        {
            Lr2PlayHistorySchemaStatusPresentation presentation = CreatePresentation(status);
            Assert.IsFalse(presentation.CanInstall, status.ToString());
            Assert.IsFalse(presentation.CanRepair, status.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(presentation.StatusText), status.ToString());
        }
    }

    [TestMethod]
    public async Task SettingDialogViewModel_InstallOrRepair_BlockedOperationUsesOwnerDialog()
    {
        var owner = new MainWindowViewModel();
        var dialogs = new RecordingUiDialogService();
        var settingDialog = new MainWindowViewModel.SettingDialogViewModel(
            owner,
            reloadSettings: () => { },
            saveSettings: () => { },
            settingsEditSession: SettingsEditSession.CreateDefault(),
            reloadScoresOnly: () => Task.CompletedTask,
            reloadFileDiff: () => Task.CompletedTask,
            schemaDialogs: dialogs,
            invalidatePlayHistoryReadCache: _ => { });
        owner.SetStartupUiInteractionBlocked(true);
        try
        {
            await settingDialog.InstallOrRepairLr2PlayHistorySchemaAsync();

            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(0, dialogs.ConfirmationCount);
            StringAssert.Contains(dialogs.LastMessage, Resources.Msg_settings_apply_blocked_during_initialization);
        }
        finally
        {
            owner.SetStartupUiInteractionBlocked(false);
        }
    }

    [TestMethod]
    public async Task SettingDialogViewModel_Uninstall_TriggersOnlyPublishesStateAndReloads()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2SchemaUi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string scoreDbPath = Path.Combine(directoryPath, "score.db");
            CreateInstalledScoreDb(scoreDbPath);
            var owner = new MainWindowViewModel();
            SetPrivateField(owner, "hasActiveLibraryProfile", true);
            var dialogs = new RecordingUiDialogService
            {
                AcceptUninstall = true,
                SelectedUninstallMode = Lr2PlayHistorySchemaUninstallMode.TriggersOnly
            };
            int reloadCount = 0;
            string invalidationReason = string.Empty;
            var settingDialog = new MainWindowViewModel.SettingDialogViewModel(
                owner,
                reloadSettings: () => { },
                saveSettings: () => { },
                settingsEditSession: SettingsEditSession.CreateDefault(),
                reloadScoresOnly: () =>
                {
                    reloadCount++;
                    return Task.CompletedTask;
                },
                reloadFileDiff: () => Task.CompletedTask,
                schemaDialogs: dialogs,
                invalidatePlayHistoryReadCache: reason => invalidationReason = reason);
            SetPrivateField(settingDialog, "operationModeLR2DB", true);
            SetPrivateField(settingDialog, "lr2PlayHistoryScoreDbPath", scoreDbPath);

            await settingDialog.UninstallLr2PlayHistorySchemaAsync();

            Assert.AreEqual(1, dialogs.WindowCount);
            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(Resources.Msg_success_lr2_play_history_schema_uninstall, dialogs.LastMessage);
            Assert.AreEqual("lr2_play_history_schema_uninstall", invalidationReason);
            Assert.AreEqual(1, reloadCount);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, settingDialog.Lr2PlayHistorySchemaCheckResult.Status);
            using var verify = new SQLiteConnection(scoreDbPath);
            Assert.AreEqual(1, verify.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = ?;",
                Lr2PlayHistorySchemaService.PlayHistoryTableName));
            Assert.AreEqual(0, verify.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'trigger' AND name = ?;",
                Lr2PlayHistorySchemaService.ScoreInsertTriggerName));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task SettingDialogViewModel_Uninstall_TablesAndTriggersRemovesObjects()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2SchemaUi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string scoreDbPath = Path.Combine(directoryPath, "score.db");
            CreateInstalledScoreDb(scoreDbPath);
            var owner = new MainWindowViewModel();
            var dialogs = new RecordingUiDialogService
            {
                AcceptUninstall = true,
                SelectedUninstallMode = Lr2PlayHistorySchemaUninstallMode.TablesAndTriggers
            };
            int reloadCount = 0;
            string invalidationReason = string.Empty;
            var settingDialog = new MainWindowViewModel.SettingDialogViewModel(
                owner,
                reloadSettings: () => { },
                saveSettings: () => { },
                settingsEditSession: SettingsEditSession.CreateDefault(),
                reloadScoresOnly: () =>
                {
                    reloadCount++;
                    return Task.CompletedTask;
                },
                reloadFileDiff: () => Task.CompletedTask,
                schemaDialogs: dialogs,
                invalidatePlayHistoryReadCache: reason => invalidationReason = reason);
            SetPrivateField(settingDialog, "operationModeLR2DB", true);
            SetPrivateField(settingDialog, "lr2PlayHistoryScoreDbPath", scoreDbPath);

            await settingDialog.UninstallLr2PlayHistorySchemaAsync();

            Assert.AreEqual(1, dialogs.WindowCount);
            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(Resources.Msg_success_lr2_play_history_schema_uninstall, dialogs.LastMessage);
            Assert.AreEqual("lr2_play_history_schema_uninstall", invalidationReason);
            Assert.AreEqual(0, reloadCount);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.NotInstalled, settingDialog.Lr2PlayHistorySchemaCheckResult.Status);
            using var verify = new SQLiteConnection(scoreDbPath);
            Assert.AreEqual(0, verify.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM sqlite_master WHERE name LIKE 'bms_lr2_%' OR name LIKE 'idx_bms_lr2_%';"));
            Assert.AreEqual(1, verify.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'score';"));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task SettingDialogViewModel_Uninstall_CancelLeavesDurableAndLiveStateUnchanged()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2SchemaUi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string scoreDbPath = Path.Combine(directoryPath, "score.db");
            CreateInstalledScoreDb(scoreDbPath);
            var owner = new MainWindowViewModel();
            var dialogs = new RecordingUiDialogService { AcceptUninstall = false };
            int reloadCount = 0;
            int invalidationCount = 0;
            var settingDialog = new MainWindowViewModel.SettingDialogViewModel(
                owner,
                reloadSettings: () => { },
                saveSettings: () => { },
                settingsEditSession: SettingsEditSession.CreateDefault(),
                reloadScoresOnly: () =>
                {
                    reloadCount++;
                    return Task.CompletedTask;
                },
                reloadFileDiff: () => Task.CompletedTask,
                schemaDialogs: dialogs,
                invalidatePlayHistoryReadCache: _ => invalidationCount++);
            SetPrivateField(settingDialog, "operationModeLR2DB", true);
            SetPrivateField(settingDialog, "lr2PlayHistoryScoreDbPath", scoreDbPath);

            await settingDialog.UninstallLr2PlayHistorySchemaAsync();

            Assert.AreEqual(1, dialogs.WindowCount);
            Assert.AreEqual(0, dialogs.MessageCount);
            Assert.AreEqual(0, reloadCount);
            Assert.AreEqual(0, invalidationCount);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, settingDialog.Lr2PlayHistorySchemaCheckResult.Status);
            using var verify = new SQLiteConnection(scoreDbPath);
            Assert.AreEqual(1, verify.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'trigger' AND name = ?;",
                Lr2PlayHistorySchemaService.ScoreInsertTriggerName));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SettingDialog_Lr2PlayHistorySchemaUiUsesExplicitInstallBoundary()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string uninstallDialogXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Lr2PlayHistorySchemaUninstallDialog.xaml"));
        string viewModel = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string resources = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.resx"));
        string resourceCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.cs"));
        string playHistorySchemaUi = ExtractBetween(
            xaml,
            "<Grid Margin=\"20,2,10,4\" IsEnabled=\"{Binding IsChecked, ElementName=radioButtonUseLR2}\">",
            "<Grid Margin=\"10,0,0,4\" IsEnabled=\"{Binding IsBmsSearchRootEditorEnabled}\">");

        Assert.AreEqual(1, CountOccurrences(xaml, "Path=Resources.Lr2_play_history_schema_label, Mode=OneWay"));
        Assert.AreEqual(0, CountOccurrences(xaml, "Click=\"refreshLr2PlayHistorySchemaButtonClicked\""));
        Assert.AreEqual(0, CountOccurrences(xaml, "Click=\"installLr2PlayHistorySchemaButtonClicked\""));
        Assert.AreEqual(0, CountOccurrences(xaml, "Click=\"repairLr2PlayHistorySchemaButtonClicked\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Click=\"installOrRepairLr2PlayHistorySchemaButtonClicked\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Text=\"{Binding Lr2PlayHistorySchemaStatusText, Mode=OneWay}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "ToolTip=\"{Binding Lr2PlayHistorySchemaDetailText, Mode=OneWay}\""));
        StringAssert.Contains(xaml, "Content=\"{Binding Lr2PlayHistorySchemaInstallOrRepairButtonText, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "IsEnabled=\"{Binding CanInstallOrRepairLr2PlayHistorySchema, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "DataTrigger Binding=\"{Binding CanInstallOrRepairLr2PlayHistorySchema, Mode=OneWay}\" Value=\"True\"");
        StringAssert.Contains(xaml, "Value=\"{DynamicResource App.WarningTextBrush}\"");
        StringAssert.Contains(xaml, "Click=\"uninstallLr2PlayHistorySchemaButtonClicked\"");
        StringAssert.Contains(xaml, "IsEnabled=\"{Binding CanUninstallLr2PlayHistorySchema, Mode=OneWay}\"");
        StringAssert.Contains(uninstallDialogXaml, "Path=Resources.Lr2_play_history_schema_uninstall_title, Mode=OneWay");
        StringAssert.Contains(uninstallDialogXaml, "Path=Resources.Lr2_play_history_schema_uninstall_desc, Mode=OneWay");
        StringAssert.Contains(uninstallDialogXaml, "Path=Resources.Lr2_play_history_schema_uninstall_score_db, Mode=OneWay");
        StringAssert.Contains(uninstallDialogXaml, "Path=Resources.Lr2_play_history_schema_uninstall_mode, Mode=OneWay");
        StringAssert.Contains(uninstallDialogXaml, "Path=Resources.Lr2_play_history_schema_uninstall_triggers_only, Mode=OneWay");
        StringAssert.Contains(uninstallDialogXaml, "Path=Resources.Lr2_play_history_schema_uninstall_tables_and_triggers, Mode=OneWay");
        StringAssert.Contains(uninstallDialogXaml, "Path=Resources.Lr2_play_history_schema_uninstall_warning, Mode=OneWay");
        Assert.IsTrue(
            xaml.IndexOf("Lr2_play_history_schema_label", StringComparison.Ordinal)
            < xaml.IndexOf("Lr2_song_db_sync_data_resync", StringComparison.Ordinal));
        StringAssert.Contains(playHistorySchemaUi, "<RowDefinition Height=\"20\" />");
        StringAssert.Contains(playHistorySchemaUi, "Grid.Row=\"0\" Grid.Column=\"1\" Width=\"270\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\" TextAlignment=\"Center\"");
        StringAssert.Contains(playHistorySchemaUi, "<Run Text=\": \" />");
        StringAssert.Contains(playHistorySchemaUi, "Grid.Row=\"1\" Grid.Column=\"0\" Width=\"190\" Height=\"24\" HorizontalAlignment=\"Center\"");
        StringAssert.Contains(playHistorySchemaUi, "Grid.Row=\"1\" Grid.Column=\"1\" Width=\"130\" Height=\"24\" HorizontalAlignment=\"Center\"");
        Assert.IsTrue(
            playHistorySchemaUi.IndexOf("Grid.Row=\"1\" Grid.Column=\"0\"", StringComparison.Ordinal)
            < playHistorySchemaUi.IndexOf("Grid.Row=\"1\" Grid.Column=\"1\"", StringComparison.Ordinal));

        string installHandler = ExtractBetween(
            codeBehind,
            "private async void installOrRepairLr2PlayHistorySchemaButtonClicked",
            "private async void uninstallLr2PlayHistorySchemaButtonClicked");
        string normalizedInstallHandler = installHandler.Replace("\r\n", "\n");
        StringAssert.Contains(installHandler, "await settingDialogViewModel.InstallOrRepairLr2PlayHistorySchemaAsync();");
        StringAssert.Contains(normalizedInstallHandler, "settingDialogOperationGrid.IsEnabled = false;");
        Assert.IsFalse(installHandler.Contains("InstallOrRepairLr2PlayHistorySchemaCore"));
        Assert.IsFalse(installHandler.Contains("ApplyLr2PlayHistorySchemaCheckResult"));
        Assert.IsFalse(installHandler.Contains("InvalidatePlayHistoryReadCache"));
        Assert.IsFalse(installHandler.Contains("ReloadScoresOnlyAsync"));
        Assert.IsTrue(
            installHandler.IndexOf("settingDialogOperationGrid.IsEnabled = false;", StringComparison.Ordinal)
            < installHandler.IndexOf("await settingDialogViewModel.InstallOrRepairLr2PlayHistorySchemaAsync();", StringComparison.Ordinal));
        StringAssert.Contains(viewModel, "internal async Task InstallOrRepairLr2PlayHistorySchemaAsync()");
        StringAssert.Contains(viewModel, "Msg_confirm_lr2_play_history_schema_install_or_repair");
        StringAssert.Contains(viewModel, "await RefreshLr2PlayHistorySchemaStatusAsync(force: true);");
        StringAssert.Contains(viewModel, "InstallOrRepairLr2PlayHistorySchemaCore(scoreDbPath, isLr2LinkedProfile)");
        StringAssert.Contains(viewModel, "invalidatePlayHistoryReadCache(\"lr2_play_history_schema_install_or_repair\")");
        StringAssert.Contains(viewModel, "await ReloadScoresOnlyAsync();");
        string installOwnerCommand = ExtractBetween(
            viewModel,
            "internal async Task InstallOrRepairLr2PlayHistorySchemaAsync()",
            "internal async Task UninstallLr2PlayHistorySchemaAsync()");
        Assert.IsTrue(
            installOwnerCommand.IndexOf("ApplyLr2PlayHistorySchemaCheckResult(result);", StringComparison.Ordinal)
            < installOwnerCommand.IndexOf("await ReloadScoresOnlyAsync();", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Contains("public async Task InstallOrRepairLr2PlayHistorySchemaAsync"));
        string uninstallHandler = ExtractBetween(
            codeBehind,
            "private async void uninstallLr2PlayHistorySchemaButtonClicked",
            "private async void detailTabItemRestoreButtonClicked");
        StringAssert.Contains(uninstallHandler, "await settingDialogViewModel.UninstallLr2PlayHistorySchemaAsync();");
        StringAssert.Contains(uninstallHandler, "settingDialogOperationGrid.IsEnabled = false;");
        Assert.IsFalse(uninstallHandler.Contains("RefreshLr2PlayHistorySchemaStatusAsync"));
        Assert.IsFalse(uninstallHandler.Contains("ShowWindowAsync"));
        Assert.IsFalse(uninstallHandler.Contains("UninstallLr2PlayHistorySchemaCore"));
        Assert.IsFalse(uninstallHandler.Contains("ApplyLr2PlayHistorySchemaCheckResult"));
        Assert.IsFalse(uninstallHandler.Contains("InvalidatePlayHistoryReadCache"));
        Assert.IsFalse(uninstallHandler.Contains("ReloadScoresOnlyAsync"));
        Assert.IsFalse(codeBehind.Contains("ShouldRefreshLr2PlayHistorySchemaStatus"));
        Assert.IsFalse(codeBehind.Contains("IsExpectedLr2PlayHistorySchemaUninstallResult"));
        Assert.IsFalse(codeBehind.Contains("viewModel.ReloadScoresOnly();"));
        StringAssert.Contains(codeBehind, "private void SettingDialogIsVisibleChanged");
        Assert.IsFalse(codeBehind.Contains("await RefreshLr2PlayHistorySchemaStatusAsync(settingDialogViewModel, force: false);"));
        StringAssert.Contains(codeBehind, "settingDialogViewModel.RefreshLr2PlayHistorySchemaStatusPresentation();");
        Assert.IsFalse(codeBehind.Contains("new Lr2PlayHistorySchemaUninstallDialog"));

        StringAssert.Contains(viewModel, "internal async Task UninstallLr2PlayHistorySchemaAsync()");
        string uninstallOwnerCommand = ExtractBetween(
            viewModel,
            "internal async Task UninstallLr2PlayHistorySchemaAsync()",
            "private async Task RefreshLr2PlayHistorySchemaStatusAsync");
        StringAssert.Contains(uninstallOwnerCommand, "ShowWindowAsync");
        StringAssert.Contains(uninstallOwnerCommand, "UninstallLr2PlayHistorySchemaCore(scoreDbPath, isLr2LinkedProfile, uninstallMode)");
        StringAssert.Contains(uninstallOwnerCommand, "invalidatePlayHistoryReadCache(\"lr2_play_history_schema_uninstall\")");
        Assert.IsTrue(
            uninstallOwnerCommand.IndexOf("ApplyLr2PlayHistorySchemaCheckResult(result);", StringComparison.Ordinal)
            < uninstallOwnerCommand.IndexOf("invalidatePlayHistoryReadCache(\"lr2_play_history_schema_uninstall\")", StringComparison.Ordinal));
        Assert.IsTrue(
            uninstallOwnerCommand.IndexOf("invalidatePlayHistoryReadCache(\"lr2_play_history_schema_uninstall\")", StringComparison.Ordinal)
            < uninstallOwnerCommand.IndexOf("await ReloadScoresOnlyAsync();", StringComparison.Ordinal));

        StringAssert.Contains(viewModel, "Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(startupSettings.LR2RootPath, lr2config.GetPlayerId)");
        StringAssert.Contains(viewModel, "Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(ApplicationSettings.LR2RootPath, () => lr2config?.GetPlayerId())");
        StringAssert.Contains(viewModel, "new Lr2PlayHistorySchemaService().Check(scoreDbPath, isLr2LinkedProfile)");
        StringAssert.Contains(viewModel, "new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, isLr2LinkedProfile)");
        StringAssert.Contains(viewModel, "new Lr2PlayHistorySchemaService().Uninstall(scoreDbPath, isLr2LinkedProfile, uninstallMode)");
        StringAssert.Contains(viewModel, "lr2PlayHistorySchemaCheckResult == null || CanInstallLr2PlayHistorySchema || CanRepairLr2PlayHistorySchema");
        StringAssert.Contains(viewModel, "play_history_schema_");

        foreach (string key in RequiredResourceKeys)
        {
            StringAssert.Contains(resources, "name=\"" + key + "\"");
            StringAssert.Contains(resourceCode, "public static string " + key);
            foreach (string languagePath in Directory.GetFiles(Path.Combine(root, "lang"), "*.json"))
            {
                StringAssert.Contains(File.ReadAllText(languagePath), "\"" + key + "\"");
            }
        }
        Assert.IsFalse(resources.Contains("name=\"Lr2_play_history_schema_refresh\""));
        Assert.IsFalse(resourceCode.Contains("public static string Lr2_play_history_schema_refresh"));
        foreach (string languagePath in Directory.GetFiles(Path.Combine(root, "lang"), "*.json"))
        {
            Assert.IsFalse(File.ReadAllText(languagePath).Contains("\"Lr2_play_history_schema_refresh\""));
        }
    }

    private static Lr2PlayHistorySchemaStatusPresentation CreatePresentation(Lr2PlayHistorySchemaStatus status)
    {
        return Lr2PlayHistorySchemaStatusPresentation.Create(new Lr2PlayHistorySchemaCheckResult
        {
            Status = status,
            Message = status.ToString()
        });
    }

    private static void CreateInstalledScoreDb(string scoreDbPath)
    {
        using (var db = new SQLiteConnection(scoreDbPath))
        {
            db.CreateTable<LR2ScoreDB.score>();
            db.CreateTable<LR2ScoreDB.player>();
        }
        new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }

    private static string FindRepositoryRoot()
    {
        string? directoryPath = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directoryPath))
        {
            string currentDirectoryPath = directoryPath!;
            if (File.Exists(Path.Combine(currentDirectoryPath, "BeMusicSeeker.sln")))
            {
                return currentDirectoryPath;
            }
            DirectoryInfo? parent = Directory.GetParent(currentDirectoryPath);
            directoryPath = parent?.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static int CountOccurrences(string value, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private sealed class RecordingUiDialogService : IUiDialogService
    {
        internal int MessageCount { get; private set; }

        internal int ConfirmationCount { get; private set; }

        internal int WindowCount { get; private set; }

        internal bool AcceptUninstall { get; set; }

        internal Lr2PlayHistorySchemaUninstallMode SelectedUninstallMode { get; set; }

        internal string LastMessage { get; private set; } = string.Empty;

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageCount++;
            LastMessage = request.MessageBoxText;
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationCount++;
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window
        {
            WindowCount++;
            if (!AcceptUninstall)
            {
                return Task.FromResult(new UiWindowDialogResult<TResult>(UiDialogStatus.CancelledByUser));
            }
            return Task.FromResult(new UiWindowDialogResult<TResult>(
                UiDialogStatus.Accepted,
                (TResult)(object)SelectedUninstallMode,
                dialogResult: true));
        }

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UiFilePickerResult(UiDialogStatus.CancelledByUser));
        }

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UiFolderPickerResult(UiDialogStatus.CancelledByUser));
        }

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UiSaveFilePickerResult(UiDialogStatus.CancelledByUser));
        }

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UiProgressResult(UiDialogStatus.CancelledByUser));
        }
    }

    private static string ExtractBetween(string text, string start, string end)
    {
        int startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(startIndex >= 0, "start marker not found: " + start);
        int endIndex = text.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.IsTrue(endIndex > startIndex, "end marker not found: " + end);
        return text.Substring(startIndex, endIndex - startIndex);
    }

    private static readonly string[] RequiredResourceKeys =
    [
        "Lr2_play_history_schema_label",
        "Lr2_play_history_schema_install",
        "Lr2_play_history_schema_repair",
        "Lr2_play_history_schema_install_or_repair",
        "Lr2_play_history_schema_uninstall",
        "Lr2_play_history_schema_uninstall_title",
        "Lr2_play_history_schema_uninstall_desc",
        "Lr2_play_history_schema_uninstall_score_db",
        "Lr2_play_history_schema_uninstall_mode",
        "Lr2_play_history_schema_uninstall_triggers_only",
        "Lr2_play_history_schema_uninstall_tables_and_triggers",
        "Lr2_play_history_schema_uninstall_warning",
        "Lr2_play_history_schema_status_unknown",
        "Lr2_play_history_schema_status_installed",
        "Lr2_play_history_schema_status_not_installed",
        "Lr2_play_history_schema_status_repairable",
        "Lr2_play_history_schema_status_manual_repair_required",
        "Lr2_play_history_schema_status_unreadable",
        "Lr2_play_history_schema_status_skipped_profile",
        "Lr2_play_history_schema_message_skipped_profile",
        "Lr2_play_history_schema_message_score_db_path_not_configured",
        "Lr2_play_history_schema_message_score_db_file_not_found",
        "Lr2_play_history_schema_message_install_or_repair_failed_format",
        "Lr2_play_history_schema_message_uninstall_failed_format",
        "Lr2_play_history_schema_message_base_schema_incompatible",
        "Lr2_play_history_schema_message_object_name_collision",
        "Lr2_play_history_schema_message_table_columns_incompatible",
        "Lr2_play_history_schema_message_partial_schema_manual_action_required",
        "Lr2_play_history_schema_message_not_installed",
        "Lr2_play_history_schema_message_installed",
        "Lr2_play_history_schema_message_install_or_repair_available",
        "Msg_confirm_lr2_play_history_schema_install_or_repair",
        "Msg_success_lr2_play_history_schema_install_or_repair",
        "Msg_lr2_play_history_schema_uninstall_not_installed",
        "Msg_success_lr2_play_history_schema_uninstall"
    ];
}
