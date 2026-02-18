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
			List<string> roots = (rootDirectories ?? Enumerable.Empty<string>()).Where((string p) => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			string[] ext = (bmsExtensions ?? Enumerable.Empty<string>()).Where((string e) => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			BMSDirectoryFileNameHash dirCache = new BMSDirectoryFileNameHash();
			HashSet<string> bmsPaths = new HashSet<string>(roots.AsParallel().SelectMany((string dir) => FastDirectoryEnumerator.GetFilePathsAsParallel(dir, dirCache, ext, SearchOption.AllDirectories)), StringComparer.OrdinalIgnoreCase);
			Dictionary<string, List<string>> filesByDirectory = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, uint[]> fileHashesByDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);
			foreach (string key in dirCache.Keys)
			{
				List<string> fileNames = FastDirectoryEnumerator.GetFileNames(key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
				filesByDirectory[key] = fileNames;
				fileHashesByDirectory[key] = BMSDirectoryFileNameHash.GetFileNameHashArray(fileNames);
			}
			return new BmsScanExecutionResult
			{
				Success = true,
				Result = new BmsScanResult
				{
					BmsFilePaths = bmsPaths,
					FilesByDirectory = filesByDirectory,
					FileNameHashesByDirectory = fileHashesByDirectory
				}
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
