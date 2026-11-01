using System.Diagnostics;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;
using static BeMusicSeeker.TestSupport.Components.Update.UpdaterPackageTestSupport;

namespace BeMusicSeeker.TestSupport.Components.Update;

/// <summary>公開updaterのCLI要求と待機を共用し、登録と領域の回収は呼出元のcaseへ残します。</summary>
internal sealed class UpdaterPackageCommands(string updaterPath, ProcessDeadline deadline, Lazy<ProcessDeadline> cleanupDeadline)
{
    /// <summary>このcaseが起動した共通所有プロセスです。親caseが全件を終端してから登録と領域を回収します。</summary>
    internal List<OwnedProcess> Processes { get; } = [];

    private string FindUpdaterExecutable() => updaterPath;

    /// <summary>managed callback検証専用の明示build fixtureで本番journalを準備します。</summary>
    internal (Type Program, object Request) PrepareManagedUpdate(string appDirectoryPath, string packagePath, string backupDirectoryPath)
        => UpdaterPackageTestSupport.PrepareManagedUpdate(Path.Combine(AppContext.BaseDirectory, "fixtures", "managed-updater", "BeMusicSeeker.Updater.dll"), GetExitedProcessId(), appDirectoryPath, packagePath, backupDirectoryPath);

    /// <summary>起動前からJobに所属させ、起動失敗にも親caseと同じ回収期限を渡します。</summary>
    internal OwnedProcess StartOwnedProcess(ProcessStartInfo info)
    {
        DistributionValidation.CheckDeadline(deadline, CancellationToken.None);
        var process = OwnedProcess.Start(new ProcessStartRequest(info.FileName, info.ArgumentList.ToArray(),
            string.IsNullOrEmpty(info.WorkingDirectory) ? Path.GetDirectoryName(updaterPath) ?? throw new InvalidOperationException("updaterの親がありません。") : info.WorkingDirectory), () => cleanupDeadline.Value);
        Processes.Add(process);
        return process;
    }

    /// <summary>保持したprocessのPIDを保存し、root・pipe・Jobの終端後に返します。</summary>
    internal int GetExitedProcessId()
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
        foreach (string arg in new[] { "/d", "/q", "/c", "exit /b 0" }) { info.ArgumentList.Add(arg); }
        OwnedProcess process = StartOwnedProcess(info);
        int id = process.ProcessId;
        WaitForExit(process);
        return id;
    }

    /// <summary>一回の操作期限内でroot・pipe・Jobの全終端を確認します。非zeroの意味は各caseが判定します。</summary>
    internal void WaitForExit(OwnedProcess process) => process.WaitForExitAsync(deadline).GetAwaiter().GetResult();

    /// <summary>元の短い非終了期待を、case全体の期限内で観測します。</summary>
    internal bool WaitForExit(OwnedProcess process, int milliseconds)
    {
        DistributionValidation.CheckDeadline(deadline, CancellationToken.None);
        ProcessDeadline local = deadline.LimitRemaining(TimeSpan.FromMilliseconds(milliseconds));
        try { process.RootExit.WaitAsync(local.Remaining).GetAwaiter().GetResult(); return true; }
        catch (TimeoutException) { DistributionValidation.CheckDeadline(deadline, CancellationToken.None); return false; }
    }

    /// <summary>失敗時も一度だけ始めた10秒期限で全子孫を停止します。</summary>
    internal void StopAndWait(OwnedProcess process)
    {
        ProcessStopResult result = process.StopAsync(null, cleanupDeadline.Value).GetAwaiter().GetResult();
        if (result.CleanupFailure is not null) { throw result.CleanupFailure; }
    }

    private bool WaitForReadyOrProcessExit(OwnedProcess process, string filePath)
    {
        while (!File.Exists(filePath))
        {
            DistributionValidation.CheckDeadline(deadline, CancellationToken.None);
            if (process.RootExit.IsCompleted) { return false; }
            Thread.Sleep(50);
        }
        DistributionValidation.CheckDeadline(deadline, CancellationToken.None);
        return true;
    }

    /// <summary>元の実適用要求を送り、全終端と終了コード0を確認します。</summary>
    internal void RunUpdater(string appDirectoryPath, string packagePath, string backupDirectoryPath, string? restartExecutablePath = null, string? processId = null)
    {
        OwnedProcess process = StartUpdater(appDirectoryPath, packagePath, backupDirectoryPath, restartExecutablePath, processId);
        WaitForExit(process);

        string standardOutput = process.StandardOutput.GetAwaiter().GetResult();
        string standardError = process.StandardError.GetAwaiter().GetResult();
        if (process.RootExit.GetAwaiter().GetResult() != 0)
        {
            Assert.Fail("Updater failed with exit code " + process.RootExit.GetAwaiter().GetResult() + Environment.NewLine + standardOutput + Environment.NewLine + standardError);
        }
    }

    /// <summary>明示回復を実行し、元の診断付き終了コード0を確認します。</summary>
    internal void RunUpdaterRecovery(string appDirectoryPath, string? workingDirectory = null)
    {
        OwnedProcess process = StartUpdaterRecoveryProcess(appDirectoryPath, workingDirectory);
        WaitForExit(process);

        if (process.RootExit.GetAwaiter().GetResult() != 0)
        {
            Assert.Fail(
                "Updater recovery failed with exit code "
                + process.RootExit.GetAwaiter().GetResult()
                + Environment.NewLine
                + process.StandardOutput.GetAwaiter().GetResult()
                + Environment.NewLine
                + process.StandardError.GetAwaiter().GetResult());
        }
    }

    /// <summary>case専有アプリに対する明示回復を、元の作業場所で開始します。</summary>
    internal OwnedProcess StartUpdaterRecoveryProcess(string appDirectoryPath, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindUpdaterExecutable(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(FindUpdaterExecutable()) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("--recover");
        startInfo.ArgumentList.Add("--app-dir");
        startInfo.ArgumentList.Add(appDirectoryPath);
        return StartOwnedProcess(startInfo);
    }

    /// <summary>終了済PIDを渡す実監視要求を実行し、回復の正常終端を確認します。</summary>
    internal void RunUpdaterWatchdogRecovery(string appDirectoryPath)
    {
        string updaterPath = FindUpdaterExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("--watch");
        startInfo.ArgumentList.Add("--app-dir");
        startInfo.ArgumentList.Add(appDirectoryPath);
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(GetExitedProcessId().ToString());
        OwnedProcess process = StartOwnedProcess(startInfo);
        WaitForExit(process);

        if (process.RootExit.GetAwaiter().GetResult() != 0)
        {
            Assert.Fail(
                "Updater watchdog failed with exit code "
                + process.RootExit.GetAwaiter().GetResult()
                + Environment.NewLine
                + process.StandardOutput.GetAwaiter().GetResult()
                + Environment.NewLine
                + process.StandardError.GetAwaiter().GetResult());
        }
    }

    /// <summary>元の不正入力を実CLIへ渡し、成功終了していないことを確認します。</summary>
    internal void RunUpdaterExpectFailure(string appDirectoryPath, string packagePath, string backupDirectoryPath, string? restartExecutablePath = null, string? processId = null)
    {
        OwnedProcess process = StartUpdater(appDirectoryPath, packagePath, backupDirectoryPath, restartExecutablePath, processId);
        WaitForExit(process);

        string output = process.StandardOutput.GetAwaiter().GetResult();
        string error = process.StandardError.GetAwaiter().GetResult();

        if (process.RootExit.GetAwaiter().GetResult() == 0)
        {
            Assert.Fail("Updater unexpectedly succeeded." + Environment.NewLine + output + Environment.NewLine + error);
        }
    }

    /// <summary>元のready判定とproceed公開順を保ち、全待機でcase期限を共有します。</summary>
    internal OwnedProcess StartUpdater(string appDirectoryPath, string packagePath, string backupDirectoryPath, string? restartExecutablePath = null, string? processId = null, bool publishProceed = true)
    {
        restartExecutablePath ??= Path.Combine(appDirectoryPath, "restart.exe");
        processId ??= GetExitedProcessId().ToString();
        string readyFilePath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-ready.txt");
        string decisionFilePath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(readyFilePath)!);
        if (File.Exists(readyFilePath))
        {
            File.Delete(readyFilePath);
        }
        if (File.Exists(decisionFilePath))
        {
            File.Delete(decisionFilePath);
        }

        OwnedProcess process = StartOwnedProcess(CreateUpdaterStartInfo(
            appDirectoryPath,
            packagePath,
            backupDirectoryPath,
            restartExecutablePath,
            processId));
        bool validProcessId = int.TryParse(processId, out int parsedProcessId) && parsedProcessId > 0;
        if (validProcessId && WaitForReadyOrProcessExit(process, readyFilePath) && publishProceed)
        {
            PublishUpdaterDecision(decisionFilePath, "proceed");
        }

        return process;
    }

    /// <summary>このcaseが選んだupdaterとPIDを、共用CLI要求へ渡します。</summary>
    internal ProcessStartInfo CreateUpdaterStartInfo(string appDirectoryPath, string packagePath, string backupDirectoryPath, string? restartExecutablePath = null, string? processId = null)
    {
        return UpdaterPackageTestSupport.CreateUpdaterStartInfo(FindUpdaterExecutable(), appDirectoryPath, packagePath, backupDirectoryPath, restartExecutablePath, processId ?? GetExitedProcessId().ToString());
    }

    /// <summary>既存managed準備と同じ失敗callbackで、巻き戻しと元例外の保持を照合します。</summary>
    internal void ApplyUpdateExpectRestartFailure(string appDirectoryPath, string packagePath, string backupDirectoryPath)
    {
        (Type program, object request) = PrepareManagedUpdate(appDirectoryPath, packagePath, backupDirectoryPath);
        AssertRestartFailure(program, request, Path.Combine(appDirectoryPath, "restart.exe"));
    }
}
