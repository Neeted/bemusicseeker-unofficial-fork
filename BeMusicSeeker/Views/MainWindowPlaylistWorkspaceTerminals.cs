using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>
/// Provides the typed subscription boundary for playlist URL-install presentation requests.
/// </summary>
internal interface IMainWindowPlaylistUrlInstallTreeExpansionEventSource
{
    /// <summary>
    /// Subscribes one shell presentation callback.
    /// </summary>
    /// <param name="handler">The shell callback to invoke when the owner publishes completion.</param>
    void Subscribe(Action handler);

    /// <summary>
    /// Removes one previously subscribed shell presentation callback.
    /// </summary>
    /// <param name="handler">The shell callback to remove.</param>
    void Unsubscribe(Action handler);
}

/// <summary>
/// Adapts the existing playlist-workspace completion event to the shell's narrow subscription boundary.
/// </summary>
internal sealed class MainWindowPlaylistUrlInstallTreeExpansionEventSource
    : IMainWindowPlaylistUrlInstallTreeExpansionEventSource
{
    private readonly PlaylistWorkspaceViewModel playlistWorkspace;

    /// <summary>
    /// Initializes the adapter over one existing playlist workspace owner.
    /// </summary>
    /// <param name="playlistWorkspace">The workspace that owns completion publication.</param>
    internal MainWindowPlaylistUrlInstallTreeExpansionEventSource(
        PlaylistWorkspaceViewModel playlistWorkspace)
    {
        this.playlistWorkspace = playlistWorkspace
            ?? throw new ArgumentNullException(nameof(playlistWorkspace));
    }

    /// <inheritdoc />
    public void Subscribe(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        playlistWorkspace.PlaylistUrlInstallTreeExpansionRequested += handler;
    }

    /// <inheritdoc />
    public void Unsubscribe(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        playlistWorkspace.PlaylistUrlInstallTreeExpansionRequested -= handler;
    }
}

/// <summary>
/// Provides the shell's best-effort foreground terminal after a successful playlist navigation.
/// </summary>
internal sealed class MainWindowForegroundTerminal
{
    private readonly Func<WindowState> readWindowState;

    private readonly Action restoreWindow;

    private readonly Func<bool> activateWindow;

    private readonly Func<bool> focusWindow;

    /// <summary>
    /// Initializes the foreground terminal over the shell's presentation operations.
    /// </summary>
    /// <param name="readWindowState">Reads the current shell state.</param>
    /// <param name="restoreWindow">Restores a minimized shell to its normal state.</param>
    /// <param name="activateWindow">Attempts to activate the shell.</param>
    /// <param name="focusWindow">Attempts to focus the shell.</param>
    internal MainWindowForegroundTerminal(
        Func<WindowState> readWindowState,
        Action restoreWindow,
        Func<bool> activateWindow,
        Func<bool> focusWindow)
    {
        this.readWindowState = readWindowState
            ?? throw new ArgumentNullException(nameof(readWindowState));
        this.restoreWindow = restoreWindow
            ?? throw new ArgumentNullException(nameof(restoreWindow));
        this.activateWindow = activateWindow
            ?? throw new ArgumentNullException(nameof(activateWindow));
        this.focusWindow = focusWindow
            ?? throw new ArgumentNullException(nameof(focusWindow));
    }

    /// <summary>
    /// Restores a minimized shell, then makes one best-effort activation and focus attempt.
    /// Presentation failures do not roll back the already-applied navigation.
    /// </summary>
    internal void FocusMainWindow()
    {
        try
        {
            if (readWindowState() == WindowState.Minimized)
            {
                restoreWindow();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            _ = activateWindow();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            _ = focusWindow();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Creates the production terminal over the main shell's WPF state and activation methods.
    /// </summary>
    /// <param name="owner">The main shell to restore, activate, and focus.</param>
    /// <returns>A foreground terminal bound to the supplied shell.</returns>
    internal static MainWindowForegroundTerminal Create(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new MainWindowForegroundTerminal(
            () => owner.WindowState,
            () => owner.WindowState = WindowState.Normal,
            owner.Activate,
            owner.Focus);
    }
}

/// <summary>
/// Provides the narrow playlist-workspace terminals used by compiled MainWindow routes.
/// </summary>
internal sealed class MainWindowPlaylistWorkspaceTerminals
{
    /// <summary>
    /// Initializes the playlist-workspace terminal bundle.
    /// </summary>
    /// <param name="entryRemoval">Playlist-entry removal terminal.</param>
    /// <param name="tableLevelOverwrite">Playlist-table level overwrite terminal.</param>
    /// <param name="tableRemoval">Playlist-table removal terminal.</param>
    /// <param name="collectionImport">External collection and built-in playlist import terminal.</param>
    /// <param name="urlAcquisition">Playlist URL action and availability terminal.</param>
    /// <param name="urlInstallTreeExpansionEventSource">Typed owner completion-event source.</param>
    /// <param name="urlInstallTreeExpansion">Install-tree presentation expansion terminal.</param>
    /// <param name="tablesReload">Playlist-root table reload terminal.</param>
    internal MainWindowPlaylistWorkspaceTerminals(
        MainWindowPlaylistEntryRemovalTerminal entryRemoval,
        MainWindowPlaylistTableLevelOverwriteTerminal tableLevelOverwrite,
        MainWindowPlaylistTableRemovalTerminal tableRemoval,
        MainWindowPlaylistCollectionImportTerminal collectionImport,
        MainWindowPlaylistUrlAcquisitionTerminal urlAcquisition,
        IMainWindowPlaylistUrlInstallTreeExpansionEventSource urlInstallTreeExpansionEventSource,
        MainWindowPlaylistUrlInstallTreeExpansionTerminal urlInstallTreeExpansion,
        MainWindowPlaylistTablesReloadTerminal tablesReload)
    {
        EntryRemoval = entryRemoval ?? throw new ArgumentNullException(nameof(entryRemoval));
        TableLevelOverwrite = tableLevelOverwrite
            ?? throw new ArgumentNullException(nameof(tableLevelOverwrite));
        TableRemoval = tableRemoval ?? throw new ArgumentNullException(nameof(tableRemoval));
        CollectionImport = collectionImport ?? throw new ArgumentNullException(nameof(collectionImport));
        UrlAcquisition = urlAcquisition ?? throw new ArgumentNullException(nameof(urlAcquisition));
        UrlInstallTreeExpansionEventSource = urlInstallTreeExpansionEventSource
            ?? throw new ArgumentNullException(nameof(urlInstallTreeExpansionEventSource));
        UrlInstallTreeExpansion = urlInstallTreeExpansion
            ?? throw new ArgumentNullException(nameof(urlInstallTreeExpansion));
        TablesReload = tablesReload ?? throw new ArgumentNullException(nameof(tablesReload));
    }

    /// <summary>
    /// Gets the playlist-entry removal terminal.
    /// </summary>
    internal MainWindowPlaylistEntryRemovalTerminal EntryRemoval { get; }

    /// <summary>
    /// Gets the playlist-table level overwrite terminal.
    /// </summary>
    internal MainWindowPlaylistTableLevelOverwriteTerminal TableLevelOverwrite { get; }

    /// <summary>
    /// Gets the playlist-table removal terminal.
    /// </summary>
    internal MainWindowPlaylistTableRemovalTerminal TableRemoval { get; }

    /// <summary>
    /// Gets the external playlist collection import terminal.
    /// </summary>
    internal MainWindowPlaylistCollectionImportTerminal CollectionImport { get; }

    /// <summary>
    /// Gets the playlist URL acquisition terminal.
    /// </summary>
    internal MainWindowPlaylistUrlAcquisitionTerminal UrlAcquisition { get; }

    /// <summary>
    /// Gets the typed owner completion-event source used by shell subscription wiring.
    /// </summary>
    internal IMainWindowPlaylistUrlInstallTreeExpansionEventSource UrlInstallTreeExpansionEventSource { get; }

    /// <summary>
    /// Gets the install-tree expansion presentation terminal.
    /// </summary>
    internal MainWindowPlaylistUrlInstallTreeExpansionTerminal UrlInstallTreeExpansion { get; }

    /// <summary>
    /// Gets the playlist-root table reload terminal.
    /// </summary>
    internal MainWindowPlaylistTablesReloadTerminal TablesReload { get; }

    /// <summary>
    /// Creates terminals that delegate directly to the existing playlist workspace owners.
    /// </summary>
    /// <param name="viewModel">The shell view model that owns playlist workspace behavior.</param>
    /// <param name="installTreeExpansion">Presentation action invoked after URL-install completion.</param>
    /// <returns>A bundle preserving the existing owner routes.</returns>
    internal static MainWindowPlaylistWorkspaceTerminals Create(
        MainWindowViewModel viewModel,
        Action installTreeExpansion)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowPlaylistWorkspaceTerminals(
            new MainWindowPlaylistEntryRemovalTerminal(
                viewModel.PlaylistWorkspace.DeleteSelectedEntriesAsync),
            new MainWindowPlaylistTableLevelOverwriteTerminal(
                viewModel.PlaylistWorkspace.PlaylistTableLevelOverwriteWorkflow.OverwriteAsync),
            new MainWindowPlaylistTableRemovalTerminal(
                viewModel.PlaylistWorkspace.PlaylistRemovalWorkflow.RemoveTreeTableAsync),
            new MainWindowPlaylistCollectionImportTerminal(
                viewModel.PlaylistWorkspace.TryEnqueueExternalPlaylistCollectionImport,
                viewModel.PlaylistWorkspace.TryEnqueueBuiltInExternalPlaylistImport),
            new MainWindowPlaylistUrlAcquisitionTerminal(
                viewModel.PlaylistWorkspace.RunSinglePlaylistUrlAsync,
                viewModel.PlaylistWorkspace.RunPlaylistUrlActionAsync,
                viewModel.PlaylistWorkspace.RunPlaylistExternalPackageLookupAsync,
                viewModel.PlaylistWorkspace.CapturePlaylistUrlContextMenuAvailability),
            new MainWindowPlaylistUrlInstallTreeExpansionEventSource(
                viewModel.PlaylistWorkspace),
            new MainWindowPlaylistUrlInstallTreeExpansionTerminal(
                installTreeExpansion ?? throw new ArgumentNullException(nameof(installTreeExpansion))),
            MainWindowPlaylistTablesReloadTerminal.Create(viewModel));
    }
}

/// <summary>
/// Narrows the compiled playlist-root reload click to the existing view-model route.
/// </summary>
internal sealed class MainWindowPlaylistTablesReloadTerminal
{
    private readonly Func<Task> reloadTablesAsync;

    /// <summary>
    /// Initializes the playlist-root reload terminal.
    /// </summary>
    /// <param name="reloadTablesAsync">The existing initialized table-reload route.</param>
    internal MainWindowPlaylistTablesReloadTerminal(Func<Task> reloadTablesAsync)
    {
        this.reloadTablesAsync = reloadTablesAsync
            ?? throw new ArgumentNullException(nameof(reloadTablesAsync));
    }

    /// <summary>
    /// Runs one playlist-root table reload request.
    /// </summary>
    /// <returns>The existing reload operation task.</returns>
    internal Task ReloadAsync() => reloadTablesAsync();

    /// <summary>
    /// Creates the default terminal backed by the shell's typed reload route.
    /// </summary>
    /// <param name="viewModel">The shell view model that owns reload orchestration.</param>
    /// <returns>A terminal that delegates to the existing reload route.</returns>
    internal static MainWindowPlaylistTablesReloadTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowPlaylistTablesReloadTerminal(viewModel.ReloadTablesAsync);
    }
}

/// <summary>
/// Narrows compiled playlist import menu clicks to the playlist workspace queue owner.
/// </summary>
internal sealed class MainWindowPlaylistCollectionImportTerminal
{
    private readonly Func<BMSTableSimple, bool> enqueueCollectionImport;

    private readonly Func<string, bool> enqueueBuiltInImport;

    /// <summary>
    /// Initializes the collection-import terminal.
    /// </summary>
    /// <param name="enqueueCollectionImport">Owner operation for one collection leaf.</param>
    /// <param name="enqueueBuiltInImport">Owner operation for one built-in raw tag.</param>
    internal MainWindowPlaylistCollectionImportTerminal(
        Func<BMSTableSimple, bool> enqueueCollectionImport,
        Func<string, bool> enqueueBuiltInImport)
    {
        this.enqueueCollectionImport = enqueueCollectionImport
            ?? throw new ArgumentNullException(nameof(enqueueCollectionImport));
        this.enqueueBuiltInImport = enqueueBuiltInImport
            ?? throw new ArgumentNullException(nameof(enqueueBuiltInImport));
    }

    /// <summary>
    /// Enqueues one external collection leaf without changing its identity.
    /// </summary>
    /// <param name="source">The exact generated menu-item data context.</param>
    /// <returns>Whether the workspace accepted the queue request.</returns>
    internal bool TryEnqueueExternalPlaylistCollectionImport(BMSTableSimple source)
        => enqueueCollectionImport(source);

    /// <summary>
    /// Enqueues one built-in playlist import using its exact XAML tag.
    /// </summary>
    /// <param name="rawTag">The exact built-in menu-item tag.</param>
    /// <returns>Whether the workspace accepted the queue request.</returns>
    internal bool TryEnqueueBuiltInExternalPlaylistImport(string rawTag)
        => enqueueBuiltInImport(rawTag);
}

/// <summary>
/// Narrows playlist URL cells and context-menu actions to the playlist workspace owner.
/// </summary>
internal sealed class MainWindowPlaylistUrlAcquisitionTerminal
{
    private readonly Func<Uri, Task> runSinglePlaylistUrlAsync;

    private readonly Func<IReadOnlyList<object>, bool, Task> runPlaylistUrlActionAsync;

    private readonly Func<IReadOnlyList<object>, Task> runExternalPackageLookupAsync;

    private readonly Func<object, IReadOnlyList<object>, PlaylistUrlContextMenuAvailability> captureAvailability;

    /// <summary>
    /// Initializes the playlist URL terminal.
    /// </summary>
    /// <param name="runSinglePlaylistUrlAsync">Owner operation for one URL cell.</param>
    /// <param name="runPlaylistUrlActionAsync">Owner operation for one exact row snapshot.</param>
    /// <param name="runExternalPackageLookupAsync">Owner operation for one exact row snapshot.</param>
    /// <param name="captureAvailability">Owner availability query for one row scope.</param>
    internal MainWindowPlaylistUrlAcquisitionTerminal(
        Func<Uri, Task> runSinglePlaylistUrlAsync,
        Func<IReadOnlyList<object>, bool, Task> runPlaylistUrlActionAsync,
        Func<IReadOnlyList<object>, Task> runExternalPackageLookupAsync,
        Func<object, IReadOnlyList<object>, PlaylistUrlContextMenuAvailability> captureAvailability)
    {
        this.runSinglePlaylistUrlAsync = runSinglePlaylistUrlAsync
            ?? throw new ArgumentNullException(nameof(runSinglePlaylistUrlAsync));
        this.runPlaylistUrlActionAsync = runPlaylistUrlActionAsync
            ?? throw new ArgumentNullException(nameof(runPlaylistUrlActionAsync));
        this.runExternalPackageLookupAsync = runExternalPackageLookupAsync
            ?? throw new ArgumentNullException(nameof(runExternalPackageLookupAsync));
        this.captureAvailability = captureAvailability
            ?? throw new ArgumentNullException(nameof(captureAvailability));
    }

    /// <summary>
    /// Runs one URL through the existing workspace acquisition route.
    /// </summary>
    /// <param name="url">The exact URL resolved by the compiled cell.</param>
    /// <returns>The owner operation task.</returns>
    internal Task RunSinglePlaylistUrlAsync(Uri url)
        => runSinglePlaylistUrlAsync(url);

    /// <summary>
    /// Runs a URL or diff-URL action using the exact effective row snapshot.
    /// </summary>
    /// <param name="rows">The effective selected rows in their compiled order.</param>
    /// <param name="isDiffUrl">Whether the diff URL column/menu was selected.</param>
    /// <returns>The owner operation task.</returns>
    internal Task RunPlaylistUrlActionAsync(IReadOnlyList<object> rows, bool isDiffUrl)
        => runPlaylistUrlActionAsync(rows ?? throw new ArgumentNullException(nameof(rows)), isDiffUrl);

    /// <summary>
    /// Runs the external-package lookup using the exact effective row snapshot.
    /// </summary>
    /// <param name="rows">The effective selected rows in their compiled order.</param>
    /// <returns>The owner operation task.</returns>
    internal Task RunExternalPackageLookupAsync(IReadOnlyList<object> rows)
        => runExternalPackageLookupAsync(rows ?? throw new ArgumentNullException(nameof(rows)));

    /// <summary>
    /// Captures URL-menu availability using the same context row and effective row scope as the shell.
    /// </summary>
    /// <param name="contextRow">The row that opened the menu.</param>
    /// <param name="effectiveRows">The selected-or-context row snapshot.</param>
    /// <returns>The owner's URL-menu availability snapshot.</returns>
    internal PlaylistUrlContextMenuAvailability CaptureAvailability(
        object contextRow,
        IReadOnlyList<object> effectiveRows)
        => captureAvailability(
            contextRow,
            effectiveRows ?? throw new ArgumentNullException(nameof(effectiveRows)));
}

/// <summary>
/// Forwards the workspace URL-install completion signal to the shell's install-tree presentation.
/// </summary>
internal sealed class MainWindowPlaylistUrlInstallTreeExpansionTerminal
{
    private readonly Action expandInstallTree;

    /// <summary>
    /// Initializes the presentation forwarding terminal.
    /// </summary>
    /// <param name="expandInstallTree">Shell presentation action after owner completion.</param>
    internal MainWindowPlaylistUrlInstallTreeExpansionTerminal(Action expandInstallTree)
    {
        this.expandInstallTree = expandInstallTree
            ?? throw new ArgumentNullException(nameof(expandInstallTree));
    }

    /// <summary>
    /// Forwards one completed URL-install expansion request to the shell presentation.
    /// </summary>
    internal void ExpandInstallTree()
        => expandInstallTree();
}

/// <summary>
/// Narrows playlist-entry removal to the existing playlist workspace owner.
/// </summary>
internal sealed class MainWindowPlaylistEntryRemovalTerminal
{
    private readonly Func<IReadOnlyList<object>, Task> deleteSelectedEntriesAsync;

    /// <summary>
    /// Initializes a playlist-entry removal terminal.
    /// </summary>
    /// <param name="deleteSelectedEntriesAsync">Owner operation receiving one selected-row snapshot.</param>
    internal MainWindowPlaylistEntryRemovalTerminal(
        Func<IReadOnlyList<object>, Task> deleteSelectedEntriesAsync)
    {
        this.deleteSelectedEntriesAsync = deleteSelectedEntriesAsync
            ?? throw new ArgumentNullException(nameof(deleteSelectedEntriesAsync));
    }

    /// <summary>
    /// Removes the exact selected-row snapshot through the workspace owner.
    /// </summary>
    /// <param name="selectedRows">The selected playlist-detail rows in table order.</param>
    /// <returns>The owner operation task.</returns>
    internal Task DeleteSelectedEntriesAsync(IReadOnlyList<object> selectedRows)
        => deleteSelectedEntriesAsync(
            selectedRows ?? throw new ArgumentNullException(nameof(selectedRows)));
}

/// <summary>
/// Narrows playlist-table level overwrite to the existing workflow owner.
/// </summary>
internal sealed class MainWindowPlaylistTableLevelOverwriteTerminal
{
    private readonly Func<BMSTable, Task> overwriteAsync;

    /// <summary>
    /// Initializes a playlist-table level overwrite terminal.
    /// </summary>
    /// <param name="overwriteAsync">Owner operation receiving the exact table instance.</param>
    internal MainWindowPlaylistTableLevelOverwriteTerminal(Func<BMSTable, Task> overwriteAsync)
    {
        this.overwriteAsync = overwriteAsync
            ?? throw new ArgumentNullException(nameof(overwriteAsync));
    }

    /// <summary>
    /// Overwrites local levels through the existing workflow owner.
    /// </summary>
    /// <param name="table">The exact playlist table selected by the menu.</param>
    /// <returns>The owner operation task.</returns>
    internal Task OverwriteAsync(BMSTable table)
        => overwriteAsync(table ?? throw new ArgumentNullException(nameof(table)));
}

/// <summary>
/// Narrows playlist-table removal to the existing workflow owner and its selection hook.
/// </summary>
internal sealed class MainWindowPlaylistTableRemovalTerminal
{
    private readonly Func<BMSTable, Action, Task> removeTreeTableAsync;

    /// <summary>
    /// Initializes a playlist-table removal terminal.
    /// </summary>
    /// <param name="removeTreeTableAsync">
    /// Owner operation receiving the exact table and the selection callback to run after acceptance.
    /// </param>
    internal MainWindowPlaylistTableRemovalTerminal(
        Func<BMSTable, Action, Task> removeTreeTableAsync)
    {
        this.removeTreeTableAsync = removeTreeTableAsync
            ?? throw new ArgumentNullException(nameof(removeTreeTableAsync));
    }

    /// <summary>
    /// Removes one playlist table through the existing workflow owner.
    /// </summary>
    /// <param name="table">The exact playlist table selected by the menu.</param>
    /// <param name="applySelectionBeforeMutation">
    /// Selection transition to invoke only when the owner accepts removal.
    /// </param>
    /// <returns>The owner operation task.</returns>
    internal Task RemoveTreeTableAsync(BMSTable table, Action applySelectionBeforeMutation)
        => removeTreeTableAsync(
            table ?? throw new ArgumentNullException(nameof(table)),
            applySelectionBeforeMutation
                ?? throw new ArgumentNullException(nameof(applySelectionBeforeMutation)));
}
