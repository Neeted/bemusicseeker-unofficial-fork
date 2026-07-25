using System;

namespace BeMusicSeeker.Models.Update;

internal sealed class UpdaterLaunchReceipt
{
}

internal interface IPreparedUpdaterLaunch
{
    UpdaterLaunchReceipt Start();
}

/// <summary>
/// Opaque updater launch prepared after package validation and updater copy.
/// </summary>
internal sealed class PreparedUpdaterLaunch : IPreparedUpdaterLaunch
{
    private readonly Func<UpdaterLaunchReceipt> start;

    internal PreparedUpdaterLaunch(Func<UpdaterLaunchReceipt> start)
    {
        this.start = start ?? throw new ArgumentNullException(nameof(start));
    }

    public UpdaterLaunchReceipt Start()
    {
        return start();
    }
}
