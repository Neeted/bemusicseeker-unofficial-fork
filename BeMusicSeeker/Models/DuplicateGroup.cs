using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models;

public class DuplicateGroup(List<BMSFile> files, List<string> folders)
{
    public List<BMSFile> Files { get; set; } = files;
    internal List<ChartFile> ChartFiles { get; set; } = [.. (files ?? []).Select(file => ChartFileProjection.FromBmsFile(file)).Where(chart => chart != null)];
    public List<string> Folders { get; set; } = folders;
    public string Header { get; set; } = (files.Count > 1) ? files[0].title : files[0].Title;

    internal DuplicateGroup(List<ChartFile> chartFiles, List<BMSFile> files, List<string> folders)
        : this(files, folders)
    {
        ChartFiles = chartFiles ?? [];
        Header = ChartFiles.Count > 1 ? ChartFiles[0].Title : ChartFiles.FirstOrDefault()?.Title ?? Header;
    }
}
