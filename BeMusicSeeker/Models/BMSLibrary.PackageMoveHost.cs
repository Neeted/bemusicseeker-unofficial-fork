using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : IPackageMoveHost
{
    BmsLibraryOptionsSnapshot IPackageMoveHost.CreateCurrentOptionsSnapshot()
    {
        return CurrentOptionsSnapshot;
    }

    string IPackageMoveHost.CreateChartFolderPathFromCharts(IEnumerable<ChartFile> chartFiles, string parentDir)
    {
        return CreateChartFolderPathFromCharts(chartFiles, parentDir);
    }

    string IPackageMoveHost.GetDisplayedExceptionMessage(Exception exception)
    {
        return GetDisplayedExceptionMessage(exception);
    }

    IFileMutationService IPackageMoveHost.FileMutationService => fileMutationService;

    IBmsLibraryDialogService IPackageMoveHost.DialogService => scopedOperationDialogService;

    FileMutationOptions IPackageMoveHost.TargetOnlyFileMutationOptions => targetOnlyFileMutationOptions;

    FileMutationOptions IPackageMoveHost.RecursiveDirectoryTreeFileMutationOptions => recursiveDirectoryTreeFileMutationOptions;

    void IPackageMoveHost.LogInstallPerformance(string message)
    {
        LogInstallPerformance(message);
    }
}
