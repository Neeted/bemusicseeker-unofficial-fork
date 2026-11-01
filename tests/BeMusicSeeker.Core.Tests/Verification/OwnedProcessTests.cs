using System.Diagnostics;
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
        await Assert.ThrowsAsync<TimeoutException>(() => process.WaitForExitAsync(shared));
        await Assert.ThrowsAsync<TimeoutException>(() => process.WaitForRootExitAsync(shared));
        Assert.AreEqual(TimeSpan.Zero, shared.Remaining);
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
