using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class DirectoryResourceLookupCache
{
    internal sealed class Entry
    {
        private readonly uint[] allBaseNameHashArray;

        private readonly uint[] audioBaseNameHashArray;

        private readonly uint[] imageBaseNameHashArray;

        private readonly uint[] movieBaseNameHashArray;

        private readonly uint[] audioRelativePathHashArray;

        private readonly uint[] imageRelativePathHashArray;

        private readonly uint[] movieRelativePathHashArray;

        private HashSet<uint> allBaseNameHashes;

        private HashSet<uint> audioBaseNameHashes;

        private HashSet<uint> imageBaseNameHashes;

        private HashSet<uint> movieBaseNameHashes;

        private HashSet<uint> audioRelativePathHashes;

        private HashSet<uint> imageRelativePathHashes;

        private HashSet<uint> movieRelativePathHashes;

        public ISet<uint> AllBaseNameHashes => allBaseNameHashes ??= CreateHashSet(allBaseNameHashArray);

        public ISet<uint> AudioBaseNameHashes => audioBaseNameHashes ??= CreateHashSet(audioBaseNameHashArray);

        public ISet<uint> ImageBaseNameHashes => imageBaseNameHashes ??= CreateHashSet(imageBaseNameHashArray);

        public ISet<uint> MovieBaseNameHashes => movieBaseNameHashes ??= CreateHashSet(movieBaseNameHashArray);

        public ISet<uint> AudioRelativePathHashes => audioRelativePathHashes ??= CreateHashSet(audioRelativePathHashArray);

        public ISet<uint> ImageRelativePathHashes => imageRelativePathHashes ??= CreateHashSet(imageRelativePathHashArray);

        public ISet<uint> MovieRelativePathHashes => movieRelativePathHashes ??= CreateHashSet(movieRelativePathHashArray);

        public uint[] AllBaseNameHashArray => allBaseNameHashArray;

        public uint[] AudioBaseNameHashArray => audioBaseNameHashArray;

        public uint[] ImageBaseNameHashArray => imageBaseNameHashArray;

        public uint[] MovieBaseNameHashArray => movieBaseNameHashArray;

        public uint[] AudioRelativePathHashArray => audioRelativePathHashArray;

        public uint[] ImageRelativePathHashArray => imageRelativePathHashArray;

        public uint[] MovieRelativePathHashArray => movieRelativePathHashArray;

        public int FileNameHashCount => allBaseNameHashArray.Length;

        public int AudioFileNameHashCount => audioBaseNameHashArray.Length;

        public int ImageFileNameHashCount => imageBaseNameHashArray.Length;

        public int MovieFileNameHashCount => movieBaseNameHashArray.Length;

        public Entry()
            : this(Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>())
        {
        }

        public Entry(
            IEnumerable<uint> allBaseNameHashes,
            IEnumerable<uint> audioBaseNameHashes,
            IEnumerable<uint> imageBaseNameHashes,
            IEnumerable<uint> movieBaseNameHashes,
            IEnumerable<uint> audioRelativePathHashes,
            IEnumerable<uint> imageRelativePathHashes,
            IEnumerable<uint> movieRelativePathHashes)
        {
            allBaseNameHashArray = MaterializeHashes(allBaseNameHashes);
            audioBaseNameHashArray = MaterializeHashes(audioBaseNameHashes);
            imageBaseNameHashArray = MaterializeHashes(imageBaseNameHashes);
            movieBaseNameHashArray = MaterializeHashes(movieBaseNameHashes);
            audioRelativePathHashArray = MaterializeHashes(audioRelativePathHashes);
            imageRelativePathHashArray = MaterializeHashes(imageRelativePathHashes);
            movieRelativePathHashArray = MaterializeHashes(movieRelativePathHashes);
        }

        public Entry Clone()
        {
            return new Entry(
                allBaseNameHashArray,
                audioBaseNameHashArray,
                imageBaseNameHashArray,
                movieBaseNameHashArray,
                audioRelativePathHashArray,
                imageRelativePathHashArray,
                movieRelativePathHashArray);
        }

        private static uint[] MaterializeHashes(IEnumerable<uint> hashes)
        {
            if (hashes == null)
            {
                return Array.Empty<uint>();
            }
            if (hashes is uint[] hashArray)
            {
                return hashArray;
            }
            return hashes.Distinct().ToArray();
        }

        private static HashSet<uint> CreateHashSet(IEnumerable<uint> hashes)
        {
            return hashes == null ? new HashSet<uint>() : new HashSet<uint>(hashes);
        }
    }

    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<uint, string[]> directoriesByHash = new Dictionary<uint, string[]>();

    private readonly object lockLazyDirectoriesByHash = new object();

    private long lazyHashBuildMs;

    private long lazyHashLookupCount;

    public IEnumerable<string> Keys => entries.Keys;

    public int Count => entries.Count;

    public long LazyHashBuildMs => Interlocked.Read(ref lazyHashBuildMs);

    public long LazyHashLookupCount => Interlocked.Read(ref lazyHashLookupCount);

    public int LazyHashCacheEntryCount
    {
        get
        {
            lock (lockLazyDirectoriesByHash)
            {
                return directoriesByHash.Count;
            }
        }
    }

    public static DirectoryResourceLookupCache CreateFromScanResult(BmsScanResult scanResult)
    {
        DirectoryResourceLookupCache cache = new DirectoryResourceLookupCache();
        foreach (string chartDirectory in scanResult?.ChartDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        {
            cache.SetEntry(chartDirectory, CreateEntry(scanResult, chartDirectory));
        }
        return cache;
    }

    public void AddDir(
        string directoryPath,
        IEnumerable<uint> allBaseNameHashes,
        IEnumerable<uint> audioBaseNameHashes,
        IEnumerable<uint> imageBaseNameHashes,
        IEnumerable<uint> movieBaseNameHashes,
        IEnumerable<uint> audioRelativePathHashes,
        IEnumerable<uint> imageRelativePathHashes,
        IEnumerable<uint> movieRelativePathHashes)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }

        Entry entry = new Entry(
            allBaseNameHashes,
            audioBaseNameHashes,
            imageBaseNameHashes,
            movieBaseNameHashes,
            audioRelativePathHashes,
            imageRelativePathHashes,
            movieRelativePathHashes);
        SetEntry(directoryPath, entry);
    }

    public void AddDir(string directoryPath, IEnumerable<string> fileNames)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }

        HashSet<uint> allBaseNameHashes = new HashSet<uint>();
        HashSet<uint> audioBaseNameHashes = new HashSet<uint>();
        HashSet<uint> imageBaseNameHashes = new HashSet<uint>();
        HashSet<uint> movieBaseNameHashes = new HashSet<uint>();
        HashSet<uint> audioRelativePathHashes = new HashSet<uint>();
        HashSet<uint> imageRelativePathHashes = new HashSet<uint>();
        HashSet<uint> movieRelativePathHashes = new HashSet<uint>();
        foreach (string fileName in fileNames ?? Enumerable.Empty<string>())
        {
            string normalizedPath = ChartResourcePathNormalizer.NormalizeReferencePathForLookup(fileName);
            string normalizedFileName = ChartResourcePathNormalizer.NormalizeFileNameForLookup(fileName);
            if (string.IsNullOrWhiteSpace(normalizedFileName))
            {
                continue;
            }
            uint baseNameHash = BMSDirectoryFileNameHash.GetLookupHash(normalizedFileName);
            allBaseNameHashes.Add(baseNameHash);
            switch (ChartResourcePathNormalizer.ClassifyPath(fileName))
            {
                case ChartResourceKind.Audio:
                    audioBaseNameHashes.Add(baseNameHash);
                    if (!string.IsNullOrWhiteSpace(normalizedPath))
                    {
                        audioRelativePathHashes.Add(BMSDirectoryFileNameHash.GetLookupHash(normalizedPath));
                    }
                    break;
                case ChartResourceKind.Image:
                    imageBaseNameHashes.Add(baseNameHash);
                    if (!string.IsNullOrWhiteSpace(normalizedPath))
                    {
                        imageRelativePathHashes.Add(BMSDirectoryFileNameHash.GetLookupHash(normalizedPath));
                    }
                    break;
                case ChartResourceKind.Movie:
                    movieBaseNameHashes.Add(baseNameHash);
                    if (!string.IsNullOrWhiteSpace(normalizedPath))
                    {
                        movieRelativePathHashes.Add(BMSDirectoryFileNameHash.GetLookupHash(normalizedPath));
                    }
                    break;
            }
        }
        SetEntry(directoryPath, new Entry(
            allBaseNameHashes,
            audioBaseNameHashes,
            imageBaseNameHashes,
            movieBaseNameHashes,
            audioRelativePathHashes,
            imageRelativePathHashes,
            movieRelativePathHashes));
    }

    public void AddDirHashed(string directoryPath, IEnumerable<uint> allBaseNameHashes)
    {
        AddDir(directoryPath, allBaseNameHashes, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());
    }

    public void AddDir(string directoryPath, BmsScanResult scanResult)
    {
        if (scanResult == null || string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }
        SetEntry(directoryPath, CreateEntry(scanResult, directoryPath));
    }

    public bool RemoveDir(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !entries.TryGetValue(directoryPath, out Entry entry))
        {
            return false;
        }
        bool removed = entries.Remove(directoryPath);
        if (removed)
        {
            InvalidateLazyReverseLookupCache();
        }
        return removed;
    }

    public bool ReplaceDir(string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
        {
            return false;
        }
        if (!entries.TryGetValue(oldPath, out Entry entry))
        {
            return false;
        }
        entries.Remove(oldPath);
        entries[newPath] = entry;
        InvalidateLazyReverseLookupCache();
        return true;
    }

    public IReadOnlyCollection<string> GetDirectoriesByHash(uint fileNameHash)
    {
        Interlocked.Increment(ref lazyHashLookupCount);
        if (fileNameHash == 0u)
        {
            return Array.Empty<string>();
        }
        lock (lockLazyDirectoriesByHash)
        {
            if (directoriesByHash.TryGetValue(fileNameHash, out string[] directories))
            {
                return directories;
            }
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        string[] builtDirectories = entries
            .Where((KeyValuePair<string, Entry> pair) => pair.Value?.AllBaseNameHashArray != null && Array.BinarySearch(pair.Value.AllBaseNameHashArray, fileNameHash) >= 0)
            .Select((KeyValuePair<string, Entry> pair) => pair.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        long elapsedMs = (long)((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

        lock (lockLazyDirectoriesByHash)
        {
            if (!directoriesByHash.TryGetValue(fileNameHash, out string[] cachedDirectories))
            {
                directoriesByHash[fileNameHash] = builtDirectories;
                Interlocked.Add(ref lazyHashBuildMs, elapsedMs);
                return builtDirectories;
            }
            return cachedDirectories;
        }
    }

    public Entry GetEntryOrNull(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }
        entries.TryGetValue(directoryPath, out Entry entry);
        return entry;
    }

    private static IEnumerable<uint> TryGetHashes(Dictionary<string, uint[]> hashesByDirectory, string directoryPath)
    {
        if (hashesByDirectory != null && hashesByDirectory.TryGetValue(directoryPath, out uint[] hashes))
        {
            return hashes ?? Array.Empty<uint>();
        }
        return Array.Empty<uint>();
    }

    private void SetEntry(string directoryPath, Entry entry)
    {
        entries[directoryPath] = entry ?? new Entry();
        InvalidateLazyReverseLookupCache();
    }

    private void InvalidateLazyReverseLookupCache()
    {
        lock (lockLazyDirectoriesByHash)
        {
            directoriesByHash.Clear();
        }
        Interlocked.Exchange(ref lazyHashBuildMs, 0L);
        Interlocked.Exchange(ref lazyHashLookupCount, 0L);
    }

    private static Entry CreateEntry(BmsScanResult scanResult, string directoryPath)
    {
        return new Entry(
            TryGetHashes(scanResult?.AllResourceBaseNameHashesByChartDirectory, directoryPath),
            TryGetHashes(scanResult?.AudioBaseNameHashesByChartDirectory, directoryPath),
            TryGetHashes(scanResult?.ImageBaseNameHashesByChartDirectory, directoryPath),
            TryGetHashes(scanResult?.MovieBaseNameHashesByChartDirectory, directoryPath),
            TryGetHashes(scanResult?.AudioRelativePathHashesByChartDirectory, directoryPath),
            TryGetHashes(scanResult?.ImageRelativePathHashesByChartDirectory, directoryPath),
            TryGetHashes(scanResult?.MovieRelativePathHashesByChartDirectory, directoryPath));
    }
}
