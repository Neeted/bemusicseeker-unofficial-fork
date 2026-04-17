using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

public class BMSPackage : LR2SongDBExtended.install
{
	private List<BMSFile> bmsFiles;

	public List<PendingChartEntry> PendingCharts => (BMSFiles ?? new List<BMSFile>()).OfType<PendingChartEntry>().ToList();

	public List<BMSFile> BMSFiles
	{
		get
		{
			if (bmsFiles == null)
			{
				bmsFiles = getBMSFiles();
			}
			return bmsFiles;
		}
	}

	public BMSPackage()
	{
	}

	public BMSPackage(BMSFile bmsFile)
	{
		path = bmsFile.path;
		bmsFiles = new List<BMSFile> { PendingChartEntry.CreateFromBmsFile(bmsFile) ?? bmsFile };
	}

	public BMSPackage(IEnumerable<BMSFile> bmsFiles)
	{
		this.bmsFiles = bmsFiles.ToList();
	}

	private List<BMSFile> getBMSFiles()
	{
		if (Directory.Exists(path))
		{
			return FastDirectoryEnumerator
				.GetFilePathsAsParallel(path, null, BMSFile.bmsExtensions.Concat(PendingChartEntry.bmsonExtensions).ToArray(), SearchOption.AllDirectories)
				.Select(CreatePendingChartFromPath)
				.Where((BMSFile file) => file != null)
				.ToList();
		}
		if (File.Exists(path) && PendingChartEntry.IsSupportedChartFilePath(path))
		{
			BMSFile file = CreatePendingChartFromPath(path);
			return (file != null) ? new List<BMSFile> { file } : new List<BMSFile>();
		}
		return new List<BMSFile>();
	}

	private static BMSFile CreatePendingChartFromPath(string filePath)
	{
		try
		{
			return PendingChartEntry.CreateFromFilePath(filePath);
		}
		catch
		{
			return null;
		}
	}
}
