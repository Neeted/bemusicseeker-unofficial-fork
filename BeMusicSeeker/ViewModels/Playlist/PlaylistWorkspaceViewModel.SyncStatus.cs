using System;
using System.Collections.Generic;
using System.Globalization;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly object playlistSyncStatusesSyncRoot = new();

    private readonly Dictionary<string, PlaylistSyncRuntimeStatus> playlistSyncStatuses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records the latest runtime status for a playlist-sync attempt.
    /// </summary>
    internal void RecordPlaylistSyncResult(PlaylistSyncAttemptResult result)
    {
        if (result == null)
        {
            return;
        }

        PlaylistSyncRuntimeStatus status = PlaylistSyncStatusMapper.Create(result, DateTime.Now);
        string resultKey = GetPlaylistSyncStatusKey(result.ResultTable ?? result.SourceTable);
        string sourceKey = GetPlaylistSyncStatusKey(result.SourceTable);
        lock (playlistSyncStatusesSyncRoot)
        {
            if (!string.IsNullOrWhiteSpace(sourceKey)
                && !string.Equals(sourceKey, resultKey, StringComparison.OrdinalIgnoreCase))
            {
                playlistSyncStatuses.Remove(sourceKey);
            }
            if (!string.IsNullOrWhiteSpace(resultKey))
            {
                playlistSyncStatuses[resultKey] = status;
            }
            else if (!string.IsNullOrWhiteSpace(sourceKey))
            {
                playlistSyncStatuses[sourceKey] = status;
            }
        }
    }

    /// <summary>
    /// Captures an isolated runtime status snapshot for one summary build.
    /// </summary>
    internal IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> CapturePlaylistSyncStatusSnapshot()
    {
        lock (playlistSyncStatusesSyncRoot)
        {
            var snapshot = new Dictionary<string, PlaylistSyncRuntimeStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, PlaylistSyncRuntimeStatus> pair in playlistSyncStatuses)
            {
                snapshot[pair.Key] = ClonePlaylistSyncRuntimeStatus(pair.Value);
            }
            return snapshot;
        }
    }

    private static PlaylistSyncRuntimeStatus ClonePlaylistSyncRuntimeStatus(PlaylistSyncRuntimeStatus value)
    {
        if (value == null)
        {
            return null;
        }
        return new PlaylistSyncRuntimeStatus
        {
            Kind = value.Kind,
            StatusText = value.StatusText,
            Detail = value.Detail,
            StatusSortOrder = value.StatusSortOrder,
            HasFailureStatus = value.HasFailureStatus,
            CheckedAt = value.CheckedAt
        };
    }

    private static string GetPlaylistSyncStatusKey(BMSTable table)
    {
        if (table == null)
        {
            return null;
        }
        if (table.playlist_id.HasValue)
        {
            return "id:" + table.playlist_id.Value.ToString(CultureInfo.InvariantCulture);
        }
        Uri uri = table.Page_url ?? table.Header_url;
        if (uri != null && uri.IsAbsoluteUri)
        {
            return "uri:" + uri.AbsoluteUri;
        }
        if (!string.IsNullOrWhiteSpace(table.name))
        {
            return "name:" + table.name;
        }
        return null;
    }
}
