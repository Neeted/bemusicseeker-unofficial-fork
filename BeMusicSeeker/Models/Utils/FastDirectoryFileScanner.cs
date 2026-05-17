using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

public class FastDirectoryFileScanner : IBmsFileScanner
{
    public BmsScanExecutionResult Scan(IEnumerable<string> rootDirectories, IEnumerable<string> bmsExtensions, bool verboseLog = false)
    {
        try
        {
            List<string> roots = (rootDirectories ?? Enumerable.Empty<string>())
                .Where((string p) => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            RootFileEnumerationResult enumerationResult = new FastRootFileEnumerator().EnumerateFiles(roots, ChartDirectoryScanBuilder.CreateDefaultEnumerationGroups(), verboseLog);
            if (!enumerationResult.Success)
            {
                return new BmsScanExecutionResult
                {
                    Success = false,
                    ErrorReason = enumerationResult.ErrorReason
                };
            }
            return BmsFileScannerResultBuilder.Build(enumerationResult);
        }
        catch (Exception ex)
        {
            return new BmsScanExecutionResult
            {
                Success = false,
                ErrorReason = ex.Message
            };
        }
    }
}
