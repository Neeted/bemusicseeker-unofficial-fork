using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace BeMusicSeeker.Models.Update;

internal sealed class UpdaterProcessLaunchRequest
{
    /// <summary>検証済みの値から更新ツール起動要求を作ります。外部からは<see cref="Create"/>を使います。</summary>
    private UpdaterProcessLaunchRequest(
        string executablePath,
        string workingDirectory,
        string applicationDirectory,
        string packagePath,
        string backupDirectory,
        string readyFilePath,
        string decisionFilePath,
        string restartExecutablePath,
        IReadOnlyList<string> restartArguments)
    {
        ExecutablePath = executablePath;
        WorkingDirectory = workingDirectory;
        ApplicationDirectory = applicationDirectory;
        PackagePath = packagePath;
        BackupDirectory = backupDirectory;
        ReadyFilePath = readyFilePath;
        DecisionFilePath = decisionFilePath;
        RestartExecutablePath = restartExecutablePath;
        RestartArguments = restartArguments;
    }

    internal string ExecutablePath { get; }

    internal string WorkingDirectory { get; }

    internal string ApplicationDirectory { get; }

    internal string PackagePath { get; }

    internal string BackupDirectory { get; }

    internal string ReadyFilePath { get; }

    internal string DecisionFilePath { get; }

    internal string RestartExecutablePath { get; }

    /// <summary>更新後にアプリを再起動するときの引数です。実行ファイルのパスは含みません。</summary>
    internal IReadOnlyList<string> RestartArguments { get; }

    /// <summary>パスと再起動引数を検証し、変更不能な更新ツール起動要求を作ります。</summary>
    /// <param name="executablePath">更新ツールの実行ファイルです。</param>
    /// <param name="workingDirectory">更新ツールの作業ディレクトリです。</param>
    /// <param name="applicationDirectory">更新対象のアプリのディレクトリです。</param>
    /// <param name="packagePath">検証済みの更新ZIPです。</param>
    /// <param name="backupDirectory">復元用の退避先です。</param>
    /// <param name="readyFilePath">更新ツールの受付通知ファイルです。</param>
    /// <param name="decisionFilePath">アプリの続行・取消の決定ファイルです。</param>
    /// <param name="restartExecutablePath">更新後に再起動する実行ファイルです。</param>
    /// <param name="restartArguments">更新後の再起動で渡す引数です。実行ファイルのパスは含めず、引数なしは空の列にします。</param>
    internal static UpdaterProcessLaunchRequest Create(
        string executablePath,
        string workingDirectory,
        string applicationDirectory,
        string packagePath,
        string backupDirectory,
        string readyFilePath,
        string decisionFilePath,
        string restartExecutablePath,
        IReadOnlyList<string> restartArguments)
    {
        ArgumentNullException.ThrowIfNull(restartArguments);
        ValidatePath(executablePath, nameof(executablePath));
        ValidatePath(workingDirectory, nameof(workingDirectory));
        ValidatePath(applicationDirectory, nameof(applicationDirectory));
        ValidatePath(packagePath, nameof(packagePath));
        ValidatePath(backupDirectory, nameof(backupDirectory));
        ValidatePath(readyFilePath, nameof(readyFilePath));
        ValidatePath(decisionFilePath, nameof(decisionFilePath));
        ValidatePath(restartExecutablePath, nameof(restartExecutablePath));
        return new UpdaterProcessLaunchRequest(
            executablePath,
            workingDirectory,
            applicationDirectory,
            packagePath,
            backupDirectory,
            readyFilePath,
            decisionFilePath,
            restartExecutablePath,
            Array.AsReadOnly(restartArguments.ToArray()));
    }

    private static void ValidatePath(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A path is required.", parameterName);
        }
    }
}

internal interface IUpdaterProcessGateway
{
    IPreparedUpdaterLaunch Prepare(UpdaterProcessLaunchRequest request);

    bool RecoverIncompleteTransaction(string executablePath, string applicationDirectory);
}

internal static class UpdaterProcessGatewayPolicy
{
    internal static IUpdaterProcessGateway Current { get; } = new WindowsUpdaterProcessGateway();
}

internal sealed class WindowsUpdaterProcessGateway : IUpdaterProcessGateway
{
    /// <summary>起動処理が<see cref="Process"/>を返した後、受付の公開を待つ製品既定の期限です。</summary>
    internal static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(10);

    /// <summary>未完了の更新処理の回復を待つ製品既定の期限です。</summary>
    internal static readonly TimeSpan DefaultRecoveryTimeout = TimeSpan.FromMinutes(2);

    /// <summary>cancel公開後の正常終了と、強制停止後の終了確認をそれぞれ待つ製品既定の期限です。</summary>
    internal static readonly TimeSpan DefaultTerminationTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<ProcessStartInfo, Process> startProcess;

    private readonly TimeSpan recoveryTimeout;

    private readonly TimeSpan terminationTimeout;

    private readonly TimeSpan readyTimeout;

    /// <summary>このインスタンスが使う受付の期限です。製品の構築が既定値を使うことの検査に使います。</summary>
    internal TimeSpan ReadyTimeout => readyTimeout;

    /// <summary>このインスタンスが使う回復の期限です。</summary>
    internal TimeSpan RecoveryTimeout => recoveryTimeout;

    /// <summary>このインスタンスが使う停止の期限です。</summary>
    internal TimeSpan TerminationTimeout => terminationTimeout;

    /// <summary>
    /// 製品は全期限を既定値で構築します。期限の引数は、期限切れの経路を製品既定の実時間を待たずに検査するためのものです。
    /// </summary>
    internal WindowsUpdaterProcessGateway(
        Func<ProcessStartInfo, Process> startProcess = null,
        TimeSpan? recoveryTimeout = null,
        TimeSpan? terminationTimeout = null,
        TimeSpan? readyTimeout = null)
    {
        this.startProcess = startProcess ?? (startInfo => Process.Start(startInfo));
        this.recoveryTimeout = recoveryTimeout ?? DefaultRecoveryTimeout;
        this.terminationTimeout = terminationTimeout ?? DefaultTerminationTimeout;
        this.readyTimeout = readyTimeout ?? DefaultReadyTimeout;
        if (this.readyTimeout <= TimeSpan.Zero
            || this.readyTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(readyTimeout));
        }
        if (this.recoveryTimeout <= TimeSpan.Zero
            || this.recoveryTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryTimeout));
        }
        if (this.terminationTimeout <= TimeSpan.Zero
            || this.terminationTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(terminationTimeout));
        }
    }

    /// <summary>
    /// 型付き要求とInvariantのPIDを<see cref="ProcessStartInfo.ArgumentList"/>で非表示の更新プロセスへ渡す準備をします。
    /// 再起動引数は<c>--restart-arguments-json</c>へ文字列配列のJSON1トークンとして渡し、引用はArgumentListに委ねます。
    /// </summary>
    public IPreparedUpdaterLaunch Prepare(UpdaterProcessLaunchRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ProcessStartInfo startInfo = new(request.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory,
        };
        AddArguments(
            startInfo,
            "--app-dir", request.ApplicationDirectory,
            "--package", request.PackagePath,
            "--backup-dir", request.BackupDirectory,
            "--ready-file", request.ReadyFilePath,
            "--decision-file", request.DecisionFilePath,
            "--pid", Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
            "--restart-exe", request.RestartExecutablePath,
            "--restart-arguments-json", JsonSerializer.Serialize(request.RestartArguments));
        return new PreparedUpdaterLaunch(() =>
        {
            Process process = null;
            try
            {
                process = startProcess(startInfo)
                    ?? throw new UpdaterLaunchFailureException("Updater process did not start.");
                WaitForReady(process, request.ReadyFilePath);
                return new UpdaterLaunchReceipt(
                    abort: () => AbortUpdaterProcess(process, request.DecisionFilePath),
                    proceed: () =>
                    {
                        try
                        {
                            EnsureProcessIsRunning(process);
                            TryPublishDecision(request.DecisionFilePath, "proceed");
                        }
                        catch (UpdaterLaunchFailureException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            throw new UpdaterLaunchFailureException(
                                "Updater proceed handshake could not be published.",
                                exception);
                        }
                    });
            }
            catch (UpdaterLaunchFailureException exception)
            {
                StopProcessAfterLaunchFailure(process, exception);
                throw;
            }
            catch (Exception exception)
            {
                var launchFailure = new UpdaterLaunchFailureException(
                    "Updater process failed before its ready handshake.",
                    exception);
                StopProcessAfterLaunchFailure(process, launchFailure);
                throw launchFailure;
            }
        });
    }

    public bool RecoverIncompleteTransaction(string executablePath, string applicationDirectory)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("An updater executable path is required.", nameof(executablePath));
        }
        if (string.IsNullOrWhiteSpace(applicationDirectory))
        {
            throw new ArgumentException("An application directory is required.", nameof(applicationDirectory));
        }

        ProcessStartInfo startInfo = new(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? applicationDirectory
        };
        AddArguments(startInfo, "--recover", "--app-dir", applicationDirectory);
        using Process process = startProcess(startInfo)
            ?? throw new UpdaterLaunchFailureException("Updater recovery process did not start.");
        if (!process.WaitForExit((int)recoveryTimeout.TotalMilliseconds))
        {
            try
            {
                StopProcess(process);
            }
            catch (Exception terminationFailure)
            {
                throw new UpdaterLaunchFailureException(
                    "Updater transaction recovery timed out and process termination could not be confirmed. "
                        + "Recovery will be retried on the next startup.",
                    terminationFailure);
            }
            throw new UpdaterLaunchFailureException(
                "Updater transaction recovery did not finish within "
                    + recoveryTimeout.TotalSeconds
                    + " seconds and was terminated. Recovery will be retried on the next startup.");
        }
        if (process.ExitCode != 0)
        {
            if (process.ExitCode == 2)
            {
                return false;
            }

            throw new UpdaterLaunchFailureException(
                "Updater transaction recovery failed (exit code " + process.ExitCode + ").");
        }

        return true;
    }

    /// <summary>完成名への移動用ハンドルが残っていても受付内容を読み取り、受付の期限内の公開を確認します。</summary>
    private void WaitForReady(Process process, string readyFilePath)
    {
        DateTime deadline = DateTime.UtcNow + readyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(readyFilePath))
                {
                    // Windowsでは名前の変更後も移動用のDELETEハンドルが閉じるまで残り得ます。
                    using var stream = new FileStream(readyFilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string marker = reader.ReadToEnd().Trim();
                    if (string.Equals(marker, "1", StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                bool hasExited;
                try
                {
                    hasExited = process.HasExited;
                }
                catch (InvalidOperationException exception)
                {
                    throw new UpdaterLaunchFailureException("Updater process could not be observed after start.", exception);
                }
                if (hasExited)
                {
                    throw new UpdaterLaunchFailureException(
                        "Updater process exited before publishing its ready handshake (exit code "
                        + process.ExitCode
                        + ").");
                }
            }
            catch (UpdaterLaunchFailureException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new UpdaterLaunchFailureException(
                    "Updater ready handshake could not be observed.",
                    exception);
            }
            Thread.Sleep(50);
        }

        throw new UpdaterLaunchFailureException(
            "Updater process did not publish its ready handshake within " + readyTimeout.TotalSeconds + " seconds.");
    }

    /// <summary>cancel公開後は既存停止期限で正常終端を待ち、公開失敗・無応答だけを強制停止して元失敗を保持します。</summary>
    private void AbortUpdaterProcess(Process process, string decisionFilePath)
    {
        Exception decisionFailure = null;
        try
        {
            TryPublishDecision(decisionFilePath, "cancel");
        }
        catch (Exception exception)
        {
            decisionFailure = exception;
        }

        Exception terminationFailure = null;
        try
        {
            if (decisionFailure != null || !process.WaitForExit((int)terminationTimeout.TotalMilliseconds))
            {
                StopProcess(process);
            }
            else if (process.ExitCode != 0)
            {
                throw new UpdaterLaunchFailureException("Updater cancellation did not complete successfully. exitCode=" + process.ExitCode);
            }
        }
        catch (Exception exception)
        {
            terminationFailure = exception;
        }

        if (decisionFailure != null && terminationFailure != null)
        {
            throw new UpdaterLaunchFailureException(
                "Updater cancel handshake failed and process termination could not be confirmed.",
                new AggregateException(decisionFailure, terminationFailure));
        }
        if (terminationFailure != null)
        {
            throw new UpdaterLaunchFailureException(
                "Updater process termination could not be confirmed after cancellation.",
                terminationFailure);
        }
        if (decisionFailure != null)
        {
            throw new UpdaterLaunchFailureException(
                "Updater cancel handshake could not be published.",
                decisionFailure);
        }
    }

    private void StopProcessAfterLaunchFailure(Process process, UpdaterLaunchFailureException launchFailure)
    {
        try
        {
            StopProcess(process);
        }
        catch (Exception terminationFailure)
        {
            throw new UpdaterLaunchFailureException(
                launchFailure.Message + " Updater process termination could not be confirmed.",
                new AggregateException(launchFailure, terminationFailure));
        }
    }

    private void StopProcess(Process process)
    {
        if (process == null)
        {
            return;
        }

        bool hasExited;
        try
        {
            hasExited = process.HasExited;
        }
        catch (Exception exception)
        {
            throw new UpdaterLaunchFailureException(
                "Updater process state could not be observed before termination.",
                exception);
        }
        if (hasExited)
        {
            return;
        }
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            throw new UpdaterLaunchFailureException(
                "Updater process could not be terminated.",
                exception);
        }
        try
        {
            if (!process.WaitForExit((int)terminationTimeout.TotalMilliseconds))
            {
                throw new UpdaterLaunchFailureException(
                    "Updater process termination was requested but could not be confirmed within "
                        + terminationTimeout.TotalSeconds
                        + " seconds.");
            }
        }
        catch (UpdaterLaunchFailureException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new UpdaterLaunchFailureException(
                "Updater process termination could not be observed.",
                exception);
        }
    }

    private static void EnsureProcessIsRunning(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                throw new UpdaterLaunchFailureException(
                    "Updater process exited before the application could approve the launch.");
            }
        }
        catch (InvalidOperationException exception) when (exception is not UpdaterLaunchFailureException)
        {
            throw new UpdaterLaunchFailureException(
                "Updater process could not be observed before the application approved the launch.",
                exception);
        }
    }

    /// <summary>UTF-8・改行なしの決定を完成後に公開し、既存の決定は置換します。</summary>
    private static void TryPublishDecision(string decisionFilePath, string decision)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(decision);
        UpdateMarkerPublisher.Publish(decisionFilePath, overwrite: true, stream => stream.Write(bytes, 0, bytes.Length));
    }

    private static void AddArguments(ProcessStartInfo startInfo, params string[] values)
    {
        foreach (string value in values)
        {
            startInfo.ArgumentList.Add(value);
        }
    }
}
