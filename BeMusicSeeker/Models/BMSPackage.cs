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

	private readonly object installEstimationSnapshotLock = new object();

	private PackageInstallSurfaceSnapshot installEstimationSurfaceSnapshot;

	internal PendingEstimateDeferredReason DeferredEstimateReason { get; set; }

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
		bmsFiles = new List<BMSFile> { bmsFile };
	}

	public BMSPackage(IEnumerable<BMSFile> bmsFiles)
	{
		this.bmsFiles = bmsFiles.ToList();
	}

	internal PackageInstallEstimationSnapshot GetOrBuildInstallEstimationSnapshot(IEnumerable<BMSFile> targetFiles)
	{
		List<BMSFile> targetFileList = (targetFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null).ToList();
		PackageInstallSurfaceSnapshot installSurfaceSnapshot = GetOrBuildInstallEstimationSurfaceSnapshot();
		return PackageInstallEstimationSnapshotBuilder.Build(this, targetFileList, installSurfaceSnapshot);
	}

	internal void InvalidateInstallEstimationSnapshot()
	{
		lock (installEstimationSnapshotLock)
		{
			installEstimationSurfaceSnapshot = null;
		}
	}

	private PackageInstallSurfaceSnapshot GetOrBuildInstallEstimationSurfaceSnapshot()
	{
		lock (installEstimationSnapshotLock)
		{
			if (installEstimationSurfaceSnapshot == null
				|| !string.Equals(installEstimationSurfaceSnapshot.SourcePath, path ?? string.Empty, StringComparison.OrdinalIgnoreCase))
			{
				installEstimationSurfaceSnapshot = PackageInstallEstimationSnapshotBuilder.BuildInstallSurface(path);
			}
			return installEstimationSurfaceSnapshot;
		}
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
