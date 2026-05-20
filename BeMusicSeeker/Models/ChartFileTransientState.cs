using System.Collections.Generic;

namespace BeMusicSeeker.Models;

internal sealed class ChartFileTransientState
{
    internal static readonly ChartFileTransientState Empty = new();

    internal string Subtitle { get; private set; }

    internal string InstallDestination { get; private set; }

    internal string InstallDestinationTitle { get; private set; }

    internal string InstallDestinationArtist { get; private set; }

    internal IReadOnlyList<string> InstallDestinationSuggestions { get; private set; } = [];

    internal IReadOnlyList<ChartWarning> Warnings { get; private set; } = [];

    internal int? WAVHealth { get; private set; }

    internal int? BGAHealth { get; private set; }

    internal int? MovieHealth { get; private set; }

    internal bool? StagefileHealth { get; private set; }

    internal bool? BannerHealth { get; private set; }

    internal bool? BackbmpHealth { get; private set; }

    internal string EncodingName { get; private set; }

    internal bool HasState =>
        !string.IsNullOrWhiteSpace(Subtitle)
        || !string.IsNullOrWhiteSpace(InstallDestination)
        || !string.IsNullOrWhiteSpace(InstallDestinationTitle)
        || !string.IsNullOrWhiteSpace(InstallDestinationArtist)
        || (InstallDestinationSuggestions?.Count ?? 0) > 0
        || (Warnings?.Count ?? 0) > 0
        || WAVHealth.HasValue
        || BGAHealth.HasValue
        || MovieHealth.HasValue
        || StagefileHealth.HasValue
        || BannerHealth.HasValue
        || BackbmpHealth.HasValue
        || !string.IsNullOrWhiteSpace(EncodingName);

    internal static ChartFileTransientState FromCompatibilityFile(BMSFile file, bool includeWarningSnapshot = true)
    {
        if (file == null)
        {
            return Empty;
        }

        return new ChartFileTransientState
        {
            Subtitle = file.subtitle,
            InstallDestination = file.instl_dst,
            InstallDestinationTitle = file.InstallDestinationTitle,
            InstallDestinationArtist = file.InstallDestinationArtist,
            InstallDestinationSuggestions = file.InstallDestinationSuggestions,
            Warnings = includeWarningSnapshot ? file.Warnings.ToStructuredList() : [],
            WAVHealth = file.WAVHealth,
            BGAHealth = file.BGAHealth,
            MovieHealth = file.MovieHealth,
            StagefileHealth = file.StagefileHealth,
            BannerHealth = file.BannerHealth,
            BackbmpHealth = file.BackbmpHealth,
            EncodingName = file.encoding
        };
    }

    internal static ChartFileTransientState FromChartFile(ChartFile chart, bool includeWarningSnapshot = true)
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
            Warnings = includeWarningSnapshot ? chart.Warnings : [],
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
            Warnings = [],
            WAVHealth = WAVHealth,
            BGAHealth = BGAHealth,
            MovieHealth = MovieHealth,
            StagefileHealth = StagefileHealth,
            BannerHealth = BannerHealth,
            BackbmpHealth = BackbmpHealth,
            EncodingName = EncodingName
        };
    }
}
