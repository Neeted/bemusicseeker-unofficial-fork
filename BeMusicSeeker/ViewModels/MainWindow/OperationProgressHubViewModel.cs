using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns status-bar progress presentation state for direct shell binding.
/// </summary>
public sealed class OperationProgressHubViewModel : ViewModel
{
    private bool isInstallPipelineStatusActive;

    private string installPipelineLabel = string.Empty;

    private string installPipelineSubLabel = string.Empty;

    private int installPipelineValue;

    private int installPipelineMaximum = 1;

    private bool installPipelineCanCancel;

    private bool isMaintenanceRescanProgressActive;

    private string maintenanceRescanLabel = string.Empty;

    private string maintenanceRescanSubLabel = string.Empty;

    private double maintenanceRescanValue;

    private double maintenanceRescanMaximum = 1.0;

    private bool maintenanceRescanCanCancel;

    private bool isFolderAutoRenameProgressActive;

    private string folderAutoRenameProgressLabel = string.Empty;

    private string folderAutoRenameProgressSubLabel = string.Empty;

    private double folderAutoRenameProgressValue;

    private double folderAutoRenameProgressMaximum = 1.0;

    private bool isPlaylistSyncProgressActive;

    private string playlistSyncProgressLabel = string.Empty;

    private string playlistSyncProgressSubLabel = string.Empty;

    private double playlistSyncProgressValue;

    private double playlistSyncProgressMaximum;

    private bool isStartupProgressActive;

    private string startupProgressLabel = string.Empty;

    private string startupProgressSubLabel = string.Empty;

    private double startupProgressValue;

    private double startupProgressMaximum;

    private bool isLr2SongDbSyncStatusActive;

    private string lr2SongDbSyncStatusLabel = string.Empty;

    private string lr2SongDbSyncStatusSubLabel = string.Empty;

    private string lr2SongDbSyncStatusToolTip = string.Empty;

    private double lr2SongDbSyncStatusProgressValue;

    private double lr2SongDbSyncStatusProgressMaximum = 1.0;

    private bool isLr2SongDbSyncStatusProgressVisible;

    private bool isLr2SongDbSyncRetryVisible;

    private bool isLr2SongDbSyncCancelVisible;

    private bool isLr2SongDbSyncCleanupVisible;

    private Lr2SongDbSyncRuntimeStatus latestLr2SongDbSyncStatus = Lr2SongDbSyncStatusMapper.CreateNone();

    private bool isStartupProgressBlockingLr2SongDbSyncStatus;

    private DropInstallQueueStatusSnapshot dropInstallQueueStatus = new();

    private PendingInstallEstimateQueueStatusSnapshot pendingInstallQueueStatus = new();

    private InstallEstimationProgressSnapshot installEstimationProgress = new();

    private PlaylistUrlDownloadStatusSnapshot playlistUrlDownloadStatus = PlaylistUrlDownloadStatusSnapshot.Inactive;

    /// <summary>
    /// Gets whether install, drop-install, playlist URL download, or estimate status is visible.
    /// </summary>
    public bool IsInstallPipelineStatusActive
    {
        get => isInstallPipelineStatusActive;
        internal set => SetValue(ref isInstallPipelineStatusActive, value, nameof(IsInstallPipelineStatusActive));
    }

    /// <summary>
    /// Gets the primary install pipeline status label.
    /// </summary>
    public string InstallPipelineLabel
    {
        get => installPipelineLabel;
        internal set => SetStringValue(ref installPipelineLabel, value, nameof(InstallPipelineLabel));
    }

    /// <summary>
    /// Gets the secondary install pipeline status label.
    /// </summary>
    public string InstallPipelineSubLabel
    {
        get => installPipelineSubLabel;
        internal set => SetStringValue(ref installPipelineSubLabel, value, nameof(InstallPipelineSubLabel));
    }

    /// <summary>
    /// Gets the current install pipeline progress value.
    /// </summary>
    public int InstallPipelineValue
    {
        get => installPipelineValue;
        internal set => SetValue(ref installPipelineValue, value, nameof(InstallPipelineValue));
    }

    /// <summary>
    /// Gets the install pipeline progress maximum, normalized to at least one for progress-bar binding.
    /// </summary>
    public int InstallPipelineMaximum
    {
        get => installPipelineMaximum;
        internal set => SetValue(ref installPipelineMaximum, Math.Max(1, value), nameof(InstallPipelineMaximum));
    }

    /// <summary>
    /// Gets whether the active install pipeline work can be canceled.
    /// </summary>
    public bool InstallPipelineCanCancel
    {
        get => installPipelineCanCancel;
        internal set => SetValue(ref installPipelineCanCancel, value, nameof(InstallPipelineCanCancel));
    }

    /// <summary>
    /// Gets whether maintenance rescan progress is visible.
    /// </summary>
    public bool IsMaintenanceRescanProgressActive
    {
        get => isMaintenanceRescanProgressActive;
        internal set => SetValue(ref isMaintenanceRescanProgressActive, value, nameof(IsMaintenanceRescanProgressActive));
    }

    /// <summary>
    /// Gets the primary maintenance rescan progress label.
    /// </summary>
    public string MaintenanceRescanLabel
    {
        get => maintenanceRescanLabel;
        internal set => SetStringValue(ref maintenanceRescanLabel, value, nameof(MaintenanceRescanLabel));
    }

    /// <summary>
    /// Gets the secondary maintenance rescan progress label.
    /// </summary>
    public string MaintenanceRescanSubLabel
    {
        get => maintenanceRescanSubLabel;
        internal set => SetStringValue(ref maintenanceRescanSubLabel, value, nameof(MaintenanceRescanSubLabel));
    }

    /// <summary>
    /// Gets the current maintenance rescan progress value.
    /// </summary>
    public double MaintenanceRescanValue
    {
        get => maintenanceRescanValue;
        internal set => SetValue(ref maintenanceRescanValue, value, nameof(MaintenanceRescanValue));
    }

    /// <summary>
    /// Gets the maintenance rescan progress maximum, normalized to at least one for progress-bar binding.
    /// </summary>
    public double MaintenanceRescanMaximum
    {
        get => maintenanceRescanMaximum;
        internal set => SetValue(ref maintenanceRescanMaximum, Math.Max(1.0, value), nameof(MaintenanceRescanMaximum));
    }

    /// <summary>
    /// Gets whether active maintenance rescan work can be canceled.
    /// </summary>
    public bool MaintenanceRescanCanCancel
    {
        get => maintenanceRescanCanCancel;
        internal set => SetValue(ref maintenanceRescanCanCancel, value, nameof(MaintenanceRescanCanCancel));
    }

    internal void UpdateMaintenanceRescanProgress(MaintenanceWorkflowProgress progress)
    {
        if (progress == null)
        {
            return;
        }
        if (progress.IsCompleted)
        {
            MaintenanceRescanLabel = progress.IsCanceled
                ? BeMusicSeeker.Properties.Resources.Maintenance_rescan_canceled
                : BeMusicSeeker.Properties.Resources.Maintenance_rescan_complete;
            MaintenanceRescanSubLabel = string.Empty;
            MaintenanceRescanCanCancel = false;
            IsMaintenanceRescanProgressActive = false;
            return;
        }

        IsMaintenanceRescanProgressActive = true;
        int total = Math.Max(progress.TotalCount, 1);
        int processed = Math.Max(0, Math.Min(progress.ProcessedCount, total));
        MaintenanceRescanMaximum = total;
        MaintenanceRescanValue = processed;
        MaintenanceRescanLabel = string.Format(
            BeMusicSeeker.Properties.Resources.Maintenance_rescan_progress_label_format,
            processed,
            total);
        MaintenanceRescanSubLabel = progress.CurrentPath ?? string.Empty;
        MaintenanceRescanCanCancel = !progress.IsCanceled;
    }

    /// <summary>
    /// Gets whether automatic folder rename progress is visible.
    /// </summary>
    public bool IsFolderAutoRenameProgressActive
    {
        get => isFolderAutoRenameProgressActive;
        internal set => SetValue(ref isFolderAutoRenameProgressActive, value, nameof(IsFolderAutoRenameProgressActive));
    }

    /// <summary>
    /// Gets the primary automatic folder rename progress label.
    /// </summary>
    public string FolderAutoRenameProgressLabel
    {
        get => folderAutoRenameProgressLabel;
        internal set => SetStringValue(ref folderAutoRenameProgressLabel, value, nameof(FolderAutoRenameProgressLabel));
    }

    /// <summary>
    /// Gets the secondary automatic folder rename progress label.
    /// </summary>
    public string FolderAutoRenameProgressSubLabel
    {
        get => folderAutoRenameProgressSubLabel;
        internal set => SetStringValue(ref folderAutoRenameProgressSubLabel, value, nameof(FolderAutoRenameProgressSubLabel));
    }

    /// <summary>
    /// Gets the current automatic folder rename progress value.
    /// </summary>
    public double FolderAutoRenameProgressValue
    {
        get => folderAutoRenameProgressValue;
        internal set => SetValue(ref folderAutoRenameProgressValue, value, nameof(FolderAutoRenameProgressValue));
    }

    /// <summary>
    /// Gets the automatic folder rename progress maximum, normalized to at least one for progress-bar binding.
    /// </summary>
    public double FolderAutoRenameProgressMaximum
    {
        get => folderAutoRenameProgressMaximum;
        internal set => SetValue(ref folderAutoRenameProgressMaximum, Math.Max(1.0, value), nameof(FolderAutoRenameProgressMaximum));
    }

    internal void UpdateFolderAutoRenameProgress(FolderAutoRenameProgressSnapshot progress)
    {
        if (progress == null)
        {
            return;
        }
        if (progress.IsCompleted)
        {
            FolderAutoRenameProgressLabel = string.Empty;
            FolderAutoRenameProgressSubLabel = string.Empty;
            FolderAutoRenameProgressValue = 0.0;
            FolderAutoRenameProgressMaximum = 1.0;
            IsFolderAutoRenameProgressActive = false;
            return;
        }

        int total = Math.Max(progress.TotalCount, 1);
        int processed = Math.Max(0, Math.Min(progress.ProcessedCount, total));
        IsFolderAutoRenameProgressActive = true;
        FolderAutoRenameProgressMaximum = total;
        FolderAutoRenameProgressValue = processed;
        FolderAutoRenameProgressLabel = BeMusicSeeker.Properties.Resources.Rename_folder_auto + " " + processed + "/" + total;
        FolderAutoRenameProgressSubLabel = progress.CurrentPath ?? string.Empty;
    }

    /// <summary>
    /// Gets whether playlist sync progress is visible.
    /// </summary>
    public bool IsPlaylistSyncProgressActive
    {
        get => isPlaylistSyncProgressActive;
        internal set => SetValue(ref isPlaylistSyncProgressActive, value, nameof(IsPlaylistSyncProgressActive));
    }

    /// <summary>
    /// Gets the primary playlist sync progress label.
    /// </summary>
    public string PlaylistSyncProgressLabel
    {
        get => playlistSyncProgressLabel;
        internal set => SetStringValue(ref playlistSyncProgressLabel, value, nameof(PlaylistSyncProgressLabel));
    }

    /// <summary>
    /// Gets the secondary playlist sync progress label.
    /// </summary>
    public string PlaylistSyncProgressSubLabel
    {
        get => playlistSyncProgressSubLabel;
        internal set => SetStringValue(ref playlistSyncProgressSubLabel, value, nameof(PlaylistSyncProgressSubLabel));
    }

    /// <summary>
    /// Gets the current playlist sync progress value.
    /// </summary>
    public double PlaylistSyncProgressValue
    {
        get => playlistSyncProgressValue;
        internal set => SetValue(ref playlistSyncProgressValue, value, nameof(PlaylistSyncProgressValue));
    }

    /// <summary>
    /// Gets the playlist sync progress maximum.
    /// </summary>
    public double PlaylistSyncProgressMaximum
    {
        get => playlistSyncProgressMaximum;
        internal set => SetValue(ref playlistSyncProgressMaximum, value, nameof(PlaylistSyncProgressMaximum));
    }

    internal void UpdatePlaylistSyncProgress(PlaylistSyncProgressSnapshot snapshot)
    {
        bool isActive = snapshot?.IsActive == true;
        IsPlaylistSyncProgressActive = isActive;
        if (!isActive)
        {
            PlaylistSyncProgressLabel = string.Empty;
            PlaylistSyncProgressSubLabel = string.Empty;
            PlaylistSyncProgressValue = 0.0;
            PlaylistSyncProgressMaximum = 0.0;
            return;
        }

        int total = Math.Max(snapshot.TotalTableCount, 1);
        int completed = Math.Max(0, Math.Min(snapshot.CompletedTableCount, total));
        PlaylistSyncProgressMaximum = total;
        PlaylistSyncProgressValue = completed;
        string labelFormat = !string.IsNullOrWhiteSpace(snapshot.LabelFormat)
            ? snapshot.LabelFormat
            : BeMusicSeeker.Properties.Resources.Playlist_sync_progress_label_format;
        string singleLabel = !string.IsNullOrWhiteSpace(snapshot.SingleLabel)
            ? snapshot.SingleLabel
            : BeMusicSeeker.Properties.Resources.Playlist_sync_progress_single_label;
        PlaylistSyncProgressLabel = snapshot.TotalTableCount > 0
            ? string.Format(labelFormat, completed, total)
            : singleLabel;
        PlaylistSyncProgressSubLabel = !string.IsNullOrWhiteSpace(snapshot.CurrentTableName)
            ? snapshot.CurrentTableName
            : (snapshot.CurrentUri?.ToString() ?? string.Empty);
    }

    /// <summary>
    /// Gets whether startup or reload progress is visible.
    /// </summary>
    public bool IsStartupProgressActive
    {
        get => isStartupProgressActive;
        internal set => SetValue(ref isStartupProgressActive, value, nameof(IsStartupProgressActive));
    }

    /// <summary>
    /// Gets the primary startup or reload progress label.
    /// </summary>
    public string StartupProgressLabel
    {
        get => startupProgressLabel;
        internal set => SetStringValue(ref startupProgressLabel, value, nameof(StartupProgressLabel));
    }

    /// <summary>
    /// Gets the secondary startup or reload progress label.
    /// </summary>
    public string StartupProgressSubLabel
    {
        get => startupProgressSubLabel;
        internal set => SetStringValue(ref startupProgressSubLabel, value, nameof(StartupProgressSubLabel));
    }

    /// <summary>
    /// Gets the current startup or reload progress value.
    /// </summary>
    public double StartupProgressValue
    {
        get => startupProgressValue;
        internal set => SetValue(ref startupProgressValue, value, nameof(StartupProgressValue));
    }

    /// <summary>
    /// Gets the startup or reload progress maximum.
    /// </summary>
    public double StartupProgressMaximum
    {
        get => startupProgressMaximum;
        internal set => SetValue(ref startupProgressMaximum, value, nameof(StartupProgressMaximum));
    }

    internal void UpdateStartupProgress(bool isActive, string label, string subLabel, double value, double maximum)
    {
        IsStartupProgressActive = isActive;
        StartupProgressLabel = label;
        StartupProgressSubLabel = subLabel;
        StartupProgressValue = value;
        StartupProgressMaximum = maximum;
    }

    /// <summary>
    /// Gets whether LR2 song DB sync status is visible.
    /// </summary>
    public bool IsLr2SongDbSyncStatusActive
    {
        get => isLr2SongDbSyncStatusActive;
        internal set => SetValue(ref isLr2SongDbSyncStatusActive, value, nameof(IsLr2SongDbSyncStatusActive));
    }

    /// <summary>
    /// Gets the primary LR2 song DB sync status label.
    /// </summary>
    public string Lr2SongDbSyncStatusLabel
    {
        get => lr2SongDbSyncStatusLabel;
        internal set => SetStringValue(ref lr2SongDbSyncStatusLabel, value, nameof(Lr2SongDbSyncStatusLabel));
    }

    /// <summary>
    /// Gets the secondary LR2 song DB sync status label.
    /// </summary>
    public string Lr2SongDbSyncStatusSubLabel
    {
        get => lr2SongDbSyncStatusSubLabel;
        internal set => SetStringValue(ref lr2SongDbSyncStatusSubLabel, value, nameof(Lr2SongDbSyncStatusSubLabel));
    }

    /// <summary>
    /// Gets LR2 song DB sync status detail text for tooltips.
    /// </summary>
    public string Lr2SongDbSyncStatusToolTip
    {
        get => lr2SongDbSyncStatusToolTip;
        internal set => SetStringValue(ref lr2SongDbSyncStatusToolTip, value, nameof(Lr2SongDbSyncStatusToolTip));
    }

    /// <summary>
    /// Gets the current LR2 song DB sync status progress value.
    /// </summary>
    public double Lr2SongDbSyncStatusProgressValue
    {
        get => lr2SongDbSyncStatusProgressValue;
        internal set => SetValue(ref lr2SongDbSyncStatusProgressValue, value, nameof(Lr2SongDbSyncStatusProgressValue));
    }

    /// <summary>
    /// Gets the LR2 song DB sync status progress maximum.
    /// </summary>
    public double Lr2SongDbSyncStatusProgressMaximum
    {
        get => lr2SongDbSyncStatusProgressMaximum;
        internal set => SetValue(ref lr2SongDbSyncStatusProgressMaximum, value, nameof(Lr2SongDbSyncStatusProgressMaximum));
    }

    /// <summary>
    /// Gets whether the LR2 song DB sync status progress bar is visible.
    /// </summary>
    public bool IsLr2SongDbSyncStatusProgressVisible
    {
        get => isLr2SongDbSyncStatusProgressVisible;
        internal set => SetValue(ref isLr2SongDbSyncStatusProgressVisible, value, nameof(IsLr2SongDbSyncStatusProgressVisible));
    }

    /// <summary>
    /// Gets whether the LR2 song DB sync retry action is visible.
    /// </summary>
    public bool IsLr2SongDbSyncRetryVisible
    {
        get => isLr2SongDbSyncRetryVisible;
        internal set => SetValue(ref isLr2SongDbSyncRetryVisible, value, nameof(IsLr2SongDbSyncRetryVisible));
    }

    /// <summary>
    /// Gets whether the LR2 song DB sync cancel action is visible.
    /// </summary>
    public bool IsLr2SongDbSyncCancelVisible
    {
        get => isLr2SongDbSyncCancelVisible;
        internal set => SetValue(ref isLr2SongDbSyncCancelVisible, value, nameof(IsLr2SongDbSyncCancelVisible));
    }

    /// <summary>
    /// Gets whether the LR2 song DB sync cleanup action is visible.
    /// </summary>
    public bool IsLr2SongDbSyncCleanupVisible
    {
        get => isLr2SongDbSyncCleanupVisible;
        internal set => SetValue(ref isLr2SongDbSyncCleanupVisible, value, nameof(IsLr2SongDbSyncCleanupVisible));
    }

    internal void UpdateLr2SongDbSyncStatus(Lr2SongDbSyncRuntimeStatus status, bool startupProgressBlocksStatus)
    {
        latestLr2SongDbSyncStatus = status ?? Lr2SongDbSyncStatusMapper.CreateNone();
        isStartupProgressBlockingLr2SongDbSyncStatus = startupProgressBlocksStatus;
        RecomputeLr2SongDbSyncStatusPresentation();
    }

    internal void UpdateLr2SongDbSyncStatusSuppression(bool startupProgressBlocksStatus)
    {
        isStartupProgressBlockingLr2SongDbSyncStatus = startupProgressBlocksStatus;
        RecomputeLr2SongDbSyncStatusPresentation();
    }

    private void RecomputeLr2SongDbSyncStatusPresentation()
    {
        Lr2SongDbSyncRuntimeStatus status = latestLr2SongDbSyncStatus ?? Lr2SongDbSyncStatusMapper.CreateNone();
        bool isActive = status.HasWarningStatus && !isStartupProgressBlockingLr2SongDbSyncStatus;
        IsLr2SongDbSyncStatusActive = isActive;
        Lr2SongDbSyncStatusLabel = isActive ? status.StatusText : string.Empty;
        Lr2SongDbSyncStatusSubLabel = isActive ? status.ProgressText : string.Empty;
        Lr2SongDbSyncStatusToolTip = isActive ? status.Detail : string.Empty;
        Lr2SongDbSyncStatusProgressValue = isActive ? status.ProgressValue : 0.0;
        Lr2SongDbSyncStatusProgressMaximum = isActive ? status.ProgressMaximum : 1.0;
        IsLr2SongDbSyncStatusProgressVisible = isActive && status.HasProgress;
        IsLr2SongDbSyncRetryVisible = isActive && status.CanRetry;
        IsLr2SongDbSyncCancelVisible = isActive && status.CanCancel;
        IsLr2SongDbSyncCleanupVisible = isActive && status.CanCleanupStartupScanBlockers;
    }

    internal void UpdateDropInstallQueueStatus(DropInstallQueueStatusSnapshot snapshot)
    {
        dropInstallQueueStatus = snapshot ?? new DropInstallQueueStatusSnapshot();
        RefreshInstallPipelinePresentation();
    }

    internal void UpdatePendingEstimateQueueStatus(PendingInstallEstimateQueueStatusSnapshot snapshot)
    {
        pendingInstallQueueStatus = snapshot?.Clone() ?? new PendingInstallEstimateQueueStatusSnapshot();
        RefreshInstallPipelinePresentation();
    }

    internal void UpdateInstallEstimationProgress(InstallEstimationProgressSnapshot snapshot)
    {
        installEstimationProgress = snapshot?.Clone() ?? new InstallEstimationProgressSnapshot();
        RefreshInstallPipelinePresentation();
    }

    internal void UpdatePlaylistUrlDownloadStatus(PlaylistUrlDownloadStatusSnapshot snapshot)
    {
        playlistUrlDownloadStatus = snapshot ?? PlaylistUrlDownloadStatusSnapshot.Inactive;
        RefreshInstallPipelinePresentation();
    }

    private void SetStringValue(ref string storage, string value, string propertyName)
    {
        SetValue(ref storage, value ?? string.Empty, propertyName);
    }

    private void SetValue<T>(ref T storage, T value, string propertyName)
    {
        if (!Equals(storage, value))
        {
            storage = value;
            RaisePropertyChanged(propertyName);
        }
    }

    private void RefreshInstallPipelinePresentation()
    {
        if (playlistUrlDownloadStatus.IsActive)
        {
            IsInstallPipelineStatusActive = true;
            InstallPipelineLabel = string.Format(
                string.IsNullOrWhiteSpace(playlistUrlDownloadStatus.LabelFormat)
                    ? BeMusicSeeker.Properties.Resources.Playlist_url_download_progress_label_format
                    : playlistUrlDownloadStatus.LabelFormat,
                Math.Max(0, playlistUrlDownloadStatus.CompletedCount),
                Math.Max(0, playlistUrlDownloadStatus.TotalCount));
            InstallPipelineSubLabel = playlistUrlDownloadStatus.CurrentDisplayName;
            InstallPipelineMaximum = Math.Max(1, playlistUrlDownloadStatus.TotalCount);
            InstallPipelineValue = Math.Max(0, playlistUrlDownloadStatus.CompletedCount);
            InstallPipelineCanCancel = playlistUrlDownloadStatus.CanCancel;
            return;
        }

        bool dropActive = dropInstallQueueStatus.IsActive;
        bool pendingQueueActive = pendingInstallQueueStatus.IsActive;
        bool estimateActive = installEstimationProgress.IsActive;
        int pendingBatchCount = Math.Max(0, dropInstallQueueStatus.PendingBatchCount)
            + Math.Max(0, pendingInstallQueueStatus.PendingBatchCount);
        if (dropActive)
        {
            IsInstallPipelineStatusActive = true;
            InstallPipelineLabel = string.Format(
                BeMusicSeeker.Properties.Resources.Drop_install_queue_label_format,
                Math.Max(0, dropInstallQueueStatus.CompletedPathCount),
                Math.Max(0, dropInstallQueueStatus.TotalPathCount),
                pendingBatchCount);
            InstallPipelineSubLabel = GetDropInstallQueueSubLabel(dropInstallQueueStatus);
            int currentWorkTotal = Math.Max(0, dropInstallQueueStatus.CurrentWorkTotal);
            int currentWorkIndex = Math.Max(0, dropInstallQueueStatus.CurrentWorkIndex);
            bool showCurrentWorkProgress = dropInstallQueueStatus.IsCurrentWorkInProgress
                && currentWorkTotal > 0
                && currentWorkIndex > 0;
            InstallPipelineMaximum = showCurrentWorkProgress
                ? Math.Max(1, currentWorkTotal)
                : Math.Max(1, dropInstallQueueStatus.TotalPathCount);
            InstallPipelineValue = showCurrentWorkProgress
                ? Math.Min(currentWorkIndex, InstallPipelineMaximum)
                : Math.Max(0, dropInstallQueueStatus.CompletedPathCount);
            InstallPipelineCanCancel = dropInstallQueueStatus.CanCancel;
            return;
        }
        if (estimateActive)
        {
            IsInstallPipelineStatusActive = true;
            InstallPipelineLabel = string.Format(
                BeMusicSeeker.Properties.Resources.Pending_estimate_queue_label_format,
                Math.Max(0, installEstimationProgress.CompletedWorkCount),
                Math.Max(0, installEstimationProgress.TotalWorkCount),
                pendingBatchCount);
            InstallPipelineSubLabel = installEstimationProgress.CurrentDisplayName;
            InstallPipelineMaximum = Math.Max(1, installEstimationProgress.TotalWorkCount);
            InstallPipelineValue = Math.Max(0, installEstimationProgress.CompletedWorkCount);
            InstallPipelineCanCancel = false;
            return;
        }
        if (pendingQueueActive)
        {
            IsInstallPipelineStatusActive = true;
            InstallPipelineLabel = string.Format(
                BeMusicSeeker.Properties.Resources.Pending_estimate_queue_label_format,
                Math.Max(0, pendingInstallQueueStatus.CompletedPackageCount),
                Math.Max(1, pendingInstallQueueStatus.CurrentPackageCount),
                pendingBatchCount);
            InstallPipelineSubLabel = pendingInstallQueueStatus.CurrentDisplayName;
            InstallPipelineMaximum = Math.Max(1, pendingInstallQueueStatus.CurrentPackageCount);
            InstallPipelineValue = Math.Max(0, pendingInstallQueueStatus.CompletedPackageCount);
            InstallPipelineCanCancel = false;
            return;
        }

        IsInstallPipelineStatusActive = false;
        InstallPipelineLabel = string.Empty;
        InstallPipelineSubLabel = string.Empty;
        InstallPipelineValue = 0;
        InstallPipelineMaximum = 1;
        InstallPipelineCanCancel = false;
    }

    private static string GetDropInstallQueueSubLabel(DropInstallQueueStatusSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return string.Empty;
        }
        if (snapshot.IsCurrentWorkInProgress && snapshot.CurrentWorkIndex > 0 && snapshot.CurrentWorkTotal > 0)
        {
            return string.Format(
                BeMusicSeeker.Properties.Resources.Drop_install_queue_extracting_sub_label_format,
                Math.Max(0, snapshot.CurrentWorkIndex),
                Math.Max(0, snapshot.CurrentWorkTotal),
                snapshot.CurrentWorkDisplayName ?? string.Empty);
        }
        return snapshot.CurrentDisplayName ?? string.Empty;
    }
}
