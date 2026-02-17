using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

public class BMSPackage : LR2SongDBExtended.install
{
	private List<BMSFile> bmsFiles;

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

	private List<BMSFile> getBMSFiles()
	{
		if (Directory.Exists(path))
		{
			return (from file in FastDirectoryEnumerator.GetFilePathsAsParallel(path, null, BMSFile.bmsExtensions, SearchOption.AllDirectories)
				select BMSFile.CreateBMSFileFromFile(file)).ToList();
		}
		if (File.Exists(path) && BMSFile.bmsExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
		{
			return new List<BMSFile> { BMSFile.CreateBMSFileFromFile(path) };
		}
		return new List<BMSFile>();
	}
}
