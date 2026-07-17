using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingPackageMutationDelta
{
    public bool HasChanges { get; set; }

    public List<ChartPackage> RemainingPackages { get; set; } = [];

    public List<string> InstallPathsToDelete { get; set; } = [];

    public List<PendingPackageEntryMutation> EntryMutations { get; } = [];
}

internal sealed class PendingPackageEntryMutation
{
    public PendingPackageEntryMutation(ChartPackage package, IEnumerable<PackageChartEntry> remainingEntries)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        RemainingEntries = [.. (remainingEntries ?? []).Where(entry => entry?.Chart != null)];
    }

    public ChartPackage Package { get; }

    public IReadOnlyList<PackageChartEntry> RemainingEntries { get; }
}
