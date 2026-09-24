using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Net;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OperationProgressHubViewModelTests
{
    [TestMethod]
    public void InstallPipelinePresentation_UsesPlaylistDropEstimateThenPendingPriority()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        using var fixture = ProgressWorkflowFixture.Create(hub);

        hub.UpdatePendingEstimateQueueStatus(new PendingInstallEstimateQueueStatusSnapshot
        {
            IsActive = true,
            PendingBatchCount = 2,
            CurrentPackageCount = 8,
            CompletedPackageCount = 3,
            CurrentDisplayName = "pending"
        });
        Assert.IsTrue(hub.IsInstallPipelineStatusActive);
        Assert.AreEqual(3, hub.InstallPipelineValue);

        hub.UpdateInstallEstimationProgress(new InstallEstimationProgressSnapshot
        {
            IsActive = true,
            TotalWorkCount = 11,
            CompletedWorkCount = 4,
            CurrentDisplayName = "estimate"
        });
        Assert.AreEqual(4, hub.InstallPipelineValue);
        Assert.AreEqual(11, hub.InstallPipelineMaximum);

        using var packageValueUpdated = new ManualResetEventSlim(false);
        hub.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(hub.InstallPipelineValue)
                && hub.InstallPipelineValue == 1)
            {
                packageValueUpdated.Set();
            }
        };
        fixture.StartPackageProgress();
        Assert.IsTrue(packageValueUpdated.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, hub.InstallPipelineValue);
        Assert.IsTrue(hub.InstallPipelineCanCancel);
        StringAssert.Contains(hub.InstallPipelineSubLabel, "drop.zip");

        string urlRoot = Path.Combine(
            Path.GetTempPath(),
            nameof(OperationProgressHubViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(urlRoot);
        try
        {
            var urlGateway = new BlockingPlaylistUrlDownloadGateway(urlRoot);
            using PlaylistWorkspaceTestPorts.OwnedPlaylistStore ownedPlaylistStore =
                PlaylistWorkspaceTestPorts.CreateOwnedPlaylistStore();
            PlaylistWorkspaceViewModel playlistWorkspace = PlaylistWorkspaceTestPorts.CreateProgressWorkspace(
                action => action(),
                new PlaylistUrlAcquisitionWorkflow(urlGateway, _ => { }),
                new AcceptedDialogService(),
                playlistStoreProvider: () => ownedPlaylistStore.Store);
            hub.AttachPlaylistProgressSources(playlistWorkspace, action => action(), () => false);

            Task urlAcquisition = playlistWorkspace.RunPlaylistUrlBatchAsync(
                [new Uri("https://example.invalid/priority.zip")],
                isDiffUrl: false);
            Assert.IsTrue(urlGateway.ReadStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(hub.IsInstallPipelineStatusActive);
            Assert.AreEqual(1, hub.InstallPipelineMaximum);
            Assert.AreEqual(0, hub.InstallPipelineValue);
            Assert.AreEqual("https://example.invalid/priority.zip", hub.InstallPipelineSubLabel);
            Assert.IsTrue(hub.InstallPipelineCanCancel);

            urlGateway.Response.TrySetResult(new AppHttpResponse(
                new Uri("https://example.invalid/priority.zip"),
                new MemoryStream([1, 2, 3], writable: false)));
            urlAcquisition.GetAwaiter().GetResult();

            Assert.IsTrue(hub.IsInstallPipelineStatusActive);
            Assert.AreEqual(1, hub.InstallPipelineValue);
            StringAssert.Contains(hub.InstallPipelineSubLabel, "drop.zip");
        }
        finally
        {
            if (Directory.Exists(urlRoot))
            {
                Directory.Delete(urlRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task InstallPipelinePresentation_NormalizesEmptyMaximumAndInactiveState()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());

        using var fixture = ProgressWorkflowFixture.Create(hub, packageTotalCount: 0);
        using var packageActive = new ManualResetEventSlim(false);
        hub.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(hub.IsInstallPipelineStatusActive))
            {
                if (hub.IsInstallPipelineStatusActive)
                {
                    packageActive.Set();
                }
            }
        };
        fixture.StartPackageProgress();
        Assert.IsTrue(packageActive.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, hub.InstallPipelineMaximum);

        fixture.ReleasePackageProgress();
        await fixture.Package.WaitForIdleAsync();
        Assert.IsFalse(hub.IsInstallPipelineStatusActive);
        Assert.AreEqual(0, hub.InstallPipelineValue);
        Assert.AreEqual(1, hub.InstallPipelineMaximum);
    }

    [TestMethod]
    public void FolderAutoRenamePresentation_NormalizesProgressAndClearsOnCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        using var fixture = ProgressWorkflowFixture.Create(hub);

        fixture.StartFolderRenameProgress();
        Assert.IsTrue(fixture.FolderProgressStarted.Wait(TimeSpan.FromSeconds(5)));

        Assert.IsTrue(hub.IsFolderAutoRenameProgressActive);
        Assert.AreEqual(1.0, hub.FolderAutoRenameProgressMaximum);
        Assert.AreEqual(1.0, hub.FolderAutoRenameProgressValue);
        StringAssert.Contains(hub.FolderAutoRenameProgressLabel, "1/1");
        Assert.AreEqual("source", hub.FolderAutoRenameProgressSubLabel);

        using var progressCleared = new ManualResetEventSlim(false);
        hub.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(hub.IsFolderAutoRenameProgressActive)
                && !hub.IsFolderAutoRenameProgressActive)
            {
                progressCleared.Set();
            }
        };
        fixture.ReleaseFolderRenameProgress();
        Assert.IsTrue(fixture.Folder.WaitForIdleAsync().Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(progressCleared.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(hub.IsFolderAutoRenameProgressActive);
        Assert.AreEqual(0.0, hub.FolderAutoRenameProgressValue);
        Assert.AreEqual(1.0, hub.FolderAutoRenameProgressMaximum);
        Assert.AreEqual(string.Empty, hub.FolderAutoRenameProgressLabel);
        Assert.AreEqual(string.Empty, hub.FolderAutoRenameProgressSubLabel);
    }

    [TestMethod]
    public void MaintenanceRescanPresentation_UsesAttachedWorkflowProgressAndTerminalReset()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        using var fixture = ProgressWorkflowFixture.Create(hub);

        fixture.StartMaintenanceProgress();
        Assert.IsTrue(fixture.MaintenanceProgressStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(hub.IsMaintenanceRescanProgressActive);
        Assert.AreEqual(1.0, hub.MaintenanceRescanMaximum);
        Assert.AreEqual(1.0, hub.MaintenanceRescanValue);

        using var progressCleared = new ManualResetEventSlim(false);
        hub.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(hub.IsMaintenanceRescanProgressActive)
                && !hub.IsMaintenanceRescanProgressActive)
            {
                progressCleared.Set();
            }
        };
        fixture.ReleaseMaintenanceProgress();
        Assert.IsTrue(fixture.Maintenance.WaitForIdleAsync().Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(progressCleared.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(hub.IsMaintenanceRescanProgressActive);
        Assert.IsFalse(hub.MaintenanceRescanCanCancel);
    }

    [TestMethod]
    public void WorkflowProgressSources_CannotBeAttachedTwice()
    {
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        using var fixture = ProgressWorkflowFixture.Create(hub);

        Assert.ThrowsException<InvalidOperationException>(() => hub.AttachWorkflowProgressSources(
            fixture.Package,
            fixture.Maintenance,
            fixture.Folder));
    }

    [TestMethod]
    public void PlaylistSyncPresentation_UsesNormalizedValuesAndCurrentTableName()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        PlaylistWorkspaceViewModel workspace = AttachPlaylistProgressSources(hub);
        var changedProperties = new List<string>();
        hub.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName!);

        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            TotalTableCount = 2,
            CompletedTableCount = 9,
            CurrentTableName = "current table",
            CurrentUri = new System.Uri("https://example.invalid/table"),
            LabelFormat = "{0}/{1}",
            SingleLabel = "single"
        });

        Assert.IsTrue(hub.IsPlaylistSyncProgressActive);
        Assert.AreEqual(2.0, hub.PlaylistSyncProgressMaximum);
        Assert.AreEqual(2.0, hub.PlaylistSyncProgressValue);
        Assert.AreEqual("2/2", hub.PlaylistSyncProgressLabel);
        Assert.AreEqual("current table", hub.PlaylistSyncProgressSubLabel);
        CollectionAssert.Contains(changedProperties, nameof(hub.IsPlaylistSyncProgressActive));
        CollectionAssert.Contains(changedProperties, nameof(hub.PlaylistSyncProgressLabel));
        CollectionAssert.Contains(changedProperties, nameof(hub.PlaylistSyncProgressSubLabel));
        CollectionAssert.Contains(changedProperties, nameof(hub.PlaylistSyncProgressValue));
        CollectionAssert.Contains(changedProperties, nameof(hub.PlaylistSyncProgressMaximum));
    }

    [TestMethod]
    public void PlaylistSyncPresentation_InactiveSnapshotClearsState()
    {
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        PlaylistWorkspaceViewModel workspace = AttachPlaylistProgressSources(hub);
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            TotalTableCount = 1,
            CompletedTableCount = 1,
            CurrentTableName = "active",
            LabelFormat = "{0}/{1}"
        });

        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot());

        Assert.IsFalse(hub.IsPlaylistSyncProgressActive);
        Assert.AreEqual(string.Empty, hub.PlaylistSyncProgressLabel);
        Assert.AreEqual(string.Empty, hub.PlaylistSyncProgressSubLabel);
        Assert.AreEqual(0.0, hub.PlaylistSyncProgressValue);
        Assert.AreEqual(0.0, hub.PlaylistSyncProgressMaximum);
    }

    [TestMethod]
    public void PlaylistSyncPresentation_DropsOlderQueuedSnapshotWhenNewerSnapshotArrives()
    {
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        var queued = new List<Action>();
        PlaylistWorkspaceViewModel workspace = AttachPlaylistProgressSources(hub, queued.Add);

        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            TotalTableCount = 2,
            CompletedTableCount = 1,
            CurrentTableName = "active",
            LabelFormat = "{0}/{1}"
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot());

        Assert.AreEqual(2, queued.Count);
        queued[0]();
        Assert.IsFalse(hub.IsPlaylistSyncProgressActive);
        queued[1]();
        Assert.IsFalse(hub.IsPlaylistSyncProgressActive);
    }

    [TestMethod]
    public void PlaylistSyncPresentation_SuppressesQueuedSnapshotDuringShellClosing()
    {
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        var queued = new List<Action>();
        bool isShellClosing = false;
        PlaylistWorkspaceViewModel workspace = AttachPlaylistProgressSources(
            hub,
            queued.Add,
            () => isShellClosing);

        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            TotalTableCount = 1,
            CompletedTableCount = 1,
            CurrentTableName = "active",
            LabelFormat = "{0}/{1}"
        });
        isShellClosing = true;

        Assert.AreEqual(1, queued.Count);
        queued[0]();
        Assert.IsFalse(hub.IsPlaylistSyncProgressActive);
    }

    [TestMethod]
    public void PlaylistProgressSources_CannotBeAttachedTwice()
    {
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        PlaylistWorkspaceViewModel workspace = AttachPlaylistProgressSources(hub);

        Assert.ThrowsException<InvalidOperationException>(() => hub.AttachPlaylistProgressSources(
            workspace,
            action => action(),
            () => false));
    }

    [TestMethod]
    public void StartupProgressPresentation_AppliesValuesInBindingOrder()
    {
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        var changedProperties = new List<string>();
        hub.StartupProgress.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName!);

        hub.StartupProgress.ApplyPresentation(true, "running", "phase", 2.0, 5.0);

        Assert.IsTrue(hub.StartupProgress.IsActive);
        Assert.AreEqual("running", hub.StartupProgress.Label);
        Assert.AreEqual("phase", hub.StartupProgress.SubLabel);
        Assert.AreEqual(2.0, hub.StartupProgress.Value);
        Assert.AreEqual(5.0, hub.StartupProgress.Maximum);
        CollectionAssert.AreEqual(
            new[]
            {
                nameof(StartupProgressWorkflowOwner.IsActive),
                nameof(StartupProgressWorkflowOwner.Label),
                nameof(StartupProgressWorkflowOwner.SubLabel),
                nameof(StartupProgressWorkflowOwner.Value),
                nameof(StartupProgressWorkflowOwner.Maximum)
            },
            changedProperties);
    }

    [TestMethod]
    public void StartupProgressPresentation_InactiveAndNullValuesClearState()
    {
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        hub.StartupProgress.ApplyPresentation(true, "running", "phase", 2.0, 5.0);

        hub.StartupProgress.ApplyPresentation(false, null, null, 0.0, 1.0);

        Assert.IsFalse(hub.StartupProgress.IsActive);
        Assert.AreEqual(string.Empty, hub.StartupProgress.Label);
        Assert.AreEqual(string.Empty, hub.StartupProgress.SubLabel);
        Assert.AreEqual(0.0, hub.StartupProgress.Value);
        Assert.AreEqual(1.0, hub.StartupProgress.Maximum);
    }

    [TestMethod]
    public void StartupBackgroundInitializationPresentation_UsesLatchAndDedicatedPrecedence()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());

        hub.StartupProgress.ApplyPresentation(true, "required", "required phase", 1.0, 2.0);
        hub.BeginStartupBackgroundInitializationPresentation();

        Assert.IsFalse(hub.IsStartupBackgroundInitializationActive);

        hub.StartupProgress.ApplyPresentation(false, null, null, 0.0, 1.0);
        Assert.IsTrue(hub.IsStartupBackgroundInitializationActive);

        hub.IsPlaylistSyncProgressActive = true;
        Assert.IsFalse(hub.IsStartupBackgroundInitializationActive);

        hub.IsPlaylistSyncProgressActive = false;
        Assert.IsTrue(hub.IsStartupBackgroundInitializationActive);

        hub.UpdateLr2SongDbSyncStatus(Lr2SongDbSyncStatusMapper.Create(
            new Lr2SongDbSyncStatusSnapshot
            {
                Status = Lr2SongDbSyncStatusKind.Running,
                Stage = "song_rows",
                StageProcessedCount = 1,
                StageTotalCount = 2
            },
            DateTime.UtcNow));
        Assert.IsFalse(hub.IsStartupBackgroundInitializationActive);

        hub.UpdateLr2SongDbSyncStatus(null);
        Assert.IsTrue(hub.IsStartupBackgroundInitializationActive);

        foreach (Lr2SongDbSyncStatusKind terminalKind in new[]
        {
            Lr2SongDbSyncStatusKind.Incomplete,
            Lr2SongDbSyncStatusKind.Failed
        })
        {
            hub.UpdateLr2SongDbSyncStatus(Lr2SongDbSyncStatusMapper.Create(
                new Lr2SongDbSyncStatusSnapshot
                {
                    Status = terminalKind,
                    Stage = "folder_rows",
                    LastError = "test failure"
                },
                DateTime.UtcNow));

            Assert.IsTrue(hub.IsLr2SongDbSyncStatusActive);
            Assert.IsTrue(hub.IsLr2SongDbSyncRetryVisible);
            Assert.IsFalse(hub.IsStartupBackgroundInitializationActive);

            hub.UpdateLr2SongDbSyncStatus(null);
            Assert.IsFalse(hub.IsLr2SongDbSyncStatusActive);
            Assert.IsTrue(hub.IsStartupBackgroundInitializationActive);
        }

        hub.CompleteStartupBackgroundInitializationPresentation();
        Assert.IsFalse(hub.IsStartupBackgroundInitializationActive);

        hub.ResetStartupBackgroundInitializationPresentation();
        Assert.IsFalse(hub.IsStartupBackgroundInitializationActive);
    }

    [TestMethod]
    public void StartupBackgroundInitializationPresentation_IsolatesThrowingSubscriber()
    {
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        hub.StartupProgress.ApplyPresentation(false, null, null, 0.0, 1.0);
        hub.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(OperationProgressHubViewModel.IsStartupBackgroundInitializationActive))
            {
                throw new InvalidOperationException("background presentation observer failed");
            }
        };

        hub.BeginStartupBackgroundInitializationPresentation();
        Assert.IsTrue(hub.IsStartupBackgroundInitializationActive);

        hub.CompleteStartupBackgroundInitializationPresentation();
        Assert.IsFalse(hub.IsStartupBackgroundInitializationActive);
    }

    [TestMethod]
    public async Task Lr2SongDbSyncPresentation_UsesRuntimeStatusAndSuppression()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var delayEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusVisibleAfterStartup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new OperationProgressHubViewModel(
            TestStartupProgressOwnerFactory.Create(
                completionHideDelay: () =>
                {
                    delayEntered.TrySetResult(true);
                    return releaseDelay.Task;
                }));
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(
            new Lr2SongDbSyncStatusSnapshot
            {
                Status = Lr2SongDbSyncStatusKind.Running,
                Stage = "song_rows",
                StageProcessedCount = 4,
                StageTotalCount = 10
            },
            new DateTime(2026, 6, 5, 12, 0, 0));

        hub.UpdateLr2SongDbSyncStatus(status);

        Assert.IsTrue(hub.IsLr2SongDbSyncStatusActive);
        Assert.AreEqual(status.StatusText, hub.Lr2SongDbSyncStatusLabel);
        Assert.AreEqual(status.ProgressText, hub.Lr2SongDbSyncStatusSubLabel);
        Assert.AreEqual(status.Detail, hub.Lr2SongDbSyncStatusToolTip);
        Assert.AreEqual(status.ProgressValue, hub.Lr2SongDbSyncStatusProgressValue);
        Assert.AreEqual(status.ProgressMaximum, hub.Lr2SongDbSyncStatusProgressMaximum);
        Assert.IsTrue(hub.IsLr2SongDbSyncStatusProgressVisible);
        Assert.IsFalse(hub.IsLr2SongDbSyncRetryVisible);

        hub.StartupProgress.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        hub.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(OperationProgressHubViewModel.IsLr2SongDbSyncStatusActive)
                && hub.IsLr2SongDbSyncStatusActive)
            {
                statusVisibleAfterStartup.TrySetResult(true);
            }
        };

        Assert.IsFalse(hub.IsLr2SongDbSyncStatusActive);
        Assert.AreEqual(string.Empty, hub.Lr2SongDbSyncStatusLabel);
        Assert.AreEqual(1.0, hub.Lr2SongDbSyncStatusProgressMaximum);
        Assert.IsFalse(hub.IsLr2SongDbSyncStatusProgressVisible);

        CompleteStartupProgress(hub.StartupProgress);
        await delayEntered.Task;
        Assert.IsFalse(hub.IsLr2SongDbSyncStatusActive);

        releaseDelay.TrySetResult(true);
        await statusVisibleAfterStartup.Task;

        Assert.AreEqual(status.StatusText, hub.Lr2SongDbSyncStatusLabel);
        Assert.AreEqual(status.ProgressText, hub.Lr2SongDbSyncStatusSubLabel);
        Assert.AreEqual(status.ProgressValue, hub.Lr2SongDbSyncStatusProgressValue);
        Assert.AreEqual(status.ProgressMaximum, hub.Lr2SongDbSyncStatusProgressMaximum);
    }

    [TestMethod]
    public void Lr2SongDbSyncPresentation_UsesRetryForIncompleteAndClearsNotNeeded()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        Lr2SongDbSyncRuntimeStatus incomplete = Lr2SongDbSyncStatusMapper.Create(
            new Lr2SongDbSyncStatusSnapshot
            {
                Status = Lr2SongDbSyncStatusKind.Incomplete,
                Stage = "folder_rows",
                LastError = "incomplete preparation"
            },
            DateTime.MinValue);

        hub.UpdateLr2SongDbSyncStatus(incomplete);

        Assert.IsTrue(hub.IsLr2SongDbSyncStatusActive);
        Assert.IsTrue(hub.IsLr2SongDbSyncRetryVisible);

        hub.UpdateLr2SongDbSyncStatus(null);

        Assert.IsFalse(hub.IsLr2SongDbSyncStatusActive);
        Assert.AreEqual(string.Empty, hub.Lr2SongDbSyncStatusLabel);
        Assert.AreEqual(string.Empty, hub.Lr2SongDbSyncStatusSubLabel);
        Assert.AreEqual(string.Empty, hub.Lr2SongDbSyncStatusToolTip);
        Assert.AreEqual(0.0, hub.Lr2SongDbSyncStatusProgressValue);
        Assert.AreEqual(1.0, hub.Lr2SongDbSyncStatusProgressMaximum);
        Assert.IsFalse(hub.IsLr2SongDbSyncStatusProgressVisible);
        Assert.IsFalse(hub.IsLr2SongDbSyncRetryVisible);
    }

    [TestMethod]
    public async Task Lr2SongDbSyncIncompleteStatus_RemainsDedicatedAndRetryableWithoutStartupAccounting()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var delayEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var presentationCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationProgressHubViewModel? hub = null;
        hub = new OperationProgressHubViewModel(
            TestStartupProgressOwnerFactory.Create(
                completionHideDelay: () =>
                {
                    delayEntered.TrySetResult(true);
                    return releaseDelay.Task;
                },
                dispatch: action =>
                {
                    try
                    {
                        action();
                        // 個々の PropertyChanged ではなく、Hub の再計算を含む反映全体を待つ。
                        if (hub != null && !hub.StartupProgress.IsOperationActive && !hub.StartupProgress.IsActive)
                        {
                            presentationCompleted.TrySetResult(true);
                        }
                    }
                    catch (Exception exception)
                    {
                        presentationCompleted.TrySetException(exception);
                        throw;
                    }
                }));
        hub.StartupProgress.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        double startupValue = hub.StartupProgress.Value;
        double startupMaximum = hub.StartupProgress.Maximum;
        Lr2SongDbSyncRuntimeStatus incomplete = Lr2SongDbSyncStatusMapper.Create(
            new Lr2SongDbSyncStatusSnapshot
            {
                Status = Lr2SongDbSyncStatusKind.Incomplete,
                Stage = "song_rows",
                LastError = "sync failed"
            },
            DateTime.MinValue);

        bool completionScheduled = false;
        try
        {
            hub.UpdateLr2SongDbSyncStatus(incomplete);

            Assert.IsFalse(hub.StartupProgress.IsFailed);
            Assert.AreEqual(startupValue, hub.StartupProgress.Value);
            Assert.AreEqual(startupMaximum, hub.StartupProgress.Maximum);

            Assert.IsFalse(hub.IsLr2SongDbSyncStatusActive);

            CompleteStartupProgress(hub.StartupProgress);
            completionScheduled = true;
            await delayEntered.Task;
            releaseDelay.TrySetResult(true);
            await presentationCompleted.Task;

            Assert.IsTrue(hub.IsLr2SongDbSyncStatusActive);
            Assert.IsTrue(hub.IsLr2SongDbSyncRetryVisible);
        }
        finally
        {
            releaseDelay.TrySetResult(true);
            if (completionScheduled)
            {
                await presentationCompleted.Task;
            }
        }
    }

    private static void CompleteStartupProgress(StartupProgressWorkflowOwner owner)
    {
        owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.TryCompleteStartupProgressLibraryFileDiff(1);
        owner.MarkStartupProgressPhaseCompleted(
            StartupProgressPhase.StartupReadyData,
            owner.GetActiveStartupProgressOperationToken());
        owner.MarkStartupProgressPhaseCompleted(
            StartupProgressPhase.StartupReadyUi,
            owner.GetActiveStartupProgressOperationToken());
        owner.MarkStartupProgressPhaseCompleted(
            StartupProgressPhase.StartupReadyOperable,
            owner.GetActiveStartupProgressOperationToken());
        foreach (StartupProgressPhase phase in new[]
        {
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressPhase.ChartInfoHydrationDone,
            StartupProgressPhase.ChartInfoBackfillDone,
            StartupProgressPhase.ChartDigestBackfillDone,
            StartupProgressPhase.ScoreHydrationDone
        })
        {
            owner.SkipStartupProgressPhaseIfExpected(
                phase,
                "test",
                owner.GetActiveStartupProgressOperationToken());
        }
        owner.MarkStartupProgressPhaseCompleted(
            StartupProgressPhase.StartupBackgroundTasksDone,
            owner.GetActiveStartupProgressOperationToken());
    }

    private static PlaylistWorkspaceViewModel AttachPlaylistProgressSources(
        OperationProgressHubViewModel hub,
        Action<Action>? dispatch = null,
        Func<bool>? shellClosingPredicate = null)
    {
        Action<Action> actualDispatch = dispatch ?? (action => action());
        PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceTestPorts.CreateProgressWorkspace(
            actualDispatch,
            dialogService: new AcceptedDialogService());
        hub.AttachPlaylistProgressSources(
            workspace,
            actualDispatch,
            shellClosingPredicate ?? (() => false));
        return workspace;
    }

    private sealed class ProgressWorkflowFixture : IDisposable
    {
        private readonly string root;
        private readonly ManualResetEventSlim packageRelease = new(false);
        private readonly ManualResetEventSlim maintenanceRelease = new(false);
        private readonly ManualResetEventSlim folderRelease = new(false);

        private ProgressWorkflowFixture(
            string root,
            PackageInstallWorkflowOwner package,
            MaintenanceRescanWorkflowOwner maintenance,
            FolderAutoRenameWorkflowOwner folder,
            ManualResetEventSlim packageProgressStarted,
            ManualResetEventSlim maintenanceProgressStarted,
            ManualResetEventSlim folderProgressStarted)
        {
            this.root = root;
            Package = package;
            Maintenance = maintenance;
            Folder = folder;
            PackageProgressStarted = packageProgressStarted;
            MaintenanceProgressStarted = maintenanceProgressStarted;
            FolderProgressStarted = folderProgressStarted;
        }

        internal PackageInstallWorkflowOwner Package { get; }

        internal MaintenanceRescanWorkflowOwner Maintenance { get; }

        internal FolderAutoRenameWorkflowOwner Folder { get; }

        internal ManualResetEventSlim PackageProgressStarted { get; }

        internal ManualResetEventSlim MaintenanceProgressStarted { get; }

        internal ManualResetEventSlim FolderProgressStarted { get; }

        internal static ProgressWorkflowFixture Create(OperationProgressHubViewModel hub, int packageTotalCount = 5)
        {
            string root = Path.Combine(Path.GetTempPath(), nameof(OperationProgressHubViewModelTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string databasePath = Path.Combine(root, "song.db");
            File.WriteAllBytes(databasePath, []);
            using (var _ = new LR2SongDBExtended(databasePath))
            {
            }
            BMSLibrary library = new TestBmsLibrary(databasePath, null, null, string.Empty);
            var packageProgressStarted = new ManualResetEventSlim(false);
            var maintenanceProgressStarted = new ManualResetEventSlim(false);
            var folderProgressStarted = new ManualResetEventSlim(false);
            ProgressWorkflowFixture fixture = null!;
            var package = new PackageInstallWorkflowOwner(
                new FileDbReportRecordingDialogs(),
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort((current, paths, token, progressWriter) =>
                {
                    progressWriter.TryWrite(PackageInstallProgressUpdate.SourceProcessed());
                    progressWriter.TryWrite(PackageInstallProgressUpdate.ArchiveExtractStarted(paths.FirstOrDefault() ?? string.Empty, 1, packageTotalCount));
                    packageProgressStarted.Set();
                    fixture.packageRelease.Wait(TimeSpan.FromSeconds(10));
                    return new PackageInstallCommandResult([], null);
                }),
                action =>
                {
                    action();
                    return true;
                });
            var maintenance = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) =>
                {
                    progress(new MaintenanceWorkflowProgress { TotalCount = 1, ProcessedCount = 1 });
                    maintenanceProgressStarted.Set();
                    fixture.maintenanceRelease.Wait(TimeSpan.FromSeconds(10));
                    return new MaintenanceWorkflowResult();
                },
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default),
                action => action(),
                dialogs: new AcceptedDialogService());
            var folder = new FolderAutoRenameWorkflowOwner(
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new ProgressFolderAutoRenameMutationPort(
                    (current, request, progress) => new FolderAutoRenameExecutionResult(),
                    (current, parentDirectory, progress) =>
                    {
                        progress.TryWrite(new FolderAutoRenameProgressUpdate(1, 1, "source"));
                        folderProgressStarted.Set();
                        fixture.folderRelease.Wait(TimeSpan.FromSeconds(10));
                        return new AutoRenameBatchResult(false, 0, LibraryMutationSessionReceipt.Empty);
                    },
                    (current, parentDirectory) => true),
                new ProgressFolderAutoRenamePlaybackPort(),
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default),
                action => action(),
                dialogs: new AcceptedDialogService());
            fixture = new ProgressWorkflowFixture(
                root,
                package,
                maintenance,
                folder,
                packageProgressStarted,
                maintenanceProgressStarted,
                folderProgressStarted);
            hub.AttachWorkflowProgressSources(package, maintenance, folder);
            package.AttachLibrary(library);
            maintenance.AttachLibrary(library);
            folder.AttachLibrary(library);
            return fixture;
        }

        internal void StartPackageProgress()
        {
            Package.Enqueue([Path.Combine(root, "drop.zip")]);
            Assert.IsTrue(PackageProgressStarted.Wait(TimeSpan.FromSeconds(5)));
        }

        internal void ReleasePackageProgress()
        {
            packageRelease.Set();
        }

        internal void StartMaintenanceProgress()
        {
            Assert.IsTrue(Maintenance.RequestStartAsync().GetAwaiter().GetResult().Started);
        }

        internal void ReleaseMaintenanceProgress()
        {
            maintenanceRelease.Set();
        }

        internal void StartFolderRenameProgress()
        {
            Folder.RequestStartAllAsync(root).GetAwaiter().GetResult();
        }

        internal void ReleaseFolderRenameProgress()
        {
            folderRelease.Set();
        }

        public void Dispose()
        {
            packageRelease.Set();
            maintenanceRelease.Set();
            folderRelease.Set();
            Package.WaitForIdleAsync().Wait(TimeSpan.FromSeconds(5));
            Maintenance.WaitForIdleAsync().Wait(TimeSpan.FromSeconds(5));
            Folder.WaitForIdleAsync().Wait(TimeSpan.FromSeconds(5));
            packageRelease.Dispose();
            maintenanceRelease.Dispose();
            folderRelease.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class ProgressFolderAutoRenameMutationPort : IFolderAutoRenameMutationPort
    {
        private readonly Func<BMSLibrary, ChartFolderAutoRenameRequest, IFolderAutoRenameProgressWriter, FolderAutoRenameExecutionResult> selected;
        private readonly Func<BMSLibrary, string, IFolderAutoRenameProgressWriter, AutoRenameBatchResult> all;
        private readonly Func<BMSLibrary, string, bool> hasTargets;

        internal ProgressFolderAutoRenameMutationPort(
            Func<BMSLibrary, ChartFolderAutoRenameRequest, IFolderAutoRenameProgressWriter, FolderAutoRenameExecutionResult> selected,
            Func<BMSLibrary, string, IFolderAutoRenameProgressWriter, AutoRenameBatchResult> all,
            Func<BMSLibrary, string, bool> hasTargets)
        {
            this.selected = selected;
            this.all = all;
            this.hasTargets = hasTargets;
        }

        public bool HasTargets(BMSLibrary library, string parentDirectory) => hasTargets(library, parentDirectory);

        public FolderAutoRenameExecutionResult RenameSelectedWithProgress(
            BMSLibrary library,
            ChartFolderAutoRenameRequest request,
            IFolderAutoRenameProgressWriter progressWriter) => selected(library, request, progressWriter);

        public AutoRenameBatchResult RenameAllWithReceiptWithProgress(
            BMSLibrary library,
            string parentDirectory,
            IFolderAutoRenameProgressWriter progressWriter) => all(library, parentDirectory, progressWriter);
    }

    private sealed class ProgressFolderAutoRenamePlaybackPort : IFolderAutoRenamePlaybackPort
    {
        public void StopPlaybackForCharts(IReadOnlyList<ChartFile> charts)
        {
        }

        public void StopPlaybackForFolderMutation()
        {
        }
    }

    private sealed class AcceptedDialogService : IUiDialogService
    {
        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class BlockingPlaylistUrlDownloadGateway : IPlaylistUrlDownloadGateway
    {
        private readonly string temporaryDirectory;

        internal BlockingPlaylistUrlDownloadGateway(string temporaryDirectory)
        {
            this.temporaryDirectory = temporaryDirectory;
        }

        internal ManualResetEventSlim ReadStarted { get; } = new(false);

        internal TaskCompletionSource<AppHttpResponse> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AppHttpResponse> OpenReadAsync(Uri uri, CancellationToken cancellationToken)
        {
            ReadStarted.Set();
            return Response.Task;
        }

        public string GetTemporaryDirectory() => temporaryDirectory;

        public FileStream OpenWrite(string path, FileMode mode, FileAccess access, FileShare share) =>
            new(path, mode, access, share);

        public bool FileExists(string path) => File.Exists(path);

        public void DeleteFile(string path) => File.Delete(path);
    }
}
