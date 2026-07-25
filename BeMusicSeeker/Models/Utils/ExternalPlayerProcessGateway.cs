using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

internal sealed class ExternalPlayerProcessDiscoveryRequest
{
    private ExternalPlayerProcessDiscoveryRequest(IReadOnlyList<string> processNames)
    {
        ProcessNames = processNames;
    }

    internal IReadOnlyList<string> ProcessNames { get; }

    internal static ExternalPlayerProcessDiscoveryRequest Create(params string[] processNames)
    {
        if (processNames == null || processNames.Length == 0)
        {
            throw new ArgumentException("At least one process name is required.", nameof(processNames));
        }

        string[] normalizedNames = processNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedNames.Length == 0)
        {
            throw new ArgumentException("At least one process name is required.", nameof(processNames));
        }

        return new ExternalPlayerProcessDiscoveryRequest(Array.AsReadOnly(normalizedNames));
    }
}

internal sealed class ExternalPlayerProcessLaunchRequest
{
    private ExternalPlayerProcessLaunchRequest(
        string executablePath,
        string arguments,
        ProcessWindowStyle windowStyle)
    {
        ExecutablePath = executablePath;
        Arguments = arguments ?? string.Empty;
        WindowStyle = windowStyle;
    }

    internal string ExecutablePath { get; }

    internal string Arguments { get; }

    internal ProcessWindowStyle WindowStyle { get; }

    internal static ExternalPlayerProcessLaunchRequest Create(
        string executablePath,
        string arguments,
        ProcessWindowStyle windowStyle = ProcessWindowStyle.Normal)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        }

        return new ExternalPlayerProcessLaunchRequest(executablePath, arguments, windowStyle);
    }
}

internal interface IExternalPlayerProcessSession
{
    event EventHandler Exited;

    bool HasExited { get; }

    ExternalWindowHandle MainWindowHandle { get; }

    IReadOnlyList<int> ThreadIds { get; }

    void Start();

    void CloseMainWindow();

    void Kill();
}

internal interface IExternalPlayerProcessGateway
{
    IReadOnlyList<IExternalPlayerProcessSession> FindExisting(ExternalPlayerProcessDiscoveryRequest request);

    IExternalPlayerProcessSession Prepare(ExternalPlayerProcessLaunchRequest request);
}

internal static class ExternalPlayerProcessGatewayPolicy
{
    internal static IExternalPlayerProcessGateway Current { get; } = new WindowsExternalPlayerProcessGateway();
}

internal sealed class WindowsExternalPlayerProcessGateway : IExternalPlayerProcessGateway
{
    public IReadOnlyList<IExternalPlayerProcessSession> FindExisting(ExternalPlayerProcessDiscoveryRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var sessions = new List<IExternalPlayerProcessSession>();
        foreach (string processName in request.ProcessNames)
        {
            sessions.AddRange(Process.GetProcessesByName(processName)
                .Select(process => new WindowsExternalPlayerProcessSession(process, started: true)));
        }
        return sessions;
    }

    public IExternalPlayerProcessSession Prepare(ExternalPlayerProcessLaunchRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(request.ExecutablePath)
            {
                WindowStyle = request.WindowStyle,
                Arguments = request.Arguments
            }
        };
        return new WindowsExternalPlayerProcessSession(process, started: false);
    }
}

internal sealed class WindowsExternalPlayerProcessSession : IExternalPlayerProcessSession
{
    private readonly Process process;

    private bool started;

    private bool raisingEventsEnabled;

    internal WindowsExternalPlayerProcessSession(Process process, bool started)
    {
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        this.started = started;
    }

    public event EventHandler Exited
    {
        add
        {
            EnsureRaisingEventsEnabled();
            process.Exited += value;
        }
        remove => process.Exited -= value;
    }

    public bool HasExited => process.HasExited;

    public ExternalWindowHandle MainWindowHandle => new(process.MainWindowHandle);

    public IReadOnlyList<int> ThreadIds => process.Threads.Cast<ProcessThread>().Select(thread => thread.Id).ToArray();

    public void Start()
    {
        if (started)
        {
            throw new InvalidOperationException("The external player process session has already started.");
        }

        EnsureRaisingEventsEnabled();
        process.Start();
        started = true;
    }

    private void EnsureRaisingEventsEnabled()
    {
        if (raisingEventsEnabled)
        {
            return;
        }

        process.EnableRaisingEvents = true;
        raisingEventsEnabled = true;
    }

    public void CloseMainWindow()
    {
        process.CloseMainWindow();
    }

    public void Kill()
    {
        process.Kill();
    }
}
