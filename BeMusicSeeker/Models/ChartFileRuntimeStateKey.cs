using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models;

internal static class ChartFileRuntimeStateKey
{
    internal static string Create(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }

        return Create(chart.Kind, chart.Path, chart.Md5, chart.Sha256);
    }

    internal static string Create(BMSFile file)
    {
        return file == null ? null : Create(ChartFileKind.Bms, file.path, file.hash, file.sha256);
    }

    internal static string Create(LR2SongDBExtended.bmson_song song)
    {
        return song == null ? null : Create(ChartFileKind.Bmson, song.path, song.md5, song.sha256);
    }

    private static string Create(ChartFileKind kind, string path, string md5, string sha256)
    {
        string kindPrefix = kind.ToString().ToLowerInvariant() + ":";
        string lookupHash = CreatePrimaryLookupHash(md5, sha256);
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

    private static string CreatePrimaryLookupHash(string md5, string sha256)
    {
        if (!string.IsNullOrWhiteSpace(md5))
        {
            return md5.Trim();
        }
        return string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim();
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
