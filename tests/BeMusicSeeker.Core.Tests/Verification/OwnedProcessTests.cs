using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>実 fixture を使い、所有と出力回収の外部契約を検査します。</summary>
[TestClass]
public sealed class OwnedProcessTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "ProcessFixture.exe");

    /// <summary>pipe 容量を超える日本語・補助文字の両出力を完全に回収し、逐次読取りのデッドロックと文字破損を検出します。</summary>
    [TestMethod]
    public async Task LargeUtf8OutputDrainsBothPipes()
    {
        await using OwnedProcess process = Start("output");
        ProcessCompletion result = await process.WaitForExitAsync(Deadline());
        string expected = string.Concat(Enumerable.Repeat("日本語🙂🎵\\\"引数\n", 20000));
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(expected, result.StandardOutput);
        Assert.AreEqual(expected, result.StandardError);
        Assert.IsNull(process.PrimaryFailure);
    }

    /// <summary>実 Job 割当失敗の未再開・両 pipe EOF・native 主失敗を保持します。回収期限 callback の正常/例外でも実停止を続け、callback による回収迂回を検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task JobAssignmentFailureDoesNotResumeAndReclaimsCreatedProcess(bool failCleanupCallback)
    {
        using var workspace = new Workspace();
        ProcessDeadline deadline = Deadline();
        // fixture と割当失敗の target を外側の Job で囲い、内側の回収が壊れても所有を失わない。
        await using OwnedProcess owner = Start("assignment-failure", workspace.Ready, workspace.Release, failCleanupCallback.ToString());
        ProcessCompletion result = await owner.WaitForExitAsync(deadline);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(string.Empty, result.StandardError);
        using var observation = JsonDocument.Parse(result.StandardOutput);
        Assert.AreEqual(6, observation.RootElement.GetProperty("NativeErrorCode").GetInt32()); // ERROR_INVALID_HANDLE
        Assert.AreEqual(1, observation.RootElement.GetProperty("FailureCleanupBegins").GetInt32());
        JsonElement cleanupFailure = observation.RootElement.GetProperty("CleanupFailure");
        if (failCleanupCallback)
        {
            Assert.IsTrue(cleanupFailure.GetString()?.Contains("回収期限 callback の故障注入", StringComparison.Ordinal) == true);
        }
        else
        {
            Assert.AreEqual(JsonValueKind.Null, cleanupFailure.ValueKind);
        }
        Assert.AreEqual(string.Empty, observation.RootElement.GetProperty("StandardOutput").GetString());
        Assert.AreEqual(string.Empty, observation.RootElement.GetProperty("StandardError").GetString());
        Assert.IsFalse(File.Exists(workspace.Ready));
        Assert.IsNull(owner.PrimaryFailure);
    }

    /// <summary>両所有方式の引数と子環境を保ち、準備 command ではUTF-8出力を指定してサーバーを無効化します。引用符破損と親環境への変更を検出します。</summary>
    [TestMethod]
    [DataRow(ProcessOwnershipPolicy.Application)]
    [DataRow(ProcessOwnershipPolicy.Preparation)]
    public async Task ChildEnvironmentAndArgumentsRemainIsolated(ProcessOwnershipPolicy policy)
    {
        string?[] before = ServerEnvironment();
        string[] arguments = ["a b", "日本語🙂", "quote\"slash\\", "", "trailing \\"];
        var environment = new Dictionary<string, string?>
        {
            ["DOTNET_CLI_FORCE_UTF8_ENCODING"] = "request-encoding",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "request-server",
            ["MSBUILDDISABLENODEREUSE"] = "request-node",
            ["UseSharedCompilation"] = "request-compiler",
        };
        await using var process = OwnedProcess.Start(new ProcessStartRequest(Fixture,
            new[] { "environment" }.Concat(arguments).ToArray(), AppContext.BaseDirectory, environment, policy));
        ProcessCompletion result = await process.WaitForExitAsync(Deadline());
        string[] expectedEnvironment = policy == ProcessOwnershipPolicy.Preparation
            ? ["1", "0", "1", "false"] : ["request-encoding", "request-server", "request-node", "request-compiler"];
        Assert.AreEqual(string.Join(Environment.NewLine, expectedEnvironment.Concat(arguments)) + Environment.NewLine, result.StandardOutput);
        CollectionAssert.AreEqual(before, ServerEnvironment());
        if (policy == ProcessOwnershipPolicy.Preparation)
        {
            using var current = Process.GetCurrentProcess();
            Assert.Throws<NotSupportedException>(() => process.ContainsProcess(current));
        }
    }

    /// <summary>即時生成した child が root 単独終了後も同じ Job に残ることを読み取り、Job の回収前に全体完了とする誤りを検出します。</summary>
    [TestMethod]
    public async Task RootExitDoesNotCompleteUntilImmediateChildTerminates()
    {
        using var workspace = new Workspace();
        await using OwnedProcess process = Start("child", workspace.Ready, workspace.Release, "exit");
        await workspace.WaitReadyAsync();
        using var child = Process.GetProcessById(int.Parse(File.ReadAllText(workspace.Ready)));
        ProcessDeadline rootDeadline = Deadline();
        Assert.AreEqual(7, await process.WaitForRootExitAsync(rootDeadline));
        Assert.IsTrue(process.RootExit.IsCompletedSuccessfully);
        Assert.IsNotNull(process.PrimaryFailure);
        Assert.IsTrue(process.ContainsProcess(child));
        Assert.IsFalse(process.Completion.IsCompleted);
        Assert.IsFalse(child.HasExited);
        ProcessStopResult cleanup = await process.StopAsync(null, Deadline());
        Assert.IsNull(cleanup.CleanupFailure);
        Assert.IsTrue(child.HasExited);
        Assert.AreEqual(7, (await process.Completion).ExitCode);
        Assert.IsNotNull(process.PrimaryFailure);
    }

    /// <summary>OS上でrootが指定コードで終了しても通知配送を保留し、期限失敗後の破棄と交差させて、事前保持した元RootExitの実値と主失敗が失われないことを保証します。</summary>
    /// <remarks>正常全終端のCompletion=7は既存のroot/child検査へ分担します。ここでは期限後のCompletion故障を診断し、遅延通知と全pipeの終端後だけ領域を削除します。</remarks>
    [TestMethod]
    [DataRow(7)]
    [DataRow(259)]
    public async Task RootExitSurvivesDisposalBeforeExitNotificationDelivery(int exitCode)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string ready = workspace.GetPath("ready");
        string release = workspace.GetPath("release");
        string command = workspace.GetPath("command.json");
        File.WriteAllText(command, JsonSerializer.Serialize(new { Ready = ready, Release = release, ExitCode = exitCode }));
        using var job = WindowsProcessJob.Create();
        using var outputPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var errorPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var info = new ProcessStartInfo(Fixture) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        info.ArgumentList.Add("runner-command");
        info.ArgumentList.Add(command);
        using Process root = job.Start(info, outputPipe, errorPipe);
        outputPipe.DisposeLocalCopyOfClientHandle();
        errorPipe.DisposeLocalCopyOfClientHandle();
        using var stdout = new StreamReader(outputPipe, new UTF8Encoding(false), false);
        using var stderr = new StreamReader(errorPipe, new UTF8Encoding(false), false);
        var delivery = new DeferredExitDelivery();
        root.SynchronizingObject = delivery;
        var process = new OwnedProcess(root, job, stdout, stderr);
        Task<int> originalRootExit = process.RootExit;
        Task<ProcessCompletion> originalCompletion = process.Completion;
        var observedRoot = Process.GetProcessById(process.ProcessId);
        // PID照会だけではhandle取得が遅延するため、生存中に取得して終了後の実コード照会を可能にする。
        _ = observedRoot.SafeHandle;
        Exception? bodyFailure = null;
        var secondaryFailures = new List<Exception>();

        async Task<bool> ObserveAsync(Func<Task> action, string name, bool allowCompletionFailure = false)
        {
            Task task;
            try { task = action(); }
            catch (Exception failure)
            {
                secondaryFailures.Add(new InvalidOperationException(name, failure));
                return false;
            }
            using var stopWatchdog = new CancellationTokenSource();
            var watchdog = Task.Delay(TimeSpan.FromSeconds(10), stopWatchdog.Token);
            Task first = await Task.WhenAny(task, watchdog);
            stopWatchdog.Cancel();
            if (first == watchdog)
            {
                secondaryFailures.Add(new TimeoutException(name + "の終端観測期限を超えています。"));
            }
            if (task.IsCompleted)
            {
                try { await task; }
                catch (Exception failure)
                {
                    Console.Error.WriteLine($"{name}: status={task.Status}, failure={failure}");
                    if (!allowCompletionFailure || process.CleanupFailure is null)
                    {
                        secondaryFailures.Add(new InvalidOperationException(name, failure));
                    }
                }
            }
            return first == task;
        }

        try
        {
            ProcessDeadline preparation = Deadline();
            while (!File.Exists(ready))
            {
                if (preparation.Remaining == TimeSpan.Zero) { throw new TimeoutException("rootの準備が完了しません。"); }
                await Task.Delay(10).WaitAsync(preparation.Remaining);
            }
            Assert.IsFalse(originalRootExit.IsCompleted, "生存中のrootを終了扱いしています。");
            var expired = ProcessDeadline.FromTimeout(TimeSpan.Zero);
            await Assert.ThrowsAsync<TimeoutException>(() => process.WaitForRootExitAsync(expired));
            Exception? originalFailure = process.PrimaryFailure;
            File.WriteAllText(release, "release");
            await delivery.Captured.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(observedRoot.HasExited);
            Assert.AreEqual(exitCode, observedRoot.ExitCode);
            // 破棄との競合はroot通知だけへ限定し、pipeのEOFと内容は解放前に確定する。
            Assert.AreEqual("stdout", await process.StandardOutput.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual("stderr", await process.StandardError.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsFalse(originalRootExit.IsCompleted);
            ProcessStopResult stopped = await process.StopAsync(null, expired).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(stopped.CleanupFailure);
            await process.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreSame(originalRootExit, process.RootExit);
            Assert.AreEqual(exitCode, await originalRootExit.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreSame(originalFailure, process.PrimaryFailure);
            Assert.IsInstanceOfType<TimeoutException>(originalFailure);
            Assert.IsNotNull(process.CleanupFailure);
            // 購読解除後も、既に捕捉したdelegateを配送する。閉鎖済みProcessへ触れないことを検査する。
            delivery.DeliverPending();
            await Task.WhenAll(delivery.Tasks).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(exitCode, await originalRootExit);
            Assert.AreSame(originalFailure, process.PrimaryFailure);
        }
        catch (Exception failure) { bodyFailure = failure; }
        finally
        {
            // 本体の失敗を保持し、一つの回収失敗で残りの観測や診断を省略しない。
            delivery.DeliverPending();
            bool resourcesTerminated = await ObserveAsync(() => process.StopAsync(null, Deadline()), "Stop");
            resourcesTerminated &= await ObserveAsync(() => process.DisposeAsync().AsTask(), "Dispose");
            delivery.DeliverPending();
            resourcesTerminated &= await ObserveAsync(() => originalRootExit, "RootExit");
            resourcesTerminated &= await ObserveAsync(() => process.StandardOutput, "stdout");
            resourcesTerminated &= await ObserveAsync(() => process.StandardError, "stderr");
            resourcesTerminated &= await ObserveAsync(() => originalCompletion, "Completion", allowCompletionFailure: true);
            foreach (Task task in delivery.Tasks)
            {
                resourcesTerminated &= await ObserveAsync(() => task, "終了通知の配送");
            }
            resourcesTerminated &= await ObserveAsync(() => observedRoot.WaitForExitAsync(), "保持rootの終了");
            try { resourcesTerminated &= observedRoot.HasExited; }
            catch (Exception failure)
            {
                resourcesTerminated = false;
                secondaryFailures.Add(failure);
            }
            try { observedRoot.Dispose(); }
            catch (Exception failure) { secondaryFailures.Add(failure); }
            if (resourcesTerminated)
            {
                try { workspace.Dispose(); }
                catch (Exception failure) { secondaryFailures.Add(failure); }
            }
        }
        foreach (Exception failure in secondaryFailures) { Console.Error.WriteLine("回収失敗: " + failure); }
        if (secondaryFailures.Count != 0)
        {
            if (bodyFailure is not null) { secondaryFailures.Insert(0, bodyFailure); }
            throw new AggregateException("root観測検査と回収の失敗", secondaryFailures);
        }
        if (bodyFailure is not null) { ExceptionDispatchInfo.Capture(bodyFailure).Throw(); }
        if (originalCompletion.IsCompletedSuccessfully) { Assert.AreEqual(exitCode, (await originalCompletion).ExitCode); }
        Assert.IsFalse(Directory.Exists(workspace.RootDirectory));
    }

    /// <summary>Processの既存配送境界でdelegate全体だけを保留し、callback threadやProcess内部のlockを占有しません。</summary>
    private sealed class DeferredExitDelivery : ISynchronizeInvoke
    {
        private readonly object gate = new();
        private readonly Queue<(Delegate Method, object?[]? Arguments, TaskCompletionSource<object?> Result)> pending = new();
        private readonly List<Task> tasks = new();
        private readonly TaskCompletionSource captured = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>ProcessのExited通知を常にBeginInvokeへ接続します。</summary>
        public bool InvokeRequired => true;
        /// <summary>最初の実配送要求を捕捉した通知です。</summary>
        internal Task Captured => captured.Task;
        /// <summary>例外を含めて全件観測する、捕捉済み配送の元Taskです。</summary>
        internal Task[] Tasks { get { lock (gate) { return tasks.ToArray(); } } }

        /// <summary>捕捉後すぐに返し、Processが保持する同期領域の外で後から配送します。</summary>
        public IAsyncResult BeginInvoke(Delegate method, object?[]? args)
        {
            var result = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                pending.Enqueue((method, args, result));
                tasks.Add(result.Task);
                captured.TrySetResult();
            }
            return result.Task;
        }

        /// <summary>捕捉済みの全配送をlock外で実行し、元delegateの例外を観測可能なTaskへ保存します。</summary>
        internal void DeliverPending()
        {
            while (true)
            {
                (Delegate Method, object?[]? Arguments, TaskCompletionSource<object?> Result) item;
                lock (gate)
                {
                    if (!pending.TryDequeue(out item)) { return; }
                }
                try { item.Result.TrySetResult(item.Method.DynamicInvoke(item.Arguments)); }
                catch (Exception failure) { item.Result.TrySetException(failure); }
            }
        }

        /// <summary>ProcessのExited配送が使わない同期呼出しを拒否します。</summary>
        public object? Invoke(Delegate method, object?[]? args) => throw new NotSupportedException();
        /// <summary>ProcessはEndInvokeで待たず、テストは配送Taskを有限に観測します。</summary>
        public object? EndInvoke(IAsyncResult result) => throw new NotSupportedException();
    }

    /// <summary>hang の期限失敗と回収失敗を分離し、Job 所属と無関係な同名 process を区別します。未関連・閉鎖 handle の偽成功と無関係な停止を検出します。</summary>
    [TestMethod]
    public async Task TimeoutAndCleanupFailureRemainSeparateWithoutStoppingUnrelatedProcess()
    {
        using var workspace = new Workspace();
        using var otherWorkspace = new Workspace();
        await using OwnedProcess unrelated = Start("hang", otherWorkspace.Ready, otherWorkspace.Release);
        await using OwnedProcess target = Start("child", workspace.Ready, workspace.Release, "wait");
        await Task.WhenAll(workspace.WaitReadyAsync(), otherWorkspace.WaitReadyAsync());
        using var child = Process.GetProcessById(int.Parse(File.ReadAllText(workspace.Ready)));
        using var other = Process.GetProcessById(int.Parse(File.ReadAllText(otherWorkspace.Ready)));
        Assert.IsTrue(target.ContainsProcess(child));
        Assert.IsFalse(target.ContainsProcess(other));
        using var missing = new Process();
        Assert.Throws<InvalidOperationException>(() => target.ContainsProcess(missing));
        using var closed = Process.GetCurrentProcess();
        closed.SafeHandle.Dispose();
        Assert.Throws<InvalidOperationException>(() => target.ContainsProcess(closed));
        await Assert.ThrowsAsync<TimeoutException>(() => target.WaitForExitAsync(ProcessDeadline.FromTimeout(TimeSpan.Zero)));
        Exception? primary = target.PrimaryFailure;
        var injected = new IOException("正常終了要求に失敗");
        ProcessStopResult cleanup = await target.StopAsync(_ => Task.FromException(injected), Deadline());
        Assert.AreSame(primary, target.PrimaryFailure);
        Assert.AreSame(injected, cleanup.CleanupFailure);
        Assert.IsTrue(child.HasExited);
        Assert.IsFalse(unrelated.Completion.IsCompleted);
        File.WriteAllText(otherWorkspace.Release, "release");
        Assert.AreEqual(0, (await unrelated.WaitForExitAsync(Deadline())).ExitCode);
        await target.DisposeAsync();
        Assert.AreSame(injected, target.CleanupFailure);
    }

    /// <summary>
    /// 正常終了要求の待機が時間切れになった直後、catch で完了を確かめる前に要求が完了する場合を固定します。
    /// 期待結果は、OperationCanceledException 以外で失敗した要求の元の例外がちょうど1回だけ回収失敗の直接の要素になり、待機の時間切れの例外は入らないことです。
    /// 取消で完了した要求は、要求由来の取消の例外も時間切れの例外も入れません。
    /// 失敗の取りこぼし、時間切れの例外の登録、二重登録、AggregateException での包み込み、取消の誤登録を検出します。
    /// </summary>
    /// <remarks>
    /// 期限切れの cleanup 期限で猶予を0にして、時間切れの送出を StopAsync の呼出しスレッドへ同期させ、
    /// FirstChanceException が送出の直後・catch の検索の前に呼ばれることで要求の完了時機を固定します。
    /// 捕捉器は AsyncLocal でこのテストの流れだけに限定します。待機が時間切れを例外で表さなくなった場合は、捕捉器の発火の表明で失敗します。
    /// 期限切れのため回収段階の失敗も加わるので、表明は例外の参照の同一性だけで行い、他の失敗の件数・型は問いません。
    /// </remarks>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task GracefulRequestCompletingAfterWaitTimeoutReportsOnlyFaultOnce(bool faulted)
    {
        using var workspace = new Workspace();
        await using OwnedProcess process = Start("hang", workspace.Ready, workspace.Release);
        await workspace.WaitReadyAsync();
        // 実際の回収は、保持した観測用 handle の終了で確認する。
        using var observer = Process.GetProcessById(process.ProcessId);
        _ = observer.SafeHandle;
        var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injected = new IOException("正常終了要求の遅延した失敗");
        CancellationToken requestToken = default;
        var probe = new WaitTimeoutProbe(request, faulted, injected);
        EventHandler<FirstChanceExceptionEventArgs> handler = probe.OnFirstChance;
        var expiredBeforeStop = ProcessDeadline.FromTimeout(TimeSpan.Zero);
        ProcessStopResult cleanup;
        WaitTimeoutProbe.Current.Value = probe;
        AppDomain.CurrentDomain.FirstChanceException += handler;
        try
        {
            cleanup = await process.StopAsync(token => { requestToken = token; probe.RequestToken = token; return request.Task; }, expiredBeforeStop);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= handler;
            WaitTimeoutProbe.Current.Value = null;
            request.TrySetCanceled();
        }
        using var exitWait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await observer.WaitForExitAsync(exitWait.Token);
        Assert.IsTrue(observer.HasExited);
        // root の終了を観測したかどうかによらず、同じ期限切れの期限で主失敗を固定し、意図して作った回収失敗を DisposeAsync が再送出しないようにする。
        await Assert.ThrowsAsync<TimeoutException>(() => process.WaitForRootExitAsync(expiredBeforeStop));
        Assert.IsNotNull(probe.Timeout, "待機の時間切れが例外として観測されませんでした。");
        Assert.IsTrue(probe.RequestWasPending);
        Assert.AreEqual(faulted ? TaskStatus.Faulted : TaskStatus.Canceled, request.Task.Status);
        Assert.IsNotNull(cleanup.CleanupFailure);
        Assert.AreSame(cleanup.CleanupFailure, process.CleanupFailure);
        List<Exception> all = Flatten(cleanup.CleanupFailure);
        Assert.IsFalse(all.Any(e => ReferenceEquals(e, probe.Timeout)));
        if (faulted)
        {
            Assert.AreEqual(1, all.Count(e => ReferenceEquals(e, injected)));
            IEnumerable<Exception> direct = cleanup.CleanupFailure is AggregateException aggregate ? aggregate.InnerExceptions : [cleanup.CleanupFailure];
            Assert.IsTrue(direct.Any(e => ReferenceEquals(e, injected)));
        }
        else
        {
            Assert.IsFalse(all.Any(e => e is OperationCanceledException canceled && canceled.CancellationToken == requestToken));
            Assert.IsFalse(all.Any(e => e is TaskCanceledException { Task: { } task } && ReferenceEquals(task, request.Task)));
        }

        static List<Exception> Flatten(Exception root)
        {
            var result = new List<Exception>();
            var pending = new Stack<Exception>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                Exception current = pending.Pop();
                result.Add(current);
                if (current is AggregateException aggregate)
                {
                    foreach (Exception inner in aggregate.InnerExceptions) { pending.Push(inner); }
                }
                else if (current.InnerException is { } inner)
                {
                    pending.Push(inner);
                }
            }
            return result;
        }
    }

    /// <summary>流れ内で最初の TimeoutException の送出直後に、要求 Task を失敗または取消で完了させます。</summary>
    private sealed class WaitTimeoutProbe(TaskCompletionSource request, bool faulted, Exception failure)
    {
        internal static readonly AsyncLocal<WaitTimeoutProbe?> Current = new();
        internal CancellationToken RequestToken;
        internal TimeoutException? Timeout;
        internal bool RequestWasPending;

        internal void OnFirstChance(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (!ReferenceEquals(Current.Value, this) || Timeout is not null || args.Exception is not TimeoutException timeout) { return; }
            Timeout = timeout;
            RequestWasPending = !request.Task.IsCompleted;
            if (faulted) { request.TrySetException(failure); }
            else { request.TrySetCanceled(RequestToken); }
        }
    }

    /// <summary>owner を強制終了しても非継承 Job handle の最終 close で child が終了し、owner 死亡時の孤児化を検出します。</summary>
    [TestMethod]
    public async Task OwnerDeathClosesItsJobAndTerminatesChild()
    {
        using var workspace = new Workspace();
        await using var owner = OwnedProcess.Start(new ProcessStartRequest(Fixture,
            ["owner", workspace.Ready, workspace.Release], AppContext.BaseDirectory,
            ownershipPolicy: ProcessOwnershipPolicy.Preparation));
        await workspace.WaitReadyAsync();
        using var child = Process.GetProcessById(int.Parse(File.ReadAllText(workspace.Ready)));
        using var root = Process.GetProcessById(owner.ProcessId);
        ProcessDeadline exitDeadline = Deadline();
        try
        {
            root.Kill(); // child を KillTree で止めず、内側の Job の OS 回収を観測する。
            await child.WaitForExitAsync().WaitAsync(exitDeadline.Remaining);
            await owner.WaitForExitAsync(exitDeadline);
            Assert.IsTrue(child.HasExited);
        }
        finally
        {
            ProcessDeadline cleanupDeadline = Deadline();
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
                await child.WaitForExitAsync().WaitAsync(cleanupDeadline.Remaining);
            }
            finally
            {
                await owner.StopAsync(null, cleanupDeadline);
            }
        }
        Assert.IsNull(owner.CleanupFailure);
    }

    /// <summary>期限後の終端を後から待っても失敗を保ち、期限内終端は待機継続が遅れても成功することを確認します。</summary>
    [TestMethod]
    public async Task CompletedProcessUsesTerminalTimestampInsteadOfWaitDeliveryTime()
    {
        var expiredBeforeStart = ProcessDeadline.FromTimeout(TimeSpan.Zero);
        await using OwnedProcess process = Start("output");
        await process.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var deadlineAfterCompletion = ProcessDeadline.FromTimeout(TimeSpan.Zero);
        Assert.AreEqual(0, await process.WaitForRootExitAsync(deadlineAfterCompletion));
        Assert.AreEqual(0, (await process.WaitForExitAsync(deadlineAfterCompletion)).ExitCode);
        await Assert.ThrowsAsync<TimeoutException>(() => process.WaitForRootExitAsync(expiredBeforeStart));
        await Assert.ThrowsAsync<TimeoutException>(() => process.WaitForExitAsync(expiredBeforeStart));
        Assert.IsInstanceOfType<TimeoutException>(process.PrimaryFailure);
    }

    /// <summary>期限切れの停止でJobを強制終了した後、破棄を待たずにOSの終了がRootExitとCompletionへ反映されることを保証します。</summary>
    /// <remarks>
    /// 強制終了の直後にProcess.HasExitedが先にtrueになると、Exited通知がhandleのsignal前に消費され、通知だけでは終了を確定できません。
    /// その順序はOSの競合で決まり決定的には作れないため、検出は確率的です。破棄時の実値確定と通知遅延は
    /// <see cref="RootExitSurvivesDisposalBeforeExitNotificationDelivery"/> が、通常の停止は既存のroot/childの検査が分担します。
    /// </remarks>
    [TestMethod]
    public async Task RootExitReflectsTerminationAfterExpiredStopWithoutDisposal()
    {
        using var workspace = new Workspace();
        await using OwnedProcess process = Start("hang", workspace.Ready, workspace.Release);
        await workspace.WaitReadyAsync();
        using var observer = Process.GetProcessById(process.ProcessId);
        _ = observer.SafeHandle; // 終了後の実終了コード照会のため、生存中にhandleを保持する。
        var expiredBeforeStop = ProcessDeadline.FromTimeout(TimeSpan.Zero);
        await process.StopAsync(null, expiredBeforeStop);
        // 期限切れ停止の回収失敗が本体の失敗を置き換えないよう、主失敗を先に固定する。
        await Assert.ThrowsAsync<TimeoutException>(() => process.WaitForRootExitAsync(expiredBeforeStop));
        ProcessDeadline observation = Deadline();
        await observer.WaitForExitAsync().WaitAsync(observation.Remaining);
        // 破棄はRootExitの確定を確認した後でだけ行う。
        int rootExit = await process.RootExit.WaitAsync(observation.Remaining);
        ProcessCompletion completion = await process.Completion.WaitAsync(observation.Remaining);
        Assert.AreEqual(observer.ExitCode, rootExit);
        Assert.AreEqual(observer.ExitCode, completion.ExitCode);
    }

    /// <summary>存在しない実行体の起動失敗を保持し、取消・使い回した期限で待機時間を再設定する誤実装を検出します。</summary>
    [TestMethod]
    public async Task StartFailureCancellationAndSharedDeadlineRemainFailures()
    {
        int failureCleanupBegins = 0;
        ProcessDeadline startFailureCleanup = Deadline();
        Assert.Throws<OwnedProcessStartException>(() => OwnedProcess.Start(new ProcessStartRequest(
            Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".exe"), [], AppContext.BaseDirectory), () =>
            {
                failureCleanupBegins++;
                return startFailureCleanup;
            }));
        Assert.AreEqual(1, failureCleanupBegins);
        using var workspace = new Workspace();
        await using OwnedProcess process = Start("hang", workspace.Ready, workspace.Release);
        await workspace.WaitReadyAsync();
        var shared = ProcessDeadline.FromTimeout(TimeSpan.FromMilliseconds(40));
        string sharedCutoff = shared.Export();
        await Assert.ThrowsAsync<TimeoutException>(() => process.WaitForExitAsync(shared));
        await Assert.ThrowsAsync<TimeoutException>(() => process.WaitForRootExitAsync(shared));
        // タイマ通知と単調時計の微小差ではなく、元の絶対期限を再設定しないことを確認します。
        Assert.AreEqual(sharedCutoff, shared.Export());
        await Assert.ThrowsAsync<TimeoutException>(() => process.WaitForExitAsync(shared));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => process.WaitForExitAsync(Deadline(), canceled.Token));
        await Assert.ThrowsAsync<TaskCanceledException>(() => process.WaitForRootExitAsync(Deadline(), canceled.Token));
        File.WriteAllText(workspace.Release, "release");
        Assert.AreEqual(0, await process.WaitForRootExitAsync(Deadline()));
        await process.WaitForExitAsync(Deadline());
        Assert.IsInstanceOfType<TimeoutException>(process.PrimaryFailure);
    }

    private static OwnedProcess Start(params string[] args)
        => OwnedProcess.Start(new ProcessStartRequest(Fixture, args, AppContext.BaseDirectory));

    private static ProcessDeadline Deadline() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10));

    private static string?[] ServerEnvironment() => [Environment.GetEnvironmentVariable("DOTNET_CLI_FORCE_UTF8_ENCODING"),
        Environment.GetEnvironmentVariable("DOTNET_CLI_USE_MSBUILD_SERVER"),
        Environment.GetEnvironmentVariable("MSBUILDDISABLENODEREUSE"), Environment.GetEnvironmentVariable("UseSharedCompilation")];

    private sealed class Workspace : IDisposable
    {
        private readonly TestWorkspace directory = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        internal string Ready => directory.GetPath("ready");
        internal string Release => directory.GetPath("release");
        internal async Task WaitReadyAsync()
        {
            var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10));
            while (!File.Exists(Ready) || new FileInfo(Ready).Length == 0)
            {
                if (deadline.Remaining == TimeSpan.Zero)
                {
                    throw new TimeoutException("fixture が準備を通知しません。");
                }

                await Task.Delay(10);
            }
        }
        public void Dispose() => directory.Dispose();
    }
}
