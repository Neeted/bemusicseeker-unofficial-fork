using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
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
        : this(addedCount, removedPaths, 0)
    {
    }

    internal CustomFolderOutputBaseSearchRootSyncPlan(int addedCount, IReadOnlyList<string> removedPaths, int appliedRemovedCount)
    {
        AddedCount = addedCount;
        this.removedPaths = removedPaths ?? [];
        AppliedRemovedCount = appliedRemovedCount;
    }

    internal int AddedCount { get; }

    internal int AppliedRemovedCount { get; }

    internal IReadOnlyList<string> RemovedPaths => removedPaths ?? [];

    internal bool Changed => AddedCount > 0 || AppliedRemovedCount > 0 || RemovedPaths.Count > 0;
}

internal readonly struct CustomFolderOutputBaseSearchRootAddResult
{
    internal CustomFolderOutputBaseSearchRootAddResult(int addedCount, int removedCount)
    {
        AddedCount = addedCount;
        RemovedCount = removedCount;
    }

    internal int AddedCount { get; }

    internal int RemovedCount { get; }
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

        IReadOnlyList<string> defaultPaths = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories([defaultBaseDirectory]);
        IReadOnlyList<string> additionalPaths = CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(serializedAdditionalBaseDirectories);
        IReadOnlyList<string> expectedPaths = [.. defaultPaths.Concat(additionalPaths)];
        ValidateNoNestedExpectedPaths(expectedPaths);
        CustomFolderOutputBaseSearchRootAddResult defaultAddResult = AddMissingSearchRoots(
            config,
            defaultPaths,
            adoptNestedRegisteredRoots: false);
        List<string> registeredBeforeAdditionalAdd = config.GetBMSSearchDirectoriesForChangeTracking();
        IReadOnlyList<string> protectedRootPaths = CreateRegisteredRootPathsOverlappingDefaultOutputRoots(
            registeredBeforeAdditionalAdd,
            defaultPaths);
        CustomFolderOutputBaseSearchRootAddResult additionalAddResult = AddMissingSearchRoots(
            config,
            additionalPaths,
            registeredBeforeAdditionalAdd,
            adoptionProtectedRootPaths: protectedRootPaths);
        return new CustomFolderOutputBaseSearchRootSyncResult(
            defaultAddResult.AddedCount + additionalAddResult.AddedCount,
            defaultAddResult.RemovedCount + additionalAddResult.RemovedCount);
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
        IReadOnlyList<string> currentDefaultPaths = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories([currentDefaultBaseDirectory]);
        IReadOnlyList<string> currentAdditionalPaths = CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(currentSerializedAdditionalBaseDirectories);
        IReadOnlyList<string> currentPaths = [.. currentDefaultPaths.Concat(currentAdditionalPaths)];
        ValidateNoNestedExpectedPaths(currentPaths);
        IReadOnlyList<string> preservedPaths = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(preservedRootPaths);
        List<string> registeredBeforeRemove = config.GetBMSSearchDirectoriesForChangeTracking();
        List<string> removedPaths = [.. previousPaths.Where(path =>
            !currentPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
            && !preservedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
            && registeredBeforeRemove.Contains(path, StringComparer.OrdinalIgnoreCase))];

        CustomFolderOutputBaseSearchRootAddResult defaultAddResult = AddMissingSearchRoots(
            config,
            currentDefaultPaths,
            registeredBeforeRemove,
            adoptNestedRegisteredRoots: false);
        List<string> registeredBeforeAdditionalAdd = config.GetBMSSearchDirectoriesForChangeTracking();
        IReadOnlyList<string> protectedRootPaths = CreateRegisteredRootPathsOverlappingDefaultOutputRoots(
            registeredBeforeAdditionalAdd,
            currentDefaultPaths)
            .Concat(preservedPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        CustomFolderOutputBaseSearchRootAddResult additionalAddResult = AddMissingSearchRoots(
            config,
            currentAdditionalPaths,
            registeredBeforeAdditionalAdd,
            adoptionProtectedRootPaths: protectedRootPaths);
        return new CustomFolderOutputBaseSearchRootSyncPlan(
            defaultAddResult.AddedCount + additionalAddResult.AddedCount,
            removedPaths,
            defaultAddResult.RemovedCount + additionalAddResult.RemovedCount);
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
        List<string> registeredBeforeRemove = config.GetBMSSearchDirectoriesForChangeTracking();
        List<string> removedPaths = [.. previousPaths.Where(path =>
            !currentPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
            && !preservedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
            && registeredBeforeRemove.Contains(path, StringComparer.OrdinalIgnoreCase))];

        CustomFolderOutputBaseSearchRootAddResult addResult = AddMissingSearchRoots(config, currentPaths, registeredBeforeRemove);

        return new CustomFolderOutputBaseSearchRootSyncPlan(addResult.AddedCount, removedPaths, addResult.RemovedCount);
    }

    internal static CustomFolderOutputBaseSearchRootSyncResult CompleteAdditionalOutputBaseRootSync(
        LR2Config config,
        CustomFolderOutputBaseSearchRootSyncPlan plan)
    {
        if (config == null)
        {
            return default;
        }

        List<string> registeredBeforeRemove = config.GetBMSSearchDirectoriesForChangeTracking();
        List<string> removablePaths = [.. plan.RemovedPaths
            .Where(path => registeredBeforeRemove.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        int removedCount = config.RemoveBMSSearchDirectories(removablePaths) ? removablePaths.Count : 0;
        return new CustomFolderOutputBaseSearchRootSyncResult(plan.AddedCount, plan.AppliedRemovedCount + removedCount);
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
        LongPathFileSystem.CreateDirectory(CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(path));
    }

    private static CustomFolderOutputBaseSearchRootAddResult AddMissingSearchRoots(
        LR2Config config,
        IEnumerable<string> paths,
        IReadOnlyList<string> registeredPaths = null,
        bool adoptNestedRegisteredRoots = true,
        IEnumerable<string> adoptionProtectedRootPaths = null)
    {
        IReadOnlyList<string> expectedPaths = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(paths);
        List<string> registeredBeforeAdd = registeredPaths == null
            ? config.GetBMSSearchDirectoriesForChangeTracking()
            : [.. registeredPaths];
        IReadOnlyList<string> protectedRootPaths = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(adoptionProtectedRootPaths);
        foreach (string expectedPath in expectedPaths)
        {
            EnsureSjisDirectoryExists(expectedPath);
        }
        ValidateNoNestedExpectedPaths(expectedPaths);

        List<string> adoptionRemovedPaths = adoptNestedRegisteredRoots
            ? [.. registeredBeforeAdd.Where(registeredPath =>
                !IsSameOrNestedWithAnyRoot(registeredPath, protectedRootPaths)
                && expectedPaths.Any(expectedPath =>
                    !IsSameDirectory(expectedPath, registeredPath)
                    && IsSameOrNestedDirectory(expectedPath, registeredPath)))
                .Distinct(StringComparer.OrdinalIgnoreCase)]
            : [];
        List<string> addedPaths = [.. expectedPaths
            .Where(path => !registeredBeforeAdd.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Where(path => adoptNestedRegisteredRoots
                || !registeredBeforeAdd.Any(registeredPath => IsSameOrNestedDirectory(path, registeredPath)))
            .Where(path => !IsSameOrNestedWithAnyRoot(path, protectedRootPaths))];
        if (adoptionRemovedPaths.Count > 0)
        {
            IReadOnlyList<string> nextPaths = [.. registeredBeforeAdd
                .Except(adoptionRemovedPaths, StringComparer.OrdinalIgnoreCase)
                .Concat(addedPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            config.SetBMSSearchDirectories(nextPaths);
        }
        else if (addedPaths.Count > 0)
        {
            config.AddBMSSearchDirectories(addedPaths);
        }
        return new CustomFolderOutputBaseSearchRootAddResult(addedPaths.Count, adoptionRemovedPaths.Count);
    }

    private static IReadOnlyList<string> CreateRegisteredRootPathsOverlappingDefaultOutputRoots(
        IEnumerable<string> registeredPaths,
        IEnumerable<string> defaultOutputRootPaths)
    {
        IReadOnlyList<string> defaultPaths = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(defaultOutputRootPaths);
        return [.. CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(registeredPaths)
            .Where(registeredPath => defaultPaths.Any(defaultPath => IsSameOrNestedDirectory(defaultPath, registeredPath)))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static void ValidateNoNestedExpectedPaths(IReadOnlyList<string> expectedPaths)
    {
        expectedPaths ??= [];
        for (int leftIndex = 0; leftIndex < expectedPaths.Count; leftIndex++)
        {
            for (int rightIndex = leftIndex + 1; rightIndex < expectedPaths.Count; rightIndex++)
            {
                if (IsSameOrNestedDirectory(expectedPaths[leftIndex], expectedPaths[rightIndex]))
                {
                    throw new ArgumentException(BeMusicSeeker.Properties.Resources.Validation_AdditionalOutputBasesNested);
                }
            }
        }
    }

    private static bool IsSameOrNestedWithAnyRoot(string path, IEnumerable<string> roots)
    {
        string normalizedPath = NormalizeComparableDirectoryPath(path);
        return !string.IsNullOrWhiteSpace(normalizedPath)
            && roots != null
            && roots.Any(root => IsSameOrNestedDirectory(normalizedPath, root));
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
