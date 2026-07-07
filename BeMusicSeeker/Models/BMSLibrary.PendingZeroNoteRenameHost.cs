using System;
using System.Collections.Generic;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using Ribbit.Logging;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : IPendingZeroNoteRenameHost
{
    IDisposable IPendingZeroNoteRenameHost.AcquireBmsFilesInitializedMinReaderGuard()
    {
        return rwlockBMSFilesInitializedMin.GetReaderGuard();
    }

    IDisposable IPendingZeroNoteRenameHost.AcquirePendingInstallChartsWriterGuard()
    {
        return rwlockPendingInstallCharts.GetWriterGuard();
    }

    IDisposable IPendingZeroNoteRenameHost.AcquireSongDbInstallWriterGuard()
    {
        return rwlockSongDBInstall.GetWriterGuard();
    }

    IEnumerable<ChartFile> IPendingZeroNoteRenameHost.GetPendingBmsFormatChartFilesSnapshot()
    {
        return packageInstallService.GetPendingBmsFormatChartFilesSnapshot(ChartPackagesPending);
    }

    RenameInvalidExtensionOutcome IPendingZeroNoteRenameHost.ProcessInvalidExtensionRename(BMSFile file, string requestedPath)
    {
        return ProcessInvalidExtensionRename(file, requestedPath, removeFromLibraryOnSuccess: false);
    }

    void IPendingZeroNoteRenameHost.ShowRenameFailure(PendingZeroNoteRenameFailure failure)
    {
        if (failure?.Outcome?.FailureException == null || failure.File == null)
        {
            return;
        }
        if (failure.Outcome.FailedDuringDelete)
        {
            ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.File.path, GetDisplayedExceptionMessage(failure.Outcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }
        else
        {
            ShowOperationDialog(string.Format(Resources.Error_BmsFileMoveFailed, failure.File.path, failure.Outcome.FinalPath, GetDisplayedExceptionMessage(failure.Outcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }
    }

    void IPendingZeroNoteRenameHost.RemovePendingChartsFromPendingPackagesAndInstallRows(IEnumerable<string> chartPaths)
    {
        RemovePendingChartsFromPendingPackagesAndInstallRows(chartPaths);
    }

    void IPendingZeroNoteRenameHost.LogInfo(string info)
    {
        NLogWrapper.FileLogger?.Info(info);
    }
}
