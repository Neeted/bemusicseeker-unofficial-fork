using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace BeMusicSeeker.Models.Update;

internal sealed class UpdaterProcessLaunchRequest
{
    private UpdaterProcessLaunchRequest(
        string executablePath,
        string workingDirectory,
        string applicationDirectory,
        string packagePath,
        string backupDirectory,
        string readyFilePath,
        string decisionFilePath,
        string restartExecutablePath)
    {
        ExecutablePath = executablePath;
        WorkingDirectory = workingDirectory;
        ApplicationDirectory = applicationDirectory;
        PackagePath = packagePath;
        BackupDirectory = backupDirectory;
        ReadyFilePath = readyFilePath;
        DecisionFilePath = decisionFilePath;
        RestartExecutablePath = restartExecutablePath;
    }

    internal string ExecutablePath { get; }

    internal string WorkingDirectory { get; }

    internal string ApplicationDirectory { get; }

    internal string PackagePath { get; }

    internal string BackupDirectory { get; }

    internal string ReadyFilePath { get; }

    internal string DecisionFilePath { get; }

    internal string RestartExecutablePath { get; }

    internal static UpdaterProcessLaunchRequest Create(
        string executablePath,
        string workingDirectory,
        string applicationDirectory,
        string packagePath,
        string backupDirectory,
        string readyFilePath,
        string decisionFilePath,
        string restartExecutablePath)
    {
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
            restartExecutablePath);
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
}

internal static class UpdaterProcessGatewayPolicy
{
    internal static IUpdaterProcessGateway Current { get; } = new WindowsUpdaterProcessGateway();
}

internal sealed class WindowsUpdaterProcessGateway : IUpdaterProcessGateway
{
    private readonly Func<ProcessStartInfo, Process> startProcess;

    internal WindowsUpdaterProcessGateway(Func<ProcessStartInfo, Process> startProcess = null)
    {
        this.startProcess = startProcess ?? (startInfo => Process.Start(startInfo));
    }

    public IPreparedUpdaterLaunch Prepare(UpdaterProcessLaunchRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ProcessStartInfo startInfo = new(request.ExecutablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = request.WorkingDirectory,
            Arguments = JoinArguments(
                "--app-dir", request.ApplicationDirectory,
                "--package", request.PackagePath,
                "--backup-dir", request.BackupDirectory,
                "--ready-file", request.ReadyFilePath,
                "--decision-file", request.DecisionFilePath,
                "--pid", Process.GetCurrentProcess().Id.ToString(),
                "--restart-exe", request.RestartExecutablePath)
        };
        return new PreparedUpdaterLaunch(() =>
        {
            Process process = null;
            try
            {
                process = startProcess(startInfo)
                    ?? throw new UpdaterLaunchFailureException("Updater process did not start.");
                WaitForReady(process, request.ReadyFilePath);
                return new UpdaterLaunchReceipt(
                    abort: () =>
                    {
                        try
                        {
                            TryPublishDecision(request.DecisionFilePath, "cancel");
                        }
                        finally
                        {
                            StopProcess(process);
                        }
                    },
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
            catch (UpdaterLaunchFailureException)
            {
                StopProcess(process);
                throw;
            }
            catch (Exception exception)
            {
                StopProcess(process);
                throw new UpdaterLaunchFailureException(
                    "Updater process failed before its ready handshake.",
                    exception);
            }
        });
    }

    private static void WaitForReady(Process process, string readyFilePath)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(readyFilePath))
                {
                    string marker = File.ReadAllText(readyFilePath).Trim();
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

        StopProcess(process);
        throw new UpdaterLaunchFailureException("Updater process did not publish its ready handshake within 10 seconds.");
    }

    private static void StopProcess(Process process)
    {
        if (process == null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
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

    private static void TryPublishDecision(string decisionFilePath, string decision)
    {
        string temporaryPath = decisionFilePath + ".tmp";
        byte[] bytes = Encoding.UTF8.GetBytes(decision);
        using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, decisionFilePath, overwrite: true);
    }

    private static string JoinArguments(params string[] values)
    {
        string[] arguments = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            arguments[i] = QuoteArgument(values[i]);
        }
        return string.Join(" ", arguments);
    }

    private static string QuoteArgument(string value)
    {
        value ??= string.Empty;
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var quoted = new System.Text.StringBuilder();
        quoted.Append('"');
        int backslashCount = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                backslashCount++;
                continue;
            }

            if (c == '"')
            {
                quoted.Append('\\', backslashCount * 2 + 1);
                quoted.Append('"');
                backslashCount = 0;
                continue;
            }

            quoted.Append('\\', backslashCount);
            backslashCount = 0;
            quoted.Append(c);
        }

        quoted.Append('\\', backslashCount * 2);
        quoted.Append('"');
        return quoted.ToString();
    }
}
