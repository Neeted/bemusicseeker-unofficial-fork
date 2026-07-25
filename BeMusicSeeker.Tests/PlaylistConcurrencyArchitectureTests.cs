using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistConcurrencyArchitectureTests
{
    [TestMethod]
    public void ExternalTableRegistration_DoesNotMutateVisibleCollectionInsideRegistrationWriterBlock()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BmsLibraryInternal", "PlaylistExternalSyncOwner.cs"));
        string method = ExtractMethodBody(source, "internal async Task<BMSTable> RegistrateExternalTableAsync(\n        BMSTable bmsTable");
        string commitAndAddMethod = ExtractMethodBody(source, "private async Task CommitAndAddBMSTableAsync");

        StringAssert.Contains(method, "await CommitAndAddBMSTableAsync(bmsTable).ConfigureAwait(false)");
        Assert.IsFalse(
            method.Contains("BMSTables.Add(bMSTable);"),
            "External registration must not call DispatcherCollection.Add while the registration writer lock is held.");
        Assert.IsFalse(
            commitAndAddMethod.Contains("ReaderWriterLock.GetWriterGuard()"),
            "External registration owner must keep collection mutation in the composed visible-collection port.");
        StringAssert.Contains(commitAndAddMethod, "playlistAggregatePersistenceOwner.CommitTablesWithEntries(");
        StringAssert.Contains(source, "playlistAggregatePersistenceOwner.TryBeginRegistration()");
    }

    [TestMethod]
    public void ExternalTableRegistration_DoesNotCommitDatabaseInsideRegistrationWriterBlock()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BmsLibraryInternal", "PlaylistExternalSyncOwner.cs"));
        string method = ExtractMethodBody(source, "internal async Task<BMSTable> RegistrateExternalTableAsync(\n        BMSTable bmsTable");

        Assert.IsFalse(
            method.Contains("CommitTablesWithEntries("),
            "External registration preparation must not perform durable writes before the owner commit.");
        StringAssert.Contains(method, "await CommitAndAddBMSTableAsync(bmsTable).ConfigureAwait(false)");
    }

    [TestMethod]
    public void ExternalTableBatchRegistration_DoesNotCommitDatabaseInsideRegistrationWriterBlock()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BmsLibraryInternal", "PlaylistExternalSyncOwner.cs"));
        string method = ExtractMethodBody(source, "internal async Task<RegisteredExternalTableBatchResult> RegistrateExternalTablesAsync");
        string commitAndAddMethod = ExtractMethodBody(source, "private async Task CommitAndAddBMSTablesAsync");

        Assert.IsFalse(
            method.Contains("CommitTablesWithEntries("),
            "Batch external registration preparation must not perform durable writes before the owner commit.");
        StringAssert.Contains(method, "await CommitAndAddBMSTablesAsync(tableList).ConfigureAwait(false)");
        StringAssert.Contains(method, "applyCachedPlaylistUrlCompletions?.Invoke(tableList, operationReason)");
        Assert.IsFalse(
            commitAndAddMethod.Contains("ReaderWriterLock.GetWriterGuard()"),
            "Batch external registration owner must keep collection mutation in the composed visible-collection port.");
        StringAssert.Contains(commitAndAddMethod, "playlistAggregatePersistenceOwner.CommitTablesWithEntries(");
        StringAssert.Contains(source, "playlistAggregatePersistenceOwner.TryBeginRegistration()");
    }

    [TestMethod]
    public void ExternalTableRegistration_HasNoBroadPreparationCallbackParameters()
    {
        Type[] constructorParameterTypes = typeof(PlaylistExternalSyncOwner)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        CollectionAssert.DoesNotContain(constructorParameterTypes, typeof(Action<Action>));
        CollectionAssert.DoesNotContain(constructorParameterTypes, typeof(Func<IReadOnlyList<BMSTable>>));
    }

    [TestMethod]
    public void PlaylistUrlCompletion_HasNoStaticMutableDelegateMembers()
    {
        var staticDelegateMembers = typeof(BMSPlaylist)
            .GetMembers(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(member => member switch
            {
                FieldInfo field => typeof(Delegate).IsAssignableFrom(field.FieldType),
                PropertyInfo property => typeof(Delegate).IsAssignableFrom(property.PropertyType),
                _ => false
            })
            .ToArray();

        Assert.AreEqual(0, staticDelegateMembers.Length, "URL completion source providers must be instance-owned.");
    }

    [TestMethod]
    public void PlaylistEntryBatchCommit_AcquiresTableWriterLocksBeforeDatabaseTransaction()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string method = ExtractMethodBody(source, "internal void CommitBMSTablesWithEntriesToDB");
        string ownerSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BmsLibraryInternal", "PlaylistAggregatePersistenceOwner.cs"));
        string ownerMethod = ExtractMethodBody(ownerSource, "private void ExecuteTablesWithEntriesCommit(");
        int sortIndex = ownerMethod.IndexOf(".OrderBy(table => table.playlist_id ?? int.MaxValue)", StringComparison.Ordinal);
        int tableLockIndex = ownerMethod.IndexOf("writerGuards.Add(table.ReaderWriterLock.GetWriterGuard())", StringComparison.Ordinal);
        int repositoryWriteIndex = ownerMethod.IndexOf("PersistTablesWithEntriesUnsafe(tableList, progressCallback)", StringComparison.Ordinal);

        Assert.IsTrue(sortIndex >= 0, "Batch entry commit must sort table writers by stable playlist identity.");
        Assert.IsTrue(tableLockIndex >= 0, "Batch entry commit must acquire table writer locks explicitly.");
        Assert.IsTrue(repositoryWriteIndex >= 0, "Batch entry commit must delegate the durable write to the playlist aggregate persistence owner.");
        Assert.IsTrue(
            sortIndex < tableLockIndex && tableLockIndex < repositoryWriteIndex,
            "Batch entry commit must keep the existing lock order: table writer lock before playlist repository transaction.");
        StringAssert.Contains(method, "playlistAggregatePersistenceOwner.CommitTablesWithEntries(");
        Assert.IsFalse(method.Contains("ReaderWriterLock.GetWriterGuard()"));
    }

    [TestMethod]
    public void PlaylistVisibleCollectionMutations_AreDispatchedThroughDedicatedBoundary()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));

        StringAssert.Contains(source, "InvokeBMSTablesCollectionMutation");
        StringAssert.Contains(source, "GetBMSTablesDispatcher");
    }

    [TestMethod]
    public void CustomFolderCommit_HydratesBeforeHoldingTableWriterLock()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        foreach (string methodName in new[]
        {
            "internal void ReOutputCustomFolderAndCommitToDB(BMSTable bmsTable)",
            "internal void MigrateCustomFolderOutputDirectoryAndCommitToDB("
        })
        {
            string method = ExtractMethodBody(source, methodName);
            int hydrationIndex = method.IndexOf("EnsurePlaylistEntriesLoaded(", StringComparison.Ordinal);
            int writerIndex = method.IndexOf("using (bmsTable.ReaderWriterLock.GetWriterGuard())", StringComparison.Ordinal);
            int commitIndex = method.IndexOf("playlistAggregatePersistenceOwner.CommitTablesWithEntries(", StringComparison.Ordinal);

            Assert.IsTrue(hydrationIndex >= 0, methodName + " must hydrate before the output commit.");
            Assert.IsTrue(writerIndex >= 0, methodName + " must hold the table writer across commit/output.");
            Assert.IsTrue(commitIndex >= 0, methodName + " must use the aggregate persistence owner.");
            Assert.IsTrue(
                hydrationIndex < writerIndex && writerIndex < commitIndex,
                methodName + " must not wait for hydration while holding the table writer lock.");
        }
    }

    [TestMethod]
    public void PlaylistHydrationOwner_PublishesReceiptInsteadOfCallbackLists()
    {
        string ownerSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "BeMusicSeeker",
            "Models",
            "BmsLibraryInternal",
            "PlaylistEntriesHydrationOwner.cs"));
        Assert.IsFalse(ownerSource.Contains("Action<PlaylistTableUpdateContext>"));
        Assert.IsFalse(ownerSource.Contains("pendingUpdateCallbacks"));
        Assert.IsFalse(ownerSource.Contains("pendingCompletionActions"));
        StringAssert.Contains(ownerSource, "PlaylistEntriesHydrationReceipt");
        StringAssert.Contains(ownerSource, "HydrationReceiptPublished");
        StringAssert.Contains(ownerSource, "PublishHydrationReceipt(");
        StringAssert.Contains(ownerSource, "out failedRetryRequested");
    }

    [TestMethod]
    public void PlaylistHydrationReceipt_UsesLockedImmutableReferenceSnapshots()
    {
        string ownerSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "BeMusicSeeker",
            "Models",
            "BmsLibraryInternal",
            "PlaylistEntriesHydrationOwner.cs"));
        string workspaceSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistWorkspaceViewModel.PlaylistStoreNotifications.cs"));
        string referenceApplySource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistReferenceApplyWorkflowOwner.cs"));

        StringAssert.Contains(ownerSource, "using (table.ReaderWriterLock.GetReaderGuard())");
        StringAssert.Contains(ownerSource, "new PlaylistReferenceTableSnapshot(");
        StringAssert.Contains(ownerSource, "internal PlaylistReferenceTableSnapshot ReferenceSnapshot { get; }");
        StringAssert.Contains(referenceApplySource, "SynchronizeReferenceBMSTableSnapshots(");
        StringAssert.Contains(referenceApplySource, "ApplyHydrationReceipt(");
        Assert.IsFalse(workspaceSource.Contains("SynchronizeReferenceBMSTableSnapshots("));
        Assert.IsFalse(workspaceSource.Contains("Select(fact => fact.Table)"));
    }

    [TestMethod]
    public void PlaylistHydrationCompletion_DoesNotQueueMutableReferenceResnapshot()
    {
        string viewModelSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs"));
        string hydrationCompletion = ExtractMethodBody(
            viewModelSource,
            "private void PlaylistWorkspacePlaylistEntriesHydrationCompleted(");

        Assert.IsFalse(
            hydrationCompletion.IndexOf("QueuePlaylistReferenceApply(", StringComparison.Ordinal) >= 0,
            "Hydration receipt consumption must not enqueue a mutable table resnapshot route.");
        StringAssert.Contains(hydrationCompletion, "TryCompleteStartupProgressPlaylistReferenceFromHydration(");
    }

    [TestMethod]
    public void PlaylistHydrationContinuation_PreservesCustomRepairAndSeparatesBmtFailure()
    {
        string playlistSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "BeMusicSeeker",
            "Models",
            "BMSPlaylist.cs"));
        StringAssert.Contains(playlistSource, "RunCustomFolderOutputRepairAfterHydration");
        StringAssert.Contains(playlistSource, "runCustomFolderOutputRepairAfterHydration: true");
        StringAssert.Contains(playlistSource, "playlist_entries_hydration_custom_folder_repair_failed");
        StringAssert.Contains(playlistSource, "playlist_entries_hydration_bmt_export_failed");
    }

    [TestMethod]
    public void PlaylistUrlCompletionSettings_UseDedicatedProviderBoundary()
    {
        string playlistSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string urlCompletionSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.UrlCompletion.cs"));

        Assert.IsFalse(playlistSource.Contains("Settings.Default.EnablePlaylistUrlCompletion"));
        Assert.IsFalse(urlCompletionSource.Contains("Settings.Default."));
        StringAssert.Contains(playlistSource, "IsPlaylistUrlCompletionEnabled,");
        StringAssert.Contains(urlCompletionSource, "GetPlaylistUrlCompletionOptions()");
        StringAssert.Contains(urlCompletionSource, "tsvResult.Snapshot.Candidates");
        StringAssert.Contains(urlCompletionSource, "stellaResult.Snapshot.Candidates");
    }

    [TestMethod]
    public void BeatorajaBmtSettings_UseDedicatedProviderBoundary()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string ownerSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BmsLibraryInternal", "PlaylistBmtOutputOwner.cs"));

        foreach (string pathSource in new[] { source, ownerSource })
        {
            Assert.IsFalse(pathSource.Contains("Settings.Default.EnableBeatorajaBmtOutput"));
            Assert.IsFalse(pathSource.Contains("Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled"));
            Assert.IsFalse(pathSource.Contains("Settings.Default.BeatorajaRootPath"));
            Assert.IsFalse(pathSource.Contains("Settings.Default.BeatorajaBmtTablePath"));
            Assert.IsFalse(pathSource.Contains("Settings.Default.RegisterBeatorajaBmtUrls"));
            Assert.IsFalse(pathSource.Contains("Settings.Default.BeatorajaBmtHashOutputMode"));
        }
        StringAssert.Contains(ownerSource, "GetOptions()");
    }

    [TestMethod]
    public void RecommendedTableWorkflow_IsOwnedByDedicatedOwner()
    {
        string root = FindRepositoryRoot();
        string playlistSource = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string ownerSource = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "PlaylistRecommendedTableOwner.cs"));
        string externalSyncSource = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "PlaylistExternalSyncOwner.cs"));

        StringAssert.Contains(playlistSource, "new PlaylistExternalSyncOwner(");
        StringAssert.Contains(externalSyncSource, "recommendedTableOwner.LoadWalkureTable");
        Assert.IsFalse(playlistSource.Contains("public BMSTable LoadWalkureTable"));
        foreach (string legacyMember in new[]
        {
            "estimationTableLock",
            "insaneTable",
            "overjoyTable",
            "updatedClearedSongs",
            "loadRecommendedTable",
            "setEstimationTable"
        })
        {
            Assert.IsFalse(playlistSource.Contains(legacyMember), "BMSPlaylist must not retain recommended-table member: " + legacyMember);
        }
        StringAssert.Contains(ownerSource, "internal BMSTable LoadWalkureTable");
        StringAssert.Contains(ownerSource, "UpdatedClearedSongs");
        StringAssert.Contains(ownerSource, "BuildEstimationEntries");
    }

    [TestMethod]
    public void CustomFolderProjectionAndMaterialization_UseDedicatedOwner()
    {
        string root = FindRepositoryRoot();
        string playlistSource = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string ownerSource = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "Models",
            "BmsLibraryInternal",
            "PlaylistCustomFolderOutputOwner.cs"));
        string maintenanceOwnerSource = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "Models",
            "BmsLibraryInternal",
            "PlaylistCustomFolderOutputMaintenanceOwner.cs"));

        StringAssert.Contains(playlistSource, "customFolderOutputOwner.CreateProjection");
        StringAssert.Contains(playlistSource, "customFolderOutputMaintenanceOwner.ReOutputProjectionsAsync");
        StringAssert.Contains(playlistSource, "CreateCustomFolderOutputPhysicalSurfaceFromGroupedEnumeration,");
        Assert.IsFalse(playlistSource.Contains("customFolderOutputOwner,\n            ResolveCustomFolderOutputPhysicalSurface"));
        Assert.IsFalse(playlistSource.Contains("private CustomFolderOutputProjection CreateCustomFolderOutputProjection("));
        Assert.IsFalse(playlistSource.Contains("private CustomFolderBatchMaterializationResult MaterializeCustomFolderOutputBatch("));
        foreach (string legacyHelper in new[]
        {
            "RemoveStaleManagedCustomFolderFiles",
            "EnumerateManagedCustomFolderFiles",
            "TryDeleteManagedCustomFolderFile",
            "AddCustomFolderPruneScopePath",
            "RemoveEmptyCustomFolderDirectories",
            "TryDeleteEmptyCustomFolderDirectory",
            "private static void WriteAllText("
        })
        {
            Assert.IsFalse(playlistSource.Contains(legacyHelper), "BMSPlaylist must not retain materialization writer helper: " + legacyHelper);
        }
        StringAssert.Contains(ownerSource, "internal CustomFolderOutputProjection CreateProjection(");
        StringAssert.Contains(ownerSource, "internal CustomFolderBatchMaterializationResult MaterializeBatch(");
        StringAssert.Contains(ownerSource, "RemoveStaleManagedFiles(");
        StringAssert.Contains(ownerSource, "WriteAllText(file.FilePath");
        StringAssert.Contains(maintenanceOwnerSource, "outputOwner.MaterializeBatch(");
    }

    [TestMethod]
    public void CustomFolderMaintenance_UsesDedicatedLifecycleOwner()
    {
        string root = FindRepositoryRoot();
        string playlistSource = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string ownerSource = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "Models",
            "BmsLibraryInternal",
            "PlaylistCustomFolderOutputMaintenanceOwner.cs"));

        foreach (string route in new[]
        {
            "customFolderOutputMaintenanceOwner.TryReOutputCustomFolder",
            "customFolderOutputMaintenanceOwner.TryMigrateCustomFolderOutputDirectory",
            "customFolderOutputMaintenanceOwner.TryRemoveCustomFolder",
            "customFolderOutputMaintenanceOwner.ReOutputTablesAsync",
            "customFolderOutputMaintenanceOwner.MigrateCustomFolderOutputDirectories",
            "customFolderOutputMaintenanceOwner.SyncRootFolderOutputDirectoriesToLr2Config",
            "customFolderOutputMaintenanceOwner.SyncCustomFolderOutputSearchRootsAfterSettingsChange"
        })
        {
            StringAssert.Contains(playlistSource, route);
        }

        foreach (string legacyRoute in new[]
        {
            "migrateCustomFolderOutputDirectoryFiles",
            "reOutputCustomFolderFiles",
            "DeleteCustomFolderOutputDirectoryTree",
            "CleanupMigratedCustomFolderOutputDirectories",
            "CreateCustomFolderMigrationProtectedOutputDirectories",
            "private bool removeCustomFolder("
        })
        {
            Assert.IsFalse(playlistSource.Contains(legacyRoute), "BMSPlaylist must not retain custom-folder maintenance route: " + legacyRoute);
        }

        StringAssert.Contains(ownerSource, "internal bool TryMigrateCustomFolderOutputDirectory(");
        StringAssert.Contains(ownerSource, "internal void MigrateCustomFolderOutputDirectories(");
        StringAssert.Contains(ownerSource, "internal bool TryRemoveCustomFolder(");
        StringAssert.Contains(ownerSource, "internal async Task<CustomFolderBatchOutputResult> ReOutputTablesAsync(");
        StringAssert.Contains(ownerSource, "CreateMigrationProtectedOutputDirectories(");
    }

    [TestMethod]
    public void CustomFolderOutputResolution_UsesDedicatedProviderBoundary()
    {
        string playlistSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string librarySource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSLibrary.cs"));
        string viewModelSource = SourceTextTestHelper.ReadMainWindowViewModelSourceText();

        StringAssert.Contains(playlistSource, "ResolveCustomFolderOutputDirectory(BMSTable bmsTable)");
        Assert.IsFalse(playlistSource.Contains("GetCustomFolderOutputDirectory(BMSTable bmsTable)"));
        StringAssert.Contains(playlistSource, "public static string GetCustomFolderOutputDirectory(");
        StringAssert.Contains(playlistSource, "string normalOutputBaseDirectory");
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
        string maintenanceOwnerSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "BeMusicSeeker",
            "Models",
            "BmsLibraryInternal",
            "PlaylistCustomFolderOutputMaintenanceOwner.cs"));
        string playlistWorkspaceSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistWorkspaceViewModel.cs"));
        string settingDialogSource = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();

        StringAssert.Contains(playlistSource, "SyncCustomFolderOutputSearchRootsAfterSettingsChange(");
        StringAssert.Contains(playlistSource, "customFolderOutputMaintenanceOwner.SyncCustomFolderOutputSearchRootsAfterSettingsChange(");
        StringAssert.Contains(maintenanceOwnerSource, "config.SetBMSSearchDirectories(nextDirectories);");
        StringAssert.Contains(settingDialogSource, "customFolderOutputPort.SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(");
        StringAssert.Contains(playlistWorkspaceSource, "ISettingsDialogCustomFolderOutputPort.SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(");
        StringAssert.Contains(playlistWorkspaceSource, "RepairRootCustomFolderOutputSearchRootsAfterStartup(");
        Assert.IsFalse(settingDialogSource.Contains("SyncRootCustomFolderOutputSearchRootsAfterSettingsChange"));
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
        StringAssert.Contains(source, "PlaylistWorkspace.RepairRootCustomFolderOutputSearchRootsAfterStartup(startupCustomFolderSettings)");
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
        string workspaceSource = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistWorkspaceViewModel.cs"));

        StringAssert.Contains(mainChartSource, "IMainChartColumnSettingsStore");
        StringAssert.Contains(workspaceSource, "IMainChartColumnSettingsStore");
        Assert.IsFalse(mainChartSource.Contains("Settings.Default."));
        Assert.IsFalse(workspaceSource.Contains("Settings.Default."));

        string compositionSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "ApplicationComposition.cs");
        StringAssert.Contains(compositionSource, "new SettingsMainChartColumnSettingsStore(() => this.settingsEditSession.Values)");
        Assert.IsFalse(compositionSource.Contains("mainChartColumnSettingsStore: new SettingsMainChartColumnSettingsStore()"));

        StringAssert.Contains(compositionSource, "new SettingsKeywordSearchHistorySettingsStore(() => this.settingsEditSession.Values)");
        StringAssert.Contains(compositionSource, "new SettingsPlayHistoryDisplaySettingsStore(() => this.settingsEditSession.Values)");
        StringAssert.Contains(compositionSource, "customFolderOutputSettingsProvider);");

        string bulkDialogSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistWorkspaceViewModel.PlaylistSummaryBulkEdit.cs");
        StringAssert.Contains(bulkDialogSource, "ownerWorkspace.CreatePlaylistCustomFolderOutputBaseOptionsForCurrentSettings");
        Assert.IsFalse(bulkDialogSource.Contains("CreatePlaylistCustomFolderOutputBaseOptions(includeNoChange"));

        string settingDialogSource = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        Assert.IsFalse(settingDialogSource.Contains("useCurrentSettingsWhenMissing"));
        Assert.IsFalse(settingDialogSource.Contains("Settings.Default."));
        Assert.IsFalse(settingDialogSource.Contains("ReadAdditionalBaseDirectories()"));

        string summaryBuildSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistWorkspaceViewModel.PlaylistSummaryBuild.cs");
        StringAssert.Contains(summaryBuildSource, "customFolderOutputSettingsProvider()");
        Assert.IsFalse(summaryBuildSource.Contains("GetDisplayName(table.custom_folder_output_base_name)"));
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

        StringAssert.Contains(source, "applicationLifetime.IsFirstStartup");
        StringAssert.Contains(source, "applicationLifetime.CompleteFirstStartup();");
        Assert.IsFalse(source.Contains("firstStartupProvider"));
        Assert.IsFalse(source.Contains("completeFirstStartup"));
        Assert.IsFalse(source.Contains("((App)System.Windows.Application.Current).firstStartup"));
    }

    [TestMethod]
    public void SettingDialogStartupMessage_UsesViewModelLifecycleBoundary()
    {
        string viewSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Views",
            "SettingDialog.cs");
        string viewModelSource = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();

        StringAssert.Contains(viewModelSource, "applicationLifetime.IsFirstStartup");
        Assert.IsFalse(viewModelSource.Contains("firstStartupStatePort"));
        StringAssert.Contains(viewModelSource, "Msg_initsetting_completed");
        Assert.IsFalse(viewSource.Contains("firstStartup"));
        Assert.IsFalse(viewSource.Contains("((App)Application.Current).firstStartup"));
    }

    [TestMethod]
    public void SettingDialogSettingsPersistence_UsesCompositionBoundary()
    {
        string source = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();

        StringAssert.Contains(source, "this.settingsEditSession.Reload();");
        StringAssert.Contains(source, "settingsEditSession.Save();");
        Assert.IsFalse(source.Contains("reloadSettings"));
        Assert.IsFalse(source.Contains("saveSettings"));
        Assert.IsFalse(source.Contains("Settings.Default.Reload();"));
        Assert.IsFalse(source.Contains("Settings.Default.Save();"));
    }

    [TestMethod]
    public void SettingDialogSettingsValues_UseInjectedEditSession()
    {
        string settingDialogSource = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
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
        Assert.IsFalse(settingDialogSource.Contains("private readonly Action reloadSettings"));
        Assert.IsFalse(settingDialogSource.Contains("private readonly Action saveSettings"));
        Assert.IsFalse(settingDialogOwnerSource.Contains("Settings.Default."));
        StringAssert.Contains(mainWindowSource, "statePort: this,");
        StringAssert.Contains(mainWindowSource, "workspacePort: PlaylistWorkspace,");
        StringAssert.Contains(mainWindowSource, "customFolderOutputPort: PlaylistWorkspace,");
        StringAssert.Contains(mainWindowSource, "playerFactoryPort: applicationComposition,");
        StringAssert.Contains(mainWindowSource, "playbackRuntimePort: PlaybackPanel,");
        Assert.IsTrue(mainWindowSource.IndexOf("ISettingsDialogPlaybackPort", StringComparison.Ordinal) < 0);
        StringAssert.Contains(mainWindowSource, "applicationComposition.CreateSettingDialogViewModel(");
        Assert.IsFalse(mainWindowSource.Contains("CreateSettingsDialogApplicationContext"));
        Assert.IsFalse(mainWindowSource.Contains("new SettingDialogViewModel(this)"));
        StringAssert.Contains(settingDialogSource, "await statePort.ReloadScoresOnlyAsync();");
        StringAssert.Contains(settingDialogSource, "await statePort.ReloadFileDiffAsync();");
        Assert.IsFalse(settingDialogSource.Contains("reloadScoresOnly"));
        Assert.IsFalse(settingDialogSource.Contains("reloadFileDiff"));
        StringAssert.Contains(settingDialogSource, "statePort.LibraryOperationAvailabilityChanged +=");
        StringAssert.Contains(settingDialogSource, "statePort.Lr2PlayHistorySchemaStatusChanged +=");
        Assert.IsFalse(settingDialogSource.Contains("ApplyLr2PlayHistorySchemaCheckResultFromRead"));
        Assert.IsFalse(settingDialogSource.Contains("ResetLr2PlayHistorySchemaStatusFromLibrary"));
        Assert.IsFalse(settingDialogSource.Contains("SubscribeStateChanges"));
        Assert.IsFalse(settingDialogSource.Contains("UnsubscribeStateChanges"));
        StringAssert.Contains(compositionSource, "ISettingsEditSession settingsEditSession = null");
        StringAssert.Contains(compositionSource, "settingsEditSession,");
        Assert.IsFalse(compositionSource.Contains("Func<MainWindowViewModel, Task<bool>> initializeOwner"));
        Assert.IsFalse(compositionSource.Contains("Func<MainWindowViewModel, Task> reloadScoresOnly"));
        Assert.IsFalse(compositionSource.Contains("Func<MainWindowViewModel, Task> reloadFileDiff"));
        Assert.IsFalse(compositionSource.Contains("reloadSettings"));
        Assert.IsFalse(compositionSource.Contains("saveSettings"));
        StringAssert.Contains(mainWindowSource, "Task<bool> ISettingsDialogStatePort.InitializeLibraryAsync()");
        StringAssert.Contains(mainWindowSource, "Task ISettingsDialogStatePort.ReloadScoresOnlyAsync()");
        StringAssert.Contains(mainWindowSource, "Task ISettingsDialogStatePort.ReloadFileDiffAsync()");
        StringAssert.Contains(mainWindowSource, "event EventHandler ISettingsDialogStatePort.LibraryOperationAvailabilityChanged");
        StringAssert.Contains(mainWindowSource, "event Action<Lr2PlayHistorySchemaStatusSnapshot> ISettingsDialogStatePort.Lr2PlayHistorySchemaStatusChanged");
        Assert.IsFalse(mainWindowSource.Contains("SettingDialog.ApplyLr2PlayHistorySchemaCheckResult"));
        Assert.IsFalse(mainWindowSource.Contains("SettingDialog.ClearLr2PlayHistorySchemaStatus"));
        Assert.IsFalse(mainWindowSource.Contains("private readonly Action reloadSettings"));
        Assert.IsFalse(mainWindowSource.Contains("private readonly Action saveSettings"));
        Assert.IsFalse(mainWindowSource.Contains("ISettingsDialogStatePort.SubscribeStateChanges"));
        Assert.IsFalse(mainWindowSource.Contains("ISettingsDialogStatePort.UnsubscribeStateChanges"));
    }

    [TestMethod]
    public void ShutdownSettingsPersistence_UsesCompositionBoundary()
    {
        string viewModelSource = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string mainWindowSource = SourceTextTestHelper.ReadMainWindowSourceText();
        string shutdownOwnerSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "ShellShutdownWorkflowOwner.cs");

        StringAssert.Contains(shutdownOwnerSource, "private readonly ISettingsEditSession settingsEditSession;");
        StringAssert.Contains(shutdownOwnerSource, "settingsEditSession.Save();");
        Assert.IsFalse(viewModelSource.Contains("private readonly Action saveSettings"));
        Assert.IsFalse(viewModelSource.Contains("SaveSettingsForShutdown"));
        StringAssert.Contains(mainWindowSource, "CaptureWindowStateForClosing();");
        StringAssert.Contains(mainWindowSource, "CompleteTerminalShutdown();");
        StringAssert.Contains(mainWindowSource, "private bool windowStateCapturedForClosing;");
        string applyTerminalShutdown = ExtractMethodBody(mainWindowSource, "private void ApplyTerminalShutdown()");
        Assert.IsTrue(
            HasCaptureBeforeTerminalCompletion(applyTerminalShutdown));
        string closedHandler = ExtractMethodBody(mainWindowSource, "private void MainWindow_Closed");
        Assert.IsTrue(
            HasCaptureBeforeTerminalCompletion(closedHandler));
        string closingHandler = ExtractMethodBody(mainWindowSource, "protected override void OnClosing");
        Assert.IsTrue(
            HasCaptureBeforeTerminalCompletion(closingHandler));
        string captureMethod = ExtractMethodBody(mainWindowSource, "private void CaptureWindowStateForClosing()");
        StringAssert.Contains(captureMethod, "if (windowStateCapturedForClosing)");
        Assert.IsFalse(captureMethod.Contains("SaveSettingsForShutdown"));
        Assert.IsFalse(captureMethod.Contains("SettingsEditSession.Save"));
        Assert.IsFalse(mainWindowSource.Contains("SaveSettingsForShutdown"));
        Assert.IsFalse(mainWindowSource.Contains("SettingsEditSession.Save();"));
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
    public void KeywordSearchHistory_UsesSettingsStoreBoundary()
    {
        string chartFilterSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "ChartListFilterViewModel.cs");
        string playlistWorkspaceKeywordSearchSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistWorkspaceViewModel.KeywordSearch.cs");
        string playlistWorkspaceSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistWorkspaceViewModel.cs");

        StringAssert.Contains(chartFilterSource, "keywordSearchHistorySettingsStore.KeywordSearchHistory");
        StringAssert.Contains(playlistWorkspaceSource, "keywordSearchHistorySettingsStore.PlaylistSummaryKeywordSearchHistory");
        Assert.IsFalse(playlistWorkspaceKeywordSearchSource.Contains("ConfigureKeywordSearchHistory"));
        StringAssert.Contains(playlistWorkspaceKeywordSearchSource, "IKeywordSearchHistorySettingsStore playlistSummaryKeywordSearchHistorySettingsStore");
        Assert.IsFalse(chartFilterSource.Contains("Settings.Default.KeywordSearchHistory"));
        Assert.IsFalse(chartFilterSource.Contains("Settings.Default.PlaylistSummaryKeywordSearchHistory"));
        Assert.IsFalse(playlistWorkspaceKeywordSearchSource.Contains("Settings.Default.KeywordSearchHistory"));
        Assert.IsFalse(playlistWorkspaceKeywordSearchSource.Contains("Settings.Default.PlaylistSummaryKeywordSearchHistory"));
    }

    [TestMethod]
    public void PlayHistoryDisplaySettings_UseSettingsStoreBoundary()
    {
        string mainWindowSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");
        string settingDialogSource = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();

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
        StringAssert.Contains(source, "applicationComposition.CreateDefaultBmsPlayer");
        Assert.IsFalse(source.Contains("new uBMplay("));
        Assert.IsFalse(source.Contains("new BMIIDXView2015("));
        Assert.IsFalse(source.Contains("new LR2body("));
        Assert.IsFalse(source.Contains("new InternalBMSAutoPlayerSoundOnly("));

        string compositionSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "ApplicationComposition.cs");
        StringAssert.Contains(compositionSource, "defaultBmsPlayerFactory");
        StringAssert.Contains(
            compositionSource,
            "new InternalBMSAutoPlayerSoundOnly(playerSettingsGateway, new BassAudioPlaybackRuntime())");
    }

    [TestMethod]
    public void PlaybackPlayersUseThePlayerSettingsGatewayBoundary()
    {
        string compositionSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "ApplicationComposition.cs");
        string gatewaySource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Models",
            "PlayerSettingsGateway.cs");

        StringAssert.Contains(compositionSource, "IPlayerSettingsGateway playerSettingsGateway");
        StringAssert.Contains(compositionSource, "new SettingsPlayerSettingsGateway(() => this.settingsEditSession.Values)");
        StringAssert.Contains(gatewaySource, "void ApplyNegotiatedAudioSettings(");
        StringAssert.Contains(gatewaySource, "void SaveWindowPlacement(Win32API.WINDOWPLACEMENT windowPlacement)");

        string internalPlayerSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "Models", "InternalBMSAutoPlayerSoundOnly.cs");
        string bmiIdxSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "Models", "BMIIDXView2015.cs");
        string ubmplaySource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "Models", "uBMplay.cs");
        string lr2Source = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "Models", "LR2", "LR2body.cs");

        foreach (string source in new[] { internalPlayerSource, bmiIdxSource, ubmplaySource, lr2Source })
        {
            StringAssert.Contains(source, "IPlayerSettingsGateway");
            Assert.IsFalse(source.Contains("Settings.Default"));
        }
    }

    [TestMethod]
    public void AudioFeatureOwnersDoNotReferenceBassAdaptersDirectly()
    {
        string settingsDialogSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "SettingsDialogViewModel.cs");
        string internalPlayerSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Models",
            "InternalBMSAutoPlayerSoundOnly.cs");
        string conversionSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "SelectedChartAudioConversionWorkflowOwner.cs");

        Assert.IsFalse(settingsDialogSource.Contains("BassAudioMapping"));
        Assert.IsFalse(settingsDialogSource.Contains("BassAudioPlayer"));
        Assert.IsFalse(settingsDialogSource.Contains("BassAudioDeviceTestRuntime"));
        Assert.IsFalse(settingsDialogSource.Contains("new AudioDeviceTestWorkflowOwner("));
        Assert.IsFalse(internalPlayerSource.Contains("BassAudioPlayer"));
        int adapterStart = conversionSource.IndexOf(
            "internal sealed class BassSelectedChartAudioConversionExecutor",
            StringComparison.Ordinal);
        Assert.IsTrue(adapterStart > 0);
        string conversionOwnerSource = conversionSource.Substring(0, adapterStart);
        Assert.IsFalse(conversionOwnerSource.Contains("BassAudioMapping"));
        Assert.IsFalse(conversionOwnerSource.Contains("BassAudioPlayer"));
    }

    [TestMethod]
    public void MainWindowViewSettingsUseTheViewHostStoreBoundary()
    {
        string mainWindowSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Views",
            "MainWindow.cs");
        string mainWindowXaml = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Views",
            "MainWindow.xaml");
        string viewModelSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");
        string settingsStoreSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "MainWindowViewSettingsStore.cs");

        StringAssert.Contains(viewModelSource, "public IMainWindowViewSettingsStore ViewSettings");
        StringAssert.Contains(mainWindowXaml, "{Binding ViewSettings.TreeViewWidth, Mode=OneWay}");
        StringAssert.Contains(mainWindowXaml, "DataContext.ViewSettings.CustomTableRowHeight");
        StringAssert.Contains(mainWindowXaml, "ViewSettings.StartupSelectInstallPending");
        StringAssert.Contains(settingsStoreSource, "SettingsMainWindowViewSettingsStore");
        StringAssert.Contains(settingsStoreSource, "CaptureTreeViewWidth(");
        StringAssert.Contains(settingsStoreSource, "CaptureWindowPlacement(");
        Assert.IsFalse(mainWindowSource.Contains("Settings.Default"));
        Assert.IsFalse(mainWindowXaml.Contains("prop:Settings.Default"));
        Assert.IsFalse(settingsStoreSource.Contains("settingsProvider = null"));
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
        Assert.IsFalse(source.Contains("PlaylistSummaryColumnSettingsCoordinator"));
        Assert.IsFalse(source.Contains("new PlaylistSummaryBmtSortCoordinator("));
        Assert.IsFalse(source.Contains("new RegularChartListOwner("));
        Assert.IsFalse(source.Contains("new DropInstallQueueProcessor("));
        string compositionSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "ApplicationComposition.cs");
        string coordinatorSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistSummaryBmtSortCoordinator.cs");
        StringAssert.Contains(compositionSource, "new PlaylistSummaryBmtSortCoordinator(tablesProvider, tableSnapshotProvider)");
        Assert.IsFalse(compositionSource.Contains("ConfigureSummaryBmtSort"));
        Assert.IsFalse(compositionSource.Contains("Func<string, bool, bool, long> refreshPlaylistSummary"));
        Assert.IsFalse(coordinatorSource.Contains("refreshPlaylistSummary"));
    }

    [TestMethod]
    public void MainWindowRuntimeSettings_UseCompositionEditSession()
    {
        string source = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");

        StringAssert.Contains(source, "applicationComposition.SettingsEditSession,");
        StringAssert.Contains(source, "GetStartupSettingsSnapshot()");
        Assert.IsFalse(source.Contains("Settings.Default."));
    }

    [TestMethod]
    public void CommittedPlaylistVisibleCollectionReflection_IsNotCanceledAfterDatabaseCommit()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BmsLibraryInternal", "PlaylistExternalSyncOwner.cs"));
        string addMethod = ExtractMethodBody(source, "private async Task CommitAndAddBMSTableAsync");

        Assert.IsFalse(addMethod.Contains("CancellationToken"), "Post-commit visible collection reflection must not accept a cancellation token.");
        StringAssert.Contains(addMethod, "addSingleVisibleTable(table)");
    }

    [TestMethod]
    public void ExternalTableImportContinuation_DoesNotReturnReferenceIndexWorkToUiThread()
    {
        string workspaceSource = SourceTextTestHelper.ReadPlaylistWorkspaceViewModelSourceText();
        string drainMethod = ExtractMethodBody(workspaceSource, "private async Task DrainExternalPlaylistImportQueueAsync");
        string completionMethod = ExtractMethodBody(workspaceSource, "internal bool CompleteImportedPlaylistRegistrations");
        string duplicatePreparationMethod = ExtractMethodBody(workspaceSource, "private List<ExternalPlaylistImportWorkItem> PrepareExternalPlaylistImportRegistrationItems");
        string duplicateSkipMethod = ExtractMethodBody(workspaceSource, "private void RecordExternalPlaylistImportDuplicateNameSkip");

        StringAssert.Contains(drainMethod, ".ConfigureAwait(false)");
        StringAssert.Contains(drainMethod, "externalPlaylistImportQueue.DequeueBatch()");
        StringAssert.Contains(drainMethod, "await tables.ExternalSyncOwner.LoadExternalTableSnapshotsAsync(");
        StringAssert.Contains(drainMethod, "schedulePlaylistUrlCompletionRefresh: false");
        StringAssert.Contains(drainMethod, "await tables.ExternalSyncOwner.RegistrateExternalTablesAsync(");
        StringAssert.Contains(drainMethod, "catch (PlaylistAlreadyExistsException ex)");
        StringAssert.Contains(drainMethod, "RecordExternalPlaylistImportDuplicateNameSkip(item, ex.PlaylistName, ex, outcomes)");
        StringAssert.Contains(drainMethod, "Playlist_import_progress_phase_check_duplicates");
        StringAssert.Contains(drainMethod, "Playlist_import_progress_phase_update_references");
        StringAssert.Contains(drainMethod, "PlaylistSyncAttemptResult.CreateFailure(item.LoadedTable, item.LoadedTable, item.Uri, referenceUpdateException)");
        StringAssert.Contains(completionMethod, "files.AddReferenceBMSTablesIncremental(tableList);");
        StringAssert.Contains(completionMethod, "QueuePlaylistSummaryDataRefreshFromImport(");
        StringAssert.Contains(workspaceSource, "private void QueuePlaylistSummaryDataRefreshFromImport(");
        StringAssert.Contains(workspaceSource, "dispatchPresentation(() =>");
        StringAssert.Contains(workspaceSource, "RequestPlaylistSummaryDataRefresh(");
        StringAssert.Contains(duplicatePreparationMethod, "GetExternalPlaylistImportExistingNamesSnapshot()");
        StringAssert.Contains(duplicateSkipMethod, "ExternalPlaylistImportOutcome.SkippedDuplicateName");
        Assert.IsFalse(
            drainMethod.Contains("await tables.ExternalSyncOwner.RegistrateExternalTableAsync("),
            "Bulk URL import should not serialize external requests through the single-table registration API.");
        Assert.IsFalse(
            completionMethod.Contains("tables.AcquireReaderLockBMSTables();"),
            "Import reference index updates must not hold the playlist collection reader lock while scanning library state.");
    }

    [TestMethod]
    public void BeatorajaTableUrlImport_ParallelizesExternalLoadAndBatchesRegistrationWork()
    {
        string workspaceSource = SourceTextTestHelper.ReadPlaylistWorkspaceViewModelSourceText();
        string rootSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string importMethod = ExtractMethodBody(workspaceSource, "private async Task ImportBeatorajaTableUrlsAsync");
        string completionMethod = ExtractMethodBody(workspaceSource, "internal bool CompleteImportedPlaylistRegistrations");

        StringAssert.Contains(importMethod, "await tables.ExternalSyncOwner.LoadExternalTableSnapshotsAsync(");
        StringAssert.Contains(importMethod, "schedulePlaylistUrlCompletionRefresh: false");
        StringAssert.Contains(importMethod, "await tables.ExternalSyncOwner.RegistrateExternalTablesAsync(");
        StringAssert.Contains(importMethod, "tables.CommitBMSTableHeadersToDB(rawUrlChangedTables);");
        StringAssert.Contains(importMethod, "tables.BmtOutput.QueueBeatorajaBmtExportForTables(rawUrlChangedTables");
        StringAssert.Contains(completionMethod, "files.AddReferenceBMSTablesIncremental(tableList);");
        StringAssert.Contains(importMethod, "BeatorajaTableUrlImportPostProgressStepCount");
        StringAssert.Contains(importMethod, "Beatoraja_table_url_import_progress_phase_register_playlists");
        StringAssert.Contains(importMethod, "Beatoraja_table_url_import_progress_phase_update_references");
        Assert.IsFalse(
            importMethod.Contains("await tables.ExternalSyncOwner.RegistrateExternalTableAsync("),
            "beatoraja Table URL import should not serialize external requests through the single-table registration API.");
        Assert.AreEqual(-1, rootSource.IndexOf("ImportBeatorajaTableUrlsAsync(", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("StartBeatorajaTableUrlImport(", StringComparison.Ordinal));
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

    private static bool HasCaptureBeforeTerminalCompletion(string source)
    {
        int captureIndex = source.IndexOf("CaptureWindowStateForClosing();", StringComparison.Ordinal);
        int completionIndex = source.IndexOf("CompleteTerminalShutdown();", StringComparison.Ordinal);
        return captureIndex >= 0 && completionIndex >= 0 && captureIndex < completionIndex;
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
