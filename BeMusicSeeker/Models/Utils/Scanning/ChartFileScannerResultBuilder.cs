using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

internal static class ChartFileScannerResultBuilder
{
    internal static ChartScanExecutionResult Build(RootFileEnumerationResult enumerationResult)
    {
        if (!RootFileEnumerationService.IsAuthoritativeComplete(enumerationResult))
        {
            string reason = RootFileEnumerationService.GetNonAuthoritativeReason(enumerationResult);
            return new ChartScanExecutionResult
            {
                Success = false,
                IsComplete = false,
                ErrorReason = reason,
                IncompleteReason = reason
            };
        }

        var buildStopwatch = Stopwatch.StartNew();
        ChartScanResult scanResult = ChartDirectoryScanBuilder.BuildFromGroupedPaths(enumerationResult);
        buildStopwatch.Stop();
        long buildMs = buildStopwatch.ElapsedMilliseconds;
        bool everythingSource = string.Equals(enumerationResult?.BackendName, "everything_bridge", StringComparison.OrdinalIgnoreCase)
            || string.Equals(enumerationResult?.BackendName, EverythingNative.GroupedEnumerationBackendName, StringComparison.OrdinalIgnoreCase);
        return new ChartScanExecutionResult
        {
            ScanSource = everythingSource ? ChartScanSource.Everything : null,
            Success = true,
            NativeBridgeUsed = everythingSource,
            NativeBridgeMs = enumerationResult?.EnumerationMs ?? 0L,
            NativeBridgeReason = enumerationResult?.BackendName ?? "unknown",
            BuildResultMs = buildMs,
            HashBuildMs = buildMs,
            HashDirCount = (ulong)(scanResult.ChartDirectories?.Count ?? 0),
            CategoryResourceKeyHashEntryCount = CountHashEntries(scanResult.AudioRelativePathHashesByChartDirectory)
                + CountHashEntries(scanResult.ImageRelativePathHashesByChartDirectory)
                + CountHashEntries(scanResult.MovieRelativePathHashesByChartDirectory),
            ChartQueryHitCount = enumerationResult?.GetQueryHitCount(ChartDirectoryScanBuilder.ChartGroupName) ?? 0UL,
            AudioQueryHitCount = enumerationResult?.GetQueryHitCount(ChartDirectoryScanBuilder.AudioGroupName) ?? 0UL,
            ImageQueryHitCount = enumerationResult?.GetQueryHitCount(ChartDirectoryScanBuilder.ImageGroupName) ?? 0UL,
            MovieQueryHitCount = enumerationResult?.GetQueryHitCount(ChartDirectoryScanBuilder.MovieGroupName) ?? 0UL,
            TextQueryHitCount = enumerationResult?.GetQueryHitCount(ChartDirectoryScanBuilder.TextGroupName) ?? 0UL,
            DirectoryQueryHitCount = enumerationResult?.GetQueryHitCount(RootFileEnumerationService.DirectoriesGroupName) ?? 0UL,
            ChartQueryMs = enumerationResult?.GetQueryMs(ChartDirectoryScanBuilder.ChartGroupName) ?? 0L,
            AudioQueryMs = enumerationResult?.GetQueryMs(ChartDirectoryScanBuilder.AudioGroupName) ?? 0L,
            ImageQueryMs = enumerationResult?.GetQueryMs(ChartDirectoryScanBuilder.ImageGroupName) ?? 0L,
            MovieQueryMs = enumerationResult?.GetQueryMs(ChartDirectoryScanBuilder.MovieGroupName) ?? 0L,
            TextQueryMs = enumerationResult?.GetQueryMs(ChartDirectoryScanBuilder.TextGroupName) ?? 0L,
            DirectoryQueryMs = ResolveDirectoryQueryMs(enumerationResult),
            ChartDirectoryCount = (ulong)(scanResult.ChartDirectories?.Count ?? 0),
            AudioAssignedCount = (ulong)(enumerationResult?.GetPaths(ChartDirectoryScanBuilder.AudioGroupName)?.Count ?? 0),
            ImageAssignedCount = (ulong)(enumerationResult?.GetPaths(ChartDirectoryScanBuilder.ImageGroupName)?.Count ?? 0),
            MovieAssignedCount = (ulong)(enumerationResult?.GetPaths(ChartDirectoryScanBuilder.MovieGroupName)?.Count ?? 0),
            AudioResourceKeyHashCount = CountHashEntries(scanResult.AudioRelativePathHashesByChartDirectory),
            ImageResourceKeyHashCount = CountHashEntries(scanResult.ImageRelativePathHashesByChartDirectory),
            MovieResourceKeyHashCount = CountHashEntries(scanResult.MovieRelativePathHashesByChartDirectory),
            AudioResourceDirCount = (ulong)(scanResult.AudioRelativePathHashesByChartDirectory?.Count ?? 0),
            ImageResourceDirCount = (ulong)(scanResult.ImageRelativePathHashesByChartDirectory?.Count ?? 0),
            MovieResourceDirCount = (ulong)(scanResult.MovieRelativePathHashesByChartDirectory?.Count ?? 0),
            Result = scanResult,
            ResourceIndex = LibraryResourceIndex.CreateFromScanResult(scanResult)
        };
    }

    private static ulong CountHashEntries(Dictionary<string, uint[]> hashesByDirectory)
    {
        return (ulong)((hashesByDirectory ?? []).Values.Sum(hashes => hashes?.Length ?? 0));
    }

    private static long ResolveDirectoryQueryMs(RootFileEnumerationResult enumerationResult)
    {
        long queryMs = enumerationResult?.GetQueryMs(RootFileEnumerationService.DirectoriesGroupName) ?? 0L;
        if (queryMs > 0L)
        {
            return queryMs;
        }

        return (enumerationResult?.GetQueryHitCount(RootFileEnumerationService.DirectoriesGroupName) ?? 0UL) > 0UL
            ? enumerationResult?.EnumerationMs ?? 0L
            : 0L;
    }

}
