using System;
using System.Collections.Generic;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Describes chart table context-menu state before WPF menu items are updated.
/// </summary>
internal sealed class ChartContextMenuState
{
    /// <summary>
    /// Initializes a new resolved context-menu state.
    /// </summary>
    internal ChartContextMenuState(
        bool isPlaylistRow,
        Uri rowUrl,
        Uri rowUrlDiff,
        bool isPendingSelected,
        bool isInstalledSelected,
        bool isPlaylistSelected,
        bool isInstallListSelected,
        bool isPlaylistContext,
        ChartOperationTarget rowTarget,
        string chartPath,
        IReadOnlyList<ChartOperationTarget> selectedTargets,
        bool isBmsonContextRow,
        bool hasBmsonSelection,
        bool hasBmsSelection)
    {
        IsPlaylistRow = isPlaylistRow;
        RowUrl = rowUrl;
        RowUrlDiff = rowUrlDiff;
        IsPendingSelected = isPendingSelected;
        IsInstalledSelected = isInstalledSelected;
        IsPlaylistSelected = isPlaylistSelected;
        IsInstallListSelected = isInstallListSelected;
        IsPlaylistContext = isPlaylistContext;
        RowTarget = rowTarget;
        ChartPath = chartPath;
        SelectedTargets = selectedTargets ?? throw new ArgumentNullException(nameof(selectedTargets));
        IsBmsonContextRow = isBmsonContextRow;
        HasBmsonSelection = hasBmsonSelection;
        HasBmsSelection = hasBmsSelection;
    }

    internal bool IsPlaylistRow { get; }

    internal Uri RowUrl { get; }

    internal Uri RowUrlDiff { get; }

    internal bool IsPendingSelected { get; }

    internal bool IsInstalledSelected { get; }

    internal bool IsPlaylistSelected { get; }

    internal bool IsInstallListSelected { get; }

    internal bool IsPlaylistContext { get; }

    internal ChartOperationTarget RowTarget { get; }

    internal string ChartPath { get; }

    internal IReadOnlyList<ChartOperationTarget> SelectedTargets { get; }

    internal bool IsBmsonContextRow { get; }

    internal bool HasBmsonSelection { get; }

    internal bool HasBmsSelection { get; }
}
