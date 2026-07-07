using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Resolves chart table context-menu state without depending on WPF control types.
/// </summary>
internal static class ChartContextMenuStateBuilder
{
    /// <summary>
    /// Builds the context-menu state from row and selection facts supplied by the view shell.
    /// </summary>
    internal static ChartContextMenuState Build(ChartContextMenuRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        List<ChartOperationTarget> selectedTargets = [.. request.SelectedTargets];
        if (request.RowTarget != null && selectedTargets.Count == 0)
        {
            selectedTargets.Add(request.RowTarget);
        }

        bool isInstallListSelected = request.IsPendingSelected || request.IsInstalledSelected;
        bool isPlaylistContext = request.IsPlaylistSelected || request.IsPlaylistRow;
        bool isBmsonContextRow = request.RowTarget?.Chart.Kind == ChartFileKind.Bmson;
        bool hasBmsonSelection = selectedTargets.Any(target => target.Chart.Kind == ChartFileKind.Bmson);
        bool hasBmsSelection = selectedTargets.Any(target => target.Chart.Kind == ChartFileKind.Bms);

        return new ChartContextMenuState(
            request.IsPlaylistRow,
            request.RowUrl,
            request.RowUrlDiff,
            request.IsPendingSelected,
            request.IsInstalledSelected,
            request.IsPlaylistSelected,
            isInstallListSelected,
            isPlaylistContext,
            request.RowTarget,
            request.RowTarget?.Chart?.Path,
            selectedTargets,
            isBmsonContextRow,
            hasBmsonSelection,
            hasBmsSelection);
    }
}
