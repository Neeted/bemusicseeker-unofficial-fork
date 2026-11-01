using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>同じ機能の複数moduleが必要とする入力・観測だけを共用します。</summary>
internal static class ApplicationCompositionTestSupport
{

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal sealed class EmptyAdmissionRootFileEnumerator : IRootFileEnumerator
    {
        public RootFileEnumerationResult EnumerateFiles(IEnumerable<string> rootDirectories, IEnumerable<RootFileEnumerationGroup> groups, bool verboseLog = false)
        {
            var result = new RootFileEnumerationResult { Success = true, IsComplete = true, BackendName = "fixture", TotalFileCount = 0 };
            foreach (RootFileEnumerationGroup group in groups) { result.InitializeGroup(group.Name); }
            return result;
        }
    }

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static void AttachImmediatePlaylistPresentationRouter(PlaylistWorkspaceViewModel workspace)
    {
        AttachPlaylistPresentationRouter(workspace, deferSummaryData: false);
    }

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static void AttachPlaylistPresentationRouter(
        PlaylistWorkspaceViewModel workspace,
        bool deferSummaryData)
    {
        workspace.PlaylistEntriesHydrationRequested += (_, request) =>
        {
            if (!workspace.TryBeginPlaylistHydrationNotification(
                request.SourceStore,
                request.SourceTables,
                request.Generation,
                request.CompletionReceipt))
            {
                return;
            }
            workspace.ExecuteCurrentPlaylistHydrationNotification(
                request.SourceStore,
                request.SourceTables,
                request.Generation,
                request.CompletionReceipt,
                () => { });
        };
        workspace.PlaylistPresentationRefreshRequested += (_, request) =>
        {
            switch (request.Kind)
            {
                case PlaylistPresentationRefreshKind.SummaryData:
                    workspace.ApplyPlaylistSummaryDataRefresh(deferSummaryData, request.RebuildAsync);
                    break;
                case PlaylistPresentationRefreshKind.SummaryPresentation:
                    workspace.ApplyPlaylistSummaryPresentationRefresh(deferred: false);
                    break;
                case PlaylistPresentationRefreshKind.Tree:
                    workspace.ApplyPlaylistTreePresentationRefresh(request.Reason, deferred: false);
                    break;
                case PlaylistPresentationRefreshKind.HydrationCompleted:
                    if (request.HydrationSourceStore != null
                        && !workspace.IsCurrentPlaylistTreeNotificationSnapshot(
                            request.HydrationSourceStore,
                            request.HydrationSourceTables,
                            request.HydrationNotificationGeneration))
                    {
                        break;
                    }
                    if (!workspace.TryBeginPlaylistHydrationNotification(
                        request.HydrationSourceStore,
                        request.HydrationSourceTables,
                        request.HydrationNotificationGeneration,
                        request.HydrationCompletionReceipt))
                    {
                        break;
                    }
                    workspace.PublishPlaylistEntriesHydrationCompleted(
                        request.HydrationVersion,
                        request.HydrationSourceStore,
                        request.HydrationSourceTables,
                        request.HydrationNotificationGeneration,
                        request.HydrationCompletionReceipt);
                    workspace.ApplyPlaylistEntriesHydrationCompleted(
                        request.HydrationVersion,
                        deferred: false,
                        request.HydrationSourceStore,
                        request.HydrationSourceTables,
                        request.HydrationNotificationGeneration,
                        request.HydrationCompletionReceipt);
                    break;
                default:
                    throw new InvalidOperationException("Unknown playlist presentation refresh request kind.");
            }
        };
    }

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal sealed class FakeSettingsEditSession : ISettingsEditSession
    {
        public void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string historyIdentity)
        {
            environment.Commit(Values);
            Values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
            Save();
            Reload();
        }

        public BeMusicSeeker.Properties.Settings Values { get; set; } = new();

        public List<string> Calls { get; } = [];

        /// <summary>標準接続の保存拒否検証だけで、fixtureが所有するXMLへ実保存・再読込みを接続します。</summary>
        public bool PersistOwnedValues { get; init; }

        public int ReloadCount { get; private set; }

        public int SaveCount { get; private set; }

        public void Reload()
        {
            ReloadCount++;
            Calls.Add("reload");
            if (PersistOwnedValues) { Values.Reload(); }
        }

        public void Save()
        {
            SaveCount++;
            Calls.Add("save");
            if (PersistOwnedValues) { Values.Save(); }
        }
    }

}
