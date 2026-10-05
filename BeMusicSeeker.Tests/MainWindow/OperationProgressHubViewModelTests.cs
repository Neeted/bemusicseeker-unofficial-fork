using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Ribbit.Net;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OperationProgressHubViewModelTests
{
    [TestMethod]
    public void InstallPipelinePresentation_ShowsAcquisitionInstallAndEstimateTogether()
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
        Assert.IsTrue((GetPipelineRow(hub) != null));
        Assert.AreEqual(3, (GetPipelineRow(hub)?.Value ?? 0d));

        hub.UpdateInstallEstimationProgress(new InstallEstimationProgressSnapshot
        {
            IsActive = true,
            TotalWorkCount = 11,
            CompletedWorkCount = 4,
            CurrentDisplayName = "estimate"
        });
        Assert.AreEqual(4, (GetPipelineRow(hub)?.Value ?? 0d));
        Assert.AreEqual(11, (GetPipelineRow(hub)?.Maximum ?? 1d));

        using var packageValueUpdated = new ManualResetEventSlim(false);
        hub.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(hub.Rows)
                && (GetPipelineRow(hub)?.Value ?? 0d) == 1)
            {
                packageValueUpdated.Set();
            }
        };
        fixture.StartPackageProgress();
        Assert.IsTrue(packageValueUpdated.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, (GetPipelineRow(hub)?.Value ?? 0d));
        Assert.IsTrue((GetRow(hub, "install")?.CanCancel == true));
        StringAssert.Contains((GetRow(hub, "install")?.Detail ?? string.Empty), "drop.zip");

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
            OperationProgressRow urlRow = hub.Rows.Single(row => row.Key == "url");
            Assert.AreEqual(1d, urlRow.Maximum);
            Assert.AreEqual(0d, urlRow.Value);
            Assert.AreEqual("https://example.invalid/priority.zip", urlRow.Detail);
            Assert.AreEqual(OperationProgressAction.CancelUrlDownload, urlRow.Action);
            Assert.AreEqual(OperationProgressAction.CancelInstall, hub.Rows.Single(row => row.Key == "install").Action);
            Assert.AreEqual(4d, hub.Rows.Single(row => row.Key == "estimate").Value);
            Assert.AreEqual(3, hub.Rows.Count);
            urlGateway.Response.TrySetResult(new AppHttpResponse(
                new Uri("https://example.invalid/priority.zip"),
                new MemoryStream([1, 2, 3], writable: false)));
            urlAcquisition.GetAwaiter().GetResult();

            Assert.IsTrue((GetPipelineRow(hub) != null));
            Assert.AreEqual(1, (GetPipelineRow(hub)?.Value ?? 0d));
            StringAssert.Contains((GetRow(hub, "install")?.Detail ?? string.Empty), "drop.zip");
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
            if (args.PropertyName == nameof(hub.Rows))
            {
                if ((GetPipelineRow(hub) != null))
                {
                    packageActive.Set();
                }
            }
        };
        fixture.StartPackageProgress();
        Assert.IsTrue(packageActive.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, (GetPipelineRow(hub)?.Maximum ?? 1d));

        fixture.ReleasePackageProgress();
        await fixture.Package.WaitForIdleAsync();
        Assert.IsFalse((GetPipelineRow(hub) != null));
        Assert.AreEqual(0, (GetPipelineRow(hub)?.Value ?? 0d));
        Assert.AreEqual(1, (GetPipelineRow(hub)?.Maximum ?? 1d));
    }

    [TestMethod]
    public void FolderAutoRenamePresentation_NormalizesProgressAndClearsOnCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        using var fixture = ProgressWorkflowFixture.Create(hub);

        fixture.StartFolderRenameProgress();
        Assert.IsTrue(fixture.FolderProgressStarted.Wait(TimeSpan.FromSeconds(5)));

        Assert.IsTrue((GetRow(hub, "rename") != null));
        Assert.AreEqual(1.0, (GetRow(hub, "rename")?.Maximum ?? 1d));
        Assert.AreEqual(1.0, (GetRow(hub, "rename")?.Value ?? 0d));
        StringAssert.Contains((GetRow(hub, "rename")?.Label ?? string.Empty), "1/1");
        Assert.AreEqual("source", (GetRow(hub, "rename")?.Detail ?? string.Empty));

        using var progressCleared = new ManualResetEventSlim(false);
        hub.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(hub.Rows)
                && !(GetRow(hub, "rename") != null))
            {
                progressCleared.Set();
            }
        };
        fixture.ReleaseFolderRenameProgress();
        Assert.IsTrue(fixture.Folder.WaitForIdleAsync().Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(progressCleared.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsFalse((GetRow(hub, "rename") != null));
        Assert.AreEqual(0.0, (GetRow(hub, "rename")?.Value ?? 0d));
        Assert.AreEqual(1.0, (GetRow(hub, "rename")?.Maximum ?? 1d));
        Assert.AreEqual(string.Empty, (GetRow(hub, "rename")?.Label ?? string.Empty));
        Assert.AreEqual(string.Empty, (GetRow(hub, "rename")?.Detail ?? string.Empty));
    }

    [TestMethod]
    public void MaintenanceRescanPresentation_UsesAttachedWorkflowProgressAndTerminalReset()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        using var fixture = ProgressWorkflowFixture.Create(hub);

        fixture.StartMaintenanceProgress();
        Assert.IsTrue(fixture.MaintenanceProgressStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue((GetRow(hub, "maintenance") != null));
        Assert.AreEqual(1.0, (GetRow(hub, "maintenance")?.Maximum ?? 1d));
        Assert.AreEqual(1.0, (GetRow(hub, "maintenance")?.Value ?? 0d));

        using var progressCleared = new ManualResetEventSlim(false);
        hub.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(hub.Rows)
                && !(GetRow(hub, "maintenance") != null))
            {
                progressCleared.Set();
            }
        };
        fixture.ReleaseMaintenanceProgress();
        Assert.IsTrue(fixture.Maintenance.WaitForIdleAsync().Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(progressCleared.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsFalse((GetRow(hub, "maintenance") != null));
        Assert.IsFalse((GetRow(hub, "maintenance")?.CanCancel == true));
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

        Assert.IsTrue(hub.Rows.Any(row => row.Key.StartsWith("playlist:")));
        Assert.AreEqual(2.0, (GetRow(hub, "playlist:playlist:0")?.Maximum ?? 0d));
        Assert.AreEqual(2.0, (GetRow(hub, "playlist:playlist:0")?.Value ?? 0d));
        Assert.AreEqual("2/2", (GetRow(hub, "playlist:playlist:0")?.Label ?? string.Empty));
        Assert.AreEqual("current table", (GetRow(hub, "playlist:playlist:0")?.Detail ?? string.Empty));
        CollectionAssert.Contains(changedProperties, nameof(hub.Rows));
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

        Assert.IsFalse(hub.Rows.Any(row => row.Key.StartsWith("playlist:")));
        Assert.AreEqual(string.Empty, (GetRow(hub, "playlist:playlist:0")?.Label ?? string.Empty));
        Assert.AreEqual(string.Empty, (GetRow(hub, "playlist:playlist:0")?.Detail ?? string.Empty));
        Assert.AreEqual(0.0, (GetRow(hub, "playlist:playlist:0")?.Value ?? 0d));
        Assert.AreEqual(0.0, (GetRow(hub, "playlist:playlist:0")?.Maximum ?? 0d));
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
        Assert.IsFalse(hub.Rows.Any(row => row.Key.StartsWith("playlist:")));
        queued[1]();
        Assert.IsFalse(hub.Rows.Any(row => row.Key.StartsWith("playlist:")));
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
        Assert.IsFalse(hub.Rows.Any(row => row.Key.StartsWith("playlist:")));
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
    public void StartupProgressPresentation_ProjectsParentStageGauge()
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
        Assert.AreEqual(2d, GetRow(hub, "startup")?.Value);
        Assert.AreEqual(5d, GetRow(hub, "startup")?.Maximum);
        OperationProgressRow parent = hub.Rows.Single(row => row.Key == "startup");
        StringAssert.Contains(parent.FullText, parent.Value + "/" + parent.Maximum);
        StringAssert.Contains(parent.FullText, hub.StartupProgress.SubLabel);
        CollectionAssert.Contains(changedProperties, nameof(StartupProgressWorkflowOwner.DetailRows));
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
    public void StartupBackgroundInitializationPresentation_KeepsParentAndIndependentWorkVisible()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());

        hub.StartupProgress.ApplyPresentation(true, "required", "required phase", 1.0, 2.0);
        hub.BeginStartupBackgroundInitializationPresentation(1, 1);

        Assert.IsTrue(hub.IsStartupBackgroundInitializationActive);

        hub.StartupProgress.ApplyPresentation(false, null, null, 0.0, 1.0);
        Assert.IsTrue(hub.IsStartupBackgroundInitializationActive);

        PlaylistWorkspaceViewModel workspace = AttachPlaylistProgressSources(hub);
        workspace.ReportPlaylistSyncProgress(new() { IsActive = true, TotalTableCount = 1 });
        Assert.IsTrue(hub.IsStartupBackgroundInitializationActive);

        workspace.ReportPlaylistSyncProgress(new() { IsActive = false });
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
        Assert.IsTrue(hub.IsStartupBackgroundInitializationActive);

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

            Assert.IsTrue((GetRow(hub, "lr2") != null));
            Assert.IsTrue((GetRow(hub, "lr2")?.CanRetry == true));
            Assert.IsTrue(hub.IsStartupBackgroundInitializationActive);

            hub.UpdateLr2SongDbSyncStatus(null);
            Assert.IsFalse((GetRow(hub, "lr2") != null));
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

        hub.BeginStartupBackgroundInitializationPresentation(1, 1);
        Assert.IsTrue(hub.IsStartupBackgroundInitializationActive);

        hub.CompleteStartupBackgroundInitializationPresentation();
        Assert.IsFalse(hub.IsStartupBackgroundInitializationActive);
    }

    [TestMethod]
    public async Task Lr2SongDbSyncPresentation_RemainsVisibleDuringStartupAndCompletionDelay()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var delayEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hidden = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create(completionHideDelay: () =>
        {
            delayEntered.TrySetResult(true);
            return releaseDelay.Task;
        });
        var hub = new OperationProgressHubViewModel(owner);
        owner.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(owner.IsActive) && !owner.IsActive) { hidden.TrySetResult(true); } };
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Running,
            Stage = "song_rows",
            StageProcessedCount = 4,
            StageTotalCount = 10
        }, DateTime.UtcNow);
        hub.UpdateLr2SongDbSyncStatus(status);
        owner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        try
        {
            Assert.IsTrue((GetRow(hub, "lr2") != null));
            CollectionAssert.AreEquivalent(new[] { "startup", "lr2" }, hub.Rows.Select(row => row.Key).ToArray());
            Assert.AreEqual(status.ProgressValue, hub.Rows.Single(row => row.Key == "lr2").Value);
            CompleteStartupProgress(owner);
            await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue((GetRow(hub, "lr2") != null));
        }
        finally
        {
            releaseDelay.TrySetResult(true);
        }
        await hidden.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("lr2", hub.Rows.Single().Key);
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

        Assert.IsTrue((GetRow(hub, "lr2") != null));
        Assert.IsTrue((GetRow(hub, "lr2")?.CanRetry == true));
        Assert.IsFalse(hub.Rows.Single(row => row.Key == "lr2").HasGauge);
        Assert.IsFalse(hub.Rows.Single(row => row.Key == "lr2").IsIndeterminate);

        hub.UpdateLr2SongDbSyncStatus(null);

        Assert.IsFalse((GetRow(hub, "lr2") != null));
        Assert.AreEqual(string.Empty, (GetRow(hub, "lr2")?.Label ?? string.Empty));
        Assert.AreEqual(string.Empty, (GetRow(hub, "lr2")?.Detail ?? string.Empty));
        Assert.AreEqual(string.Empty, (GetRow(hub, "lr2")?.ToolTip ?? string.Empty));
        Assert.AreEqual(0.0, (GetRow(hub, "lr2")?.Value ?? 0d));
        Assert.AreEqual(1.0, (GetRow(hub, "lr2")?.Maximum ?? 1d));
        Assert.IsFalse((GetRow(hub, "lr2")?.HasGauge == true));
        Assert.IsFalse((GetRow(hub, "lr2")?.CanRetry == true));
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

            Assert.IsTrue((GetRow(hub, "lr2") != null));

            CompleteStartupProgress(hub.StartupProgress);
            completionScheduled = true;
            await delayEntered.Task;
            releaseDelay.TrySetResult(true);
            await presentationCompleted.Task;

            Assert.IsTrue((GetRow(hub, "lr2") != null));
            Assert.IsTrue((GetRow(hub, "lr2")?.CanRetry == true));
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

    [TestMethod]
    public void Lr2Rows_AttachOnlyAssociatedStartupExecutionAndLeaveTerminalWarningIndependent()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        hub.BeginBackgroundProgressGeneration(1);
        hub.BeginStartupBackgroundInitializationPresentation(11, 1);
        var request = new OperationProgressRequest(1, 11, "scheduler:lr2_song_db_sync", 3);
        hub.UpdateBackgroundTaskProgress(new("lr2_song_db_sync", 1, 3, true, true, request));
        OperationProgressRow generic = hub.Rows.Single(row => row.Key.StartsWith("background:"));
        Assert.AreEqual("startup_background", generic.ParentKey);
        hub.UpdateLr2SongDbSyncStatus(Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Running,
            Stage = "song_rows",
            StageTotalCount = 5,
            StageProcessedCount = 2
        }, DateTime.UtcNow));
        OperationProgressRow dedicated = hub.Rows.Single(row => row.Key == "lr2");
        Assert.AreEqual(generic.Label, dedicated.Label);
        Assert.AreEqual(generic.ParentKey, dedicated.ParentKey);
        Assert.IsFalse(hub.Rows.Any(row => row.Key.StartsWith("background:")));
        hub.UpdateBackgroundTaskProgress(new("lr2_song_db_sync", 1, 3, true, false, request));
        hub.UpdateLr2SongDbSyncStatus(Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Incomplete
        }, DateTime.UtcNow));
        OperationProgressRow warning = hub.Rows.Single(row => row.Key == "lr2");
        Assert.IsFalse(warning.IsChild);
        Assert.IsTrue(warning.CanRetry);
        hub.CompleteStartupBackgroundInitializationPresentation();
        Assert.AreEqual("lr2", hub.Rows.Single().Key);
    }

    [TestMethod]
    public void PlaylistRows_KeepIndependentRequestsAndMergeOnlyMatchingDedicatedWork()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        PlaylistWorkspaceViewModel workspace = AttachPlaylistProgressSources(hub);
        var bmt = new OperationProgressRequest(1, 1, "scheduler:beatoraja_bmt_export_all", 1);
        var repair = new OperationProgressRequest(1, 1, "scheduler:playlist_custom_folder_output_repair", 2);
        OperationProgressRequest other = bmt with { Version = 3 };
        hub.BeginBackgroundProgressGeneration(1);
        hub.BeginStartupBackgroundInitializationPresentation(1, 1);
        hub.UpdateBackgroundTaskProgress(new("beatoraja_bmt_export_all", 1, 1, true, true, bmt));
        hub.UpdateBackgroundTaskProgress(new("playlist_custom_folder_output_repair", 1, 2, true, true, repair));
        workspace.BeginPlaylistSyncProgressOperation();
        workspace.ReportPlaylistSyncProgress(new() { Source = "playlist", IsActive = true, TotalTableCount = 3, CompletedTableCount = 1 });
        workspace.ReportPlaylistSyncProgress(new() { Source = "bmt", OperationId = 7, Request = bmt, SingleLabel = Resources.Statusbar_progress_task_bmt_output, LabelFormat = Resources.Statusbar_progress_task_bmt_output + " {0}/{1}", IsActive = true, TotalTableCount = 2, CompletedTableCount = 1 });
        workspace.ReportPlaylistSyncProgress(new() { Source = "bmt", OperationId = 8, Request = other, IsActive = true, TotalTableCount = 4, CompletedTableCount = 2 });
        workspace.ReportPlaylistSyncProgress(new() { Source = "custom_folder_repair", Request = repair, SingleLabel = Resources.Statusbar_progress_task_custom_folder_repair, LabelFormat = Resources.Statusbar_progress_task_custom_folder_repair + " {0}/{1}", IsActive = true, TotalTableCount = 5, CompletedTableCount = 1 });
        Assert.AreEqual(5, hub.Rows.Count);
        Assert.IsFalse(hub.Rows.Any(row => row.Key.StartsWith("background:")));
        Assert.IsTrue(hub.Rows.Where(row => row.Key.StartsWith("playlist:bmt:")).All(row => row.ParentKey == "startup_background"));
        StringAssert.Contains(hub.Rows.Single(row => row.Key.StartsWith("playlist:bmt:7:")).Label, Resources.Statusbar_progress_task_bmt_output);
        StringAssert.Contains(hub.Rows.Single(row => row.Key.StartsWith("playlist:custom_folder_repair:")).Label, Resources.Statusbar_progress_task_custom_folder_repair);
        workspace.ReportPlaylistSyncProgress(new() { Source = "bmt", OperationId = 7, Request = bmt, IsActive = false });
        Assert.IsFalse(hub.Rows.Any(row => row.Key.StartsWith("playlist:bmt:7:")));
        Assert.IsTrue(hub.Rows.Any(row => row.Key.StartsWith("playlist:bmt:8:")));
        workspace.EndPlaylistSyncProgressOperation();
        workspace.ReportPlaylistSyncProgress(new() { Source = "bmt", OperationId = 8, Request = other, IsActive = false });
        workspace.ReportPlaylistSyncProgress(new() { Source = "custom_folder_repair", Request = repair, IsActive = false });
        Assert.IsFalse(hub.Rows.Any(row => row.Key.StartsWith("playlist:")));
        Assert.AreEqual(3, hub.Rows.Count, "専用件数通知だけの終端では実行中の汎用処理を消さない。");
        hub.UpdateBackgroundTaskProgress(new("beatoraja_bmt_export_all", 1, 1, true, false, bmt));
        hub.UpdateBackgroundTaskProgress(new("playlist_custom_folder_output_repair", 1, 2, true, false, repair));
        hub.CompleteStartupBackgroundInitializationPresentation();
        Assert.IsFalse(hub.HasRows);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void PlaylistRows_SyncAndBeatorajaImportRemainIndependentInEitherCompletionOrder(bool syncCompletesFirst)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        var presentation = new ConcurrentQueue<Action>();
        PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceTestPorts.CreateProgressWorkspace(action => action());
        hub.AttachPlaylistProgressSources(workspace, presentation.Enqueue, () => false);
        hub.BeginBackgroundProgressGeneration(1);
        hub.BeginStartupBackgroundInitializationPresentation(1, 1);
        var request = new OperationProgressRequest(1, 1, "external_playlist_sync", 1);
        const string genericKey = "background:external_playlist_sync:1:1:1:external_playlist_sync:1";
        const string syncKey = "playlist:external_playlist_sync:0:1:1:external_playlist_sync:1";
        const string importKey = "playlist:beatoraja_table_url_import:0";
        workspace.BeginPlaylistSyncProgressOperation("external_playlist_sync", request);
        workspace.BeginPlaylistSyncProgressOperation("beatoraja_table_url_import");
        try
        {
            hub.UpdateBackgroundTaskProgress(new("external_playlist_sync", 1, 1, true, true, request));
            workspace.ReportPlaylistSyncProgress(new()
            {
                Source = "beatoraja_table_url_import",
                IsActive = true,
                TotalTableCount = 5,
                CompletedTableCount = 2,
                CurrentTableName = "ImportTarget",
                CurrentUri = new Uri("https://example.test/import.json")
            });
            DrainProgressPresentation(presentation);
            Assert.AreEqual("startup_background", hub.Rows.Single(row => row.Key == genericKey).ParentKey);
            Assert.IsFalse(hub.Rows.Single(row => row.Key == importKey).IsChild);
            Assert.IsFalse(hub.Rows.Any(row => row.Key == syncKey));

            workspace.ReportPlaylistSyncProgress(new()
            {
                Source = "external_playlist_sync",
                Request = request,
                IsActive = true,
                TotalTableCount = 4,
                CompletedTableCount = 1,
                CurrentTableName = "SyncTarget",
                CurrentUri = new Uri("https://example.test/sync.json")
            });
            DrainProgressPresentation(presentation);
            Assert.IsFalse(hub.Rows.Any(row => row.Key == genericKey));
            Assert.AreEqual("startup_background", hub.Rows.Single(row => row.Key == syncKey).ParentKey);
            Assert.IsFalse(hub.Rows.Single(row => row.Key == importKey).IsChild);
            OperationProgressRow surviving = hub.Rows.Single(row => row.Key == (syncCompletesFirst ? importKey : syncKey));
            Assert.AreEqual(syncCompletesFirst ? 2d : 1d, surviving.Value);
            Assert.AreEqual(syncCompletesFirst ? 5d : 4d, surviving.Maximum);
            StringAssert.Contains(surviving.Detail, syncCompletesFirst ? "ImportTarget" : "SyncTarget");

            workspace.EndPlaylistSyncProgressOperation(syncCompletesFirst ? "external_playlist_sync" : "beatoraja_table_url_import", syncCompletesFirst ? request : null);
            if (syncCompletesFirst)
            {
                hub.UpdateBackgroundTaskProgress(new("external_playlist_sync", 1, 1, true, false, request));
            }
            Assert.IsTrue(hub.Rows.Any(row => row.Key == syncKey) && hub.Rows.Any(row => row.Key == importKey),
                "終端の受付は表示callbackの排出を待たない。");
            DrainProgressPresentation(presentation);
            Assert.IsFalse(hub.Rows.Any(row => row.Key == (syncCompletesFirst ? syncKey : importKey)));
            OperationProgressRow remaining = hub.Rows.Single(row => row.Key == surviving.Key);
            Assert.AreEqual(surviving.Value, remaining.Value);
            Assert.AreEqual(surviving.Maximum, remaining.Maximum);
            Assert.AreEqual(surviving.Detail, remaining.Detail);
            Assert.AreEqual(surviving.ParentKey, remaining.ParentKey);
            Assert.IsFalse(hub.Rows.Any(row => row.Key == genericKey));

            workspace.EndPlaylistSyncProgressOperation(syncCompletesFirst ? "beatoraja_table_url_import" : "external_playlist_sync", syncCompletesFirst ? null : request);
            if (!syncCompletesFirst)
            {
                hub.UpdateBackgroundTaskProgress(new("external_playlist_sync", 1, 1, true, false, request));
            }
            Assert.IsTrue(hub.Rows.Any(row => row.Key == surviving.Key));
            DrainProgressPresentation(presentation);
            Assert.IsFalse(hub.Rows.Any(row => row.Key == syncKey || row.Key == importKey || row.Key == genericKey));
        }
        finally
        {
            workspace.EndPlaylistSyncProgressOperation("external_playlist_sync", request);
            workspace.EndPlaylistSyncProgressOperation("beatoraja_table_url_import");
            hub.UpdateBackgroundTaskProgress(new("external_playlist_sync", 1, 1, true, false, request));
            DrainProgressPresentation(presentation);
            hub.CompleteStartupBackgroundInitializationPresentation();
        }
    }

    private static void DrainProgressPresentation(ConcurrentQueue<Action> presentation)
    {
        while (presentation.TryDequeue(out Action? apply))
        {
            apply();
        }
    }

    [TestMethod]
    public void InitializationRows_RejectOldTokensKeepConcurrentDetailsAndUsePhaseCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create();
        var hub = new OperationProgressHubViewModel(owner);
        long oldToken = owner.StartStartupProgressOperation(StartupProgressOperationKind.ReloadFileDiff);
        long token = owner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        owner.UpdateStartupProgressLibraryInitializationStatuses([
            new(1, BMSLibrary.LibraryInitializationProgressStage.DatabaseLoad, "", 0, 0, "", token),
            new(2, BMSLibrary.LibraryInitializationProgressStage.FileEnumeration, "BMS", 0, 12, "", token),
            new(3, BMSLibrary.LibraryInitializationProgressStage.Lr2FolderFileCheck, "", 8, 8, "folder.lr2", token)]);
        Assert.AreEqual(4, hub.Rows.Count);
        Assert.IsTrue(hub.Rows.Where(row => row.Key != "startup").All(row => row.ParentKey == "startup"));
        Assert.IsTrue(hub.Rows.Single(row => row.Key.Contains("DatabaseLoad")).IsIndeterminate);
        Assert.IsTrue(hub.Rows.Single(row => row.Key.Contains("FileEnumeration")).IsIndeterminate);
        OperationProgressRow lr2 = hub.Rows.Single(row => row.Key.Contains("Lr2FolderFileCheck"));
        Assert.AreEqual(8d, lr2.Maximum);
        Assert.AreEqual(8d, lr2.Value);
        owner.UpdateStartupProgressLibraryInitializationStatuses([
            new(4, BMSLibrary.LibraryInitializationProgressStage.FileDiff, "", 99, 1, "old", oldToken)]);
        Assert.AreEqual(4, hub.Rows.Count);
        owner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.LibraryDatabaseLoadDone, token);
        Assert.IsFalse(hub.Rows.Any(row => row.Key.Contains("DatabaseLoad")));
        Assert.IsTrue(hub.Rows.Any(row => row.Key.Contains("Lr2FolderFileCheck")), "準備数100%を保存成功として消さない。");
        owner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.LibraryFileDiffDone, token);
        Assert.IsFalse(hub.Rows.Any(row => row.Key.Contains("Lr2FolderFileCheck")));
    }

    [TestMethod]
    public void BackgroundRows_KeepParallelWorkAndRejectOldGeneration()
    {
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        hub.StartupProgress.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        var score = new OperationProgressRequest(2, 1, "score_hydration_deferred", 2);
        var index = new OperationProgressRequest(2, 1, "scheduler:playlist_library_index_prewarm", 2);
        hub.StartupProgress.TrackStartupProgressScoreHydrationRequested(2, score);
        hub.BeginStartupBackgroundInitializationPresentation(1, 2);
        hub.BeginBackgroundProgressGeneration(2);
        hub.UpdateBackgroundTaskProgress(new("score_hydration_deferred", 2, 2, false, true, score));
        hub.UpdateBackgroundTaskProgress(new("playlist_library_index_prewarm", 2, 2, true, true, index));
        Assert.AreEqual(4, hub.Rows.Count);
        Assert.AreEqual("startup", hub.Rows.Single(row => row.Key.Contains("score_hydration")).ParentKey);
        Assert.AreEqual("startup_background", hub.Rows.Single(row => row.Key.Contains("playlist_library_index")).ParentKey);
        OperationProgressRow current = hub.Rows.Single(row => row.Key.Contains("score_hydration"));
        double parentValue = hub.StartupProgress.Value;
        double parentMaximum = hub.StartupProgress.Maximum;
        hub.UpdateBackgroundTaskProgress(new("score_hydration_deferred", 1, 1, false, false));
        Assert.AreEqual(current, hub.Rows.Single(row => row.Key.Contains("score_hydration")));
        Assert.AreEqual(parentValue, hub.StartupProgress.Value);
        Assert.AreEqual(parentMaximum, hub.StartupProgress.Maximum);
        hub.StartupProgress.ApplyPresentation(false, string.Empty, string.Empty, 0, 1);
        hub.CompleteStartupBackgroundInitializationPresentation();
        Assert.IsTrue(hub.Rows.All(row => !row.IsChild));
        hub.UpdateBackgroundTaskProgress(new("score_hydration_deferred", 1, 1, false, false));
        Assert.AreEqual(2, hub.Rows.Count);
        hub.UpdateBackgroundTaskProgress(new("score_hydration_deferred", 2, 2, false, false, score));
        Assert.AreEqual(1, hub.Rows.Count);
        hub.BeginBackgroundProgressGeneration(3);
        Assert.IsFalse(hub.HasRows);
    }

    [DataTestMethod]
    [DataRow(false, "score_hydration_deferred", 2, true)]
    [DataRow(true, "ranking_refresh_deferred", 2, true)]
    [DataRow(true, "ranking_refresh_deferred", 1, false)]
    [DataRow(false, "ranking_refresh_deferred", 2, false)]
    public void ExecutionRows_BelongToParentOnlyForExpectedMatchingRequest(
        bool fullReinitialize, string name, int reportedVersion, bool expectedChild)
    {
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        hub.StartupProgress.StartStartupProgressOperation(fullReinitialize
            ? StartupProgressOperationKind.FullReinitialize : StartupProgressOperationKind.Startup);
        hub.StartupProgress.TrackStartupProgressScoreHydrationRequested(2, new(1, 1, "score_hydration_deferred", 2));
        hub.StartupProgress.TrackStartupProgressRankingRefreshRequested(2, new(1, 1, "ranking_refresh_deferred", 2));
        double parentValue = hub.StartupProgress.Value;
        double parentMaximum = hub.StartupProgress.Maximum;
        hub.BeginBackgroundProgressGeneration(1);
        hub.UpdateBackgroundTaskProgress(new(name, 1, reportedVersion, false, true, new(1, 1, name, reportedVersion)));
        OperationProgressRow execution = hub.Rows.Single(row => row.Key.StartsWith("background:"));
        Assert.AreEqual(expectedChild, execution.IsChild);
        Assert.AreEqual(parentValue, hub.StartupProgress.Value);
        Assert.AreEqual(parentMaximum, hub.StartupProgress.Maximum);
    }

    [DataTestMethod]
    [DataRow(0L)]
    [DataRow(7L)]
    public void PlaylistRows_LateTerminalWithSameSourceAndOperationIdKeepsDifferentAcceptedRequest(long operationId)
    {
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        PlaylistWorkspaceViewModel workspace = AttachPlaylistProgressSources(hub);
        var first = new OperationProgressRequest(1, 11, "external_playlist_sync", 1);
        var next = new OperationProgressRequest(1, 12, "external_playlist_sync", 2);
        workspace.ReportPlaylistSyncProgress(new() { Source = "external_playlist_sync", OperationId = operationId, Request = first, IsActive = true, TotalTableCount = 2 });
        workspace.ReportPlaylistSyncProgress(new() { Source = "external_playlist_sync", OperationId = operationId, Request = next, IsActive = true, TotalTableCount = 4, CompletedTableCount = 1, CurrentTableName = "next" });
        Assert.AreEqual(2, hub.Rows.Count);
        workspace.ReportPlaylistSyncProgress(new() { Source = "external_playlist_sync", OperationId = operationId, Request = first, IsActive = false });
        OperationProgressRow remaining = hub.Rows.Single();
        Assert.AreEqual(4d, remaining.Maximum);
        Assert.AreEqual(1d, remaining.Value);
        Assert.AreEqual("next", remaining.Detail);
        workspace.ReportPlaylistSyncProgress(new() { Source = "external_playlist_sync", OperationId = operationId, Request = next, IsActive = false });
        Assert.IsFalse(hub.HasRows);
    }

    [DataTestMethod]
    [DataRow("score_hydration_deferred")]
    [DataRow("ranking_refresh_deferred")]
    public void ExecutionRows_ScoreOnlyAttachesOnlyMatchingModelRequest(string name)
    {
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create();
        owner.StartStartupProgressOperation(StartupProgressOperationKind.ScoreOnly);
        var request = new OperationProgressRequest(1, owner.GetActiveStartupProgressOperationToken(), name, 3);
        if (name == "score_hydration_deferred") { owner.TrackStartupProgressScoreHydrationRequested(3, request); }
        else { owner.TrackStartupProgressRankingRefreshRequested(3, request); }
        var hub = new OperationProgressHubViewModel(owner);
        hub.BeginBackgroundProgressGeneration(1);
        hub.UpdateBackgroundTaskProgress(new(name, 1, 3, false, true, request));
        Assert.AreEqual("startup", hub.Rows.Single(row => row.Key.StartsWith("background:")).ParentKey);
        hub.UpdateBackgroundTaskProgress(new(name, 1, 4, false, true, request with { Version = 4 }));
        Assert.AreEqual(2, hub.Rows.Count(row => row.Key.StartsWith("background:")));
        Assert.IsFalse(hub.Rows.Single(row => row.Key.StartsWith("background:") && row.Key.Contains(":4:")).IsChild);
    }

    private static OperationProgressRow? GetRow(OperationProgressHubViewModel hub, string key) =>
        hub.Rows.SingleOrDefault(row => row.Key == key);

    private static OperationProgressRow? GetPipelineRow(OperationProgressHubViewModel hub) =>
        GetRow(hub, "install") ?? GetRow(hub, "estimate");

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
                new NoOpChartMutationPlaybackPort(),
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
                new NoOpChartMutationPlaybackPort(),
                action => Task.Factory.StartNew(
                    action,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap(),
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
