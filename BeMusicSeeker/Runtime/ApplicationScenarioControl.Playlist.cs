#nullable enable
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Runtime;

internal sealed partial class ApplicationScenarioControl
{
    private async Task<ScenarioPlaylistCompleted> ExecutePlaylistAsync(MainWindowViewModel model, ScenarioCommand command)
    {
        MainWindow shell = window ?? throw new InvalidOperationException("The shell is unavailable.");
        PlaylistWorkspaceViewModel workspace = model.PlaylistWorkspace;
        ImmutableArray<ScenarioImportOutcome> imported = [];
        ScenarioSettingsEditObservation? savedSettings = null;
        switch (command)
        {
            case ScenarioPlaylistRemoveCommand input:
                await SelectSummaryAsync(workspace);
                Dialogs.ArmTableRemoval();
                await workspace.PlaylistRemovalWorkflow.RemoveSummaryRowsAsync(
                    workspace.PlaylistSummaryView.Where(row => row.TableRef.playlist_id == input.TableId).ToArray());
                break;
            case ScenarioPlaylistImportCommand input:
                imported = await ImportPlaylistAsync(workspace, input.Url);
                break;
            case ScenarioPlaylistCreateCommand input:
                Dialogs.ArmProperty(input.Name, input.Symbol);
                await shell.CreatePlaylistWithPropertiesAsync();
                Dialogs.AssertConsumed();
                break;
            case ScenarioPlaylistSelectCommand input:
                if (input.TableId is int tableId) { await SelectPlaylistAsync(model, FindTable(workspace, tableId)); }
                else { await SelectSummaryAsync(workspace); }
                break;
            case ScenarioPlaylistEditCommand input:
                PlaylistDetailRow row = model.MainChartList.Rows.Cast<object>().OfType<PlaylistDetailRow>()
                    .Single(item => item.Entry.md5 == input.Md5);
                var context = new MainChartListCellEditContext(row, input.Level ? nameof(PlaylistDetailRow.Level) : nameof(PlaylistDetailRow.memo),
                    row.IsOwned ? ChartOperationSourceScope.PlaylistOwned : ChartOperationSourceScope.PlaylistMissing, MainViewOperationSection.Playlist);
                if (!workspace.CanBeginDetailEdit(context)) { throw new InvalidOperationException("The requested detail edit is unavailable."); }
                workspace.BeginDetailEdit(context);
                await workspace.CompleteDetailEdit(new(context, input.Text, commit: true));
                break;
            case ScenarioPlaylistAddCommand input:
                if (input.Folder is null) { await NavigateLibraryAsync(model); }
                await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind, stopping.Token);
                var sourceView = (CustomTableView)shell.FindName("customTableView");
                sourceView.SelectRowsByPredicate(item => input.Md5s.Contains(item switch
                {
                    LibraryChartRow chart => chart.Chart.Md5,
                    PlaylistDetailRow detail => detail.Entry.md5,
                    _ => string.Empty
                }));
                object[] rows = sourceView.GetSelectedRowsSnapshot().ToArray();
                if (rows.Length != input.Md5s.Length) { throw new InvalidOperationException("The declared source rows are not displayed."); }
                BMSTable destination = FindTable(workspace, input.TableId);
                await workspace.AddRowsToFolderAsync(rows, destination, input.Folder is null ? null
                    : destination.FolderNodes.Single(folder => !folder.IsSpecial && folder.FolderName == input.Folder));
                break;
            case ScenarioPlaylistFolderCommand input:
                BMSTable table = FindTable(workspace, input.TableId);
                string[] before = table.FolderNodes.Where(folder => !folder.IsSpecial).Select(folder => folder.FolderName).ToArray();
                await workspace.CreateFolderAsync(table);
                PlaylistFolderNode created = table.FolderNodes.Single(folder => !folder.IsSpecial && !before.Contains(folder.FolderName));
                await workspace.RenameFolderAsync(table, created, input.Name);
                break;
            case ScenarioPlaylistOrderCommand input:
                Dialogs.ArmProperty(null, null, input.Folder, input.Down);
                await shell.OpenPlaylistPropertyDialogAsync(model, FindTable(workspace, input.TableId));
                Dialogs.AssertConsumed();
                break;
            case ScenarioPlaylistExportCommand input:
                Dialogs.ArmJson(input.HeaderPath, input.DataPath);
                long noticeRequestsBeforeExport = workspace.PlaylistOperationNoticeRequestCount;
                await workspace.ExportPlaylistTableAsync(FindTable(workspace, input.TableId));
                if (workspace.PlaylistOperationNoticeRequestCount != noticeRequestsBeforeExport)
                { throw new InvalidOperationException("The JSON export requested an operation notification."); }
                Dialogs.AssertConsumed();
                break;
            case ScenarioSettingsEditCommand input:
                Task<ScenarioSettingsEditObservation> settingsSaved = Dialogs.ArmSettings(input.FontSize, input.CandidatePath);
                model.SettingDialog.RequestOpen();
                Dialogs.AssertConsumed();
                savedSettings = await settingsSaved.WaitAsync(stopping.Token);
                break;
            default: throw new InvalidOperationException("Unknown playlist command.");
        }
        // Binding配送の順序だけを待ちます。成功は実保存値と表示値を別に照合します。
        await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind, stopping.Token);
        return new(command.RequestId, CapturePlaylistSnapshot(model, shell), imported, savedSettings);
    }

    private async Task<ImmutableArray<ScenarioImportOutcome>> ImportPlaylistAsync(PlaylistWorkspaceViewModel workspace, string url)
    {
        Task<ExternalPlaylistImportCompletion>? operation = null;
        void Started(Task<ExternalPlaylistImportCompletion> task) => operation = task;
        workspace.ExternalPlaylistImportStarted += Started;
        try
        {
            PlaylistImportStartResult start = workspace.TryStartPlaylistImport([new Uri(url, UriKind.Absolute)]);
            if (start.Status != PlaylistImportStartStatus.Started) { throw new InvalidOperationException("The import was not accepted: " + start.Status); }
            ExternalPlaylistImportCompletion completed = await (operation ?? throw new InvalidOperationException("The import Task was not observed.")).WaitAsync(stopping.Token);
            if (completed.Receipt is not { IsEmpty: true }) { throw new InvalidOperationException("The import did not end with an empty notification receipt."); }
            ExternalPlaylistImportQueueSummary summary = completed.Summary ?? throw new InvalidOperationException("The import ended without a summary.");
            return summary.Outcomes.Select(item => new ScenarioImportOutcome(item.Uri.AbsoluteUri, item.TableName, item.Kind.ToString())).ToImmutableArray();
        }
        finally
        {
            workspace.ExternalPlaylistImportStarted -= Started;
        }
    }

    private async Task SelectPlaylistAsync(MainWindowViewModel model, BMSTable table)
    {
        var started = new TaskCompletionSource<(int Version, Task Operation)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Started(int version, Task task) => started.TrySetResult((version, task));
        model.PlaylistDetailSelectionStarted += Started;
        try
        {
            model.PlaylistWorkspace.RequestDetailSelection(table);
            (int Version, Task Operation) request = await started.Task.WaitAsync(stopping.Token);
            await request.Operation.WaitAsync(stopping.Token);
            PlaylistWorkspaceViewModel workspace = model.PlaylistWorkspace;
            lock (workspace.DetailBuildState.SyncRoot)
            {
                if (request.Version <= 0 || workspace.DetailBuildState.CompletedRequestVersion < request.Version
                    || workspace.DetailBuildState.RequestVersion != request.Version)
                { throw new InvalidOperationException("The detail request did not reach its current terminal."); }
            }
            lock (workspace.DetailViewState.SyncRoot)
            {
                if (!ReferenceEquals(workspace.DetailViewState.View.CurrentIdentity?.Table, table))
                { throw new InvalidOperationException("The requested detail table was not adopted."); }
            }
        }
        finally { model.PlaylistDetailSelectionStarted -= Started; }
    }

    private async Task SelectSummaryAsync(PlaylistWorkspaceViewModel workspace)
    {
        var started = new TaskCompletionSource<(long Generation, Task Operation)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Started(long generation, Task task) => started.TrySetResult((generation, task));
        workspace.PlaylistSummaryBuildStarted += Started;
        try
        {
            workspace.RequestSummarySelection();
            (long Generation, Task Operation) request = await started.Task.WaitAsync(stopping.Token);
            await request.Operation.WaitAsync(stopping.Token);
            if (workspace.AppliedPlaylistSummaryDataGeneration != request.Generation || !workspace.IsPlaylistSummaryMode)
            { throw new InvalidOperationException("The requested summary generation was not adopted."); }
        }
        finally { workspace.PlaylistSummaryBuildStarted -= Started; }
    }

    private async Task NavigateLibraryAsync(MainWindowViewModel model)
    {
        var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Requested(object? sender, RegularChartTreeNavigationPresentationRequestedEventArgs args) => started.TrySetResult(args.Presentation);
        model.RegularChartList.TreeNavigationPresentationRequested += Requested;
        try
        {
            if (!model.RegularChartList.NavigateTree(null)) { throw new InvalidOperationException("The library navigation was rejected."); }
            await (await started.Task.WaitAsync(stopping.Token)).WaitAsync(stopping.Token);
            if (model.PlaylistWorkspace.IsPlaylistDetailViewActive) { throw new InvalidOperationException("The library view was not adopted."); }
        }
        finally { model.RegularChartList.TreeNavigationPresentationRequested -= Requested; }
    }

    private static BMSTable FindTable(PlaylistWorkspaceViewModel workspace, int id)
        => workspace.PlaylistTreeTables.Single(table => table.playlist_id == id);

    private static ScenarioPlaylistSnapshot CapturePlaylistSnapshot(MainWindowViewModel model, MainWindow shell)
    {
        PlaylistWorkspaceViewModel workspace = model.PlaylistWorkspace;
        int version;
        lock (workspace.DetailBuildState.SyncRoot) { version = workspace.DetailBuildState.CompletedRequestVersion; }
        var main = (CustomTableView)shell.FindName("customTableView");
        var summary = (CustomTableView)shell.FindName("customTablePlaylistSummary");
        return new(workspace.PlaylistTreeTables.Select(table => new ScenarioPlaylistTable(
                table.playlist_id ?? throw new InvalidOperationException("An unregistered draft was published."),
                table.name, table.symbol, table.is_external_sync == true, [.. table.Folder_order ?? []],
                [.. table.FolderNodes.Where(folder => !folder.IsSpecial).Select(folder => folder.FolderName)])).ToImmutableArray(),
            model.MainChartList.Rows.Cast<object>().OfType<PlaylistDetailRow>().Select(row => new ScenarioPlaylistRow(
                row.Entry.playlist_id ?? throw new InvalidOperationException("A detail row has no table identity."),
                row.Entry.md5, row.sha256, row.Title, row.Artist, row.Folder, row.Level, row.memo, row.IsOwned)).ToImmutableArray(),
            workspace.PlaylistSummaryView.Select(row => row.TableRef.playlist_id ?? throw new InvalidOperationException("A summary has no table identity.")).ToImmutableArray(),
            version, workspace.AppliedPlaylistSummaryDataGeneration, main.TextFontSize, summary.TextFontSize);
    }
}
