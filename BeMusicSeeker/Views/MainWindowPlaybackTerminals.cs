using System;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>
/// Narrows compiled main-table playback events to the existing playback-panel owner.
/// </summary>
internal sealed class MainWindowPlaybackTerminal
{
    private readonly Action<object> handleTableSelection;

    private readonly Func<int, object, bool> handleTableRowActivation;

    /// <summary>
    /// Initializes a playback terminal with the two table event operations it owns.
    /// </summary>
    /// <param name="handleTableSelection">Receives the exact selected row object.</param>
    /// <param name="handleTableRowActivation">Receives the exact row index and row object.</param>
    internal MainWindowPlaybackTerminal(
        Action<object> handleTableSelection,
        Func<int, object, bool> handleTableRowActivation)
    {
        this.handleTableSelection = handleTableSelection
            ?? throw new ArgumentNullException(nameof(handleTableSelection));
        this.handleTableRowActivation = handleTableRowActivation
            ?? throw new ArgumentNullException(nameof(handleTableRowActivation));
    }

    /// <summary>
    /// Forwards one compiled selection event without changing row identity.
    /// </summary>
    /// <param name="selectedRow">The row supplied by the table event.</param>
    internal void HandleTableSelection(object selectedRow)
        => handleTableSelection(selectedRow);

    /// <summary>
    /// Forwards one compiled activation event without changing its row index or identity.
    /// </summary>
    /// <param name="rowIndex">The index supplied by the table event.</param>
    /// <param name="row">The row supplied by the table event.</param>
    /// <returns>Whether the playback panel accepted the activation.</returns>
    internal bool HandleTableRowActivation(int rowIndex, object row)
        => handleTableRowActivation(rowIndex, row);

    /// <summary>
    /// Creates the default terminal bound directly to the playback-panel owner.
    /// </summary>
    /// <param name="viewModel">The shell view model owning playback state.</param>
    /// <returns>A terminal that preserves the existing owner behavior.</returns>
    internal static MainWindowPlaybackTerminal Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowPlaybackTerminal(
            viewModel.PlaybackPanel.HandleTableSelection,
            viewModel.PlaybackPanel.HandleTableRowActivation);
    }
}
