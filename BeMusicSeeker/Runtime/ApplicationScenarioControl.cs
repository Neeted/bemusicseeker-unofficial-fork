#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Runtime;

/// <summary>明示test sessionだけで、固定要求を通常のUI操作境界へ配送します。処理待機中もpipeを読み続けます。</summary>
internal sealed class ApplicationScenarioControl : IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly Dispatcher dispatcher;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly HashSet<Task> commands = [];
    private MainWindowViewModel? viewModel;
    private MainWindow? window;
    private Task? reading;
    private Task? startupObservation;

    private ApplicationScenarioControl(NamedPipeClientStream pipe, Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        this.pipe = pipe;
        reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
    }

    /// <summary>schema確認やmodalより先に接続します。接続失敗を通常起動へフォールバックしません。</summary>
    internal static async Task<ApplicationScenarioControl> ConnectAsync(ApplicationTestSession session, Dispatcher dispatcher)
    {
        var pipe = new NamedPipeClientStream(".", session.ControlPipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        ApplicationScenarioControl? control = null;
        try
        {
            await pipe.ConnectAsync().ConfigureAwait(false);
            // AutoFlushは設定時にもFlushするため、接続済みstreamにだけwriterを構成します。
            control = new ApplicationScenarioControl(pipe, dispatcher);
            await control.SendAsync(new ScenarioConnected(Environment.ProcessId)).ConfigureAwait(false);
            control.reading = control.ReadAsync();
            return control;
        }
        catch (Exception failure)
        {
            try
            {
                if (control is null) { pipe.Dispose(); }
                else { await control.DisposeAsync().ConfigureAwait(false); }
            }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            throw;
        }
    }

    /// <summary>通常構成が作ったVMと実Windowを一回だけ渡します。ここから初期化を開始しません。</summary>
    internal void Attach(MainWindowViewModel model, MainWindow mainWindow)
    {
        dispatcher.VerifyAccess();
        if (viewModel is not null) { throw new InvalidOperationException("The scenario shell is already attached."); }
        viewModel = model;
        window = mainWindow;
        startupObservation = ObserveStartupAsync(model);
    }

    /// <summary>ContentRenderedへ到達する前の構成失敗を、通常の起動失敗通知・終了より先に伝えます。</summary>
    internal Task ReportStartupFailureAsync(Exception failure)
        => SendAsync(new ScenarioCommandFailed(0, ScenarioFailure.FromException(failure)));

    private async Task ObserveStartupAsync(MainWindowViewModel model)
    {
        try
        {
            ScenarioMessage response;
            try
            {
                StartupInitializationResult result = await model.ShellActivationWorkflow.StartupCompletion.WaitAsync(stopping.Token).ConfigureAwait(false);
                ScenarioInitializationResult snapshot = await dispatcher.InvokeAsync(() => CaptureInitialization(result, model),
                    DispatcherPriority.Normal, stopping.Token).Task.ConfigureAwait(false);
                response = new ScenarioStartupCompleted(snapshot);
            }
            catch (Exception failure) when (!stopping.IsCancellationRequested)
            { response = new ScenarioCommandFailed(0, ScenarioFailure.FromException(failure)); }
            await SendAsync(response).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (IOException) when (stopping.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await reader.ReadLineAsync(stopping.Token).ConfigureAwait(false) is string line)
            {
                ScenarioCommand request = JsonSerializer.Deserialize<ScenarioCommand>(line)
                    ?? throw new InvalidDataException("A scenario command is required.");
                Task operation = HandleAsync(request);
                lock (commands) { commands.Add(operation); }
                _ = operation.ContinueWith(completed =>
                {
                    // 応答送信の失敗は終端回収まで残し、配送完了扱いで捨てません。
                    if (!completed.IsFaulted) { lock (commands) { commands.Remove(completed); } }
                    _ = completed.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (IOException) when (stopping.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { }
    }

    private async Task HandleAsync(ScenarioCommand request)
    {
        try
        {
            ScenarioMessage response;
            try
            {
                response = await dispatcher.InvokeAsync(() => ExecuteAsync(request), DispatcherPriority.Normal, stopping.Token)
                    .Task.Unwrap().ConfigureAwait(false);
            }
            catch (Exception failure) when (!stopping.IsCancellationRequested)
            { response = new ScenarioCommandFailed(request.RequestId, ScenarioFailure.FromException(failure)); }
            await SendAsync(response).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (IOException) when (stopping.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { }
    }

    private async Task<ScenarioMessage> ExecuteAsync(ScenarioCommand request)
    {
        MainWindowViewModel model = viewModel ?? throw new InvalidOperationException("The scenario shell is not attached.");
        if (request is ScenarioCloseCommand)
        {
            MainWindow mainWindow = window ?? throw new InvalidOperationException("The main window is unavailable.");
            // このメソッドはDispatcher上にあります。Closeの戻りを退出成功とは扱いません。
            mainWindow.Close();
            return new ScenarioCloseRequested(request.RequestId);
        }
        if (request is ScenarioSnapshotCommand)
        { return new ScenarioSnapshotCompleted(request.RequestId, CaptureSnapshot(model)); }
        if (!model.IsInitializationCompleted)
        { throw new InvalidOperationException("The library is not ready for this command."); }
        if (request is ScenarioReloadCommand)
        {
            StartupInitializationResult? result = await model.ReloadFileDiffAsync().WaitAsync(stopping.Token);
            return new ScenarioReloadCompleted(request.RequestId, result is null ? null : CaptureInitialization(result, model), CaptureSnapshot(model));
        }
        if (request is ScenarioKeywordCommand keyword)
        {
            if (model.MainChartList.CurrentOperationContext.OperationSection != MainViewOperationSection.Library
                || model.PlaylistWorkspace.IsPlaylistDetailViewActive)
            { throw new InvalidOperationException("This command requires the regular library view."); }
            if (string.Equals(model.ChartFilters.KeywordFilter, keyword.Keyword, StringComparison.Ordinal))
            { throw new InvalidOperationException("The keyword must change to produce a search request."); }
            Task<RegularChartListEntryResult>? refresh = null;
            void Started(string value, Task<RegularChartListEntryResult> task)
            {
                if (!string.Equals(value, keyword.Keyword, StringComparison.Ordinal))
                { throw new InvalidOperationException("A different keyword request started during the operation."); }
                refresh = task;
            }
            model.RegularKeywordRefreshStarted += Started;
            try { model.ChartFilters.KeywordFilter = keyword.Keyword; }
            finally { model.RegularKeywordRefreshStarted -= Started; }
            RegularChartListEntryResult result = await (refresh ?? throw new InvalidOperationException("The keyword did not start a regular refresh."))
                .WaitAsync(stopping.Token);
            if (!result.WasCommitted || result.RequestId == 0 || result.RowsGeneration == 0)
            { throw new InvalidOperationException("The keyword request did not publish its rows."); }
            ScenarioLibrarySnapshot snapshot = CaptureSnapshot(model);
            if (snapshot.ViewRequestId != result.RequestId || snapshot.RowsGeneration != result.RowsGeneration)
            { throw new InvalidOperationException("The keyword result was superseded before its snapshot was captured."); }
            return new ScenarioKeywordCompleted(request.RequestId, keyword.Keyword, result.RequestId, result.RowsGeneration, snapshot);
        }
        throw new InvalidDataException("Unsupported scenario command.");
    }

    private static ScenarioInitializationResult CaptureInitialization(StartupInitializationResult result, MainWindowViewModel model)
        => new(result.Outcome, result.OperationToken, result.CompletionPublished, model.IsInitializationCompleted,
            result.Failure is null ? null : ScenarioFailure.FromException(result.Failure));

    private static ScenarioLibrarySnapshot CaptureSnapshot(MainWindowViewModel model)
    {
        CatalogChartCollectionSnapshot? catalog = model.CaptureLibraryCatalog();
        ImmutableArray<ScenarioChart> charts = catalog is null ? []
            : catalog.BmsRows.Concat(catalog.BmsonRows).Select(CaptureChart).ToImmutableArray();
        return new(charts, model.MainChartList.Rows.Cast<object>().OfType<LibraryChartRow>()
            .Select(row => CaptureChart(row.Chart)).ToImmutableArray(),
            model.MainChartList.LastCompletion.RequestId, model.MainChartList.RowsGeneration);
    }

    private static ScenarioChart CaptureChart(ChartFile chart)
        => new(chart.Path, chart.Md5, chart.Sha256, chart.Title, chart.Kind.ToString(), chart.Tag, chart.Favorite, chart.AddDate);

    private async Task SendAsync(ScenarioMessage message)
    {
        await writeGate.WaitAsync(stopping.Token).ConfigureAwait(false);
        try { await writer.WriteLineAsync(JsonSerializer.Serialize<ScenarioMessage>(message).AsMemory(), stopping.Token).ConfigureAwait(false); }
        finally { writeGate.Release(); }
    }

    /// <summary>観測待機と未配送commandを先に取り消し、通信を回収します。業務Taskそのものは通常shutdownが先に終端します。</summary>
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        var failures = new List<Exception>();
        try { await (reading ?? Task.CompletedTask).ConfigureAwait(false); }
        catch (Exception failure) { failures.Add(failure); }
        // reader停止後に捕捉し、停止直前に配送された最後の要求も回収します。
        Task[] running;
        lock (commands) { running = [.. commands]; }
        try { await Task.WhenAll(running.Append(startupObservation ?? Task.CompletedTask)).ConfigureAwait(false); }
        catch (Exception failure) { failures.Add(failure); }
        finally
        {
            reader.Dispose();
            try { writer.Dispose(); }
            finally
            {
                pipe.Dispose();
                writeGate.Dispose();
                stopping.Dispose();
            }
        }
        if (failures.Count > 0) { throw new AggregateException(failures); }
    }
}
