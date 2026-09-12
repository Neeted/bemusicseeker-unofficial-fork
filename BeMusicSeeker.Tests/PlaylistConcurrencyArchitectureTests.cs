using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistConcurrencyArchitectureTests
{
    [TestMethod]
    public void ModelObservableState_UsesBclNotificationContract()
    {
        string modelsRoot = Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models");
        string[] modelSources = Directory.GetFiles(modelsRoot, "*.cs", SearchOption.AllDirectories);

        foreach (string modelSourcePath in modelSources)
        {
            string source = File.ReadAllText(modelSourcePath);
            Assert.IsFalse(source.Contains("Livet.NotificationObject"), modelSourcePath);
            Assert.IsFalse(source.Contains("PropertyChangedEventListener"), modelSourcePath);
        }

        Assert.AreEqual(typeof(ObservableObject), typeof(BMSLibrary).BaseType);
        Assert.AreEqual(typeof(ObservableObject), typeof(BMSPlaylist).BaseType);
        Assert.IsTrue(typeof(INotifyPropertyChanged).IsAssignableFrom(typeof(BMSLibrary)));
        Assert.IsTrue(typeof(INotifyPropertyChanged).IsAssignableFrom(typeof(BMSPlaylist)));

        Type notificationObjectType = typeof(Livet.NotificationObject);
        Type[] modelTypes = typeof(ObservableObject).Assembly
            .GetTypes()
            .Where(type => type.IsClass
                && type.Namespace?.StartsWith("BeMusicSeeker.Models", StringComparison.Ordinal) == true)
            .ToArray();
        foreach (Type modelType in modelTypes)
        {
            Assert.IsFalse(
                notificationObjectType.IsAssignableFrom(modelType),
                modelType.FullName + " must not inherit Livet.NotificationObject.");
        }
    }

    [TestMethod]
    public void ModelPropertyMutation_PublishesExistingPropertyNameOnce()
    {
        BMSScore score = new();
        List<string> changedProperties = [];
        score.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName!);

        score.ranking = 7;
        score.ranking = 7;

        CollectionAssert.AreEqual(new[] { "ranking" }, changedProperties);
    }

    [TestMethod]
    public void ReaderWriterLock_PublishesLockCountThroughBclNotificationContract()
    {
        ReaderWriterLockSlimWrapper readerWriterLock = new();
        List<string> changedProperties = [];
        readerWriterLock.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName!);

        readerWriterLock.EnterReadLock();
        readerWriterLock.ExitReadLock();

        CollectionAssert.AreEqual(new[] { "LockingReadCount", "LockingReadCount" }, changedProperties);
        readerWriterLock.Dispose();
    }

    [TestMethod]
    public void PropertyChangedSubscription_ForwardsRegisteredPropertyAndStopsAfterDispose()
    {
        BMSScore score = new();
        int callbackCount = 0;
        PropertyChangedSubscription subscription = PropertyChangedSubscription.Create(score);
        subscription.RegisterHandler(() => score.ranking, () => callbackCount++);

        score.ranking = 1;
        subscription.Dispose();
        score.ranking = 2;

        Assert.AreEqual(1, callbackCount);
    }

    [TestMethod]
    public void UiSchedulerContractKeepsWpfThreadingTypesAtTerminalAdapter()
    {
        string portsSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "ApplicationContextPorts.cs"));
        int contractStart = portsSource.IndexOf("internal interface IUiScheduler", StringComparison.Ordinal);
        int adapterStart = portsSource.IndexOf("internal sealed class WpfUiScheduler", StringComparison.Ordinal);
        Assert.IsTrue(contractStart >= 0 && adapterStart > contractStart);
        string contract = portsSource.Substring(contractStart, adapterStart - contractStart);
        Assert.IsFalse(contract.Contains("Dispatcher"));
        StringAssert.Contains(contract, "IUiScheduledOperation");

        foreach (string relativePath in new[]
        {
            Path.Combine("BeMusicSeeker", "Models", "BMSLibrary.cs"),
            Path.Combine("BeMusicSeeker", "Models", "BMSPlaylist.cs"),
            Path.Combine("BeMusicSeeker", "Models", "BmsLibraryInternal", "BmsLibraryStateApplier.cs"),
            Path.Combine("BeMusicSeeker", "Models", "BmsLibraryInternal", "PackageLifecycleOwner.cs"),
            Path.Combine("BeMusicSeeker", "Models", "BmsLibraryInternal", "PlaylistAggregatePersistenceOwner.cs"),
            Path.Combine("BeMusicSeeker", "ViewModels", "ApplicationComposition.cs"),
            Path.Combine("BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"),
            Path.Combine("BeMusicSeeker", "ViewModels", "MainWindow", "LibraryFolderTreeViewModel.cs"),
            Path.Combine("BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistPropertyDialogViewModel.cs")
        })
        {
            string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath));
            Assert.IsFalse(source.Contains("System.Windows.Threading"), relativePath);
        }

        string terminalAdapter = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "BeMusicSeeker",
            "Views",
            "WpfPlaybackUiDispatcher.cs"));
        StringAssert.Contains(terminalAdapter, "UiSchedulePriority.DataBind");
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
    public void PlaylistHydrationReceipt_QueuesCurrentAtomicReferenceSynchronization()
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
        StringAssert.Contains(referenceApplySource, "ApplyHydrationReceipt(");
        StringAssert.Contains(referenceApplySource, "Queue(receipt.Reason, operationToken: 0L)");
        StringAssert.Contains(referenceApplySource, "PrepareReferenceBMSTableSynchronization(tables)");
        StringAssert.Contains(referenceApplySource, "TryCommitReferenceBMSTableSynchronization(synchronizationPlan)");
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
        StringAssert.Contains(ownerSource, "internal async Task<BMSTable> LoadWalkureTableAsync");
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
            "customFolderOutputMaintenanceOwner.TryReOutputPreparedCustomFolder",
            "customFolderOutputMaintenanceOwner.TryMigratePreparedCustomFolderOutputDirectory",
            "customFolderOutputMaintenanceOwner.TryRemoveCustomFolder",
            "customFolderOutputMaintenanceOwner.ReOutputPreparedTablesAsync",
            "customFolderOutputMaintenanceOwner.MigratePreparedCustomFolderOutputDirectories",
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

        StringAssert.Contains(ownerSource, "internal CustomFolderBatchOutputResult TryMigratePreparedCustomFolderOutputDirectory(");
        StringAssert.Contains(ownerSource, "internal CustomFolderBatchOutputResult MigratePreparedCustomFolderOutputDirectories(");
        StringAssert.Contains(ownerSource, "internal CustomFolderBatchOutputResult TryRemoveCustomFolder(");
        StringAssert.Contains(ownerSource, "internal Task<CustomFolderBatchOutputResult> ReOutputPreparedTablesAsync(");
        StringAssert.Contains(ownerSource, "CreateMigrationProtectedOutputDirectories(");
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
    public void PlaybackStartContractReturnsAnObservableTask()
    {
        Assert.AreEqual(
            typeof(Task),
            typeof(IBMSPlayer).GetMethod(nameof(IBMSPlayer.PlayStart))!.ReturnType);
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
            DirectoryInfo? parent = Directory.GetParent(directory);
            directory = parent == null ? string.Empty : parent.FullName;
        }
        Assert.Fail("Repository root was not found.");
        return string.Empty;
    }
}
