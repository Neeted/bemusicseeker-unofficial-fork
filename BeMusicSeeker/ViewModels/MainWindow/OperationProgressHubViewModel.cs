using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>既存の処理通知から共通行を投影し、独立した処理を同時に表示します。</summary>
public sealed class OperationProgressHubViewModel : ViewModel
{
    private DropInstallQueueStatusSnapshot dropInstallQueueStatus = new();
    private PendingInstallEstimateQueueStatusSnapshot pendingInstallQueueStatus = new();
    private InstallEstimationProgressSnapshot installEstimationProgress = new();
    private PlaylistUrlDownloadStatusSnapshot playlistUrlDownloadStatus = PlaylistUrlDownloadStatusSnapshot.Inactive;
    private MaintenanceWorkflowProgress maintenanceProgress;
    private FolderAutoRenameProgressSnapshot renameProgress;
    private Lr2SongDbSyncRuntimeStatus latestLr2SongDbSyncStatus = Lr2SongDbSyncStatusMapper.CreateNone();
    private bool workflowProgressSourcesAttached;
    private bool playlistProgressSourcesAttached;
    private long startupBackgroundOperationToken;
    private long startupBackgroundOriginGeneration;
    private readonly ConcurrentDictionary<string, long> playlistUiVersions = new();
    private long playlistUiVersionSeed;
    private Action<Action> dispatchPlaylistProgressAction;
    private Func<bool> isShellClosing;
    private readonly Dictionary<string, StartupBackgroundTaskProgressSnapshot> backgroundStatuses = new();
    private readonly Dictionary<string, PlaylistSyncProgressSnapshot> playlistStatuses = new();
    private long backgroundGeneration;

    /// <summary>親操作の段階数と完了条件の正本を取得します。</summary>
    public StartupProgressWorkflowOwner StartupProgress { get; }

    /// <summary>親進捗の正本と表示更新を接続します。</summary>
    internal OperationProgressHubViewModel(StartupProgressWorkflowOwner startupProgress)
    {
        StartupProgress = startupProgress ?? throw new ArgumentNullException(nameof(startupProgress));
        StartupProgress.PropertyChanged += StartupProgressPropertyChanged;
    }

    /// <summary>処理管理主体の進捗を、それぞれ独立した表示行へ接続します。</summary>
    internal void AttachWorkflowProgressSources(PackageInstallWorkflowOwner packageInstallWorkflow,
        MaintenanceRescanWorkflowOwner maintenanceRescanWorkflow, FolderAutoRenameWorkflowOwner folderAutoRenameWorkflow)
    {
        ArgumentNullException.ThrowIfNull(packageInstallWorkflow);
        ArgumentNullException.ThrowIfNull(maintenanceRescanWorkflow);
        ArgumentNullException.ThrowIfNull(folderAutoRenameWorkflow);
        if (workflowProgressSourcesAttached)
        {
            throw new InvalidOperationException("Workflow progress sources are already attached.");
        }

        workflowProgressSourcesAttached = true;
        packageInstallWorkflow.StatusChanged += status => { dropInstallQueueStatus = status ?? new(); RaiseRowsChanged(); };
        maintenanceRescanWorkflow.ProgressChanged += status => { maintenanceProgress = status; RaiseRowsChanged(); };
        folderAutoRenameWorkflow.ProgressChanged += status => { renameProgress = status; RaiseRowsChanged(); };
    }

    /// <summary>プレイリストの生産元別通知を、その画面の排出先へ接続します。</summary>
    internal void AttachPlaylistProgressSources(PlaylistWorkspaceViewModel playlistWorkspace,
        Action<Action> dispatchPresentationAction, Func<bool> shellClosingPredicate)
    {
        ArgumentNullException.ThrowIfNull(playlistWorkspace);
        ArgumentNullException.ThrowIfNull(dispatchPresentationAction);
        ArgumentNullException.ThrowIfNull(shellClosingPredicate);
        if (playlistProgressSourcesAttached)
        {
            throw new InvalidOperationException("Playlist progress sources are already attached.");
        }

        dispatchPlaylistProgressAction = dispatchPresentationAction;
        isShellClosing = shellClosingPredicate;
        playlistProgressSourcesAttached = true;
        playlistWorkspace.PlaylistSyncProgressChanged += PlaylistWorkspacePlaylistSyncProgressChanged;
        playlistWorkspace.PlaylistUrlDownloadStatusChanged += (_, snapshot) => dispatchPlaylistProgressAction(() =>
        {
            if (isShellClosing())
            {
                return;
            }

            playlistUrlDownloadStatus = snapshot ?? PlaylistUrlDownloadStatusSnapshot.Inactive;
            RaiseRowsChanged();
        });
    }

    /// <summary>起動後グループが既存の複合終端まで存続しているかを取得します。</summary>
    public bool IsStartupBackgroundInitializationActive => startupBackgroundOperationToken != 0;

    /// <summary>起動後グループの親ラベルを取得します。</summary>
    public string StartupBackgroundInitializationLabel => BeMusicSeeker.Properties.Resources.Statusbar_progress_startup_additional;

    /// <summary>既存の後続グループ開始境界で親行を表示します。</summary>
    internal void BeginStartupBackgroundInitializationPresentation(long operationToken, long generation)
    {
        startupBackgroundOperationToken = operationToken;
        startupBackgroundOriginGeneration = generation;
        RaiseRowsChanged();
    }

    /// <summary>既存の複合終端で後続グループ親行を消します。</summary>
    internal void CompleteStartupBackgroundInitializationPresentation() { startupBackgroundOperationToken = 0; RaiseRowsChanged(); }

    /// <summary>新しい操作が開始されたとき旧後続グループ親行を消します。</summary>
    internal void ResetStartupBackgroundInitializationPresentation() { startupBackgroundOperationToken = 0; RaiseRowsChanged(); }

    /// <summary>導入先推定キューを、推定詳細と一つの仕事として反映します。</summary>
    internal void UpdatePendingEstimateQueueStatus(PendingInstallEstimateQueueStatusSnapshot snapshot)
    {
        pendingInstallQueueStatus = snapshot?.Clone() ?? new();
        RaiseRowsChanged();
    }

    /// <summary>実行中の導入先推定の詳細を反映します。</summary>
    internal void UpdateInstallEstimationProgress(InstallEstimationProgressSnapshot snapshot)
    {
        installEstimationProgress = snapshot?.Clone() ?? new();
        RaiseRowsChanged();
    }

    /// <summary>LR2専用状態を他の行と同時に反映します。</summary>
    internal void UpdateLr2SongDbSyncStatus(Lr2SongDbSyncRuntimeStatus status)
    {
        latestLr2SongDbSyncStatus = status ?? Lr2SongDbSyncStatusMapper.CreateNone();
        RaiseRowsChanged();
    }

    private void StartupProgressPropertyChanged(object sender, PropertyChangedEventArgs e) => RaiseRowsChanged();

    private void PlaylistWorkspacePlaylistSyncProgressChanged(object sender, PlaylistSyncProgressChangedEventArgs request)
    {
        string key = GetPlaylistKey(request?.Snapshot);
        long version = Interlocked.Increment(ref playlistUiVersionSeed);
        playlistUiVersions[key] = version;
        dispatchPlaylistProgressAction(() =>
        {
            if (!playlistUiVersions.TryGetValue(key, out long currentVersion) || currentVersion != version || isShellClosing())
            {
                return;
            }

            if (request?.Snapshot?.IsActive == true)
            {
                playlistStatuses[key] = request.Snapshot;
            }
            else
            {
                playlistStatuses.Remove(key);
                playlistUiVersions.TryRemove(new KeyValuePair<string, long>(key, version));
            }

            RaiseRowsChanged();
            if (request?.Snapshot?.IsActive != true)
            {
                ((ICollection<KeyValuePair<string, long>>)playlistUiVersions).Remove(new(key, version));
            }
        });
    }

    /// <summary>独立処理を固定の意味順で同時に表示する共通行一覧を取得します。</summary>
    public IReadOnlyList<OperationProgressRow> Rows
    {
        get
        {
            var rows = new List<OperationProgressRow>();
            if (StartupProgress.IsActive)
            {
                string label = StartupProgressWorkflowOwner.FormatStartupProgressCountLabel(StartupProgress.Label,
                    (int)StartupProgress.Value, (int)StartupProgress.Maximum, string.Empty);
                rows.Add(new("startup", label, StartupProgress.SubLabel,
                    StartupProgress.Value, StartupProgress.Maximum));
            }

            rows.AddRange(StartupProgress.DetailRows);
            if (IsStartupBackgroundInitializationActive)
            {
                rows.Add(new("startup_background", StartupBackgroundInitializationLabel, string.Empty, IsIndeterminate: true));
            }

            foreach (StartupBackgroundTaskProgressSnapshot status in backgroundStatuses.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Version))
            {
                if (HasDedicatedPresentation(status, rows))
                {
                    continue;
                }

                rows.Add(new("background:" + status.Name + ":" + status.Version + (status.Request == null ? string.Empty : ":" + GetRequestKey(status.Request)),
                    GetBackgroundTaskLabel(status.Name), string.Empty, IsIndeterminate: true,
                    ParentKey: GetBackgroundParentKey(status)));
            }
            if (playlistUrlDownloadStatus.IsActive)
            {
                PlaylistUrlDownloadStatusSnapshot status = playlistUrlDownloadStatus;
                rows.Add(new("url", string.Format(string.IsNullOrWhiteSpace(status.LabelFormat)
                    ? BeMusicSeeker.Properties.Resources.Playlist_url_download_progress_label_format : status.LabelFormat,
                    Math.Max(0, status.CompletedCount), Math.Max(0, status.TotalCount)), status.CurrentDisplayName,
                    Math.Max(0, status.CompletedCount), Math.Max(1, status.TotalCount), status.TotalCount <= 0,
                    status.CanCancel ? OperationProgressAction.CancelUrlDownload : OperationProgressAction.None));
            }
            if (dropInstallQueueStatus.IsActive)
            {
                DropInstallQueueStatusSnapshot status = dropInstallQueueStatus;
                int workTotal = Math.Max(0, status.CurrentWorkTotal);
                int workIndex = Math.Max(0, status.CurrentWorkIndex);
                bool showWork = status.IsCurrentWorkInProgress && workTotal > 0 && workIndex > 0;
                int total = showWork ? workTotal : Math.Max(0, status.TotalPathCount);
                int completed = showWork ? Math.Min(workIndex, total) : Math.Max(0, status.CompletedPathCount);
                rows.Add(new("install", string.Format(BeMusicSeeker.Properties.Resources.Drop_install_queue_label_format,
                    Math.Max(0, status.CompletedPathCount), Math.Max(0, status.TotalPathCount)),
                    GetDropInstallQueueSubLabel(status), completed, Math.Max(1, total), total <= 0,
                    status.CanCancel ? OperationProgressAction.CancelInstall : OperationProgressAction.None));
            }
            if (installEstimationProgress.IsActive || pendingInstallQueueStatus.IsActive)
            {
                bool detail = installEstimationProgress.IsActive;
                int total = detail ? installEstimationProgress.TotalWorkCount : pendingInstallQueueStatus.CurrentPackageCount;
                int completed = detail ? installEstimationProgress.CompletedWorkCount : pendingInstallQueueStatus.CompletedPackageCount;
                int pending = Math.Max(0, pendingInstallQueueStatus.PendingBatchCount);
                rows.Add(new("estimate", string.Format(BeMusicSeeker.Properties.Resources.Pending_estimate_queue_label_format,
                    Math.Max(0, completed), Math.Max(0, total), pending),
                    detail ? installEstimationProgress.CurrentDisplayName : pendingInstallQueueStatus.CurrentDisplayName,
                    Math.Max(0, completed), Math.Max(1, total), total <= 0));
            }
            foreach (KeyValuePair<string, PlaylistSyncProgressSnapshot> pair in playlistStatuses.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                PlaylistSyncProgressSnapshot status = pair.Value;
                string format = string.IsNullOrWhiteSpace(status.LabelFormat)
                    ? BeMusicSeeker.Properties.Resources.Playlist_sync_progress_label_format : status.LabelFormat;
                string single = string.IsNullOrWhiteSpace(status.SingleLabel)
                    ? BeMusicSeeker.Properties.Resources.Playlist_sync_progress_single_label : status.SingleLabel;
                int completed = Math.Clamp(status.CompletedTableCount, 0, Math.Max(1, status.TotalTableCount));
                rows.Add(new("playlist:" + pair.Key, status.TotalTableCount > 0
                    ? string.Format(format, completed, status.TotalTableCount) : single,
                    !string.IsNullOrWhiteSpace(status.CurrentTableName) ? status.CurrentTableName : status.CurrentUri?.ToString() ?? string.Empty,
                    completed, Math.Max(1, status.TotalTableCount), status.TotalTableCount <= 0,
                    ParentKey: GetRequestParentKey(status.Request)));
            }
            if (maintenanceProgress is { IsCompleted: false } maintenance)
            {
                int total = Math.Max(0, maintenance.TotalCount);
                int processed = Math.Clamp(maintenance.ProcessedCount, 0, Math.Max(1, total));
                rows.Add(new("maintenance", string.Format(BeMusicSeeker.Properties.Resources.Maintenance_rescan_progress_label_format,
                    processed, total), maintenance.CurrentPath ?? string.Empty, processed, Math.Max(1, total), total <= 0,
                    maintenance.IsCanceled ? OperationProgressAction.None : OperationProgressAction.CancelMaintenance));
            }
            if (renameProgress is { IsCompleted: false } rename)
            {
                int total = Math.Max(0, rename.TotalCount);
                int processed = Math.Clamp(rename.ProcessedCount, 0, Math.Max(1, total));
                rows.Add(new("rename", BeMusicSeeker.Properties.Resources.Statusbar_progress_task_folder_rename + " " + processed + "/" + total,
                    rename.CurrentPath ?? string.Empty, processed, Math.Max(1, total), total <= 0));
            }
            if (latestLr2SongDbSyncStatus.HasWarningStatus)
            {
                Lr2SongDbSyncRuntimeStatus status = latestLr2SongDbSyncStatus;
                rows.Add(new("lr2", status.StatusText, status.ProgressText, status.ProgressValue, status.ProgressMaximum,
                    status.Kind == Lr2SongDbSyncStatusKind.Running && !status.HasProgress,
                    status.CanRetry ? OperationProgressAction.RetryLr2 : OperationProgressAction.None, status.Detail,
                    ParentKey: GetLr2ParentKey(),
                    HasGauge: status.HasProgress || status.Kind == Lr2SongDbSyncStatusKind.Running));
            }
            // 分類は保持し、表示されていない親への見かけの所属だけを外します。
            var visibleParents = rows.Where(row => !row.IsChild).Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
            rows = rows.Select(row => row.IsChild && !visibleParents.Contains(row.ParentKey)
                ? row with { ParentKey = string.Empty } : row).ToList();
            // 親と子の所属を先に決め、件数の変化で表示順を入れ替えません。
            rows = rows.OrderBy(GetRowGroup).ThenBy(row => row.IsChild ? 1 : 0)
                .ThenBy(GetChildOrder).ThenBy(row => row.Key, StringComparer.Ordinal).ToList();
            return rows;
        }
    }

    /// <summary>共通行の有無に応じてステータスバー全体を表示します。</summary>
    public bool HasRows => Rows.Count != 0;

    private static string GetPlaylistKey(PlaylistSyncProgressSnapshot snapshot) =>
        (snapshot?.Source ?? "playlist") + ":" + (snapshot?.OperationId ?? 0) + (snapshot?.Request == null ? string.Empty : ":" + GetRequestKey(snapshot.Request));

    private static string GetRequestKey(OperationProgressRequest request) => request == null ? string.Empty
        : request.Generation + ":" + request.OperationToken + ":" + request.Source + ":" + request.Version;

    /// <summary>操作開始時に旧世代の表示通知を排除します。</summary>
    internal void BeginBackgroundProgressGeneration(long generation)
    {
        backgroundGeneration = generation;
        backgroundStatuses.Clear();
        RaiseRowsChanged();
    }

    /// <summary>スケジューラーの実行境界を表示へ反映し、旧世代を拒否します。</summary>
    internal void UpdateBackgroundTaskProgress(StartupBackgroundTaskProgressSnapshot status)
    {
        if (status.Generation != backgroundGeneration)
        {
            return;
        }

        string key = status.Name + ":" + status.Version + (status.Request == null ? string.Empty : ":" + GetRequestKey(status.Request));
        if (status.IsRunning)
        {
            backgroundStatuses[key] = status;
        }
        else
        {
            backgroundStatuses.Remove(key);
        }

        RaiseRowsChanged();
    }

    private bool HasDedicatedPresentation(StartupBackgroundTaskProgressSnapshot status, IReadOnlyList<OperationProgressRow> rows)
    {
        if (status.Name == "lr2_song_db_sync")
        {
            return latestLr2SongDbSyncStatus.Kind == Lr2SongDbSyncStatusKind.Running;
        }
        if (playlistStatuses.Values.Any(value => value.Request != null && value.Request == status.Request))
        {
            return true;
        }
        string detailName = status.Name == "chart_info_backfill_after_hydration" ? "chart_info_backfill" : status.Name;
        return StartupProgress.IsExecutionProgressPartOfStartup(status.Request)
            && rows.Any(row => row.Key == "task:" + detailName);
    }

    private static string GetBackgroundTaskLabel(string name) => name switch
    {
        "score_hydration_deferred" => BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_score_hydration,
        "ranking_refresh" or "ranking_refresh_deferred" => BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_ranking_refresh,
        "chart_info_backfill" or "chart_info_backfill_after_hydration" => BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_chart_info,
        "chart_info_hydration" => BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_chart_info_load,
        "playlist_entries_hydration" => BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_playlist_loading,
        "maintenance_hydration" => BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_maintenance,
        "installable_maintenance" => BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_installable_maintenance,
        "lr2_song_db_sync" => BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_lr2_song_db_sync,
        "lr2_song_db_sync_enrollment" => BeMusicSeeker.Properties.Resources.Statusbar_progress_task_lr2_sync_enrollment,
        "playlist_ref_apply" => BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_playlist_ref,
        "external_playlist_sync" => BeMusicSeeker.Properties.Resources.Statusbar_progress_task_external_playlist_sync,
        "external_table_catalog" => BeMusicSeeker.Properties.Resources.Statusbar_progress_task_external_table_catalog,
        "playlist_library_index_prewarm" => BeMusicSeeker.Properties.Resources.Statusbar_progress_task_playlist_index,
        "playlist_virtual_order_prewarm" => BeMusicSeeker.Properties.Resources.Statusbar_progress_task_chart_list_preparation,
        "playlist_url_completion" => BeMusicSeeker.Properties.Resources.Statusbar_progress_task_playlist_url_completion,
        "library_folder_tree_refresh" => BeMusicSeeker.Properties.Resources.Statusbar_progress_task_folder_tree,
        "playlist_custom_folder_output_repair" => BeMusicSeeker.Properties.Resources.Statusbar_progress_task_custom_folder_repair,
        "post_initialize_gc" => BeMusicSeeker.Properties.Resources.Statusbar_progress_task_gc,
        _ when name.StartsWith("beatoraja_bmt_", StringComparison.Ordinal) => BeMusicSeeker.Properties.Resources.Statusbar_progress_task_bmt_output,
        _ => BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_background
    };


    private static int GetRowGroup(OperationProgressRow row)
    {
        string key = row.IsChild ? row.ParentKey : row.Key;
        if (key == "startup")
        {
            return 0;
        }

        if (key == "startup_background")
        {
            return 1;
        }

        if (key == "url")
        {
            return 2;
        }

        if (key == "install")
        {
            return 3;
        }

        if (key == "estimate")
        {
            return 4;
        }

        if (key.StartsWith("playlist:", StringComparison.Ordinal))
        {
            return 5;
        }

        if (key == "maintenance")
        {
            return 6;
        }

        if (key == "rename")
        {
            return 7;
        }

        return 8;
    }

    private static int GetChildOrder(OperationProgressRow row)
    {
        if (!row.IsChild)
        {
            return 0;
        }
        string[] parts = row.Key.Split(':');
        string name = parts.Length > 1 ? parts[1] : row.Key;
        if (parts[0] == "library")
        {
            return name switch { "DatabaseLoad" => 10, "FileEnumeration" => 20, "FileDiff" => 30, "Lr2FolderFileCheck" => 40, _ => 50 };
        }
        // 専用通知へ切り替わっても、同じ仕事の意味順はスケジューラー行と一致させます。
        return name switch
        {
            "score_hydration_deferred" => 100,
            "ranking_refresh_deferred" or "ranking_refresh" => 110,
            "chart_info_hydration" => 120,
            "chart_info_backfill" or "chart_info_backfill_after_hydration" => 130,
            "playlist_entries_hydration" => 140,
            "playlist_ref_apply" => 150,
            "external_playlist_sync" or "playlist" => 160,
            "external_table_catalog" => 170,
            "playlist_url_completion" => 180,
            "library_folder_tree_refresh" => 190,
            "maintenance_hydration" => 200,
            "installable_maintenance" => 210,
            "playlist_custom_folder_output_repair" or "custom_folder_repair" => 220,
            "bmt" => 230,
            _ when name.StartsWith("beatoraja_bmt_", StringComparison.Ordinal) => 230,
            "lr2_song_db_sync_enrollment" => 235,
            "lr2_song_db_sync" or "lr2" => 240,
            "playlist_library_index_prewarm" => 250,
            "playlist_virtual_order_prewarm" => 260,
            "post_initialize_gc" => 270,
            _ => 300
        };
    }

    private string GetLr2ParentKey()
    {
        if (latestLr2SongDbSyncStatus.Kind != Lr2SongDbSyncStatusKind.Running)
        {
            return string.Empty;
        }
        // LR2の既存単一実行中だけ対応通知を持ちます。終端後の警告や再試行には所属を引き継ぎません。
        return backgroundStatuses.Values.Where(status => status.Name == "lr2_song_db_sync")
            .Select(GetBackgroundParentKey).FirstOrDefault(parent => parent == "startup_background") ?? string.Empty;
    }

    private string GetBackgroundParentKey(StartupBackgroundTaskProgressSnapshot status) => GetRequestParentKey(status?.Request);

    private string GetRequestParentKey(OperationProgressRequest request)
    {
        if (request == null || request.OperationToken == 0)
        {
            return string.Empty;
        }
        if (StartupProgress.IsExecutionProgressPartOfStartup(request))
        {
            return "startup";
        }
        return IsStartupBackgroundInitializationActive
            && request.OperationToken == startupBackgroundOperationToken
            && request.Generation == startupBackgroundOriginGeneration
            && StartupBackgroundTaskSchedulerOwner.IsPostInitializationTask(
                request.Source.StartsWith("scheduler:", StringComparison.Ordinal) ? request.Source[10..] : request.Source)
            && request.Source is not "ranking_refresh_deferred" and not "score_hydration_deferred"
            ? "startup_background" : string.Empty;
    }

    private void RaiseRowsChanged()
    {
        // 表示購読先の失敗が受付済みの仕事や起動後グループの終結を変えないよう隔離します。
        try { RaisePropertyChanged(nameof(Rows)); } catch { }
        try { RaisePropertyChanged(nameof(HasRows)); } catch { }
        try { RaisePropertyChanged(nameof(IsStartupBackgroundInitializationActive)); } catch { }
    }

    private static string GetDropInstallQueueSubLabel(DropInstallQueueStatusSnapshot snapshot)
    {
        if (snapshot.IsCurrentWorkInProgress && snapshot.CurrentWorkIndex > 0 && snapshot.CurrentWorkTotal > 0)
        {
            return string.Format(BeMusicSeeker.Properties.Resources.Drop_install_queue_extracting_sub_label_format,
                Math.Max(0, snapshot.CurrentWorkIndex), Math.Max(0, snapshot.CurrentWorkTotal), snapshot.CurrentWorkDisplayName ?? string.Empty);
        }

        return snapshot.CurrentDisplayName ?? string.Empty;
    }
}
