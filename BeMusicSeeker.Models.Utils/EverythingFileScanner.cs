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
		BmsScanExecutionResult result = EverythingNative.ExecuteScan(bmsQuery, siblingQuery, extsWithDot, verboseLog);
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
				logger.Info("everything_scan failed reason=empty_results_with_roots");
			}
			return new BmsScanExecutionResult
			{
				Success = false,
				ErrorReason = "empty_results_with_roots"
			};
		}
		if (verboseLog)
		{
			logger.Info("everything_scan success bms={0} dirs={1} totalMs={2} connectMs={3} bmsQueryMs={4} bmsSearchMs={5} bmsReadMs={6} bmsHits={7} siblingQueryMs={8} siblingSearchMs={9} siblingReadMs={10} siblingHits={11} buildResultMs={12}", result.Result.BmsFilePaths.Count, result.Result.FilesByDirectory.Count, stopwatch.ElapsedMilliseconds, result.ConnectMs, result.BmsQueryMs, result.BmsSearchMs, result.BmsReadMs, result.BmsQueryHitCount, result.SiblingQueryMs, result.SiblingSearchMs, result.SiblingReadMs, result.SiblingQueryHitCount, result.BuildResultMs);
		}
		return result;
	}
}
