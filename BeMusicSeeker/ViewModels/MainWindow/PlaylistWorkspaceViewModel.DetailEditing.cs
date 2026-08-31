using System;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly Func<BMSPlaylist> getPlaylistStore;

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

    /// <summary>
    /// Completes a playlist-detail cell edit and returns the scheduled persistence commit.
    /// </summary>
    /// <param name="request">The edit result raised by the main chart list.</param>
    /// <returns>
    /// A task for the persistence commit, or <see cref="Task.CompletedTask"/> when the edit is
    /// cancelled or cannot be applied.
    /// </returns>
    internal Task CompleteDetailEdit(MainChartListCellEditEndedEventArgs request)
    {
        if (request?.Context.Row is not PlaylistDetailRow playlistRow)
        {
            return Task.CompletedTask;
        }
        try
        {
            if (!request.Commit
                || !IsDetailEditableProperty(request.Context.PropertyName)
                || !GridRowResolver.CanEditPlaylistCell(playlistRow, request.Context.PropertyName))
            {
                return Task.CompletedTask;
            }
            BMSTableEntry originalEntry = playlistRow.Entry.CreatePlaylistReloadSnapshot();
            if (!TryApplyEdit(playlistRow, request.Context.PropertyName, request.Text))
            {
                return Task.CompletedTask;
            }
            string editedPropertyName = request.Context.PropertyName;
            return CommitRowAndSynchronizeSourceAsync(playlistRow, editedPropertyName, originalEntry);
        }
        finally
        {
            CompleteDetailEditSession();
        }
    }

    private async Task CommitRowAndSynchronizeSourceAsync(
        PlaylistDetailRow playlistRow,
        string editedPropertyName,
        BMSTableEntry originalEntry)
    {
        try
        {
            await Task.Run(() => CommitRow(playlistRow, editedPropertyName)).ConfigureAwait(true);
        }
        catch
        {
            playlistRow.Entry.ApplyPlaylistEditableStateFrom(originalEntry, editedPropertyName);
            throw;
        }
        SynchronizeSourceRow(playlistRow);
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
        BMSPlaylist playlistStore = getPlaylistStore()
            ?? throw new InvalidOperationException("Playlist persistence is not available.");
        ChartFile chart = playlistRow.Chart;
        if (chart != null && playlistRow.Entry?.parent?.is_external_sync != true)
        {
            playlistRow.Entry.ApplyPlaylistHashesFromChart(chart);
        }
        RunWithNotifications(
            () => playlistStore.CommitBMSTableEntry(playlistRow.Entry, editedPropertyName),
            "playlist detail cell edit notification");
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
            PublishPlaylistDetailScoreSnapshotRefreshRequested(
                pendingScoreSnapshotVersion,
                lastBuiltScoreSnapshotVersion,
                deferredByEdit: true,
                refreshRequired: true);
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
