using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Ribbit.Cryptography;
using Ribbit.Threading;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models;

public class BMSDirectoryFileNameHash
{
	private ReaderWriterLockSlim rwlock = new ReaderWriterLockSlim();

	private Dictionary<string, uint[]> allFileList = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);

	internal static BMSDirectoryFileNameHash CreateFromHashedDirectories(IEnumerable<string> directories, IDictionary<string, uint[]> fileNameHashesByDirectory)
	{
		BMSDirectoryFileNameHash hashIndex = new BMSDirectoryFileNameHash();
		IEnumerable<string> sourceDirectories = directories ?? fileNameHashesByDirectory?.Keys ?? Enumerable.Empty<string>();
		foreach (string directory in sourceDirectories.Where((string directory) => !string.IsNullOrWhiteSpace(directory)).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			uint[] hashes = null;
			if (fileNameHashesByDirectory != null)
			{
				fileNameHashesByDirectory.TryGetValue(directory, out hashes);
			}
			hashIndex.allFileList[directory] = hashes ?? Array.Empty<uint>();
		}
		return hashIndex;
	}

	public List<string> Keys
	{
		get
		{
			using (new ReaderGuard(rwlock))
			{
				return allFileList.Keys.ToList();
			}
		}
	}

	public static uint[] GetFileNameHashArray(string path)
	{
		return GetFileNameHashArray(FastDirectoryEnumerator.GetFileNames(path));
	}

	public static uint[] GetFileNameHashArray(IEnumerable<string> list)
	{
		return list.Select(GetFileNameHash).ToArray();
	}

	public static uint GetFileNameHash(string fileName)
	{
		return GetLookupHash(ChartResourcePathNormalizer.NormalizeFileNameForLookup(fileName));
	}

	public static uint GetLookupHash(string normalizedLookupValue)
	{
		if (string.IsNullOrWhiteSpace(normalizedLookupValue))
		{
			return 0u;
		}
		return xxHash32.CalculateHash(normalizedLookupValue.ToUpperInvariant());
	}

	private static string extensionNormalizer(string str)
	{
		int length = str.Length;
		if (length > 3 && str[length - 4] == '.')
		{
			if ((str[length - 3] == 'O' && str[length - 2] == 'G' && str[length - 1] == 'G') || (str[length - 3] == 'M' && str[length - 2] == 'P' && str[length - 1] == '3'))
			{
				char[] array = str.ToArray();
				array[length - 3] = 'W';
				array[length - 2] = 'A';
				array[length - 1] = 'V';
				return new string(array);
			}
			if ((str[length - 3] == 'B' && str[length - 2] == 'M' && str[length - 1] == 'P') || (str[length - 3] == 'J' && str[length - 2] == 'P' && str[length - 1] == 'G'))
			{
				char[] array2 = str.ToArray();
				array2[length - 3] = 'P';
				array2[length - 2] = 'N';
				array2[length - 1] = 'G';
				return new string(array2);
			}
		}
		return str;
	}

	public uint[] GetFileNameHashArray(string path, bool forceReload = false)
	{
		if (!forceReload)
		{
			using (new ReaderGuard(rwlock))
			{
				if (allFileList.TryGetValue(path, out var value))
				{
					return value;
				}
			}
		}
		uint[] fileNameHashArray = GetFileNameHashArray(path);
		if (forceReload)
		{
			using (new WriterGuard(rwlock))
			{
				allFileList[path] = fileNameHashArray;
			}
		}
		return fileNameHashArray;
	}

	public uint[] TryGetCachedFileNameHashArray(string path)
	{
		using (new ReaderGuard(rwlock))
		{
			return allFileList.TryGetValue(path, out var value) ? value : null;
		}
	}

	public void AddDir(string path, IEnumerable<string> fileNames)
	{
		uint[] fileNameHashArray = GetFileNameHashArray(fileNames);
		using (new WriterGuard(rwlock))
		{
			allFileList[path] = fileNameHashArray;
		}
	}

	public void AddDirHashed(string path, uint[] fileNameHashes)
	{
		using (new WriterGuard(rwlock))
		{
			allFileList[path] = fileNameHashes ?? Array.Empty<uint>();
		}
	}

	public void AddDir(string path, bool update = false)
	{
		using (new ReaderGuard(rwlock))
		{
			if (!update && allFileList.Keys.Contains(path))
			{
				return;
			}
		}
		uint[] fileNameHashArray = GetFileNameHashArray(path);
		using (new WriterGuard(rwlock))
		{
			allFileList[path] = fileNameHashArray;
		}
	}

	public bool RemoveDir(string key)
	{
		if (key == null)
		{
			throw new ArgumentNullException("key");
		}
		using (new WriterGuard(rwlock))
		{
			return allFileList.Remove(key);
		}
	}

	public bool ReplaceDir(string key, string newKey)
	{
		if (key == null)
		{
			throw new ArgumentNullException("key");
		}
		if (newKey == null)
		{
			throw new ArgumentNullException("newKey");
		}
		using (new WriterGuard(rwlock))
		{
			uint[] array = allFileList.TryGetValue(key, null);
			if (array != null)
			{
				allFileList.Remove(key);
				allFileList[newKey] = array;
				return true;
			}
			return false;
		}
	}
}
