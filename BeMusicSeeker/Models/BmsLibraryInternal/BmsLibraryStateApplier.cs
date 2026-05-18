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
    Func<List<BMSFile>> getBmsFiles,
    Action<List<BMSFile>> setBmsFiles,
    Func<List<LR2SongDBExtended.bmson_song>> getBmsonSongs,
    Action<List<LR2SongDBExtended.bmson_song>> setBmsonSongs,
    Func<DispatcherCollection<ChartPackage>> getPendingPackages,
    Action<DispatcherCollection<ChartPackage>> setPendingPackages,
    Func<DispatcherCollection<ChartPackage>> getInstalledPackages,
    Action<DispatcherCollection<ChartPackage>> setInstalledPackages,
    Action invalidateInstalledDirectoryIndex,
    Action invalidateParentFolderCache,
    Action clearDuplicatedCache,
    Action raiseBmsFilesChanged,
    Action raiseInstalledPackagesChanged)
{
    private readonly BmsLibraryDbGateway dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));

    private readonly Func<List<BMSFile>> getBmsFiles = getBmsFiles ?? throw new ArgumentNullException(nameof(getBmsFiles));

    private readonly Action<List<BMSFile>> setBmsFiles = setBmsFiles ?? throw new ArgumentNullException(nameof(setBmsFiles));

    private readonly Func<List<LR2SongDBExtended.bmson_song>> getBmsonSongs = getBmsonSongs ?? throw new ArgumentNullException(nameof(getBmsonSongs));

    private readonly Action<List<LR2SongDBExtended.bmson_song>> setBmsonSongs = setBmsonSongs ?? throw new ArgumentNullException(nameof(setBmsonSongs));

    private readonly Func<DispatcherCollection<ChartPackage>> getPendingPackages = getPendingPackages ?? throw new ArgumentNullException(nameof(getPendingPackages));

    private readonly Action<DispatcherCollection<ChartPackage>> setPendingPackages = setPendingPackages ?? throw new ArgumentNullException(nameof(setPendingPackages));

    private readonly Func<DispatcherCollection<ChartPackage>> getInstalledPackages = getInstalledPackages ?? throw new ArgumentNullException(nameof(getInstalledPackages));

    private readonly Action<DispatcherCollection<ChartPackage>> setInstalledPackages = setInstalledPackages ?? throw new ArgumentNullException(nameof(setInstalledPackages));

    private readonly Action invalidateInstalledDirectoryIndex = invalidateInstalledDirectoryIndex ?? throw new ArgumentNullException(nameof(invalidateInstalledDirectoryIndex));

    private readonly Action invalidateParentFolderCache = invalidateParentFolderCache ?? throw new ArgumentNullException(nameof(invalidateParentFolderCache));

    private readonly Action clearDuplicatedCache = clearDuplicatedCache ?? throw new ArgumentNullException(nameof(clearDuplicatedCache));

    private readonly Action raiseBmsFilesChanged = raiseBmsFilesChanged ?? throw new ArgumentNullException(nameof(raiseBmsFilesChanged));

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

        foreach (LibraryFilePathChange filePathChange in delta.FilePathChanges)
        {
            ReplaceBmsFilePath(filePathChange.File, filePathChange.NewPath, filePathChange.OldPath, filePathChange.CalcFolderParent);
        }

        foreach (LibraryBmsonSongPathChange bmsonSongPathChange in delta.BmsonSongPathChanges)
        {
            ReplaceBmsonSongPath(bmsonSongPathChange.Song, bmsonSongPathChange.NewPath, bmsonSongPathChange.OldPath);
        }

        foreach (LibraryInstallDestinationChange installDestinationChange in delta.UpdatedInstallDestinations)
        {
            if (installDestinationChange?.File != null)
            {
                installDestinationChange.File.instl_dst = installDestinationChange.NewInstallDestination;
            }
        }

        foreach (LibraryInstalledPackagePathChange installedPackagePathChange in delta.UpdatedInstalledPackagePaths)
        {
            if (installedPackagePathChange?.Package != null)
            {
                installedPackagePathChange.Package.path = installedPackagePathChange.NewPath;
            }
        }

        if (delta.FilesToUnregister.Count > 0)
        {
            UnregisterBmsFiles([.. delta.FilesToUnregister.Distinct()]);
        }

        if (delta.BmsonSongsToUnregister.Count > 0)
        {
            UnregisterBmsonSongs([.. delta.BmsonSongsToUnregister.Distinct()]);
        }

        if (delta.InvalidateInstalledDirectoryIndex)
        {
            invalidateInstalledDirectoryIndex();
        }

        if (delta.InvalidateParentFolderCache)
        {
            invalidateParentFolderCache();
        }

        if (delta.RaiseInstalledPackagesChanged)
        {
            raiseInstalledPackagesChanged();
        }

        if (delta.ClearDuplicatedCache)
        {
            clearDuplicatedCache();
        }

        if (delta.RaiseBmsFilesChanged)
        {
            raiseBmsFilesChanged();
        }
    }

    public void UnregisterBmsFiles(IEnumerable<BMSFile> bmsFiles)
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
        setBmsFiles([.. getBmsFiles().Where(file => !IsMatchedRemovedFile(file, removedPaths, removedFileRefs))]);
        dbGateway.DeleteSongsAndMaintenance(removedFilesList);
        DispatcherCollection<ChartPackage> installedPackages = getInstalledPackages();
        bool installedPackagesChanged = false;
        List<ChartPackage> emptyInstalledPackages = [];

        foreach (ChartPackage installedPackage in installedPackages.Where(package => package != null).ToList())
        {
            if (installedPackage.RemoveChartEntries(entry => IsMatchedRemovedFile(entry, removedPaths, removedFileRefs)))
            {
                installedPackagesChanged = true;
                if (installedPackage.IsChartAdapterEmpty())
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

    public void UnregisterBmsonSongs(IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
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
        setBmsonSongs([.. getBmsonSongs().Where(song => song != null && !removedSongRefs.Contains(song) && !removedPaths.Contains(song.path))]);
        dbGateway.DeleteBmsonSongs(removedSongsList);

        DispatcherCollection<ChartPackage> installedPackages = getInstalledPackages();
        bool installedPackagesChanged = false;
        List<ChartPackage> emptyInstalledPackages = [];
        foreach (ChartPackage installedPackage in installedPackages.Where(package => package != null).ToList())
        {
            if (installedPackage.RemoveChartEntries(entry => IsMatchedRemovedBmsonFile(entry, removedPaths, removedSongRefs)))
            {
                installedPackagesChanged = true;
                if (installedPackage.IsChartAdapterEmpty())
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

        invalidateInstalledDirectoryIndex();
        invalidateParentFolderCache();
        clearDuplicatedCache();
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

        invalidateInstalledDirectoryIndex();
        invalidateParentFolderCache();
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

        if (!string.IsNullOrWhiteSpace(oldPath) && !string.Equals(bmsonSong.path, oldPath, StringComparison.OrdinalIgnoreCase))
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
        invalidateInstalledDirectoryIndex();
        invalidateParentFolderCache();
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

        if (entry.CompatibilityAdapter != null && removedFiles.Contains(entry.CompatibilityAdapter))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(entry.Chart.Path) && removedPaths.Contains(entry.Chart.Path);
    }

    private static bool IsMatchedRemovedBmsonFile(BMSFile file, HashSet<string> removedPaths, HashSet<LR2SongDBExtended.bmson_song> removedSongs)
    {
        if (file == null)
        {
            return false;
        }

        if (file is PendingChartEntry pending && pending.IsBmsonChart)
        {
            if (pending.BmsonSong != null && removedSongs.Contains(pending.BmsonSong))
            {
                return true;
            }
        }

        return !string.IsNullOrWhiteSpace(file.path) && removedPaths.Contains(file.path);
    }

    private static bool IsMatchedRemovedBmsonFile(PackageChartEntry entry, HashSet<string> removedPaths, HashSet<LR2SongDBExtended.bmson_song> removedSongs)
    {
        if (entry?.Chart == null)
        {
            return false;
        }

        if (entry.Chart.BmsonSong != null && removedSongs.Contains(entry.Chart.BmsonSong))
        {
            return true;
        }

        if (entry.CompatibilityAdapter is PendingChartEntry pending && pending.IsBmsonChart && pending.BmsonSong != null && removedSongs.Contains(pending.BmsonSong))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(entry.Chart.Path) && removedPaths.Contains(entry.Chart.Path);
    }
}
