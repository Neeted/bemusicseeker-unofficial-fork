using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal enum PlaylistUrlAcquisitionConfirmationKind
{
    SelectedUrls,
    ExternalPackages
}

internal sealed class PlaylistUrlAcquisitionConfirmationRequestedEventArgs : EventArgs
{
    internal PlaylistUrlAcquisitionConfirmationRequestedEventArgs(
        PlaylistUrlAcquisitionConfirmationKind kind,
        int targetCount,
        bool isDiffUrl,
        int largeSelectionWarningThreshold)
    {
        Kind = kind;
        TargetCount = Math.Max(0, targetCount);
        IsDiffUrl = isDiffUrl;
        LargeSelectionWarningThreshold = Math.Max(0, largeSelectionWarningThreshold);
    }

    internal PlaylistUrlAcquisitionConfirmationKind Kind { get; }

    internal int TargetCount { get; }

    internal bool IsDiffUrl { get; }

    internal int LargeSelectionWarningThreshold { get; }

    internal bool Confirmed { get; set; }
}

internal enum PlaylistUrlAcquisitionNotificationKind
{
    SelectedUrlsNoTargets,
    SelectedUrlsBlockedByInstallQueue,
    ExternalPackagesNoTargets,
    ExternalPackagesBlockedByInstallQueue
}

internal sealed class PlaylistUrlAcquisitionNotificationRequestedEventArgs : EventArgs
{
    internal PlaylistUrlAcquisitionNotificationRequestedEventArgs(PlaylistUrlAcquisitionNotificationKind kind)
    {
        Kind = kind;
    }

    internal PlaylistUrlAcquisitionNotificationKind Kind { get; }
}

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

internal sealed class PlaylistUrlAcquisitionSummaryReadyEventArgs : EventArgs
{
    internal PlaylistUrlAcquisitionSummaryReadyEventArgs(
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
        ExternalPackageLookup = externalPackageLookup;
        TargetCount = Math.Max(0, targetCount);
        DownloadedCount = Math.Max(0, downloadedCount);
        BrowserFallbackCount = Math.Max(0, browserFallbackCount);
        BlockedBySizeLimitCount = Math.Max(0, blockedBySizeLimitCount);
        DuplicateCount = Math.Max(0, duplicateCount);
        FailedCount = Math.Max(0, failedCount);
        NoCandidateCount = Math.Max(0, noCandidateCount);
        DuplicateDownloadedUrlCount = Math.Max(0, duplicateDownloadedUrlCount);
        DuplicateFailedUrlCount = Math.Max(0, duplicateFailedUrlCount);
        UnsupportedCount = Math.Max(0, unsupportedCount);
        CanceledCount = Math.Max(0, canceledCount);
    }

    internal bool ExternalPackageLookup { get; }

    internal int TargetCount { get; }

    internal int DownloadedCount { get; }

    internal int BrowserFallbackCount { get; }

    internal int BlockedBySizeLimitCount { get; }

    internal int DuplicateCount { get; }

    internal int FailedCount { get; }

    internal int NoCandidateCount { get; }

    internal int DuplicateDownloadedUrlCount { get; }

    internal int DuplicateFailedUrlCount { get; }

    internal int UnsupportedCount { get; }

    internal int CanceledCount { get; }
}

public sealed partial class PlaylistWorkspaceViewModel
{
    private const int PlaylistUrlDownloadLargeSelectionWarningThreshold = 50;

    private readonly object playlistUrlAcquisitionSync = new();

    private readonly Func<bool> playlistUrlInstallQueueActiveProvider;

    private readonly Action<IReadOnlyList<string>> playlistUrlInstallSink;

    private readonly Action<Uri> playlistUrlBrowserOpenSink;

    private int playlistUrlAcquisitionRunning;

    private CancellationTokenSource playlistUrlAcquisitionCancellation;

    private int playlistUrlAcquisitionTotalCount;

    private int playlistUrlAcquisitionCompletedCount;

    private string playlistUrlAcquisitionCurrentDisplayName = string.Empty;

    private string playlistUrlAcquisitionLabelFormat = string.Empty;

    internal event EventHandler<PlaylistUrlDownloadStatusSnapshot> PlaylistUrlDownloadStatusChanged;

    internal event Action PlaylistUrlInstallQueued;

    internal event EventHandler<PlaylistUrlAcquisitionConfirmationRequestedEventArgs> PlaylistUrlAcquisitionConfirmationRequested;

    internal event EventHandler<PlaylistUrlAcquisitionNotificationRequestedEventArgs> PlaylistUrlAcquisitionNotificationRequested;

    internal event EventHandler<PlaylistUrlAcquisitionSummaryReadyEventArgs> PlaylistUrlAcquisitionSummaryReady;

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

    internal async Task OpenSinglePlaylistUrlAsync(Uri url)
    {
        if (url == null || !url.IsAbsoluteUri || IsPlaylistUrlDownloadRunning)
        {
            return;
        }

        if (GetPlaylistUrlAcquisitionOptions().ShouldAutoInstall)
        {
            PlaylistUrlDownloadResult result = await DownloadSinglePlaylistUrlCandidateWithStatusAsync(url).ConfigureAwait(false);
            if (result.Kind == PlaylistUrlDownloadResultKind.Downloaded
                && QueuePlaylistUrlInstallPaths([result.FilePath]))
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
        DispatchPlaylistUrlAcquisitionAction(() => playlistUrlBrowserOpenSink(url));
    }

    internal async Task DownloadSelectedPlaylistUrlsAsync(IEnumerable<Uri> urls, bool isDiffUrl)
    {
        if (IsPlaylistUrlDownloadRunning)
        {
            return;
        }
        List<Uri> targets = [.. (urls ?? []).Where(url => url != null && url.IsAbsoluteUri)];
        if (targets.Count == 0)
        {
            RequestPlaylistUrlAcquisitionNotification(PlaylistUrlAcquisitionNotificationKind.SelectedUrlsNoTargets);
            return;
        }
        if (IsPlaylistUrlInstallQueueActive())
        {
            RequestPlaylistUrlAcquisitionNotification(PlaylistUrlAcquisitionNotificationKind.SelectedUrlsBlockedByInstallQueue);
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
        if (!RequestPlaylistUrlAcquisitionConfirmation(
            new PlaylistUrlAcquisitionConfirmationRequestedEventArgs(
                PlaylistUrlAcquisitionConfirmationKind.SelectedUrls,
                targets.Count,
                isDiffUrl,
                PlaylistUrlDownloadLargeSelectionWarningThreshold)))
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

        QueuePlaylistUrlInstallPaths(downloadedPaths);
        RequestPlaylistUrlAcquisitionSummary(new PlaylistUrlAcquisitionSummaryReadyEventArgs(
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
            canceledCount));
    }

    internal async Task DownloadSelectedPlaylistExternalPackagesAsync(IEnumerable<string> chartMd5Targets)
    {
        if (IsPlaylistUrlDownloadRunning)
        {
            return;
        }
        List<string> targets = [.. (chartMd5Targets ?? []).Where(target => !string.IsNullOrWhiteSpace(target))];
        if (targets.Count == 0)
        {
            RequestPlaylistUrlAcquisitionNotification(PlaylistUrlAcquisitionNotificationKind.ExternalPackagesNoTargets);
            return;
        }
        if (IsPlaylistUrlInstallQueueActive())
        {
            RequestPlaylistUrlAcquisitionNotification(PlaylistUrlAcquisitionNotificationKind.ExternalPackagesBlockedByInstallQueue);
            return;
        }
        if (!RequestPlaylistUrlAcquisitionConfirmation(
            new PlaylistUrlAcquisitionConfirmationRequestedEventArgs(
                PlaylistUrlAcquisitionConfirmationKind.ExternalPackages,
                targets.Count,
                isDiffUrl: false,
                largeSelectionWarningThreshold: 0)))
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

        QueuePlaylistUrlInstallPaths(downloadedPaths);
        RequestPlaylistUrlAcquisitionSummary(new PlaylistUrlAcquisitionSummaryReadyEventArgs(
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
            canceledCount));
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

    private bool RequestPlaylistUrlAcquisitionConfirmation(PlaylistUrlAcquisitionConfirmationRequestedEventArgs request)
    {
        PlaylistUrlAcquisitionConfirmationRequested?.Invoke(this, request);
        return request.Confirmed;
    }

    private void RequestPlaylistUrlAcquisitionNotification(PlaylistUrlAcquisitionNotificationKind kind)
    {
        PlaylistUrlAcquisitionNotificationRequestedEventArgs request =
            new(kind);
        DispatchPlaylistUrlAcquisitionAction(() => PlaylistUrlAcquisitionNotificationRequested?.Invoke(this, request));
    }

    private void RequestPlaylistUrlAcquisitionSummary(PlaylistUrlAcquisitionSummaryReadyEventArgs summary)
    {
        DispatchPlaylistUrlAcquisitionAction(() => PlaylistUrlAcquisitionSummaryReady?.Invoke(this, summary));
    }

    private bool QueuePlaylistUrlInstallPaths(IEnumerable<string> paths)
    {
        string[] pathSnapshot = [.. (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => playlistUrlAcquisitionWorkflow.IsStagedFileReady(path))];
        if (pathSnapshot.Length == 0)
        {
            return false;
        }
        playlistUrlInstallSink(Array.AsReadOnly(pathSnapshot));
        DispatchPlaylistUrlAcquisitionAction(() => PlaylistUrlInstallQueued?.Invoke());
        return true;
    }

    private void DispatchPlaylistUrlAcquisitionAction(Action action)
    {
        if (action != null)
        {
            dispatchPresentation(action);
        }
    }
}
