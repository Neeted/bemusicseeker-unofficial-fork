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

/// <summary>
/// 不足登録の追加結果と、出力移行が完了してから外す旧登録を保持します。
/// 準備時に既存の親子登録を置換・削除する処理は持ちません。
/// </summary>
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
    /// <summary>
    /// 通常・追加出力先の不足登録を補います。親子重複は変更前に拒否し、既存登録は置き換えません。
    /// </summary>
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
        int addedCount = AddMissingSearchRoots(config, expectedPaths);
        return new CustomFolderOutputBaseSearchRootSyncResult(addedCount, 0);
    }

    /// <summary>
    /// 追加出力先の配置を検証して不足登録を補い、不要になった旧追加登録を外します。
    /// </summary>
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

    /// <summary>
    /// 通常・追加出力先を検証して不足登録を補い、移行完了後に外す旧登録を返します。
    /// </summary>
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

        int addedCount = AddMissingSearchRoots(config, currentPaths, registeredBeforeRemove);
        return new CustomFolderOutputBaseSearchRootSyncPlan(addedCount, removedPaths);
    }

    /// <summary>
    /// 追加出力先を検証して不足登録を補い、移行完了後に外す旧登録を返します。
    /// </summary>
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

        int addedCount = AddMissingSearchRoots(config, currentPaths, registeredBeforeRemove);
        return new CustomFolderOutputBaseSearchRootSyncPlan(addedCount, removedPaths);
    }

    /// <summary>
    /// 移行完了後に、準備で確定した旧登録だけを外します。
    /// </summary>
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
        LongPathFileSystem.CreateDirectory(CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(path));
    }

    private static int AddMissingSearchRoots(
        LR2Config config,
        IEnumerable<string> paths,
        IReadOnlyList<string> registeredPaths = null)
    {
        IReadOnlyList<string> expectedPaths = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(paths);
        registeredPaths ??= config.GetBMSSearchDirectoriesForChangeTracking();
        ValidateNoNestedExpectedPaths(expectedPaths);
        foreach (string expectedPath in expectedPaths)
        {
            ValidateSjisDirectoryPath(expectedPath, BeMusicSeeker.Properties.Resources.Label_CustomFolderOutputBase);
            ValidateOutputBaseAgainstSearchRoots(
                expectedPath, registeredPaths, BeMusicSeeker.Properties.Resources.Label_CustomFolderOutputBase);
        }

        // 不正な親子登録を削除・置換せず、全候補の検証後に不足する同列の登録だけを補います。
        List<string> addedPaths = [.. expectedPaths
            .Where(path => !registeredPaths.Any(registeredPath => IsSameDirectory(path, registeredPath)))];
        foreach (string expectedPath in expectedPaths)
        {
            EnsureSjisDirectoryExists(expectedPath);
        }
        if (addedPaths.Count > 0)
        {
            config.AddBMSSearchDirectories(addedPaths);
        }
        return addedPaths.Count;
    }

    /// <summary>
    /// 出力先と登録済み検索ルートの配置を検証します。同一パスは採用でき、
    /// ルートフォルダ出力先に限り配下の登録を復元対象として許可します。
    /// 禁止する親子関係は設定不備として通知し、登録を自動補正しません。
    /// </summary>
    internal static void ValidateOutputBaseAgainstSearchRoots(
        string outputBasePath,
        IEnumerable<string> registeredRoots,
        string outputBaseLabel,
        bool allowRegisteredChildren = false)
    {
        foreach (string registeredRoot in registeredRoots ?? [])
        {
            if (IsSameDirectory(outputBasePath, registeredRoot)
                || !IsSameOrNestedDirectory(outputBasePath, registeredRoot)
                || (allowRegisteredChildren && IsSameOrChildDirectory(registeredRoot, outputBasePath)))
            {
                continue;
            }
            throw new ArgumentException(string.Format(
                CultureInfo.CurrentCulture,
                allowRegisteredChildren
                    ? BeMusicSeeker.Properties.Resources.Validation_OutputBaseInsideBmsRootFormat
                    : BeMusicSeeker.Properties.Resources.Validation_OutputBaseNestedWithBmsRootFormat,
                outputBaseLabel,
                outputBasePath,
                registeredRoot));
        }
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
                    throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, BeMusicSeeker.Properties.Resources.Validation_OutputBasesOverlapFormat,
                        BeMusicSeeker.Properties.Resources.Label_CustomFolderOutputBase, BeMusicSeeker.Properties.Resources.Label_CustomFolderOutputBase));
                }
            }
        }
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

    /// <summary>
    /// パスを正規化し、同一または子ディレクトリかを区切り文字単位で判定します。
    /// </summary>
    internal static bool IsSameOrChildDirectory(string candidate, string parent)
    {
        string normalizedCandidate = NormalizeComparableDirectoryPath(candidate);
        string normalizedParent = NormalizeComparableDirectoryPath(parent);
        return !string.IsNullOrWhiteSpace(normalizedCandidate)
            && !string.IsNullOrWhiteSpace(normalizedParent)
            && IsSameOrChildPath(normalizedCandidate, normalizedParent);
    }

    private static bool IsSameOrChildPath(string candidate, string parent)
    {
        if (string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string parentWithSeparator = Path.EndsInDirectorySeparator(parent)
            ? parent
            : parent + Path.DirectorySeparatorChar;
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
