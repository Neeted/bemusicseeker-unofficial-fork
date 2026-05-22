namespace BeMusicSeeker.Models;

internal static class ChartFileRuntimeStateKey
{
    internal static string Create(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }

        string kindPrefix = chart.Kind.ToString().ToLowerInvariant() + ":";
        string lookupHash = chart.PrimaryLookupHash;
        string path = chart.Path;
        if (!string.IsNullOrWhiteSpace(lookupHash) && !string.IsNullOrWhiteSpace(path))
        {
            return kindPrefix + "hash-path:" + lookupHash + "|" + path;
        }
        if (!string.IsNullOrWhiteSpace(lookupHash))
        {
            return kindPrefix + "hash:" + lookupHash;
        }
        return string.IsNullOrWhiteSpace(path) ? null : kindPrefix + "path:" + path;
    }

    internal static string CreatePathKey(ChartFile chart)
    {
        if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
        {
            return null;
        }

        return chart.Kind.ToString().ToLowerInvariant() + ":path:" + chart.Path;
    }
}
