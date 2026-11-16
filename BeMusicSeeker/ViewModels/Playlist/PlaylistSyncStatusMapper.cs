using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

internal static class PlaylistSyncStatusMapper
{
    private static readonly PlaylistSyncRuntimeStatus None = new()
    {
        Kind = PlaylistSyncStatusKind.None,
        CheckedAt = DateTime.MinValue
    };

    /// <summary>未実施の状態を返します。不変値のため共有します。</summary>
    internal static PlaylistSyncRuntimeStatus CreateNone() => None;

    /// <summary>同期結果と検査日時の事実を保持します。文言は読取り時に現在の資源から作り、日時の説明は時刻帯を変えずGregorianの固定書式へ揃えます。</summary>
    internal static PlaylistSyncRuntimeStatus Create(PlaylistSyncAttemptResult result, DateTime checkedAt)
    {
        if (result == null)
        {
            return CreateNone();
        }
        PlaylistSyncStatusKind playlistSyncStatusKind = result.Succeeded ? (result.Updated ? PlaylistSyncStatusKind.Updated : PlaylistSyncStatusKind.Ok) : result.FailureKind;
        return new PlaylistSyncRuntimeStatus
        {
            Kind = playlistSyncStatusKind,
            CheckedAt = checkedAt,
            PageUri = result.PageUri?.ToString() ?? string.Empty,
            Diagnostic = result.Succeeded ? string.Empty : result.Detail ?? string.Empty,
            HasResult = true
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

    /// <summary>保持した事実から現在語の詳細文を作ります。先頭行は現在の状態名です。</summary>
    internal static string BuildDetail(PlaylistSyncRuntimeStatus status)
    {
        if (!status.HasResult)
        {
            return string.Empty;
        }
        string timestampText = status.CheckedAt == DateTime.MinValue ? string.Empty : DateTimeDisplayFormatter.FormatTimestamp(status.CheckedAt);
        return JoinNonEmptyLines(GetStatusText(status.Kind), timestampText, status.PageUri, status.Diagnostic);
    }

    private static string JoinNonEmptyLines(params string[] values)
    {
        return string.Join(Environment.NewLine, Array.FindAll(values ?? [], value => !string.IsNullOrWhiteSpace(value)));
    }
}
