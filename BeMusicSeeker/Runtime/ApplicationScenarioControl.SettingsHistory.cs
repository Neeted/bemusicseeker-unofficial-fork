#nullable enable
using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Runtime;

internal sealed partial class ApplicationScenarioControl
{
    private async Task<ScenarioHistoryCompleted> ExecuteHistoryAsync(MainWindowViewModel model, ScenarioHistoryCommand command)
    {
        MainWindow shell = window ?? throw new InvalidOperationException("The shell is unavailable.");
        if (command.Action == ScenarioHistoryAction.ReloadScores)
        {
            await model.ReloadScoresOnlyAsync();
            return EmptyHistory(command.RequestId, CaptureSnapshot(model).Visible.Select(chart => chart.Path).ToImmutableArray());
        }
        if (command.Action is ScenarioHistoryAction.Library or ScenarioHistoryAction.Duplicates)
        {
            if (command.Action == ScenarioHistoryAction.Library) { await NavigateLibraryAsync(model); }
            else if (!await shell.NavigateScenarioDuplicatesAsync()) { throw new InvalidOperationException("Duplicate navigation was not adopted."); }
            return EmptyHistory(command.RequestId, CaptureSnapshot(model).Visible.Select(chart => chart.Path).ToImmutableArray());
        }
        if (command.Action == ScenarioHistoryAction.Lamp)
        {
            PlaylistLampViewerWindow viewer = await shell.PlaylistLampViewerWindows.TryOpenAsync(FindTable(model.PlaylistWorkspace, command.TableId))
                ?? throw new InvalidOperationException("The lamp window did not open.");
            try
            {
                if (!viewer.IsVisible || viewer.DataContext is not PlaylistLampViewerViewModel lamp)
                { throw new InvalidOperationException("The real lamp window is not visible."); }
                PlaylistLampAggregationResult result = lamp.CurrentResult ?? throw new InvalidOperationException("The lamp result is absent.");
                return EmptyHistory(command.RequestId, []) with
                {
                    LampState = result.State.ToString(),
                    LampTotal = result.Statistics.TotalCount,
                    LampPlayed = result.Statistics.PlayedCount,
                    LampSegments = result.ClearSegments.Select(segment => new ScenarioLampSegment(segment.ClearCategory?.ToString() ?? "", segment.Count)).ToImmutableArray()
                };
            }
            finally
            {
                viewer.Close();
                await shell.PlaylistLampViewerWindows.WaitForCompletionAsync();
            }
        }
        var started = new TaskCompletionSource<(long RequestId, long Revision, Task Operation)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Started(long request, long revision, Task operation) => started.TrySetResult((request, revision, operation));
        model.PlayHistory.ViewRefreshStarted += Started;
        try
        {
            if (command.Action == ScenarioHistoryAction.PeriodDay)
            {
                var day = DateTime.ParseExact(command.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                model.PlayHistory.ActivatePeriod(PlayHistoryPeriodRequest.CreateDay(day.Year, day.Month, day.Day, TimeZoneInfo.Local), model.ChartFilters.KeywordFilter);
            }
            else if (command.Action == ScenarioHistoryAction.PeriodRecent7Days)
            {
                var anchor = DateTimeOffset.FromUnixTimeSeconds(long.Parse(command.Text, CultureInfo.InvariantCulture));
                model.PlayHistory.ActivatePeriod(PlayHistoryPeriodRequest.Create(PlayHistoryPeriodKind.Recent7Days, anchor, TimeZoneInfo.Local), model.ChartFilters.KeywordFilter);
            }
            else if (command.Action == ScenarioHistoryAction.Keyword)
            {
                if (!model.PlayHistory.IsViewActive || model.ChartFilters.KeywordFilter == command.Text)
                { throw new InvalidOperationException("A changed keyword in the active history view is required."); }
                model.ChartFilters.KeywordFilter = command.Text;
            }
            else { throw new InvalidOperationException("Unknown history input."); }
            (long RequestId, long Revision, Task Operation) operation = await started.Task.WaitAsync(stopping.Token);
            await operation.Operation.WaitAsync(stopping.Token);
            PlayHistoryPresentationState state = model.PlayHistory.PresentationState;
            lock (state.SyncRoot)
            {
                PlayHistoryViewState current = state.CurrentView ?? throw new InvalidOperationException("History view is absent.");
                PlayHistoryAdoptedSummary adopted = state.AdoptedSummary ?? throw new InvalidOperationException("History summary was not adopted.");
                if (adopted.Failed || !state.IsFresh(current) || current.RequestId != operation.RequestId
                    || current.KeywordFilterRevision != operation.Revision
                    || model.MainChartList.CurrentOperationContext.OperationSection != MainViewOperationSection.PlayHistory)
                { throw new InvalidOperationException("The requested history result was not adopted."); }
                PlayHistoryPeriodSummary summary = adopted.Summary ?? throw new InvalidOperationException("History summary is absent.");
                var rows = model.MainChartList.Rows.Cast<object>().OfType<PlayHistoryRow>().Select(row => new ScenarioHistoryRow(
                    row.Provider.ToString(), row.SourcePath, row.HistoryId, row.PlayedAtUnix, row.RawHash, row.Path,
                    row.Finalized, row.PlaycountDelta, row.NewBestExscore, row.NewBestBp, row.NewBestClear?.ToString())).ToImmutableArray();
                return new(command.RequestId, current.RequestId, current.KeywordFilterRevision, current.Provider.ToString(), rows,
                    summary.RowCount, summary.FinalizedCount, summary.SummaryEligibleCount, summary.PlayCountAvailable, summary.PlaytimeAvailable, summary.PlaytimeSeconds,
                    [], "", 0, null, []);
            }
        }
        finally { model.PlayHistory.ViewRefreshStarted -= Started; }
    }

    private static ScenarioHistoryCompleted EmptyHistory(long requestId, ImmutableArray<string> paths)
        => new(requestId, 0, 0, "", [], 0, 0, 0, false, false, 0, paths, "", 0, null, []);
}
