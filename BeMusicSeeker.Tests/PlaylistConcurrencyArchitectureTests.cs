using System;
using System.IO;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistConcurrencyArchitectureTests
{
    [TestMethod]
    public void ExternalTableRegistration_DoesNotMutateVisibleCollectionInsideRegistrationWriterBlock()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string method = ExtractMethodBody(source, "internal async Task<BMSTable> RegistrateExternalTableAsync(BMSTable bMSTable");

        StringAssert.Contains(method, "await AddCommittedBMSTableToVisibleCollectionAsync(bMSTable).ConfigureAwait(false)");
        Assert.IsFalse(
            method.Contains("BMSTables.Add(bMSTable);"),
            "External registration must not call DispatcherCollection.Add while the registration writer lock is held.");
    }

    [TestMethod]
    public void ExternalTableRegistration_DoesNotCommitDatabaseInsideRegistrationWriterBlock()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string method = ExtractMethodBody(source, "internal async Task<BMSTable> RegistrateExternalTableAsync(BMSTable bMSTable");
        string writerBlock = ExtractBlockBody(method, "using (rwlockBMSTables.GetWriterGuard())");

        Assert.IsFalse(
            writerBlock.Contains("CommitBMSTable("),
            "External registration writer lock must not cover playlist DB persistence.");
        StringAssert.Contains(method, "CommitBMSTable(bMSTable);");
        StringAssert.Contains(method, "await AddCommittedBMSTableToVisibleCollectionAsync(bMSTable).ConfigureAwait(false)");
    }

    [TestMethod]
    public void ExternalTableBatchRegistration_DoesNotCommitDatabaseInsideRegistrationWriterBlock()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string method = ExtractMethodBody(source, "internal async Task<RegisteredExternalTableBatchResult> RegistrateExternalTablesAsync");
        string writerBlock = ExtractBlockBody(method, "using (rwlockBMSTables.GetWriterGuard())");

        Assert.IsFalse(
            writerBlock.Contains("CommitBMSTable("),
            "Batch external registration writer lock must not cover playlist DB persistence.");
        StringAssert.Contains(method, "CommitBMSTable(tableList);");
        StringAssert.Contains(method, "await AddCommittedBMSTablesToVisibleCollectionAsync(tableList).ConfigureAwait(false)");
        StringAssert.Contains(method, "ApplyCachedPlaylistUrlCompletionToTables(tableList, operationReason);");
    }

    [TestMethod]
    public void PlaylistEntryBatchCommit_AcquiresTableWriterLocksBeforeDatabaseTransaction()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string method = ExtractMethodBody(source, "internal void CommitBMSTablesWithEntriesToDB");
        int tableLockIndex = method.IndexOf("writerGuards.Add(table.ReaderWriterLock.GetWriterGuard())", StringComparison.Ordinal);
        int databaseOpenIndex = method.IndexOf("new LR2SongDBExtended(lr2SongDBPath)", StringComparison.Ordinal);

        Assert.IsTrue(tableLockIndex >= 0, "Batch entry commit must acquire table writer locks explicitly.");
        Assert.IsTrue(databaseOpenIndex >= 0, "Batch entry commit must open the playlist database explicitly.");
        Assert.IsTrue(
            tableLockIndex < databaseOpenIndex,
            "Batch entry commit must keep the existing lock order: table writer lock before playlist DB transaction.");
        StringAssert.Contains(method, ".OrderBy(table => table.playlist_id ?? int.MaxValue)");
    }

    [TestMethod]
    public void PlaylistVisibleCollectionMutations_AreDispatchedThroughDedicatedBoundary()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));

        StringAssert.Contains(source, "InvokeBMSTablesCollectionMutation");
        StringAssert.Contains(source, "InvokeBMSTablesCollectionMutationAsync");
        StringAssert.Contains(source, "GetBMSTablesDispatcher");
    }

    [TestMethod]
    public void PlaylistUrlCompletionSettings_UseDedicatedProviderBoundary()
    {
        string playlistSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string urlCompletionSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.UrlCompletion.cs"));

        Assert.IsFalse(playlistSource.Contains("Settings.Default.EnablePlaylistUrlCompletion"));
        Assert.IsFalse(urlCompletionSource.Contains("Settings.Default."));
        StringAssert.Contains(playlistSource, "IsPlaylistUrlCompletionEnabled()");
        StringAssert.Contains(urlCompletionSource, "GetPlaylistUrlCompletionOptions()");
        StringAssert.Contains(urlCompletionSource, "tsvResult.Snapshot.Candidates");
        StringAssert.Contains(urlCompletionSource, "stellaResult.Snapshot.Candidates");
    }

    [TestMethod]
    public void BeatorajaBmtSettings_UseDedicatedProviderBoundary()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));

        Assert.IsFalse(source.Contains("Settings.Default.EnableBeatorajaBmtOutput"));
        Assert.IsFalse(source.Contains("Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled"));
        Assert.IsFalse(source.Contains("Settings.Default.BeatorajaRootPath"));
        Assert.IsFalse(source.Contains("Settings.Default.BeatorajaBmtTablePath"));
        Assert.IsFalse(source.Contains("Settings.Default.RegisterBeatorajaBmtUrls"));
        Assert.IsFalse(source.Contains("Settings.Default.BeatorajaBmtHashOutputMode"));
        StringAssert.Contains(source, "GetBeatorajaBmtOptions()");
    }

    [TestMethod]
    public void CustomFolderOutputResolution_UsesDedicatedProviderBoundary()
    {
        string playlistSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string librarySource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSLibrary.cs"));
        string viewModelSource = SourceTextTestHelper.ReadMainWindowViewModelSourceText();

        StringAssert.Contains(playlistSource, "ResolveCustomFolderOutputDirectory(BMSTable bmsTable)");
        StringAssert.Contains(playlistSource, "customFolderOutputSettingsProvider()");
        StringAssert.Contains(librarySource, "CurrentOptionsSnapshot");
        StringAssert.Contains(librarySource, "options.LR2CustomFolderAdditionalOutputBaseDirs");
        StringAssert.Contains(viewModelSource, "customFolderOutputSettingsProvider()");
        Assert.IsFalse(playlistSource.Contains("Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent"));
        Assert.IsFalse(viewModelSource.Contains("return BMSPlaylist.GetCustomFolderOutputDirectory(bmsTable);"));
    }

    [TestMethod]
    public void SettingDialogCustomFolderSearchRootSynchronization_DelegatesToPlaylist()
    {
        string playlistSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string settingDialogSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "MainWindowViewModel.SettingDialogViewModel.cs"));

        StringAssert.Contains(playlistSource, "SyncCustomFolderOutputSearchRootsAfterSettingsChange(");
        StringAssert.Contains(playlistSource, "config.SetBMSSearchDirectories(nextDirectories);");
        StringAssert.Contains(settingDialogSource, "ownerViewModel.tables.SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(");
        Assert.IsFalse(settingDialogSource.Contains("lr2config.SetBMSSearchDirectories(nextDirectories)"));
        Assert.IsFalse(settingDialogSource.Contains("private static bool IsRootOutputBaseAdoptionRemovalTarget"));
    }

    [TestMethod]
    public void StartupRootCustomFolderRepair_UsesStartupSettingsSnapshot()
    {
        string source = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        StringAssert.Contains(source, "CustomFolderOutputSettingsSnapshot startupCustomFolderSettings = null;");
        StringAssert.Contains(source, "startupCustomFolderSettings = customFolderOutputSettingsProvider()");
        StringAssert.Contains(source, "RepairRootCustomFolderOutputSearchRootsAfterStartupPlaylistLoad(startupCustomFolderSettings);");
        StringAssert.Contains(source, "settingDialog.SyncRootCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(startupCustomFolderSettings)");
        Assert.IsFalse(
            source.Contains("private void RepairRootCustomFolderOutputSearchRootsAfterStartupPlaylistLoad()"),
            "Startup root repair must not reacquire settings through an unscoped provider call.");
    }

    [TestMethod]
    public void MainChartColumnSettings_AreOwnedByInjectedStore()
    {
        string root = FindRepositoryRoot();
        string mainChartSource = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "MainChartListViewModel.cs"));
        string coordinatorSource = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistSummaryColumnSettingsCoordinator.cs"));

        StringAssert.Contains(mainChartSource, "IMainChartColumnSettingsStore");
        StringAssert.Contains(coordinatorSource, "IMainChartColumnSettingsStore");
        Assert.IsFalse(mainChartSource.Contains("Settings.Default."));
        Assert.IsFalse(coordinatorSource.Contains("Settings.Default."));

        string compositionSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "ApplicationComposition.cs");
        StringAssert.Contains(compositionSource, "new SettingsMainChartColumnSettingsStore(() => this.settingsEditSession.Values)");
        Assert.IsFalse(compositionSource.Contains("mainChartColumnSettingsStore: new SettingsMainChartColumnSettingsStore()"));
    }

    [TestMethod]
    public void StartupSettingsSnapshot_DoesNotDependOnNestedSettingDialogParser()
    {
        string root = FindRepositoryRoot();
        string snapshotSource = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "StartupSettingsSnapshot.cs"));

        StringAssert.Contains(snapshotSource, "StandaloneBmsRootPathSettings.Deserialize(");
        StringAssert.Contains(snapshotSource, "CreateCurrent(Settings settings)");
        Assert.IsFalse(snapshotSource.Contains("Settings.Default."));
        Assert.IsFalse(snapshotSource.Contains("SettingDialogViewModel"));

        string compositionSource = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "ApplicationComposition.cs"));
        StringAssert.Contains(compositionSource, "StartupSettingsSnapshot.CreateCurrent(this.settingsEditSession.Values)");
    }

    [TestMethod]
    public void CompositionWorkflowSnapshots_UseTheInjectedSettingsSession()
    {
        string root = FindRepositoryRoot();
        string compositionSource = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "ApplicationComposition.cs"));

        StringAssert.Contains(compositionSource, "BmsLibraryOptionsSnapshot.CreateCurrent(this.settingsEditSession.Values)");
        StringAssert.Contains(compositionSource, "PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(this.settingsEditSession.Values)");
        StringAssert.Contains(compositionSource, "BeatorajaBmtOptionsSnapshot.CreateCurrent(this.settingsEditSession.Values)");
        StringAssert.Contains(compositionSource, "CustomFolderOutputSettingsSnapshot.CreateCurrent(this.settingsEditSession.Values)");

        string[] snapshotPaths =
        [
            Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "BmsLibraryOptionsSnapshot.cs"),
            Path.Combine(root, "BeMusicSeeker", "Models", "PlaylistUrlCompletionOptionsSnapshot.cs"),
            Path.Combine(root, "BeMusicSeeker", "Models", "BeatorajaBmtOptionsSnapshot.cs"),
            Path.Combine(root, "BeMusicSeeker", "Models", "CustomFolderOutputSettingsSnapshot.cs")
        ];

        foreach (string snapshotPath in snapshotPaths)
        {
            Assert.IsFalse(
                File.ReadAllText(snapshotPath).Contains("Settings.Default."),
                $"Workflow snapshot must not read Settings.Default directly: {snapshotPath}");
        }
    }

    [TestMethod]
    public void MainWindowStartupFirstRunState_UsesCompositionBoundary()
    {
        string source = SourceTextTestHelper.ReadMainWindowViewModelSourceText();

        StringAssert.Contains(source, "firstStartupProvider()");
        StringAssert.Contains(source, "completeFirstStartup();");
        Assert.IsFalse(source.Contains("((App)System.Windows.Application.Current).firstStartup"));
    }

    [TestMethod]
    public void SettingDialogStartupMessage_UsesViewModelLifecycleBoundary()
    {
        string source = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Views",
            "SettingDialog.cs");

        StringAssert.Contains(source, "viewModel.IsFirstStartup");
        Assert.IsFalse(source.Contains("firstStartup"));
        Assert.IsFalse(source.Contains("((App)Application.Current).firstStartup"));
    }

    [TestMethod]
    public void SettingDialogSettingsPersistence_UsesCompositionBoundary()
    {
        string source = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "MainWindowViewModel.SettingDialogViewModel.cs");

        StringAssert.Contains(source, "this.reloadSettings();");
        StringAssert.Contains(source, "saveSettings();");
        Assert.IsFalse(source.Contains("Settings.Default.Reload();"));
        Assert.IsFalse(source.Contains("Settings.Default.Save();"));
    }

    [TestMethod]
    public void SettingDialogSettingsValues_UseInjectedEditSession()
    {
        string settingDialogSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "MainWindowViewModel.SettingDialogViewModel.cs");
        string mainWindowSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");
        string compositionSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "ApplicationComposition.cs");
        int outerHelperIndex = settingDialogSource.IndexOf(
            "internal static IReadOnlyList<PlaylistCustomFolderOutputBaseOption> CreatePlaylistCustomFolderOutputBaseOptions",
            StringComparison.Ordinal);
        string settingDialogOwnerSource = outerHelperIndex >= 0
            ? settingDialogSource.Substring(0, outerHelperIndex)
            : settingDialogSource;

        StringAssert.Contains(settingDialogSource, "private readonly ISettingsEditSession settingsEditSession;");
        StringAssert.Contains(settingDialogSource, "private Settings ApplicationSettings => settingsEditSession.Values;");
        Assert.IsFalse(settingDialogOwnerSource.Contains("Settings.Default."));
        StringAssert.Contains(mainWindowSource, "applicationComposition.CreateSettingDialogViewModel(this)");
        Assert.IsFalse(mainWindowSource.Contains("new SettingDialogViewModel(this)"));
        StringAssert.Contains(compositionSource, "ISettingsEditSession settingsEditSession = null");
        StringAssert.Contains(compositionSource, "settingsEditSession);");
    }

    [TestMethod]
    public void ShutdownSettingsPersistence_UsesCompositionBoundary()
    {
        string viewModelSource = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string mainWindowSource = SourceTextTestHelper.ReadMainWindowSourceText();

        StringAssert.Contains(viewModelSource, "saveSettings();");
        StringAssert.Contains(mainWindowSource, "viewModel.SaveSettingsForShutdown();");
        Assert.IsFalse(mainWindowSource.Contains("Settings.Default.Save();"));
    }

    [TestMethod]
    public void ApplicationSettingsLifecycle_UsesSettingsStoreBoundary()
    {
        string source = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Models",
            "ApplicationSettingsLifecycle.cs");

        StringAssert.Contains(source, "IApplicationSettingsStore");
        Assert.IsFalse(source.Contains("Settings.Default"));
    }

    [TestMethod]
    public void MainWindowKeywordSearchHistory_UsesSettingsStoreBoundary()
    {
        string source = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");

        StringAssert.Contains(source, "keywordSearchHistorySettingsStore.KeywordSearchHistory");
        StringAssert.Contains(source, "keywordSearchHistorySettingsStore.PlaylistSummaryKeywordSearchHistory");
        Assert.IsFalse(source.Contains("Settings.Default.KeywordSearchHistory"));
        Assert.IsFalse(source.Contains("Settings.Default.PlaylistSummaryKeywordSearchHistory"));
    }

    [TestMethod]
    public void PlayHistoryDisplaySettings_UseSettingsStoreBoundary()
    {
        string mainWindowSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");
        string settingDialogSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "MainWindowViewModel.SettingDialogViewModel.cs");

        StringAssert.Contains(mainWindowSource, "playHistoryDisplaySettingsStore.DisplayTargetSetsJson");
        StringAssert.Contains(mainWindowSource, "playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity");
        StringAssert.Contains(settingDialogSource, "playHistoryDisplaySettingsStore.DisplayTargetSetsJson");
        StringAssert.Contains(settingDialogSource, "playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity");
        Assert.IsFalse(mainWindowSource.Contains("Settings.Default.PlayHistoryDisplayTargetSetsJson"));
        Assert.IsFalse(mainWindowSource.Contains("Settings.Default.PlayHistorySelectedDisplayTargetIdentity"));
        Assert.IsFalse(settingDialogSource.Contains("Settings.Default.PlayHistoryDisplayTargetSetsJson"));
        Assert.IsFalse(settingDialogSource.Contains("Settings.Default.PlayHistorySelectedDisplayTargetIdentity"));
    }

    [TestMethod]
    public void MainTableOwners_AreConstructedByApplicationComposition()
    {
        string source = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");

        StringAssert.Contains(source, "composition.CreateMainChartListViewModel(");
        StringAssert.Contains(source, "composition.CreatePlaylistWorkspaceViewModel(");
        Assert.IsFalse(source.Contains("new MainChartListViewModel("));
        Assert.IsFalse(source.Contains("new PlaylistWorkspaceViewModel("));
    }

    [TestMethod]
    public void StartupLibraryServices_AreConstructedByApplicationComposition()
    {
        string source = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");

        StringAssert.Contains(source, "applicationComposition.CreateBmsLibrary(libraryProfile)");
        StringAssert.Contains(source, "applicationComposition.CreateBmsPlaylist(");
        Assert.IsFalse(source.Contains("new BMSLibrary("));
        Assert.IsFalse(source.Contains("new BMSPlaylist("));
    }

    [TestMethod]
    public void StartupPlayerServices_AreConstructedByApplicationComposition()
    {
        string source = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");

        StringAssert.Contains(source, "applicationComposition.CreateBmsPlayer(");
        Assert.IsFalse(source.Contains("new uBMplay("));
        Assert.IsFalse(source.Contains("new BMIIDXView2015("));
        Assert.IsFalse(source.Contains("new LR2body("));
    }

    [TestMethod]
    public void MainWindowChildOwners_AreConstructedByApplicationComposition()
    {
        string source = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");

        StringAssert.Contains(source, "composition.CreateMainWindowChildComposition(");
        Assert.IsFalse(source.Contains("new OperationProgressHubViewModel("));
        Assert.IsFalse(source.Contains("new PlaybackPanelViewModel("));
        Assert.IsFalse(source.Contains("new ChartListFilterViewModel("));
        Assert.IsFalse(source.Contains("new MainWindowRuntimeContext("));
        Assert.IsFalse(source.Contains("new PlayHistoryWorkflowOwner("));
        Assert.IsFalse(source.Contains("new PlaylistSummaryColumnSettingsCoordinator("));
        Assert.IsFalse(source.Contains("new PlaylistSummaryBmtSortCoordinator("));
        Assert.IsFalse(source.Contains("new RegularChartListOwner("));
        Assert.IsFalse(source.Contains("new DropInstallQueueProcessor("));
    }

    [TestMethod]
    public void MainWindowRuntimeSettings_UseCompositionEditSession()
    {
        string source = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");

        StringAssert.Contains(source, "private Settings ApplicationSettings => applicationComposition.SettingsEditSession.Values;");
        Assert.IsFalse(source.Contains("Settings.Default."));
    }

    [TestMethod]
    public void CommittedPlaylistVisibleCollectionReflection_IsNotCanceledAfterDatabaseCommit()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string addMethod = ExtractMethodBody(source, "private async Task AddCommittedBMSTableToVisibleCollectionAsync");
        string invokeAsyncMethod = ExtractMethodBody(source, "private async Task<T> InvokeBMSTablesCollectionMutationAsync<T>");

        Assert.IsFalse(addMethod.Contains("CancellationToken"), "Post-commit visible collection reflection must not accept a cancellation token.");
        Assert.IsFalse(invokeAsyncMethod.Contains("CancellationToken"), "Post-commit dispatcher reflection must not be canceled after DB commit.");
    }

    [TestMethod]
    public void ExternalTableImportContinuation_DoesNotReturnReferenceIndexWorkToUiThread()
    {
        string source = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string drainMethod = ExtractMethodBody(source, "private async Task DrainExternalPlaylistImportQueueAsync");
        string completionMethod = ExtractMethodBody(source, "private void CompleteImportedPlaylistRegistrations");
        string duplicatePreparationMethod = ExtractMethodBody(source, "private List<ExternalPlaylistImportWorkItem> PrepareExternalPlaylistImportRegistrationItems");
        string duplicateSkipMethod = ExtractMethodBody(source, "private static void RecordExternalPlaylistImportDuplicateNameSkip");

        StringAssert.Contains(drainMethod, ".ConfigureAwait(false)");
        StringAssert.Contains(drainMethod, "externalPlaylistImportQueue.DequeueBatch()");
        StringAssert.Contains(drainMethod, "await tables.LoadExternalTableSnapshotsAsync(");
        StringAssert.Contains(drainMethod, "schedulePlaylistUrlCompletionRefresh: false");
        StringAssert.Contains(drainMethod, "await tables.RegistrateExternalTablesAsync(");
        StringAssert.Contains(drainMethod, "catch (PlaylistAlreadyExistsException ex)");
        StringAssert.Contains(drainMethod, "RecordExternalPlaylistImportDuplicateNameSkip(item, ex.PlaylistName, ex, outcomes)");
        StringAssert.Contains(drainMethod, "Playlist_import_progress_phase_check_duplicates");
        StringAssert.Contains(drainMethod, "Playlist_import_progress_phase_update_references");
        StringAssert.Contains(drainMethod, "PlaylistSyncAttemptResult.CreateFailure(item.LoadedTable, item.LoadedTable, item.Uri, referenceUpdateException)");
        StringAssert.Contains(completionMethod, "files.AddReferenceBMSTablesIncremental(tableList);");
        StringAssert.Contains(completionMethod, "QueuePlaylistSummaryRefreshIfVisible(reason ?? \"playlist_registered\", invalidateTableCountCache: true)");
        StringAssert.Contains(duplicatePreparationMethod, "GetExternalPlaylistImportExistingNamesSnapshot()");
        StringAssert.Contains(duplicateSkipMethod, "ExternalPlaylistImportOutcome.SkippedDuplicateName");
        Assert.IsFalse(
            drainMethod.Contains("await tables.RegistrateExternalTableAsync("),
            "Bulk URL import should not serialize external requests through the single-table registration API.");
        Assert.IsFalse(
            completionMethod.Contains("tables.AcquireReaderLockBMSTables();"),
            "Import reference index updates must not hold the playlist collection reader lock while scanning library state.");
    }

    [TestMethod]
    public void BeatorajaTableUrlImport_ParallelizesExternalLoadAndBatchesRegistrationWork()
    {
        string source = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string importMethod = ExtractMethodBody(source, "private async Task ImportBeatorajaTableUrlsAsync");
        string completionMethod = ExtractMethodBody(source, "private void CompleteImportedPlaylistRegistrations");

        StringAssert.Contains(importMethod, "await tables.LoadExternalTableSnapshotsAsync(");
        StringAssert.Contains(importMethod, "schedulePlaylistUrlCompletionRefresh: false");
        StringAssert.Contains(importMethod, "await tables.RegistrateExternalTablesAsync(");
        StringAssert.Contains(importMethod, "tables.CommitBMSTableHeadersToDB(rawUrlChangedTables);");
        StringAssert.Contains(importMethod, "tables.QueueBeatorajaBmtExportForTables(rawUrlChangedTables");
        StringAssert.Contains(completionMethod, "files.AddReferenceBMSTablesIncremental(tableList);");
        StringAssert.Contains(importMethod, "BeatorajaTableUrlImportPostProgressStepCount");
        StringAssert.Contains(importMethod, "Beatoraja_table_url_import_progress_phase_register_playlists");
        StringAssert.Contains(importMethod, "Beatoraja_table_url_import_progress_phase_update_references");
        Assert.IsFalse(
            importMethod.Contains("await tables.RegistrateExternalTableAsync("),
            "beatoraja Table URL import should not serialize external requests through the single-table registration API.");
    }

    [TestMethod]
    public void ReaderWriterLockSlimWrapper_RawLockApiUpdatesObservableCounts()
    {
        var rwlock = new ReaderWriterLockSlimWrapper();

        rwlock.EnterReadLock();
        try
        {
            Assert.AreEqual(1u, rwlock.LockingReadCount);
        }
        finally
        {
            rwlock.ExitReadLock();
        }
        Assert.AreEqual(0u, rwlock.LockingReadCount);

        rwlock.EnterWriteLock();
        try
        {
            Assert.AreEqual(1u, rwlock.LockingWriteCount);
        }
        finally
        {
            rwlock.ExitWriteLock();
        }
        Assert.AreEqual(0u, rwlock.LockingWriteCount);

        Assert.IsTrue(rwlock.TryEnterUpgradeableReadLock(TimeSpan.Zero));
        try
        {
            Assert.AreEqual(1u, rwlock.LockingWriteCount);
        }
        finally
        {
            rwlock.ExitUpgradeableReadLock();
        }
        Assert.AreEqual(0u, rwlock.LockingWriteCount);
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        int nameIndex = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.IsTrue(nameIndex >= 0, methodName + " was not found.");
        int braceIndex = source.IndexOf('{', nameIndex);
        Assert.IsTrue(braceIndex >= 0, methodName + " body was not found.");

        int depth = 0;
        for (int i = braceIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(braceIndex, i - braceIndex + 1);
                }
            }
        }

        Assert.Fail(methodName + " body was not closed.");
        return string.Empty;
    }

    private static string ExtractBlockBody(string source, string blockHeader)
    {
        int headerIndex = source.IndexOf(blockHeader, StringComparison.Ordinal);
        Assert.IsTrue(headerIndex >= 0, blockHeader + " was not found.");
        int braceIndex = source.IndexOf('{', headerIndex);
        Assert.IsTrue(braceIndex >= 0, blockHeader + " body was not found.");

        int depth = 0;
        for (int i = braceIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(braceIndex, i - braceIndex + 1);
                }
            }
        }

        Assert.Fail(blockHeader + " body was not closed.");
        return string.Empty;
    }

    private static string FindRepositoryRoot()
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "BeMusicSeeker.sln")))
            {
                return directory;
            }
            DirectoryInfo parent = Directory.GetParent(directory);
            directory = parent == null ? string.Empty : parent.FullName;
        }
        Assert.Fail("Repository root was not found.");
        return string.Empty;
    }
}
