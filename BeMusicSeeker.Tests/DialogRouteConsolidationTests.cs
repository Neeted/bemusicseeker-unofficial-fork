using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DialogRouteConsolidationTests
{
    private static readonly Regex[] LegacyRoutePatterns =
    [
        new Regex(@"DispatcherMessageBox\.Show", RegexOptions.Compiled),
        new Regex(@"internal static class DispatcherMessageBox", RegexOptions.Compiled),
        new Regex(@"internal static class UiDialogLegacyAdapter", RegexOptions.Compiled),
        new Regex(@"UiDialogLegacyAdapter\.ShowMessageBox", RegexOptions.Compiled),
        new Regex(@"System\.Windows\.MessageBox\.Show", RegexOptions.Compiled),
        new Regex(@"(?<!Themed)MessageBox\.Show\(", RegexOptions.Compiled),
        new Regex(@"new ConfirmationMessage", RegexOptions.Compiled),
        new Regex(@"InteractionMessageAction<FrameworkElement>", RegexOptions.Compiled),
        new Regex(@"RaiseInteractionMessageOnUiThread", RegexOptions.Compiled),
        new Regex(@"InteractionMessageTrigger", RegexOptions.Compiled),
        new Regex(@"MessageKey=", RegexOptions.Compiled),
        new Regex(@"CommonOpenFileDialog", RegexOptions.Compiled),
        new Regex(@"OpenFileDialog", RegexOptions.Compiled),
        new Regex(@"SaveFileDialog", RegexOptions.Compiled),
        new Regex(@"FolderBrowserDialog", RegexOptions.Compiled),
        new Regex(@"\.ShowDialog\(", RegexOptions.Compiled),
        new Regex(@"ProgressDialog\.Execute", RegexOptions.Compiled),
        new Regex(@"ProgressDialog\.Current", RegexOptions.Compiled),
    ];

    [TestMethod]
    public void LegacyDialogRouteFiles_AreDocumentedInInventory()
    {
        string root = FindRepositoryRoot();
        string inventoryPath = Path.Combine(root, "devdocs", "spec", "dialog-route-inventory.md");
        string inventory = File.ReadAllText(inventoryPath);
        IReadOnlyList<string> documentedFiles = ReadDocumentedLegacySourceFiles(inventory);
        IReadOnlyList<string> actualFiles = FindLegacyDialogRouteFiles(root);

        CollectionAssert.AreEquivalent(
            documentedFiles.ToList(),
            actualFiles.ToList(),
            "Legacy dialog routes must be tracked in devdocs/spec/dialog-route-inventory.md before they are migrated.");
    }

    [TestMethod]
    public void UiDialogResult_SeparatesUserChoicesFromDisplayFailures()
    {
        Assert.AreEqual(UiDialogStatus.Accepted, UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK).Status);
        Assert.AreEqual(UiDialogStatus.Accepted, UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes).Status);
        Assert.AreEqual(UiDialogStatus.Rejected, UiDialogResult.FromMessageBoxResult(MessageBoxResult.No).Status);
        Assert.AreEqual(UiDialogStatus.CancelledByUser, UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel).Status);
        Assert.AreEqual(UiDialogStatus.ClosedByUser, UiDialogResult.FromMessageBoxResult(MessageBoxResult.None).Status);
        Assert.AreEqual(UiDialogStatus.ClosedByUser, UiDialogResult.ClosedByUser(MessageBoxResult.Cancel).Status);
        Assert.AreEqual(MessageBoxResult.Cancel, UiDialogResult.ClosedByUser(MessageBoxResult.Cancel).MessageBoxResult);
        Assert.AreEqual(UiDialogStatus.OwnerUnavailable, UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable).Status);
        Assert.AreEqual(UiDialogStatus.DispatcherUnavailable, UiDialogResult.NotShown(UiDialogStatus.DispatcherUnavailable).Status);
        Assert.IsFalse(UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable).IsAccepted);
    }

    [TestMethod]
    public void LegacyDialogEntryPoints_AreCoordinatorBackedAndDoNotUseStandardFallback()
    {
        string root = FindRepositoryRoot();
        string bmsLibraryDialogService = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "BmsLibraryDialogService.cs"));
        string mainWindowXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string emergencyDialog = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Dialogs", "EmergencyDialog.cs"));

        Assert.IsFalse(File.Exists(Path.Combine(root, "BeMusicSeeker", "Models", "Utils", "DispatcherMessageBox.cs")), "DispatcherMessageBox should be removed after production call sites move to coordinator-backed routes.");
        Assert.IsFalse(File.Exists(Path.Combine(root, "BeMusicSeeker", "Views", "Dialogs", "UiDialogLegacyAdapter.cs")), "UiDialogLegacyAdapter should be removed after legacy entrypoints are retired.");
        StringAssert.Contains(bmsLibraryDialogService, "UiDialogCoordinator");
        Assert.IsFalse(bmsLibraryDialogService.Contains("DispatcherMessageBox"), "BmsLibraryDialogService must not route model dialogs through DispatcherMessageBox.");
        Assert.IsFalse(bmsLibraryDialogService.Contains("UiDialogLegacyAdapter"), "BmsLibraryDialogService must not route through the retired legacy adapter.");
        StringAssert.Contains(emergencyDialog, "MessageBox.Show(");
        Assert.IsFalse(File.Exists(Path.Combine(root, "BeMusicSeeker", "Views", "ThemedDialogInteractionMessageActions.cs")), "Livet message box actions should be removed after notification routes move to the coordinator.");
        Assert.IsFalse(mainWindowXaml.Contains("MessageKey=\"InformationDialog\""), "MainWindow must not keep unused Livet information dialog triggers.");
        Assert.IsFalse(mainWindowXaml.Contains("MessageKey=\"ConfirmationDialog\""), "MainWindow must not keep unused Livet confirmation dialog triggers.");
    }

    [TestMethod]
    public void BmsPlaylist_DoesNotShowModelDialogsDirectly()
    {
        string root = FindRepositoryRoot();
        string playlistCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string ubmplayCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "uBMplay.cs"));
        string fastDirectoryEnumeratorCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "Utils", "FastDirectoryEnumerator.cs"));
        string taskExCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "Utils", "TaskEx.cs"));
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();

        Assert.IsFalse(playlistCode.Contains("DispatcherMessageBox.Show("), "BMSPlaylist must return operation notifications instead of showing message boxes from the model layer.");
        Assert.IsFalse(ubmplayCode.Contains("DispatcherMessageBox.Show("), "uBMplay must report startup failures to its caller instead of showing message boxes from the model layer.");
        Assert.IsFalse(fastDirectoryEnumeratorCode.Contains("DispatcherMessageBox.Show("), "FastDirectoryEnumerator must not show message boxes while enumerating utility paths.");
        Assert.IsFalse(taskExCode.Contains("DispatcherMessageBox.Show("), "TaskEx must record task faults without showing message boxes from utility continuations.");
        Assert.IsFalse(playlistCode.Contains("OperationNotificationScope"), "BMSPlaylist must not own the mutable presentation scope.");
        Assert.IsFalse(playlistCode.Contains("QueueOperationNotification"), "BMSPlaylist must not own the notification queue implementation.");
        StringAssert.Contains(playlistCode, "OperationNotificationOwner");
        StringAssert.Contains(mainWindowCode, "PresentPlaylistOperationNotifications(");
        Assert.IsFalse(viewModelCode.Contains("BMSPlaylist.BeginOperationNotificationScope()"));
    }

    [TestMethod]
    public void ScoreViewerRegistration_UsesFeatureWorkflowOwner()
    {
        string rootViewModelCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string workflowCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "ScoreViewerRegistration.cs");
        string runScoreViewerRegistration = ExtractBetween(
            workflowCode,
            "internal async Task<ScoreViewerRegistrationResult> RunAsync",
            "internal ScoreViewerRegistrationPlan Prepare");

        StringAssert.Contains(mainWindowCode, "viewModel.ScoreViewerRegistration.RunAsync(");
        Assert.IsFalse(mainWindowCode.Contains("RunScoreViewerRegistrationAsync"));
        Assert.IsFalse(mainWindowCode.Contains("ConfirmScoreViewerUploadIfNeededAsync"));
        Assert.IsFalse(mainWindowCode.Contains("ShowScoreViewerRegistrationResultAsync"));
        Assert.IsFalse(rootViewModelCode.Contains("PrepareScoreViewerRegistration"));
        Assert.IsFalse(rootViewModelCode.Contains("CompleteScoreViewerRegistration"));
        Assert.IsFalse(rootViewModelCode.Contains("scoreRegisterUrl"));
        Assert.IsFalse(rootViewModelCode.Contains("scoreStatusUrl"));
        Assert.IsFalse(rootViewModelCode.Contains("scoreViewUrl"));
        StringAssert.Contains(workflowCode, "IScoreViewerRegistrationGateway");
        StringAssert.Contains(workflowCode, "IScoreViewerRegistrationInteraction");
        StringAssert.Contains(runScoreViewerRegistration, "interaction.ConfirmUploadAsync(");
        StringAssert.Contains(runScoreViewerRegistration, "Complete(plan, uploadConfirmed)");
        Assert.IsTrue(
            runScoreViewerRegistration.IndexOf("interaction.ConfirmUploadAsync(", StringComparison.Ordinal)
            < runScoreViewerRegistration.IndexOf("Complete(plan, uploadConfirmed)", StringComparison.Ordinal),
            "Score Viewer upload must happen only after UI confirmation has completed.");
        StringAssert.Contains(workflowCode, "UiDialogCoordinator");
        StringAssert.Contains(workflowCode, "UiDialogStatus.Failed => throw");
        StringAssert.Contains(workflowCode, "ScoreViewerRegistrationItem.HashOnly");
        StringAssert.Contains(workflowCode, "ScoreViewerRegistrationItem.AlreadyRegistered");
        StringAssert.Contains(workflowCode, "ScoreViewerRegistrationItem.NeedsUpload");
        StringAssert.Contains(workflowCode, "ScoreViewerRegistrationItem.StatusCheckFailed");
        StringAssert.Contains(workflowCode, "ScoreViewerRegistrationItem.Uploaded");
        StringAssert.Contains(workflowCode, "ScoreViewerRegistrationItem.UploadFailed");
        StringAssert.Contains(workflowCode, "ScoreViewerRegistrationItem.UploadDeclined");
        Assert.IsFalse(mainWindowCode.Contains("Settings.Default.ShowScoreViewerRegisterConfirmMsg"));
    }

    [TestMethod]
    public void DecisionConfirmations_DoNotDependOnLivetConfirmationResponse()
    {
        string root = FindRepositoryRoot();
        string combinedCode = string.Concat(
            SourceTextTestHelper.ReadMainWindowViewModelSourceText(),
            SourceTextTestHelper.ReadMainWindowSourceText(),
            File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "PlaylistSummaryBulkEditDialog.cs")));

        Assert.IsFalse(combinedCode.Contains("confirmationMessage.Response"), "Decision confirmations must use UiDialogCoordinator results instead of Livet ConfirmationMessage.Response.");
        Assert.IsFalse(combinedCode.Contains("confirmationMessage2.Response"), "Decision confirmations must not keep secondary Livet response checks.");
        StringAssert.Contains(combinedCode, "ShowUiConfirmation(");
        Assert.IsFalse(combinedCode.Contains("DispatcherMessageBox.Show("), "View and ViewModel decision confirmations must not depend on DispatcherMessageBox.");
        Assert.IsFalse(combinedCode.Contains("new ConfirmationMessage"), "ViewModel notifications must use UiDialogCoordinator-backed routes instead of Livet ConfirmationMessage.");
        StringAssert.Contains(combinedCode, "ShowUiMessage(");
        StringAssert.Contains(combinedCode, "UiDialogStatus.ClosedByUser => result.MessageBoxResult is MessageBoxResult.OK or MessageBoxResult.Yes");
    }

    [TestMethod]
    public void ProgressOperations_AreRoutedThroughUiDialogCoordinator()
    {
        string root = FindRepositoryRoot();
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string progressDialogCode = File.ReadAllText(Path.Combine(root, "Parago", "Windows", "ProgressDialog.cs"));
        string ownerResolverCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Dialogs", "UiDialogOwnerResolver.cs"));
        string coordinatorCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Dialogs", "UiDialogCoordinator.cs"));
        string audioOwnerCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindow", "SelectedChartAudioConversionWorkflowOwner.cs"));

        Assert.IsFalse(mainWindowCode.Contains("ProgressDialog.Execute("), "MainWindow progress operations must go through UiDialogCoordinator.");
        Assert.IsFalse(mainWindowCode.Contains("ProgressDialog.Current"), "MainWindow must not depend on static progress dialog state.");
        Assert.IsFalse(progressDialogCode.Contains("static ProgressDialogContext Current"), "ProgressDialog must pass operation context explicitly instead of exposing static state.");
        Assert.IsFalse(mainWindowCode.Contains("RunProgressUntilTaskCompletesAsync("));
        StringAssert.Contains(audioOwnerCode, "dialogs.RunWithProgressAsync(");
        StringAssert.Contains(coordinatorCode, "UiDialogOwnerResolver.PushActiveModal");
        int activeModalOwnerIndex = ownerResolverCode.IndexOf("Window activeModalWindow = ResolveActiveModalWindow();", StringComparison.Ordinal);
        int requestedOwnerIndex = ownerResolverCode.IndexOf("IsUsableOwner(requestedOwner)", StringComparison.Ordinal);
        Assert.IsTrue(activeModalOwnerIndex >= 0, "Owner resolver must query coordinator-managed active modal owners.");
        Assert.IsTrue(requestedOwnerIndex >= 0, "Owner resolver must keep requested owner handling after active modal resolution.");
        Assert.IsTrue(
            activeModalOwnerIndex < requestedOwnerIndex,
            "Coordinator-managed active modal owner must take precedence over requested owners.");
    }

    [TestMethod]
    public void FilePickers_AreRoutedThroughUiDialogCoordinator()
    {
        string root = FindRepositoryRoot();
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string loadPlaylistCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "LoadPlaylistURIDialog.cs"));
        string coordinatorCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Dialogs", "UiDialogCoordinator.cs"));

        Assert.IsFalse(mainWindowCode.Contains("new OpenFileDialog"), "MainWindow must not create open file pickers directly.");
        Assert.IsFalse(mainWindowCode.Contains("new SaveFileDialog"), "MainWindow must not create save file pickers directly.");
        Assert.IsFalse(mainWindowCode.Contains("new CommonOpenFileDialog"), "MainWindow must not create common file pickers directly.");
        Assert.IsFalse(settingDialogCode.Contains("new OpenFileDialog"), "SettingDialog must not create open file pickers directly.");
        Assert.IsFalse(settingDialogCode.Contains("new SaveFileDialog"), "SettingDialog must not create save file pickers directly.");
        Assert.IsFalse(settingDialogCode.Contains("new CommonOpenFileDialog"), "SettingDialog must not create common file pickers directly.");
        Assert.IsFalse(loadPlaylistCode.Contains("new OpenFileDialog"), "LoadPlaylistURIDialog must not create open file pickers directly.");
        StringAssert.Contains(coordinatorCode, "PickFileCore(");
        StringAssert.Contains(coordinatorCode, "PickFolderCore(");
        StringAssert.Contains(coordinatorCode, "PickSaveFileCore(");
        StringAssert.Contains(coordinatorCode, "UiDialogStatus.OwnerUnavailable");
        Assert.IsFalse(File.Exists(Path.Combine(root, "BeMusicSeeker", "Views", "CommonOpenFileDialogInteractionMessageAction.cs")));
    }

    [TestMethod]
    public void WindowModals_AreRoutedThroughUiDialogCoordinator()
    {
        string root = FindRepositoryRoot();
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string coordinatorCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Dialogs", "UiDialogCoordinator.cs"));
        string requestsCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Dialogs", "UiDialogRequests.cs"));
        string windowResultCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Dialogs", "UiWindowDialogResult.cs"));
        string selectedChartMutationOwnerCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindow", "SelectedChartMutationWorkflowOwner.cs"));
        string settingDialogViewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();

        Assert.IsFalse(mainWindowCode.Contains(".ShowDialog("), "MainWindow modal windows must go through UiDialogCoordinator.");
        Assert.IsFalse(settingDialogCode.Contains(".ShowDialog("), "SettingDialog modal windows must go through UiDialogCoordinator.");
        StringAssert.Contains(mainWindowCode, "ShowWindowAsync(new UiWindowDialogRequest<UpdateAvailableDialog, UpdateAssetInfo>");
        StringAssert.Contains(mainWindowCode, "StartupUpdatePresentationRequest");
        StringAssert.Contains(mainWindowCode, "ShellShutdownWorkflow");
        Assert.IsFalse(mainWindowCode.Contains("BindShutdownPreparation("));
        Assert.IsFalse(mainWindowCode.Contains("PrepareStartupUpdateShutdownAsync"));
        Assert.IsFalse(mainWindowCode.Contains("StartupUpdateShutdownPreparationRequest"));
        Assert.IsFalse(mainWindowCode.Contains("ShutdownPreparationRequested"));
        StringAssert.Contains(mainWindowCode, "viewModel.StartupUpdateWorkflow.Start();");
        StringAssert.Contains(mainWindowCode, "new UpdateAvailableDialog(request.Result, viewModel.ProgressHub)");
        Assert.IsFalse(mainWindowCode.Contains("CheckForUpdatesAsync("), "MainWindow must not own the startup update check.");
        Assert.IsFalse(mainWindowCode.Contains("DownloadAndApplyUpdateAsync("), "MainWindow must not own update download/apply orchestration.");
        Assert.IsFalse(mainWindowCode.Contains("CleanupPreviousUpdateWorkDirectory("), "MainWindow must not own update work-directory cleanup.");
        Assert.IsFalse(mainWindowCode.Contains("TryDeleteDownloadedUpdatePackage("), "MainWindow must not own downloaded-package cleanup.");
        Assert.IsFalse(mainWindowCode.Contains("updateCheckService"), "MainWindow must not retain the update-check service field.");
        Assert.IsFalse(mainWindowCode.Contains("updateDownloadService"), "MainWindow must not retain the update-download service field.");
        StringAssert.Contains(mainWindowCode, "ElevatedProcessWarningWorkflow.Start(CanPresentElevatedProcessWarning);");
        StringAssert.Contains(mainWindowCode, "UiDialogRoute.ShowMessageBox(");
        StringAssert.Contains(mainWindowCode, "Warn_ElevatedProcessDragDropLimited");
        Assert.IsFalse(mainWindowCode.Contains("ShowElevatedProcessWarningIfNeeded"), "MainWindow must not retain the elevated warning workflow.");
        Assert.IsFalse(mainWindowCode.Contains("ShouldSkipElevatedProcessWarning"), "MainWindow must keep only the presentation guard for the elevated warning.");
        Assert.IsFalse(mainWindowCode.Contains("IsCurrentProcessElevated"), "MainWindow must not own the platform elevation probe.");
        Assert.IsFalse(mainWindowCode.Contains("WindowsIdentity"), "MainWindow must not depend on the platform elevation API.");
        Assert.IsFalse(mainWindowCode.Contains("WindowsPrincipal"), "MainWindow must not depend on the platform elevation API.");
        string elevationProbeCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "Utils", "ProcessElevationProbe.cs"));
        StringAssert.Contains(elevationProbeCode, "WindowsIdentity");
        StringAssert.Contains(elevationProbeCode, "WindowsPrincipal");
        string warningOwnerCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "ElevatedProcessWarningWorkflowOwner.cs"));
        StringAssert.Contains(warningOwnerCode, "processElevationProbe()");
        StringAssert.Contains(warningOwnerCode, "PresentationRequested");
        StringAssert.Contains(warningOwnerCode, "CanPresent(run)");
        string warningPresentationHandler = ExtractBetween(
            mainWindowCode,
            "private void MainWindowViewModel_ElevatedProcessWarningPresentationRequested",
            "private void MainWindowViewModel_StartupUpdatePresentationRequested");
        StringAssert.Contains(warningPresentationHandler, "CanPresentElevatedProcessWarning()");
        StringAssert.Contains(warningPresentationHandler, "Warn_ElevatedProcessDragDropLimited");
        StringAssert.Contains(warningPresentationHandler, "BeMusicSeeker.Properties.Resources.Warning");
        StringAssert.Contains(warningPresentationHandler, "MessageBoxButton.OK");
        StringAssert.Contains(warningPresentationHandler, "MessageBoxImage.Exclamation");
        StringAssert.Contains(warningPresentationHandler, "request.Complete(true)");
        StringAssert.Contains(warningPresentationHandler, "request.Fail(exception)");
        string closingHandler = ExtractBetween(mainWindowCode, "protected override void OnClosing", "private static void CloseContextMenuIfOpen");
        StringAssert.Contains(closingHandler, "TryBeginWindowCloseRequest(out Task<ShellShutdownWorkflowCompletionReceipt> closeRequest)");
        StringAssert.Contains(closingHandler, "CompleteCloseAfterShellRequestAsync(closeRequest)");
        Assert.IsFalse(closingHandler.Contains("ElevatedProcessWarningWorkflow.NotifyClosing()"));
        Assert.IsFalse(closingHandler.Contains("StartupUpdateWorkflow.NotifyClosing()"));
        StringAssert.Contains(selectedChartMutationOwnerCode, "ShowWindowAsync(");
        StringAssert.Contains(selectedChartMutationOwnerCode, "UiWindowDialogRequest<PendingDeleteConfirmDialog, bool>");
        StringAssert.Contains(settingDialogViewModelCode, "ShowWindowAsync(");
        StringAssert.Contains(settingDialogViewModelCode, "UiWindowDialogRequest<Lr2PlayHistorySchemaUninstallDialog, Lr2PlayHistorySchemaUninstallMode>");
        Assert.IsFalse(settingDialogCode.Contains("ShowWindowAsync(new UiWindowDialogRequest<Lr2PlayHistorySchemaUninstallDialog, Lr2PlayHistorySchemaUninstallMode>"));
        StringAssert.Contains(settingDialogCode, "ShowWindowAsync(new UiWindowDialogRequest<PlayHistoryFolderDisplayPresetEditDialog, object>");
        StringAssert.Contains(requestsCode, "internal sealed class UiWindowDialogRequest<TWindow, TResult>");
        StringAssert.Contains(windowResultCode, "internal sealed class UiWindowDialogResult<TResult>");
        StringAssert.Contains(coordinatorCode, "ShowWindowAsync<TWindow, TResult>");
        StringAssert.Contains(coordinatorCode, "UiDialogOwnerResolver.PushActiveModal(window)");
        StringAssert.Contains(coordinatorCode, "UiDialogStatus.OwnerUnavailable");
        StringAssert.Contains(coordinatorCode, "Window dialog factory assigned a different owner.");
    }

    [TestMethod]
    public void ViewCodeBehindMessages_DoNotUseDispatcherMessageBox()
    {
        string root = FindRepositoryRoot();
        string viewsRoot = Path.Combine(root, "BeMusicSeeker", "Views");
        List<string> offenders = Directory
            .EnumerateFiles(viewsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("DispatcherMessageBox.Show("))
            .Select(path => NormalizeRelativePath(new Uri(root + Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(path)).ToString()))
            .ToList();

        CollectionAssert.AreEqual(Array.Empty<string>(), offenders, "View code-behind messages must use UiDialogRoute / UiDialogCoordinator instead of DispatcherMessageBox.");
        string routeCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Dialogs", "UiDialogRoute.cs"));
        StringAssert.Contains(routeCode, "ShowMessageAsync(new UiMessageRequest(");
        StringAssert.Contains(routeCode, "ConfirmAsync(new UiConfirmationRequest(");
        StringAssert.Contains(routeCode, "ThrowIfNotShown(result, caption);");
    }

    [TestMethod]
    public void ViewModels_DoNotUseDispatcherMessageBox()
    {
        string root = FindRepositoryRoot();
        string viewModelsRoot = Path.Combine(root, "BeMusicSeeker", "ViewModels");
        List<string> offenders = Directory
            .EnumerateFiles(viewModelsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("DispatcherMessageBox.Show("))
            .Select(path => NormalizeRelativePath(new Uri(root + Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(path)).ToString()))
            .ToList();

        CollectionAssert.AreEqual(Array.Empty<string>(), offenders, "ViewModel messages must use UiDialogCoordinator-backed routes instead of DispatcherMessageBox.");
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string temporaryCopyCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "temporarilyCopyFiles.cs"));
        StringAssert.Contains(viewModelCode, "ShowUiMessage(");
        StringAssert.Contains(viewModelCode, "ShowUiConfirmation(");
        StringAssert.Contains(temporaryCopyCode, "UiDialogRoute.ShowMessageBox(");
        StringAssert.Contains(temporaryCopyCode, "temporary_preview_cleanup_failed");
    }

    [TestMethod]
    public void StandardMessageBox_IsLimitedToEmergencyDialog()
    {
        string root = FindRepositoryRoot();
        List<string> offenders = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(IsProductionAppSourceFile)
            .Where(path => File.ReadAllText(path).Contains("MessageBox.Show("))
            .Select(path => NormalizeRelativePath(new Uri(root + Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(path)).ToString()))
            .ToList();
        List<string> emergencyCallers = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(IsProductionAppSourceFile)
            .Where(path => File.ReadAllText(path).Contains("EmergencyDialog.Show("))
            .Select(path => NormalizeRelativePath(new Uri(root + Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(path)).ToString()))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        CollectionAssert.AreEquivalent(
            new[] { "BeMusicSeeker/Views/Dialogs/EmergencyDialog.cs" },
            offenders,
            "Standard MessageBox.Show is allowed only in the explicit emergency dialog boundary.");
        CollectionAssert.AreEquivalent(
            new[] { "BeMusicSeeker/App.cs" },
            emergencyCallers,
            "EmergencyDialog.Show may be called only from App startup/shutdown/unhandled-exception emergency routes.");
    }

    [TestMethod]
    public void OverlayDialogs_AreShownThroughMainWindowHost()
    {
        string root = FindRepositoryRoot();
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string mainWindowXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string initialSetupCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "InitialSetupLanguageDialog.xaml.cs"));
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string loadPlaylistCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "LoadPlaylistURIDialog.cs"));
        string playlistPropertyCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "PlaylistPropertyDialog.cs"));
        string playlistBulkEditCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "PlaylistSummaryBulkEditDialog.cs"));

        StringAssert.Contains(mainWindowCode, "internal void ShowOverlayDialog(FrameworkElement dialog)");
        StringAssert.Contains(mainWindowCode, "internal void HideOverlayDialog(FrameworkElement dialog)");
        StringAssert.Contains(mainWindowCode, "ShowOverlayDialog(settingDialog)");
        StringAssert.Contains(mainWindowCode, "ShowOverlayDialog(initialSetupLanguageDialog)");
        StringAssert.Contains(mainWindowCode, "viewModel.SettingDialog.AttachPresentationPort(this);");
        StringAssert.Contains(mainWindowCode, "InitialSetupLanguageDialogRequested += MainWindowViewModel_InitialSetupLanguageDialogRequested;");
        StringAssert.Contains(mainWindowXaml, "SettingsCommand=\"{Binding DataContext.SettingDialog.OpenCommand, ElementName=window}\"");
        string playbackPanelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "PlaybackPanelView.xaml"));
        StringAssert.Contains(playbackPanelCode, "Command=\"{Binding SettingsCommand, ElementName=playbackPanelView}\"");
        Assert.IsFalse(mainWindowXaml.Contains("InteractionMessageTrigger"), "MainWindow must not use Livet message triggers for overlay or table callbacks.");
        Assert.IsFalse(mainWindowXaml.Contains("MessageKey="), "MainWindow must not route UI callbacks through MessageKey strings.");
        Assert.IsFalse(mainWindowXaml.Contains("PropertyName=\"Visibility\" Value=\"Visible\" TargetObject=\"{Binding ElementName=settingDialog"));
        Assert.IsFalse(initialSetupCode.Contains("Parent is Panel"), "Initial setup must not find SettingDialog by walking the parent panel.");
        Assert.IsFalse(initialSetupCode.Contains("MainWindow"), "Initial setup must request settings through its inherited owner command.");
        Assert.IsFalse(initialSetupCode.Contains("HideOverlayDialog"));
        StringAssert.Contains(settingDialogCode, "HideThisOverlay()");
        StringAssert.Contains(loadPlaylistCode, "HideOverlayDialog(this)");
        StringAssert.Contains(playlistPropertyCode, "ClosePlaylistPropertyDialog(playlistPropertyDialogViewModel)");
        StringAssert.Contains(playlistBulkEditCode, "HideOverlayDialog(playlistSummaryBulkEditDialog)");
    }

    private static IReadOnlyList<string> ReadDocumentedLegacySourceFiles(string inventory)
    {
        string section = ExtractBetween(inventory, "## Legacy Source Files", "## Route Classification Axes");
        MatchCollection matches = Regex.Matches(section, @"\| `([^`]+)` \|");
        return matches
            .Cast<Match>()
            .Select(match => NormalizeRelativePath(match.Groups[1].Value))
            .Where(path => path.StartsWith("BeMusicSeeker/", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> FindLegacyDialogRouteFiles(string root)
    {
        string sourceRoot = Path.Combine(root, "BeMusicSeeker");
        return Directory.GetFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                AbsolutePath = path,
                RelativePath = NormalizeRelativePath(GetRelativePath(root, path))
            })
            .Where(file => LegacyRoutePatterns.Any(pattern => pattern.IsMatch(File.ReadAllText(file.AbsolutePath))))
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsProductionAppSourceFile(string path)
    {
        string relativePath = NormalizeRelativePath(GetRelativePath(FindRepositoryRoot(), path));
        return !relativePath.StartsWith("BeMusicSeeker.Tests/", StringComparison.Ordinal)
            && !relativePath.StartsWith("BeMusicSeeker.Updater/", StringComparison.Ordinal)
            && !relativePath.StartsWith("tools/", StringComparison.Ordinal)
            && !relativePath.StartsWith("obj/", StringComparison.Ordinal)
            && !relativePath.StartsWith("bin/", StringComparison.Ordinal)
            && relativePath.IndexOf("/obj/", StringComparison.Ordinal) < 0
            && relativePath.IndexOf("/bin/", StringComparison.Ordinal) < 0;
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string currentDirectory = directory!;
            if (File.Exists(Path.Combine(currentDirectory, "BeMusicSeeker.sln")))
            {
                return currentDirectory;
            }

            directory = Directory.GetParent(currentDirectory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find repository root from " + AppContext.BaseDirectory);
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static string GetRelativePath(string root, string path)
    {
        string rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Uri.UnescapeDataString(new Uri(rootWithSeparator).MakeRelativeUri(new Uri(path)).ToString()).Replace('/', Path.DirectorySeparatorChar);
    }

    private static string ExtractBetween(string text, string start, string end)
    {
        int startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(startIndex >= 0, "Start marker was not found: " + start);
        startIndex += start.Length;
        int endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.IsTrue(endIndex >= 0, "End marker was not found: " + end);
        return text.Substring(startIndex, endIndex - startIndex);
    }
}
