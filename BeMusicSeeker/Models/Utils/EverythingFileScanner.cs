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
		if (!EverythingNative.EnsureBridgeAvailable(out string reason))
		{
			return new BmsScanExecutionResult
			{
				Success = false,
				ErrorReason = reason
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
		if (!result.Success)
		{
			if (verboseLog)
			{
				logger.Info("everything_scan failed reason={0}", result.ErrorReason ?? "unknown");
			}
			return result;
		}
		if (result.Result.ChartFilePaths.Count == 0)
		{
			if (verboseLog)
			{
				logger.Info("everything_scan failed reason=empty_results_with_roots nativeBridgeUsed={0} nativeBridgeReason={1} nativeBridgeMs={2}", result.NativeBridgeUsed.ToString().ToLowerInvariant(), result.NativeBridgeReason ?? "none", result.NativeBridgeMs);
			}
			return new BmsScanExecutionResult
			{
				Success = false,
				ErrorReason = "empty_results_with_roots:bridgeUsed=" + result.NativeBridgeUsed.ToString().ToLowerInvariant() + ":bridgeReason=" + (result.NativeBridgeReason ?? "none") + ":bridgeMs=" + result.NativeBridgeMs
			};
		}
		if (verboseLog)
		{
			logger.Info("everything_scan success charts={0} dirs={1} totalMs={2} nativeBridgeUsed={3} nativeBridgeMs={4} nativeBridgeReason={5} hashDirs={6} hashEntries={7} chartQueryHits={8} audioQueryHits={9} imageQueryHits={10} movieQueryHits={11} chartQueryMs={12} audioQueryMs={13} imageQueryMs={14} movieQueryMs={15} chartDirectoryCount={16} audioAssignedCount={17} imageAssignedCount={18} movieAssignedCount={19} allBaseHashCount={20} audioBaseHashCount={21} imageBaseHashCount={22} movieBaseHashCount={23} audioRelHashCount={24} imageRelHashCount={25} movieRelHashCount={26} audioResourceDirCount={27} imageResourceDirCount={28} movieResourceDirCount={29} ownerCacheHitCount={30} ownerCacheMissCount={31} relativePrefixCacheHitCount={32} relativePrefixCacheMissCount={33} audioGroupMs={34} audioAssignMs={35} audioMergeMs={36} imageGroupMs={37} imageAssignMs={38} imageMergeMs={39} movieGroupMs={40} movieAssignMs={41} movieMergeMs={42} assignMs={43} dedupeMs={44} packMs={45}",
				result.Result.ChartFilePaths.Count,
				result.Result.ChartDirectories.Count,
				stopwatch.ElapsedMilliseconds,
				result.NativeBridgeUsed.ToString().ToLowerInvariant(),
				result.NativeBridgeMs,
				result.NativeBridgeReason ?? "none",
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
