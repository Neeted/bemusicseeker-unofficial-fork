using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class DirectoryResourceLookupCache
{
    internal readonly struct ReverseLookupWarmupStepResult
    {
        public int ChunkEntryCount { get; }

        public int ProcessedEntryCount { get; }

        public int TotalEntryCount { get; }

        public int BuiltHashCount { get; }

        public long ChunkBuildMs { get; }

        public long TotalBuildMs { get; }

        public bool Completed { get; }

        public bool Paused { get; }

        public bool Cancelled { get; }

        public ReverseLookupWarmupStepResult(
            int chunkEntryCount,
            int processedEntryCount,
            int totalEntryCount,
            int builtHashCount,
            long chunkBuildMs,
            long totalBuildMs,
            bool completed,
            bool paused,
            bool cancelled)
        {
            ChunkEntryCount = chunkEntryCount;
            ProcessedEntryCount = processedEntryCount;
            TotalEntryCount = totalEntryCount;
            BuiltHashCount = builtHashCount;
            ChunkBuildMs = chunkBuildMs;
            TotalBuildMs = totalBuildMs;
            Completed = completed;
            Paused = paused;
            Cancelled = cancelled;
        }
    }

    private sealed class ReverseLookupWarmupState
    {
        public int Version { get; }

        public KeyValuePair<string, Entry>[] EntrySnapshot { get; }

        public int NextEntryIndex { get; set; }

        public Dictionary<uint, List<string>> AccumulatedDirectoriesByHash { get; }

        public long BuildCpuMs { get; set; }

        public bool Completed { get; set; }

        public int TotalEntryCount => EntrySnapshot.Length;

        public int BuiltHashCount => AccumulatedDirectoriesByHash.Count;

        public int ProcessedEntryCount => Math.Min(NextEntryIndex, TotalEntryCount);

        public ReverseLookupWarmupState(int version, KeyValuePair<string, Entry>[] entrySnapshot)
        {
            Version = version;
            EntrySnapshot = entrySnapshot ?? Array.Empty<KeyValuePair<string, Entry>>();
            AccumulatedDirectoriesByHash = new Dictionary<uint, List<string>>();
        }
    }

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

    private readonly object lockEntries = new object();

    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<uint, string[]> directoriesByHash = new Dictionary<uint, string[]>();

    private readonly object lockLazyDirectoriesByHash = new object();

    private ReverseLookupWarmupState warmupState;

    private long lazyHashBuildMs;

    private long lazyHashLookupCount;

    private int warmupVersion = 1;

    private bool isFullReverseLookupBuilt;

    private int highPriorityBuildCount;

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

    public int Count
    {
        get
        {
            lock (lockEntries)
            {
                return entries.Count;
            }
        }
    }

    public long LazyHashBuildMs => Interlocked.Read(ref lazyHashBuildMs);

    public long LazyHashLookupCount => Interlocked.Read(ref lazyHashLookupCount);

    public int WarmupVersion
    {
        get
        {
            lock (lockLazyDirectoriesByHash)
            {
                return warmupVersion;
            }
        }
    }

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
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }
        bool removed = false;
        lock (lockEntries)
        {
            removed = entries.Remove(directoryPath);
        }
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
        Entry entry = null;
        lock (lockEntries)
        {
            if (!entries.TryGetValue(oldPath, out entry))
            {
                return false;
            }
            entries.Remove(oldPath);
            entries[newPath] = entry;
        }
        InvalidateLazyReverseLookupCache();
        return true;
    }

    public IReadOnlyCollection<string> GetDirectoriesByHash(uint fileNameHash)
    {
        if (fileNameHash == 0u)
        {
            return Array.Empty<string>();
        }
        Interlocked.Increment(ref lazyHashLookupCount);
        EnsureDirectoriesByHashes(new uint[1] { fileNameHash });
        lock (lockLazyDirectoriesByHash)
        {
            if (directoriesByHash.TryGetValue(fileNameHash, out string[] directories))
            {
                return directories;
            }
        }
        return Array.Empty<string>();
    }

    public void EnsureDirectoriesByHashes(IEnumerable<uint> hashes)
    {
        BuildDirectoriesByHashes(hashes, countAsLookup: false, highPriority: true);
    }

    public int PrepareWarmupState()
    {
        lock (lockLazyDirectoriesByHash)
        {
            if (isFullReverseLookupBuilt)
            {
                return 0;
            }
            if (warmupState != null)
            {
                return warmupState.TotalEntryCount;
            }
        }
        KeyValuePair<string, Entry>[] entrySnapshot = SnapshotEntries();
        lock (lockLazyDirectoriesByHash)
        {
            if (isFullReverseLookupBuilt)
            {
                return 0;
            }
            if (warmupState == null)
            {
                warmupState = new ReverseLookupWarmupState(warmupVersion, entrySnapshot);
            }
            return warmupState.TotalEntryCount;
        }
    }

    public ReverseLookupWarmupStepResult WarmupReverseLookupStep(int maxEntryCount, int maxCpuMs, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return new ReverseLookupWarmupStepResult(0, 0, 0, 0, 0L, 0L, completed: false, paused: false, cancelled: true);
        }

        PrepareWarmupState();
        ReverseLookupWarmupState currentWarmupState;
        lock (lockLazyDirectoriesByHash)
        {
            currentWarmupState = warmupState;
            if (currentWarmupState == null)
            {
                return new ReverseLookupWarmupStepResult(0, 0, 0, directoriesByHash.Count, 0L, 0L, completed: true, paused: false, cancelled: false);
            }
            if (Volatile.Read(ref highPriorityBuildCount) > 0)
            {
                return CreateWarmupProgressResult(currentWarmupState, 0, 0L, completed: false, paused: true, cancelled: false);
            }
        }

        int processedInChunk = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (processedInChunk < maxEntryCount
            && currentWarmupState.NextEntryIndex < currentWarmupState.TotalEntryCount
            && (maxCpuMs <= 0 || stopwatch.ElapsedMilliseconds < maxCpuMs))
        {
            if (token.IsCancellationRequested)
            {
                stopwatch.Stop();
                return CreateWarmupProgressResult(currentWarmupState, processedInChunk, stopwatch.ElapsedMilliseconds, completed: false, paused: false, cancelled: true);
            }
            KeyValuePair<string, Entry> entryPair = currentWarmupState.EntrySnapshot[currentWarmupState.NextEntryIndex++];
            uint[] allBaseNameHashes = entryPair.Value?.AllBaseNameHashArray;
            if (allBaseNameHashes != null)
            {
                foreach (uint hash in allBaseNameHashes)
                {
                    if (!currentWarmupState.AccumulatedDirectoriesByHash.TryGetValue(hash, out List<string> directories))
                    {
                        directories = new List<string>();
                        currentWarmupState.AccumulatedDirectoriesByHash[hash] = directories;
                    }
                    directories.Add(entryPair.Key);
                }
            }
            processedInChunk++;
        }

        bool completed = currentWarmupState.NextEntryIndex >= currentWarmupState.TotalEntryCount;
        Dictionary<uint, string[]> completedIndex = null;
        if (completed)
        {
            completedIndex = new Dictionary<uint, string[]>(currentWarmupState.AccumulatedDirectoriesByHash.Count);
            foreach (KeyValuePair<uint, List<string>> hashDirectoriesPair in currentWarmupState.AccumulatedDirectoriesByHash)
            {
                completedIndex[hashDirectoriesPair.Key] = hashDirectoriesPair.Value.ToArray();
            }
            currentWarmupState.Completed = true;
        }

        stopwatch.Stop();
        currentWarmupState.BuildCpuMs += stopwatch.ElapsedMilliseconds;

        lock (lockLazyDirectoriesByHash)
        {
            if (!ReferenceEquals(warmupState, currentWarmupState) || currentWarmupState.Version != warmupVersion)
            {
                return CreateWarmupProgressResult(currentWarmupState, processedInChunk, stopwatch.ElapsedMilliseconds, completed: false, paused: false, cancelled: true);
            }

            if (completed)
            {
                directoriesByHash.Clear();
                foreach (KeyValuePair<uint, string[]> hashDirectoriesPair in completedIndex)
                {
                    directoriesByHash[hashDirectoriesPair.Key] = hashDirectoriesPair.Value;
                }
                warmupState = null;
                isFullReverseLookupBuilt = true;
            }
        }

        return CreateWarmupProgressResult(currentWarmupState, processedInChunk, stopwatch.ElapsedMilliseconds, completed: completed, paused: false, cancelled: false);
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
        lock (lockEntries)
        {
            entries[directoryPath] = entry ?? new Entry();
        }
        InvalidateLazyReverseLookupCache();
    }

    private void InvalidateLazyReverseLookupCache()
    {
        lock (lockLazyDirectoriesByHash)
        {
            directoriesByHash.Clear();
            warmupState = null;
            isFullReverseLookupBuilt = false;
            warmupVersion++;
        }
        Interlocked.Exchange(ref lazyHashBuildMs, 0L);
        Interlocked.Exchange(ref lazyHashLookupCount, 0L);
    }

    private ReverseLookupBuildResult BuildDirectoriesByHashes(IEnumerable<uint> hashes, bool countAsLookup, bool highPriority)
    {
        uint[] requestedHashes = hashes?
            .Where((uint hash) => hash != 0u)
            .Distinct()
            .ToArray() ?? Array.Empty<uint>();
        if (requestedHashes.Length == 0)
        {
            return ReverseLookupBuildResult.Empty;
        }

        if (countAsLookup)
        {
            Interlocked.Add(ref lazyHashLookupCount, requestedHashes.Length);
        }

        HashSet<uint> missingHashes = new HashSet<uint>(requestedHashes);
        lock (lockLazyDirectoriesByHash)
        {
            missingHashes.RemoveWhere((uint hash) => directoriesByHash.ContainsKey(hash));
        }
        if (missingHashes.Count == 0)
        {
            return ReverseLookupBuildResult.Empty;
        }

        if (highPriority)
        {
            Interlocked.Increment(ref highPriorityBuildCount);
        }

        try
        {
            KeyValuePair<string, Entry>[] entrySnapshot = SnapshotEntries();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Dictionary<uint, List<string>> builtDirectories = new Dictionary<uint, List<string>>();
            foreach (KeyValuePair<string, Entry> entryPair in entrySnapshot)
            {
                uint[] allBaseNameHashes = entryPair.Value?.AllBaseNameHashArray;
                if (allBaseNameHashes == null || allBaseNameHashes.Length == 0)
                {
                    continue;
                }
                foreach (uint hash in allBaseNameHashes)
                {
                    if (!missingHashes.Contains(hash))
                    {
                        continue;
                    }
                    if (!builtDirectories.TryGetValue(hash, out List<string> directories))
                    {
                        directories = new List<string>();
                        builtDirectories[hash] = directories;
                    }
                    directories.Add(entryPair.Key);
                }
            }
            long elapsedMs = (long)((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

            int addedEntryCount = 0;
            lock (lockLazyDirectoriesByHash)
            {
                foreach (uint hash in missingHashes)
                {
                    if (directoriesByHash.ContainsKey(hash))
                    {
                        continue;
                    }
                    if (builtDirectories.TryGetValue(hash, out List<string> directories))
                    {
                        directoriesByHash[hash] = directories
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    }
                    else
                    {
                        directoriesByHash[hash] = Array.Empty<string>();
                    }
                    addedEntryCount++;
                }
            }
            Interlocked.Add(ref lazyHashBuildMs, elapsedMs);
            return new ReverseLookupBuildResult(addedEntryCount, elapsedMs);
        }
        finally
        {
            if (highPriority)
            {
                Interlocked.Decrement(ref highPriorityBuildCount);
            }
        }
    }

    private KeyValuePair<string, Entry>[] SnapshotEntries()
    {
        lock (lockEntries)
        {
            return entries.ToArray();
        }
    }

    private static ReverseLookupWarmupStepResult CreateWarmupProgressResult(ReverseLookupWarmupState currentWarmupState, int chunkEntryCount, long chunkBuildMs, bool completed, bool paused, bool cancelled)
    {
        return new ReverseLookupWarmupStepResult(
            chunkEntryCount,
            currentWarmupState?.ProcessedEntryCount ?? 0,
            currentWarmupState?.TotalEntryCount ?? 0,
            currentWarmupState?.BuiltHashCount ?? 0,
            chunkBuildMs,
            currentWarmupState?.BuildCpuMs ?? 0L,
            completed,
            paused,
            cancelled);
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

    private readonly struct ReverseLookupBuildResult
    {
        public static ReverseLookupBuildResult Empty => new ReverseLookupBuildResult(0, 0L);

        public int AddedEntryCount { get; }

        public long ElapsedMs { get; }

        public ReverseLookupBuildResult(int addedEntryCount, long elapsedMs)
        {
            AddedEntryCount = addedEntryCount;
            ElapsedMs = elapsedMs;
        }
    }
}
