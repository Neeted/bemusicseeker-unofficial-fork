using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
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
			list = list.OrderByDescending((FileInfo f) => f.LastWriteTimeUtc).ThenBy((FileInfo f) => f.FullName, StringComparer.Ordinal).ToList();
			NLogWrapper.TraceLogger?.Info("portable_settings_migration candidates count=" + list.Count);
			FileInfo fileInfo = null;
			foreach (FileInfo item in list)
			{
				if (TryValidateCandidate(item, out var reason))
				{
					fileInfo = item;
					break;
				}
				NLogWrapper.TraceLogger?.Info("portable_settings_migration candidate_skip source=" + item.FullName + " reason=" + reason);
			}
			if (fileInfo == null)
			{
				NLogWrapper.TraceLogger?.Info("portable_settings_migration skip reason=no_valid_legacy_file");
				return;
			}
			Directory.CreateDirectory(PortableSettingsPath.ConfigDirectoryPath);
			File.Copy(fileInfo.FullName, PortableSettingsPath.UserConfigPath, overwrite: false);
			NLogWrapper.TraceLogger?.Info("portable_settings_migration success source=" + fileInfo.FullName + " target=" + PortableSettingsPath.UserConfigPath + " sourceWriteUtc=" + fileInfo.LastWriteTimeUtc.ToString("o"));
		}
		catch (Exception ex)
		{
			NLogWrapper.TraceLogger?.Warn(ex, "portable_settings_migration failed");
		}
	}

	private static bool TryValidateCandidate(FileInfo file, out string reason)
	{
		reason = "unknown";
		try
		{
			XDocument xDocument = XDocument.Load(file.FullName, LoadOptions.None);
			XElement xElement = xDocument.Root?.Element("userSettings")?.Element("BeMusicSeeker.Properties.Settings");
			if (xElement == null)
			{
				reason = "missing_settings_section";
				return false;
			}
			if (IsDefaultEquivalent(xDocument, out reason))
			{
				return false;
			}
			reason = string.Empty;
			return true;
		}
		catch
		{
			reason = "invalid_xml";
			return false;
		}
	}

	private static bool IsDefaultEquivalent(XDocument doc, out string reason)
	{
		string settingValue = GetSettingValue(doc, "AssemblyVersion");
		if (string.IsNullOrWhiteSpace(settingValue))
		{
			reason = "default_equivalent:missing_AssemblyVersion";
			return true;
		}
		settingValue = GetSettingValue(doc, "TableListURL");
		if (string.IsNullOrWhiteSpace(settingValue))
		{
			reason = "default_equivalent:missing_TableListURL";
			return true;
		}
		settingValue = GetSettingValue(doc, "BMSInstallDir");
		if (string.IsNullOrWhiteSpace(settingValue))
		{
			reason = "default_equivalent:missing_BMSInstallDir";
			return true;
		}
		bool flag = string.Equals(GetSettingValue(doc, "OperationModeLR2DB"), "True", StringComparison.OrdinalIgnoreCase);
		if (flag)
		{
			string settingValue2 = GetSettingValue(doc, "LR2SongDBPath");
			if (string.IsNullOrWhiteSpace(settingValue2))
			{
				reason = "default_equivalent:missing_LR2SongDBPath";
				return true;
			}
			settingValue2 = GetSettingValue(doc, "LR2ConfigXmlPath");
			if (string.IsNullOrWhiteSpace(settingValue2))
			{
				reason = "default_equivalent:missing_LR2ConfigXmlPath";
				return true;
			}
		}
		else
		{
			string settingValue3 = GetSettingValue(doc, "BMSRootPath");
			if (string.IsNullOrWhiteSpace(settingValue3))
			{
				reason = "default_equivalent:missing_BMSRootPath";
				return true;
			}
		}
		reason = string.Empty;
		return false;
	}

	private static string GetSettingValue(XDocument doc, string name)
	{
		IEnumerable<XElement> source = doc.Root?.Element("userSettings")?.Element("BeMusicSeeker.Properties.Settings")?.Elements("setting");
		if (source == null)
		{
			return string.Empty;
		}
		XElement xElement = source.FirstOrDefault((XElement e) => string.Equals((string)e.Attribute("name"), name, StringComparison.Ordinal));
		if (xElement == null)
		{
			return string.Empty;
		}
		return xElement.Element("value")?.Value?.Trim() ?? string.Empty;
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
