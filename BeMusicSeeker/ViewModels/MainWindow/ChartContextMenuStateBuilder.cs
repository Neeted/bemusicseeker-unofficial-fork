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
        bool hasScoreViewerTarget = HasCapability(selectedTargets, ChartOperationCapabilities.UseScoreViewer);
        bool hasResourceHealthTarget = HasCapability(selectedTargets, ChartOperationCapabilities.RunResourceHealthCheck);
        bool canOpenLr2Ir = request.RowTarget?.HasCapability(ChartOperationCapabilities.UseLr2Ir) == true;
        bool canOpenInstallDestination = request.RowTarget?.HasCapability(ChartOperationCapabilities.UpdateInstallDestination) == true && !request.IsPlaylistRow;
        bool canShowResourceHealthMenu = ShouldShowResourceHealthContextMenu(isPlaylistContext, selectedTargets);
        bool canMoveSelectedFiles = !request.IsPendingSelected;
        bool canDeleteFiles = HasCapability(selectedTargets, ChartOperationCapabilities.RemoveFromLibrary)
            || (request.IsPendingSelected && HasCapability(selectedTargets, ChartOperationCapabilities.UpdateInstallDestination));
        bool canRenameInvalidExtension = canDeleteFiles && HasCapability(selectedTargets, ChartOperationCapabilities.RenameInvalidExtension);
        bool canShowFolderViewSeparator = !isPlaylistContext && !request.IsPendingSelected;
        bool canAutoRenameFolders = !isPlaylistContext && !request.IsPendingSelected && (hasBmsSelection || hasBmsonSelection);
        bool canFixEncoding = !isPlaylistContext && hasBmsSelection;
        bool canConvertToAudio = !request.IsPendingSelected;
        bool canDeleteInstallPackages = isInstallListSelected && selectedTargets.Count > 0;

        return new ChartContextMenuState(
            request.IsPlaylistRow,
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
            hasBmsSelection,
            hasScoreViewerTarget,
            hasResourceHealthTarget,
            canOpenLr2Ir,
            canOpenInstallDestination,
            canShowResourceHealthMenu,
            canMoveSelectedFiles,
            canDeleteFiles,
            canRenameInvalidExtension,
            canShowFolderViewSeparator,
            canAutoRenameFolders,
            canFixEncoding,
            canConvertToAudio,
            canDeleteInstallPackages);
    }

    /// <summary>
    /// Determines whether the resource health submenu should be shown for selected chart targets.
    /// </summary>
    internal static bool ShouldShowResourceHealthContextMenu(bool isPlaylistContext, IEnumerable<ChartOperationTarget> selectedTargets)
    {
        return !isPlaylistContext
            && HasCapability(selectedTargets, ChartOperationCapabilities.RunResourceHealthCheck);
    }

    private static bool HasCapability(IEnumerable<ChartOperationTarget> selectedTargets, ChartOperationCapabilities capability)
    {
        return (selectedTargets ?? []).Any(target => target.HasCapability(capability));
    }
}
