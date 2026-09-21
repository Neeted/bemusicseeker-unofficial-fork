using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Immutable source-to-destination facts selected during package-install
/// preflight.  Consumers must use these paths instead of deriving them again
/// after collision resolution has completed.
/// </summary>
internal sealed class PackageInstallDestinationMap
{
    private readonly IReadOnlyDictionary<string, string> destinationsBySourcePath;

    /// <summary>
    /// Creates the immutable package and chart destinations selected by one
    /// package-install preflight. Source keys use filesystem path identity,
    /// while each selected destination value retains its original casing.
    /// </summary>
    internal PackageInstallDestinationMap(
        string packageSourcePath,
        string packageDestinationPath,
        IEnumerable<FileDbMutationPathPlan> mutationPaths,
        IEnumerable<KeyValuePair<string, string>> chartDestinationPaths)
    {
        PackageSourcePath = packageSourcePath ?? string.Empty;
        PackageDestinationPath = packageDestinationPath ?? string.Empty;

        var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (FileDbMutationPathPlan mutationPath in mutationPaths ?? [])
        {
            AddDestination(destinations, mutationPath?.SourcePath, mutationPath?.DestinationPath);
        }
        foreach (KeyValuePair<string, string> chartDestinationPath in chartDestinationPaths ?? [])
        {
            AddDestination(destinations, chartDestinationPath.Key, chartDestinationPath.Value);
        }
        destinationsBySourcePath = new ReadOnlyDictionary<string, string>(destinations);
    }

    /// <summary>
    /// Gets the package path observed before filesystem mutation.
    /// </summary>
    internal string PackageSourcePath { get; }

    /// <summary>
    /// Gets the exact package path selected during preflight. Directory
    /// packages use their installed root; single-file packages use the
    /// selected chart destination when one exists.
    /// </summary>
    internal string PackageDestinationPath { get; }

    /// <summary>
    /// Gets the exact, case-preserving destination selected for a source path.
    /// Missing source identities fail closed instead of deriving a new path
    /// after preflight.
    /// </summary>
    internal string GetRequiredDestinationPath(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)
            || !destinationsBySourcePath.TryGetValue(sourcePath, out string destinationPath))
        {
            throw new InvalidOperationException(
                "The package-install destination map has no destination for the source path: "
                + (sourcePath ?? string.Empty));
        }
        return destinationPath;
    }

    private static void AddDestination(
        IDictionary<string, string> destinations,
        string sourcePath,
        string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
        {
            return;
        }
        if (destinations.TryGetValue(sourcePath, out string existingDestinationPath)
            && !string.Equals(existingDestinationPath, destinationPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The package-install destination map contains conflicting destinations for the source path: "
                + sourcePath);
        }
        destinations[sourcePath] = destinationPath;
    }
}
