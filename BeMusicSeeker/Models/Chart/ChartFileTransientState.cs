using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models;

internal sealed class ChartFileTransientState
{
    internal static readonly ChartFileTransientState Empty = new();

    internal string Subtitle { get; private set; }

    internal string InstallDestination { get; private set; }

    internal string InstallDestinationTitle { get; private set; }

    internal string InstallDestinationArtist { get; private set; }

    internal IReadOnlyList<string> InstallDestinationSuggestions { get; private set; } = [];

    internal bool HasInstallDestinationProjection { get; private set; }

    internal IReadOnlyList<ChartWarning> Warnings { get; private set; } = [];

    internal bool HasWarningProjection { get; private set; }

    internal bool HasInstallEstimationWarningProjection { get; private set; }

    internal int? WAVHealth { get; private set; }

    internal int? BGAHealth { get; private set; }

    internal int? MovieHealth { get; private set; }

    internal bool? StagefileHealth { get; private set; }

    internal bool? BannerHealth { get; private set; }

    internal bool? BackbmpHealth { get; private set; }

    internal string EncodingName { get; private set; }

    internal bool HasInstallDestinationState =>
        HasInstallDestinationProjection
        || !string.IsNullOrWhiteSpace(InstallDestination)
        || !string.IsNullOrWhiteSpace(InstallDestinationTitle)
        || !string.IsNullOrWhiteSpace(InstallDestinationArtist)
        || (InstallDestinationSuggestions?.Count ?? 0) > 0;

    internal bool HasState =>
        !string.IsNullOrWhiteSpace(Subtitle)
        || HasInstallDestinationState
        || HasWarningProjection
        || HasInstallEstimationWarningProjection
        || (Warnings?.Count ?? 0) > 0
        || WAVHealth.HasValue
        || BGAHealth.HasValue
        || MovieHealth.HasValue
        || StagefileHealth.HasValue
        || BannerHealth.HasValue
        || BackbmpHealth.HasValue
        || !string.IsNullOrWhiteSpace(EncodingName);

    internal static ChartFileTransientState FromChartFile(
        ChartFile chart,
        bool includeWarningSnapshot = true,
        bool forceInstallDestinationProjection = false,
        bool forceWarningProjection = false)
    {
        if (chart == null)
        {
            return Empty;
        }

        return new ChartFileTransientState
        {
            Subtitle = chart.Subtitle,
            InstallDestination = chart.InstallDestination,
            InstallDestinationTitle = chart.InstallDestinationTitle,
            InstallDestinationArtist = chart.InstallDestinationArtist,
            InstallDestinationSuggestions = chart.InstallDestinationSuggestions,
            HasInstallDestinationProjection = forceInstallDestinationProjection,
            Warnings = includeWarningSnapshot ? chart.Warnings : [],
            HasWarningProjection = forceWarningProjection || (includeWarningSnapshot && (chart.Warnings?.Count ?? 0) > 0),
            WAVHealth = chart.WAVHealth,
            BGAHealth = chart.BGAHealth,
            MovieHealth = chart.MovieHealth,
            StagefileHealth = chart.StagefileHealth,
            BannerHealth = chart.BannerHealth,
            BackbmpHealth = chart.BackbmpHealth,
            EncodingName = chart.EncodingName
        };
    }

    internal ChartFileTransientState WithoutWarnings()
    {
        return new ChartFileTransientState
        {
            Subtitle = Subtitle,
            InstallDestination = InstallDestination,
            InstallDestinationTitle = InstallDestinationTitle,
            InstallDestinationArtist = InstallDestinationArtist,
            InstallDestinationSuggestions = InstallDestinationSuggestions,
            HasInstallDestinationProjection = HasInstallDestinationProjection,
            Warnings = [],
            HasWarningProjection = false,
            HasInstallEstimationWarningProjection = false,
            WAVHealth = WAVHealth,
            BGAHealth = BGAHealth,
            MovieHealth = MovieHealth,
            StagefileHealth = StagefileHealth,
            BannerHealth = BannerHealth,
            BackbmpHealth = BackbmpHealth,
            EncodingName = EncodingName
        };
    }

    internal static ChartFileTransientState FromInstallDestinationState(
        ChartFile chart,
        bool includeWarningSnapshot = true,
        bool forceInstallDestinationProjection = false,
        bool forceWarningProjection = false)
    {
        if (chart == null)
        {
            return Empty;
        }

        return new ChartFileTransientState
        {
            InstallDestination = chart.InstallDestination,
            InstallDestinationTitle = chart.InstallDestinationTitle,
            InstallDestinationArtist = chart.InstallDestinationArtist,
            InstallDestinationSuggestions = chart.InstallDestinationSuggestions,
            HasInstallDestinationProjection = forceInstallDestinationProjection,
            Warnings = includeWarningSnapshot
                ? [.. (chart.Warnings ?? []).Where(warning => warning?.Category == ChartWarningCategory.InstallEstimation)]
                : [],
            HasInstallEstimationWarningProjection = forceWarningProjection
                || (includeWarningSnapshot && (chart.Warnings ?? []).Any(warning => warning?.Category == ChartWarningCategory.InstallEstimation))
        };
    }

    internal static ChartFileTransientState FromResourceHealthMaintenanceInfo(BMSFileMaintenanceInfo maintenanceInfo)
    {
        if (maintenanceInfo == null)
        {
            return Empty;
        }

        return new ChartFileTransientState
        {
            WAVHealth = maintenanceInfo.WAVHealth,
            BGAHealth = maintenanceInfo.BGAHealth,
            MovieHealth = maintenanceInfo.MovieHealth,
            StagefileHealth = maintenanceInfo.StagefileHealth,
            BannerHealth = maintenanceInfo.BannerHealth,
            BackbmpHealth = maintenanceInfo.BackbmpHealth,
            EncodingName = maintenanceInfo.encoding
        };
    }

    internal static ChartFileTransientState FromResourceHealthChart(ChartFile chart)
    {
        if (chart == null)
        {
            return Empty;
        }

        return new ChartFileTransientState
        {
            WAVHealth = chart.WAVHealth,
            BGAHealth = chart.BGAHealth,
            MovieHealth = chart.MovieHealth,
            StagefileHealth = chart.StagefileHealth,
            BannerHealth = chart.BannerHealth,
            BackbmpHealth = chart.BackbmpHealth,
            EncodingName = chart.EncodingName
        };
    }
}
