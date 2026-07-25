using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using BeMusicSeeker.Models.Localization;

namespace BeMusicSeeker.Models;

/// <summary>
/// Provides the UI scheduler needed by presentation and collection owners.
/// </summary>
internal interface IUiScheduler
{
    Dispatcher Dispatcher { get; }

    bool CheckAccess();
}

/// <summary>
/// Adapts a WPF dispatcher provider at the application composition boundary.
/// </summary>
internal sealed class WpfUiScheduler : IUiScheduler
{
    private readonly Dispatcher dispatcher;

    internal WpfUiScheduler(Func<Dispatcher> dispatcherProvider)
    {
        this.dispatcher = (dispatcherProvider
            ?? throw new ArgumentNullException(nameof(dispatcherProvider)))();
    }

    public Dispatcher Dispatcher => dispatcher;

    public bool CheckAccess() => Dispatcher?.CheckAccess() == true;
}

/// <summary>
/// Owns application-level lifecycle actions consumed by ViewModels.
/// </summary>
internal interface IApplicationLifetimePort
{
    bool IsFirstStartup { get; }

    void CompleteFirstStartup();

    void MarkCoordinatedShutdownStarted(string reason);

    void RequestShutdown();

    void RestartApplication();
}

/// <summary>
/// Supplies the culture names and persisted language values available to the UI.
/// </summary>
internal interface ICultureCatalog
{
    IReadOnlyDictionary<string, string> Cultures { get; }
}

internal sealed class JsonCultureCatalog : ICultureCatalog
{
    private readonly IReadOnlyDictionary<string, string> cultures =
        JsonLanguageCatalog.GetLanguagesSnapshot();

    public IReadOnlyDictionary<string, string> Cultures => cultures;
}
