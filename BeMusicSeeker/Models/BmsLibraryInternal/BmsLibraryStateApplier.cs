using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
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
    Action raiseInstalledPackagesChanged,
    Action<string, Exception> notifyLr2SongDbWriteFailure = null)
{
    private readonly BmsLibraryDbGateway dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));

    private readonly Func<DispatcherCollection<ChartPackage>> getPendingPackages = getPendingPackages ?? throw new ArgumentNullException(nameof(getPendingPackages));

    private readonly Action<DispatcherCollection<ChartPackage>> setPendingPackages = setPendingPackages ?? throw new ArgumentNullException(nameof(setPendingPackages));

    private readonly Func<DispatcherCollection<ChartPackage>> getInstalledPackages = getInstalledPackages ?? throw new ArgumentNullException(nameof(getInstalledPackages));

    private readonly Action<DispatcherCollection<ChartPackage>> setInstalledPackages = setInstalledPackages ?? throw new ArgumentNullException(nameof(setInstalledPackages));

    private readonly Action raiseInstalledPackagesChanged = raiseInstalledPackagesChanged ?? throw new ArgumentNullException(nameof(raiseInstalledPackagesChanged));

    private readonly Action<string, Exception> notifyLr2SongDbWriteFailure = notifyLr2SongDbWriteFailure;

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

    public BmsLibraryStateApplyResult ApplyLibraryMutationDelta(
        LibraryMutationDelta delta,
        IEnumerable<OwnedChartRemoveRequest> resolvedRemoveRequests = null)
    {
        var result = new BmsLibraryStateApplyResult();
        if (delta == null)
        {
            return result;
        }

        List<BmsSongPathReplacement> bmsPathReplacements = [];
        List<BmsonSongPathReplacement> bmsonPathReplacements = [];
        var folderParentHashCache = new Lr2SongFolderParentNormalizer.Lr2FolderParentHashCache();
        foreach (LibraryChartPathChange chartPathChange in delta.ChartPathChanges)
        {
            BMSFile bmsFile = chartPathChange?.GetBmsStorageOwner();
            if (bmsFile != null)
            {
                bmsPathReplacements.Add(CreateBmsSongPathReplacement(bmsFile, chartPathChange.NewPath, chartPathChange.OldPath, folderParentHashCache));
            }
            else
            {
                LR2SongDBExtended.bmson_song bmsonSong = chartPathChange?.GetBmsonStorageOwner();
                if (bmsonSong != null)
                {
                    bmsonPathReplacements.Add(CreateBmsonSongPathReplacement(bmsonSong, chartPathChange.NewPath, chartPathChange.OldPath));
                }
            }
        }

        BmsLibraryStateApplyResult dbResult = ExecuteLr2SongDbMutation(
            () => dbGateway.ReplaceLibraryMutationRows(delta.FolderPathChanges, bmsPathReplacements, bmsonPathReplacements),
            "lr2_song_db_library_mutation_path_replace_failed");
        result.FolderDbMs = dbResult.FolderDbMs;
        result.BmsPathDbMs = dbResult.BmsPathDbMs;
        result.BmsonPathDbMs = dbResult.BmsonPathDbMs;

        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (LibraryChartPathChange chartPathChange in delta.ChartPathChanges)
        {
            BMSFile bmsFile = chartPathChange?.GetBmsStorageOwner();
            if (bmsFile != null)
            {
                ApplyBmsFilePathInMemory(bmsFile, chartPathChange.NewPath, chartPathChange.OldPath, folderParentHashCache);
            }
            else
            {
                LR2SongDBExtended.bmson_song bmsonSong = chartPathChange?.GetBmsonStorageOwner();
                if (bmsonSong != null)
                {
                    ApplyBmsonSongPathInMemory(bmsonSong, chartPathChange.NewPath, chartPathChange.OldPath);
                }
            }
        }
        stopwatch.Stop();
        result.PathMemoryApplyMs = stopwatch.ElapsedMilliseconds;

        stopwatch.Restart();
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
        stopwatch.Stop();
        result.PackageApplyMs = stopwatch.ElapsedMilliseconds;

        if (delta.ChartRemoveRequests.Count > 0 || resolvedRemoveRequests != null)
        {
            UnregisterCharts(resolvedRemoveRequests ?? delta.ChartRemoveRequests);
        }

        if (delta.RaiseInstalledPackagesChanged)
        {
            raiseInstalledPackagesChanged();
        }
        return result;
    }

    private static BmsSongPathReplacement CreateBmsSongPathReplacement(
        BMSFile bmsFile,
        string newPath,
        string oldPath,
        Lr2SongFolderParentNormalizer.Lr2FolderParentHashCache folderParentHashCache)
    {
        ValidateBmsFilePathChange(bmsFile, newPath, oldPath);
        BMSFile copy = bmsFile.CreateSongRowPersistenceCopy();
        copy.path = newPath;
        copy.SetTextGroupFlag(Lr2TextGroupResolver.ResolveFlag(newPath, bmsFile.txt.GetValueOrDefault()));
        copy.folder = null;
        copy.parent = null;
        Lr2SongRowEnricher.EnrichGeneratedSong(copy, folderParentHashCache);
        BMSFileMaintenanceInfo maintenanceInfo = bmsFile.HasValidMaintenanceInfoSnapshot
            ? bmsFile.TryGetMaintenanceInfoWithoutCreating()?.CreatePersistenceCopy(newPath, bmsFile.hash)
            : null;
        return new BmsSongPathReplacement
        {
            Song = copy,
            OldPath = string.IsNullOrWhiteSpace(oldPath) ? bmsFile.path : oldPath,
            MaintenanceInfo = maintenanceInfo
        };
    }

    private static BmsonSongPathReplacement CreateBmsonSongPathReplacement(LR2SongDBExtended.bmson_song bmsonSong, string newPath, string oldPath = null)
    {
        ValidateBmsonSongPathChange(bmsonSong, newPath, oldPath);
        LR2SongDBExtended.bmson_song copy = CreateBmsonSongPersistenceCopy(bmsonSong);
        copy.path = newPath;
        copy.folder = Path.GetDirectoryName(newPath) ?? string.Empty;
        copy.MaintenanceInfo = bmsonSong.MaintenanceInfo?.CreatePersistenceCopy();
        copy.MaintenanceInfo?.NormalizeForBmson(copy.path, copy.md5);
        return new BmsonSongPathReplacement
        {
            Song = copy,
            OldPath = string.IsNullOrWhiteSpace(oldPath) ? bmsonSong.path : oldPath
        };
    }

    private static void ApplyBmsFilePathInMemory(
        BMSFile bmsFile,
        string newPath,
        string oldPath,
        Lr2SongFolderParentNormalizer.Lr2FolderParentHashCache folderParentHashCache)
    {
        bmsFile.path = newPath;
        bmsFile.SetTextGroupFlag(Lr2TextGroupResolver.ResolveFlag(newPath, bmsFile.txt.GetValueOrDefault()));
        bmsFile.folder = null;
        bmsFile.parent = null;
        Lr2SongRowEnricher.EnrichGeneratedSong(bmsFile, folderParentHashCache);
    }

    private static void ApplyBmsonSongPathInMemory(LR2SongDBExtended.bmson_song bmsonSong, string newPath, string oldPath = null)
    {
        bmsonSong.path = newPath;
        bmsonSong.folder = Path.GetDirectoryName(newPath) ?? string.Empty;
        bmsonSong.MaintenanceInfo?.NormalizeForBmson(bmsonSong.path, bmsonSong.md5);
    }

    private static void ValidateBmsFilePathChange(BMSFile bmsFile, string newPath, string oldPath = null)
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
        if (!string.IsNullOrWhiteSpace(oldPath)
            && !string.Equals(bmsFile.path, oldPath, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(bmsFile.path, newPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidCastException(Resources.Error_OldPathMismatch);
        }
    }

    private static void ValidateBmsonSongPathChange(LR2SongDBExtended.bmson_song bmsonSong, string newPath, string oldPath = null)
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
    }

    private static LR2SongDBExtended.bmson_song CreateBmsonSongPersistenceCopy(LR2SongDBExtended.bmson_song source)
    {
        if (source == null)
        {
            return null;
        }
        return new LR2SongDBExtended.bmson_song
        {
            path = source.path,
            folder = source.folder,
            title = source.title,
            subtitle = source.subtitle,
            artist = source.artist,
            genre = source.genre,
            level = source.level,
            mode_hint = source.mode_hint,
            md5 = source.md5,
            sha256 = source.sha256,
            banner = source.banner,
            backbmp = source.backbmp,
            stagefile = source.stagefile,
            preview_music = source.preview_music,
            updated_at = source.updated_at
        };
    }

    private void UnregisterCharts(IEnumerable<OwnedChartRemoveRequest> removeRequests)
    {
        if (removeRequests == null)
        {
            throw new ArgumentNullException(nameof(removeRequests));
        }

        List<OwnedChartRemoveRequest> requestList = [.. (removeRequests ?? []).Where(request => request != null)];
        if (requestList.Count == 0)
        {
            return;
        }

        List<BMSFile> bmsFilesToUnregister = [.. requestList
            .Select(request => request.BmsOwner)
            .Where(owner => owner != null)
            .Distinct()];
        List<string> bmsPathCleanupPaths = [.. requestList
            .Where(request => request.Mode == OwnedChartRemoveMode.PathCleanup && request.Kind == ChartFileKind.Bms)
            .Select(request => request.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (bmsFilesToUnregister.Count > 0 || bmsPathCleanupPaths.Count > 0)
        {
            UnregisterBmsFiles(bmsFilesToUnregister, bmsPathCleanupPaths);
        }

        List<LR2SongDBExtended.bmson_song> bmsonSongsToUnregister = [.. requestList
            .Select(request => request.BmsonOwner)
            .Where(owner => owner != null)
            .Distinct()];
        List<string> bmsonPathCleanupPaths = [.. requestList
            .Where(request => request.Mode == OwnedChartRemoveMode.PathCleanup && request.Kind == ChartFileKind.Bmson)
            .Select(request => request.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (bmsonSongsToUnregister.Count > 0 || bmsonPathCleanupPaths.Count > 0)
        {
            UnregisterBmsonSongs(bmsonSongsToUnregister, bmsonPathCleanupPaths);
        }
    }

    private void UnregisterBmsFiles(IEnumerable<BMSFile> bmsFiles, IEnumerable<string> pathCleanupPaths)
    {
        if (bmsFiles == null && pathCleanupPaths == null)
        {
            throw new ArgumentNullException(nameof(bmsFiles));
        }

        List<BMSFile> removedFilesList = [.. bmsFiles.Where(file => file != null)];
        List<string> pathCleanupList = [.. (pathCleanupPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
        if (removedFilesList.Count == 0 && pathCleanupList.Count == 0)
        {
            return;
        }

        var pathCleanupSet = new HashSet<string>(
            pathCleanupList.Select(OwnedChartCollectionState.CreateOwnedPathKey).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var removedFileRefs = new HashSet<BMSFile>(removedFilesList);
        ExecuteLr2SongDbMutation(
            () => dbGateway.DeleteSongsAndMaintenance(removedFilesList),
            "lr2_song_db_bms_unregister_failed");
        ExecuteLr2SongDbMutation(
            () => dbGateway.DeleteSongsAndMaintenanceByPath(pathCleanupList),
            "lr2_song_db_bms_path_cleanup_failed");
        DispatcherCollection<ChartPackage> installedPackages = getInstalledPackages();
        bool installedPackagesChanged = false;
        List<ChartPackage> emptyInstalledPackages = [];

        foreach (ChartPackage installedPackage in installedPackages.Where(package => package != null).ToList())
        {
            if (installedPackage.RemoveChartEntries(entry => IsMatchedRemovedFile(entry, removedFileRefs, pathCleanupSet)))
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

    private void UnregisterBmsonSongs(IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs, IEnumerable<string> pathCleanupPaths)
    {
        if (bmsonSongs == null && pathCleanupPaths == null)
        {
            throw new ArgumentNullException(nameof(bmsonSongs));
        }

        List<LR2SongDBExtended.bmson_song> removedSongsList = [.. bmsonSongs.Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))];
        List<string> pathCleanupList = [.. (pathCleanupPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
        if (removedSongsList.Count == 0 && pathCleanupList.Count == 0)
        {
            return;
        }

        var pathCleanupSet = new HashSet<string>(
            pathCleanupList.Select(OwnedChartCollectionState.CreateOwnedPathKey).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var removedSongRefs = new HashSet<LR2SongDBExtended.bmson_song>(removedSongsList);
        dbGateway.DeleteBmsonSongs(removedSongsList);
        dbGateway.DeleteBmsonSongsByPath(pathCleanupList);

        DispatcherCollection<ChartPackage> installedPackages = getInstalledPackages();
        bool installedPackagesChanged = false;
        List<ChartPackage> emptyInstalledPackages = [];
        foreach (ChartPackage installedPackage in installedPackages.Where(package => package != null).ToList())
        {
            if (installedPackage.RemoveChartEntries(entry => IsMatchedRemovedBmsonFile(entry, removedSongRefs, pathCleanupSet)))
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

    private void ReplaceBmsFilePath(BMSFile bmsFile, string newPath, string oldPath = null)
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

        if (string.IsNullOrWhiteSpace(oldPath))
        {
            oldPath = bmsFile.path;
        }
        else if (!string.Equals(bmsFile.path, oldPath, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(bmsFile.path, newPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidCastException(Resources.Error_OldPathMismatch);
        }

        bmsFile.path = newPath;
        bmsFile.SetTextGroupFlag(Lr2TextGroupResolver.ResolveFlag(newPath, bmsFile.txt.GetValueOrDefault()));
        bmsFile.folder = null;
        bmsFile.parent = null;
        Lr2SongRowEnricher.EnrichGeneratedSong(bmsFile);

        ExecuteLr2SongDbMutation(
            () => dbGateway.ReplaceSongPathWithMaintenance(bmsFile, oldPath),
            "lr2_song_db_bms_path_replace_failed");
    }

    private void ExecuteLr2SongDbMutation(Action mutation, string stage)
    {
        if (mutation == null)
        {
            return;
        }

        try
        {
            mutation();
        }
        catch (Exception ex)
        {
            notifyLr2SongDbWriteFailure?.Invoke(stage, ex);
            throw;
        }
    }

    private T ExecuteLr2SongDbMutation<T>(Func<T> mutation, string stage)
    {
        if (mutation == null)
        {
            return default;
        }

        try
        {
            return mutation();
        }
        catch (Exception ex)
        {
            notifyLr2SongDbWriteFailure?.Invoke(stage, ex);
            throw;
        }
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

    private static bool IsMatchedRemovedFile(PackageChartEntry entry, HashSet<BMSFile> removedFiles, HashSet<string> pathCleanupPaths)
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

        string pathKey = OwnedChartCollectionState.CreateOwnedPathKey(entry.Chart.Path);
        return !string.IsNullOrWhiteSpace(pathKey) && pathCleanupPaths.Contains(pathKey);
    }

    private static bool IsMatchedRemovedBmsonFile(PackageChartEntry entry, HashSet<LR2SongDBExtended.bmson_song> removedSongs, HashSet<string> pathCleanupPaths)
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

        string pathKey = OwnedChartCollectionState.CreateOwnedPathKey(entry.Chart.Path);
        return !string.IsNullOrWhiteSpace(pathKey) && pathCleanupPaths.Contains(pathKey);
    }
}
