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
}
