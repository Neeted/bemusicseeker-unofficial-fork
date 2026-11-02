using System.Runtime.ExceptionServices;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.TestSupport.Files;

/// <summary>実junctionの作成process・pipe・リンクを順に終端し、回収不能時は呼出元の専有媒体を保持します。</summary>
internal static class DirectoryJunctionFixture
{
    /// <summary>元のPowerShell環境入力でjunctionを作り、実target確認後の同期callbackとunlinkまでを所有します。</summary>
    /// <remarks>process30秒、終了後pipe5秒、失敗回収5秒を守り、正常待機はsuite期限でも制限します。呼出元は内部treeを別途削除しません。</remarks>
    internal static void Run(TestWorkspace workspace, string link, string target, Action test)
    {
        string? suiteValue = Environment.GetEnvironmentVariable(ProcessDeadline.SuiteDeadlineEnvironmentVariable);
        ProcessDeadline? suiteDeadline = suiteValue is null ? null : ProcessDeadline.Import(suiteValue);
        var failureCleanup = new Lazy<ProcessDeadline>(() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(5)));
        OwnedProcess? process = null;
        Exception? primary = null;
        var failures = new List<Exception>();
        try
        {
            string executable = FindPowerShell();
            var request = new ProcessStartRequest(executable,
                ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path $env:BMS_TEST_JUNCTION_PATH -Target $env:BMS_TEST_JUNCTION_TARGET | Out-Null"],
                workspace.RootDirectory, new Dictionary<string, string?>
                {
                    ["BMS_TEST_JUNCTION_PATH"] = link,
                    ["BMS_TEST_JUNCTION_TARGET"] = target,
                });
            ProcessDeadline rootDeadline = suiteDeadline?.LimitRemaining(TimeSpan.FromSeconds(30))
                ?? ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30));
            process = OwnedProcess.Start(request, () => failureCleanup.Value);
            process.WaitForRootExitAsync(rootDeadline).GetAwaiter().GetResult();
            // pipeの5秒はroot終端後から数え、root局所30秒の残りへ誤って縮めません。
            ProcessDeadline pipeDeadline = suiteDeadline?.LimitRemaining(TimeSpan.FromSeconds(5))
                ?? ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(5));
            ProcessCompletion result = process.WaitForExitAsync(pipeDeadline).GetAwaiter().GetResult();
            Assert.AreEqual(0, result.ExitCode, result.StandardOutput + Environment.NewLine + result.StandardError);
            Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Assert.AreEqual(target, new DirectoryInfo(link).ResolveLinkTarget(returnFinalTarget: true)?.FullName, ignoreCase: true);
            test();
        }
        catch (Exception failure)
        {
            primary = failure;
            if (failure is OwnedProcessStartException { CleanupFailure: not null } startFailure) { failures.Add(startFailure.CleanupFailure); }
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    ProcessDeadline cleanup = primary is null
                        ? suiteDeadline ?? ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(5))
                        : failureCleanup.Value;
                    ProcessStopResult stopped = process.StopAsync(null, cleanup).GetAwaiter().GetResult();
                    if (stopped.CleanupFailure is not null) { failures.Add(stopped.CleanupFailure); }
                    process.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception failure) { failures.Add(failure); }
            }
            // 未終端processがlinkを再作成できる状態ではunlinkもtree回収も行いません。
            if (failures.Count == 0)
            {
                try
                {
                    if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(link); }
                }
                catch (Exception failure) { failures.Add(failure); }
            }
            if (failures.Count > 0) { workspace.PreserveArtifacts(); }
        }
        if (primary is not null)
        {
            if (failures.Count > 0) { primary.Data["DirectoryJunctionCleanupFailures"] = new AggregateException("保持媒体: " + workspace.RootDirectory, failures); }
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (failures.Count > 0) { throw new AggregateException("junctionの回収を確認できません。保持媒体: " + workspace.RootDirectory, failures); }
    }

    private static string FindPowerShell()
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (!Path.IsPathFullyQualified(directory)) { continue; }
            string candidate = Path.Combine(directory, "pwsh.exe");
            if (File.Exists(candidate)) { return Path.GetFullPath(candidate); }
        }
        throw new FileNotFoundException("PowerShell 7の絶対パスを解決できません。");
    }
}
