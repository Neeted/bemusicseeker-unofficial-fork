#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
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
internal sealed partial class ApplicationScenarioControl : IAsyncDisposable
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

    /// <summary>明示test sessionだけで共有する、操作ごとに一回だけ固定入力を受け付ける窓口です。</summary>
    internal ApplicationScenarioDialogs Dialogs { get; } = new();

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
            if (session.PendingSchemaConfirmation) { control.Dialogs.ArmSchemaConfirmation(); }
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
        AttachInitialPlaylistSync(model);
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
                if (snapshot.Outcome == StartupInitializationOutcome.Succeeded && snapshot.CompletionPublished
                    && snapshot.IsInitializationCompleted && snapshot.Failure is null)
                {
                    await dispatcher.InvokeAsync(() => WaitForStartupAdditionalCompletionAsync(model.ProgressHub),
                        DispatcherPriority.Normal, stopping.Token).Task.Unwrap().ConfigureAwait(false);
                }
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

    /// <summary>成功した必須初期化の後、次の通常ライブラリ操作に先立ち、既存の起動追加仕事の実終端通知を待ちます。</summary>
    /// <remarks>必須初期化Taskは追加仕事の登録・開始後に返ります。実終端の投影だけを待ち、製品の操作可能時機や追加仕事の失敗結果は変更しません。</remarks>
    private async Task WaitForStartupAdditionalCompletionAsync(OperationProgressHubViewModel progress)
    {
        dispatcher.VerifyAccess();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName == nameof(OperationProgressHubViewModel.IsStartupBackgroundInitializationActive)
                && !progress.IsStartupBackgroundInitializationActive)
            {
                completed.TrySetResult();
            }
        };
        progress.PropertyChanged += changed;
        try
        {
            if (!progress.IsStartupBackgroundInitializationActive) { completed.TrySetResult(); }
            await completed.Task.WaitAsync(stopping.Token);
        }
        finally { progress.PropertyChanged -= changed; }
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
        finally
        {
            // 通信終端では観測待機だけを解放します。既存業務Taskの取消や結果補完は行いません。
            stopping.Cancel();
            await dispatcher.InvokeAsync(() => { ClearObservation(); ClearInitialPlaylistSync(); playback = null; }).Task.ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(ScenarioCommand request)
    {
        try
        {
            ScenarioMessage response;
            Action? afterAcknowledged = null;
            try
            {
                if (request is ScenarioEnvironmentAnswerCommand)
                {
                    afterAcknowledged = await dispatcher.InvokeAsync(() => Dialogs.PrepareEnvironmentAnswer()).Task.ConfigureAwait(false);
                    response = new ScenarioRecoveryAcknowledged(request.RequestId);
                }
                else if (request is ScenarioSchemaAnswerCommand answer)
                {
                    afterAcknowledged = await dispatcher.InvokeAsync(() => Dialogs.PrepareSchemaAnswer(answer.Approve)).Task.ConfigureAwait(false);
                    response = new ScenarioRecoveryAcknowledged(request.RequestId);
                }
                else
                {
                    response = await dispatcher.InvokeAsync(() => ExecuteAsync(request), DispatcherPriority.Normal, stopping.Token)
                        .Task.Unwrap().ConfigureAwait(false);
                }
            }
            catch (Exception failure) when (!stopping.IsCancellationRequested || KeepsResponseThroughShutdown(request))
            { response = new ScenarioCommandFailed(request.RequestId, ScenarioFailure.FromException(failure)); }
            // 復元・環境確定が通常終了を要求しても、同じ実Task終端の応答をDisposeが待つcommandから返します。
            await SendAsync(response, drainTerminal: KeepsResponseThroughShutdown(request)).ConfigureAwait(false);
            afterAcknowledged?.Invoke();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested && !KeepsResponseThroughShutdown(request)) { }
        catch (IOException) when (stopping.IsCancellationRequested && !KeepsResponseThroughShutdown(request)) { }
        catch (ObjectDisposedException) when (stopping.IsCancellationRequested && !KeepsResponseThroughShutdown(request)) { }
    }

    private async Task<ScenarioMessage> ExecuteAsync(ScenarioCommand request)
    {
        if (request is ScenarioEnvironmentConfirmationCommand)
        {
            await Dialogs.WaitForEnvironmentConfirmationAsync(stopping.Token);
            return new ScenarioRecoveryAcknowledged(request.RequestId);
        }
        if (request is ScenarioSchemaConfirmationCommand)
        {
            await Dialogs.WaitForSchemaConfirmationAsync(stopping.Token);
            return new ScenarioRecoveryAcknowledged(request.RequestId);
        }
        MainWindowViewModel model = viewModel ?? throw new InvalidOperationException("The scenario shell is not attached.");
        if (request is ScenarioRegisterObservationCommand register) { return RegisterObservation(model, register); }
        if (request is ScenarioAwaitObservationCommand observe) { return await AwaitObservationAsync(observe); }
        if (request is ScenarioCancelObservationCommand cancel) { return CancelObservation(cancel); }
        if (request is ScenarioTableStateCommand) { return new ScenarioTableStateCompleted(request.RequestId, CaptureTableState(model)); }
        if (request is ScenarioInitialPlaylistSyncCommand) { return await ConsumeInitialSyncAsync(request.RequestId); }
        if (request is ScenarioCloseCommand)
        {
            MainWindow mainWindow = window ?? throw new InvalidOperationException("The main window is unavailable.");
            // このメソッドはDispatcher上にあります。Closeの戻りを退出成功とは扱いません。
            mainWindow.Close();
            return new ScenarioCloseRequested(request.RequestId);
        }
        if (request is ScenarioSnapshotCommand)
        { return new ScenarioSnapshotCompleted(request.RequestId, CaptureSnapshot(model)); }
        // 起動完了前から受け付けます。更新確認の終端は初期化完了とは独立です。
        if (request is ScenarioStartupUpdateCommand) { return await ObserveStartupUpdateAsync(model, request.RequestId); }
        if (!model.IsInitializationCompleted)
        { throw new InvalidOperationException("The library is not ready for this command."); }
        if (request is ScenarioPlaybackCommand playbackCommand) { return await ExecutePlaybackAsync(model, playbackCommand); }
        if (request is ScenarioHistoryCommand history) { return await ExecuteHistoryAsync(model, history); }
        if (request is ScenarioSettingsSectionCommand section)
        {
            Task<ScenarioSettingsSectionObservation> operation = Dialogs.ArmSettingsSection(section);
            model.SettingDialog.RequestOpen();
            Dialogs.AssertConsumed();
            ScenarioSettingsSectionObservation settings = await operation;
            return new ScenarioSettingsSectionCompleted(request.RequestId, settings, CaptureSnapshot(model));
        }
        if (request is ScenarioPlaylistSqlCommand sql)
        {
            // 復元では実handlerが開始する通常終了と応答が重なるため、handler終端を待ってから応答し、
            // 制御窓口の破棄より先にその終端を回収します。
            Task operation = Dialogs.ArmPlaylistSql(sql.Path, sql.Restore);
            model.SettingDialog.RequestOpen();
            Dialogs.AssertConsumed();
            await operation;
            return new ScenarioRecoveryAcknowledged(request.RequestId);
        }
        if (request is ScenarioPlaylistImportCommand or ScenarioPlaylistCreateCommand or ScenarioPlaylistSelectCommand
            or ScenarioPlaylistEditCommand or ScenarioPlaylistAddCommand or ScenarioPlaylistFolderCommand
            or ScenarioPlaylistOrderCommand or ScenarioPlaylistExportCommand or ScenarioSettingsEditCommand or ScenarioPlaylistRemoveCommand)
        { return await ExecutePlaylistAsync(model, request); }
        if (request is ScenarioDropCommand drop) { return await DropAsync(model, drop); }
        if (request is ScenarioPackageViewCommand view) { return await NavigatePackagesAsync(model, view.RequestId, view.Pending); }
        if (request is ScenarioPendingCommand pending) { return await ApplyPendingAsync(model, pending); }
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

    private async Task SendAsync(ScenarioMessage message, bool drainTerminal = false)
    {
        CancellationToken cancellation = drainTerminal ? CancellationToken.None : stopping.Token;
        await writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try { await writer.WriteLineAsync(JsonSerializer.Serialize<ScenarioMessage>(message).AsMemory(), cancellation).ConfigureAwait(false); }
        finally { writeGate.Release(); }
    }

    /// <summary>
    /// 書込み側を破棄します。writerはAutoFlushのため、正常な書込みの後に未送信のデータはありません。
    /// 相手の切断後はPipeStream.Flushが状態検査だけで失敗し得るため、pipeが接続中でない場合のIOExceptionだけを捨てます。
    /// 判定はpipeの状態だけで行い、相手の終了を前提にしません。接続中のIOExceptionと他の例外はそのまま送出します。
    /// </summary>
    private void DisposeWriter()
    {
        try { writer.Dispose(); }
        catch (IOException) when (!pipe.IsConnected) { }
    }

    /// <summary>観測待機と未配送commandを先に取り消し、通信を回収します。業務Taskそのものは通常shutdownが先に終端します。</summary>
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        await dispatcher.InvokeAsync(() => { ClearObservation(); ClearInitialPlaylistSync(); playback = null; }).Task.ConfigureAwait(false);
        var failures = new List<Exception>();
        try { await (reading ?? Task.CompletedTask).ConfigureAwait(false); }
        catch (Exception failure) { failures.Add(failure); }
        // reader停止後に捕捉し、停止直前に配送された最後の要求も回収します。
        Task[] running;
        lock (commands) { running = [.. commands]; }
        try { await Task.WhenAll(running.Append(startupObservation ?? Task.CompletedTask).Append(initialSyncObservation ?? Task.CompletedTask)).ConfigureAwait(false); }
        catch (Exception failure) { failures.Add(failure); }
        finally
        {
            reader.Dispose();
            try { DisposeWriter(); }
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
