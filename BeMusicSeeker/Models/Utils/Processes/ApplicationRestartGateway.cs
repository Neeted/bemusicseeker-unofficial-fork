using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.Utils;

internal sealed class ApplicationRestartRequest
{
    private ApplicationRestartRequest(string executablePath, string arguments, string workingDirectory)
    {
        ExecutablePath = executablePath;
        Arguments = arguments ?? string.Empty;
        WorkingDirectory = workingDirectory;
    }

    internal string ExecutablePath { get; }

    internal string Arguments { get; }

    internal string WorkingDirectory { get; }

    internal static ApplicationRestartRequest Create(
        string executablePath,
        string arguments,
        string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        }
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("A working directory is required.", nameof(workingDirectory));
        }

        return new ApplicationRestartRequest(executablePath, arguments, workingDirectory);
    }
}

internal interface IApplicationRestartGateway
{
    void Restart(ApplicationRestartRequest request);
}

internal static class ApplicationRestartGatewayPolicy
{
    internal static IApplicationRestartGateway Current { get; } = new WindowsApplicationRestartGateway();
}

/// <summary>
/// Builds the Windows command-line representation used when restarting the application.
/// </summary>
internal static class ApplicationRestartArgumentsPolicy
{
    /// <summary>
    /// Excludes the executable element from a process argument array and quotes each remaining
    /// argument according to the Windows C runtime command-line rules.
    /// </summary>
    /// <param name="processArguments">The process argument array, including its executable element.</param>
    /// <returns>The canonical argument string for a new process.</returns>
    internal static string BuildCommandLineArguments(IEnumerable<string> processArguments)
    {
        if (processArguments == null)
        {
            throw new ArgumentNullException(nameof(processArguments));
        }

        return string.Join(
            " ",
            processArguments
                .Skip(1)
                .Select(QuoteCommandLineArgument));
    }

    private static string QuoteCommandLineArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }
        if (!argument.Any(char.IsWhiteSpace) && !argument.Contains('"'))
        {
            return argument;
        }

        var builder = new StringBuilder();
        builder.Append('"');
        int backslashCount = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashCount++;
                continue;
            }
            if (c == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }
            builder.Append('\\', backslashCount);
            backslashCount = 0;
            builder.Append(c);
        }
        builder.Append('\\', backslashCount * 2);
        builder.Append('"');
        return builder.ToString();
    }
}

internal sealed class WindowsApplicationRestartGateway : IApplicationRestartGateway
{
    public void Restart(ApplicationRestartRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false
        });
    }
}

internal sealed class ApplicationRestartCoordinator
{
    private readonly ApplicationPathSnapshot applicationPathSnapshot;

    private readonly IApplicationRestartGateway restartGateway;

    private readonly Func<string> argumentsProvider;

    private readonly Action releaseSingleInstanceMutex;

    /// <summary>
    /// 終端 cleanup 後に後継 process を起動する boundary を構築します。
    /// </summary>
    internal ApplicationRestartCoordinator(
        ApplicationPathSnapshot applicationPathSnapshot,
        IApplicationRestartGateway restartGateway,
        Func<string> argumentsProvider,
        Action releaseSingleInstanceMutex)
    {
        this.applicationPathSnapshot = applicationPathSnapshot
            ?? throw new ArgumentNullException(nameof(applicationPathSnapshot));
        this.restartGateway = restartGateway
            ?? throw new ArgumentNullException(nameof(restartGateway));
        this.argumentsProvider = argumentsProvider
            ?? throw new ArgumentNullException(nameof(argumentsProvider));
        this.releaseSingleInstanceMutex = releaseSingleInstanceMutex
            ?? throw new ArgumentNullException(nameof(releaseSingleInstanceMutex));
    }

    /// <summary>
    /// mutex を解放して後継 process を起動します。WPF shutdown は呼び出し側の terminal owner が行います。
    /// </summary>
    internal void Restart()
    {
        string arguments = argumentsProvider() ?? string.Empty;
        releaseSingleInstanceMutex();
        restartGateway.Restart(
            ApplicationRestartRequest.Create(
                applicationPathSnapshot.ExecutablePath,
                arguments,
                applicationPathSnapshot.BaseDirectory));
    }
}
