using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NLog;

namespace BeMusicSeeker.Models.Utils;

public class EverythingFileScanner : IChartFileScanner
{
    private static readonly Logger logger = LogManager.GetLogger("InstallPerformance.EverythingScanner");

    public ChartScanExecutionResult Scan(IEnumerable<string> rootDirectories, IEnumerable<string> chartExtensions, bool verboseLog = false)
    {
        List<string> roots = [.. (rootDirectories ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (roots.Count == 0)
        {
            return new ChartScanExecutionResult
            {
                Success = true,
                Result = new ChartScanResult()
            };
        }

        string chartQuery = EverythingNative.BuildFilesQuery([.. roots], Array.ConvertAll(ChartDirectoryScanBuilder.ResolveChartExtensions(chartExtensions), ext => ext.TrimStart('.')));
        string audioQuery = EverythingNative.BuildFilesQuery([.. roots], Array.ConvertAll(ChartDirectoryScanBuilder.AudioExtensions, ext => ext.TrimStart('.')));
        string imageQuery = EverythingNative.BuildFilesQuery([.. roots], Array.ConvertAll(ChartDirectoryScanBuilder.ImageExtensions, ext => ext.TrimStart('.')));
        string movieQuery = EverythingNative.BuildFilesQuery([.. roots], Array.ConvertAll(ChartDirectoryScanBuilder.MovieExtensions, ext => ext.TrimStart('.')));

        if (verboseLog)
        {
            logger.Info("everything_scan start roots={0} chartQuery={1} audioQuery={2} imageQuery={3} movieQuery={4}", roots.Count, chartQuery, audioQuery, imageQuery, movieQuery);
        }

        var stopwatch = Stopwatch.StartNew();
        ChartScanExecutionResult result = EverythingNative.ExecuteScan(chartQuery, audioQuery, imageQuery, movieQuery);
        stopwatch.Stop();
        if (!result.Success)
        {
            if (verboseLog)
            {
                logger.Info("everything_scan failed reason={0}", result.ErrorReason ?? "unknown");
            }
            return result;
        }
        if (result.Result == null || result.Result.ChartFilePaths.Count == 0)
        {
            if (verboseLog)
            {
                logger.Info("everything_scan failed reason=empty_results_with_roots nativeBridgeReason={0} nativeBridgeMs={1}",
                    result.NativeBridgeReason ?? result.ErrorReason ?? "unknown",
                    result.NativeBridgeMs);
            }
            return new ChartScanExecutionResult
            {
                Success = false,
                ErrorReason = "empty_results_with_roots:bridgeReason=" + (result.NativeBridgeReason ?? result.ErrorReason ?? "unknown") + ":bridgeMs=" + result.NativeBridgeMs
            };
        }
        PopulateTextFileSurface(result.Result, roots, verboseLog);
        if (verboseLog)
        {
            logger.Info("everything_scan success charts={0} dirs={1} totalMs={2} nativeBridgeUsed={3} nativeBridgeMs={4} nativeBridgeReason={5} managedDecodeMs={6} managedMaterializeMs={7} bridgeRawBufferBytes={8} hashDirs={9} categoryResourceKeyHashEntries={10} chartQueryHits={11} audioQueryHits={12} imageQueryHits={13} movieQueryHits={14} chartQueryMs={15} audioQueryMs={16} imageQueryMs={17} movieQueryMs={18} chartSearchMs={19} chartReadMs={20} audioSearchMs={21} audioReadMs={22} imageSearchMs={23} imageReadMs={24} movieSearchMs={25} movieReadMs={26} chartDirectoryCount={27} audioAssignedCount={28} imageAssignedCount={29} movieAssignedCount={30} audioResourceKeyHashCount={31} imageResourceKeyHashCount={32} movieResourceKeyHashCount={33} audioResourceDirCount={34} imageResourceDirCount={35} movieResourceDirCount={36} ownerCacheHitCount={37} ownerCacheMissCount={38} relativePrefixCacheHitCount={39} relativePrefixCacheMissCount={40} audioGroupMs={41} audioAssignMs={42} audioMergeMs={43} imageGroupMs={44} imageAssignMs={45} imageMergeMs={46} movieGroupMs={47} movieAssignMs={48} movieMergeMs={49} assignMs={50} dedupeMs={51} packMs={52} packReverseBuildMs={53} audioReverseBuildMs={54} imageReverseBuildMs={55} movieReverseBuildMs={56} packLayoutMs={57} packAllocMs={58} packWriteMs={59} reverseIndexBytes={60} chartSdkReadMs={61} chartCallbackMs={62} audioSdkReadMs={63} audioCallbackMs={64} imageSdkReadMs={65} imageCallbackMs={66} movieSdkReadMs={67} movieCallbackMs={68} chartPathResizeCount={69} chartNameResizeCount={70} audioPathResizeCount={71} audioNameResizeCount={72} imagePathResizeCount={73} imageNameResizeCount={74} moviePathResizeCount={75} movieNameResizeCount={76}",
                result.Result.ChartFilePaths.Count,
                result.Result.ChartDirectories.Count,
                stopwatch.ElapsedMilliseconds,
                result.NativeBridgeUsed.ToString().ToLowerInvariant(),
                result.NativeBridgeMs,
                result.NativeBridgeReason ?? "none",
                result.ManagedDecodeMs,
                result.ManagedMaterializeMs,
                result.BridgeRawBufferBytes,
                result.HashDirCount,
                result.CategoryResourceKeyHashEntryCount,
                result.ChartQueryHitCount,
                result.AudioQueryHitCount,
                result.ImageQueryHitCount,
                result.MovieQueryHitCount,
                result.ChartQueryMs,
                result.AudioQueryMs,
                result.ImageQueryMs,
                result.MovieQueryMs,
                result.ChartSearchMs,
                result.ChartReadMs,
                result.AudioSearchMs,
                result.AudioReadMs,
                result.ImageSearchMs,
                result.ImageReadMs,
                result.MovieSearchMs,
                result.MovieReadMs,
                result.ChartDirectoryCount,
                result.AudioAssignedCount,
                result.ImageAssignedCount,
                result.MovieAssignedCount,
                result.AudioResourceKeyHashCount,
                result.ImageResourceKeyHashCount,
                result.MovieResourceKeyHashCount,
                result.AudioResourceDirCount,
                result.ImageResourceDirCount,
                result.MovieResourceDirCount,
                result.OwnerCacheHitCount,
                result.OwnerCacheMissCount,
                result.RelativePrefixCacheHitCount,
                result.RelativePrefixCacheMissCount,
                result.AudioGroupMs,
                result.AudioAssignMs,
                result.AudioMergeMs,
                result.ImageGroupMs,
                result.ImageAssignMs,
                result.ImageMergeMs,
                result.MovieGroupMs,
                result.MovieAssignMs,
                result.MovieMergeMs,
                result.AssignMs,
                result.DedupeMs,
                result.PackMs,
                result.PackReverseBuildMs,
                result.AudioReverseBuildMs,
                result.ImageReverseBuildMs,
                result.MovieReverseBuildMs,
                result.PackLayoutMs,
                result.PackAllocMs,
                result.PackWriteMs,
                result.ReverseIndexBytes,
                result.ChartSdkReadMs,
                result.ChartCallbackMs,
                result.AudioSdkReadMs,
                result.AudioCallbackMs,
                result.ImageSdkReadMs,
                result.ImageCallbackMs,
                result.MovieSdkReadMs,
                result.MovieCallbackMs,
                result.ChartPathResizeCount,
                result.ChartNameResizeCount,
                result.AudioPathResizeCount,
                result.AudioNameResizeCount,
                result.ImagePathResizeCount,
                result.ImageNameResizeCount,
                result.MoviePathResizeCount,
                result.MovieNameResizeCount);
        }
        return result;
    }

    private static void PopulateTextFileSurface(ChartScanResult scanResult, IReadOnlyList<string> roots, bool verboseLog)
    {
        if (scanResult == null || scanResult.ChartDirectories.Count == 0 || roots == null || roots.Count == 0)
        {
            return;
        }
        RootFileEnumerationResult textEnumeration = new EverythingRootFileEnumerator().EnumerateFiles(
            roots,
            [new RootFileEnumerationGroup(ChartDirectoryScanBuilder.TextGroupName, ChartDirectoryScanBuilder.TextExtensions)],
            verboseLog);
        if (!textEnumeration.Success)
        {
            if (verboseLog)
            {
                logger.Info("everything_scan text_group_skipped reason={0}", textEnumeration.ErrorReason ?? "unknown");
            }
            return;
        }
        ChartDirectoryScanBuilder.AddDirectTextFileDirectories(
            scanResult,
            textEnumeration.GetPaths(ChartDirectoryScanBuilder.TextGroupName));
    }
}
