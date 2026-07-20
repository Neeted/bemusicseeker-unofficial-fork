using System;
using System.Windows;
using Livet;

namespace BeMusicSeeker.ViewModels;

public partial class MainWindowViewModel
{
    internal event Action PlaylistUrlInstallTreeExpansionRequested;

    internal event Action<PlaylistSummarySelectionRestoreRequest> PlaylistSummarySelectionRestoreRequested;

    private void PlaylistWorkspacePlaylistUrlDownloadStatusChanged(
        object sender,
        PlaylistUrlDownloadStatusSnapshot snapshot)
    {
        DispatchMainChartListAction(() => ProgressHub.UpdatePlaylistUrlDownloadStatus(snapshot));
    }

    private void PlaylistWorkspacePlaylistUrlAcquisitionConfirmationRequested(
        object sender,
        PlaylistUrlAcquisitionConfirmationRequestedEventArgs request)
    {
        if (request == null)
        {
            return;
        }

        if (request.Kind == PlaylistUrlAcquisitionConfirmationKind.ExternalPackages)
        {
            string confirmationMessage = string.Format(
                BeMusicSeeker.Properties.Resources.Confirm_SelectedPlaylistExternalPackageLookup,
                request.TargetCount);
            request.Confirmed = ShowUiConfirmation(
                confirmationMessage,
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxImage.Question,
                MessageBoxButton.OKCancel,
                "Playlist URL external package lookup confirmation",
                MessageBoxResult.Cancel,
                BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistExternalPackageLookup);
            return;
        }

        string urlKind = request.IsDiffUrl
            ? BeMusicSeeker.Properties.Resources.Diff_URL
            : BeMusicSeeker.Properties.Resources.Original_URL;
        string selectedUrlsMessage = string.Format(
            BeMusicSeeker.Properties.Resources.Confirm_SelectedPlaylistUrlDownload,
            request.TargetCount,
            urlKind);
        string largeSelectionWarningMessage = request.TargetCount >= request.LargeSelectionWarningThreshold
            ? string.Format(
                BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistUrlDownloadLargeSelection,
                request.LargeSelectionWarningThreshold)
            : null;
        request.Confirmed = ShowUiConfirmation(
            selectedUrlsMessage,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxImage.Question,
            MessageBoxButton.OKCancel,
            "Playlist URL download confirmation",
            MessageBoxResult.Cancel,
            largeSelectionWarningMessage);
    }

    private void PlaylistWorkspacePlaylistUrlAcquisitionNotificationRequested(
        object sender,
        PlaylistUrlAcquisitionNotificationRequestedEventArgs request)
    {
        if (request == null)
        {
            return;
        }

        string message = request.Kind switch
        {
            PlaylistUrlAcquisitionNotificationKind.SelectedUrlsNoTargets => BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistUrlDownloadNoTargets,
            PlaylistUrlAcquisitionNotificationKind.SelectedUrlsBlockedByInstallQueue => BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistUrlDownloadBlockedByInstallQueue,
            PlaylistUrlAcquisitionNotificationKind.ExternalPackagesNoTargets => BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistExternalPackageLookupNoTargets,
            PlaylistUrlAcquisitionNotificationKind.ExternalPackagesBlockedByInstallQueue => BeMusicSeeker.Properties.Resources.Warn_SelectedPlaylistExternalPackageLookupBlockedByInstallQueue,
            _ => throw new ArgumentOutOfRangeException(nameof(request.Kind), request.Kind, null)
        };
        ShowUiMessage(
            message,
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxImage.Exclamation,
            "Playlist URL acquisition notification");
    }

    private void PlaylistWorkspacePlaylistUrlAcquisitionSummaryReady(
        object sender,
        PlaylistUrlAcquisitionSummaryReadyEventArgs summary)
    {
        if (summary == null)
        {
            return;
        }

        string message = summary.ExternalPackageLookup
            ? string.Format(
                BeMusicSeeker.Properties.Resources.Msg_SelectedPlaylistExternalPackageLookupResult,
                summary.TargetCount,
                summary.DownloadedCount,
                summary.NoCandidateCount,
                summary.DuplicateDownloadedUrlCount,
                summary.DuplicateFailedUrlCount,
                summary.BlockedBySizeLimitCount,
                summary.UnsupportedCount,
                summary.FailedCount,
                summary.CanceledCount)
            : string.Format(
                BeMusicSeeker.Properties.Resources.Msg_SelectedPlaylistUrlDownloadResult,
                summary.TargetCount,
                summary.DownloadedCount,
                summary.BrowserFallbackCount,
                summary.BlockedBySizeLimitCount,
                summary.DuplicateCount,
                summary.FailedCount,
                summary.CanceledCount);
        ShowUiMessage(
            message,
            BeMusicSeeker.Properties.Resources.Information,
            summary.DownloadedCount > 0 ? MessageBoxImage.Asterisk : MessageBoxImage.Exclamation,
            "Playlist URL acquisition summary");
    }
}
