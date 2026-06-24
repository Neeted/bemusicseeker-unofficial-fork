using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace BeMusicSeeker.Library.Util;

public static class DllLoader
{
    [DllImport("kernel32")]
    private static extern IntPtr LoadLibrary(string lpFileName);

    public static void LoadLybraries(string directoryName)
    {
        directoryName = Path.GetFullPath(directoryName);
        if (!Directory.Exists(directoryName))
        {
            throw new DirectoryNotFoundException("Directory not found: " + directoryName);
        }
        LoadAllLybraries(directoryName, Directory.EnumerateFiles(directoryName, "*.dll"));
    }

    public static void LoadAllLybraries(string directoryName, IEnumerable<string> fileNames)
    {
        directoryName = Path.GetFullPath(directoryName);
        if (!Directory.Exists(directoryName))
        {
            throw new DirectoryNotFoundException("Directory not found: " + directoryName);
        }
        foreach (string fileName in fileNames)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                throw new PlatformNotSupportedException();
            }
            string text = Path.Combine(directoryName, fileName);
            if (!File.Exists(text) || !(LoadLibrary(text) != IntPtr.Zero))
            {
                throw new DllNotFoundException(text);
            }
        }
    }
}
