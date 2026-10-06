using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly object playlistSyncProgressPublicationLock = new();
    private readonly Dictionary<(string Source, OperationProgressRequest Request), int> playlistSyncProgressActiveOperations = new();
    private readonly HashSet<(string Source, long OperationId, OperationProgressRequest Request)> activePlaylistProgressIds = [];

    /// <summary>同じ生産元の複合処理が終結するまで、その進捗行を所有します。</summary>
    internal void BeginPlaylistSyncProgressOperation(string source = "playlist", OperationProgressRequest request = null)
    {
        lock (playlistSyncProgressPublicationLock)
        {
            playlistSyncProgressActiveOperations.TryGetValue((source, request), out int count);
            playlistSyncProgressActiveOperations[(source, request)] = count + 1;
        }
    }

    /// <summary>生産元の最後の所有処理が終わったとき、その行だけを消します。</summary>
    internal void EndPlaylistSyncProgressOperation(string source = "playlist", OperationProgressRequest request = null)
    {
        lock (playlistSyncProgressPublicationLock)
        {
            playlistSyncProgressActiveOperations.TryGetValue((source, request), out int count);
            if (count > 1)
            {
                playlistSyncProgressActiveOperations[(source, request)] = count - 1;
                return;
            }
            playlistSyncProgressActiveOperations.Remove((source, request));
            PublishPlaylistSyncProgress(new PlaylistSyncProgressSnapshot { Source = source, Request = request, IsActive = false });
        }
    }

    /// <summary>異なる生産元と既存操作IDの進捗・終端を、互いに上書きせず表示先へ渡します。</summary>
    internal void ReportPlaylistSyncProgress(PlaylistSyncProgressSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        lock (playlistSyncProgressPublicationLock)
        {
            if (snapshot.OperationId != 0)
            {
                (string Source, long OperationId, OperationProgressRequest Request) key = (snapshot.Source, snapshot.OperationId, snapshot.Request);
                if (snapshot.IsActive)
                {
                    activePlaylistProgressIds.Add(key);
                }
                else if (!activePlaylistProgressIds.Remove(key))
                {
                    return;
                }
            }
            // ID付きBMT出力と修復は自身の終端を持ち、利用者の複合処理とは別に終結します。
            if (!snapshot.IsActive && snapshot.OperationId == 0
                && playlistSyncProgressActiveOperations.ContainsKey((snapshot.Source, snapshot.Request)))
            {
                return;
            }

            PublishPlaylistSyncProgress(snapshot);
        }
    }

    // モデル共通通知の既存操作IDを保ち、呼出し元が所有する仕事だけを識別します。
    private void ReportPlaylistSyncProgress(PlaylistSyncProgressSnapshot snapshot, string source, string singleLabel = null, OperationProgressRequest request = null)
    {
        if (snapshot == null)
        {
            return;
        }
        snapshot.Source = source;
        snapshot.Request = request;
        if (singleLabel != null)
        {
            snapshot.SingleLabel = singleLabel;
            snapshot.LabelFormat = singleLabel + " {0}/{1}";
        }
        ReportPlaylistSyncProgress(snapshot);
    }

    private void PublishPlaylistSyncProgress(PlaylistSyncProgressSnapshot snapshot)
    {
        PlaylistSyncProgressChanged?.Invoke(this, new PlaylistSyncProgressChangedEventArgs(snapshot));
    }
}
