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
        IReadOnlyList<ChartOperationTarget> selectedTargets,
        bool hasBmsonSelection,
        bool hasBmsSelection,
        bool hasResourceHealthTarget,
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
        SelectedTargets = selectedTargets ?? throw new ArgumentNullException(nameof(selectedTargets));
        HasBmsonSelection = hasBmsonSelection;
        HasBmsSelection = hasBmsSelection;
        HasResourceHealthTarget = hasResourceHealthTarget;
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

    internal IReadOnlyList<ChartOperationTarget> SelectedTargets { get; }

    internal bool HasBmsonSelection { get; }

    internal bool HasBmsSelection { get; }

    internal bool HasResourceHealthTarget { get; }

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
