using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace BeMusicSeeker.Views.Dialogs;

internal static class UiFilePickerUtilities
{
    internal static IReadOnlyList<Tuple<string, string>> ParseFilterPairs(string filter)
    {
        List<Tuple<string, string>> filters = [];
        if (!string.IsNullOrWhiteSpace(filter))
        {
            string[] parts = filter.Split('|');
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                string displayName = parts[i];
                string extensionList = parts[i + 1];
                if (string.IsNullOrWhiteSpace(extensionList))
                {
                    continue;
                }
                filters.Add(Tuple.Create(
                    string.IsNullOrWhiteSpace(displayName) ? extensionList : displayName,
                    extensionList));
            }
        }
        if (filters.Count == 0)
        {
            filters.Add(Tuple.Create("*.*", "*.*"));
        }
        return filters;
    }

    internal static string InferDefaultExtension(string fileName, string filter)
    {
        string extension = GetExtensionWithoutDot(fileName);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            return extension;
        }
        foreach (Tuple<string, string> filterPair in ParseFilterPairs(filter))
        {
            foreach (string pattern in (filterPair.Item2 ?? string.Empty).Split(';'))
            {
                extension = GetExtensionWithoutDot(pattern);
                if (!string.IsNullOrWhiteSpace(extension))
                {
                    return extension;
                }
            }
        }
        return null;
    }

    internal static string ResolveInitialDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            if (Directory.Exists(path))
            {
                return path;
            }
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                return directory;
            }
        }
        catch
        {
        }
        return null;
    }

    internal static void SetInitialDirectory(CommonOpenFileDialog dialog, string path)
    {
        string initialDirectory = ResolveInitialDirectory(path);
        if (!string.IsNullOrWhiteSpace(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
            dialog.DefaultDirectory = initialDirectory;
        }
    }

    private static string GetExtensionWithoutDot(string pathOrPattern)
    {
        if (string.IsNullOrWhiteSpace(pathOrPattern) || pathOrPattern.Contains("?"))
        {
            return null;
        }
        string value = pathOrPattern.Trim();
        if (value == "*.*")
        {
            return null;
        }
        try
        {
            string extension = Path.GetExtension(value);
            if (string.IsNullOrWhiteSpace(extension) || extension == "." || extension.Contains("*") || extension.Contains("?"))
            {
                return null;
            }
            return extension.TrimStart('.');
        }
        catch
        {
            return null;
        }
    }
}
