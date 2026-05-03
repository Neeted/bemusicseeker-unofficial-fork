using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class DirectoryRelativePathHashIndex
{
    internal sealed class Entry
    {
        private readonly uint[] audioBaseNameHashArray;

        private readonly uint[] imageBaseNameHashArray;

        private readonly uint[] movieBaseNameHashArray;

        private readonly uint[] audioRelativePathHashArray;

        private readonly uint[] imageRelativePathHashArray;

        private readonly uint[] movieRelativePathHashArray;

        private readonly uint[] selfOwnedAudioBaseNameHashArray;

        private readonly uint[] selfOwnedImageBaseNameHashArray;

        private readonly uint[] selfOwnedMovieBaseNameHashArray;

        private readonly uint[] selfOwnedAudioRelativePathHashArray;

        private readonly uint[] selfOwnedImageRelativePathHashArray;

        private readonly uint[] selfOwnedMovieRelativePathHashArray;

        private HashSet<uint> audioBaseNameHashes;

        private HashSet<uint> imageBaseNameHashes;

        private HashSet<uint> movieBaseNameHashes;

        private HashSet<uint> audioRelativePathHashes;

        private HashSet<uint> imageRelativePathHashes;

        private HashSet<uint> movieRelativePathHashes;

        private HashSet<uint> selfOwnedAudioBaseNameHashes;

        private HashSet<uint> selfOwnedImageBaseNameHashes;

        private HashSet<uint> selfOwnedMovieBaseNameHashes;

        private HashSet<uint> selfOwnedAudioRelativePathHashes;

        private HashSet<uint> selfOwnedImageRelativePathHashes;

        private HashSet<uint> selfOwnedMovieRelativePathHashes;

        public ISet<uint> AudioBaseNameHashes => audioBaseNameHashes ??= CreateHashSet(audioBaseNameHashArray);

        public ISet<uint> ImageBaseNameHashes => imageBaseNameHashes ??= CreateHashSet(imageBaseNameHashArray);

        public ISet<uint> MovieBaseNameHashes => movieBaseNameHashes ??= CreateHashSet(movieBaseNameHashArray);

        public ISet<uint> AudioRelativePathHashes => audioRelativePathHashes ??= CreateHashSet(audioRelativePathHashArray);

        public ISet<uint> ImageRelativePathHashes => imageRelativePathHashes ??= CreateHashSet(imageRelativePathHashArray);

        public ISet<uint> MovieRelativePathHashes => movieRelativePathHashes ??= CreateHashSet(movieRelativePathHashArray);

        public ISet<uint> SelfOwnedAudioBaseNameHashes => selfOwnedAudioBaseNameHashes ??= CreateHashSet(selfOwnedAudioBaseNameHashArray);

        public ISet<uint> SelfOwnedImageBaseNameHashes => selfOwnedImageBaseNameHashes ??= CreateHashSet(selfOwnedImageBaseNameHashArray);

        public ISet<uint> SelfOwnedMovieBaseNameHashes => selfOwnedMovieBaseNameHashes ??= CreateHashSet(selfOwnedMovieBaseNameHashArray);

        public ISet<uint> SelfOwnedAudioRelativePathHashes => selfOwnedAudioRelativePathHashes ??= CreateHashSet(selfOwnedAudioRelativePathHashArray);

        public ISet<uint> SelfOwnedImageRelativePathHashes => selfOwnedImageRelativePathHashes ??= CreateHashSet(selfOwnedImageRelativePathHashArray);

        public ISet<uint> SelfOwnedMovieRelativePathHashes => selfOwnedMovieRelativePathHashes ??= CreateHashSet(selfOwnedMovieRelativePathHashArray);

        public uint[] AudioBaseNameHashArray => audioBaseNameHashArray;

        public uint[] ImageBaseNameHashArray => imageBaseNameHashArray;

        public uint[] MovieBaseNameHashArray => movieBaseNameHashArray;

        public uint[] AudioRelativePathHashArray => audioRelativePathHashArray;

        public uint[] ImageRelativePathHashArray => imageRelativePathHashArray;

        public uint[] MovieRelativePathHashArray => movieRelativePathHashArray;

        public uint[] SelfOwnedAudioBaseNameHashArray => selfOwnedAudioBaseNameHashArray;

        public uint[] SelfOwnedImageBaseNameHashArray => selfOwnedImageBaseNameHashArray;

        public uint[] SelfOwnedMovieBaseNameHashArray => selfOwnedMovieBaseNameHashArray;

        public uint[] SelfOwnedAudioRelativePathHashArray => selfOwnedAudioRelativePathHashArray;

        public uint[] SelfOwnedImageRelativePathHashArray => selfOwnedImageRelativePathHashArray;

        public uint[] SelfOwnedMovieRelativePathHashArray => selfOwnedMovieRelativePathHashArray;

        public Entry()
            : this(Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>())
        {
        }

        public Entry(IEnumerable<uint> audioBaseNameHashes, IEnumerable<uint> imageBaseNameHashes, IEnumerable<uint> movieBaseNameHashes, IEnumerable<uint> audioRelativePathHashes, IEnumerable<uint> imageRelativePathHashes, IEnumerable<uint> movieRelativePathHashes, IEnumerable<uint> selfOwnedAudioBaseNameHashes = null, IEnumerable<uint> selfOwnedImageBaseNameHashes = null, IEnumerable<uint> selfOwnedMovieBaseNameHashes = null, IEnumerable<uint> selfOwnedAudioRelativePathHashes = null, IEnumerable<uint> selfOwnedImageRelativePathHashes = null, IEnumerable<uint> selfOwnedMovieRelativePathHashes = null)
        {
            audioBaseNameHashArray = MaterializeHashes(audioBaseNameHashes);
            imageBaseNameHashArray = MaterializeHashes(imageBaseNameHashes);
            movieBaseNameHashArray = MaterializeHashes(movieBaseNameHashes);
            audioRelativePathHashArray = MaterializeHashes(audioRelativePathHashes);
            imageRelativePathHashArray = MaterializeHashes(imageRelativePathHashes);
            movieRelativePathHashArray = MaterializeHashes(movieRelativePathHashes);
            selfOwnedAudioBaseNameHashArray = MaterializeHashes(selfOwnedAudioBaseNameHashes, audioBaseNameHashArray);
            selfOwnedImageBaseNameHashArray = MaterializeHashes(selfOwnedImageBaseNameHashes, imageBaseNameHashArray);
            selfOwnedMovieBaseNameHashArray = MaterializeHashes(selfOwnedMovieBaseNameHashes, movieBaseNameHashArray);
            selfOwnedAudioRelativePathHashArray = MaterializeHashes(selfOwnedAudioRelativePathHashes, audioRelativePathHashArray);
            selfOwnedImageRelativePathHashArray = MaterializeHashes(selfOwnedImageRelativePathHashes, imageRelativePathHashArray);
            selfOwnedMovieRelativePathHashArray = MaterializeHashes(selfOwnedMovieRelativePathHashes, movieRelativePathHashArray);
        }

        public Entry Clone()
        {
            return new Entry(audioBaseNameHashArray, imageBaseNameHashArray, movieBaseNameHashArray, audioRelativePathHashArray, imageRelativePathHashArray, movieRelativePathHashArray, selfOwnedAudioBaseNameHashArray, selfOwnedImageBaseNameHashArray, selfOwnedMovieBaseNameHashArray, selfOwnedAudioRelativePathHashArray, selfOwnedImageRelativePathHashArray, selfOwnedMovieRelativePathHashArray);
        }

        private static uint[] MaterializeHashes(IEnumerable<uint> hashes)
        {
            if (hashes == null)
            {
                return Array.Empty<uint>();
            }
            uint[] hashArray = hashes as uint[] ?? hashes.Distinct().ToArray();
            if (hashArray.Length <= 1)
            {
                return hashArray;
            }
            uint[] sorted = hashArray.Distinct().ToArray();
            Array.Sort(sorted);
            return sorted;
        }

        private static uint[] MaterializeHashes(IEnumerable<uint> hashes, uint[] fallback)
        {
            if (hashes == null)
            {
                return fallback ?? Array.Empty<uint>();
            }
            return MaterializeHashes(hashes);
        }

        private static HashSet<uint> CreateHashSet(IEnumerable<uint> hashes)
        {
            return hashes == null ? new HashSet<uint>() : new HashSet<uint>(hashes);
        }
    }

    private readonly object lockEntries = new object();

    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Keys
    {
        get
        {
            lock (lockEntries)
            {
                return entries.Keys.ToArray();
            }
        }
    }

    public static DirectoryRelativePathHashIndex CreateFromScanResult(BmsScanResult scanResult)
    {
        DirectoryRelativePathHashIndex index = new DirectoryRelativePathHashIndex();
        foreach (string chartDirectory in scanResult?.ChartDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        {
            index.SetEntry(chartDirectory, CreateEntry(scanResult, chartDirectory));
        }
        return index;
    }

    public void AddDir(string directoryPath, BmsScanResult scanResult)
    {
        if (scanResult == null || string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }
        SetEntry(directoryPath, CreateEntry(scanResult, directoryPath));
    }

    public void AddDir(string directoryPath, IEnumerable<uint> audioRelativePathHashes, IEnumerable<uint> imageRelativePathHashes, IEnumerable<uint> movieRelativePathHashes)
    {
        AddDir(directoryPath, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), audioRelativePathHashes, imageRelativePathHashes, movieRelativePathHashes);
    }

    public void AddDir(string directoryPath, IEnumerable<uint> audioBaseNameHashes, IEnumerable<uint> imageBaseNameHashes, IEnumerable<uint> movieBaseNameHashes, IEnumerable<uint> audioRelativePathHashes, IEnumerable<uint> imageRelativePathHashes, IEnumerable<uint> movieRelativePathHashes, IEnumerable<uint> selfOwnedAudioBaseNameHashes = null, IEnumerable<uint> selfOwnedImageBaseNameHashes = null, IEnumerable<uint> selfOwnedMovieBaseNameHashes = null, IEnumerable<uint> selfOwnedAudioRelativePathHashes = null, IEnumerable<uint> selfOwnedImageRelativePathHashes = null, IEnumerable<uint> selfOwnedMovieRelativePathHashes = null)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }
        SetEntry(directoryPath, new Entry(audioBaseNameHashes, imageBaseNameHashes, movieBaseNameHashes, audioRelativePathHashes, imageRelativePathHashes, movieRelativePathHashes, selfOwnedAudioBaseNameHashes, selfOwnedImageBaseNameHashes, selfOwnedMovieBaseNameHashes, selfOwnedAudioRelativePathHashes, selfOwnedImageRelativePathHashes, selfOwnedMovieRelativePathHashes));
    }

    public bool RemoveDir(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }
        lock (lockEntries)
        {
            return entries.Remove(directoryPath);
        }
    }

    public bool ReplaceDir(string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
        {
            return false;
        }
        lock (lockEntries)
        {
            if (!entries.TryGetValue(oldPath, out Entry entry))
            {
                return false;
            }
            entries.Remove(oldPath);
            entries[newPath] = entry;
            return true;
        }
    }

    public Entry GetEntryOrNull(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }
        lock (lockEntries)
        {
            entries.TryGetValue(directoryPath, out Entry entry);
            return entry;
        }
    }

    private void SetEntry(string directoryPath, Entry entry)
    {
        lock (lockEntries)
        {
            entries[directoryPath] = entry ?? new Entry();
        }
    }

    private static Entry CreateEntry(BmsScanResult scanResult, string directoryPath)
    {
        return new Entry(
            TryGetHashes(scanResult?.AudioBaseNameHashesByChartDirectory, directoryPath),
            TryGetHashes(scanResult?.ImageBaseNameHashesByChartDirectory, directoryPath),
            TryGetHashes(scanResult?.MovieBaseNameHashesByChartDirectory, directoryPath),
            TryGetHashes(scanResult?.AudioRelativePathHashesByChartDirectory, directoryPath),
            TryGetHashes(scanResult?.ImageRelativePathHashesByChartDirectory, directoryPath),
            TryGetHashes(scanResult?.MovieRelativePathHashesByChartDirectory, directoryPath),
            TryGetHashes(scanResult?.SelfOwnedAudioBaseNameHashesByChartDirectory, directoryPath, scanResult?.AudioBaseNameHashesByChartDirectory),
            TryGetHashes(scanResult?.SelfOwnedImageBaseNameHashesByChartDirectory, directoryPath, scanResult?.ImageBaseNameHashesByChartDirectory),
            TryGetHashes(scanResult?.SelfOwnedMovieBaseNameHashesByChartDirectory, directoryPath, scanResult?.MovieBaseNameHashesByChartDirectory),
            TryGetHashes(scanResult?.SelfOwnedAudioRelativePathHashesByChartDirectory, directoryPath, scanResult?.AudioRelativePathHashesByChartDirectory),
            TryGetHashes(scanResult?.SelfOwnedImageRelativePathHashesByChartDirectory, directoryPath, scanResult?.ImageRelativePathHashesByChartDirectory),
            TryGetHashes(scanResult?.SelfOwnedMovieRelativePathHashesByChartDirectory, directoryPath, scanResult?.MovieRelativePathHashesByChartDirectory));
    }

    private static IEnumerable<uint> TryGetHashes(Dictionary<string, uint[]> hashesByDirectory, string directoryPath)
    {
        if (hashesByDirectory != null && hashesByDirectory.TryGetValue(directoryPath, out uint[] hashes))
        {
            return hashes ?? Array.Empty<uint>();
        }
        return Array.Empty<uint>();
    }

    private static IEnumerable<uint> TryGetHashes(Dictionary<string, uint[]> hashesByDirectory, string directoryPath, Dictionary<string, uint[]> fallbackHashesByDirectory)
    {
        if (hashesByDirectory != null && hashesByDirectory.TryGetValue(directoryPath, out uint[] hashes) && hashes != null && hashes.Length > 0)
        {
            return hashes;
        }
        return TryGetHashes(fallbackHashesByDirectory, directoryPath);
    }
}
