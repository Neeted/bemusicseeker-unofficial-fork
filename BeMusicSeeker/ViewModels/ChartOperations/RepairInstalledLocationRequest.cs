using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class RepairInstalledLocationRequest
{
    private readonly Lazy<IReadOnlyList<PackageChartEntry>> repairEntries;
    private readonly Lazy<IReadOnlyList<ChartFile>> repairCharts;

    private RepairInstalledLocationRequest(IReadOnlyList<ChartOperationTarget> targets)
    {
        Targets = targets ?? throw new ArgumentNullException(nameof(targets));
        Charts = [.. Targets.Select(target => target.Chart).Where(chart => chart != null)];
        repairEntries = new Lazy<IReadOnlyList<PackageChartEntry>>(
            () => [.. Targets
                .Select(target => target.ToPackageChartEntry())
                .Where(entry => entry?.Chart != null)]);
        repairCharts = new Lazy<IReadOnlyList<ChartFile>>(CreateRepairCharts);
    }

    internal IReadOnlyList<ChartOperationTarget> Targets { get; }

    internal IReadOnlyList<ChartFile> Charts { get; }

    internal IReadOnlyList<PackageChartEntry> RepairEntries => repairEntries.Value;

    internal IReadOnlyList<ChartFile> RepairCharts => repairCharts.Value;

    internal bool HasTargets => Charts.Count > 0;

    internal bool HasInstallDestination => RepairCharts.Any(chart => !string.IsNullOrWhiteSpace(chart.InstallDestination));

    internal static bool TryCreate(IEnumerable<ChartOperationTarget> targets, out RepairInstalledLocationRequest request)
    {
        request = null;
        if (targets == null)
        {
            return false;
        }

        List<ChartOperationTarget> validTargets = [.. targets
            .Where(target => target?.Chart != null
                && target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation))];
        if (validTargets.Count == 0)
        {
            return false;
        }

        request = new RepairInstalledLocationRequest(validTargets);
        return true;
    }

    internal void MaterializeRepairEntries()
    {
        _ = RepairEntries.Count;
    }

    private IReadOnlyList<ChartFile> CreateRepairCharts()
    {
        IReadOnlyList<PackageChartEntry> entries = RepairEntries;
        if (entries.Count == 0)
        {
            return Charts;
        }

        var entriesByChartKey = entries
            .Select(entry => new
            {
                Entry = entry,
                Key = CreateRepairChartKey(entry.Chart)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return [.. Charts.Select(chart =>
        {
            string key = CreateRepairChartKey(chart);
            if (string.IsNullOrWhiteSpace(key) || !entriesByChartKey.TryGetValue(key, out var entry))
            {
                return chart;
            }
            if (!HasInstallDestinationState(entry.Entry.Chart))
            {
                return chart;
            }

            return ChartFileProjection.WithPackageState(
                chart,
                entry.Entry.Chart.InstallDestination,
                entry.Entry.Chart.InstallDestinationTitle,
                entry.Entry.Chart.InstallDestinationArtist,
                entry.Entry.Chart.InstallDestinationSuggestions,
                chart.Warnings);
        })];
    }

    private static bool HasInstallDestinationState(ChartFile chart)
    {
        return chart != null
            && (!string.IsNullOrWhiteSpace(chart.InstallDestination)
                || !string.IsNullOrWhiteSpace(chart.InstallDestinationTitle)
                || !string.IsNullOrWhiteSpace(chart.InstallDestinationArtist)
                || (chart.InstallDestinationSuggestions?.Count ?? 0) > 0);
    }

    private static string CreateRepairChartKey(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(chart.Path))
        {
            return "path:" + chart.Path;
        }

        string hash = ChartLookupKey.GetPrimaryHash(chart);
        return string.IsNullOrWhiteSpace(hash) ? null : "hash:" + hash;
    }
}
