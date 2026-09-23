using System;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Defines the startup presentation gates independently of the shell ViewModel lifecycle.
/// </summary>
internal static class StartupPresentationPolicy
{
    [Flags]
    private enum Channel
    {
        None = 0,
        LibraryMainView = 1,
        LibraryFolderTree = 2,
        InstallTree = 4,
        PlaylistTree = 8,
        DuplicateTree = 16
    }

    private const Channel DeferredChannels =
        Channel.LibraryMainView
        | Channel.LibraryFolderTree
        | Channel.PlaylistTree
        | Channel.DuplicateTree;

    private const Channel BasicChannels = Channel.LibraryFolderTree | Channel.PlaylistTree;

    internal static bool IsReadyUiMaskSatisfied(bool installTree, bool libraryMainView, bool playlistTree)
    {
        return installTree;
    }

    internal static bool IsPresentationDeferred(
        MainViewUpdateMode currentTreeMode,
        bool startupUiSuppressFlush,
        bool libraryMainView,
        bool libraryFolderTree,
        bool playlistTree,
        bool duplicateTree)
    {
        Channel mask = Channel.None;
        if (libraryMainView)
        {
            mask |= Channel.LibraryMainView;
        }
        if (libraryFolderTree)
        {
            mask |= Channel.LibraryFolderTree;
        }
        if (playlistTree)
        {
            mask |= Channel.PlaylistTree;
        }
        if (duplicateTree)
        {
            mask |= Channel.DuplicateTree;
        }

        return GetDeferredPresentationChannels(
            (int)mask,
            startupUiSuppressFlush,
            CanShowBasicLibraryMainView(currentTreeMode)) != 0;
    }

    internal static int GetDeferredPresentationChannels(
        int mask,
        bool startupUiSuppressFlush,
        bool includeBasicLibraryMainView)
    {
        Channel deferred = (Channel)mask & DeferredChannels;
        if (startupUiSuppressFlush)
        {
            Channel basic = BasicChannels;
            if (includeBasicLibraryMainView)
            {
                basic |= Channel.LibraryMainView;
            }
            deferred &= ~basic;
        }
        return (int)deferred;
    }

    internal static bool ShouldDeferLibraryFolderRefresh(
        bool deferredContinuation,
        long continuationOperationToken,
        long activeOperationToken,
        bool startupOperationActive)
    {
        return !deferredContinuation
            || !startupOperationActive
            || continuationOperationToken == 0L
            || continuationOperationToken != activeOperationToken;
    }

    private static bool CanShowBasicLibraryMainView(MainViewUpdateMode currentTreeMode)
    {
        return currentTreeMode == MainViewUpdateMode.FolderFilterSelected
            || currentTreeMode == MainViewUpdateMode.FullScanAllChartsFilterSelected;
    }
}
