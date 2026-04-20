using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

public class FastDirectoryFileScanner : IBmsFileScanner
{
	public BmsScanExecutionResult Scan(IEnumerable<string> rootDirectories, IEnumerable<string> bmsExtensions, bool verboseLog = false)
	{
		try
		{
			List<string> roots = (rootDirectories ?? Enumerable.Empty<string>())
				.Where((string p) => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
				.Select(Path.GetFullPath)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			return new BmsScanExecutionResult
			{
				Success = true,
				Result = ChartDirectoryScanBuilder.BuildFromRoots(roots)
			};
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
