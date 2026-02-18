using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

public class BmsScanDiffReport
{
	public bool IsMatch { get; set; }

	public int BmsPathDiffCount { get; set; }

	public int DirectoryDiffCount { get; set; }

	public int FileDiffCount { get; set; }

	public List<string> Samples { get; set; } = new List<string>();
}

public static class BmsScanResultComparer
{
	public static BmsScanDiffReport Compare(BmsScanResult everything, BmsScanResult fast, int maxSamples = 10)
	{
		HashSet<string> ewBms = everything?.BmsFilePaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> fastBms = fast?.BmsFilePaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, HashSet<uint>> ewDirs = ToHashDirectoryMap(everything);
		Dictionary<string, HashSet<uint>> fastDirs = ToHashDirectoryMap(fast);
		BmsScanDiffReport report = new BmsScanDiffReport();
		int bmsDiff = ewBms.Except(fastBms, StringComparer.OrdinalIgnoreCase).Count() + fastBms.Except(ewBms, StringComparer.OrdinalIgnoreCase).Count();
		report.BmsPathDiffCount = bmsDiff;
		HashSet<string> allDirs = new HashSet<string>(ewDirs.Keys, StringComparer.OrdinalIgnoreCase);
		allDirs.UnionWith(fastDirs.Keys);
		foreach (string dir in allDirs)
		{
			bool hasEw = ewDirs.TryGetValue(dir, out var ewFiles);
			bool hasFast = fastDirs.TryGetValue(dir, out var fastFiles);
			if (!hasEw || !hasFast)
			{
				report.DirectoryDiffCount++;
				if (report.Samples.Count < maxSamples)
				{
					report.Samples.Add("dir=" + dir + " missing_in_everything=" + (!hasEw).ToString().ToLowerInvariant() + " missing_in_fast=" + (!hasFast).ToString().ToLowerInvariant());
				}
				continue;
			}
			List<uint> missingInEverything = fastFiles.Except(ewFiles).Take(5).ToList();
			List<uint> extraInEverything = ewFiles.Except(fastFiles).Take(5).ToList();
			int fileDiff = fastFiles.Except(ewFiles).Count() + ewFiles.Except(fastFiles).Count();
			if (fileDiff > 0)
			{
				report.FileDiffCount += fileDiff;
				if (report.Samples.Count < maxSamples)
				{
					report.Samples.Add("dir=" + dir + " missing_hashes_in_everything=[" + string.Join(",", missingInEverything.Select((uint h) => h.ToString("x8"))) + "] extra_hashes_in_everything=[" + string.Join(",", extraInEverything.Select((uint h) => h.ToString("x8"))) + "]");
				}
			}
		}
		report.IsMatch = report.BmsPathDiffCount == 0 && report.DirectoryDiffCount == 0 && report.FileDiffCount == 0;
		return report;
	}

	private static Dictionary<string, HashSet<uint>> ToHashDirectoryMap(BmsScanResult result)
	{
		Dictionary<string, HashSet<uint>> dictionary = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
		if (result?.FileNameHashesByDirectory != null && result.FileNameHashesByDirectory.Count > 0)
		{
			foreach (KeyValuePair<string, uint[]> item in result.FileNameHashesByDirectory)
			{
				dictionary[item.Key] = new HashSet<uint>(item.Value ?? Array.Empty<uint>());
			}
			return dictionary;
		}
		if (result?.FilesByDirectory == null || result.FilesByDirectory.Count == 0)
		{
			return dictionary;
		}
		foreach (KeyValuePair<string, List<string>> item2 in result.FilesByDirectory)
		{
			HashSet<uint> hashSet = new HashSet<uint>();
			if (item2.Value != null)
			{
				foreach (string item3 in item2.Value)
				{
					hashSet.Add(BMSDirectoryFileNameHash.GetFileNameHash(item3));
				}
			}
			dictionary[item2.Key] = hashSet;
		}
		return dictionary;
	}
}
