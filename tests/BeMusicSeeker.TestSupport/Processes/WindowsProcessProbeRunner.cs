using System.Runtime.ExceptionServices;
using System.Text.Json;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.Tests;

/// <summary>既存Windows probeの一回の実行と、root・両pipe・Job・領域の終端を所有します。</summary>
internal static class WindowsProcessProbeRunner
{
    /// <summary>旧専用hostの360秒を全実行phaseで共有し、caseの取消を実行待機へ渡し、失敗・取消時も独立した10秒期限で回収します。</summary>
    internal static async Task RunAsync(string mode, string caseName, CancellationToken cancellationToken)
        => await RunAsync(mode, caseName, cancellationToken, ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(360)), [], null, new Lazy<ProcessDeadline>(() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10))));

    /// <summary>呼出元の操作期限を延長せず、固定probeの追加入力とbool variantを照合します。</summary>
    internal static async Task RunAsync(string mode, string caseName, CancellationToken cancellationToken,
        ProcessDeadline deadline, IReadOnlyList<string> arguments, bool? expectedRestarting, Lazy<ProcessDeadline> cleanupDeadline)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        OwnedProcess? process = null;
        Exception? primary = null;
        var cleanup = new List<Exception>();
        try
        {
            string executable = Path.Combine(AppContext.BaseDirectory, "fixtures", "windows-process-probe", "WindowsProcessProbe.exe");
            var request = new ProcessStartRequest(executable, [mode, caseName, .. arguments], workspace.RootDirectory,
                new Dictionary<string, string?> { [TestRunDirectory.EnvironmentVariable] = workspace.GetPath("child") });
            process = OwnedProcess.Start(request, () => cleanupDeadline.Value);
            ProcessCompletion completion = await process.WaitForExitAsync(deadline, cancellationToken);
            AssertSuccessfulResult(caseName, completion, expectedRestarting);
        }
        catch (Exception exception)
        {
            primary = exception;
            if (exception is OwnedProcessStartException { CleanupFailure: not null } startFailure) { cleanup.Add(startFailure.CleanupFailure); }
        }
        finally
        {
            // 取消済みのcase tokenを回収へ渡さず、全所有資源を独立期限で終端します。
            if (process is not null)
            {
                try
                {
                    ProcessStopResult stopped = await process.StopAsync(null, cleanupDeadline.Value);
                    if (stopped.CleanupFailure is not null) { cleanup.Add(stopped.CleanupFailure); }
                }
                catch (Exception exception) { cleanup.Add(exception); }
                finally
                {
                    try { await process.DisposeAsync(); }
                    catch (Exception exception) { cleanup.Add(exception); }
                }
            }
            if (primary is not null || cleanup.Count > 0) { workspace.PreserveArtifacts(); }
            try { workspace.Dispose(); }
            catch (Exception exception) { cleanup.Add(exception); }
        }
        if (primary is not null)
        {
            if (cleanup.Count > 0) { primary.Data["FreshProbeCleanupFailures"] = new AggregateException(cleanup); }
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (cleanup.Count > 0)
        {
            var failure = new AggregateException("fresh probeの回収を確認できません。", cleanup);
            failure.Data["FreshProbeCleanupFailures"] = new AggregateException(cleanup);
            throw failure;
        }
    }

    /// <summary>唯一のJSON結果・要求caseの一致・exit 0・stderr空をすべて成功の必要条件にします。</summary>
    internal static void AssertSuccessfulResult(string caseName, ProcessCompletion completion, bool? expectedRestarting = null)
    {
        Assert.AreEqual(0, completion.ExitCode, completion.StandardError);
        Assert.AreEqual(string.Empty, completion.StandardError);
        string[] lines = completion.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(1, lines.Length, "成功JSONは一件だけ必要です。");
        using var document = JsonDocument.Parse(lines[0]);
        Assert.AreEqual(caseName, document.RootElement.GetProperty("Case").GetString());
        Assert.IsTrue(document.RootElement.GetProperty("Passed").GetBoolean());
        if (expectedRestarting is bool restarting)
        {
            Assert.AreEqual(restarting, document.RootElement.GetProperty("Restarting").GetBoolean());
        }
    }
}
