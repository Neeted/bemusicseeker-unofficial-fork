using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.Tests;

/// <summary>既存Windows probeの一回の実行と、root・両pipe・Job・領域の終端を所有します。</summary>
internal static class FreshPlaybackProbe
{
    /// <summary>旧専用hostの360秒を全実行phaseで共有し、caseの取消を実行待機へ渡し、失敗・取消時も独立した10秒期限で回収します。</summary>
    internal static async Task RunAsync(string caseName, CancellationToken cancellationToken)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        OwnedProcess? process = null;
        Exception? primary = null;
        var cleanup = new List<Exception>();
        try
        {
            string executable = Path.Combine(AppContext.BaseDirectory, "fixtures", "windows-process-probe", "WindowsProcessProbe.exe");
            var request = new ProcessStartRequest(executable, ["playback", caseName], workspace.RootDirectory,
                new Dictionary<string, string?> { [TestRunDirectory.EnvironmentVariable] = workspace.GetPath("child") });
            var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(360));
            process = OwnedProcess.Start(request);
            ProcessCompletion completion = await process.WaitForExitAsync(deadline, cancellationToken);
            AssertSuccessfulResult(caseName, completion);
        }
        catch (Exception exception) { primary = exception; }
        finally
        {
            // 取消済みのcase tokenを回収へ渡さず、全所有資源を独立期限で終端します。
            var cleanupDeadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10));
            if (process is not null)
            {
                try
                {
                    ProcessStopResult stopped = await process.StopAsync(null, cleanupDeadline);
                    if (stopped.CleanupFailure is not null) { cleanup.Add(stopped.CleanupFailure); }
                }
                catch (Exception exception) { cleanup.Add(exception); }
                finally
                {
                    try { await process.DisposeAsync(); }
                    catch (Exception exception) { cleanup.Add(exception); }
                }
            }
            try { workspace.Dispose(); }
            catch (Exception exception) { cleanup.Add(exception); }
        }
        if (primary is not null)
        {
            if (cleanup.Count > 0) { primary.Data["FreshProbeCleanupFailures"] = new AggregateException(cleanup); }
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (cleanup.Count > 0) { throw new AggregateException("fresh probeの回収を確認できません。", cleanup); }
    }

    /// <summary>唯一のJSON結果・要求caseの一致・exit 0・stderr空をすべて成功の必要条件にします。</summary>
    internal static void AssertSuccessfulResult(string caseName, ProcessCompletion completion)
    {
        Assert.AreEqual(0, completion.ExitCode, completion.StandardError);
        Assert.AreEqual(string.Empty, completion.StandardError);
        string[] lines = completion.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(1, lines.Length, "成功JSONは一件だけ必要です。");
        using var document = JsonDocument.Parse(lines[0]);
        Assert.AreEqual(caseName, document.RootElement.GetProperty("Case").GetString());
        Assert.IsTrue(document.RootElement.GetProperty("Passed").GetBoolean());
    }
}
