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
        ExternalPackagesBlockedByInstallQueue,
        InstallUnavailable
    }

    private readonly object playlistUrlAcquisitionSync = new();

    private readonly Func<Action, Task> playlistUrlAcquisitionPresentationScheduler;


    private readonly Func<DroppedInstallBatchRequest, bool> playlistUrlInstallSink;

    private readonly Action<Uri> playlistUrlBrowserOpenSink;

    internal event Action PlaylistUrlInstallTreeExpansionRequested;

    private int playlistUrlAcquisitionRunning;

    private CancellationTokenSource playlistUrlAcquisitionCancellation;

    private readonly CancellationTokenSource playlistUrlShutdownCancellation = new();

    private readonly HashSet<Task> playlistUrlTasks = [];

    /// <summary>表示終了後の導入引渡しと未受理入力の回収も含め、新規取得を閉じ全要求を取り消します。</summary>
    internal void RequestPlaylistUrlShutdown()
    {
        lock (playlistUrlAcquisitionSync) { playlistUrlShutdownCancellation.Cancel(); }
    }

    /// <summary>終了時に受理済みの全URL操作の実終端を待ちます。取得表示の終了は完了として扱いません。</summary>
    internal Task WaitForPlaylistUrlIdleAsync()
    {
        lock (playlistUrlAcquisitionSync) { return Task.WhenAll(playlistUrlTasks); }
    }

    private Task TrackPlaylistUrlTask(Func<Task> operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (playlistUrlAcquisitionSync)
        {
            if (playlistUrlShutdownCancellation.IsCancellationRequested) { return Task.CompletedTask; }
            playlistUrlTasks.Add(completion.Task);
        }
        _ = CompletePlaylistUrlTaskAsync(operation, completion);
        return completion.Task;
    }

    private async Task CompletePlaylistUrlTaskAsync(Func<Task> operation, TaskCompletionSource completion)
    {
        try { await operation().ConfigureAwait(false); completion.TrySetResult(); }
        catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
        catch (Exception exception) { completion.TrySetException(exception); }
        finally { lock (playlistUrlAcquisitionSync) { playlistUrlTasks.Remove(completion.Task); } }
    }

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

    /// <summary>右クリック時の有効行を固定し、単体は元URLをブラウザーへ、複数行は確認付き一括取得へ渡します。</summary>
    /// <param name="rows">選択順の有効行。空の場合は何もしません。</param>
    /// <param name="isDiffUrl">差分URLを使う場合はtrue。</param>
    /// <returns>ブラウザー配送または一括取得の終端まで待つTask。</returns>
    internal Task RunPlaylistUrlActionAsync(IEnumerable<object> rows, bool isDiffUrl)
    {
        if (IsPlaylistUrlDownloadRunning)
        {
            return Task.CompletedTask;
        }
        List<object> rowSnapshot = [.. (rows ?? []).Where(row => row != null)];
        if (rowSnapshot.Count <= 1)
        {
            Uri url = rowSnapshot.Count == 0
                ? null
                : isDiffUrl
                    ? GridRowResolver.GetUrlDiff(rowSnapshot[0])
                    : GridRowResolver.GetUrl(rowSnapshot[0]);
            return OpenPlaylistUrlInBrowserAsync(url);
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
            && PlaylistContextMenuTargetResolver.BuildPlaylistExternalPackageMd5Targets(rowSnapshot).Count > 0;

        return new PlaylistUrlContextMenuAvailability(
            isPlaylistContext,
            isBulkContext,
            canOpenUrl,
            canOpenDiffUrl,
            canFindExternalPackage);
    }

    /// <summary>URLセルの単体クリックとして、自動導入条件に応じた取得またはブラウザー配送を行います。</summary>
    /// <param name="url">セルの元URL。</param>
    /// <returns>取得・導入引渡しまたはブラウザー配送の終端まで待つTask。</returns>
    internal Task RunSinglePlaylistUrlAsync(Uri url)
    {
        if (url == null || !url.IsAbsoluteUri || IsPlaylistUrlDownloadRunning)
        {
            return Task.CompletedTask;
        }

        return TrackPlaylistUrlTask(() => RunSinglePlaylistUrlCoreAsync(url));
    }

    private async Task RunSinglePlaylistUrlCoreAsync(Uri url)
    {
        if (url == null || !url.IsAbsoluteUri)
        {
            return;
        }
        if (!PlaylistUrlAcquisitionWorkflow.IsHttpOrHttpsUri(url))
        {
            return;
        }
        Uri normalizedUrl = PlaylistUrlAcquisitionWorkflow.NormalizeDownloadUri(url);
        CancellationToken requestCancellation = playlistUrlShutdownCancellation.Token;
        if (!PlaylistUrlAcquisitionWorkflow.IsHttpOrHttpsUri(normalizedUrl))
        {
            return;
        }
        if (GetPlaylistUrlAcquisitionOptions().ShouldAutoInstall)
        {
            using CancellationTokenSource cancellation = BeginPlaylistUrlAcquisition(1, url.ToString(), string.Empty);
            if (cancellation == null) { return; }
            requestCancellation = cancellation.Token;
            PlaylistUrlDownloadResult result = await DownloadSinglePlaylistUrlCandidateWithStatusAsync(url, cancellation).ConfigureAwait(false);
            if (result.Kind == PlaylistUrlDownloadResultKind.Downloaded
                && playlistUrlAcquisitionWorkflow.IsStagedFileReady(result.FilePath))
            {
                // 取得成功後の導入拒否は、URL 解決失敗によるブラウザ fallback とは別の終端。
                await QueuePlaylistUrlInstallPathsAsync([result.FilePath]).ConfigureAwait(true);
                return;
            }
            if (result.Kind == PlaylistUrlDownloadResultKind.BlockedBySizeLimit)
            {
                return;
            }
            if (result.Kind == PlaylistUrlDownloadResultKind.Failed
                && result.IsUnsupportedScheme)
            {
                return;
            }
        }

        await OpenPlaylistUrlInBrowserAsync(url, requestCancellation).ConfigureAwait(true);
    }

    /// <summary>取得用の正規化をせず、元の絶対HTTP(S) URLを既存のUI配送で開き、配送先の失敗も呼出元へ返します。</summary>
    private Task OpenPlaylistUrlInBrowserAsync(Uri url, CancellationToken cancellationToken = default)
    {
        if (url == null || !url.IsAbsoluteUri || !PlaylistUrlAcquisitionWorkflow.IsHttpOrHttpsUri(url))
        {
            return Task.CompletedTask;
        }
        return playlistUrlAcquisitionPresentationScheduler(() =>
        {
            playlistUrlShutdownCancellation.Token.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            playlistUrlBrowserOpenSink(url);
        });
    }

    internal Task RunPlaylistUrlBatchAsync(IEnumerable<Uri> urls, bool isDiffUrl)
        => TrackPlaylistUrlTask(() => RunPlaylistUrlBatchCoreAsync(urls, isDiffUrl));

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
        using CancellationTokenSource cancellation = BeginPlaylistUrlAcquisition(targets.Count, string.Empty, string.Empty);
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
        catch (Exception exception)
        {
            await ReclaimPlaylistUrlInputsAsync(downloadedPaths, exception).ConfigureAwait(false);
            throw;
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

    private Task RunExternalPackageLookupAsync(IEnumerable<string> chartMd5Targets)
        => TrackPlaylistUrlTask(() => RunExternalPackageLookupCoreAsync(chartMd5Targets));

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
        using CancellationTokenSource cancellation = BeginPlaylistUrlAcquisition(
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
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
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
        catch (Exception exception)
        {
            await ReclaimPlaylistUrlInputsAsync(downloadedPaths, exception).ConfigureAwait(false);
            throw;
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

    private async Task<PlaylistUrlDownloadResult> DownloadSinglePlaylistUrlCandidateWithStatusAsync(Uri url, CancellationTokenSource cancellation)
    {
        string displayName = url?.ToString() ?? string.Empty;
        PlaylistUrlDownloadResult result = null;
        try
        {
            UpdatePlaylistUrlDownloadStatus(true, 1, 0, displayName, canCancel: true, labelFormat: null);
            result = await playlistUrlAcquisitionWorkflow.DownloadCandidateAsync(url, cancellationToken: cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            UpdatePlaylistUrlDownloadStatus(true, 1, 1, displayName, canCancel: false, labelFormat: null);
            return result;
        }
        catch (Exception exception)
        {
            if (result?.Kind == PlaylistUrlDownloadResultKind.Downloaded)
            {
                await ReclaimPlaylistUrlInputsAsync([result.FilePath], exception).ConfigureAwait(false);
            }
            throw;
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
        // 保存と応答解放を完了した取得物は、取消し後も既存の集約・回収へ渡します。
        if (result.Kind != PlaylistUrlDownloadResultKind.Downloaded)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
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
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(playlistUrlShutdownCancellation.Token);
        lock (playlistUrlAcquisitionSync)
        {
            if (playlistUrlAcquisitionRunning != 0 || playlistUrlShutdownCancellation.IsCancellationRequested)
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

    // 回収失敗で取得・導入・公開の失敗を隠さず、両方の原因を保持する。
    private async Task ReclaimPlaylistUrlInputsAsync(IReadOnlyList<string> paths, Exception operationFailure)
    {
        try
        {
            await playlistUrlAcquisitionWorkflow.ReclaimStagedFilesAsync(paths).ConfigureAwait(false);
        }
        catch (Exception cleanupFailure) when (operationFailure != null)
        {
            throw new AggregateException(operationFailure, cleanupFailure);
        }
    }

    private void EndPlaylistUrlAcquisition(CancellationTokenSource cancellation)
    {
        bool shouldPublishInactive = false;
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
            shouldPublishInactive = true;
        }
        if (shouldPublishInactive)
        {
            UpdatePlaylistUrlDownloadStatus(false, 0, 0, string.Empty, canCancel: false, labelFormat: null);
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
            PublishPlaylistProgressEvent(
                PlaylistUrlDownloadStatusChanged,
                new PlaylistUrlDownloadStatusSnapshot(
                    isActive,
                    totalCount,
                    completedCount,
                    currentDisplayName,
                    canCancel,
                    labelFormat));
        }
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
        try
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
                PlaylistUrlAcquisitionNotificationKind.InstallUnavailable
                    => BeMusicSeeker.Properties.Resources.Warn_PackageInstallUnavailable,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
            UiDialogResult result = await playlistWorkspaceDialogService.ShowMessageAsync(
                UiMessageRequest.CreateWarning(
                    message,
                    BeMusicSeeker.Properties.Resources.Warning))
                .ConfigureAwait(true);
            ThrowIfPlaylistUrlDialogNotShown(result, "Playlist URL acquisition notification");
        }
        catch (Exception failure)
        {
            TryLogPlaylistDropSecondaryFailure(failure, "playlist_url_acquisition_notification_failed");
        }
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
        try
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
        catch (Exception failure)
        {
            TryLogPlaylistDropSecondaryFailure(failure, "playlist_url_acquisition_summary_failed");
        }
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

    private async Task QueuePlaylistUrlInstallPathsAsync(IEnumerable<string> paths)
    {
        string[] pathSnapshot = [.. (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => playlistUrlAcquisitionWorkflow.IsStagedFileReady(path))];
        if (pathSnapshot.Length == 0)
        {
            return;
        }
        var cleanupFailures = new List<Exception>();
        var request = new DroppedInstallBatchRequest(pathSnapshot, pathSnapshot, pathSnapshot,
            playlistUrlAcquisitionWorkflow.ReclaimStagedFile, (_, failure) => cleanupFailures.Add(failure));
        bool accepted = false;
        Exception operationFailure = null;
        try
        {
            accepted = playlistUrlInstallSink(request);
            if (!accepted)
            {
                await ShowPlaylistUrlAcquisitionNotificationAsync(PlaylistUrlAcquisitionNotificationKind.InstallUnavailable).ConfigureAwait(true);
                return;
            }
            await playlistUrlInstallCompletionProvider().ConfigureAwait(false);
            await playlistUrlAcquisitionPresentationScheduler(() => PlaylistUrlInstallTreeExpansionRequested?.Invoke()).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            if (!accepted)
            {
                // 拒否時のownerによる回収と受理前故障を、同じ要求の一回限りの処分へ集約します。
                await Task.Run(request.TryAbandonUnconsumedSources).ConfigureAwait(false);
                await request.WaitForDispositionAsync().ConfigureAwait(false);
            }
            // 受理後は待ち行列の実idleまで待った結果だけを確認し、導入へ移した入力を削除しません。
            if (cleanupFailures.Count > 0)
            {
                if (operationFailure != null) { cleanupFailures.Insert(0, operationFailure); }
                throw new AggregateException(cleanupFailures);
            }
        }

    }

}
