using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Livet;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Applies precomputed mutation results to the live BMSLibrary state.
/// The facade must acquire every required lock before invoking this applier.
/// This type never acquires locks and never shows UI.
/// </summary>
internal sealed class BmsLibraryStateApplier(
    BmsLibraryDbGateway dbGateway,
    Func<DispatcherCollection<ChartPackage>> getPendingPackages,
    Action<DispatcherCollection<ChartPackage>> setPendingPackages,
    Func<DispatcherCollection<ChartPackage>> getInstalledPackages,
    Action<DispatcherCollection<ChartPackage>> setInstalledPackages,
    Action raiseInstalledPackagesChanged)
{
    private readonly BmsLibraryDbGateway dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));

    private readonly Func<DispatcherCollection<ChartPackage>> getPendingPackages = getPendingPackages ?? throw new ArgumentNullException(nameof(getPendingPackages));

    private readonly Action<DispatcherCollection<ChartPackage>> setPendingPackages = setPendingPackages ?? throw new ArgumentNullException(nameof(setPendingPackages));

    private readonly Func<DispatcherCollection<ChartPackage>> getInstalledPackages = getInstalledPackages ?? throw new ArgumentNullException(nameof(getInstalledPackages));

    private readonly Action<DispatcherCollection<ChartPackage>> setInstalledPackages = setInstalledPackages ?? throw new ArgumentNullException(nameof(setInstalledPackages));

    private readonly Action raiseInstalledPackagesChanged = raiseInstalledPackagesChanged ?? throw new ArgumentNullException(nameof(raiseInstalledPackagesChanged));

    public void ApplyPendingPackageMutationDelta(PendingPackageMutationDelta delta)
    {
        if (delta == null || !delta.HasChanges)
        {
            return;
        }

        setPendingPackages(new DispatcherCollection<ChartPackage>(
            new ObservableCollection<ChartPackage>(delta.RemainingPackages ?? []),
            DispatcherHelper.UIDispatcher));

        List<string> installPathsToDelete = [.. (delta.InstallPathsToDelete ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (installPathsToDelete.Count == 0)
        {
            return;
        }

        dbGateway.DeleteInstallRows(installPathsToDelete);
    }

    public void ApplyLibraryMutationDelta(LibraryMutationDelta delta)
    {
        if (delta == null)
        {
            return;
        }

        foreach (LibraryFolderPathChange folderPathChange in delta.FolderPathChanges)
        {
            ReplaceBmsFolder(folderPathChange.NewFolderPath, folderPathChange.OldFolderPath);
        }

        foreach (LibraryChartPathChange chartPathChange in delta.ChartPathChanges)
        {
            BMSFile bmsFile = chartPathChange?.GetBmsStorageOwner();
            if (bmsFile != null)
            {
                ReplaceBmsFilePath(bmsFile, chartPathChange.NewPath, chartPathChange.OldPath, chartPathChange.CalcFolderParent);
            }
            else
            {
                LR2SongDBExtended.bmson_song bmsonSong = chartPathChange?.GetBmsonStorageOwner();
                if (bmsonSong != null)
                {
                    ReplaceBmsonSongPath(bmsonSong, chartPathChange.NewPath, chartPathChange.OldPath);
                }
            }
        }

        foreach (LibraryInstallDestinationChange installDestinationChange in delta.UpdatedInstallDestinations)
        {
            if (installDestinationChange?.Entry != null)
            {
                if (installDestinationChange.ClearInstallDestinationState)
                {
                    installDestinationChange.Entry.ClearInstallDestination();
                }
                else
                {
                    installDestinationChange.Entry.SetInstallDestinationPathOnly(installDestinationChange.NewInstallDestination);
                }
            }
        }

        foreach (LibraryInstalledPackagePathChange installedPackagePathChange in delta.UpdatedInstalledPackagePaths)
        {
            if (installedPackagePathChange?.Package != null)
            {
                installedPackagePathChange.Package.path = installedPackagePathChange.NewPath;
            }
        }

        if (delta.ChartsToUnregister.Count > 0)
        {
            UnregisterCharts(delta.ChartsToUnregister);
        }

        if (delta.RaiseInstalledPackagesChanged)
        {
            raiseInstalledPackagesChanged();
        }
    }

    private void UnregisterCharts(IEnumerable<ChartFile> charts)
    {
        if (charts == null)
        {
            throw new ArgumentNullException(nameof(charts));
        }

        List<ChartFile> chartList = [.. charts.Where(chart => chart != null)];
        if (chartList.Count == 0)
        {
            return;
        }

        List<BMSFile> bmsFilesToUnregister = [.. chartList
            .Select(chart => chart.GetBmsStorageOwner())
            .Where(file => file != null)
            .Distinct()];
        if (bmsFilesToUnregister.Count > 0)
        {
            UnregisterBmsFiles(bmsFilesToUnregister);
        }

        List<LR2SongDBExtended.bmson_song> bmsonSongsToUnregister = [.. chartList
            .Select(chart => chart.GetBmsonStorageOwner())
            .Where(song => song != null)
            .Distinct()];
        if (bmsonSongsToUnregister.Count > 0)
        {
            UnregisterBmsonSongs(bmsonSongsToUnregister);
        }
    }

    private void UnregisterBmsFiles(IEnumerable<BMSFile> bmsFiles)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException(nameof(bmsFiles));
        }

        List<BMSFile> removedFilesList = [.. bmsFiles.Where(file => file != null)];
        if (removedFilesList.Count == 0)
        {
            return;
        }

        var removedPaths = new HashSet<string>(
            removedFilesList.Where(file => !string.IsNullOrWhiteSpace(file.path)).Select(file => file.path),
            StringComparer.OrdinalIgnoreCase);
        var removedFileRefs = new HashSet<BMSFile>(removedFilesList);
        dbGateway.DeleteSongsAndMaintenance(removedFilesList);
        DispatcherCollection<ChartPackage> installedPackages = getInstalledPackages();
        bool installedPackagesChanged = false;
        List<ChartPackage> emptyInstalledPackages = [];

        foreach (ChartPackage installedPackage in installedPackages.Where(package => package != null).ToList())
        {
            if (installedPackage.RemoveChartEntries(entry => IsMatchedRemovedFile(entry, removedPaths, removedFileRefs)))
            {
                installedPackagesChanged = true;
                if (installedPackage.ChartEntries.Count == 0)
                {
                    emptyInstalledPackages.Add(installedPackage);
                }
            }
        }

        if (emptyInstalledPackages.Count > 0)
        {
            foreach (ChartPackage emptyInstalledPackage in emptyInstalledPackages)
            {
                installedPackages.Remove(emptyInstalledPackage);
            }
            setInstalledPackages(installedPackages);
        }

        if (installedPackagesChanged)
        {
            raiseInstalledPackagesChanged();
        }
    }

    private void UnregisterBmsonSongs(IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        if (bmsonSongs == null)
        {
            throw new ArgumentNullException(nameof(bmsonSongs));
        }

        List<LR2SongDBExtended.bmson_song> removedSongsList = [.. bmsonSongs.Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))];
        if (removedSongsList.Count == 0)
        {
            return;
        }

        var removedPaths = new HashSet<string>(
            removedSongsList.Select(song => song.path),
            StringComparer.OrdinalIgnoreCase);
        var removedSongRefs = new HashSet<LR2SongDBExtended.bmson_song>(removedSongsList);
        dbGateway.DeleteBmsonSongs(removedSongsList);

        DispatcherCollection<ChartPackage> installedPackages = getInstalledPackages();
        bool installedPackagesChanged = false;
        List<ChartPackage> emptyInstalledPackages = [];
        foreach (ChartPackage installedPackage in installedPackages.Where(package => package != null).ToList())
        {
            if (installedPackage.RemoveChartEntries(entry => IsMatchedRemovedBmsonFile(entry, removedPaths, removedSongRefs)))
            {
                installedPackagesChanged = true;
                if (installedPackage.ChartEntries.Count == 0)
                {
                    emptyInstalledPackages.Add(installedPackage);
                }
            }
        }
        if (emptyInstalledPackages.Count > 0)
        {
            foreach (ChartPackage emptyInstalledPackage in emptyInstalledPackages)
            {
                installedPackages.Remove(emptyInstalledPackage);
            }
            setInstalledPackages(installedPackages);
        }
        if (installedPackagesChanged)
        {
            raiseInstalledPackagesChanged();
        }
    }

    private void ReplaceBmsFilePath(BMSFile bmsFile, string newPath, string oldPath = null, bool calcFolderParent = true)
    {
        if (bmsFile == null)
        {
            throw new ArgumentNullException(nameof(bmsFile));
        }

        if (newPath == null)
        {
            throw new ArgumentNullException(nameof(newPath));
        }

        if (!File.Exists(newPath))
        {
            throw new FileNotFoundException(Resources.Error_RenameDestFileNotFound, newPath);
        }

        if (!string.IsNullOrWhiteSpace(oldPath) && !bmsFile.path.Equals(newPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidCastException(Resources.Error_OldPathMismatch);
        }

        if (string.IsNullOrWhiteSpace(oldPath))
        {
            oldPath = bmsFile.path;
        }

        bmsFile.path = newPath;
        if (calcFolderParent)
        {
            try
            {
                string directoryName = Path.GetDirectoryName(bmsFile.path);
                var encoding = Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                bmsFile.folder = LR2CRC32.Compute(encoding.GetBytes(directoryName + "\\\0")).ToString("x");
                bmsFile.parent = LR2CRC32.Compute(encoding.GetBytes(Path.GetDirectoryName(directoryName) + "\\\0")).ToString("x");
            }
            catch
            {
                bmsFile.parent = null;
                bmsFile.folder = null;
                bmsFile.adddate = null;
                bmsFile.date = null;
            }
        }
        else
        {
            bmsFile.parent = null;
            bmsFile.folder = null;
            bmsFile.adddate = null;
            bmsFile.date = null;
        }

        dbGateway.ReplaceSongPathWithMaintenance(bmsFile, oldPath);
    }

    private void ReplaceBmsonSongPath(LR2SongDBExtended.bmson_song bmsonSong, string newPath, string oldPath = null)
    {
        if (bmsonSong == null)
        {
            throw new ArgumentNullException(nameof(bmsonSong));
        }

        if (newPath == null)
        {
            throw new ArgumentNullException(nameof(newPath));
        }

        if (!File.Exists(newPath))
        {
            throw new FileNotFoundException(Resources.Error_RenameDestFileNotFound, newPath);
        }

        if (!string.IsNullOrWhiteSpace(oldPath)
            && !string.Equals(bmsonSong.path, oldPath, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(bmsonSong.path, newPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidCastException(Resources.Error_OldPathMismatch);
        }

        if (string.IsNullOrWhiteSpace(oldPath))
        {
            oldPath = bmsonSong.path;
        }

        bmsonSong.path = newPath;
        bmsonSong.folder = Path.GetDirectoryName(newPath) ?? string.Empty;
        bmsonSong.MaintenanceInfo?.NormalizeForBmson(bmsonSong.path, bmsonSong.md5);
        dbGateway.ReplaceBmsonSongPath(bmsonSong, oldPath);
    }

    private bool ReplaceBmsFolder(string newFolderPath, string oldFolderPath)
    {
        if (string.IsNullOrWhiteSpace(newFolderPath))
        {
            throw new ArgumentNullException(nameof(newFolderPath));
        }

        if (string.IsNullOrWhiteSpace(oldFolderPath))
        {
            throw new ArgumentNullException(nameof(oldFolderPath));
        }

        return dbGateway.ReplaceFolderRecord(
            oldFolderPath.TrimEnd(Path.DirectorySeparatorChar),
            newFolderPath.TrimEnd(Path.DirectorySeparatorChar));
    }

    private static bool IsMatchedRemovedFile(BMSFile file, HashSet<string> removedPaths, HashSet<BMSFile> removedFiles)
    {
        if (file == null)
        {
            return false;
        }

        if (removedFiles.Contains(file))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(file.path) && removedPaths.Contains(file.path);
    }

    private static bool IsMatchedRemovedFile(PackageChartEntry entry, HashSet<string> removedPaths, HashSet<BMSFile> removedFiles)
    {
        if (entry?.Chart == null)
        {
            return false;
        }

        BMSFile bmsFile = entry.Chart.GetBmsStorageOwner();
        if (bmsFile != null && removedFiles.Contains(bmsFile))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(entry.Chart.Path) && removedPaths.Contains(entry.Chart.Path);
    }

    private static bool IsMatchedRemovedBmsonFile(PackageChartEntry entry, HashSet<string> removedPaths, HashSet<LR2SongDBExtended.bmson_song> removedSongs)
    {
        if (entry?.Chart == null)
        {
            return false;
        }

        LR2SongDBExtended.bmson_song bmsonSong = entry.Chart.GetBmsonStorageOwner();
        if (bmsonSong != null && removedSongs.Contains(bmsonSong))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(entry.Chart.Path) && removedPaths.Contains(entry.Chart.Path);
    }
}
