#nullable enable
using System;
using System.Collections;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Runtime;

internal sealed partial class ApplicationScenarioControl
{
    private Observation? observation;

    private sealed class Observation(long requestId, ScenarioObservationTarget target, IList source,
        MainChartListOperationContext context, long generation, ImmutableArray<object> identities, BMSTable? table, PlaylistFolderNode? folder)
    {
        internal readonly long RequestId = requestId;
        internal readonly ScenarioObservationTarget Target = target;
        internal readonly IList Source = source;
        internal readonly MainChartListOperationContext Context = context;
        internal readonly long Generation = generation;
        internal readonly ImmutableArray<object> Identities = identities;
        internal readonly ImmutableArray<BMSTable?> Parents = [.. identities.Select(identity => (identity as BMSTableEntry)?.parent)];
        internal readonly ImmutableArray<ScenarioObservedRow> Facts = [.. identities.Select(identity => CaptureIdentityFacts(source, identity))];
        internal readonly BMSTable? Table = table;
        internal readonly PlaylistFolderNode? Folder = folder;
        internal readonly TaskCompletionSource<ScenarioObservedResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action Unsubscribe = () => { };
        internal bool Captured;
        internal bool Awaited;
    }

    private static ScenarioTableState CaptureTableState(MainWindowViewModel model)
    {
        MainChartListViewModel list = model.MainChartList;
        IList source = list.Rows;
        return new(list.CurrentOperationContext.OperationSection.ToString(), list.CurrentOperationContext.SourceScope.ToString(),
            source.GetType().Name, list.RowsGeneration, source.Count, source is IChartListViewMetadata metadata ? metadata.RealizedRowCount : source.Count);
    }

    private ScenarioObservationRegistered RegisterObservation(MainWindowViewModel model, ScenarioRegisterObservationCommand request)
    {
        dispatcher.VerifyAccess();
        if (observation is not null) { throw new InvalidOperationException("An observation is already registered."); }
        IList source = model.MainChartList.Rows;
        ImmutableArray<string> ids = request.Target switch
        {
            ScenarioCellEditTarget cell => [cell.RowAutomationId],
            ScenarioPlaylistDropTarget drop => drop.RowAutomationIds,
            ScenarioPlaylistEntryDeleteTarget delete => delete.RowAutomationIds,
            _ => []
        };
        ImmutableArray<object>.Builder identities = ImmutableArray.CreateBuilder<object>();
        foreach (string id in ids)
        {
            if (source is not ICustomTableAutomationSource automation) { throw new InvalidOperationException("The source has no non-projecting identity."); }
            int index = automation.FindAutomationItem(id, -1);
            CustomTableAutomationItem item = automation.GetAutomationItem(index) ?? throw new InvalidOperationException("The target is absent.");
            if (automation.FindAutomationItem(id, index) >= 0) { throw new InvalidOperationException("The target is ambiguous."); }
            identities.Add(item.Identity);
        }
        if (request.Target is ScenarioCellEditTarget cellTarget
            && !(identities.Count == 1 && (identities[0] is BMSTableEntry && cellTarget.PropertyName == "memo"
                || identities[0] is OwnedChartToken && cellTarget.PropertyName == "Folder")))
        { throw new InvalidOperationException("Only playlist MEMO and normal FOLDER edits can be observed."); }
        if (request.Target is ScenarioPlaylistDropTarget && (identities.Count == 0 || identities.Any(identity => identity is not OwnedChartToken)))
        { throw new InvalidOperationException("Drop observation requires normal chart identities."); }
        if (request.Target is ScenarioPlaylistEntryDeleteTarget && (identities.Count == 0 || identities.Any(identity => identity is not BMSTableEntry)))
        { throw new InvalidOperationException("Entry deletion requires playlist entry identities."); }
        int? playlistId = request.Target switch
        {
            ScenarioPlaylistDropTarget drop => drop.PlaylistId,
            ScenarioPlaylistFolderDeleteTarget delete => delete.PlaylistId,
            _ => null
        };
        BMSTable? table = playlistId.HasValue ? model.PlaylistWorkspace.PlaylistTreeTables.Single(value => value.playlist_id == playlistId) : null;
        string? folderName = request.Target switch
        {
            ScenarioPlaylistDropTarget drop => drop.FolderName,
            ScenarioPlaylistFolderDeleteTarget delete => delete.FolderName,
            _ => null
        };
        PlaylistFolderNode? folder = folderName is not null
            ? (table ?? throw new InvalidOperationException("A table is required.")).FolderNodes.Single(value => !value.IsSpecial && value.FolderName == folderName) : null;
        var current = new Observation(request.RequestId, request.Target, source, model.MainChartList.CurrentOperationContext,
            model.MainChartList.RowsGeneration, identities.ToImmutable(), table, folder);
        void Fail(Exception failure)
        {
            current.Captured = true;
            current.Unsubscribe();
            current.Completion.TrySetException(failure);
        }
        void Started(ScenarioUiOperationInput input, Task task)
        {
            if (current.Captured) { return; }
            string? family = request.Target switch
            {
                ScenarioCellEditTarget => "cell-edit",
                ScenarioPlaylistDropTarget => "playlist-drop",
                ScenarioPlaylistFolderDeleteTarget => "playlist-folder-delete",
                ScenarioPlaylistEntryDeleteTarget => "playlist-entry-delete",
                _ => null
            };
            if (family != input.Family) { return; }
            try
            {
                if (!ReferenceEquals(input.Source, current.Source) || !ReferenceEquals(input.Context, current.Context)
                    || input.RowsGeneration != current.Generation) { throw new InvalidOperationException("The registered source expired before the operation."); }
                object[] actual = input.Rows.Select(row => row.Identity).ToArray();
                if (actual.Length != current.Identities.Length || actual.Where((identity, index) => !ReferenceEquals(identity, current.Identities[index])).Any())
                { throw new InvalidOperationException("A different row target started."); }
                if (input.Rows.Where((row, index) => !ReferenceEquals(row.Parent, current.Parents[index]) || row.Facts != current.Facts[index]).Any())
                { throw new InvalidOperationException("The registered entry parent changed."); }
                if (!ReferenceEquals(input.Table, current.Table) || !ReferenceEquals(input.Folder, current.Folder))
                { throw new InvalidOperationException("A different playlist target started."); }
                if (request.Target is ScenarioPlaylistDropTarget dropTarget
                    && (input.PlaylistId != dropTarget.PlaylistId || input.FolderName != dropTarget.FolderName)
                    || request.Target is ScenarioPlaylistFolderDeleteTarget deleteTarget
                    && (input.PlaylistId != deleteTarget.PlaylistId || input.FolderName != deleteTarget.FolderName))
                { throw new InvalidOperationException("The playlist domain target changed before input."); }
                if (request.Target is ScenarioCellEditTarget cell)
                {
                    MainChartListCellEditEndedEventArgs edit = input.Edit ?? throw new InvalidOperationException("Missing cell input.");
                    if (edit.Context.PropertyName != cell.PropertyName || edit.Text != cell.Text || edit.Commit != cell.Commit
                        || edit.Context.SourceScope != current.Context.SourceScope || edit.Context.OperationSection != current.Context.OperationSection)
                    { throw new InvalidOperationException("A different cell operation started."); }
                }
                current.Captured = true;
                current.Unsubscribe();
                _ = CompleteTaskObservationAsync(current, task, [.. input.Rows.Select(row => row.Facts)]);
            }
            catch (Exception failure) { Fail(failure); }
        }
        void KeywordStarted(string keyword, Task<RegularChartListEntryResult> task)
        {
            if (current.Captured || request.Target is not ScenarioKeywordTarget target || keyword != target.Keyword) { return; }
            if (model.PlaylistWorkspace.IsPlaylistDetailViewActive
                || model.MainChartList.CurrentOperationContext.OperationSection != current.Context.OperationSection
                || model.MainChartList.CurrentOperationContext.SourceScope != current.Context.SourceScope
                || !ReferenceEquals(model.MainChartList.Rows, current.Source)
                    && !(current.Source is ICustomTableSelectionIdentitySource before
                        && model.MainChartList.Rows is ICustomTableSelectionIdentitySource after && before.SharesSelectionIdentityScope(after)))
            { Fail(new InvalidOperationException("The search scope expired before capture.")); return; }
            current.Captured = true;
            current.Unsubscribe();
            _ = CompleteKeywordObservationAsync(current, model, task);
        }
        Guid? batchId = null;
        void InstallStarted(ScenarioInstallStarted input)
        {
            if (current.Captured || request.Target is not ScenarioInstallTarget install) { return; }
            try
            {
                if (!ReferenceEquals(model.MainChartList.Rows, current.Source)
                    || !ReferenceEquals(model.MainChartList.CurrentOperationContext, current.Context))
                { throw new InvalidOperationException("The install observation scope expired."); }
                if (!input.OriginalPaths.SequenceEqual(install.Paths, StringComparer.Ordinal)) { throw new InvalidOperationException("A different install input started."); }
                current.Captured = true;
                batchId = input.BatchId;
                model.PackageInstallWorkflow.BatchStarted -= InstallStarted;
            }
            catch (Exception failure) { Fail(failure); }
        }
        void InstallTerminated(PackageInstallBatchTerminal terminal)
        {
            if (batchId != terminal.BatchId) { return; }
            try
            {
                if (terminal.CommandResult is null && terminal.Failure is null) { throw new InvalidOperationException("The batch has no executed result."); }
                current.Unsubscribe();
                current.Completion.TrySetResult(new(current.Target, null, Install: new(0, terminal.BatchId, terminal.Generation,
                    CaptureInstall(terminal.CommandResult), terminal.Failure is null ? null : ScenarioFailure.FromException(terminal.Failure))));
            }
            catch (Exception failure) { Fail(failure); }
        }
        current.Unsubscribe = () =>
        {
            model.ScenarioOperationStarted -= Started;
            model.RegularKeywordRefreshStarted -= KeywordStarted;
            model.PackageInstallWorkflow.BatchStarted -= InstallStarted;
            model.PackageInstallWorkflow.BatchTerminated -= InstallTerminated;
        };
        model.ScenarioOperationStarted += Started;
        model.RegularKeywordRefreshStarted += KeywordStarted;
        model.PackageInstallWorkflow.BatchTerminated += InstallTerminated;
        model.PackageInstallWorkflow.BatchStarted += InstallStarted;
        observation = current;
        return new(request.RequestId, CaptureTableState(model));
    }

    private static ScenarioObservedRow CaptureIdentityFacts(IList source, object identity)
    {
        if (identity is BMSTableEntry entry)
        { return new("PlaylistEntry", null, entry.md5, entry.sha256, entry.playlist_id, entry.folder); }
        if (source is ChartListVirtualView view && view.NormalInput is { } normal && identity is OwnedChartToken token
            && normal.Snapshot.TryGetChart(token, out ChartFile chart, out _))
        { return new(chart.Kind.ToString(), chart.Path, chart.Md5, chart.Sha256, null, null); }
        throw new InvalidOperationException("The target has no supported non-projecting domain identity.");
    }

    private async Task CompleteTaskObservationAsync(Observation current, Task task, ImmutableArray<ScenarioObservedRow> rows)
    {
        ScenarioFailure? failure = null;
        try { await task.WaitAsync(stopping.Token); }
        catch (Exception exception) { failure = ScenarioFailure.FromException(exception); }
        current.Completion.TrySetResult(new(current.Target, failure, Rows: rows));
    }

    private async Task CompleteKeywordObservationAsync(Observation current, MainWindowViewModel model, Task<RegularChartListEntryResult> task)
    {
        try
        {
            RegularChartListEntryResult result = await task.WaitAsync(stopping.Token);
            current.Completion.TrySetResult(new(current.Target, null, new(result.RequestId, result.RowsGeneration, result.WasCommitted,
                model.MainChartList.LastCompletion.RequestId, model.MainChartList.RowsGeneration)));
        }
        catch (Exception failure) { current.Completion.TrySetResult(new(current.Target, ScenarioFailure.FromException(failure))); }
    }

    private async Task<ScenarioObservationCompleted> AwaitObservationAsync(ScenarioAwaitObservationCommand request)
    {
        Observation current = GetObservation(request.RegistrationRequestId);
        if (current.Awaited) { throw new InvalidOperationException("The observation is already being consumed."); }
        current.Awaited = true;
        try { return new(request.RequestId, current.RequestId, await current.Completion.Task.WaitAsync(stopping.Token)); }
        finally { current.Unsubscribe(); if (ReferenceEquals(observation, current)) { observation = null; } }
    }

    private ScenarioObservationCanceled CancelObservation(ScenarioCancelObservationCommand request)
    {
        Observation current = GetObservation(request.RegistrationRequestId);
        if (current.Awaited || current.Captured) { throw new InvalidOperationException("An observation can only be canceled before input and await."); }
        current.Unsubscribe();
        observation = null;
        return new(request.RequestId, request.RegistrationRequestId);
    }

    private Observation GetObservation(long requestId)
        => observation is { } current && current.RequestId == requestId ? current : throw new InvalidOperationException("The observation is absent or was consumed.");

    private void ClearObservation()
    {
        observation?.Unsubscribe();
        observation?.Completion.TrySetCanceled();
        observation = null;
    }
}
