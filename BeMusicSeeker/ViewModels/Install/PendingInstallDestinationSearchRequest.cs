using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class PendingInstallDestinationSearchRequest
{
    private PendingInstallDestinationSearchRequest(
        IReadOnlyList<ChartOperationTarget> packageTargets,
        IReadOnlyList<PackageChartEntry> looseEntries,
        InstallEstimationCandidatePolicy candidatePolicy)
    {
        PackageTargets = packageTargets ?? throw new ArgumentNullException(nameof(packageTargets));
        LooseEntries = looseEntries ?? throw new ArgumentNullException(nameof(looseEntries));
        CandidatePolicy = candidatePolicy;
        if (!HasTargets)
        {
            throw new ArgumentException("At least one pending install destination target is required.", nameof(packageTargets));
        }
    }

    internal IReadOnlyList<ChartOperationTarget> PackageTargets { get; }

    internal IReadOnlyList<PackageChartEntry> LooseEntries { get; }

    /// <summary>この一回の推定に限って適用する候補の表示・順位方針です。</summary>
    internal InstallEstimationCandidatePolicy CandidatePolicy { get; }

    internal int SelectedRowCount => PackageTargets.Count + LooseEntries.Count;

    internal bool HasTargets => PackageTargets.Count > 0 || LooseEntries.Count > 0;

    /// <summary>パッケージ・単独譜面の対象と一操作の候補方針を固定します。既存入口は関連候補だけを扱います。</summary>
    internal static PendingInstallDestinationSearchRequest CreateInstallDestinationSearch(
        IEnumerable<ChartOperationTarget> targets,
        InstallEstimationCandidatePolicy candidatePolicy = InstallEstimationCandidatePolicy.RelatedOnly)
    {
        return Create(targets, candidatePolicy);
    }

    private static PendingInstallDestinationSearchRequest Create(IEnumerable<ChartOperationTarget> targets, InstallEstimationCandidatePolicy candidatePolicy)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }

        List<ChartOperationTarget> validTargets = [.. targets
            .Where(target => target?.Chart != null
                && target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination))];
        List<ChartOperationTarget> packageTargets = [.. validTargets.Where(target => target.PackageEntry != null)];
        List<PackageChartEntry> looseEntries = [.. validTargets
            .Where(target => target.PackageEntry == null)
            .Select(target => target.ToPackageChartEntry())
            .Where(entry => entry?.Chart != null)];

        return new PendingInstallDestinationSearchRequest(packageTargets, looseEntries, candidatePolicy);
    }
}
