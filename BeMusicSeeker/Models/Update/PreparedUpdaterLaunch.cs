using System;
using System.Diagnostics;

namespace BeMusicSeeker.Models.Update;

internal interface IPreparedUpdaterLaunch
{
    Process Start();
}

/// <summary>
/// Opaque updater launch prepared after package validation and updater copy.
/// </summary>
internal sealed class PreparedUpdaterLaunch : IPreparedUpdaterLaunch
{
    private readonly Func<Process> start;

    internal PreparedUpdaterLaunch(ProcessStartInfo startInfo)
    {
        if (startInfo == null)
        {
            throw new ArgumentNullException(nameof(startInfo));
        }
        start = () => Process.Start(startInfo);
    }

    public Process Start()
    {
        return start();
    }
}
