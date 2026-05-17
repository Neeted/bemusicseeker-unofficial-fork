using System.Collections.Generic;

namespace BeMusicSeeker.Models.Utils;

public interface IChartFileScanner
{
    ChartScanExecutionResult Scan(IEnumerable<string> rootDirectories, IEnumerable<string> chartExtensions, bool verboseLog = false);
}
