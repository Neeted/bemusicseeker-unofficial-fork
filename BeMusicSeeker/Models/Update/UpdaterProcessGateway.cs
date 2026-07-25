using System;
using System.Diagnostics;

namespace BeMusicSeeker.Models.Update;

internal sealed class UpdaterProcessLaunchRequest
{
    private UpdaterProcessLaunchRequest(
        string executablePath,
        string workingDirectory,
        string applicationDirectory,
        string packagePath,
        string backupDirectory,
        string restartExecutablePath)
    {
        ExecutablePath = executablePath;
        WorkingDirectory = workingDirectory;
        ApplicationDirectory = applicationDirectory;
        PackagePath = packagePath;
        BackupDirectory = backupDirectory;
        RestartExecutablePath = restartExecutablePath;
    }

    internal string ExecutablePath { get; }

    internal string WorkingDirectory { get; }

    internal string ApplicationDirectory { get; }

    internal string PackagePath { get; }

    internal string BackupDirectory { get; }

    internal string RestartExecutablePath { get; }

    internal static UpdaterProcessLaunchRequest Create(
        string executablePath,
        string workingDirectory,
        string applicationDirectory,
        string packagePath,
        string backupDirectory,
        string restartExecutablePath)
    {
        ValidatePath(executablePath, nameof(executablePath));
        ValidatePath(workingDirectory, nameof(workingDirectory));
        ValidatePath(applicationDirectory, nameof(applicationDirectory));
        ValidatePath(packagePath, nameof(packagePath));
        ValidatePath(backupDirectory, nameof(backupDirectory));
        ValidatePath(restartExecutablePath, nameof(restartExecutablePath));
        return new UpdaterProcessLaunchRequest(
            executablePath,
            workingDirectory,
            applicationDirectory,
            packagePath,
            backupDirectory,
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
                "--pid", Process.GetCurrentProcess().Id.ToString(),
                "--restart-exe", request.RestartExecutablePath)
        };
        return new PreparedUpdaterLaunch(() =>
        {
            Process process = startProcess(startInfo);
            return process == null ? null : new UpdaterLaunchReceipt();
        });
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
