using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

public class FastDirectoryFileScanner : IChartFileScanner
{
    public ChartScanExecutionResult Scan(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> chartExtensions,
        bool verboseLog = false,
        bool includeTextSurface = true,
        bool includeDirectorySurface = false)
    {
        try
        {
            List<string> roots = [.. (rootDirectories ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            RootFileEnumerationResult enumerationResult = new FastRootFileEnumerator().EnumerateFiles(
                roots,
                ChartDirectoryScanBuilder.CreateEnumerationGroups(
                    chartExtensions,
                    includeTextFiles: includeTextSurface,
                    includeDirectoryMetadata: includeDirectorySurface),
                verboseLog);
            if (!enumerationResult.Success)
            {
                return new ChartScanExecutionResult
                {
                    Success = false,
                    ErrorReason = enumerationResult.ErrorReason
                };
            }
            return ChartFileScannerResultBuilder.Build(enumerationResult);
        }
        catch (Exception ex)
        {
            return new ChartScanExecutionResult
            {
                Success = false,
                ErrorReason = ex.Message
            };
        }
    }
}
