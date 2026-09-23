using System;
using System.Collections.Generic;

namespace BeMusicSeeker.ViewModels;

internal static class PlaylistContextMenuTargetResolver
{
    internal static List<Uri> BuildPlaylistUrlTargets(IEnumerable<object> rows, bool isDiffUrl)
    {
        var targets = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (object row in rows ?? [])
        {
            Uri url = isDiffUrl ? GridRowResolver.GetUrlDiff(row) : GridRowResolver.GetUrl(row);
            if (url == null || !url.IsAbsoluteUri)
            {
                continue;
            }
            string key = url.ToString();
            if (seen.Add(key))
            {
                targets.Add(url);
            }
        }
        return targets;
    }

    internal static List<string> BuildPlaylistExternalPackageMd5Targets(IEnumerable<object> rows)
    {
        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (object row in rows ?? [])
        {
            string md5 = GridRowResolver.GetPlaylistExternalPackageLookupMd5(row);
            if (string.IsNullOrWhiteSpace(md5))
            {
                continue;
            }
            if (seen.Add(md5))
            {
                targets.Add(md5);
            }
        }
        return targets;
    }
}
