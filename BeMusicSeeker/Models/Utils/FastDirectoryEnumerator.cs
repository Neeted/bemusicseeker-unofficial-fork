using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Permissions;
using System.Windows;
using Microsoft.Win32.SafeHandles;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.Utils;

public static class FastDirectoryEnumerator
{
	private class FileEnumerable : IEnumerable<FileData>, IEnumerable
	{
		private readonly string m_path;

		private readonly string m_filter;

		private readonly SearchOption m_searchOption;

		public FileEnumerable(string path, string filter, SearchOption searchOption)
		{
			m_path = path;
			m_filter = filter;
			m_searchOption = searchOption;
		}

		public IEnumerator<FileData> GetEnumerator()
		{
			return new FileEnumerator(m_path, m_filter, m_searchOption);
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return new FileEnumerator(m_path, m_filter, m_searchOption);
		}
	}

	private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
	{
		[DllImport("kernel32.dll")]
		[ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
		private static extern bool FindClose(IntPtr handle);

		[SecurityPermission(SecurityAction.LinkDemand, UnmanagedCode = true)]
		internal SafeFindHandle()
			: base(ownsHandle: true)
		{
		}

		protected override bool ReleaseHandle()
		{
			return FindClose(handle);
		}
	}

	[SuppressUnmanagedCodeSecurity]
	private class FileEnumerator : IEnumerator<FileData>, IDisposable, IEnumerator
	{
		private class SearchContext
		{
			public readonly string Path;

			public Stack<string> SubdirectoriesToProcess;

			public SearchContext(string path)
			{
				Path = path;
			}
		}

		private string m_path;

		private string m_filter;

		private SearchOption m_searchOption;

		private Stack<SearchContext> m_contextStack;

		private SearchContext m_currentContext;

		private SafeFindHandle m_hndFindFile;

		private WIN32_FIND_DATA m_win_find_data = new WIN32_FIND_DATA();

		private List<string> dirCache = new List<string>();

		public FileData Current => new FileData(m_path, m_win_find_data);

		object IEnumerator.Current => new FileData(m_path, m_win_find_data);

		public FileEnumerator(string path, string filter, SearchOption searchOption)
		{
			m_path = path;
			m_filter = filter;
			m_searchOption = searchOption;
			m_currentContext = new SearchContext(path);
			if (m_searchOption == SearchOption.AllDirectories)
			{
				m_contextStack = new Stack<SearchContext>();
			}
		}

		public void Dispose()
		{
			if (m_hndFindFile != null)
			{
				m_hndFindFile.Dispose();
			}
		}

		public bool MoveNext()
		{
			bool flag = false;
			if (m_currentContext.SubdirectoriesToProcess == null)
			{
				if (m_hndFindFile == null)
				{
					new FileIOPermission(FileIOPermissionAccess.PathDiscovery, m_path).Demand();
					string fileName = Path.Combine(m_path, m_filter);
					m_hndFindFile = FindFirstFile(fileName, m_win_find_data);
					flag = !m_hndFindFile.IsInvalid;
				}
				else
				{
					flag = FindNextFile(m_hndFindFile, m_win_find_data);
				}
			}
			if (flag)
			{
				if ((m_win_find_data.dwFileAttributes & FileAttributes.Directory) == FileAttributes.Directory)
				{
					if (m_win_find_data.cFileName != "." && m_win_find_data.cFileName != "..")
					{
						dirCache.Add(Path.Combine(m_path, m_win_find_data.cFileName));
					}
					return MoveNext();
				}
			}
			else if (m_searchOption == SearchOption.AllDirectories)
			{
				if (m_currentContext.SubdirectoriesToProcess == null)
				{
					string[] collection = dirCache.ToArray();
					dirCache = new List<string>();
					m_currentContext.SubdirectoriesToProcess = new Stack<string>(collection);
				}
				if (m_currentContext.SubdirectoriesToProcess.Count > 0)
				{
					string path = m_currentContext.SubdirectoriesToProcess.Pop();
					m_contextStack.Push(m_currentContext);
					m_path = path;
					m_hndFindFile = null;
					m_currentContext = new SearchContext(m_path);
					return MoveNext();
				}
				if (m_contextStack.Count > 0)
				{
					m_currentContext = m_contextStack.Pop();
					m_path = m_currentContext.Path;
					if (m_hndFindFile != null)
					{
						m_hndFindFile.Close();
						m_hndFindFile = null;
					}
					return MoveNext();
				}
			}
			return flag;
		}

		public void Reset()
		{
			m_hndFindFile = null;
		}
	}

	private enum FINDEX_INFO_LEVELS
	{
		FindExInfoStandard,
		FindExInfoBasic
	}

	private enum FINDEX_SEARCH_OPS
	{
		FindExSearchNameMatch,
		FindExSearchLimitToDirectories,
		FindExSearchLimitToDevices
	}

	private const int FIND_FIRST_EX_CASE_SENSITIVE = 1;

	private const int FIND_FIRST_EX_LARGE_FETCH = 2;

	public static IEnumerable<FileData> EnumerateFiles(string path)
	{
		return EnumerateFiles(path, "*");
	}

	public static IEnumerable<FileData> EnumerateFiles(string path, string searchPattern)
	{
		return EnumerateFiles(path, searchPattern, SearchOption.TopDirectoryOnly);
	}

	public static IEnumerable<FileData> EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
	{
		if (path == null)
		{
			throw new ArgumentNullException("path");
		}
		if (searchPattern == null)
		{
			throw new ArgumentNullException("searchPattern");
		}
		if (searchOption != SearchOption.TopDirectoryOnly && searchOption != SearchOption.AllDirectories)
		{
			throw new ArgumentOutOfRangeException("searchOption");
		}
		return new FileEnumerable(Path.GetFullPath(path), searchPattern, searchOption);
	}

	public static FileData[] GetFiles(string path, string searchPattern, SearchOption searchOption)
	{
		List<FileData> list = new List<FileData>(EnumerateFiles(path, searchPattern, searchOption));
		FileData[] array = new FileData[list.Count];
		list.CopyTo(array);
		return array;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	private static extern SafeFindHandle FindFirstFile(string fileName, [In][Out] WIN32_FIND_DATA data);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFindHandle FindFirstFileEx(string lpFileName, FINDEX_INFO_LEVELS fInfoLevelId, [In][Out] WIN32_FIND_DATA lpFindFileData, FINDEX_SEARCH_OPS fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

	[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	private static extern bool FindNextFile(SafeFindHandle hndFindFile, [In][Out][MarshalAs(UnmanagedType.LPStruct)] WIN32_FIND_DATA lpFindFileData);

	[SuppressUnmanagedCodeSecurity]
	public static IEnumerable<string> GetFileNames(string dirPath, string searchPattern = "*")
	{
		FINDEX_INFO_LEVELS fInfoLevelId = (Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsHomeServer2011) ? FINDEX_INFO_LEVELS.FindExInfoBasic : FINDEX_INFO_LEVELS.FindExInfoStandard);
		int dwAdditionalFlags = (Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsHomeServer2011) ? 2 : 0);
		try
		{
			new FileIOPermission(FileIOPermissionAccess.PathDiscovery, dirPath).Demand();
		}
		catch (Exception ex)
		{
			DispatcherMessageBox.Show("ファイルリスト取得中に下記のエラーが発生したためスキップされました。" + Environment.NewLine + "ディレクトリへのアクセスが可能か確認して下さい。" + Environment.NewLine + Environment.NewLine + "対象:" + Environment.NewLine + dirPath + Environment.NewLine + ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
			yield break;
		}
		string lpFileName = Path.Combine(dirPath, searchPattern);
		WIN32_FIND_DATA wIN32_FIND_DATA = new WIN32_FIND_DATA();
		SafeFindHandle m_hndFindFile = FindFirstFileEx(lpFileName, fInfoLevelId, wIN32_FIND_DATA, FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero, dwAdditionalFlags);
		if (m_hndFindFile.IsInvalid)
		{
			yield break;
		}
		do
		{
			if ((wIN32_FIND_DATA.dwFileAttributes & FileAttributes.Directory) != FileAttributes.Directory)
			{
				yield return wIN32_FIND_DATA.cFileName;
			}
		}
		while (FindNextFile(m_hndFindFile, wIN32_FIND_DATA = new WIN32_FIND_DATA()));
	}

	[SuppressUnmanagedCodeSecurity]
	public static IEnumerable<string> GetFilePathsAsParallel(string dirPath, string[] extensions = null, SearchOption searchOption = SearchOption.TopDirectoryOnly)
	{
		FINDEX_INFO_LEVELS fInfoLevelId = (Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsHomeServer2011) ? FINDEX_INFO_LEVELS.FindExInfoBasic : FINDEX_INFO_LEVELS.FindExInfoStandard);
		int dwAdditionalFlags = (Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsHomeServer2011) ? 2 : 0);
		try
		{
			new FileIOPermission(FileIOPermissionAccess.PathDiscovery, dirPath).Demand();
		}
		catch (Exception ex)
		{
			DispatcherMessageBox.Show("ファイルリスト取得中に下記のエラーが発生したためスキップされました。" + Environment.NewLine + "ディレクトリへのアクセスが可能か確認して下さい。" + Environment.NewLine + Environment.NewLine + "対象:" + Environment.NewLine + dirPath + Environment.NewLine + ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
			yield break;
		}
		WIN32_FIND_DATA m_win_find_data = new WIN32_FIND_DATA();
		SafeFindHandle m_hndFindFile = FindFirstFileEx(dirPath + "\\*", fInfoLevelId, m_win_find_data, FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero, dwAdditionalFlags);
		if (m_hndFindFile.IsInvalid)
		{
			yield break;
		}
		List<string> subdirs = new List<string>();
		do
		{
			string text = dirPath + "\\" + m_win_find_data.cFileName;
			if ((m_win_find_data.dwFileAttributes & FileAttributes.Directory) == FileAttributes.Directory)
			{
				if (m_win_find_data.cFileName != "." && m_win_find_data.cFileName != "..")
				{
					subdirs.Add(text);
				}
				continue;
			}
			if (extensions == null || extensions.Any((string e) => m_win_find_data.cFileName.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
			{
				yield return text;
			}
		}
		while (FindNextFile(m_hndFindFile, m_win_find_data));
		if (subdirs.Count <= 0 || searchOption != SearchOption.AllDirectories)
		{
			yield break;
		}
		foreach (string item in subdirs.AsParallel().SelectMany((string path) => GetFilePathsAsParallel(path, extensions, searchOption)))
		{
			yield return item;
		}
	}
}
