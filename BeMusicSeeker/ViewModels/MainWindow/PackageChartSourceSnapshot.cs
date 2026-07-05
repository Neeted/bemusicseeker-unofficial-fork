using System.Collections.Generic;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Holds package chart sources accepted by the virtual chart-list pipeline.
/// </summary>
internal sealed class PackageChartSourceSnapshot
{
    /// <summary>
    /// Initializes a snapshot whose package entries are the live virtual row factory input.
    /// </summary>
    /// <param name="entries">Package chart entries.</param>
    internal PackageChartSourceSnapshot(IReadOnlyList<PackageChartEntry> entries)
    {
        Entries = entries ?? [];
    }

    /// <summary>
    /// Gets live package chart entries used by the virtual package subset.
    /// </summary>
    internal IReadOnlyList<PackageChartEntry> Entries { get; }
}
