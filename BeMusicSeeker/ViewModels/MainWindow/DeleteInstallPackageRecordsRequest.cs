using System;
using System.Collections.Generic;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Classifies the install package records selected for deletion from the chart table.
/// </summary>
internal enum DeleteInstallPackageRecordsKind
{
    Pending,
    Installed
}

/// <summary>
/// Describes a chart-table request to delete pending or newly installed package records.
/// </summary>
internal sealed class DeleteInstallPackageRecordsRequest
{
    private DeleteInstallPackageRecordsRequest(DeleteInstallPackageRecordsKind kind, IReadOnlyList<ChartOperationTarget> targets)
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

    internal DeleteInstallPackageRecordsKind Kind { get; }

    internal IReadOnlyList<ChartOperationTarget> Targets { get; }

    internal int SelectedRowCount => Targets.Count;

    internal bool IsPending => Kind == DeleteInstallPackageRecordsKind.Pending;

    internal bool IsInstalled => Kind == DeleteInstallPackageRecordsKind.Installed;

    internal static DeleteInstallPackageRecordsRequest CreatePending(IEnumerable<ChartOperationTarget> targets)
    {
        return new DeleteInstallPackageRecordsRequest(DeleteInstallPackageRecordsKind.Pending, MaterializeTargets(targets));
    }

    internal static DeleteInstallPackageRecordsRequest CreateInstalled(IEnumerable<ChartOperationTarget> targets)
    {
        return new DeleteInstallPackageRecordsRequest(DeleteInstallPackageRecordsKind.Installed, MaterializeTargets(targets));
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
