using System;
using System.Collections.Concurrent;
using System.Text;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2SongFolderParentNormalizer
{
    internal const string RootParentHash = "e2977170";

    private static readonly Encoding StrictShiftJis = Encoding.GetEncoding(
        "shift_jis",
        EncoderFallback.ExceptionFallback,
        DecoderFallback.ExceptionFallback);

    internal static bool ApplyExpected(BMSFile song, string lr2RootPath, bool fixRelativePath)
    {
        if (song == null || string.IsNullOrWhiteSpace(song.path))
        {
            return false;
        }

        string targetPath = song.path;
        if (fixRelativePath && !PathIsRooted(targetPath))
        {
            targetPath = System.IO.Path.Combine(lr2RootPath ?? string.Empty, targetPath);
        }

        if (!TryCompute(targetPath, out string folder, out string parent))
        {
            bool changed = MarkUnsupported(song);
            return changed;
        }

        bool hasChanged = false;
        if (!string.Equals(song.path, targetPath, StringComparison.Ordinal))
        {
            song.path = targetPath;
            hasChanged = true;
        }
        if (!string.Equals(song.folder, folder, StringComparison.OrdinalIgnoreCase))
        {
            song.folder = folder;
            hasChanged = true;
        }
        if (!string.Equals(song.parent, parent, StringComparison.OrdinalIgnoreCase))
        {
            song.parent = parent;
            hasChanged = true;
        }
        song.ClearWarning(ChartWarningKind.Lr2PathEncodingUnsupported);
        return hasChanged;
    }

    internal static bool ApplyIfMissingOrInvalid(BMSFile song)
    {
        return ApplyIfMissingOrInvalid(song, null);
    }

    internal static bool ApplyIfMissingOrInvalid(BMSFile song, Lr2FolderParentHashCache cache)
    {
        if (song == null || string.IsNullOrWhiteSpace(song.path) || !PathIsRooted(song.path))
        {
            return false;
        }

        bool crcMissingOrInvalid = !IsLikelyCrcHex(song.folder) || !IsLikelyCrcHex(song.parent);
        if (!TryCompute(song.path, cache, out string folder, out string parent))
        {
            return MarkUnsupported(song);
        }

        if (!crcMissingOrInvalid)
        {
            song.ClearWarning(ChartWarningKind.Lr2PathEncodingUnsupported);
            return false;
        }

        bool hasChanged = false;
        if (!string.Equals(song.folder, folder, StringComparison.OrdinalIgnoreCase))
        {
            song.folder = folder;
            hasChanged = true;
        }
        if (!string.Equals(song.parent, parent, StringComparison.OrdinalIgnoreCase))
        {
            song.parent = parent;
            hasChanged = true;
        }
        song.ClearWarning(ChartWarningKind.Lr2PathEncodingUnsupported);
        return hasChanged;
    }

    internal static bool IsLikelyCrcHex(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 1 || value.Length > 8)
        {
            return false;
        }
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            {
                return false;
            }
        }
        return true;
    }

    internal static bool TryComputeExpectedHashes(string chartPath, out string folder, out string parent)
    {
        return TryCompute(chartPath, out folder, out parent);
    }

    internal static string ComputeDirectoryHash(string directoryPath)
    {
        return LR2CRC32.Compute(StrictShiftJis.GetBytes((directoryPath ?? string.Empty) + "\\\0")).ToString("x");
    }

    internal static string ComputeRootHash()
    {
        return LR2CRC32.Compute(StrictShiftJis.GetBytes("ROOT\0")).ToString("x");
    }

    private static bool TryCompute(string chartPath, out string folder, out string parent)
    {
        folder = null;
        parent = null;
        if (string.IsNullOrWhiteSpace(chartPath))
        {
            return false;
        }

        try
        {
            StrictShiftJis.GetBytes(chartPath);
            string directoryPath = System.IO.Path.GetDirectoryName(chartPath);
            folder = ComputeDirectoryHash(directoryPath);
            parent = ComputeDirectoryHash(System.IO.Path.GetDirectoryName(directoryPath));
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCompute(string chartPath, Lr2FolderParentHashCache cache, out string folder, out string parent)
    {
        if (cache == null)
        {
            return TryCompute(chartPath, out folder, out parent);
        }
        return cache.TryCompute(chartPath, out folder, out parent);
    }

    private static bool MarkUnsupported(BMSFile song)
    {
        bool changed = !string.IsNullOrWhiteSpace(song.folder) || !string.IsNullOrWhiteSpace(song.parent);
        song.folder = null;
        song.parent = null;
        song.SetWarning(ChartWarningKind.Lr2PathEncodingUnsupported, Resources.Warning_Lr2PathEncodingUnsupported);
        return changed;
    }

    private static bool PathIsRooted(string path)
    {
        try
        {
            return System.IO.Path.IsPathRooted(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal sealed class Lr2FolderParentHashCache
    {
        private readonly ConcurrentDictionary<string, DirectoryHashPair> hashesByDirectory = new(StringComparer.OrdinalIgnoreCase);

        public bool TryCompute(string chartPath, out string folder, out string parent)
        {
            folder = null;
            parent = null;
            if (string.IsNullOrWhiteSpace(chartPath))
            {
                return false;
            }
            try
            {
                StrictShiftJis.GetBytes(chartPath);
                string directoryPath = System.IO.Path.GetDirectoryName(chartPath) ?? string.Empty;
                DirectoryHashPair pair = hashesByDirectory.GetOrAdd(directoryPath, CreatePair);
                folder = pair.Folder;
                parent = pair.Parent;
                return true;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        private static DirectoryHashPair CreatePair(string directoryPath)
        {
            return new DirectoryHashPair(
                ComputeDirectoryHash(directoryPath),
                ComputeDirectoryHash(System.IO.Path.GetDirectoryName(directoryPath)));
        }
    }

    private readonly struct DirectoryHashPair(string folder, string parent)
    {
        public string Folder { get; } = folder;

        public string Parent { get; } = parent;
    }
}
