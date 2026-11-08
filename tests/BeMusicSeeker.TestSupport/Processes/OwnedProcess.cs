using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BeMusicSeeker.TestSupport.Processes;

/// <summary>実行体、UTF-8 出力、子孫の寿命を一つの所有境界として回収します。</summary>
/// <remarks>
/// Preparation は起動前に標準出力・標準エラーの文字コードを UTF-8 に設定し、子環境に
/// <c>DOTNET_CLI_FORCE_UTF8_ENCODING=1</c> を設定します。Application は所有する両 pipe を UTF-8 の
/// <see cref="StreamReader"/> で読み取ります。保存時の変換では読取り時の誤変換を直せないため、読取り側で文字コードを確定します。
/// 親プロセスのコンソール・出力文字コード・カルチャ・環境変数は変更しません。
/// </remarks>
public sealed class OwnedProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly WindowsProcessJob? job;
    private readonly StreamReader stdout;
    private readonly StreamReader stderr;
    private readonly object rootGate = new();
    private readonly TaskCompletionSource<int> rootExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>WindowsのProcessが所有する同じhandleを借用します。別途解放せず、rootGate内ではProcessへ再照会しません。</summary>
    private readonly SafeProcessHandle? rootHandle;
    private long rootExitedTimestamp;
    private long completedTimestamp;
    private Exception? primaryFailure;
    private Task<ProcessStopResult>? stopping;
    private bool disposed;

    /// <summary>Windows上で起動時の所属を確立済みの root と Job、両 reader の所有を成功時に引き受けます。reader 単独の待機条件もこの境界で検証できます。</summary>
    internal OwnedProcess(Process process, WindowsProcessJob? job, StreamReader stdout, StreamReader stderr)
    {
        this.process = process;
        this.job = job;
        this.stdout = stdout;
        this.stderr = stderr;
        ProcessId = process.Id;
        StandardOutput = stdout.ReadToEndAsync();
        StandardError = stderr.ReadToEndAsync();
        RootExit = rootExit.Task;
        try
        {
            rootHandle = process.SafeHandle;
            process.Exited += OnRootExited;
            process.EnableRaisingEvents = true;
            ObserveRootExit(releasing: false);
        }
        catch (Exception failure)
        {
            lock (rootGate) { FailRootObservation(failure); }
        }
        Completion = CollectAsync();
    }

    /// <summary>保持した root の識別子です。停止対象の再発見には使いません。</summary>
    public int ProcessId { get; }
    /// <summary>起動直後から非同期に回収する標準出力です。</summary>
    public Task<string> StandardOutput { get; }
    /// <summary>起動直後から非同期に回収する標準エラーです。</summary>
    public Task<string> StandardError { get; }
    /// <summary>保持した root だけの終了コードです。破棄前に実値または観測不能の失敗を確定し、同じTaskを維持します。両 pipe と Job の子孫回収は Completion で別に待ちます。</summary>
    public Task<int> RootExit { get; }
    /// <summary>root、両 pipe、Job の全子孫の終端です。</summary>
    public Task<ProcessCompletion> Completion { get; }
    /// <summary>最初の非成功終了・待機失敗です。回収失敗とは別に保持します。</summary>
    public Exception? PrimaryFailure => primaryFailure;
    /// <summary>正常終了要求・強制停止・回収で発生した失敗です。</summary>
    public Exception? CleanupFailure { get; private set; }

    /// <summary>渡された保持 Process ハンドルの、この所有 Job への所属を読み取ります。登録・再割当は行いません。</summary>
    /// <returns>この Job に所属する場合は true、別 Job または Job に所属しない場合は false です。</returns>
    /// <exception cref="NotSupportedException">Preparation は Job を持たないため所属を判定できません。</exception>
    /// <exception cref="InvalidOperationException">Process が未関連、または照会に使うハンドルが無効です。</exception>
    /// <exception cref="ObjectDisposedException">所有者または照会に使うハンドルが閉じられています。</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">所属照会の native API が失敗しました。</exception>
    public bool ContainsProcess(Process process)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(process);
        if (job is null)
        {
            throw new NotSupportedException("Preparation は Job の所属照会を提供しません。");
        }
        return job.ContainsProcess(process);
    }

    /// <summary>要求を忠実に渡し、所有を確立してから子のコードを実行します。独立起動の失敗回収には最初の失敗から10秒を使います。</summary>
    public static OwnedProcess Start(ProcessStartRequest request)
        => Start(request, static () => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)));

    /// <summary>起動失敗を最初に検出した時点で共通の回収期限を確定し、低位・上位の失敗回収へ同じ期限を渡します。</summary>
    /// <remarks>callback が失敗しても停止を続けます。その失敗を回収失敗へ残し、新しい猶予は作りません。</remarks>
    internal static OwnedProcess Start(ProcessStartRequest request, Func<ProcessDeadline> beginFailureCleanup)
    {
        // callback の例外も保存し、catch を重ねても新しい猶予を取得しない。
        var failureCleanup = new Lazy<ProcessDeadline>(beginFailureCleanup);
        Process? process = null;
        WindowsProcessJob? job = null;
        AnonymousPipeServerStream? output = null;
        AnonymousPipeServerStream? error = null;
        try
        {
            var info = new ProcessStartInfo(request.FileName)
            {
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (string argument in request.Arguments)
            {
                info.ArgumentList.Add(argument);
            }
            foreach (KeyValuePair<string, string?> pair in request.Environment)
            {
                if (pair.Value is null)
                {
                    info.Environment.Remove(pair.Key);
                }
                else
                {
                    info.Environment[pair.Key] = pair.Value;
                }
            }
            if (request.OwnershipPolicy == ProcessOwnershipPolicy.Preparation)
            {
                info.Environment["DOTNET_CLI_FORCE_UTF8_ENCODING"] = "1";
                info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
                info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
                info.Environment["UseSharedCompilation"] = "false";
                process = Process.Start(info) ?? throw new InvalidOperationException("プロセスを起動できません。");
                return new OwnedProcess(process, null, process.StandardOutput, process.StandardError);
            }
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Application の所有には Windows Job が必要です。");
            }

            job = WindowsProcessJob.Create();
            output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            process = job.Start(info, output, error, () => failureCleanup.Value);
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
            return new OwnedProcess(process, job,
                new StreamReader(output, new UTF8Encoding(false), false),
                new StreamReader(error, new UTF8Encoding(false), false));
        }
        catch (Exception primary)
        {
            var cleanupFailures = new List<Exception>();
            var lowLevelFailure = primary as OwnedProcessStartException;
            if (lowLevelFailure?.CleanupFailure is Exception priorCleanupFailure)
            {
                cleanupFailures.Add(priorCleanupFailure);
            }
            int priorFailureCount = cleanupFailures.Count;
            ProcessDeadline? cleanupDeadline = null;
            try
            {
                cleanupDeadline = failureCleanup.Value;
            }
            catch (Exception failure)
            {
                // 低位で callback の失敗を保存済みなら、同じ失敗を再び追加しない。
                if (lowLevelFailure?.CleanupFailure is null)
                {
                    cleanupFailures.Add(failure);
                }
            }
            try
            {
                if (job is not null)
                {
                    job.Terminate();
                }
                else if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception failure) { cleanupFailures.Add(failure); }
            try
            {
                if (process is not null)
                {
                    // cutoff を取得できなければ追加猶予を作らず、停止要求後の有限 poll だけを行う。
                    int waitMilliseconds = (int)Math.Min(int.MaxValue, Math.Floor((cleanupDeadline?.Remaining ?? TimeSpan.Zero).TotalMilliseconds));
                    if (!process.WaitForExit(waitMilliseconds) || (cleanupDeadline is not null && !cleanupDeadline.Includes(Stopwatch.GetTimestamp())))
                    {
                        cleanupFailures.Add(new TimeoutException("起動失敗したプロセスを回収できません。"));
                    }
                }
            }
            catch (Exception failure) { cleanupFailures.Add(failure); }
            finally
            {
                process?.Dispose();
                output?.Dispose();
                error?.Dispose();
                job?.Dispose();
            }
            if (lowLevelFailure is not null && cleanupFailures.Count == priorFailureCount)
            {
                throw;
            }
            Exception? cleanup = cleanupFailures.Count switch { 0 => null, 1 => cleanupFailures[0], _ => new AggregateException(cleanupFailures) };
            throw new OwnedProcessStartException(lowLevelFailure?.InnerException ?? primary, cleanup);
        }
    }

    /// <summary>段階で共有する絶対期限まで全終端を待ちます。期限超過と取消は主失敗として再送出します。</summary>
    public async Task<ProcessCompletion> WaitForExitAsync(ProcessDeadline deadline, CancellationToken cancellationToken = default)
    {
        try
        {
            return await WaitForCompletionAsync(deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            Interlocked.CompareExchange(ref primaryFailure, failure, null);
            throw;
        }
    }

    /// <summary>保持した root の終了だけを共有期限内で待ちます。終了観測時刻が期限後なら、後から待っても期限超過を保持します。</summary>
    public async Task<int> WaitForRootExitAsync(ProcessDeadline deadline, CancellationToken cancellationToken = default)
    {
        try
        {
            int exitCode;
            try
            {
                exitCode = await RootExit.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) when (RootExit.IsCompletedSuccessfully && deadline.Includes(Volatile.Read(ref rootExitedTimestamp)))
            {
                exitCode = await RootExit.ConfigureAwait(false);
            }
            if (!deadline.Includes(Volatile.Read(ref rootExitedTimestamp)))
            {
                throw new TimeoutException("root プロセスの終端が期限を超えました。");
            }
            return exitCode;
        }
        catch (Exception failure)
        {
            Interlocked.CompareExchange(ref primaryFailure, failure, null);
            throw;
        }
    }

    /// <summary>正常終了を先に要求し、残期限で所有対象だけを強制停止・回収します。繰返し呼出しでも期限は更新しません。</summary>
    public Task<ProcessStopResult> StopAsync(Func<CancellationToken, Task>? requestGracefulExit, ProcessDeadline cleanupDeadline)
        => stopping ??= StopCoreAsync(requestGracefulExit, cleanupDeadline);

    private async Task<ProcessCompletion> WaitForCompletionAsync(ProcessDeadline deadline, CancellationToken cancellationToken = default)
    {
        ProcessCompletion result;
        try
        {
            result = await Completion.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) when (Completion.IsCompletedSuccessfully && deadline.Includes(Volatile.Read(ref completedTimestamp)))
        {
            // タイマーと継続の配送順だけで、期限内に完了した実行を失敗へ変えない。
            result = await Completion.ConfigureAwait(false);
        }
        if (!deadline.Includes(Volatile.Read(ref completedTimestamp)))
        {
            throw new TimeoutException("所有プロセスの終端が期限を超えました。");
        }
        return result;
    }

    private async Task<ProcessStopResult> StopCoreAsync(Func<CancellationToken, Task>? requestGracefulExit, ProcessDeadline deadline)
    {
        var failures = new List<Exception>();
        if (!Completion.IsCompletedSuccessfully)
        {
            // 後半を強制停止と pipe 回収に残し、正常終了要求のハングで全予算を使い切らない。
            var grace = TimeSpan.FromTicks(deadline.Remaining.Ticks / 2);
            var gracefulDeadline = ProcessDeadline.FromTimeout(grace);
            using var cancellation = new CancellationTokenSource(grace);
            Task? gracefulRequest = null;
            try
            {
                bool requested = requestGracefulExit is not null;
                if (requestGracefulExit is not null)
                {
                    gracefulRequest = requestGracefulExit(cancellation.Token);
                    await gracefulRequest.WaitAsync(gracefulDeadline.Remaining).ConfigureAwait(false);
                }
                else if (!process.HasExited)
                {
                    requested = process.CloseMainWindow();
                }

                if (requested)
                {
                    await WaitForCompletionAsync(gracefulDeadline).ConfigureAwait(false);
                }
            }
            catch (TimeoutException failure)
            {
                if (gracefulRequest is { IsCompleted: false })
                {
                    failures.Add(failure);
                }
                else
                {
                    AddCompletedRequestFailure(gracefulRequest, failures);
                }
            }
            catch (OperationCanceledException failure) when (cancellation.IsCancellationRequested)
            {
                if (gracefulRequest is { IsCompleted: false })
                {
                    failures.Add(failure);
                }
                else
                {
                    AddCompletedRequestFailure(gracefulRequest, failures);
                }
            }
            catch (Exception failure) { failures.Add(failure); }
            try { cancellation.Cancel(); }
            catch (Exception failure) { failures.Add(failure); }
            if (gracefulRequest is { IsCompleted: false })
            {
                // 取消を無視した要求の後発例外も観測する。未終端自体は上の回収失敗へ残す。
                _ = gracefulRequest.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            else
            {
                // 待機の時間切れの後、取消までの間に完了した要求は、継続が登録されないためここで観測する。
                AddCompletedRequestFailure(gracefulRequest, failures);
            }
        }
        if (!Completion.IsCompletedSuccessfully)
        {
            try
            {
                if (job is not null)
                {
                    job.Terminate();
                }
                else if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception failure) { failures.Add(failure); }
        }
        try { await WaitForCompletionAsync(deadline).ConfigureAwait(false); }
        catch (Exception failure)
        {
            failures.Add(failure);
            try
            {
                // 出力回収が faulted でも、生存している root と Job の終端を独立に確認する。
                await ConfirmOwnedExitAsync(deadline).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure) { failures.Add(cleanupFailure); }
        }
        CleanupFailure = failures.Count switch { 0 => null, 1 => failures[0], _ => new AggregateException(failures) };
        return new ProcessStopResult(CleanupFailure);
    }

    /// <summary>
    /// 完了済みの正常終了要求が OperationCanceledException 以外で失敗していた場合に、その元の例外だけを回収失敗へ入れます。
    /// 取消は猶予による正常な停止経路なので入れません。待機の時間切れの後に要求が失敗で完了しても、取りこぼしません。
    /// await が送出する例外と同じ参照で既出を判定するため、catch-all が入れた例外は二重に入れません。
    /// </summary>
    private static void AddCompletedRequestFailure(Task? request, List<Exception> failures)
    {
        if (request is { IsFaulted: true, Exception.InnerExceptions: [var failure, ..] }
            && failure is not OperationCanceledException
            && !failures.Contains(failure))
        {
            failures.Add(failure);
        }
    }

    private async Task ConfirmOwnedExitAsync(ProcessDeadline deadline)
    {
        using var cancellation = new CancellationTokenSource(deadline.Remaining);
        Task<long> ownedExit = ObserveOwnedExitAsync(cancellation.Token);
        try
        {
            long observedTimestamp;
            try
            {
                observedTimestamp = await ownedExit.WaitAsync(deadline.Remaining).ConfigureAwait(false);
            }
            catch (TimeoutException) when (ownedExit.IsCompletedSuccessfully)
            {
                observedTimestamp = await ownedExit.ConfigureAwait(false);
            }
            if (!deadline.Includes(observedTimestamp))
            {
                throw new TimeoutException("所有対象の回収確認が期限を超えました。");
            }
        }
        finally
        {
            // WaitAsync のタイマーが先に通知した場合も、残った Job 待機を打ち切る。
            cancellation.Cancel();
            _ = ownedExit.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private async Task<long> ObserveOwnedExitAsync(CancellationToken cancellationToken)
    {
        Task rootExit = process.WaitForExitAsync(cancellationToken);
        Task jobExit = job?.WaitForEmptyAsync(cancellationToken) ?? Task.CompletedTask;
        await Task.WhenAll(rootExit, jobExit).ConfigureAwait(false);
        return Stopwatch.GetTimestamp();
    }

    private void OnRootExited(object? sender, EventArgs args) => ObserveRootExit(releasing: false);

    /// <summary>Windowsの保持handleを非待機で照会し、終了通知・初回確認・解放前確認を直列化します。gate内でProcessの同期通知APIを呼ばず、Process monitorとの逆順取得を防ぎます。</summary>
    private void ObserveRootExit(bool releasing)
    {
        lock (rootGate)
        {
            if (rootExit.Task.IsCompleted) { return; }
            try
            {
                SafeProcessHandle handle = rootHandle ?? throw new InvalidOperationException("rootの観測用handleを取得できませんでした。");
                uint waitResult = WaitForSingleObject(handle, 0);
                if (waitResult == 258) // WAIT_TIMEOUT
                {
                    if (releasing) { FailRootObservation(new InvalidOperationException("rootの終了を確認できないまま所有ハンドルを解放します。")); }
                    return;
                }
                if (waitResult == uint.MaxValue) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                if (waitResult != 0) { throw new InvalidOperationException($"rootの終了照会が未知の結果を返しました: {waitResult}"); }
                if (!GetExitCodeProcess(handle, out uint nativeExitCode)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                // signalを確認済みなので259も実終了コード。Process.ExitCodeと同じ符号付き表現へ変換する。
                int exitCode = unchecked((int)nativeExitCode);
                Volatile.Write(ref rootExitedTimestamp, Stopwatch.GetTimestamp());
                if (exitCode != 0)
                {
                    Interlocked.CompareExchange(ref primaryFailure, new InvalidOperationException($"プロセスが終了コード {exitCode} で失敗しました。"), null);
                }
                rootExit.TrySetResult(exitCode);
            }
            catch (Exception failure) { FailRootObservation(failure); }
        }
    }

    /// <summary>rootGate内で未確定の観測失敗を保存し、既存の主失敗を置き換えません。</summary>
    private void FailRootObservation(Exception failure)
    {
        if (rootExit.Task.IsCompleted) { return; }
        Interlocked.CompareExchange(ref primaryFailure, failure, null);
        rootExit.TrySetException(failure);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint timeout);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    private async Task<ProcessCompletion> CollectAsync()
    {
        try
        {
            int exitCode = await RootExit.ConfigureAwait(false);
            await Task.WhenAll(StandardOutput, StandardError).ConfigureAwait(false);
            if (job is not null)
            {
                await job.WaitForEmptyAsync().ConfigureAwait(false);
            }

            var result = new ProcessCompletion(exitCode, await StandardOutput.ConfigureAwait(false), await StandardError.ConfigureAwait(false));
            Volatile.Write(ref completedTimestamp, Stopwatch.GetTimestamp());
            return result;
        }
        catch (Exception failure)
        {
            Interlocked.CompareExchange(ref primaryFailure, failure, null);
            throw;
        }
    }

    /// <summary>未停止なら10秒の回収期限で停止し、既存の停止があればその期限を引き継ぎます。元RootExitを実値または観測失敗で確定してからハンドルを解放します。</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            ProcessStopResult result = await StopAsync(null, ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            // await using の本体が失敗した際に、回収例外でその主失敗を置き換えない。
            if (result.CleanupFailure is not null && PrimaryFailure is null)
            {
                throw result.CleanupFailure;
            }
        }
        finally
        {
            ObserveRootExit(releasing: true);
            process.Exited -= OnRootExited;
            job?.Dispose();
            stdout.Dispose();
            stderr.Dispose();
            process.Dispose();
        }
    }
}
