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
		Dictionary<string, List<string>> ewDirs = everything?.FilesByDirectory ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, List<string>> fastDirs = fast?.FilesByDirectory ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
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
			HashSet<string> ewSet = new HashSet<string>(ewFiles ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
			HashSet<string> fastSet = new HashSet<string>(fastFiles ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
			List<string> missingInEverything = fastSet.Except(ewSet, StringComparer.OrdinalIgnoreCase).Take(5).ToList();
			List<string> extraInEverything = ewSet.Except(fastSet, StringComparer.OrdinalIgnoreCase).Take(5).ToList();
			int fileDiff = missingInEverything.Count + extraInEverything.Count;
			if (fileDiff > 0)
			{
				report.FileDiffCount += fileDiff;
				if (report.Samples.Count < maxSamples)
				{
					report.Samples.Add("dir=" + dir + " missing_in_everything=[" + string.Join(",", missingInEverything) + "] extra_in_everything=[" + string.Join(",", extraInEverything) + "]");
				}
			}
		}
		report.IsMatch = report.BmsPathDiffCount == 0 && report.DirectoryDiffCount == 0 && report.FileDiffCount == 0;
		return report;
	}
}
