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
        bool isPendingSelected,
        bool isInstalledSelected,
        bool isPlaylistSelected,
        ChartOperationTarget rowTarget,
        IEnumerable<ChartOperationTarget> selectedTargets)
    {
        IsPlaylistRow = isPlaylistRow;
        IsPendingSelected = isPendingSelected;
        IsInstalledSelected = isInstalledSelected;
        IsPlaylistSelected = isPlaylistSelected;
        RowTarget = rowTarget;
        SelectedTargets = selectedTargets ?? throw new ArgumentNullException(nameof(selectedTargets));
    }

    internal bool IsPlaylistRow { get; }

    internal bool IsPendingSelected { get; }

    internal bool IsInstalledSelected { get; }

    internal bool IsPlaylistSelected { get; }

    internal ChartOperationTarget RowTarget { get; }

    internal IEnumerable<ChartOperationTarget> SelectedTargets { get; }
}
