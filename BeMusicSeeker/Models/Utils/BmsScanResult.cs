using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.Utils;

public class BmsScanResult
{
    public HashSet<string> ChartFilePaths { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> ChartDirectories { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, uint[]> AudioRelativePathHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, uint[]> ImageRelativePathHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, uint[]> MovieRelativePathHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, uint[]> SelfOwnedAudioRelativePathHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, uint[]> SelfOwnedImageRelativePathHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, uint[]> SelfOwnedMovieRelativePathHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

}
