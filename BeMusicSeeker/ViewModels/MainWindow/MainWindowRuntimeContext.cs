using System;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Provides root-owned runtime dependencies to child view models and coordinators without hiding initialization gaps.
/// </summary>
internal sealed class MainWindowRuntimeContext
{
    private readonly Func<BMSLibrary> filesProvider;

    private readonly Func<BMSPlaylist> tablesProvider;

    private readonly Func<LR2Config> lr2ConfigProvider;

    private readonly Func<IBMSPlayer> bmsPlayerProvider;

    private readonly Func<Dispatcher> uiDispatcherProvider;

    internal MainWindowRuntimeContext(
        Func<BMSLibrary> filesProvider,
        Func<BMSPlaylist> tablesProvider,
        Func<LR2Config> lr2ConfigProvider,
        Func<IBMSPlayer> bmsPlayerProvider,
        Func<Dispatcher> uiDispatcherProvider)
    {
        this.filesProvider = filesProvider ?? throw new ArgumentNullException(nameof(filesProvider));
        this.tablesProvider = tablesProvider ?? throw new ArgumentNullException(nameof(tablesProvider));
        this.lr2ConfigProvider = lr2ConfigProvider ?? throw new ArgumentNullException(nameof(lr2ConfigProvider));
        this.bmsPlayerProvider = bmsPlayerProvider ?? throw new ArgumentNullException(nameof(bmsPlayerProvider));
        this.uiDispatcherProvider = uiDispatcherProvider ?? throw new ArgumentNullException(nameof(uiDispatcherProvider));
    }

    /// <summary>
    /// Gets the active BMS library.
    /// </summary>
    internal BMSLibrary Files => RequireInitialized(filesProvider(), nameof(Files));

    /// <summary>
    /// Gets the active playlist model.
    /// </summary>
    internal BMSPlaylist Tables => RequireInitialized(tablesProvider(), nameof(Tables));

    /// <summary>
    /// Gets the active LR2 configuration model.
    /// </summary>
    internal LR2Config Lr2Config => RequireInitialized(lr2ConfigProvider(), nameof(Lr2Config));

    /// <summary>
    /// Gets the active BMS player.
    /// </summary>
    internal IBMSPlayer BmsPlayer => RequireInitialized(bmsPlayerProvider(), nameof(BmsPlayer));

    /// <summary>
    /// Gets the UI dispatcher used by root-owned WPF collections and callbacks.
    /// </summary>
    internal Dispatcher UiDispatcher => RequireInitialized(uiDispatcherProvider(), nameof(UiDispatcher));

    private static T RequireInitialized<T>(T value, string dependencyName) where T : class
    {
        if (value == null)
        {
            throw new InvalidOperationException("MainWindowRuntimeContext." + dependencyName + " is not initialized.");
        }

        return value;
    }
}
