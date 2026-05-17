using System.Collections.Generic;

namespace BeMusicSeeker.Models.Utils;

public interface IBmsFileScanner
{
    BmsScanExecutionResult Scan(IEnumerable<string> rootDirectories, IEnumerable<string> bmsExtensions, bool verboseLog = false);
}
