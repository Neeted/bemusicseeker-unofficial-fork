using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models;

public class DuplicateGroup(List<BMSFile> files, List<string> folders)
{
    public List<BMSFile> Files { get; set; } = files;
    public List<string> Folders { get; set; } = folders;
    public string Header { get; set; } = (files.Count > 1) ? files[0].title : files[0].Title;
}
