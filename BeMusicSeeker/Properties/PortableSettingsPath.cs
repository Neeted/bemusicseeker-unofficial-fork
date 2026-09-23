using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Properties;

internal static class PortableSettingsPath
{
    public static string AppBaseDirectory => ApplicationPathPolicy.Current.BaseDirectory;

    public static string ConfigDirectoryPath => ApplicationPathPolicy.Current.ConfigDirectoryPath;

    public static string UserConfigPath => ApplicationPathPolicy.Current.UserConfigPath;

    public static string DataDirectoryPath => ApplicationPathPolicy.Current.DataDirectoryPath;

    public static string StandaloneSongDbPath => ApplicationPathPolicy.Current.StandaloneSongDbPath;
}
