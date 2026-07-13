using System;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private Func<BMSPlaylist> getPlaylistStore;

    internal event EventHandler<PlaylistDetailEditRefreshRequestedEventArgs> PlaylistDetailEditRefreshRequested;

    internal void ConfigureDetailEditing(Func<BMSPlaylist> playlistStore)
    {
        getPlaylistStore = playlistStore ?? throw new ArgumentNullException(nameof(playlistStore));
    }

    internal bool CanBeginDetailEdit(MainChartListCellEditContext context)
    {
        return context?.Row is PlaylistDetailRow
            && IsDetailEditableProperty(context.PropertyName)
            && GridRowResolver.CanEditPlaylistCell(context.Row, context.PropertyName);
    }

    internal void BeginDetailEdit(MainChartListCellEditContext context)
    {
        if (!CanBeginDetailEdit(context))
        {
            return;
        }
        lock (DetailViewState.SyncRoot)
        {
            DetailViewState.Source.IsPlaylistCellEditing = true;
        }
    }

    internal void CompleteDetailEdit(MainChartListCellEditEndedEventArgs request)
    {
        if (request?.Context.Row is not PlaylistDetailRow playlistRow)
        {
            return;
        }
        try
        {
            if (!request.Commit
                || !IsDetailEditableProperty(request.Context.PropertyName)
                || !GridRowResolver.CanEditPlaylistCell(playlistRow, request.Context.PropertyName)
                || !TryApplyEdit(playlistRow, request.Context.PropertyName, request.Text))
            {
                return;
            }
            SynchronizeSourceRow(playlistRow);
            Task.Run(() => CommitRow(playlistRow, request.Context.PropertyName)).Logging("playlistDetailCellEditCommit");
        }
        finally
        {
            CompleteDetailEditSession();
        }
    }

    private void SynchronizeSourceRow(PlaylistDetailRow playlistRow)
    {
        lock (DetailViewState.SyncRoot)
        {
            PlaylistDetailSourceRow sourceRow = (DetailViewState.Source.Rows ?? [])
                .FirstOrDefault(row => row != null && ReferenceEquals(row.Entry, playlistRow.Entry));
            sourceRow?.SynchronizeEditableSnapshot(playlistRow);
        }
    }

    private void CommitRow(PlaylistDetailRow playlistRow, string editedPropertyName)
    {
        BMSPlaylist playlistStore = getPlaylistStore?.Invoke()
            ?? throw new InvalidOperationException("Playlist persistence is not available.");
        ChartFile chart = playlistRow.Chart;
        if (chart != null && playlistRow.Entry?.parent?.is_external_sync != true)
        {
            playlistRow.Entry.ApplyPlaylistHashesFromChart(chart);
        }
        playlistStore.CommitBMSTableEntry(playlistRow.Entry, editedPropertyName);
    }

    private void CompleteDetailEditSession()
    {
        int pendingScoreSnapshotVersion;
        int lastBuiltScoreSnapshotVersion;
        lock (DetailViewState.SyncRoot)
        {
            DetailViewState.Source.IsPlaylistCellEditing = false;
            pendingScoreSnapshotVersion = DetailViewState.Source.PendingScoreSnapshotRefreshVersion;
            lastBuiltScoreSnapshotVersion = DetailViewState.Source.LastBuiltScoreSnapshotVersion;
            if (pendingScoreSnapshotVersion > lastBuiltScoreSnapshotVersion)
            {
                DetailViewState.Source.PendingScoreSnapshotRefreshVersion = 0;
            }
        }
        if (pendingScoreSnapshotVersion > lastBuiltScoreSnapshotVersion)
        {
            PlaylistDetailEditRefreshRequested?.Invoke(
                this,
                new PlaylistDetailEditRefreshRequestedEventArgs(
                    pendingScoreSnapshotVersion,
                    lastBuiltScoreSnapshotVersion));
        }
    }

    private static bool TryApplyEdit(PlaylistDetailRow row, string propertyName, string text)
    {
        switch (propertyName)
        {
            case nameof(PlaylistDetailRow.Level):
                row.Level = text;
                return true;
            case nameof(PlaylistDetailRow.Url):
                if (!Uri.TryCreate(text, UriKind.Absolute, out Uri url)) return false;
                row.Url = url;
                return true;
            case nameof(PlaylistDetailRow.Url_diff):
                if (!Uri.TryCreate(text, UriKind.Absolute, out Uri urlDiff)) return false;
                row.Url_diff = urlDiff;
                return true;
            case nameof(PlaylistDetailRow.comment):
                row.comment = text;
                return true;
            case nameof(PlaylistDetailRow.memo):
                row.memo = text;
                return true;
            default:
                return false;
        }
    }

    private static bool IsDetailEditableProperty(string propertyName)
    {
        return string.Equals(propertyName, nameof(PlaylistDetailRow.Level), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.Url), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.Url_diff), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.comment), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.memo), StringComparison.Ordinal);
    }
}

internal sealed class PlaylistDetailEditRefreshRequestedEventArgs : EventArgs
{
    internal PlaylistDetailEditRefreshRequestedEventArgs(int pendingVersion, int lastBuiltVersion)
    {
        PendingVersion = pendingVersion;
        LastBuiltVersion = lastBuiltVersion;
    }

    internal int PendingVersion { get; }

    internal int LastBuiltVersion { get; }
}
