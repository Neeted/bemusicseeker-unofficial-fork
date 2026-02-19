using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ribbit.Logging;

namespace BeMusicSeeker.Properties;

internal static class LegacyUserConfigMigrator
{
	public static void MigrateIfNeeded()
	{
		try
		{
			if (File.Exists(PortableSettingsPath.UserConfigPath))
			{
				return;
			}
			string legacyRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeMusicSeeker");
			if (!Directory.Exists(legacyRoot))
			{
				NLogWrapper.TraceLogger?.Info("portable_settings_migration skip reason=no_legacy_root");
				return;
			}
			List<FileInfo> list = (from path in Directory.EnumerateFiles(legacyRoot, "user.config", SearchOption.AllDirectories)
				where IsLegacyConfigPath(path)
				select new FileInfo(path)).ToList();
			if (list.Count == 0)
			{
				NLogWrapper.TraceLogger?.Info("portable_settings_migration skip reason=no_legacy_file");
				return;
			}
			FileInfo fileInfo = list.OrderByDescending((FileInfo f) => f.LastWriteTimeUtc).ThenBy((FileInfo f) => f.FullName, StringComparer.Ordinal).First();
			Directory.CreateDirectory(PortableSettingsPath.ConfigDirectoryPath);
			File.Copy(fileInfo.FullName, PortableSettingsPath.UserConfigPath, overwrite: false);
			NLogWrapper.TraceLogger?.Info("portable_settings_migration success source=" + fileInfo.FullName + " target=" + PortableSettingsPath.UserConfigPath + " sourceWriteUtc=" + fileInfo.LastWriteTimeUtc.ToString("o"));
		}
		catch (Exception ex)
		{
			NLogWrapper.TraceLogger?.Warn(ex, "portable_settings_migration failed");
		}
	}

	private static bool IsLegacyConfigPath(string path)
	{
		try
		{
			string directoryName = Path.GetDirectoryName(path) ?? string.Empty;
			DirectoryInfo directoryInfo = new DirectoryInfo(directoryName);
			DirectoryInfo directoryInfo2 = directoryInfo.Parent;
			if (directoryInfo2 == null || directoryInfo2.Parent == null)
			{
				return false;
			}
			return directoryInfo2.Name.StartsWith("BeMusicSeeker.exe_Url_", StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}
}
