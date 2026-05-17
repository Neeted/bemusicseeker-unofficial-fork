using System;

namespace Ribbit.Util.Extensions;

public static class OperatingSystemExt
{
    public enum WindowsProductName
    {
        WindowsNT3_1 = 310,
        WindowsNT3_5 = 350,
        WindowsNT3_51 = 351,
        Windows95 = 400,
        WindowsNT4_0 = 400,
        Windows98 = 410,
        Windows98Sec = 410,
        WindowsMe = 490,
        Windows2000 = 500,
        WindowsXP = 510,
        WindowsHomeServer = 520,
        WindowsServer2003 = 520,
        WindowsServer2003R2 = 520,
        WindowsXP64 = 520,
        WindowsServer2008 = 600,
        WindowsVista = 600,
        WindowsHomeServer2011 = 610,
        WindowsServer2008R2 = 610,
        Windows7 = 610,
        WindowsServer2012 = 620,
        Windows8 = 620,
        WindowsServer2012R2 = 630,
        Windows8_1 = 630,
        Windows10 = 1000,
        WindowsServer2016 = 1000
    }

    public static bool IsLaterOrEqual(this OperatingSystem os, WindowsProductName ver)
    {
        if (os.Platform == PlatformID.Win32NT)
        {
            return 100 * os.Version.Major + 10 * os.Version.Minor >= (int)ver;
        }
        return false;
    }
}
