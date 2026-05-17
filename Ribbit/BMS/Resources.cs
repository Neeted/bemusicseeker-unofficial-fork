using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ribbit.Cryptography;
using Ribbit.Util.Extensions;

namespace Ribbit.BMS;

public class Resources
{
    public const char DirectorySeparatorChar = '/';

    private readonly Dictionary<string, uint> _files = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

    public static readonly ReadOnlyCollection<string> AudioFileExtensions = new string[3] { ".wav", ".ogg", ".mp3" }.ToList().AsReadOnly();

    public static readonly ReadOnlyCollection<string> ImageFileExtensions = new string[5] { ".bmp", ".png", ".gif", ".jpg", ".jpeg" }.ToList().AsReadOnly();

    public static readonly ReadOnlyCollection<string> VideoFileExtensions = new string[4] { ".mpg", ".wmv", ".mp4", ".mpeg" }.ToList().AsReadOnly();

    private static readonly Regex FormatFilePathRegex = new Regex("[^/]+/\\.\\./", RegexOptions.Compiled);

    public ImmutableHashSet<string> FilePaths => _files.Keys.ToImmutableHashSet();

    public ImmutableHashSet<uint> FilePathsHashSet => _files.Values.ToImmutableHashSet();

    private static string NormalizeFilePath(string filePath)
    {
        filePath = filePath?.Trim();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(filePath.Tail(1, 1).TrimEnd('\\', '/')))
        {
            return null;
        }
        filePath = filePath.FastReplace('\\', '/').Replace("//", "/").ReplaceFromStart("./", string.Empty);
        filePath = FormatFilePathRegex.Replace(filePath, string.Empty);
        return filePath;
    }

    public static IEnumerable<string> NormalizeExtension(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || filePath.Length < 4)
        {
            yield return filePath;
            yield break;
        }
        string extension = Path.GetExtension(filePath);
        string filePathWoExt = filePath.Substring(0, filePath.Length - extension.Length);
        ReadOnlyCollection<string>[] array = new ReadOnlyCollection<string>[3] { AudioFileExtensions, ImageFileExtensions, VideoFileExtensions };
        for (int i = 0; i < array.Length; i++)
        {
            if (!array[i].Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (string audioFileExtension in AudioFileExtensions)
            {
                yield return filePathWoExt + audioFileExtension;
            }
            yield break;
        }
        yield return filePath;
    }

    private static string GeneralizeExtension(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || filePath.Length < 4)
        {
            return filePath;
        }
        string extension = Path.GetExtension(filePath);
        string text = filePath.Substring(0, filePath.Length - extension.Length);
        ReadOnlyCollection<string>[] array = new ReadOnlyCollection<string>[3] { AudioFileExtensions, ImageFileExtensions, VideoFileExtensions };
        foreach (ReadOnlyCollection<string> readOnlyCollection in array)
        {
            if (readOnlyCollection.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                string text2 = readOnlyCollection[0];
                return text + text2;
            }
        }
        return filePath;
    }

    public string AddFilePath(string filePath)
    {
        filePath = GeneralizeExtension(NormalizeFilePath(filePath.ToLowerInvariant()));
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            _files[filePath] = xxHash32.CalculateHash(filePath);
            return filePath;
        }
        return null;
    }

    public bool RemoveFilePath(string filePath)
    {
        filePath = GeneralizeExtension(NormalizeFilePath(filePath.ToLowerInvariant()));
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            return _files.Remove(filePath);
        }
        return false;
    }
}
