using System;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker;

/// <summary>
/// Owns the ordered construction and presentation boundary for the application's main window.
/// </summary>
/// <remarks>
/// The owner keeps the view-model resource, application main-window, and presentation steps
/// explicit so that the startup contract can be exercised without constructing or showing the
/// production <see cref="Views.MainWindow"/>.
/// </remarks>
internal sealed class ApplicationStartupCompositionOwner
{
    private readonly Func<MainWindowViewModel> createViewModel;

    private readonly Action<MainWindowViewModel> assignViewModelResource;

    private readonly Func<MainWindowViewModel, Window> createMainWindow;

    private readonly Action<Window> assignApplicationMainWindow;

    private readonly Action<Window> showMainWindow;

    private readonly Func<Exception, Task> handleFailure;

    private readonly Func<bool> acquireOwnership;

    private readonly Action prepareSettings;

    /// <summary>
    /// Creates an owner for the ordered startup composition and presentation steps.
    /// </summary>
    /// <param name="createViewModel">Creates the one shell view-model instance for startup.</param>
    /// <param name="assignViewModelResource">Assigns that exact view-model to the application resource.</param>
    /// <param name="createMainWindow">Creates the main window for the supplied view-model.</param>
    /// <param name="assignApplicationMainWindow">Assigns that exact window to the application.</param>
    /// <param name="showMainWindow">Presents the assigned window.</param>
    /// <param name="acquireOwnership">Acquires single-instance ownership; false means its terminal denial route has completed.</param>
    /// <param name="prepareSettings">Prepares and materializes settings after ownership and before composition.</param>
    /// <param name="handleFailure">Handles a failure from any startup composition step.</param>
    internal ApplicationStartupCompositionOwner(
        Func<MainWindowViewModel> createViewModel,
        Action<MainWindowViewModel> assignViewModelResource,
        Func<MainWindowViewModel, Window> createMainWindow,
        Action<Window> assignApplicationMainWindow,
        Action<Window> showMainWindow,
        Func<Exception, Task> handleFailure,
        Func<bool> acquireOwnership = null,
        Action prepareSettings = null)
    {
        this.acquireOwnership = acquireOwnership ?? (() => true);
        this.prepareSettings = prepareSettings ?? (() => { });
        this.createViewModel = createViewModel
            ?? throw new ArgumentNullException(nameof(createViewModel));
        this.assignViewModelResource = assignViewModelResource
            ?? throw new ArgumentNullException(nameof(assignViewModelResource));
        this.createMainWindow = createMainWindow
            ?? throw new ArgumentNullException(nameof(createMainWindow));
        this.assignApplicationMainWindow = assignApplicationMainWindow
            ?? throw new ArgumentNullException(nameof(assignApplicationMainWindow));
        this.showMainWindow = showMainWindow
            ?? throw new ArgumentNullException(nameof(showMainWindow));
        this.handleFailure = handleFailure
            ?? throw new ArgumentNullException(nameof(handleFailure));
    }

    /// <summary>
    /// Creates, binds, assigns, and presents the main window in the startup contract order.
    /// </summary>
    /// <returns>A task that completes after presentation or its failure route completes.</returns>
    internal async Task StartAsync()
    {
        try
        {
            if (!acquireOwnership())
        {
            return;
        }
            prepareSettings();
            MainWindowViewModel viewModel = createViewModel()
                ?? throw new InvalidOperationException("Startup composition returned a null view-model.");
            assignViewModelResource(viewModel);

            Window mainWindow = createMainWindow(viewModel)
                ?? throw new InvalidOperationException("Startup composition returned a null main window.");
            assignApplicationMainWindow(mainWindow);
            showMainWindow(mainWindow);
        }
        catch (Exception exception)
        {
            await handleFailure(exception).ConfigureAwait(true);
        }
    }
}
