using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

public class BmsScanDiffReport
{
	public bool IsMatch { get; set; }

	public int ChartPathDiffCount { get; set; }

	public int ChartDirectoryDiffCount { get; set; }

	public int CategoryHashDiffCount { get; set; }

	public List<string> Samples { get; set; } = new List<string>();
}

public static class BmsScanResultComparer
{
	public static BmsScanDiffReport Compare(BmsScanResult everything, BmsScanResult fast, int maxSamples = 10)
	{
		HashSet<string> ewCharts = everything?.ChartFilePaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> fastCharts = fast?.ChartFilePaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> ewDirs = everything?.ChartDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> fastDirs = fast?.ChartDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		BmsScanDiffReport report = new BmsScanDiffReport
		{
			ChartPathDiffCount = ewCharts.Except(fastCharts, StringComparer.OrdinalIgnoreCase).Count()
				+ fastCharts.Except(ewCharts, StringComparer.OrdinalIgnoreCase).Count(),
			ChartDirectoryDiffCount = ewDirs.Except(fastDirs, StringComparer.OrdinalIgnoreCase).Count()
				+ fastDirs.Except(ewDirs, StringComparer.OrdinalIgnoreCase).Count()
		};
		CompareCategory(report, "all_base", everything?.AllResourceBaseNameHashesByChartDirectory, fast?.AllResourceBaseNameHashesByChartDirectory, maxSamples);
		CompareCategory(report, "audio_base", everything?.AudioBaseNameHashesByChartDirectory, fast?.AudioBaseNameHashesByChartDirectory, maxSamples);
		CompareCategory(report, "image_base", everything?.ImageBaseNameHashesByChartDirectory, fast?.ImageBaseNameHashesByChartDirectory, maxSamples);
		CompareCategory(report, "movie_base", everything?.MovieBaseNameHashesByChartDirectory, fast?.MovieBaseNameHashesByChartDirectory, maxSamples);
		CompareCategory(report, "audio_rel", everything?.AudioRelativePathHashesByChartDirectory, fast?.AudioRelativePathHashesByChartDirectory, maxSamples);
		CompareCategory(report, "image_rel", everything?.ImageRelativePathHashesByChartDirectory, fast?.ImageRelativePathHashesByChartDirectory, maxSamples);
		CompareCategory(report, "movie_rel", everything?.MovieRelativePathHashesByChartDirectory, fast?.MovieRelativePathHashesByChartDirectory, maxSamples);
		CompareCategory(report, "self_all_base", everything?.SelfOwnedAllResourceBaseNameHashesByChartDirectory, fast?.SelfOwnedAllResourceBaseNameHashesByChartDirectory, maxSamples);
		CompareCategory(report, "self_audio_base", everything?.SelfOwnedAudioBaseNameHashesByChartDirectory, fast?.SelfOwnedAudioBaseNameHashesByChartDirectory, maxSamples);
		CompareCategory(report, "self_image_base", everything?.SelfOwnedImageBaseNameHashesByChartDirectory, fast?.SelfOwnedImageBaseNameHashesByChartDirectory, maxSamples);
		CompareCategory(report, "self_movie_base", everything?.SelfOwnedMovieBaseNameHashesByChartDirectory, fast?.SelfOwnedMovieBaseNameHashesByChartDirectory, maxSamples);
		CompareCategory(report, "self_audio_rel", everything?.SelfOwnedAudioRelativePathHashesByChartDirectory, fast?.SelfOwnedAudioRelativePathHashesByChartDirectory, maxSamples);
		CompareCategory(report, "self_image_rel", everything?.SelfOwnedImageRelativePathHashesByChartDirectory, fast?.SelfOwnedImageRelativePathHashesByChartDirectory, maxSamples);
		CompareCategory(report, "self_movie_rel", everything?.SelfOwnedMovieRelativePathHashesByChartDirectory, fast?.SelfOwnedMovieRelativePathHashesByChartDirectory, maxSamples);
		report.IsMatch = report.ChartPathDiffCount == 0 && report.ChartDirectoryDiffCount == 0 && report.CategoryHashDiffCount == 0;
		return report;
	}

	private static void CompareCategory(BmsScanDiffReport report, string label, Dictionary<string, uint[]> everything, Dictionary<string, uint[]> fast, int maxSamples)
	{
		Dictionary<string, HashSet<uint>> ewMap = ToHashSets(everything);
		Dictionary<string, HashSet<uint>> fastMap = ToHashSets(fast);
		HashSet<string> allDirs = new HashSet<string>(ewMap.Keys, StringComparer.OrdinalIgnoreCase);
		allDirs.UnionWith(fastMap.Keys);
		foreach (string dir in allDirs.OrderBy((string item) => item, StringComparer.OrdinalIgnoreCase))
		{
			bool hasEw = ewMap.TryGetValue(dir, out HashSet<uint> ewHashes);
			bool hasFast = fastMap.TryGetValue(dir, out HashSet<uint> fastHashes);
			if (!hasEw || !hasFast)
			{
				report.CategoryHashDiffCount++;
				if (report.Samples.Count < maxSamples)
				{
					report.Samples.Add(label + " dir=" + dir + " missing_in_everything=" + (!hasEw).ToString().ToLowerInvariant() + " missing_in_fast=" + (!hasFast).ToString().ToLowerInvariant());
				}
				continue;
			}
			int diffCount = ewHashes.Except(fastHashes).Count() + fastHashes.Except(ewHashes).Count();
			if (diffCount <= 0)
			{
				continue;
			}
			report.CategoryHashDiffCount += diffCount;
			if (report.Samples.Count < maxSamples)
			{
				List<uint> missing = fastHashes.Except(ewHashes).Take(5).ToList();
				List<uint> extra = ewHashes.Except(fastHashes).Take(5).ToList();
				report.Samples.Add(label + " dir=" + dir + " missing=[" + string.Join(",", missing.Select((uint h) => h.ToString("x8"))) + "] extra=[" + string.Join(",", extra.Select((uint h) => h.ToString("x8"))) + "]");
			}
		}
	}

	private static Dictionary<string, HashSet<uint>> ToHashSets(Dictionary<string, uint[]> hashesByDirectory)
	{
		Dictionary<string, HashSet<uint>> dictionary = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
		foreach (KeyValuePair<string, uint[]> item in hashesByDirectory ?? new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase))
		{
			dictionary[item.Key] = new HashSet<uint>(item.Value ?? Array.Empty<uint>());
		}
		return dictionary;
	}
}
