using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Livet;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Applies precomputed mutation results to the live BMSLibrary state.
/// The facade must acquire every required lock before invoking this applier.
/// This type never acquires locks and never shows UI.
/// </summary>
internal sealed class BmsLibraryStateApplier
{
    private readonly BmsLibraryDbGateway dbGateway;

    private readonly Func<List<BMSFile>> getBmsFiles;

    private readonly Action<List<BMSFile>> setBmsFiles;

    private readonly Func<List<LR2SongDBExtended.bmson_song>> getBmsonSongs;

    private readonly Action<List<LR2SongDBExtended.bmson_song>> setBmsonSongs;

    private readonly Func<DispatcherCollection<BMSPackage>> getPendingPackages;

    private readonly Action<DispatcherCollection<BMSPackage>> setPendingPackages;

    private readonly Func<DispatcherCollection<BMSPackage>> getInstalledPackages;

    private readonly Action<DispatcherCollection<BMSPackage>> setInstalledPackages;

    private readonly Action invalidateBmsHashIndex;

    private readonly Action invalidateInstalledDirectoryIndex;

    private readonly Action invalidateParentFolderCache;

    private readonly Action clearDuplicatedCache;

    private readonly Action raiseBmsFilesChanged;

    private readonly Action raiseInstalledPackagesChanged;

    public BmsLibraryStateApplier(
        BmsLibraryDbGateway dbGateway,
        Func<List<BMSFile>> getBmsFiles,
        Action<List<BMSFile>> setBmsFiles,
        Func<List<LR2SongDBExtended.bmson_song>> getBmsonSongs,
        Action<List<LR2SongDBExtended.bmson_song>> setBmsonSongs,
        Func<DispatcherCollection<BMSPackage>> getPendingPackages,
        Action<DispatcherCollection<BMSPackage>> setPendingPackages,
        Func<DispatcherCollection<BMSPackage>> getInstalledPackages,
        Action<DispatcherCollection<BMSPackage>> setInstalledPackages,
        Action invalidateBmsHashIndex,
        Action invalidateInstalledDirectoryIndex,
        Action invalidateParentFolderCache,
        Action clearDuplicatedCache,
        Action raiseBmsFilesChanged,
        Action raiseInstalledPackagesChanged)
    {
        this.dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));
        this.getBmsFiles = getBmsFiles ?? throw new ArgumentNullException(nameof(getBmsFiles));
        this.setBmsFiles = setBmsFiles ?? throw new ArgumentNullException(nameof(setBmsFiles));
        this.getBmsonSongs = getBmsonSongs ?? throw new ArgumentNullException(nameof(getBmsonSongs));
        this.setBmsonSongs = setBmsonSongs ?? throw new ArgumentNullException(nameof(setBmsonSongs));
        this.getPendingPackages = getPendingPackages ?? throw new ArgumentNullException(nameof(getPendingPackages));
        this.setPendingPackages = setPendingPackages ?? throw new ArgumentNullException(nameof(setPendingPackages));
        this.getInstalledPackages = getInstalledPackages ?? throw new ArgumentNullException(nameof(getInstalledPackages));
        this.setInstalledPackages = setInstalledPackages ?? throw new ArgumentNullException(nameof(setInstalledPackages));
        this.invalidateBmsHashIndex = invalidateBmsHashIndex ?? throw new ArgumentNullException(nameof(invalidateBmsHashIndex));
        this.invalidateInstalledDirectoryIndex = invalidateInstalledDirectoryIndex ?? throw new ArgumentNullException(nameof(invalidateInstalledDirectoryIndex));
        this.invalidateParentFolderCache = invalidateParentFolderCache ?? throw new ArgumentNullException(nameof(invalidateParentFolderCache));
        this.clearDuplicatedCache = clearDuplicatedCache ?? throw new ArgumentNullException(nameof(clearDuplicatedCache));
        this.raiseBmsFilesChanged = raiseBmsFilesChanged ?? throw new ArgumentNullException(nameof(raiseBmsFilesChanged));
        this.raiseInstalledPackagesChanged = raiseInstalledPackagesChanged ?? throw new ArgumentNullException(nameof(raiseInstalledPackagesChanged));
    }

    public void ApplyPendingPackageMutationDelta(PendingPackageMutationDelta delta)
    {
        if (delta == null || !delta.HasChanges)
        {
            return;
        }

        setPendingPackages(new DispatcherCollection<BMSPackage>(
            new ObservableCollection<BMSPackage>(delta.RemainingPackages ?? new List<BMSPackage>()),
            DispatcherHelper.UIDispatcher));

        List<string> installPathsToDelete = (delta.InstallPathsToDelete ?? new List<string>())
            .Where((string path) => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            UnregisterBmsFiles(delta.FilesToUnregister.Distinct().ToList());
        }

        if (delta.BmsonSongsToUnregister.Count > 0)
        {
            UnregisterBmsonSongs(delta.BmsonSongsToUnregister.Distinct().ToList());
        }

        if (delta.InvalidateBMSHashIndex)
        {
            invalidateBmsHashIndex();
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

        List<BMSFile> removedFilesList = bmsFiles.Where((BMSFile file) => file != null).ToList();
        if (removedFilesList.Count == 0)
        {
            return;
        }

        HashSet<string> removedPaths = new HashSet<string>(
            removedFilesList.Where((BMSFile file) => !string.IsNullOrWhiteSpace(file.path)).Select((BMSFile file) => file.path),
            StringComparer.OrdinalIgnoreCase);
        HashSet<BMSFile> removedFileRefs = new HashSet<BMSFile>(removedFilesList);
        setBmsFiles(getBmsFiles()
            .Where((BMSFile file) => !IsMatchedRemovedFile(file, removedPaths, removedFileRefs))
            .ToList());
        dbGateway.DeleteSongsAndMaintenance(removedFilesList);
        DispatcherCollection<BMSPackage> installedPackages = getInstalledPackages();
        bool installedPackagesChanged = false;
        List<BMSPackage> emptyInstalledPackages = new List<BMSPackage>();

        foreach (BMSPackage installedPackage in installedPackages.Where((BMSPackage package) => package != null).ToList())
        {
            int countBefore = installedPackage.ChartFiles.Count;
            installedPackage.ChartFiles.RemoveAll((BMSFile file) => IsMatchedRemovedFile(file, removedPaths, removedFileRefs));
            if (installedPackage.ChartFiles.Count != countBefore)
            {
                installedPackagesChanged = true;
                if (installedPackage.ChartFiles.Count == 0)
                {
                    emptyInstalledPackages.Add(installedPackage);
                }
            }
        }

        if (emptyInstalledPackages.Count > 0)
        {
            foreach (BMSPackage emptyInstalledPackage in emptyInstalledPackages)
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

        List<LR2SongDBExtended.bmson_song> removedSongsList = bmsonSongs
            .Where((LR2SongDBExtended.bmson_song song) => song != null && !string.IsNullOrWhiteSpace(song.path))
            .ToList();
        if (removedSongsList.Count == 0)
        {
            return;
        }

        HashSet<string> removedPaths = new HashSet<string>(
            removedSongsList.Select((LR2SongDBExtended.bmson_song song) => song.path),
            StringComparer.OrdinalIgnoreCase);
        HashSet<LR2SongDBExtended.bmson_song> removedSongRefs = new HashSet<LR2SongDBExtended.bmson_song>(removedSongsList);
        setBmsonSongs(getBmsonSongs()
            .Where((LR2SongDBExtended.bmson_song song) => song != null && !removedSongRefs.Contains(song) && !removedPaths.Contains(song.path))
            .ToList());
        dbGateway.DeleteBmsonSongs(removedSongsList);

        DispatcherCollection<BMSPackage> installedPackages = getInstalledPackages();
        bool installedPackagesChanged = false;
        List<BMSPackage> emptyInstalledPackages = new List<BMSPackage>();
        foreach (BMSPackage installedPackage in installedPackages.Where((BMSPackage package) => package != null).ToList())
        {
            int countBefore = installedPackage.ChartFiles.Count;
            installedPackage.ChartFiles.RemoveAll((BMSFile file) => IsMatchedRemovedBmsonFile(file, removedPaths, removedSongRefs));
            if (installedPackage.ChartFiles.Count != countBefore)
            {
                installedPackagesChanged = true;
                if (installedPackage.ChartFiles.Count == 0)
                {
                    emptyInstalledPackages.Add(installedPackage);
                }
            }
        }
        if (emptyInstalledPackages.Count > 0)
        {
            foreach (BMSPackage emptyInstalledPackage in emptyInstalledPackages)
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
                Encoding encoding = Encoding.GetEncoding("shift_jis", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
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
}
