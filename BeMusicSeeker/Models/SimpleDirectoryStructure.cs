using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models;

public class SimpleDirectoryStructure(string dirName = null)
{
    public string DirName { get; private set; } = dirName;

    public HashSet<SimpleDirectoryStructure> DirList { get; private set; } = [];

    public HashSet<string> FileList { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public SimpleDirectoryStructure(List<string> paths, string dirName = null)
        : this(dirName)
    {
        foreach (IGrouping<string, string> item in paths.GroupBy(delegate (string path)
        {
            string text = ((dirName == null) ? path : path.Remove(0, dirName.Length + 1));
            int num = text.IndexOf(Path.DirectorySeparatorChar);
            if (num == -1)
            {
                return dirName;
            }
            string text2 = text.Substring(0, num);
            if (dirName != null)
            {
                text2 = dirName + Path.DirectorySeparatorChar + text2;
            }
            return text2;
        }, StringComparer.OrdinalIgnoreCase))
        {
            if (item.Key.Equals(dirName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (string item2 in item)
                {
                    FileList.Add(item2);
                }
            }
            else
            {
                DirList.Add(new SimpleDirectoryStructure([.. item], item.Key));
            }
        }
    }

    public IEnumerable<string> GetAllFiles()
    {
        return DirList.SelectMany(d => d.GetAllFiles()).Concat(FileList);
    }

    public override bool Equals(object a)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(DirName, a as SimpleDirectoryStructure);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(DirName);
    }

    public override string ToString()
    {
        return DirName;
    }
}
