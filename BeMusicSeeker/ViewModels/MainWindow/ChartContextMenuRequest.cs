using System;
using System.Collections.Generic;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Captures UI-independent inputs needed to resolve chart table context-menu state.
/// </summary>
internal sealed class ChartContextMenuRequest
{
    /// <summary>
    /// Initializes a new context-menu state request.
    /// </summary>
    internal ChartContextMenuRequest(
        bool isPlaylistRow,
        Uri rowUrl,
        Uri rowUrlDiff,
        bool isPendingSelected,
        bool isInstalledSelected,
        bool isPlaylistSelected,
        ChartOperationTarget rowTarget,
        IEnumerable<ChartOperationTarget> selectedTargets)
    {
        IsPlaylistRow = isPlaylistRow;
        RowUrl = rowUrl;
        RowUrlDiff = rowUrlDiff;
        IsPendingSelected = isPendingSelected;
        IsInstalledSelected = isInstalledSelected;
        IsPlaylistSelected = isPlaylistSelected;
        RowTarget = rowTarget;
        SelectedTargets = selectedTargets ?? throw new ArgumentNullException(nameof(selectedTargets));
    }

    internal bool IsPlaylistRow { get; }

    internal Uri RowUrl { get; }

    internal Uri RowUrlDiff { get; }

    internal bool IsPendingSelected { get; }

    internal bool IsInstalledSelected { get; }

    internal bool IsPlaylistSelected { get; }

    internal ChartOperationTarget RowTarget { get; }

    internal IEnumerable<ChartOperationTarget> SelectedTargets { get; }
}
