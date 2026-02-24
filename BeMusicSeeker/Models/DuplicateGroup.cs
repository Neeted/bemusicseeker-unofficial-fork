using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models;

public class DuplicateGroup
{
    public List<BMSFile> Files { get; set; }
    public List<string> Folders { get; set; }
    public string Header { get; set; }

    public DuplicateGroup(List<BMSFile> files, List<string> folders)
    {
        Files = files;
        Folders = folders;
        Header = (files.Count > 1) ? files[0].title : files[0].Title;
    }
}
