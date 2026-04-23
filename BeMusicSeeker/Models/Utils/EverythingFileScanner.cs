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
			logger.Info("everything_scan success charts={0} dirs={1} totalMs={2} nativeBridgeUsed={3} nativeBridgeMs={4} nativeBridgeReason={5} bridgeContract={6} managedDecodeMs={7} managedMaterializeMs={8} bridgeRawBufferBytes={9} hashDirs={10} hashEntries={11} chartQueryHits={12} audioQueryHits={13} imageQueryHits={14} movieQueryHits={15} chartQueryMs={16} audioQueryMs={17} imageQueryMs={18} movieQueryMs={19} chartDirectoryCount={20} audioAssignedCount={21} imageAssignedCount={22} movieAssignedCount={23} allBaseHashCount={24} audioBaseHashCount={25} imageBaseHashCount={26} movieBaseHashCount={27} audioRelHashCount={28} imageRelHashCount={29} movieRelHashCount={30} audioResourceDirCount={31} imageResourceDirCount={32} movieResourceDirCount={33} ownerCacheHitCount={34} ownerCacheMissCount={35} relativePrefixCacheHitCount={36} relativePrefixCacheMissCount={37} audioGroupMs={38} audioAssignMs={39} audioMergeMs={40} imageGroupMs={41} imageAssignMs={42} imageMergeMs={43} movieGroupMs={44} movieAssignMs={45} movieMergeMs={46} assignMs={47} dedupeMs={48} packMs={49}",
				result.Result.ChartFilePaths.Count,
				result.Result.ChartDirectories.Count,
				stopwatch.ElapsedMilliseconds,
				result.NativeBridgeUsed.ToString().ToLowerInvariant(),
				result.NativeBridgeMs,
				result.NativeBridgeReason ?? "none",
				result.NativeBridgeContract ?? string.Empty,
				result.ManagedDecodeMs,
				result.ManagedMaterializeMs,
				result.BridgeRawBufferBytes,
				result.HashDirCount,
				result.HashEntryCount,
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
				result.AllBaseHashCount,
				result.AudioBaseHashCount,
				result.ImageBaseHashCount,
				result.MovieBaseHashCount,
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
