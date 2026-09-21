using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly object playlistSyncProgressLock = new();

    private readonly object playlistSyncProgressPublicationLock = new();

    private int playlistSyncProgressActiveOperationCount;

    private readonly HashSet<long> activeBeatorajaBmtExportProgressOperations = [];

    internal void BeginPlaylistSyncProgressOperation()
    {
        lock (playlistSyncProgressPublicationLock)
        {
            lock (playlistSyncProgressLock)
            {
                playlistSyncProgressActiveOperationCount++;
            }
        }
    }

    internal void EndPlaylistSyncProgressOperation()
    {
        lock (playlistSyncProgressPublicationLock)
        {
            bool shouldClear;
            lock (playlistSyncProgressLock)
            {
                if (playlistSyncProgressActiveOperationCount > 0)
                {
                    playlistSyncProgressActiveOperationCount--;
                }
                shouldClear = playlistSyncProgressActiveOperationCount == 0;
            }
            if (shouldClear)
            {
                PublishPlaylistSyncProgress(CreateInactivePlaylistSyncProgressSnapshot());
            }
        }
    }

    internal void ReportPlaylistSyncProgress(PlaylistSyncProgressSnapshot snapshot)
    {
        bool isActive = snapshot?.IsActive == true;
        long operationId = snapshot?.OperationId ?? 0;
        lock (playlistSyncProgressPublicationLock)
        {
            if (isActive)
            {
                if (operationId != 0)
                {
                    lock (playlistSyncProgressLock)
                    {
                        if (activeBeatorajaBmtExportProgressOperations.Add(operationId))
                        {
                            playlistSyncProgressActiveOperationCount++;
                        }
                    }
                }
                PublishPlaylistSyncProgress(snapshot);
                return;
            }

            if (operationId != 0)
            {
                bool shouldClear = false;
                lock (playlistSyncProgressLock)
                {
                    if (activeBeatorajaBmtExportProgressOperations.Remove(operationId))
                    {
                        if (playlistSyncProgressActiveOperationCount > 0)
                        {
                            playlistSyncProgressActiveOperationCount--;
                        }
                        shouldClear = playlistSyncProgressActiveOperationCount == 0;
                    }
                }
                if (shouldClear)
                {
                    PublishPlaylistSyncProgress(CreateInactivePlaylistSyncProgressSnapshot());
                }
                return;
            }

            lock (playlistSyncProgressLock)
            {
                if (playlistSyncProgressActiveOperationCount > 0)
                {
                    return;
                }
            }
            PublishPlaylistSyncProgress(snapshot);
        }
    }

    private void PublishPlaylistSyncProgress(PlaylistSyncProgressSnapshot snapshot)
    {
        PlaylistSyncProgressChanged?.Invoke(
            this,
            new PlaylistSyncProgressChangedEventArgs(snapshot));
    }

    private static PlaylistSyncProgressSnapshot CreateInactivePlaylistSyncProgressSnapshot()
    {
        return new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            TotalTableCount = 0,
            CompletedTableCount = 0,
            CurrentTableName = string.Empty,
            CurrentUri = null
        };
    }
}
