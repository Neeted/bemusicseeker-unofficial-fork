using System;
using System.Collections.Generic;

namespace BeMusicSeeker.ViewModels;

internal enum PendingInstallPackageOperationKind
{
    ForceInstall,
    ManualInstall
}

internal sealed class PendingInstallPackageOperationRequest
{
    private PendingInstallPackageOperationRequest(PendingInstallPackageOperationKind kind, IReadOnlyList<ChartOperationTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }
        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one chart operation target is required.", nameof(targets));
        }

        Kind = kind;
        Targets = targets;
    }

    internal PendingInstallPackageOperationKind Kind { get; }

    internal IReadOnlyList<ChartOperationTarget> Targets { get; }

    internal int SelectedRowCount => Targets.Count;

    internal bool IsForceInstall => Kind == PendingInstallPackageOperationKind.ForceInstall;

    internal bool IsManualInstall => Kind == PendingInstallPackageOperationKind.ManualInstall;

    internal static PendingInstallPackageOperationRequest CreateForceInstall(IEnumerable<ChartOperationTarget> targets)
    {
        return new PendingInstallPackageOperationRequest(PendingInstallPackageOperationKind.ForceInstall, MaterializeTargets(targets));
    }

    internal static PendingInstallPackageOperationRequest CreateManualInstall(IEnumerable<ChartOperationTarget> targets)
    {
        return new PendingInstallPackageOperationRequest(PendingInstallPackageOperationKind.ManualInstall, MaterializeTargets(targets));
    }

    private static IReadOnlyList<ChartOperationTarget> MaterializeTargets(IEnumerable<ChartOperationTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }

        var materializedTargets = new List<ChartOperationTarget>();
        foreach (ChartOperationTarget target in targets)
        {
            if (target == null)
            {
                throw new ArgumentException("Selected target collection must not contain null targets.", nameof(targets));
            }

            materializedTargets.Add(target);
        }

        return materializedTargets;
    }
}
