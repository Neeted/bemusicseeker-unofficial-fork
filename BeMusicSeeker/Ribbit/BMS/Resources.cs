using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Ribbit.Cryptography;
using Ribbit.Util.Extensions;

namespace Ribbit.BMS;

public class Resources
{
    public const char DirectorySeparatorChar = '/';

    private readonly Dictionary<string, uint> _files = new(StringComparer.OrdinalIgnoreCase);

    public static readonly ReadOnlyCollection<string> AudioFileExtensions = new string[3] { ".wav", ".ogg", ".mp3" }.ToList().AsReadOnly();

    public static readonly ReadOnlyCollection<string> ImageFileExtensions = new string[5] { ".bmp", ".png", ".gif", ".jpg", ".jpeg" }.ToList().AsReadOnly();

    public static readonly ReadOnlyCollection<string> VideoFileExtensions = new string[4] { ".mpg", ".wmv", ".mp4", ".mpeg" }.ToList().AsReadOnly();

    public ImmutableHashSet<string> FilePaths => _files.Keys.ToImmutableHashSet();

    public ImmutableHashSet<uint> FilePathsHashSet => _files.Values.ToImmutableHashSet();

    /// <summary>参照の区切り表記を揃えます。親参照は基点と結合する読込み側で解決するため保持します。</summary>
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
        var array = new ReadOnlyCollection<string>[3] { AudioFileExtensions, ImageFileExtensions, VideoFileExtensions };
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
        var array = new ReadOnlyCollection<string>[3] { AudioFileExtensions, ImageFileExtensions, VideoFileExtensions };
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

    /// <summary>資源参照の表記と拡張子を共通化して登録し、基点未解決の親参照を保った参照名を返します。</summary>
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

    /// <summary>登録時と同じ表記・拡張子の変換を使い、対応する資源参照を削除します。</summary>
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
