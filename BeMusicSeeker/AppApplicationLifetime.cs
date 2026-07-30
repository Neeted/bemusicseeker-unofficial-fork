using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker;

internal sealed class AppApplicationLifetime : IApplicationLifetimePort
{
    private readonly App application;

    internal AppApplicationLifetime(App application)
    {
        this.application = application ?? throw new System.ArgumentNullException(nameof(application));
    }

    public bool IsFirstStartup => application.firstStartup;

    public void CompleteFirstStartup() => application.firstStartup = false;

    public void MarkCoordinatedShutdownStarted(string reason)
        => App.MarkCoordinatedShutdownStarted(reason);

    public void RequestShutdown() => application.Shutdown();

    public System.Threading.Tasks.Task RestartApplicationAsync()
        => application.RestartApplicationAsync();
}

internal sealed class AppCultureCatalog : ICultureCatalog
{
    private readonly App application;

    internal AppCultureCatalog(App application)
    {
        this.application = application ?? throw new System.ArgumentNullException(nameof(application));
    }

    public IReadOnlyDictionary<string, string> Cultures
        => App.AvailableCultures;
}
