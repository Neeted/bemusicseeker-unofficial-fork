using System.Collections.Generic;
using System.IO;

namespace BeMusicSeeker;

public static class TempDirectoryPublisher
{
    private static readonly List<string> DirList = [];
    private static readonly object SyncRoot = new();

    public static string Get()
    {
        string text = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(text);
        lock (SyncRoot)
        {
            DirList.Add(text);
        }
        return text;
    }

    public static void RemoveAll()
    {
        List<string> dirs;
        lock (SyncRoot)
        {
            dirs = [.. DirList];
            DirList.Clear();
        }
        foreach (string dir in dirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
