using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

internal enum ChartOperationSourceScope
{
    Library,
    PendingPackage,
    NewlyInstalledPackage,
    PlaylistOwned,
    PlaylistMissing
}

[Flags]
internal enum ChartOperationCapabilities
{
    None = 0,
    OpenFile = 1 << 0,
    OpenFolder = 1 << 1,
    OpenRepositoryBySha256 = 1 << 2,
    UseLr2Ir = 1 << 3,
    UseScoreViewer = 1 << 4,
    UpdateRanking = 1 << 5,
    RunResourceHealthCheck = 1 << 6,
    RunBmsEncodingCheck = 1 << 7,
    RunBmsEncodingFix = 1 << 8,
    RunZeroNoteCheck = 1 << 9,
    MoveInLibrary = 1 << 10,
    RemoveFromLibrary = 1 << 11,
    UpdateInstallDestination = 1 << 12,
    RenameInvalidExtension = 1 << 13,
    ConvertToAudio = 1 << 14,
    OpenPlaylistUrls = 1 << 15,
    RepairInstalledLocation = 1 << 16
}

internal sealed class ChartOperationTarget
{
    private readonly Lazy<BMSFile> compatibilityBmsFile;

    internal ChartFile Chart { get; }

    internal BMSFile CompatibilityBmsFile => compatibilityBmsFile.Value;

    internal BMSTableEntry PlaylistEntry { get; }

    internal PackageChartEntry PackageEntry { get; }

    internal ChartOperationSourceScope SourceScope { get; }

    internal bool IsOwned { get; }

    internal bool IsPending { get; }

    internal bool IsPlaylistMissing { get; }

    internal ChartOperationCapabilities Capabilities { get; }

    internal ChartOperationTarget(
        ChartFile chart,
        BMSFile compatibilityBmsFile,
        BMSTableEntry playlistEntry,
        ChartOperationSourceScope sourceScope,
        bool isOwned,
        bool isPending,
        bool isPlaylistMissing,
        ChartOperationCapabilities capabilities,
        PackageChartEntry packageEntry = null)
        : this(
            chart,
            () => compatibilityBmsFile,
            playlistEntry,
            sourceScope,
            isOwned,
            isPending,
            isPlaylistMissing,
            capabilities,
            packageEntry)
    {
    }

    internal ChartOperationTarget(
        ChartFile chart,
        Func<BMSFile> compatibilityBmsFileProvider,
        BMSTableEntry playlistEntry,
        ChartOperationSourceScope sourceScope,
        bool isOwned,
        bool isPending,
        bool isPlaylistMissing,
        ChartOperationCapabilities capabilities,
        PackageChartEntry packageEntry = null)
    {
        Chart = chart ?? throw new ArgumentNullException(nameof(chart));
        compatibilityBmsFile = new Lazy<BMSFile>(() => compatibilityBmsFileProvider?.Invoke());
        PlaylistEntry = playlistEntry;
        PackageEntry = packageEntry;
        SourceScope = sourceScope;
        IsOwned = isOwned;
        IsPending = isPending;
        IsPlaylistMissing = isPlaylistMissing;
        Capabilities = capabilities;
    }

    internal bool HasCapability(ChartOperationCapabilities capability)
    {
        return (Capabilities & capability) == capability;
    }

    internal LibraryChartRef ToLibraryChartRef()
    {
        BMSFile materializedCompatibilityFile = compatibilityBmsFile.IsValueCreated
            ? compatibilityBmsFile.Value
            : null;
        return LibraryChartRef.FromChartFile(Chart, materializedCompatibilityFile);
    }

}
