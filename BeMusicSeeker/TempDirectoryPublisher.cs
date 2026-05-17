using System.Collections.Generic;
using System.IO;

namespace BeMusicSeeker;

public static class TempDirectoryPublisher
{
    private static readonly List<string> DirList = new List<string>();

    public static string Get()
    {
        string text = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(text);
        DirList.Add(text);
        return text;
    }

    public static void RemoveAll()
    {
        foreach (string dir in DirList)
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
        DirList.Clear();
    }
}
