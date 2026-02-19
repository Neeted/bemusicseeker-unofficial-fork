using System;
using System.IO;
using System.Reflection;

namespace BeMusicSeeker.Properties;

internal static class PortableSettingsPath
{
	public static string AppBaseDirectory => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory;

	public static string ConfigDirectoryPath => Path.Combine(AppBaseDirectory, "config");

	public static string UserConfigPath => Path.Combine(ConfigDirectoryPath, "user.config");
}
