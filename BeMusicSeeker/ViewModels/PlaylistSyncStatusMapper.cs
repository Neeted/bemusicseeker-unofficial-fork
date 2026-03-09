using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

internal static class PlaylistSyncStatusMapper
{
    internal static PlaylistSyncRuntimeStatus CreateNone()
    {
        return new PlaylistSyncRuntimeStatus
        {
            Kind = PlaylistSyncStatusKind.None,
            StatusText = Resources.Playlist_sync_status_none,
            Detail = string.Empty,
            StatusSortOrder = 120,
            HasFailureStatus = false,
            CheckedAt = DateTime.MinValue
        };
    }

    internal static PlaylistSyncRuntimeStatus Create(PlaylistSyncAttemptResult result, DateTime checkedAt)
    {
        if (result == null)
        {
            return CreateNone();
        }
        PlaylistSyncStatusKind playlistSyncStatusKind = result.Succeeded ? (result.Updated ? PlaylistSyncStatusKind.Updated : PlaylistSyncStatusKind.Ok) : result.FailureKind;
        string statusText = GetStatusText(playlistSyncStatusKind);
        string detail = BuildDetail(result, statusText, checkedAt);
        return new PlaylistSyncRuntimeStatus
        {
            Kind = playlistSyncStatusKind,
            StatusText = statusText,
            Detail = detail,
            StatusSortOrder = GetStatusSortOrder(playlistSyncStatusKind),
            HasFailureStatus = playlistSyncStatusKind != PlaylistSyncStatusKind.None && playlistSyncStatusKind != PlaylistSyncStatusKind.Ok && playlistSyncStatusKind != PlaylistSyncStatusKind.Updated,
            CheckedAt = checkedAt
        };
    }

    internal static string GetStatusText(PlaylistSyncStatusKind kind)
    {
        return kind switch
        {
            PlaylistSyncStatusKind.Ok => Resources.Playlist_sync_status_ok,
            PlaylistSyncStatusKind.Updated => Resources.Playlist_sync_status_updated,
            PlaylistSyncStatusKind.HeaderNotFound => Resources.Playlist_sync_status_header_url,
            PlaylistSyncStatusKind.HeaderParseError => Resources.Playlist_sync_status_header,
            PlaylistSyncStatusKind.DataParseError => Resources.Playlist_sync_status_data,
            PlaylistSyncStatusKind.InvalidDataUrl => Resources.Playlist_sync_status_invalid_url,
            PlaylistSyncStatusKind.Http404 => Resources.Playlist_sync_status_404,
            PlaylistSyncStatusKind.Http403 => Resources.Playlist_sync_status_403,
            PlaylistSyncStatusKind.HttpError => Resources.Playlist_sync_status_http,
            PlaylistSyncStatusKind.NetworkError => Resources.Playlist_sync_status_network,
            PlaylistSyncStatusKind.UnknownError => Resources.Playlist_sync_status_unknown,
            _ => Resources.Playlist_sync_status_none,
        };
    }

    internal static int GetStatusSortOrder(PlaylistSyncStatusKind kind)
    {
        return kind switch
        {
            PlaylistSyncStatusKind.HeaderNotFound => 10,
            PlaylistSyncStatusKind.HeaderParseError => 20,
            PlaylistSyncStatusKind.DataParseError => 30,
            PlaylistSyncStatusKind.InvalidDataUrl => 40,
            PlaylistSyncStatusKind.Http404 => 50,
            PlaylistSyncStatusKind.Http403 => 60,
            PlaylistSyncStatusKind.HttpError => 70,
            PlaylistSyncStatusKind.NetworkError => 80,
            PlaylistSyncStatusKind.UnknownError => 90,
            PlaylistSyncStatusKind.Updated => 100,
            PlaylistSyncStatusKind.Ok => 110,
            _ => 120,
        };
    }

    private static string BuildDetail(PlaylistSyncAttemptResult result, string statusText, DateTime checkedAt)
    {
        string timestampText = (checkedAt == DateTime.MinValue) ? string.Empty : checkedAt.ToString("yyyy/MM/dd HH:mm:ss");
        string pageUriText = result?.PageUri?.ToString() ?? string.Empty;
        string diagnosticText = result?.Detail ?? string.Empty;
        if (result != null && result.Succeeded)
        {
            return JoinNonEmptyLines(statusText, timestampText, pageUriText);
        }
        return JoinNonEmptyLines(statusText, timestampText, pageUriText, diagnosticText);
    }

    private static string JoinNonEmptyLines(params string[] values)
    {
        return string.Join(Environment.NewLine, Array.FindAll(values ?? Array.Empty<string>(), (string value) => !string.IsNullOrWhiteSpace(value)));
    }
}
