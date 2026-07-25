using System;
using System.Diagnostics;
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

    private readonly Action shutdown;

    internal ApplicationRestartCoordinator(
        ApplicationPathSnapshot applicationPathSnapshot,
        IApplicationRestartGateway restartGateway,
        Func<string> argumentsProvider,
        Action releaseSingleInstanceMutex,
        Action shutdown)
    {
        this.applicationPathSnapshot = applicationPathSnapshot
            ?? throw new ArgumentNullException(nameof(applicationPathSnapshot));
        this.restartGateway = restartGateway
            ?? throw new ArgumentNullException(nameof(restartGateway));
        this.argumentsProvider = argumentsProvider
            ?? throw new ArgumentNullException(nameof(argumentsProvider));
        this.releaseSingleInstanceMutex = releaseSingleInstanceMutex
            ?? throw new ArgumentNullException(nameof(releaseSingleInstanceMutex));
        this.shutdown = shutdown
            ?? throw new ArgumentNullException(nameof(shutdown));
    }

    internal void Restart()
    {
        string arguments = argumentsProvider() ?? string.Empty;
        releaseSingleInstanceMutex();
        restartGateway.Restart(
            ApplicationRestartRequest.Create(
                applicationPathSnapshot.ExecutablePath,
                arguments,
                applicationPathSnapshot.BaseDirectory));
        shutdown();
    }
}
