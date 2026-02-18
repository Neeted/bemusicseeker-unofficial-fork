using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using NLog;

namespace BeMusicSeeker.Models.Utils;

public class EverythingFileScanner : IBmsFileScanner
{
	private static readonly Logger logger = LogManager.GetLogger("InstallPerformance.EverythingScanner");

	private static readonly bool verifyEnabled = CommandLineSwitches.IsEverythingVerifyEnabled;

	public BmsScanExecutionResult Scan(IEnumerable<string> rootDirectories, IEnumerable<string> bmsExtensions, bool verboseLog = false)
	{
		List<string> roots = (rootDirectories ?? Enumerable.Empty<string>()).Where((string p) => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		string[] exts = (bmsExtensions ?? Enumerable.Empty<string>()).Where((string e) => !string.IsNullOrWhiteSpace(e)).Select((string e) => e.TrimStart('.')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		string[] extsWithDot = exts.Select((string e) => "." + e).ToArray();
		if (roots.Count == 0 || exts.Length == 0)
		{
			return new BmsScanExecutionResult
			{
				Success = true,
				Result = new BmsScanResult()
			};
		}
		if (!EverythingNative.EnsureLoaded(out var reason))
		{
			return new BmsScanExecutionResult
			{
				Success = false,
				ErrorReason = reason
			};
		}
		string bmsQuery = EverythingNative.BuildBmsFilesQuery(roots.ToArray(), exts);
		string siblingQuery = EverythingNative.BuildSiblingFilesQuery(roots.ToArray(), exts);
		if (verboseLog)
		{
			logger.Info("everything_scan start roots={0} ext={1} bmsQuery={2} siblingQuery={3}", roots.Count, string.Join(";", exts), bmsQuery, siblingQuery);
		}
		Stopwatch stopwatch = Stopwatch.StartNew();
		BmsScanExecutionResult result = EverythingNative.ExecuteScan(bmsQuery, siblingQuery, extsWithDot, verboseLog, verifyEnabled);
		if (!result.Success)
		{
			if (verboseLog)
			{
				logger.Info("everything_scan failed reason={0}", result.ErrorReason ?? "unknown");
			}
			return result;
		}
		if (result.Result.BmsFilePaths.Count == 0 && roots.Count > 0)
		{
			if (verboseLog)
			{
				logger.Info("everything_scan failed reason=empty_results_with_roots nativeBridgeUsed={0} nativeBridgeReason={1} nativeBridgeMs={2} bmsHits={3} siblingHits={4}", result.NativeBridgeUsed.ToString().ToLowerInvariant(), result.NativeBridgeReason ?? "none", result.NativeBridgeMs, result.BmsQueryHitCount, result.SiblingQueryHitCount);
			}
			return new BmsScanExecutionResult
			{
				Success = false,
				ErrorReason = "empty_results_with_roots:bridgeUsed=" + result.NativeBridgeUsed.ToString().ToLowerInvariant() + ":bridgeReason=" + (result.NativeBridgeReason ?? "none") + ":bridgeMs=" + result.NativeBridgeMs
			};
		}
		if (verboseLog)
		{
			logger.Info("everything_scan success bms={0} dirs={1} totalMs={2} nativeBridgeUsed={3} nativeBridgeMs={4} nativeBridgeReason={5} connectMs={6} bmsQueryMs={7} bmsSearchMs={8} bmsReadMs={9} bmsHits={10} siblingQueryMs={11} siblingSearchMs={12} siblingReadMs={13} siblingHits={14} buildResultMs={15} hashBuildMs={16} hashDirs={17} hashEntries={18}", result.Result.BmsFilePaths.Count, result.Result.FileNameHashesByDirectory.Count, stopwatch.ElapsedMilliseconds, result.NativeBridgeUsed.ToString().ToLowerInvariant(), result.NativeBridgeMs, result.NativeBridgeReason ?? "none", result.ConnectMs, result.BmsQueryMs, result.BmsSearchMs, result.BmsReadMs, result.BmsQueryHitCount, result.SiblingQueryMs, result.SiblingSearchMs, result.SiblingReadMs, result.SiblingQueryHitCount, result.BuildResultMs, result.HashBuildMs, result.HashDirCount, result.HashEntryCount);
		}
		return result;
	}
}
