using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlaylistUrlDownloadStatusSnapshot : EventArgs
{
    internal static PlaylistUrlDownloadStatusSnapshot Inactive { get; } = new(false, 0, 0, string.Empty, false, string.Empty);

    internal PlaylistUrlDownloadStatusSnapshot(
        bool isActive,
        int totalCount,
        int completedCount,
        string currentDisplayName,
        bool canCancel,
        string labelFormat)
    {
        IsActive = isActive;
        TotalCount = isActive ? Math.Max(0, totalCount) : 0;
        CompletedCount = isActive ? Math.Max(0, completedCount) : 0;
        CurrentDisplayName = isActive ? currentDisplayName ?? string.Empty : string.Empty;
        CanCancel = isActive && canCancel;
        LabelFormat = isActive ? labelFormat ?? string.Empty : string.Empty;
    }

    internal bool IsActive { get; }

    internal int TotalCount { get; }

    internal int CompletedCount { get; }

    internal string CurrentDisplayName { get; }

    internal bool CanCancel { get; }

    internal string LabelFormat { get; }
}

internal sealed class PlaylistUrlContextMenuAvailability
{
    internal PlaylistUrlContextMenuAvailability(
        bool isPlaylistContext,
        bool isBulkContext,
        bool canOpenUrl,
        bool canOpenDiffUrl,
        bool canFindExternalPackage)
    {
        IsPlaylistContext = isPlaylistContext;
        IsBulkContext = isBulkContext;
        CanOpenUrl = canOpenUrl;
        CanOpenDiffUrl = canOpenDiffUrl;
        CanFindExternalPackage = canFindExternalPackage;
    }

    internal bool IsPlaylistContext { get; }

    internal bool IsBulkContext { get; }

    internal bool CanOpenUrl { get; }

    internal bool CanOpenDiffUrl { get; }

    internal bool CanFindExternalPackage { get; }
}

public sealed partial class PlaylistWorkspaceViewModel
{
    private const int PlaylistUrlDownloadLargeSelectionWarningThreshold = 50;

    private enum PlaylistUrlAcquisitionConfirmationKind
    {
        SelectedUrls,
        ExternalPackages
    }

    private enum PlaylistUrlAcquisitionNotificationKind
    {
        SelectedUrlsNoTargets,
        SelectedUrlsBlockedByInstallQueue,
        ExternalPackagesNoTargets,
        ExternalPackagesBlockedByInstallQueue
    }

    private readonly object playlistUrlAcquisitionSync = new();

    private readonly SemaphoreSlim playlistUrlAcquisitionCommandGate = new(1, 1);

    private readonly Func<Action, Task> playlistUrlAcquisitionPresentationScheduler;

    private readonly Func<bool> playlistUrlInstallQueueActiveProvider;

    private readonly Action<IReadOnlyList<string>> playlistUrlInstallSink;

    private readonly Action<Uri> playlistUrlBrowserOpenSink;

    internal event Action PlaylistUrlInstallTreeExpansionRequested;

    private int playlistUrlAcquisitionRunning;

    private CancellationTokenSource playlistUrlAcquisitionCancellation;

    private int playlistUrlAcquisitionTotalCount;

    private int playlistUrlAcquisitionCompletedCount;

    private string playlistUrlAcquisitionCurrentDisplayName = string.Empty;

    private string playlistUrlAcquisitionLabelFormat = string.Empty;

    internal event EventHandler<PlaylistUrlDownloadStatusSnapshot> PlaylistUrlDownloadStatusChanged;

    internal bool IsPlaylistUrlDownloadRunning
    {
        get
        {
            lock (playlistUrlAcquisitionSync)
            {
                return playlistUrlAcquisitionRunning != 0;
            }
        }
    }

    internal Task RunPlaylistUrlActionAsync(IEnumerable<object> rows, bool isDiffUrl)
    {
        List<object> rowSnapshot = [.. (rows ?? []).Where(row => row != null)];
        if (rowSnapshot.Count <= 1)
        {
            Uri url = rowSnapshot.Count == 0
                ? null
                : isDiffUrl
                    ? GridRowResolver.GetUrlDiff(rowSnapshot[0])
                    : GridRowResolver.GetUrl(rowSnapshot[0]);
            return RunSinglePlaylistUrlAsync(url);
        }
        return RunPlaylistUrlBatchAsync(
            PlaylistContextMenuTargetResolver.BuildPlaylistUrlTargets(rowSnapshot, isDiffUrl),
            isDiffUrl);
    }

    internal Task RunPlaylistExternalPackageLookupAsync(IEnumerable<object> rows)
    {
        List<object> rowSnapshot = [.. (rows ?? []).Where(row => row != null)];
        return RunExternalPackageLookupAsync(
            PlaylistContextMenuTargetResolver.BuildPlaylistExternalPackageMd5Targets(rowSnapshot));
    }

    internal PlaylistUrlContextMenuAvailability CapturePlaylistUrlContextMenuAvailability(
        object contextRow,
        IEnumerable<object> effectiveRows)
    {
        bool isPlaylistContext = GridRowResolver.IsPlaylistRow(contextRow);
        if (!isPlaylistContext)
        {
            return new PlaylistUrlContextMenuAvailability(
                isPlaylistContext: false,
                isBulkContext: false,
                canOpenUrl: false,
                canOpenDiffUrl: false,
                canFindExternalPackage: false);
        }

        List<object> rowSnapshot = [.. (effectiveRows ?? []).Where(row => row != null)];
        bool isBulkContext = rowSnapshot.Count > 1;
        bool isDownloadRunning = IsPlaylistUrlDownloadRunning;
        bool canOpenUrl = isBulkContext
            ? !isDownloadRunning
                && PlaylistContextMenuTargetResolver.BuildPlaylistUrlTargets(rowSnapshot, isDiffUrl: false).Count > 0
            : !isDownloadRunning
                && GridRowResolver.GetUrl(contextRow) is Uri url
                && url.IsAbsoluteUri;
        bool canOpenDiffUrl = isBulkContext
            ? !isDownloadRunning
                && PlaylistContextMenuTargetResolver.BuildPlaylistUrlTargets(rowSnapshot, isDiffUrl: true).Count > 0
            : !isDownloadRunning
                && GridRowResolver.GetUrlDiff(contextRow) is Uri diffUrl
                && diffUrl.IsAbsoluteUri;
        bool canFindExternalPackage = !isDownloadRunning
            && !playlistUrlInstallQueueActiveProvider()
            && PlaylistContextMenuTargetResolver.BuildPlaylistExternalPackageMd5Targets(rowSnapshot).Count > 0;

        return new PlaylistUrlContextMenuAvailability(
            isPlaylistContext,
            isBulkContext,
            canOpenUrl,
            canOpenDiffUrl,
            canFindExternalPackage);
    }

    internal async Task RunSinglePlaylistUrlAsync(Uri url)
    {
        if (url == null || !url.IsAbsoluteUri || IsPlaylistUrlDownloadRunning)
        {
            return;
        }

        if (!playlistUrlAcquisitionCommandGate.Wait(0))
        {
            return;
        }
        try
        {
            await RunSinglePlaylistUrlCoreAsync(url);
        }
        finally
        {
            playlistUrlAcquisitionCommandGate.Release();
        }
    }

    private async Task RunSinglePlaylistUrlCoreAsync(Uri url)
    {

        if (GetPlaylistUrlAcquisitionOptions().ShouldAutoInstall)
        {
            PlaylistUrlDownloadResult result = await DownloadSinglePlaylistUrlCandidateWithStatusAsync(url).ConfigureAwait(false);
            if (result.Kind == PlaylistUrlDownloadResultKind.Downloaded
                && await QueuePlaylistUrlInstallPathsAsync([result.FilePath]).ConfigureAwait(true))
            {
                return;
            }
            if (result.Kind == PlaylistUrlDownloadResultKind.BlockedBySizeLimit)
            {
                return;
            }
        }

        if (!url.IsAbsoluteUri)
        {
            return;
        }
        await playlistUrlAcquisitionPresentationScheduler(
            () => playlistUrlBrowserOpenSink(url)).ConfigureAwait(true);
    }

    internal async Task RunPlaylistUrlBatchAsync(IEnumerable<Uri> urls, bool isDiffUrl)
    {
        if (!playlistUrlAcquisitionCommandGate.Wait(0))
        {
            return;
        }
        try
        {
            await RunPlaylistUrlBatchCoreAsync(urls, isDiffUrl);
        }
        finally
        {
            playlistUrlAcquisitionCommandGate.Release();
        }
    }

    private async Task RunPlaylistUrlBatchCoreAsync(IEnumerable<Uri> urls, bool isDiffUrl)
    {
        if (IsPlaylistUrlDownloadRunning)
        {
            return;
        }
        List<Uri> targets = [.. (urls ?? []).Where(url => url != null && url.IsAbsoluteUri)];
        if (targets.Count == 0)
        {
            await ShowPlaylistUrlAcquisitionNotificationAsync(
                PlaylistUrlAcquisitionNotificationKind.SelectedUrlsNoTargets);
            return;
        }
        if (IsPlaylistUrlInstallQueueActive())
        {
            await ShowPlaylistUrlAcquisitionNotificationAsync(
                PlaylistUrlAcquisitionNotificationKind.SelectedUrlsBlockedByInstallQueue);
            return;
        }
        if (targets.Count >= PlaylistUrlDownloadLargeSelectionWarningThreshold)
        {
            playlistUrlAcquisitionWorkflow.LogDownload(
                "playlist_url_download large_selection_warning count="
                    + targets.Count
                    + " threshold="
                    + PlaylistUrlDownloadLargeSelectionWarningThreshold);
        }
        if (!await ConfirmPlaylistUrlAcquisitionAsync(
            targets.Count,
            isDiffUrl,
            PlaylistUrlAcquisitionConfirmationKind.SelectedUrls))
        {
            return;
        }

        var downloadedPaths = new List<string>();
        var downloadedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int browserFallbackCount = 0;
        int blockedBySizeLimitCount = 0;
        int duplicateCount = 0;
        int failedCount = 0;
        int canceledCount = 0;
        CancellationTokenSource cancellation = BeginPlaylistUrlAcquisition(targets.Count, string.Empty, string.Empty);
        if (cancellation == null)
        {
            return;
        }
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (cancellation.IsCancellationRequested)
                {
                    canceledCount = targets.Count - i;
                    break;
                }
                Uri target = targets[i];
                UpdatePlaylistUrlDownloadStatus(
                    true,
                    targets.Count,
                    i,
                    target.ToString(),
                    canCancel: true,
                    labelFormat: null);
                PlaylistUrlDownloadResult result;
                try
                {
                    result = await playlistUrlAcquisitionWorkflow.DownloadCandidateAsync(
                        target,
                        downloadedKeys,
                        cancellationToken: cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    canceledCount = targets.Count - i;
                    break;
                }
                switch (result.Kind)
                {
                    case PlaylistUrlDownloadResultKind.Downloaded when playlistUrlAcquisitionWorkflow.IsStagedFileReady(result.FilePath):
                        downloadedPaths.Add(result.FilePath);
                        break;
                    case PlaylistUrlDownloadResultKind.BlockedBySizeLimit:
                        blockedBySizeLimitCount++;
                        break;
                    case PlaylistUrlDownloadResultKind.Duplicate:
                        duplicateCount++;
                        break;
                    case PlaylistUrlDownloadResultKind.Failed:
                        failedCount++;
                        break;
                    default:
                        browserFallbackCount++;
                        break;
                }
                UpdatePlaylistUrlDownloadStatus(true, targets.Count, i + 1, target.ToString(), !cancellation.IsCancellationRequested, null);
                if (cancellation.IsCancellationRequested)
                {
                    canceledCount = targets.Count - i - 1;
                    break;
                }
            }
        }
        finally
        {
            EndPlaylistUrlAcquisition(cancellation);
        }

        await QueuePlaylistUrlInstallPathsAsync(downloadedPaths).ConfigureAwait(true);
        await ShowPlaylistUrlAcquisitionSummaryAsync(
            externalPackageLookup: false,
            targets.Count,
            downloadedPaths.Count,
            browserFallbackCount,
            blockedBySizeLimitCount,
            duplicateCount,
            failedCount,
            noCandidateCount: 0,
            duplicateDownloadedUrlCount: 0,
            duplicateFailedUrlCount: 0,
            unsupportedCount: 0,
            canceledCount);
    }

    private async Task RunExternalPackageLookupAsync(IEnumerable<string> chartMd5Targets)
    {
        if (!playlistUrlAcquisitionCommandGate.Wait(0))
        {
            return;
        }
        try
        {
            await RunExternalPackageLookupCoreAsync(chartMd5Targets);
        }
        finally
        {
            playlistUrlAcquisitionCommandGate.Release();
        }
    }

    private async Task RunExternalPackageLookupCoreAsync(IEnumerable<string> chartMd5Targets)
    {
        if (IsPlaylistUrlDownloadRunning)
        {
            return;
        }
        List<string> targets = [.. (chartMd5Targets ?? []).Where(target => !string.IsNullOrWhiteSpace(target))];
        if (targets.Count == 0)
        {
            await ShowPlaylistUrlAcquisitionNotificationAsync(
                PlaylistUrlAcquisitionNotificationKind.ExternalPackagesNoTargets);
            return;
        }
        if (IsPlaylistUrlInstallQueueActive())
        {
            await ShowPlaylistUrlAcquisitionNotificationAsync(
                PlaylistUrlAcquisitionNotificationKind.ExternalPackagesBlockedByInstallQueue);
            return;
        }
        if (!await ConfirmPlaylistUrlAcquisitionAsync(
            targets.Count,
            isDiffUrl: false,
            PlaylistUrlAcquisitionConfirmationKind.ExternalPackages))
        {
            return;
        }

        var downloadedPaths = new List<string>();
        var downloadedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedDownloadKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int noCandidateCount = 0;
        int duplicateDownloadedUrlCount = 0;
        int duplicateFailedUrlCount = 0;
        int blockedBySizeLimitCount = 0;
        int unsupportedCount = 0;
        int failedCount = 0;
        int canceledCount = 0;
        CancellationTokenSource cancellation = BeginPlaylistUrlAcquisition(
            targets.Count,
            string.Empty,
            BeMusicSeeker.Properties.Resources.Playlist_external_package_lookup_progress_label_format);
        if (cancellation == null)
        {
            return;
        }
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (cancellation.IsCancellationRequested)
                {
                    canceledCount = targets.Count - i;
                    break;
                }
                string targetMd5 = targets[i];
                UpdatePlaylistUrlDownloadStatus(
                    true,
                    targets.Count,
                    i,
                    targetMd5,
                    canCancel: true,
                    labelFormat: BeMusicSeeker.Properties.Resources.Playlist_external_package_lookup_progress_label_format);
                PlaylistExternalPackageWorkflowResult result;
                try
                {
                    result = await playlistExternalPackageLookupService.DownloadFirstAvailablePackageAsync(
                        targetMd5,
                        (lookupResult, token) => DownloadExternalPackageLookupCandidateAsync(lookupResult, downloadedKeys, token),
                        downloadedKeys,
                        failedDownloadKeys,
                        PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey,
                        playlistUrlAcquisitionWorkflow.LogDownload,
                        cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    canceledCount = targets.Count - i;
                    break;
                }
                if (cancellation.IsCancellationRequested)
                {
                    canceledCount = targets.Count - i;
                    break;
                }
                switch (result.Kind)
                {
                    case PlaylistExternalPackageWorkflowResultKind.Downloaded when playlistUrlAcquisitionWorkflow.IsStagedFileReady(result.FilePath):
                        downloadedPaths.Add(result.FilePath);
                        break;
                    case PlaylistExternalPackageWorkflowResultKind.NoCandidate:
                        noCandidateCount++;
                        break;
                    case PlaylistExternalPackageWorkflowResultKind.DuplicateDownloadedUrl:
                        duplicateDownloadedUrlCount++;
                        break;
                    case PlaylistExternalPackageWorkflowResultKind.DuplicateFailedUrl:
                        duplicateFailedUrlCount++;
                        break;
                    case PlaylistExternalPackageWorkflowResultKind.BlockedBySizeLimit:
                        blockedBySizeLimitCount++;
                        break;
                    case PlaylistExternalPackageWorkflowResultKind.Unsupported:
                        unsupportedCount++;
                        break;
                    default:
                        failedCount++;
                        break;
                }
                UpdatePlaylistUrlDownloadStatus(
                    true,
                    targets.Count,
                    i + 1,
                    targetMd5,
                    !cancellation.IsCancellationRequested,
                    BeMusicSeeker.Properties.Resources.Playlist_external_package_lookup_progress_label_format);
                if (cancellation.IsCancellationRequested)
                {
                    canceledCount = targets.Count - i - 1;
                    break;
                }
            }
        }
        finally
        {
            EndPlaylistUrlAcquisition(cancellation);
        }

        await QueuePlaylistUrlInstallPathsAsync(downloadedPaths).ConfigureAwait(true);
        await ShowPlaylistUrlAcquisitionSummaryAsync(
            externalPackageLookup: true,
            targets.Count,
            downloadedPaths.Count,
            browserFallbackCount: 0,
            blockedBySizeLimitCount,
            duplicateCount: 0,
            failedCount,
            noCandidateCount,
            duplicateDownloadedUrlCount,
            duplicateFailedUrlCount,
            unsupportedCount,
            canceledCount);
    }

    internal void CancelPlaylistUrlDownload()
    {
        lock (playlistUrlAcquisitionSync)
        {
            CancellationTokenSource cancellation = playlistUrlAcquisitionCancellation;
            if (playlistUrlAcquisitionRunning == 0
                || cancellation == null
                || cancellation.IsCancellationRequested)
            {
                return;
            }
            cancellation.Cancel();
            playlistUrlAcquisitionWorkflow.LogDownload(
                "playlist_url_download cancel_requested completed="
                    + playlistUrlAcquisitionCompletedCount
                    + " total="
                    + playlistUrlAcquisitionTotalCount
                    + " current="
                    + PlaylistUrlAcquisitionWorkflow.SanitizeDownloadLogValue(playlistUrlAcquisitionCurrentDisplayName));
            UpdatePlaylistUrlDownloadStatus(
                true,
                playlistUrlAcquisitionTotalCount,
                playlistUrlAcquisitionCompletedCount,
                playlistUrlAcquisitionCurrentDisplayName,
                canCancel: false,
                labelFormat: playlistUrlAcquisitionLabelFormat);
        }
    }

    private async Task<PlaylistUrlDownloadResult> DownloadSinglePlaylistUrlCandidateWithStatusAsync(Uri url)
    {
        string displayName = url?.ToString() ?? string.Empty;
        CancellationTokenSource cancellation = BeginPlaylistUrlAcquisition(1, displayName, string.Empty);
        if (cancellation == null)
        {
            return PlaylistUrlDownloadResult.Failed();
        }
        UpdatePlaylistUrlDownloadStatus(true, 1, 0, displayName, canCancel: false, labelFormat: null);
        try
        {
            PlaylistUrlDownloadResult result = await playlistUrlAcquisitionWorkflow.DownloadCandidateAsync(url, cancellationToken: cancellation.Token).ConfigureAwait(false);
            UpdatePlaylistUrlDownloadStatus(true, 1, 1, displayName, canCancel: false, labelFormat: null);
            return result;
        }
        finally
        {
            EndPlaylistUrlAcquisition(cancellation);
        }
    }

    private async Task<PlaylistExternalPackageDownloadAttempt> DownloadExternalPackageLookupCandidateAsync(
        PlaylistExternalPackageLookupResult lookupResult,
        HashSet<string> downloadedKeys,
        CancellationToken cancellationToken)
    {
        if (lookupResult == null)
        {
            return PlaylistExternalPackageDownloadAttempt.Failed();
        }
        PlaylistUrlDownloadResult result = await playlistUrlAcquisitionWorkflow.DownloadCandidateAsync(
            lookupResult.DownloadUri,
            downloadedKeys,
            allowSharedPageResolution: false,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        playlistUrlAcquisitionWorkflow.LogDownload(
            "playlist_external_package_lookup download_result provider="
                + lookupResult.ProviderId
                + " md5="
                + lookupResult.ChartMd5
                + " kind="
                + result.Kind
                + " url="
                + lookupResult.DownloadUri);
        return result.Kind switch
        {
            PlaylistUrlDownloadResultKind.Downloaded => PlaylistExternalPackageDownloadAttempt.Downloaded(result.FilePath, result.DownloadKey),
            PlaylistUrlDownloadResultKind.Duplicate => PlaylistExternalPackageDownloadAttempt.DuplicateDownloadedUrl(result.DownloadKey),
            PlaylistUrlDownloadResultKind.BlockedBySizeLimit => PlaylistExternalPackageDownloadAttempt.BlockedBySizeLimit(result.DownloadKey),
            PlaylistUrlDownloadResultKind.BrowserFallback => PlaylistExternalPackageDownloadAttempt.Unsupported(result.DownloadKey),
            _ => PlaylistExternalPackageDownloadAttempt.Failed(result.DownloadKey)
        };
    }

    private CancellationTokenSource BeginPlaylistUrlAcquisition(int totalCount, string currentDisplayName, string labelFormat)
    {
        var cancellation = new CancellationTokenSource();
        lock (playlistUrlAcquisitionSync)
        {
            if (playlistUrlAcquisitionRunning != 0)
            {
                cancellation.Dispose();
                return null;
            }
            playlistUrlAcquisitionTotalCount = Math.Max(0, totalCount);
            playlistUrlAcquisitionCompletedCount = 0;
            playlistUrlAcquisitionCurrentDisplayName = currentDisplayName ?? string.Empty;
            playlistUrlAcquisitionLabelFormat = labelFormat ?? string.Empty;
            playlistUrlAcquisitionCancellation = cancellation;
            playlistUrlAcquisitionRunning = 1;
            return cancellation;
        }
    }

    private void EndPlaylistUrlAcquisition(CancellationTokenSource cancellation)
    {
        bool shouldDispose = false;
        lock (playlistUrlAcquisitionSync)
        {
            if (!ReferenceEquals(playlistUrlAcquisitionCancellation, cancellation))
            {
                return;
            }
            playlistUrlAcquisitionCancellation = null;
            playlistUrlAcquisitionTotalCount = 0;
            playlistUrlAcquisitionCompletedCount = 0;
            playlistUrlAcquisitionCurrentDisplayName = string.Empty;
            playlistUrlAcquisitionLabelFormat = string.Empty;
            playlistUrlAcquisitionRunning = 0;
            UpdatePlaylistUrlDownloadStatus(false, 0, 0, string.Empty, canCancel: false, labelFormat: null);
            shouldDispose = true;
        }
        if (shouldDispose)
        {
            cancellation.Dispose();
        }
    }

    private void UpdatePlaylistUrlDownloadStatus(
        bool isActive,
        int totalCount,
        int completedCount,
        string currentDisplayName,
        bool canCancel,
        string labelFormat)
    {
        lock (playlistUrlAcquisitionSync)
        {
            if (isActive)
            {
                playlistUrlAcquisitionTotalCount = Math.Max(0, totalCount);
                playlistUrlAcquisitionCompletedCount = Math.Max(0, completedCount);
                playlistUrlAcquisitionCurrentDisplayName = currentDisplayName ?? string.Empty;
                playlistUrlAcquisitionLabelFormat = labelFormat ?? string.Empty;
            }
            else
            {
                playlistUrlAcquisitionTotalCount = 0;
                playlistUrlAcquisitionCompletedCount = 0;
                playlistUrlAcquisitionCurrentDisplayName = string.Empty;
                playlistUrlAcquisitionLabelFormat = string.Empty;
            }
            PlaylistUrlDownloadStatusChanged?.Invoke(
                this,
                new PlaylistUrlDownloadStatusSnapshot(
                    isActive,
                    totalCount,
                    completedCount,
                    currentDisplayName,
                    canCancel,
                    labelFormat));
        }
    }

    private bool IsPlaylistUrlInstallQueueActive()
    {
        return playlistUrlInstallQueueActiveProvider();
    }

    private PlaylistUrlAcquisitionOptionsSnapshot GetPlaylistUrlAcquisitionOptions()
    {
        return playlistUrlAcquisitionOptionsProvider();
    }

    private async Task<bool> ConfirmPlaylistUrlAcquisitionAsync(
        int targetCount,
        bool isDiffUrl,
        PlaylistUrlAcquisitionConfirmationKind kind)
    {
        string message;
        string warningMessage = null;
        if (kind == PlaylistUrlAcquisitionConfirmationKind.ExternalPackages)
        {
            message = string.Format(
                BeMusicSeeker.Properties.Resources.Confirm_SelectedPlaylistExternalPackageLookup,
                targetCount);
            warningMessage = BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistExternalPackageLookup;
        }
        else
        {
            string urlKind = isDiffUrl
                ? BeMusicSeeker.Properties.Resources.Diff_URL
                : BeMusicSeeker.Properties.Resources.Original_URL;
            message = string.Format(
                BeMusicSeeker.Properties.Resources.Confirm_SelectedPlaylistUrlDownload,
                targetCount,
                urlKind);
            if (targetCount >= PlaylistUrlDownloadLargeSelectionWarningThreshold)
            {
                warningMessage = string.Format(
                    BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistUrlDownloadLargeSelection,
                    PlaylistUrlDownloadLargeSelectionWarningThreshold);
            }
        }

        UiDialogResult result = await playlistWorkspaceDialogService.ConfirmAsync(
            UiConfirmationRequest.CreateDefault(
                message,
                BeMusicSeeker.Properties.Resources.Confirm,
                warningMessage))
            .ConfigureAwait(true);
        return ToPlaylistUrlConfirmationDecision(
            result,
            kind == PlaylistUrlAcquisitionConfirmationKind.ExternalPackages
                ? "Playlist URL external package lookup confirmation"
                : "Playlist URL download confirmation");
    }

    private async Task ShowPlaylistUrlAcquisitionNotificationAsync(
        PlaylistUrlAcquisitionNotificationKind kind)
    {
        string message = kind switch
        {
            PlaylistUrlAcquisitionNotificationKind.SelectedUrlsNoTargets
                => BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistUrlDownloadNoTargets,
            PlaylistUrlAcquisitionNotificationKind.SelectedUrlsBlockedByInstallQueue
                => BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistUrlDownloadBlockedByInstallQueue,
            PlaylistUrlAcquisitionNotificationKind.ExternalPackagesNoTargets
                => BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistExternalPackageLookupNoTargets,
            PlaylistUrlAcquisitionNotificationKind.ExternalPackagesBlockedByInstallQueue
                => BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistExternalPackageLookupBlockedByInstallQueue,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        UiDialogResult result = await playlistWorkspaceDialogService.ShowMessageAsync(
            UiMessageRequest.CreateWarning(
                message,
                BeMusicSeeker.Properties.Resources.Warning))
            .ConfigureAwait(true);
        ThrowIfPlaylistUrlDialogNotShown(result, "Playlist URL acquisition notification");
    }

    private async Task ShowPlaylistUrlAcquisitionSummaryAsync(
        bool externalPackageLookup,
        int targetCount,
        int downloadedCount,
        int browserFallbackCount,
        int blockedBySizeLimitCount,
        int duplicateCount,
        int failedCount,
        int noCandidateCount,
        int duplicateDownloadedUrlCount,
        int duplicateFailedUrlCount,
        int unsupportedCount,
        int canceledCount)
    {
        string message = externalPackageLookup
            ? string.Format(
                BeMusicSeeker.Properties.Resources.Msg_SelectedPlaylistExternalPackageLookupResult,
                targetCount,
                downloadedCount,
                noCandidateCount,
                duplicateDownloadedUrlCount,
                duplicateFailedUrlCount,
                blockedBySizeLimitCount,
                unsupportedCount,
                failedCount,
                canceledCount)
            : string.Format(
                BeMusicSeeker.Properties.Resources.Msg_SelectedPlaylistUrlDownloadResult,
                targetCount,
                downloadedCount,
                browserFallbackCount,
                blockedBySizeLimitCount,
                duplicateCount,
                failedCount,
                canceledCount);
        UiDialogResult result = await playlistWorkspaceDialogService.ShowMessageAsync(
            UiMessageRequest.CreateInformation(
                message,
                BeMusicSeeker.Properties.Resources.Information,
                downloadedCount > 0))
            .ConfigureAwait(true);
        ThrowIfPlaylistUrlDialogNotShown(result, "Playlist URL acquisition summary");
    }

    private static bool ToPlaylistUrlConfirmationDecision(UiDialogResult result, string routeName)
    {
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " returned no dialog result.");
        }
        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.IsPositive,
            _ => throw CreatePlaylistUrlDialogDisplayException(routeName, result)
        };
    }

    private static void ThrowIfPlaylistUrlDialogNotShown(UiDialogResult result, string routeName)
    {
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " returned no dialog result.");
        }
        if (result.Status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return;
        }
        throw CreatePlaylistUrlDialogDisplayException(routeName, result);
    }

    private static InvalidOperationException CreatePlaylistUrlDialogDisplayException(
        string routeName,
        UiDialogResult result)
    {
        return new InvalidOperationException(
            routeName + " could not be displayed (" + result.Status + ").",
            result.Exception);
    }

    private async Task<bool> QueuePlaylistUrlInstallPathsAsync(IEnumerable<string> paths)
    {
        string[] pathSnapshot = [.. (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => playlistUrlAcquisitionWorkflow.IsStagedFileReady(path))];
        if (pathSnapshot.Length == 0)
        {
            return false;
        }
        playlistUrlInstallSink(Array.AsReadOnly(pathSnapshot));
        await playlistUrlAcquisitionPresentationScheduler(
            () => PlaylistUrlInstallTreeExpansionRequested?.Invoke())
            .ConfigureAwait(true);
        return true;
    }

}
