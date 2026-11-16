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

    /// <summary>検索時の所属・固有警告を維持し、一覧の共通値だけを同tokenの捕捉済み現在値へ置換します。</summary>
    internal static ChartFile ProjectCurrentChart(ChartFile membership, ChartFile current)
        => current with
        {
            Warnings = [.. current.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart),
                .. membership.Warnings.Where(warning => warning.Kind == ChartWarningKind.DuplicateChart)]
        };

    public List<string> Folders { get; set; }

    public string Header { get; set; }
}
