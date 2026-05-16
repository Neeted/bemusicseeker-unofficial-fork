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
	private List<BMSFile> chartFiles;

	private readonly bool hasExplicitChartFiles;

	private readonly object installEstimationSnapshotLock = new object();

	private PackageChartDiscoverySnapshot packageChartDiscoverySnapshot;

	private PackageInstallSurfaceSnapshot packageInstallSurfaceSnapshot;

	internal PendingEstimateDeferredReason DeferredEstimateReason { get; set; }

	public List<PendingChartEntry> PendingCharts => (ChartFiles ?? new List<BMSFile>()).OfType<PendingChartEntry>().ToList();

	public List<BMSFile> ChartFiles
	{
		get
		{
			if (hasExplicitChartFiles)
			{
				return chartFiles ?? new List<BMSFile>();
			}
			return GetOrBuildPackageChartDiscoverySnapshot(out _).ChartFiles;
		}
	}

	public List<BMSFile> BMSFiles => ChartFiles;

	public BMSPackage()
	{
	}

	public BMSPackage(BMSFile bmsFile)
	{
		path = bmsFile.path;
		chartFiles = new List<BMSFile> { bmsFile };
		hasExplicitChartFiles = true;
	}

	public BMSPackage(IEnumerable<BMSFile> chartFiles)
	{
		this.chartFiles = chartFiles.ToList();
		hasExplicitChartFiles = true;
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
			packageChartDiscoverySnapshot = null;
			packageInstallSurfaceSnapshot = null;
		}
	}

	private PackageInstallSurfaceSnapshot GetOrBuildInstallEstimationSurfaceSnapshot(out bool cacheHit)
	{
		lock (installEstimationSnapshotLock)
		{
			if (packageInstallSurfaceSnapshot == null
				|| !string.Equals(packageInstallSurfaceSnapshot.SourcePath, path ?? string.Empty, StringComparison.OrdinalIgnoreCase))
			{
				packageInstallSurfaceSnapshot = PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(path);
				cacheHit = false;
				return packageInstallSurfaceSnapshot ?? PackageInstallSurfaceSnapshot.Empty;
			}
			cacheHit = true;
			return packageInstallSurfaceSnapshot;
		}
	}

	private PackageChartDiscoverySnapshot GetOrBuildPackageChartDiscoverySnapshot(out bool cacheHit)
	{
		lock (installEstimationSnapshotLock)
		{
			if (packageChartDiscoverySnapshot == null
				|| !string.Equals(packageChartDiscoverySnapshot.SourcePath, path ?? string.Empty, StringComparison.OrdinalIgnoreCase))
			{
				packageChartDiscoverySnapshot = PackageInstallEstimationSnapshotBuilder.BuildPackageChartDiscoverySnapshot(path);
				cacheHit = false;
				return packageChartDiscoverySnapshot ?? new PackageChartDiscoverySnapshot();
			}
			cacheHit = true;
			return packageChartDiscoverySnapshot;
		}
	}
}
