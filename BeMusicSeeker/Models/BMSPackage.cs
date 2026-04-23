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

	private readonly bool hasExplicitBmsFiles;

	private readonly object installEstimationSnapshotLock = new object();

	private PackageSourceScanSnapshot packageSourceScanSnapshot;

	internal PendingEstimateDeferredReason DeferredEstimateReason { get; set; }

	public List<PendingChartEntry> PendingCharts => (BMSFiles ?? new List<BMSFile>()).OfType<PendingChartEntry>().ToList();

	public List<BMSFile> BMSFiles
	{
		get
		{
			if (hasExplicitBmsFiles)
			{
				return bmsFiles ?? new List<BMSFile>();
			}
			return GetOrBuildPackageSourceScanSnapshot(out _).BmsFiles;
		}
	}

	public BMSPackage()
	{
	}

	public BMSPackage(BMSFile bmsFile)
	{
		path = bmsFile.path;
		bmsFiles = new List<BMSFile> { bmsFile };
		hasExplicitBmsFiles = true;
	}

	public BMSPackage(IEnumerable<BMSFile> bmsFiles)
	{
		this.bmsFiles = bmsFiles.ToList();
		hasExplicitBmsFiles = true;
	}

	internal PackageInstallEstimationSnapshot GetOrBuildInstallEstimationSnapshot(IEnumerable<BMSFile> targetFiles)
	{
		List<BMSFile> targetFileList = (targetFiles ?? Enumerable.Empty<BMSFile>()).Where((BMSFile file) => file != null).ToList();
		PackageInstallSurfaceSnapshot installSurfaceSnapshot = GetOrBuildInstallEstimationSurfaceSnapshot(out bool sourceSurfaceCacheHit);
		return PackageInstallEstimationSnapshotBuilder.Build(this, targetFileList, installSurfaceSnapshot, sourceSurfaceCacheHit);
	}

	internal void InvalidateInstallEstimationSnapshot()
	{
		lock (installEstimationSnapshotLock)
		{
			packageSourceScanSnapshot = null;
		}
	}

	private PackageInstallSurfaceSnapshot GetOrBuildInstallEstimationSurfaceSnapshot(out bool cacheHit)
	{
		return GetOrBuildPackageSourceScanSnapshot(out cacheHit).InstallSurfaceSnapshot;
	}

	private PackageSourceScanSnapshot GetOrBuildPackageSourceScanSnapshot(out bool cacheHit)
	{
		lock (installEstimationSnapshotLock)
		{
			if (packageSourceScanSnapshot == null
				|| !string.Equals(packageSourceScanSnapshot.SourcePath, path ?? string.Empty, StringComparison.OrdinalIgnoreCase))
			{
				packageSourceScanSnapshot = PackageInstallEstimationSnapshotBuilder.BuildPackageSourceScanSnapshot(path);
				cacheHit = false;
				return packageSourceScanSnapshot ?? new PackageSourceScanSnapshot();
			}
			cacheHit = true;
			return packageSourceScanSnapshot;
		}
	}
}
