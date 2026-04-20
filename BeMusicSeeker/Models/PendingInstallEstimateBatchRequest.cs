using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models;

internal sealed class PendingInstallEstimateBatchRequest
{
    public PendingInstallEstimateBatchSource Source { get; }

    public BMSPackage[] Packages { get; }

    public int PackageCount => Packages.Length;

    public string DisplayName { get; }

    public string[] RegroupEligibleSourceDirectories { get; }

    public PendingInstallEstimateBatchRequest(
        PendingInstallEstimateBatchSource source,
        IEnumerable<BMSPackage> packages,
        string displayName,
        IEnumerable<string> regroupEligibleSourceDirectories = null)
    {
        Source = source;
        Packages = (packages ?? Enumerable.Empty<BMSPackage>())
            .Where((BMSPackage package) => package != null)
            .ToArray();
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? GetDisplayName(Packages.FirstOrDefault()?.path)
            : displayName;
        RegroupEligibleSourceDirectories = (regroupEligibleSourceDirectories ?? Enumerable.Empty<string>())
            .Where((string path) => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string GetDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
    }
}
