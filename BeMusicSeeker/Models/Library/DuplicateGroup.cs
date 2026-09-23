using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models;

public class DuplicateGroup
{
    internal DuplicateGroup(List<ChartFile> chartFiles, List<string> folders)
    {
        ChartFiles = chartFiles ?? [];
        Folders = folders ?? [];
        Header = ChartFiles.FirstOrDefault()?.Title ?? string.Empty;
    }

    internal List<ChartFile> ChartFiles { get; set; }

    public List<string> Folders { get; set; }

    public string Header { get; set; }
}
