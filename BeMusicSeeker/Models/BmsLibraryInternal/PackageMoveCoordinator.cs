using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IPackageMoveHost
{
    BmsLibraryOptionsSnapshot CreateCurrentOptionsSnapshot();

    string CreateChartFolderPathFromCharts(IEnumerable<ChartFile> chartFiles, string parentDir);

    string GetDisplayedExceptionMessage(Exception exception);

    IFileMutationService FileMutationService { get; }

    IBmsLibraryDialogService DialogService { get; }

    FileMutationOptions TargetOnlyFileMutationOptions { get; }

    FileMutationOptions RecursiveDirectoryTreeFileMutationOptions { get; }

    void LogInstallPerformance(string message);
}

internal static class PackageMoveCoordinator
{
    internal static bool MoveChartPackageFiles(
        BmsLibraryPackageInstallService packageInstallService,
        IPackageMoveHost host,
        ChartPackage package,
        string installationDirectory,
        bool showMessageBoxOnInstallFail = true,
        bool deleteAllContents = false,
        IPrimaryHashLookup existingHashes = null,
        ISet<string> excludedComponentPaths = null)
    {
        return packageInstallService.MovePackageFiles(
            package,
            installationDirectory,
            host.CreateCurrentOptionsSnapshot(),
            host.CreateChartFolderPathFromCharts,
            host.GetDisplayedExceptionMessage,
            host.FileMutationService,
            host.DialogService,
            host.TargetOnlyFileMutationOptions,
            host.RecursiveDirectoryTreeFileMutationOptions,
            host.LogInstallPerformance,
            showMessageBoxOnInstallFail,
            deleteAllContents,
            existingHashes,
            excludedComponentPaths);
    }
}
