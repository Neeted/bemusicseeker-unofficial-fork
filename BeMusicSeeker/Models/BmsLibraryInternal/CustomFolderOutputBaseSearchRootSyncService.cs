using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal readonly struct CustomFolderOutputBaseSearchRootSyncResult
{
    internal CustomFolderOutputBaseSearchRootSyncResult(int addedCount, int removedCount)
    {
        AddedCount = addedCount;
        RemovedCount = removedCount;
    }

    internal int AddedCount { get; }

    internal int RemovedCount { get; }

    internal bool Changed => AddedCount > 0 || RemovedCount > 0;
}

internal readonly struct CustomFolderOutputBaseSearchRootSyncPlan
{
    private readonly IReadOnlyList<string> removedPaths;

    internal CustomFolderOutputBaseSearchRootSyncPlan(int addedCount, IReadOnlyList<string> removedPaths)
    {
        AddedCount = addedCount;
        this.removedPaths = removedPaths ?? [];
    }

    internal int AddedCount { get; }

    internal IReadOnlyList<string> RemovedPaths => removedPaths ?? [];

    internal bool Changed => AddedCount > 0 || RemovedPaths.Count > 0;
}

internal static class CustomFolderOutputBaseSearchRootSyncService
{
    internal static CustomFolderOutputBaseSearchRootSyncResult RepairNormalOutputBaseRoots(
        LR2Config config,
        string defaultBaseDirectory,
        string serializedAdditionalBaseDirectories)
    {
        if (config == null)
        {
            return default;
        }

        int addedCount = AddMissingSearchRoots(
            config,
            new[] { defaultBaseDirectory }.Concat(CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(serializedAdditionalBaseDirectories)));
        return new CustomFolderOutputBaseSearchRootSyncResult(addedCount, 0);
    }

    internal static CustomFolderOutputBaseSearchRootSyncResult SyncAdditionalOutputBaseRoots(
        LR2Config config,
        string previousSerializedBaseDirectories,
        string currentSerializedBaseDirectories,
        IEnumerable<string> preservedRootPaths = null)
    {
        CustomFolderOutputBaseSearchRootSyncPlan plan = PrepareAdditionalOutputBaseRoots(
            config,
            previousSerializedBaseDirectories,
            currentSerializedBaseDirectories,
            preservedRootPaths);
        return CompleteAdditionalOutputBaseRootSync(config, plan);
    }

    internal static CustomFolderOutputBaseSearchRootSyncPlan PrepareNormalOutputBaseRoots(
        LR2Config config,
        string previousDefaultBaseDirectory,
        string currentDefaultBaseDirectory,
        string previousSerializedAdditionalBaseDirectories,
        string currentSerializedAdditionalBaseDirectories,
        IEnumerable<string> preservedRootPaths = null)
    {
        if (config == null)
        {
            return default;
        }

        IReadOnlyList<string> previousPaths = CreateNormalOutputBaseRootSet(
            previousDefaultBaseDirectory,
            previousSerializedAdditionalBaseDirectories);
        IReadOnlyList<string> currentPaths = CreateNormalOutputBaseRootSet(
            currentDefaultBaseDirectory,
            currentSerializedAdditionalBaseDirectories);
        IReadOnlyList<string> preservedPaths = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(preservedRootPaths);
        List<string> registeredBeforeRemove = config.GetBMSSearchDirectories();
        List<string> removedPaths = [.. previousPaths.Where(path =>
            !currentPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
            && !preservedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
            && registeredBeforeRemove.Contains(path, StringComparer.OrdinalIgnoreCase))];

        int addedCount = AddMissingSearchRoots(config, currentPaths, registeredBeforeRemove);
        return new CustomFolderOutputBaseSearchRootSyncPlan(addedCount, removedPaths);
    }

    internal static CustomFolderOutputBaseSearchRootSyncPlan PrepareAdditionalOutputBaseRoots(
        LR2Config config,
        string previousSerializedBaseDirectories,
        string currentSerializedBaseDirectories,
        IEnumerable<string> preservedRootPaths = null)
    {
        if (config == null)
        {
            return default;
        }

        IReadOnlyList<string> previousPaths = CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(previousSerializedBaseDirectories);
        IReadOnlyList<string> currentPaths = CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(currentSerializedBaseDirectories);
        IReadOnlyList<string> preservedPaths = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(preservedRootPaths);
        List<string> registeredBeforeRemove = config.GetBMSSearchDirectories();
        List<string> removedPaths = [.. previousPaths.Where(path =>
            !currentPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
            && !preservedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
            && registeredBeforeRemove.Contains(path, StringComparer.OrdinalIgnoreCase))];

        int addedCount = AddMissingSearchRoots(config, currentPaths, registeredBeforeRemove);

        return new CustomFolderOutputBaseSearchRootSyncPlan(addedCount, removedPaths);
    }

    internal static CustomFolderOutputBaseSearchRootSyncResult CompleteAdditionalOutputBaseRootSync(
        LR2Config config,
        CustomFolderOutputBaseSearchRootSyncPlan plan)
    {
        if (config == null)
        {
            return default;
        }

        int removedCount = config.RemoveBMSSearchDirectories(plan.RemovedPaths) ? plan.RemovedPaths.Count : 0;
        return new CustomFolderOutputBaseSearchRootSyncResult(plan.AddedCount, removedCount);
    }

    internal static void ValidateSjisDirectoryPath(string path)
    {
        ValidateSjisDirectoryPath(path, BeMusicSeeker.Properties.Resources.Label_AdditionalOutputBaseFolder);
    }

    internal static void ValidateSjisDirectoryPath(string path, string pathLabel)
    {
        string normalizedPath = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            throw new ArgumentException(string.Format(
                CultureInfo.CurrentCulture,
                BeMusicSeeker.Properties.Resources.Error_SjisDirectoryPathNotSelectedFormat,
                pathLabel));
        }
        if (!normalizedPath.IsSjisSchemeString())
        {
            throw new ArgumentException(string.Format(
                CultureInfo.CurrentCulture,
                BeMusicSeeker.Properties.Resources.Error_SjisDirectoryPathContainsUnsupportedCharsFormat,
                pathLabel,
                normalizedPath));
        }
    }

    private static IReadOnlyList<string> CreateNormalOutputBaseRootSet(
        string defaultBaseDirectory,
        string serializedAdditionalBaseDirectories)
    {
        return CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(
            new[] { defaultBaseDirectory }
                .Concat(CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(serializedAdditionalBaseDirectories)));
    }

    private static void EnsureSjisDirectoryExists(string path)
    {
        ValidateSjisDirectoryPath(path);
        Directory.CreateDirectory(CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(path));
    }

    private static int AddMissingSearchRoots(
        LR2Config config,
        IEnumerable<string> paths,
        IReadOnlyList<string> registeredPaths = null)
    {
        IReadOnlyList<string> expectedPaths = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(paths);
        List<string> registeredBeforeAdd = registeredPaths == null
            ? config.GetBMSSearchDirectories()
            : [.. registeredPaths];
        List<string> addedPaths = [.. expectedPaths.Where(path => !registeredBeforeAdd.Contains(path, StringComparer.OrdinalIgnoreCase))];
        foreach (string addedPath in addedPaths)
        {
            EnsureSjisDirectoryExists(addedPath);
        }
        if (addedPaths.Count > 0)
        {
            config.AddBMSSearchDirectories(addedPaths);
        }
        return addedPaths.Count;
    }

    internal static bool IsSameDirectory(string left, string right)
    {
        string normalizedLeft = NormalizeComparableDirectoryPath(left);
        string normalizedRight = NormalizeComparableDirectoryPath(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft)
            && !string.IsNullOrWhiteSpace(normalizedRight)
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSameOrNestedDirectory(string left, string right)
    {
        string normalizedLeft = NormalizeComparableDirectoryPath(left);
        string normalizedRight = NormalizeComparableDirectoryPath(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft)
            && !string.IsNullOrWhiteSpace(normalizedRight)
            && (IsSameOrChildPath(normalizedLeft, normalizedRight)
                || IsSameOrChildPath(normalizedRight, normalizedLeft));
    }

    private static bool IsSameOrChildPath(string candidate, string parent)
    {
        if (string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string parentWithSeparator = TrimDirectorySeparatorUnlessRoot(parent) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeComparableDirectoryPath(string path)
    {
        string normalizedPath = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        try
        {
            normalizedPath = Path.GetFullPath(normalizedPath);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
        }
        return TrimDirectorySeparatorUnlessRoot(normalizedPath);
    }

    private static string TrimDirectorySeparatorUnlessRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string root = Path.GetPathRoot(path);
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            ? root
            : trimmed;
    }
}
