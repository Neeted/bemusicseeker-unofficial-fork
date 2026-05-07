using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NLog;

namespace BeMusicSeeker.Models.Utils;

public class EverythingFileScanner : IBmsFileScanner
{
	private static readonly Logger logger = LogManager.GetLogger("InstallPerformance.EverythingScanner");

	public BmsScanExecutionResult Scan(IEnumerable<string> rootDirectories, IEnumerable<string> bmsExtensions, bool verboseLog = false)
	{
		List<string> roots = (rootDirectories ?? Enumerable.Empty<string>())
			.Where((string p) => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
			.Select(Path.GetFullPath)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (roots.Count == 0)
		{
			return new BmsScanExecutionResult
			{
				Success = true,
				Result = new BmsScanResult()
			};
		}

		string chartQuery = EverythingNative.BuildFilesQuery(roots.ToArray(), Array.ConvertAll(ChartDirectoryScanBuilder.ChartExtensions, (string ext) => ext.TrimStart('.')));
		string audioQuery = EverythingNative.BuildFilesQuery(roots.ToArray(), Array.ConvertAll(ChartDirectoryScanBuilder.AudioExtensions, (string ext) => ext.TrimStart('.')));
		string imageQuery = EverythingNative.BuildFilesQuery(roots.ToArray(), Array.ConvertAll(ChartDirectoryScanBuilder.ImageExtensions, (string ext) => ext.TrimStart('.')));
		string movieQuery = EverythingNative.BuildFilesQuery(roots.ToArray(), Array.ConvertAll(ChartDirectoryScanBuilder.MovieExtensions, (string ext) => ext.TrimStart('.')));

		if (verboseLog)
		{
			logger.Info("everything_scan start roots={0} chartQuery={1} audioQuery={2} imageQuery={3} movieQuery={4}", roots.Count, chartQuery, audioQuery, imageQuery, movieQuery);
		}

		Stopwatch stopwatch = Stopwatch.StartNew();
		BmsScanExecutionResult result = EverythingNative.ExecuteScan(chartQuery, audioQuery, imageQuery, movieQuery);
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
			return new BmsScanExecutionResult
			{
				Success = false,
				ErrorReason = "empty_results_with_roots:bridgeReason=" + (result.NativeBridgeReason ?? result.ErrorReason ?? "unknown") + ":bridgeMs=" + result.NativeBridgeMs
			};
		}
		if (verboseLog)
		{
			logger.Info("everything_scan success charts={0} dirs={1} totalMs={2} nativeBridgeUsed={3} nativeBridgeMs={4} nativeBridgeReason={5} managedDecodeMs={6} managedMaterializeMs={7} bridgeRawBufferBytes={8} hashDirs={9} categoryResourceKeyHashEntries={10} chartQueryHits={11} audioQueryHits={12} imageQueryHits={13} movieQueryHits={14} chartQueryMs={15} audioQueryMs={16} imageQueryMs={17} movieQueryMs={18} chartDirectoryCount={19} audioAssignedCount={20} imageAssignedCount={21} movieAssignedCount={22} audioResourceKeyHashCount={23} imageResourceKeyHashCount={24} movieResourceKeyHashCount={25} audioRelHashCount={26} imageRelHashCount={27} movieRelHashCount={28} audioResourceDirCount={29} imageResourceDirCount={30} movieResourceDirCount={31} ownerCacheHitCount={32} ownerCacheMissCount={33} relativePrefixCacheHitCount={34} relativePrefixCacheMissCount={35} audioGroupMs={36} audioAssignMs={37} audioMergeMs={38} imageGroupMs={39} imageAssignMs={40} imageMergeMs={41} movieGroupMs={42} movieAssignMs={43} movieMergeMs={44} assignMs={45} dedupeMs={46} packMs={47}",
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
				result.ChartDirectoryCount,
				result.AudioAssignedCount,
				result.ImageAssignedCount,
				result.MovieAssignedCount,
				result.AudioResourceKeyHashCount,
				result.ImageResourceKeyHashCount,
				result.MovieResourceKeyHashCount,
				result.AudioRelativeHashCount,
				result.ImageRelativeHashCount,
				result.MovieRelativeHashCount,
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
				result.PackMs);
		}
		return result;
	}
}
