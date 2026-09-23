using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2FolderPhysicalParentDirectoryTargetHelper
{
    internal static IReadOnlyCollection<string> CreateTargets(
        IEnumerable<string> lr2FolderFilePaths,
        IEnumerable<string> rootDirectories,
        string normalCustomFolderOutputBaseDir,
        IEnumerable<string> additionalNormalCustomFolderOutputBaseDirs,
        string rootCustomFolderOutputBaseDir,
        IEnumerable<string> builtinSourceDirectories)
    {
        IReadOnlyList<string> boundaries = CreateBoundaries(
            rootDirectories,
            normalCustomFolderOutputBaseDir,
            additionalNormalCustomFolderOutputBaseDirs,
            rootCustomFolderOutputBaseDir,
            builtinSourceDirectories);
        if (boundaries.Count == 0)
        {
            return [];
        }

        var boundarySet = new HashSet<string>(boundaries, StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string filePath in lr2FolderFilePaths ?? [])
        {
            string normalizedFilePath = Lr2FolderPath.NormalizeDirectoryPath(filePath);
            if (string.IsNullOrWhiteSpace(normalizedFilePath) || !visitedFilePaths.Add(normalizedFilePath))
            {
                continue;
            }

            string directory = Lr2FolderPath.SafeGetParentNormalizedDirectory(normalizedFilePath);
            if (string.IsNullOrWhiteSpace(directory) || !visitedDirectories.Add(directory))
            {
                continue;
            }

            string boundary = FindNearestBoundary(directory, boundarySet);
            while (!string.IsNullOrWhiteSpace(directory)
                && !string.IsNullOrWhiteSpace(boundary)
                && !string.Equals(directory, boundary, StringComparison.OrdinalIgnoreCase))
            {
                targets.Add(directory);
                string parent = Lr2FolderPath.SafeGetParentNormalizedDirectory(directory);
                if (string.IsNullOrWhiteSpace(parent)
                    || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                directory = parent;
            }
        }
        return [.. targets.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<string> CreateBoundaries(
        IEnumerable<string> rootDirectories,
        string normalCustomFolderOutputBaseDir,
        IEnumerable<string> additionalNormalCustomFolderOutputBaseDirs,
        string rootCustomFolderOutputBaseDir,
        IEnumerable<string> builtinSourceDirectories)
    {
        var boundaries = new List<string>();
        foreach (string rootDirectory in rootDirectories ?? [])
        {
            boundaries.Add(CreateBoundary(rootDirectory));
        }
        string normalOutputBase = Lr2FolderPath.NormalizeDirectoryPath(normalCustomFolderOutputBaseDir);
        if (!string.IsNullOrWhiteSpace(normalOutputBase))
        {
            boundaries.Add(CreateBoundary(normalOutputBase));
        }
        foreach (string additionalNormalOutputBase in additionalNormalCustomFolderOutputBaseDirs ?? [])
        {
            string normalizedAdditionalNormalOutputBase = Lr2FolderPath.NormalizeDirectoryPath(additionalNormalOutputBase);
            if (!string.IsNullOrWhiteSpace(normalizedAdditionalNormalOutputBase))
            {
                boundaries.Add(CreateBoundary(normalizedAdditionalNormalOutputBase));
            }
        }
        string rootOutputBase = Lr2FolderPath.NormalizeDirectoryPath(rootCustomFolderOutputBaseDir);
        if (!string.IsNullOrWhiteSpace(rootOutputBase))
        {
            boundaries.Add(rootOutputBase);
        }
        boundaries.AddRange(builtinSourceDirectories ?? []);
        return [.. boundaries
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static string CreateBoundary(string rootEquivalentDirectory)
    {
        string normalizedRoot = Lr2FolderPath.NormalizeDirectoryPath(rootEquivalentDirectory);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            return null;
        }

        string parent = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(normalizedRoot));
        return string.IsNullOrWhiteSpace(parent) ? normalizedRoot : parent;
    }

    private static string FindNearestBoundary(string normalizedDirectory, ISet<string> boundaries)
    {
        string current = normalizedDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (boundaries?.Contains(current) == true)
            {
                return current;
            }

            string parent = Lr2FolderPath.SafeGetParentNormalizedDirectory(current);
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = parent;
        }

        return null;
    }
}
