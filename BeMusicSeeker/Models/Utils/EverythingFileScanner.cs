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

    public ChartScanExecutionResult Scan(IEnumerable<string> rootDirectories, IEnumerable<string> chartExtensions, bool verboseLog = false, bool includeTextSurface = true)
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
        string textQuery = includeTextSurface
            ? EverythingNative.BuildFilesQuery([.. roots], Array.ConvertAll(ChartDirectoryScanBuilder.TextExtensions, ext => ext.TrimStart('.')))
            : string.Empty;

        if (verboseLog)
        {
            logger.Info("everything_scan start roots={0} chartQuery={1} audioQuery={2} imageQuery={3} movieQuery={4} textQuery={5}", roots.Count, chartQuery, audioQuery, imageQuery, movieQuery, textQuery);
        }

        var stopwatch = Stopwatch.StartNew();
        ChartScanExecutionResult result = EverythingNative.ExecuteScan(chartQuery, audioQuery, imageQuery, movieQuery, textQuery);
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
        if (verboseLog)
        {
            logger.Info("everything_scan success charts={0} dirs={1} totalMs={2} nativeBridgeUsed={3} nativeBridgeMs={4} nativeBridgeReason={5} managedDecodeMs={6} managedMaterializeMs={7} bridgeRawBufferBytes={8} hashDirs={9} categoryResourceKeyHashEntries={10} chartQueryHits={11} audioQueryHits={12} imageQueryHits={13} movieQueryHits={14} textQueryHits={15} chartQueryMs={16} audioQueryMs={17} imageQueryMs={18} movieQueryMs={19} textQueryMs={20} chartSearchMs={21} chartReadMs={22} audioSearchMs={23} audioReadMs={24} imageSearchMs={25} imageReadMs={26} movieSearchMs={27} movieReadMs={28} textSearchMs={29} textReadMs={30} chartDirectoryCount={31} audioAssignedCount={32} imageAssignedCount={33} movieAssignedCount={34} textFileEntries={35} folderInfoHits={36} textFileDirs={37} audioResourceKeyHashCount={38} imageResourceKeyHashCount={39} movieResourceKeyHashCount={40} audioResourceDirCount={41} imageResourceDirCount={42} movieResourceDirCount={43} ownerCacheHitCount={44} ownerCacheMissCount={45} relativePrefixCacheHitCount={46} relativePrefixCacheMissCount={47} audioGroupMs={48} audioAssignMs={49} audioMergeMs={50} imageGroupMs={51} imageAssignMs={52} imageMergeMs={53} movieGroupMs={54} movieAssignMs={55} movieMergeMs={56} assignMs={57} dedupeMs={58} packMs={59} packReverseBuildMs={60} audioReverseBuildMs={61} imageReverseBuildMs={62} movieReverseBuildMs={63} packLayoutMs={64} packAllocMs={65} packWriteMs={66} reverseIndexBytes={67} chartSdkReadMs={68} chartCallbackMs={69} audioSdkReadMs={70} audioCallbackMs={71} imageSdkReadMs={72} imageCallbackMs={73} movieSdkReadMs={74} movieCallbackMs={75} textSdkReadMs={76} textCallbackMs={77} chartPathResizeCount={78} chartNameResizeCount={79} audioPathResizeCount={80} audioNameResizeCount={81} imagePathResizeCount={82} imageNameResizeCount={83} moviePathResizeCount={84} movieNameResizeCount={85} textPathResizeCount={86} textNameResizeCount={87}",
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
                result.TextQueryHitCount,
                result.ChartQueryMs,
                result.AudioQueryMs,
                result.ImageQueryMs,
                result.MovieQueryMs,
                result.TextQueryMs,
                result.ChartSearchMs,
                result.ChartReadMs,
                result.AudioSearchMs,
                result.AudioReadMs,
                result.ImageSearchMs,
                result.ImageReadMs,
                result.MovieSearchMs,
                result.MovieReadMs,
                result.TextSearchMs,
                result.TextReadMs,
                result.ChartDirectoryCount,
                result.AudioAssignedCount,
                result.ImageAssignedCount,
                result.MovieAssignedCount,
                result.Result.TextFileEntriesByPath.Count,
                result.Result.FolderInfoFilePaths.Count,
                result.Result.ChartDirectoriesWithTextFiles.Count,
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
                result.TextSdkReadMs,
                result.TextCallbackMs,
                result.ChartPathResizeCount,
                result.ChartNameResizeCount,
                result.AudioPathResizeCount,
                result.AudioNameResizeCount,
                result.ImagePathResizeCount,
                result.ImageNameResizeCount,
                result.MoviePathResizeCount,
                result.MovieNameResizeCount,
                result.TextPathResizeCount,
                result.TextNameResizeCount);
        }
        return result;
    }

}
