using System.Collections.Concurrent;
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
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly OwnedProcess process;
    private readonly string diagnosticsDirectory;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<ScenarioMessage>> requests = new();
    private readonly TaskCompletionSource<ScenarioConnected> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ScenarioInitializationResult> startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task reading;
    private long nextRequestId;
    private bool closeRequested;
    private Exception? readFailure;

    private ApplicationScenarioDriver(NamedPipeServerStream pipe, OwnedProcess process, string diagnosticsDirectory)
    {
        this.pipe = pipe;
        this.process = process;
        this.diagnosticsDirectory = diagnosticsDirectory;
        reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        reading = ReadAsync();
    }

    /// <summary>保持している実processの識別子です。</summary>
    internal int ProcessId => process.ProcessId;

    /// <summary>readyと独立した実process退出です。未知の切断を正常終了へ読み替えません。</summary>
    internal Task<int> RootExit => process.RootExit;

    /// <summary>初期化前から用意したpipeへ、指定された実製品exeだけを起動します。別buildの探索は行いません。</summary>
    internal static async Task<ApplicationScenarioDriver> StartAsync(string executablePath, string profileDirectory,
        string manifestPath, string caseId, string updateManifestUrl, ProcessDeadline deadline)
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
            updateManifestUrl
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

    /// <summary>ContentRendered起点の実初期化結果を待ちます。正常Task完了だけでは成功にしません。</summary>
    internal Task<ScenarioInitializationResult> WaitForStartupAsync(ProcessDeadline deadline)
        => AwaitOrExitAsync(startup.Task, deadline);

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

    /// <summary>実保留行を対象に既存操作を行い、操作結果とその後の一覧移動結果を返します。</summary>
    internal Task<ScenarioPendingCompleted> ApplyPendingAsync(ScenarioPendingAction action, IEnumerable<string> chartPaths,
        ProcessDeadline deadline, string? destination = null)
        => RequestAsync<ScenarioPendingCompleted>(id => new ScenarioPendingCommand(id, action, [.. chartPaths], destination), deadline);

    /// <summary>実Window.Closeを要求し、root・両出力pipe・全所有子孫の正常退出まで待ちます。</summary>
    internal async Task CloseAsync(ProcessDeadline deadline)
    {
        await RequestCloseAsync(deadline).ConfigureAwait(false);
        ProcessCompletion completion = await process.WaitForExitAsync(deadline).ConfigureAwait(false);
        if (completion.ExitCode != 0)
        { throw new InvalidOperationException($"製品の終了コード: {completion.ExitCode}\n{completion.StandardError}"); }
    }

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
        await Task.WhenAny(completion, process.RootExit, reading).WaitAsync(deadline.Remaining).ConfigureAwait(false);
        if (completion.IsCompleted) { return await completion.ConfigureAwait(false); }
        if (process.RootExit.IsCompleted)
        {
            int code = await process.WaitForRootExitAsync(deadline).ConfigureAwait(false);
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
            if (!closeRequested) { failure = new EndOfStreamException("製品の制御pipeが要求外に切断されました。"); }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception exception) { failure = exception; }
        finally
        {
            readFailure = failure;
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

    /// <summary>未終了processを通常Close後に必要なら強制回収し、通信を回収します。case本体の失敗は呼出元で別に保持します。</summary>
    public async ValueTask DisposeAsync()
    {
        var cleanup = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10));
        ProcessStopResult stopped = await process.StopAsync(_ => RequestCloseAsync(cleanup), cleanup).ConfigureAwait(false);
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
        if (failures.Count > 0) { throw new AggregateException("製品processの回収に失敗しました。", failures); }
    }
}
