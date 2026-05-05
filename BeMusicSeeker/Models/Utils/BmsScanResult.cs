using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

public class BmsScanResult
{
	public HashSet<string> ChartFilePaths { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public HashSet<string> ChartDirectories { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> AudioBaseNameHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> ImageBaseNameHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> MovieBaseNameHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> AudioRelativePathHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> ImageRelativePathHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> MovieRelativePathHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> SelfOwnedAudioBaseNameHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> SelfOwnedImageBaseNameHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> SelfOwnedMovieBaseNameHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> SelfOwnedAudioRelativePathHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> SelfOwnedImageRelativePathHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> SelfOwnedMovieRelativePathHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	internal Dictionary<string, uint[]> CreateResourceUnionHashesByChartDirectory(bool selfOwned)
	{
		Dictionary<string, uint[]> map = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);
		foreach (string chartDirectory in ChartDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			if (string.IsNullOrWhiteSpace(chartDirectory))
			{
				continue;
			}
			map[chartDirectory] = GetResourceUnionHashArray(chartDirectory, selfOwned);
		}
		return map;
	}

	internal uint[] GetResourceUnionHashArray(string chartDirectory, bool selfOwned)
	{
		if (string.IsNullOrWhiteSpace(chartDirectory))
		{
			return Array.Empty<uint>();
		}
		Dictionary<string, uint[]> audioMap = selfOwned ? SelfOwnedAudioBaseNameHashesByChartDirectory : AudioBaseNameHashesByChartDirectory;
		Dictionary<string, uint[]> imageMap = selfOwned ? SelfOwnedImageBaseNameHashesByChartDirectory : ImageBaseNameHashesByChartDirectory;
		Dictionary<string, uint[]> movieMap = selfOwned ? SelfOwnedMovieBaseNameHashesByChartDirectory : MovieBaseNameHashesByChartDirectory;
		return CreateSortedDistinctUnion(
			TryGetHashes(audioMap, chartDirectory),
			TryGetHashes(imageMap, chartDirectory),
			TryGetHashes(movieMap, chartDirectory));
	}

	internal static uint[] CreateSortedDistinctUnion(params uint[][] hashArrays)
	{
		if (hashArrays == null || hashArrays.Length == 0)
		{
			return Array.Empty<uint>();
		}
		uint[] single = null;
		int nonEmptyCount = 0;
		foreach (uint[] hashArray in hashArrays)
		{
			if (hashArray == null || hashArray.Length == 0)
			{
				continue;
			}
			single = hashArray;
			nonEmptyCount++;
		}
		if (nonEmptyCount == 0)
		{
			return Array.Empty<uint>();
		}
		if (nonEmptyCount == 1)
		{
			return single;
		}
		uint[] union = hashArrays
			.Where((uint[] hashArray) => hashArray != null && hashArray.Length > 0)
			.SelectMany((uint[] hashArray) => hashArray)
			.Distinct()
			.ToArray();
		Array.Sort(union);
		return union;
	}

	private static uint[] TryGetHashes(Dictionary<string, uint[]> hashesByDirectory, string chartDirectory)
	{
		if (hashesByDirectory != null && hashesByDirectory.TryGetValue(chartDirectory, out uint[] hashes))
		{
			return hashes ?? Array.Empty<uint>();
		}
		return Array.Empty<uint>();
	}
}
