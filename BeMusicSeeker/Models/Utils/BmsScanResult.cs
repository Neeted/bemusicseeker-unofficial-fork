using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

public class BmsScanResult
{
	public HashSet<string> ChartFilePaths { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public HashSet<string> ChartDirectories { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> AllResourceBaseNameHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> AudioBaseNameHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> ImageBaseNameHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> MovieBaseNameHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> AudioRelativePathHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> ImageRelativePathHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, uint[]> MovieRelativePathHashesByChartDirectory { get; set; } = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	[Obsolete("Use ChartFilePaths")]
	public HashSet<string> BmsFilePaths
	{
		get => ChartFilePaths;
		set => ChartFilePaths = value ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	}

	[Obsolete("Use chart-directory keyed hash dictionaries")]
	public Dictionary<string, List<string>> FilesByDirectory
	{
		get => new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		set
		{
			foreach (KeyValuePair<string, List<string>> item in value ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase))
			{
				string chartDirectory = item.Key;
				ChartDirectories.Add(chartDirectory);
				HashSet<uint> allHashes = new HashSet<uint>();
				HashSet<uint> audioBaseHashes = new HashSet<uint>();
				HashSet<uint> imageBaseHashes = new HashSet<uint>();
				HashSet<uint> movieBaseHashes = new HashSet<uint>();
				HashSet<uint> audioRelativeHashes = new HashSet<uint>();
				HashSet<uint> imageRelativeHashes = new HashSet<uint>();
				HashSet<uint> movieRelativeHashes = new HashSet<uint>();
				foreach (string fileName in item.Value ?? new List<string>())
				{
					string normalizedFileName = BmsLibraryInternal.ChartResourcePathNormalizer.NormalizeFileNameForLookup(fileName);
					if (!string.IsNullOrWhiteSpace(normalizedFileName))
					{
						uint baseHash = Models.BMSDirectoryFileNameHash.GetLookupHash(normalizedFileName);
						allHashes.Add(baseHash);
						switch (BmsLibraryInternal.ChartResourcePathNormalizer.ClassifyPath(fileName))
						{
							case BmsLibraryInternal.ChartResourceKind.Audio:
								audioBaseHashes.Add(baseHash);
								break;
							case BmsLibraryInternal.ChartResourceKind.Image:
								imageBaseHashes.Add(baseHash);
								break;
							case BmsLibraryInternal.ChartResourceKind.Movie:
								movieBaseHashes.Add(baseHash);
								break;
						}
					}
					string normalizedRelativePath = BmsLibraryInternal.ChartResourcePathNormalizer.NormalizeReferencePathForLookup(fileName);
					if (!string.IsNullOrWhiteSpace(normalizedRelativePath))
					{
						uint relativeHash = Models.BMSDirectoryFileNameHash.GetLookupHash(normalizedRelativePath);
						switch (BmsLibraryInternal.ChartResourcePathNormalizer.ClassifyPath(fileName))
						{
							case BmsLibraryInternal.ChartResourceKind.Audio:
								audioRelativeHashes.Add(relativeHash);
								break;
							case BmsLibraryInternal.ChartResourceKind.Image:
								imageRelativeHashes.Add(relativeHash);
								break;
							case BmsLibraryInternal.ChartResourceKind.Movie:
								movieRelativeHashes.Add(relativeHash);
								break;
						}
					}
					if (ChartDirectoryScanBuilder.IsChartFile(fileName))
					{
						ChartFilePaths.Add(fileName);
					}
				}
				AllResourceBaseNameHashesByChartDirectory[chartDirectory] = allHashes.ToArray();
				AudioBaseNameHashesByChartDirectory[chartDirectory] = audioBaseHashes.ToArray();
				ImageBaseNameHashesByChartDirectory[chartDirectory] = imageBaseHashes.ToArray();
				MovieBaseNameHashesByChartDirectory[chartDirectory] = movieBaseHashes.ToArray();
				AudioRelativePathHashesByChartDirectory[chartDirectory] = audioRelativeHashes.ToArray();
				ImageRelativePathHashesByChartDirectory[chartDirectory] = imageRelativeHashes.ToArray();
				MovieRelativePathHashesByChartDirectory[chartDirectory] = movieRelativeHashes.ToArray();
			}
		}
	}

	[Obsolete("Use AllResourceBaseNameHashesByChartDirectory")]
	public Dictionary<string, uint[]> FileNameHashesByDirectory
	{
		get => AllResourceBaseNameHashesByChartDirectory;
		set
		{
			AllResourceBaseNameHashesByChartDirectory = value ?? new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);
			foreach (string chartDirectory in AllResourceBaseNameHashesByChartDirectory.Keys)
			{
				ChartDirectories.Add(chartDirectory);
			}
		}
	}
}
