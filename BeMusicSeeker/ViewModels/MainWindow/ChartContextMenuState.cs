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
        bool hasBmsSelection,
        bool hasScoreViewerTarget,
        bool hasRankingTarget,
        bool hasResourceHealthTarget,
        bool canOpenLr2Ir,
        bool canOpenInstallDestination,
        bool canShowResourceHealthMenu,
        bool canMoveSelectedFiles,
        bool canDeleteFiles,
        bool canRenameInvalidExtension,
        bool canShowFolderViewSeparator,
        bool canAutoRenameFolders,
        bool canFixEncoding,
        bool canConvertToAudio,
        bool canDeleteInstallPackages)
    {
        IsPlaylistRow = isPlaylistRow;
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
        HasScoreViewerTarget = hasScoreViewerTarget;
        HasRankingTarget = hasRankingTarget;
        HasResourceHealthTarget = hasResourceHealthTarget;
        CanOpenLr2Ir = canOpenLr2Ir;
        CanOpenInstallDestination = canOpenInstallDestination;
        CanShowResourceHealthMenu = canShowResourceHealthMenu;
        CanMoveSelectedFiles = canMoveSelectedFiles;
        CanDeleteFiles = canDeleteFiles;
        CanRenameInvalidExtension = canRenameInvalidExtension;
        CanShowFolderViewSeparator = canShowFolderViewSeparator;
        CanAutoRenameFolders = canAutoRenameFolders;
        CanFixEncoding = canFixEncoding;
        CanConvertToAudio = canConvertToAudio;
        CanDeleteInstallPackages = canDeleteInstallPackages;
    }

    internal bool IsPlaylistRow { get; }

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

    internal bool HasScoreViewerTarget { get; }

    internal bool HasRankingTarget { get; }

    internal bool HasResourceHealthTarget { get; }

    internal bool CanOpenLr2Ir { get; }

    internal bool CanOpenInstallDestination { get; }

    internal bool CanShowResourceHealthMenu { get; }

    internal bool CanMoveSelectedFiles { get; }

    internal bool CanDeleteFiles { get; }

    internal bool CanRenameInvalidExtension { get; }

    internal bool CanShowFolderViewSeparator { get; }

    internal bool CanAutoRenameFolders { get; }

    internal bool CanFixEncoding { get; }

    internal bool CanConvertToAudio { get; }

    internal bool CanDeleteInstallPackages { get; }
}
