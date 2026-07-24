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
        using ProgressWorkflowFixture fixture = ProgressWorkflowFixture.Create(hub);

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

        fixture.StartPackageProgress();
        Assert.IsTrue(SpinWait.SpinUntil(() => hub.InstallPipelineValue == 1, TimeSpan.FromSeconds(5)));
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
            var playlistWorkspace = PlaylistWorkspaceTestPorts.CreateProgressWorkspace(
                action => action(),
                new PlaylistUrlAcquisitionWorkflow(urlGateway, _ => { }),
                new AcceptedDialogService());
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
    public void InstallPipelinePresentation_NormalizesEmptyMaximumAndInactiveState()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());

        using ProgressWorkflowFixture fixture = ProgressWorkflowFixture.Create(hub, packageTotalCount: 0);
        fixture.StartPackageProgress();
        Assert.IsTrue(SpinWait.SpinUntil(() => hub.IsInstallPipelineStatusActive, TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, hub.InstallPipelineMaximum);

        fixture.ReleasePackageProgress();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Package.IsIdle, TimeSpan.FromSeconds(5)));
        Assert.IsFalse(hub.IsInstallPipelineStatusActive);
        Assert.AreEqual(0, hub.InstallPipelineValue);
        Assert.AreEqual(1, hub.InstallPipelineMaximum);
    }

    [TestMethod]
    public void FolderAutoRenamePresentation_NormalizesProgressAndClearsOnCompletion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        using ProgressWorkflowFixture fixture = ProgressWorkflowFixture.Create(hub);

        fixture.StartFolderRenameProgress();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.FolderProgressStarted.IsSet, TimeSpan.FromSeconds(5)));

        Assert.IsTrue(hub.IsFolderAutoRenameProgressActive);
        Assert.AreEqual(1.0, hub.FolderAutoRenameProgressMaximum);
        Assert.AreEqual(1.0, hub.FolderAutoRenameProgressValue);
        StringAssert.Contains(hub.FolderAutoRenameProgressLabel, "1/1");
        Assert.AreEqual("source", hub.FolderAutoRenameProgressSubLabel);

        fixture.ReleaseFolderRenameProgress();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Folder.IsIdle, TimeSpan.FromSeconds(5)));
        Assert.IsTrue(SpinWait.SpinUntil(() => !hub.IsFolderAutoRenameProgressActive, TimeSpan.FromSeconds(5)));
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
        using ProgressWorkflowFixture fixture = ProgressWorkflowFixture.Create(hub);

        fixture.StartMaintenanceProgress();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.MaintenanceProgressStarted.IsSet, TimeSpan.FromSeconds(5)));
        Assert.IsTrue(hub.IsMaintenanceRescanProgressActive);
        Assert.AreEqual(1.0, hub.MaintenanceRescanMaximum);
        Assert.AreEqual(1.0, hub.MaintenanceRescanValue);

        fixture.ReleaseMaintenanceProgress();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Maintenance.IsIdle, TimeSpan.FromSeconds(5)));
        Assert.IsTrue(SpinWait.SpinUntil(() => !hub.IsMaintenanceRescanProgressActive, TimeSpan.FromSeconds(5)));
        Assert.IsFalse(hub.IsMaintenanceRescanProgressActive);
        Assert.IsFalse(hub.MaintenanceRescanCanCancel);
    }

    [TestMethod]
    public void WorkflowProgressSources_CannotBeAttachedTwice()
    {
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        using ProgressWorkflowFixture fixture = ProgressWorkflowFixture.Create(hub);

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
        hub.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

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
        hub.StartupProgress.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

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
    public void Lr2SongDbSyncPresentation_UsesRuntimeStatusAndSuppression()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
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
        Assert.IsTrue(hub.IsLr2SongDbSyncCancelVisible);

        hub.StartupProgress.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        hub.StartupProgress.TrackStartupProgressLr2SongDbSyncRequested(1);

        Assert.IsFalse(hub.IsLr2SongDbSyncStatusActive);
        Assert.AreEqual(string.Empty, hub.Lr2SongDbSyncStatusLabel);
        Assert.AreEqual(1.0, hub.Lr2SongDbSyncStatusProgressMaximum);
        Assert.IsFalse(hub.IsLr2SongDbSyncStatusProgressVisible);
        Assert.IsFalse(hub.IsLr2SongDbSyncCancelVisible);
    }

    [TestMethod]
    public void Lr2SongDbSyncPresentation_UsesCleanupForStartupScanBlockersAndClearsNotNeeded()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        Lr2SongDbSyncRuntimeStatus blockers = Lr2SongDbSyncStatusMapper.Create(
            new Lr2SongDbSyncStatusSnapshot
            {
                Status = Lr2SongDbSyncStatusKind.Incomplete,
                Stage = Lr2SongDbSyncService.StartupScanBlockersStage,
                LastError = "startup scan blockers"
            },
            DateTime.MinValue);

        hub.UpdateLr2SongDbSyncStatus(blockers);

        Assert.IsTrue(hub.IsLr2SongDbSyncStatusActive);
        Assert.IsFalse(hub.IsLr2SongDbSyncRetryVisible);
        Assert.IsFalse(hub.IsLr2SongDbSyncCancelVisible);
        Assert.IsTrue(hub.IsLr2SongDbSyncCleanupVisible);

        hub.UpdateLr2SongDbSyncStatus(null);

        Assert.IsFalse(hub.IsLr2SongDbSyncStatusActive);
        Assert.AreEqual(string.Empty, hub.Lr2SongDbSyncStatusLabel);
        Assert.AreEqual(string.Empty, hub.Lr2SongDbSyncStatusSubLabel);
        Assert.AreEqual(string.Empty, hub.Lr2SongDbSyncStatusToolTip);
        Assert.AreEqual(0.0, hub.Lr2SongDbSyncStatusProgressValue);
        Assert.AreEqual(1.0, hub.Lr2SongDbSyncStatusProgressMaximum);
        Assert.IsFalse(hub.IsLr2SongDbSyncStatusProgressVisible);
        Assert.IsFalse(hub.IsLr2SongDbSyncRetryVisible);
        Assert.IsFalse(hub.IsLr2SongDbSyncCancelVisible);
        Assert.IsFalse(hub.IsLr2SongDbSyncCleanupVisible);
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
            BMSLibrary library = new(databasePath, null, null, string.Empty);
            var packageProgressStarted = new ManualResetEventSlim(false);
            var maintenanceProgressStarted = new ManualResetEventSlim(false);
            var folderProgressStarted = new ManualResetEventSlim(false);
            ProgressWorkflowFixture fixture = null!;
            var package = new PackageInstallWorkflowOwner(
                (current, paths, token, onPath, onArchive) =>
                {
                    onPath();
                    onArchive(paths.FirstOrDefault() ?? string.Empty, 1, packageTotalCount);
                    packageProgressStarted.Set();
                    fixture.packageRelease.Wait(TimeSpan.FromSeconds(10));
                    return [];
                },
                action => action());
            var maintenance = new MaintenanceRescanWorkflowOwner(
                (current, progress, token) =>
                {
                    progress(new MaintenanceWorkflowProgress { TotalCount = 1, ProcessedCount = 1 });
                    maintenanceProgressStarted.Set();
                    fixture.maintenanceRelease.Wait(TimeSpan.FromSeconds(10));
                    return new MaintenanceWorkflowResult();
                },
                action => Task.Run(action),
                action => action(),
                dialogs: new AcceptedDialogService());
            var folder = new FolderAutoRenameWorkflowOwner(
                (current, request, progress) => new FolderAutoRenameExecutionResult(),
                (current, parentDirectory, progress) =>
                {
                    progress(1, 1, "source");
                    folderProgressStarted.Set();
                    fixture.folderRelease.Wait(TimeSpan.FromSeconds(10));
                    return new FolderAutoRenameExecutionResult();
                },
                (current, parentDirectory) => true,
                action => Task.Run(action),
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
            SpinWait.SpinUntil(() => Package.IsIdle, TimeSpan.FromSeconds(5));
            SpinWait.SpinUntil(() => Maintenance.IsIdle, TimeSpan.FromSeconds(5));
            SpinWait.SpinUntil(() => Folder.IsIdle, TimeSpan.FromSeconds(5));
            packageRelease.Dispose();
            maintenanceRelease.Dispose();
            folderRelease.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
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
