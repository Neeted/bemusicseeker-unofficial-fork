using System;
using System.IO;

namespace BeMusicSeeker.Models.Utils;

internal static class DirectoryExt
{
    public static string GetDirectoryNameSimple(string f)
    {
        return f.Substring(0, Math.Max(f.IndexOf(Path.DirectorySeparatorChar) + 1, f.LastIndexOf(Path.DirectorySeparatorChar)));
    }
}
