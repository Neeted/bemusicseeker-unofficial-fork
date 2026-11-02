using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.TestSupport.Scenarios;

/// <summary>実製品processと専有pipeを所有し、固定要求の実結果を対応する呼出元へ返します。</summary>
/// <remarks>通信相関だけを管理し、業務成功・ready・保存結果を作りません。各待機はcaseから渡す同じ絶対期限を使います。</remarks>
internal sealed class ApplicationScenarioDriver : IAsyncDisposable
{
    private readonly NamedPipeServerStream pipe;
    private StreamReader reader;
    private StreamWriter writer;
    private readonly OwnedProcess process;
    private readonly string diagnosticsDirectory;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<ScenarioMessage>> requests = new();
    private TaskCompletionSource<ScenarioConnected> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<ScenarioInitializationResult> startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task reading;
    private Process? successor;
    private Task<int> peerExit;
    private bool restartExpected;
    private bool rebound;
    private DateTime? successorStartTime;
    private long nextRequestId;
    private bool closeRequested;
    private Exception? readFailure;
    private Exception? unansweredRequestFailure;
    private readonly object exitExpectationGate = new();
    private bool naturalExitExpected;
    private bool readerTerminated;

    private ApplicationScenarioDriver(NamedPipeServerStream pipe, OwnedProcess process, string diagnosticsDirectory)
    {
        this.pipe = pipe;
        this.process = process;
        peerExit = process.RootExit;
        this.diagnosticsDirectory = diagnosticsDirectory;
        reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        reading = ReadAsync();
    }

    /// <summary>保持している実processの識別子です。</summary>
    internal int ProcessId => successor?.Id ?? process.ProcessId;

    /// <summary>後継helloで保持した実handleの開始時刻です。wireの申告値は使いません。</summary>
    internal DateTime? SuccessorStartTime => successorStartTime;

    /// <summary>readyと独立した実process退出です。未知の切断を正常終了へ読み替えません。</summary>
    internal Task<int> RootExit => process.RootExit;

    /// <summary>初期化前から用意したpipeへ、指定された実製品exeだけを起動します。別buildの探索は行いません。</summary>
    internal static async Task<ApplicationScenarioDriver> StartAsync(string executablePath, string profileDirectory,
        string manifestPath, string caseId, string updateManifestUrl, ProcessDeadline deadline, bool pendingSchemaConfirmation = false)
    {
        string executable = Path.GetFullPath(executablePath);
        string profile = Path.GetFullPath(profileDirectory);
        if (!File.Exists(executable)) { throw new FileNotFoundException("実製品fixtureがありません。", executable); }
        string assets = Path.GetDirectoryName(executable) ?? throw new InvalidOperationException("実行体の親がありません。");
        string pipeName = "bms-scenario-" + Guid.NewGuid().ToString("N");
        string runId = Guid.NewGuid().ToString("N");
        string diagnosticsDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))
            ?? throw new InvalidOperationException("manifestの親がありません。"), "process-" + runId);
        Directory.CreateDirectory(profile);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            profileDirectory = profile,
            runId,
            caseId,
            controlPipeName = pipeName,
            languageDirectory = Path.Combine(assets, "lang"),
            providers = new { fileEnumeration = "file-system" },
            updateManifestUrl,
            pendingSchemaConfirmation
        }), new UTF8Encoding(false));
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        OwnedProcess? owned = null;
        ApplicationScenarioDriver? driver = null;
        try
        {
            owned = OwnedProcess.Start(new ProcessStartRequest(executable, ["--test-session", Path.GetFullPath(manifestPath)], profile));
            using var cancellation = new CancellationTokenSource(deadline.Remaining);
            Task connection = server.WaitForConnectionAsync(cancellation.Token);
            Task first = await Task.WhenAny(connection, owned.RootExit).WaitAsync(deadline.Remaining).ConfigureAwait(false);
            if (first == owned.RootExit)
            { throw new InvalidOperationException($"制御接続前に製品が終了しました: {await owned.RootExit.ConfigureAwait(false)}"); }
            await connection.ConfigureAwait(false);
            driver = new ApplicationScenarioDriver(server, owned, diagnosticsDirectory);
            ScenarioConnected hello = await driver.AwaitOrExitAsync(driver.connected.Task, deadline).ConfigureAwait(false);
            if (hello.ProcessId != owned.ProcessId)
            { throw new InvalidDataException("制御pipeのprocessと所有processが一致しません。"); }
            return driver;
        }
        catch (Exception primary)
        {
            try
            {
                if (driver is not null) { await driver.DisposeAsync().ConfigureAwait(false); }
                else
                {
                    server.Dispose();
                    if (owned is not null) { await owned.DisposeAsync().ConfigureAwait(false); }
                }
            }
            catch (Exception cleanup) { primary.Data["ScenarioCleanupFailure"] = cleanup; }
            if (driver is null && owned is not null)
            {
                try { await SaveProcessDiagnosticsAsync(owned, diagnosticsDirectory).ConfigureAwait(false); }
                catch (Exception diagnosticFailure) { primary.Data["ScenarioDiagnosticsFailure"] = diagnosticFailure; }
            }
            throw;
        }
    }

    /// <summary>ContentRendered起点の実初期化結果を待ち、成功時は次の通常操作に先立ち起動追加仕事の実終端も待ちます。正常Task完了だけでは成功にしません。</summary>
    internal Task<ScenarioInitializationResult> WaitForStartupAsync(ProcessDeadline deadline)
        => AwaitOrExitAsync(startup.Task, deadline);

    /// <summary>起動前に宣言した実schema確認への到達を待ちます。</summary>
    internal Task<ScenarioRecoveryAcknowledged> WaitForSchemaConfirmationAsync(ProcessDeadline deadline)
        => RequestAsync<ScenarioRecoveryAcknowledged>(id => new ScenarioSchemaConfirmationCommand(id), deadline);

    /// <summary>必要な自然終了の期待登録後に呼びます。回答は製品側ACK送信後だけ解放されます。</summary>
    internal Task<ScenarioRecoveryAcknowledged> AnswerSchemaAsync(bool approve, ProcessDeadline deadline)
        => RequestAsync<ScenarioRecoveryAcknowledged>(id => new ScenarioSchemaAnswerCommand(id, approve), deadline);

    /// <summary>実設定画面のSQL操作Task終端を待ちます。復元時の退出は別に期待登録・検査します。</summary>
    internal Task<ScenarioRecoveryAcknowledged> PlaylistSqlAsync(string path, bool restore, ProcessDeadline deadline)
        => RequestAsync<ScenarioRecoveryAcknowledged>(id => new ScenarioPlaylistSqlCommand(id, path, restore), deadline);

    /// <summary>実サマリー削除の確認・保存・必要出力を通します。</summary>
    internal Task<ScenarioPlaylistCompleted> RemovePlaylistAsync(int tableId, ProcessDeadline deadline)
        => RequestAsync<ScenarioPlaylistCompleted>(id => new ScenarioPlaylistRemoveCommand(id, tableId), deadline);

    /// <summary>現在の実catalog・表示を読取り専用で取得します。</summary>
    internal Task<ScenarioSnapshotCompleted> SnapshotAsync(ProcessDeadline deadline)
        => RequestAsync<ScenarioSnapshotCompleted>(id => new ScenarioSnapshotCommand(id), deadline);

    /// <summary>通常検索setterと、その変更が開始した実Task・採用世代を待ちます。</summary>
    internal Task<ScenarioKeywordCompleted> SetKeywordAsync(string keyword, ProcessDeadline deadline)
        => RequestAsync<ScenarioKeywordCompleted>(id => new ScenarioKeywordCommand(id, keyword), deadline);

    /// <summary>通常差分リロードの受付・実結果・必須表示を受け取ります。</summary>
    internal Task<ScenarioReloadCompleted> ReloadAsync(ProcessDeadline deadline)
        => RequestAsync<ScenarioReloadCompleted>(id => new ScenarioReloadCommand(id), deadline);

    /// <summary>実Dropの入力確保と、同じBatchIdの導入終端を待ちます。</summary>
    internal Task<ScenarioDropCompleted> DropAsync(IEnumerable<string> paths, ProcessDeadline deadline)
        => RequestAsync<ScenarioDropCompleted>(id => new ScenarioDropCommand(id, [.. paths]), deadline);

    /// <summary>通常の保留・新規一覧へ移動し、表示Task終端後の実行を読みます。</summary>
    internal Task<ScenarioPackageViewCompleted> NavigatePackagesAsync(bool pending, ProcessDeadline deadline)
        => RequestAsync<ScenarioPackageViewCompleted>(id => new ScenarioPackageViewCommand(id, pending), deadline);

    /// <summary>実保留行を対象に既存操作を行い、元の結果と一覧移動を返します。明示した場合だけ消失先の実報告へ一回OKを入力します。</summary>
    internal Task<ScenarioPendingCompleted> ApplyPendingAsync(ScenarioPendingAction action, IEnumerable<string> chartPaths,
        ProcessDeadline deadline, string? destination = null, bool expectInstallFailureReport = false)
        => RequestAsync<ScenarioPendingCompleted>(id => new ScenarioPendingCommand(id, action, [.. chartPaths], destination, expectInstallFailureReport), deadline);

    /// <summary>既存の外部表インポートの実Task終端と対象別結果を取得します。</summary>
    internal Task<ScenarioPlaylistCompleted> ImportPlaylistAsync(string url, ProcessDeadline deadline)
        => RequestAsync<ScenarioPlaylistCompleted>(id => new ScenarioPlaylistImportCommand(id, url), deadline);

    /// <summary>実propertyの入力とOKから登録・閉鎖まで待ちます。</summary>
    internal Task<ScenarioPlaylistCompleted> CreatePlaylistAsync(string name, string symbol, ProcessDeadline deadline)
        => RequestAsync<ScenarioPlaylistCompleted>(id => new ScenarioPlaylistCreateCommand(id, name, symbol), deadline);

    /// <summary>通常ツリー選択が開始する実表示Taskと採用を待ちます。</summary>
    internal Task<ScenarioPlaylistCompleted> SelectPlaylistAsync(int? tableId, ProcessDeadline deadline)
        => RequestAsync<ScenarioPlaylistCompleted>(id => new ScenarioPlaylistSelectCommand(id, tableId), deadline);

    /// <summary>表示中の実詳細行のMEMOまたはLevelを確定します。</summary>
    internal Task<ScenarioPlaylistCompleted> EditPlaylistAsync(string md5, string text, bool level, ProcessDeadline deadline)
        => RequestAsync<ScenarioPlaylistCompleted>(id => new ScenarioPlaylistEditCommand(id, md5, text, level), deadline);

    /// <summary>通常行の追加または表示中の詳細行のフォルダ移動を実行します。</summary>
    internal Task<ScenarioPlaylistCompleted> AddPlaylistRowsAsync(int tableId, IEnumerable<string> md5s, string? folder, ProcessDeadline deadline)
        => RequestAsync<ScenarioPlaylistCompleted>(id => new ScenarioPlaylistAddCommand(id, tableId, [.. md5s], folder), deadline);

    /// <summary>既存作成・改名を通じてフォルダ名を保存します。</summary>
    internal Task<ScenarioPlaylistCompleted> CreatePlaylistFolderAsync(int tableId, string name, ProcessDeadline deadline)
        => RequestAsync<ScenarioPlaylistCompleted>(id => new ScenarioPlaylistFolderCommand(id, tableId, name), deadline);

    /// <summary>実propertyの上下操作一回とOKを通します。</summary>
    internal Task<ScenarioPlaylistCompleted> MovePlaylistFolderAsync(int tableId, string folder, bool down, ProcessDeadline deadline)
        => RequestAsync<ScenarioPlaylistCompleted>(id => new ScenarioPlaylistOrderCommand(id, tableId, folder, down), deadline);

    /// <summary>宣言済み二pathへ実JSON出力と通知session終端を待ちます。</summary>
    internal Task<ScenarioPlaylistCompleted> ExportPlaylistAsync(int tableId, string header, string data, ProcessDeadline deadline)
        => RequestAsync<ScenarioPlaylistCompleted>(id => new ScenarioPlaylistExportCommand(id, tableId, header, data), deadline);

    /// <summary>通常設定画面のfont即時commitとCloseによるpath候補取消を通します。</summary>
    internal Task<ScenarioPlaylistCompleted> EditSettingsAsync(double fontSize, string candidatePath, ProcessDeadline deadline)
        => RequestAsync<ScenarioPlaylistCompleted>(id => new ScenarioSettingsEditCommand(id, fontSize, candidatePath), deadline);

    /// <summary>履歴の既存実Taskと採用後の表示・実ランプ結果を取得します。</summary>
    internal Task<ScenarioHistoryCompleted> HistoryAsync(ScenarioHistoryAction action, ProcessDeadline deadline, string text = "", int tableId = 0)
        => RequestAsync<ScenarioHistoryCompleted>(id => new ScenarioHistoryCommand(id, action, text, tableId), deadline);

    /// <summary>実設定画面の節操作Taskと、候補・公開・fresh providerの別々の値を返します。</summary>
    internal Task<ScenarioSettingsSectionCompleted> SettingsSectionAsync(ScenarioSettingsSectionAction action, string path,
        string configPath, ProcessDeadline deadline)
        => RequestAsync<ScenarioSettingsSectionCompleted>(id => new ScenarioSettingsSectionCommand(id, action, path, configPath), deadline);

    /// <summary>環境確認の実到達後、ACK送信完了から回答を解放し、同一Jobの後継へ一回だけ接続します。</summary>
    internal async Task<ScenarioSettingsSectionCompleted> RestartEnvironmentAsync(string songDb, string configPath, ProcessDeadline deadline)
    {
        lock (exitExpectationGate)
        {
            if (restartExpected || rebound || naturalExitExpected || closeRequested || readerTerminated || readFailure is not null || unansweredRequestFailure is not null || peerExit.IsCompleted)
            { throw new InvalidOperationException("再起動の期待を登録できません。"); }
            restartExpected = true;
        }
        Task<ScenarioSettingsSectionCompleted> operation = SettingsSectionAsync(ScenarioSettingsSectionAction.DetailsRestart, songDb, configPath, deadline);
        try
        {
            Task<ScenarioRecoveryAcknowledged> confirmation = RequestAsync<ScenarioRecoveryAcknowledged>(id => new ScenarioEnvironmentConfirmationCommand(id), deadline);
            if (await Task.WhenAny(confirmation, operation).ConfigureAwait(false) == operation)
            {
                await operation.ConfigureAwait(false);
                throw new InvalidOperationException("環境操作が再起動確認に到達せず終端しました。");
            }
            await confirmation.ConfigureAwait(false);
            await RequestAsync<ScenarioRecoveryAcknowledged>(id => new ScenarioEnvironmentAnswerCommand(id), deadline).ConfigureAwait(false);
            ScenarioSettingsSectionCompleted result = await operation.ConfigureAwait(false);
            await RebindAfterRestartAsync(deadline).ConfigureAwait(false);
            return result;
        }
        catch
        {
            // 相関要求の実故障は保持し、終端しているTaskの例外だけ回収します。未終端業務を成功補完しません。
            if (operation.IsFaulted) { _ = operation.Exception; }
            throw;
        }
    }

    private async Task RebindAfterRestartAsync(ProcessDeadline deadline)
    {
        if (await process.WaitForRootExitAsync(deadline).ConfigureAwait(false) != 0)
        { throw new InvalidOperationException("再起動元の終了コードが0ではありません。"); }
        await reading.WaitAsync(deadline.Remaining).ConfigureAwait(false);
        if (readFailure is not null || unansweredRequestFailure is not null)
        { throw new IOException("再起動元の制御通信が失敗しました。", readFailure ?? unansweredRequestFailure); }
        reader.Dispose();
        writer.Dispose();
        pipe.Disconnect();
        using var cancellation = new CancellationTokenSource(deadline.Remaining);
        await pipe.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
        reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        // hello受信までは初代exitを監視しません。保持handleを採用してから通常要求の退出監視へ戻します。
        string line = await reader.ReadLineAsync(cancellation.Token).ConfigureAwait(false) ?? throw new EndOfStreamException("後継helloがありません。");
        ScenarioConnected hello = JsonSerializer.Deserialize<ScenarioMessage>(line) as ScenarioConnected
            ?? throw new InvalidDataException("後継の先頭応答がhelloではありません。");
        AdoptSuccessor(hello);
        connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connected.SetResult(hello);
        startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (exitExpectationGate) { restartExpected = false; readerTerminated = false; }
        reading = ReadAsync();
    }

    private void AdoptSuccessor(ScenarioConnected hello)
    {
        if (rebound) { throw new InvalidOperationException("後継の再束縛は一回だけです。"); }
        var candidate = Process.GetProcessById(hello.ProcessId);
        try
        {
            _ = candidate.Handle;
            DateTime started = candidate.StartTime;
            if (candidate.HasExited || hello.ProcessId == process.ProcessId || !process.ContainsProcess(candidate))
            { throw new InvalidDataException("後継helloのprocessは所有Jobの生存する後継ではありません。"); }
            successor = candidate;
            successorStartTime = started;
            rebound = true;
            peerExit = ObservePeerExitAsync(candidate);
        }
        catch { candidate.Dispose(); throw; }
    }

    private static async Task<int> ObservePeerExitAsync(Process peer)
    {
        await peer.WaitForExitAsync().ConfigureAwait(false);
        return peer.ExitCode;
    }

    /// <summary>実Window.Closeを要求し、root・両出力pipe・全所有子孫の正常退出まで待ちます。</summary>
    internal async Task CloseAsync(ProcessDeadline deadline)
    {
        await RequestCloseAsync(deadline).ConfigureAwait(false);
        int peerCode = await peerExit.WaitAsync(deadline.Remaining).ConfigureAwait(false);
        ProcessCompletion completion = await process.WaitForExitAsync(deadline).ConfigureAwait(false);
        if (peerCode != 0 || completion.ExitCode != 0)
        { throw new InvalidOperationException($"製品の終了コード: {completion.ExitCode}\n{completion.StandardError}"); }
    }

    /// <summary>実UI入力より先に自然終了の期待期間を同期登録します。既知の終了・通信失敗を追認しません。</summary>
    internal void ExpectNaturalExit()
    {
        lock (exitExpectationGate)
        {
            if (naturalExitExpected || restartExpected || closeRequested || readerTerminated || readFailure is not null || unansweredRequestFailure is not null || process.RootExit.IsCompleted)
            { throw new InvalidOperationException("自然終了の期待を登録できません。"); }
            naturalExitExpected = true;
        }
    }

    /// <summary>Closeを送らず、root・子孫・両出力・readerの実終端を同一期限で待ちます。</summary>
    internal async Task WaitForExpectedExitAsync(ProcessDeadline deadline)
    {
        lock (exitExpectationGate)
        {
            if (!naturalExitExpected) { throw new InvalidOperationException("自然終了の期待がありません。"); }
        }
        ProcessCompletion completion = await process.WaitForExitAsync(deadline).ConfigureAwait(false);
        await reading.WaitAsync(deadline.Remaining).ConfigureAwait(false);
        if (readFailure is not null) { throw new IOException("自然終了中の制御通信が失敗しました。", readFailure); }
        if (unansweredRequestFailure is not null) { throw new IOException("未応答の要求が失敗しました。", unansweredRequestFailure); }
        if (completion.ExitCode != 0)
        { throw new InvalidOperationException($"製品の終了コード: {completion.ExitCode}\n{completion.StandardError}"); }
    }

    /// <summary>実UI入力前に対象と購読を登録し、ACKの通信番号を待機に使います。</summary>
    internal Task<ScenarioObservationRegistered> RegisterObservationAsync(ScenarioObservationTarget target, ProcessDeadline deadline)
        => RequestAsync<ScenarioObservationRegistered>(id => new ScenarioRegisterObservationCommand(id, target), deadline);

    /// <summary>元の登録を別通信番号で一回だけ消費します。</summary>
    internal Task<ScenarioObservationCompleted> AwaitObservationAsync(long registrationRequestId, ProcessDeadline deadline)
        => RequestAsync<ScenarioObservationCompleted>(id => new ScenarioAwaitObservationCommand(id, registrationRequestId), deadline);

    /// <summary>入力前・待機前の登録を破棄します。開始後の失敗回収には使いません。</summary>
    internal Task<ScenarioObservationCanceled> CancelObservationAsync(long registrationRequestId, ProcessDeadline deadline)
        => RequestAsync<ScenarioObservationCanceled>(id => new ScenarioCancelObservationCommand(id, registrationRequestId), deadline);

    /// <summary>行・選択の投影を発生させずに現在の表示状態を読みます。</summary>
    internal Task<ScenarioTableStateCompleted> TableStateAsync(ProcessDeadline deadline)
        => RequestAsync<ScenarioTableStateCompleted>(id => new ScenarioTableStateCommand(id), deadline);

    /// <summary>Attachから捕捉した初回同期の実Taskとreceiptだけを一回消費します。</summary>
    internal Task<ScenarioInitialPlaylistSyncCompleted> WaitForInitialPlaylistSyncAsync(ProcessDeadline deadline)
        => RequestAsync<ScenarioInitialPlaylistSyncCompleted>(id => new ScenarioInitialPlaylistSyncCommand(id), deadline);

    private async Task RequestCloseAsync(ProcessDeadline deadline)
    {
        closeRequested = true;
        await WriteAsync(new ScenarioCloseCommand(Interlocked.Increment(ref nextRequestId)), deadline).ConfigureAwait(false);
    }

    private async Task<T> RequestAsync<T>(Func<long, ScenarioCommand> create, ProcessDeadline deadline) where T : ScenarioMessage
    {
        if (readFailure is not null) { throw new InvalidOperationException("制御通信が既に失敗しています。", readFailure); }
        long id = Interlocked.Increment(ref nextRequestId);
        var completion = new TaskCompletionSource<ScenarioMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!requests.TryAdd(id, completion)) { throw new InvalidOperationException("通信要求番号が重複しました。"); }
        try
        {
            await WriteAsync(create(id), deadline).ConfigureAwait(false);
            ScenarioMessage response = await AwaitOrExitAsync(completion.Task, deadline).ConfigureAwait(false);
            if (response is ScenarioCommandFailed failed) { throw DescribeFailure(failed.Failure); }
            return response as T ?? throw new InvalidDataException("要求と異なる型の応答を受け取りました。");
        }
        catch (Exception failure)
        {
            // root退出がEOFより先でも、辞書からの回収によって未応答の失敗を消しません。
            if (!completion.Task.IsCompletedSuccessfully) { Interlocked.CompareExchange(ref unansweredRequestFailure, failure, null); }
            throw;
        }
        finally { requests.TryRemove(id, out _); }
    }

    private async Task WriteAsync(ScenarioCommand command, ProcessDeadline deadline)
    {
        using var cancellation = new CancellationTokenSource(deadline.Remaining);
        await writeGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try { await writer.WriteLineAsync(JsonSerializer.Serialize<ScenarioCommand>(command).AsMemory(), cancellation.Token).ConfigureAwait(false); }
        finally { writeGate.Release(); }
    }

    private async Task<T> AwaitOrExitAsync<T>(Task<T> completion, ProcessDeadline deadline)
    {
        // readerの終端と要求登録が前後しても、切断後の要求を期限まで放置しません。
        await Task.WhenAny(completion, peerExit, reading).WaitAsync(deadline.Remaining).ConfigureAwait(false);
        if (completion.IsCompleted) { return await completion.ConfigureAwait(false); }
        if (peerExit.IsCompleted)
        {
            int code = await peerExit.ConfigureAwait(false);
            if (code == 0 && restartExpected && !rebound)
            {
                // 再起動元のOS退出は、送信済み応答のreader消費より先に届き得ます。
                // 旧readerの実終端と元の応答を同一期限で待ち、未応答や通信故障を成功に補いません。
                await reading.WaitAsync(deadline.Remaining).ConfigureAwait(false);
                if (readFailure is not null || unansweredRequestFailure is not null)
                { throw new IOException("再起動元の制御通信が失敗しました。", readFailure ?? unansweredRequestFailure); }
                return await completion.WaitAsync(deadline.Remaining).ConfigureAwait(false);
            }
            throw new InvalidOperationException($"要求の結果より先に製品が終了しました: {code}");
        }
        await reading.ConfigureAwait(false);
        throw new IOException("要求の結果より先に制御通信が終了しました。", readFailure);
    }

    private async Task ReadAsync()
    {
        Exception? failure = null;
        try
        {
            while (await reader.ReadLineAsync(stopping.Token).ConfigureAwait(false) is string line)
            {
                ScenarioMessage message = JsonSerializer.Deserialize<ScenarioMessage>(line)
                    ?? throw new InvalidDataException("空の制御応答です。");
                switch (message)
                {
                    case ScenarioConnected hello:
                        if (!connected.TrySetResult(hello)) { throw new InvalidDataException("接続通知が重複しました。"); }
                        break;
                    case ScenarioStartupCompleted ready:
                        if (!startup.TrySetResult(ready.Result)) { throw new InvalidDataException("初期化通知が重複しました。"); }
                        break;
                    case ScenarioCommandFailed { RequestId: 0 } startupFailure:
                        startup.TrySetException(DescribeFailure(startupFailure.Failure));
                        break;
                    default:
                        if (requests.TryGetValue(message.RequestId, out TaskCompletionSource<ScenarioMessage>? request))
                        { request.TrySetResult(message); }
                        break;
                }
            }
            lock (exitExpectationGate)
            {
                if (!closeRequested && !naturalExitExpected && !restartExpected) { failure = new EndOfStreamException("製品の制御pipeが要求外に切断されました。"); }
                // 応答後の呼出元による辞書回収はreaderと並行するため、実応答Taskの状態で区別します。
                if ((naturalExitExpected || restartExpected) && requests.Values.Any(request => !request.Task.IsCompleted)) { failure = new EndOfStreamException("未応答の要求を残して製品が終了しました。"); }
                readerTerminated = true;
                readFailure = failure;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception exception) { failure = exception; }
        finally
        {
            lock (exitExpectationGate) { readFailure = failure; readerTerminated = true; }
            Exception terminal = failure ?? new EndOfStreamException("製品の制御pipeが終了しました。");
            connected.TrySetException(terminal);
            startup.TrySetException(terminal);
            foreach (TaskCompletionSource<ScenarioMessage> pending in requests.Values) { pending.TrySetException(terminal); }
        }
    }

    private static Exception DescribeFailure(ScenarioFailure failure)
        => new InvalidOperationException($"{failure.Type}: {failure.Message}\n{failure.StackTrace}");

    private static async Task SaveProcessDiagnosticsAsync(OwnedProcess owned, string directory)
    {
        Directory.CreateDirectory(directory);
        var failures = new List<Exception>();
        // 回収失敗時に新たな無期限待機を作らず、回収できた出力は片方の保存失敗でも残します。
        if (owned.StandardOutput.IsCompletedSuccessfully)
        {
            try { await File.WriteAllTextAsync(Path.Combine(directory, "stdout.log"), await owned.StandardOutput.ConfigureAwait(false)).ConfigureAwait(false); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (owned.StandardError.IsCompletedSuccessfully)
        {
            try { await File.WriteAllTextAsync(Path.Combine(directory, "stderr.log"), await owned.StandardError.ConfigureAwait(false)).ConfigureAwait(false); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (owned.RootExit.IsCompletedSuccessfully)
        {
            try { await File.WriteAllTextAsync(Path.Combine(directory, "exit-code.txt"), (await owned.RootExit.ConfigureAwait(false)).ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count > 0) { throw new AggregateException("製品processの診断保存に失敗しました。", failures); }
    }

    /// <summary>現在peerの実退出を確認済みならCloseを再送せず、未終了processは通常Close後に必要なら強制回収して通信を回収します。case本体の失敗は呼出元で別に保持します。</summary>
    public async ValueTask DisposeAsync()
    {
        var cleanup = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10));
        ProcessStopResult stopped = await process.StopAsync(_ => naturalExitExpected || peerExit.IsCompletedSuccessfully ? Task.CompletedTask : RequestCloseAsync(cleanup), cleanup).ConfigureAwait(false);
        stopping.Cancel();
        var failures = new List<Exception>();
        try
        {
            try { await reading.ConfigureAwait(false); }
            finally
            {
                reader.Dispose();
                try { writer.Dispose(); }
                finally
                {
                    pipe.Dispose();
                    successor?.Dispose();
                    writeGate.Dispose();
                    stopping.Dispose();
                    await process.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception failure) { failures.Add(failure); }
        try { await SaveProcessDiagnosticsAsync(process, diagnosticsDirectory).ConfigureAwait(false); }
        catch (Exception failure) { failures.Add(failure); }
        if (stopped.CleanupFailure is not null) { failures.Add(stopped.CleanupFailure); }
        if (readFailure is not null) { failures.Add(new InvalidOperationException("制御通信が失敗しました。", readFailure)); }
        if (naturalExitExpected && unansweredRequestFailure is not null) { failures.Add(new IOException("未応答の要求が失敗しました。", unansweredRequestFailure)); }
        if (failures.Count > 0) { throw new AggregateException("製品processの回収に失敗しました。", failures); }
    }
}
