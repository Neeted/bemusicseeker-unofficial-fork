using System;
using System.Collections;
using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public partial class MainWindowViewModel
{
    private sealed class PlaylistDetailTerminalRequest
    {
        internal PlaylistBuildRequest BuildRequest { get; set; }

        internal bool ReplaceSource { get; set; }

        internal List<PlaylistDetailSourceRow> SourceRows { get; set; }

        internal BMSTable CurrentTable { get; set; }

        internal string CurrentFolderName { get; set; }

        internal PlaylistFilterType CurrentFilterType { get; set; }

        internal IList ViewRows { get; set; }

        internal MainChartListColumnSelection ColumnSelection { get; set; }

        internal MainChartListRowsApplyRequest MainRowsRequest { get; set; }
    }

    private sealed class PlaylistDetailTerminalCommitResult
    {
        internal bool Applied { get; set; }

        internal List<PlaylistDetailSourceRow> PreviousSourceRows { get; set; }

        internal long PreviousSourceGenerationId { get; set; }

        internal long SourceGenerationId { get; set; }

        internal IList PreviousViewRows { get; set; }

        internal long PreviousViewGenerationId { get; set; }

        internal long ViewGenerationId { get; set; }

        internal int SourceRowsAlive { get; set; }

        internal MainChartListRowsApplyResult MainRowsApply { get; set; }

        internal PlaylistColumnPresentationCommit ColumnPresentationCommit { get; set; }
    }

    private static class PlaylistDetailTerminalTransition
    {
        internal static PlaylistDetailTerminalCommitResult TryCommit(
            MainWindowViewModel owner,
            PlaylistDetailTerminalRequest request)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }
            if (request?.BuildRequest == null || request.ViewRows == null || request.MainRowsRequest == null)
            {
                throw new ArgumentException("A complete playlist detail terminal request is required.", nameof(request));
            }
            if (request.ReplaceSource && request.SourceRows == null)
            {
                throw new ArgumentException("Source rows are required when replacing the playlist source.", nameof(request));
            }

            PlaylistDetailBuildState buildState = owner.playlistDetailBuildState;
            PlaylistViewState viewState = owner.playlistViewState;
            MainChartListViewModel mainChartList = owner.MainChartList;
            MainChartListPreparedRowsApply prepared = mainChartList.PrepareRowsApply(request.MainRowsRequest);
            MainChartListRowsCommit mainRowsCommit = null;
            var result = new PlaylistDetailTerminalCommitResult();
            bool stale = false;
            try
            {
                lock (buildState.SyncRoot)
                {
                    if (request.BuildRequest.RequestVersion != buildState.RequestVersion)
                    {
                        stale = true;
                    }
                    else
                    {
                        lock (viewState.SyncRoot)
                        {
                            mainRowsCommit = mainChartList.CommitPreparedRowsWithoutDisposal(prepared);

                            if (request.ReplaceSource)
                            {
                                result.PreviousSourceRows = viewState.Source.Rows;
                                result.PreviousSourceGenerationId = viewState.Source.GenerationId;
                                if (result.PreviousSourceRows != null)
                                {
                                    viewState.Source.PreviousRowsWeakReference = new WeakReference<List<PlaylistDetailSourceRow>>(result.PreviousSourceRows);
                                    viewState.Source.PreviousGenerationId = result.PreviousSourceGenerationId;
                                }
                                viewState.Source.Rows = request.SourceRows;
                                viewState.Source.CurrentTable = request.CurrentTable;
                                viewState.Source.CurrentFolderName = request.CurrentFolderName;
                                viewState.Source.CurrentFilterType = request.CurrentFilterType;
                                viewState.Source.LastBuiltLibraryIndexVersion = request.BuildRequest.Identity.LibraryIndexVersion;
                                viewState.Source.LastBuiltPlaylistRevision = request.BuildRequest.Identity.PlaylistRevision;
                                viewState.Source.LastBuiltScoreSnapshotVersion = request.BuildRequest.Identity.ScoreSnapshotVersion;
                                viewState.Source.LastBuiltChartInfoIndexVersion = request.BuildRequest.Identity.ChartInfoIndexVersion;
                                viewState.Source.CurrentIdentity = request.BuildRequest.Identity.SourceIdentity;
                                viewState.Source.GenerationId++;
                            }

                            result.PreviousViewRows = viewState.View.Rows;
                            result.PreviousViewGenerationId = viewState.View.GenerationId;
                            if (result.PreviousViewRows != null)
                            {
                                viewState.View.PreviousRowsWeakReference = new WeakReference<IList>(result.PreviousViewRows);
                                viewState.View.PreviousGenerationId = result.PreviousViewGenerationId;
                            }
                            viewState.View.Rows = request.ViewRows;
                            viewState.View.GenerationId++;
                            viewState.View.LastAppliedCount = request.ViewRows.Count;
                            viewState.View.CurrentIdentity = request.BuildRequest.Identity;

                            result.ColumnPresentationCommit = owner.PlaylistWorkspace.CommitColumnPresentationWithoutNotification(
                                request.ColumnSelection.PlaylistColumnSettingsVisibility,
                                request.ColumnSelection.PlaylistSummaryColumnsSettings);
                            if (request.ColumnSelection.AppliedMode.HasValue)
                            {
                                owner.lastAppliedMainColumnSettingMode = request.ColumnSelection.AppliedMode.Value;
                            }

                            result.SourceGenerationId = viewState.Source.GenerationId;
                            result.ViewGenerationId = viewState.View.GenerationId;
                            result.SourceRowsAlive = viewState.Source.Rows?.Count ?? 0;
                            result.Applied = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (mainRowsCommit == null)
                {
                    mainChartList.CancelPreparedRowsApply(prepared);
                    throw;
                }
                mainChartList.FailRowsReplacementPublish();
                throw new PlaylistDetailTerminalPublishException(ex);
            }

            if (stale)
            {
                mainChartList.CancelPreparedRowsApply(prepared);
                return result;
            }

            try
            {
                mainChartList.DisposeCommittedRows(mainRowsCommit);
            }
            catch (Exception ex)
            {
                mainChartList.FailRowsReplacementPublish();
                throw new PlaylistDetailTerminalPublishException(ex);
            }

            try
            {
                result.MainRowsApply = mainChartList.PublishRowsCommit(mainRowsCommit);
                owner.PlaylistWorkspace.PublishColumnPresentation(result.ColumnPresentationCommit);
            }
            catch (Exception ex)
            {
                throw new PlaylistDetailTerminalPublishException(ex);
            }
            return result;
        }
    }
}
