using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class DirectoryResourceLookupCache
{
    internal readonly struct ReverseLookupMutationResult
    {
        public static ReverseLookupMutationResult Empty => new ReverseLookupMutationResult(
            changed: false,
            addedDirectoryCount: 0,
            removedDirectoryCount: 0,
            replacedDirectoryCount: 0,
            updatedHashCount: 0,
            cancelledWarmup: false,
            maintainedFullReverseLookup: false,
            requiresDeferredWarmup: false);

        public bool Changed { get; }

        public int AddedDirectoryCount { get; }

        public int RemovedDirectoryCount { get; }

        public int ReplacedDirectoryCount { get; }

        public int UpdatedHashCount { get; }

        public bool CancelledWarmup { get; }

        public bool MaintainedFullReverseLookup { get; }

        public bool RequiresDeferredWarmup { get; }

        public ReverseLookupMutationResult(
            bool changed,
            int addedDirectoryCount,
            int removedDirectoryCount,
            int replacedDirectoryCount,
            int updatedHashCount,
            bool cancelledWarmup,
            bool maintainedFullReverseLookup,
            bool requiresDeferredWarmup)
        {
            Changed = changed;
            AddedDirectoryCount = addedDirectoryCount;
            RemovedDirectoryCount = removedDirectoryCount;
            ReplacedDirectoryCount = replacedDirectoryCount;
            UpdatedHashCount = updatedHashCount;
            CancelledWarmup = cancelledWarmup;
            MaintainedFullReverseLookup = maintainedFullReverseLookup;
            RequiresDeferredWarmup = requiresDeferredWarmup;
        }

        public ReverseLookupMutationResult Combine(ReverseLookupMutationResult other)
        {
            if (!Changed)
            {
                return other;
            }
            if (!other.Changed)
            {
                return this;
            }
            return new ReverseLookupMutationResult(
                changed: true,
                addedDirectoryCount: AddedDirectoryCount + other.AddedDirectoryCount,
                removedDirectoryCount: RemovedDirectoryCount + other.RemovedDirectoryCount,
                replacedDirectoryCount: ReplacedDirectoryCount + other.ReplacedDirectoryCount,
                updatedHashCount: UpdatedHashCount + other.UpdatedHashCount,
                cancelledWarmup: CancelledWarmup || other.CancelledWarmup,
                maintainedFullReverseLookup: MaintainedFullReverseLookup && other.MaintainedFullReverseLookup,
                requiresDeferredWarmup: RequiresDeferredWarmup || other.RequiresDeferredWarmup);
        }
    }

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

        private readonly uint[] selfOwnedAllBaseNameHashArray;

        private readonly uint[] selfOwnedAudioBaseNameHashArray;

        private readonly uint[] selfOwnedImageBaseNameHashArray;

        private readonly uint[] selfOwnedMovieBaseNameHashArray;

        private readonly uint[] selfOwnedAudioRelativePathHashArray;

        private readonly uint[] selfOwnedImageRelativePathHashArray;

        private readonly uint[] selfOwnedMovieRelativePathHashArray;

        private HashSet<uint> allBaseNameHashes;

        private HashSet<uint> audioBaseNameHashes;

        private HashSet<uint> imageBaseNameHashes;

        private HashSet<uint> movieBaseNameHashes;

        private HashSet<uint> audioRelativePathHashes;

        private HashSet<uint> imageRelativePathHashes;

        private HashSet<uint> movieRelativePathHashes;

        private HashSet<uint> selfOwnedAllBaseNameHashes;

        private HashSet<uint> selfOwnedAudioBaseNameHashes;

        private HashSet<uint> selfOwnedImageBaseNameHashes;

        private HashSet<uint> selfOwnedMovieBaseNameHashes;

        private HashSet<uint> selfOwnedAudioRelativePathHashes;

        private HashSet<uint> selfOwnedImageRelativePathHashes;

        private HashSet<uint> selfOwnedMovieRelativePathHashes;

        public ISet<uint> AllBaseNameHashes => allBaseNameHashes ??= CreateHashSet(allBaseNameHashArray);

        public ISet<uint> AudioBaseNameHashes => audioBaseNameHashes ??= CreateHashSet(audioBaseNameHashArray);

        public ISet<uint> ImageBaseNameHashes => imageBaseNameHashes ??= CreateHashSet(imageBaseNameHashArray);

        public ISet<uint> MovieBaseNameHashes => movieBaseNameHashes ??= CreateHashSet(movieBaseNameHashArray);

        public ISet<uint> AudioRelativePathHashes => audioRelativePathHashes ??= CreateHashSet(audioRelativePathHashArray);

        public ISet<uint> ImageRelativePathHashes => imageRelativePathHashes ??= CreateHashSet(imageRelativePathHashArray);

        public ISet<uint> MovieRelativePathHashes => movieRelativePathHashes ??= CreateHashSet(movieRelativePathHashArray);

        public ISet<uint> SelfOwnedAllBaseNameHashes => selfOwnedAllBaseNameHashes ??= CreateHashSet(selfOwnedAllBaseNameHashArray);

        public ISet<uint> SelfOwnedAudioBaseNameHashes => selfOwnedAudioBaseNameHashes ??= CreateHashSet(selfOwnedAudioBaseNameHashArray);

        public ISet<uint> SelfOwnedImageBaseNameHashes => selfOwnedImageBaseNameHashes ??= CreateHashSet(selfOwnedImageBaseNameHashArray);

        public ISet<uint> SelfOwnedMovieBaseNameHashes => selfOwnedMovieBaseNameHashes ??= CreateHashSet(selfOwnedMovieBaseNameHashArray);

        public ISet<uint> SelfOwnedAudioRelativePathHashes => selfOwnedAudioRelativePathHashes ??= CreateHashSet(selfOwnedAudioRelativePathHashArray);

        public ISet<uint> SelfOwnedImageRelativePathHashes => selfOwnedImageRelativePathHashes ??= CreateHashSet(selfOwnedImageRelativePathHashArray);

        public ISet<uint> SelfOwnedMovieRelativePathHashes => selfOwnedMovieRelativePathHashes ??= CreateHashSet(selfOwnedMovieRelativePathHashArray);

        public uint[] AllBaseNameHashArray => allBaseNameHashArray;

        public uint[] AudioBaseNameHashArray => audioBaseNameHashArray;

        public uint[] ImageBaseNameHashArray => imageBaseNameHashArray;

        public uint[] MovieBaseNameHashArray => movieBaseNameHashArray;

        public uint[] AudioRelativePathHashArray => audioRelativePathHashArray;

        public uint[] ImageRelativePathHashArray => imageRelativePathHashArray;

        public uint[] MovieRelativePathHashArray => movieRelativePathHashArray;

        public uint[] SelfOwnedAllBaseNameHashArray => selfOwnedAllBaseNameHashArray;

        public uint[] SelfOwnedAudioBaseNameHashArray => selfOwnedAudioBaseNameHashArray;

        public uint[] SelfOwnedImageBaseNameHashArray => selfOwnedImageBaseNameHashArray;

        public uint[] SelfOwnedMovieBaseNameHashArray => selfOwnedMovieBaseNameHashArray;

        public uint[] SelfOwnedAudioRelativePathHashArray => selfOwnedAudioRelativePathHashArray;

        public uint[] SelfOwnedImageRelativePathHashArray => selfOwnedImageRelativePathHashArray;

        public uint[] SelfOwnedMovieRelativePathHashArray => selfOwnedMovieRelativePathHashArray;

        public int FileNameHashCount => allBaseNameHashArray.Length;

        public int AudioFileNameHashCount => audioBaseNameHashArray.Length;

        public int ImageFileNameHashCount => imageBaseNameHashArray.Length;

        public int MovieFileNameHashCount => movieBaseNameHashArray.Length;

        public int SelfOwnedFileNameHashCount => selfOwnedAllBaseNameHashArray.Length;

        public Entry()
            : this(Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>())
        {
        }

        public Entry(
            IEnumerable<uint> allBaseNameHashes,
            IEnumerable<uint> audioBaseNameHashes,
            IEnumerable<uint> imageBaseNameHashes,
            IEnumerable<uint> movieBaseNameHashes,
            IEnumerable<uint> audioRelativePathHashes,
            IEnumerable<uint> imageRelativePathHashes,
            IEnumerable<uint> movieRelativePathHashes,
            IEnumerable<uint> selfOwnedAllBaseNameHashes = null,
            IEnumerable<uint> selfOwnedAudioBaseNameHashes = null,
            IEnumerable<uint> selfOwnedImageBaseNameHashes = null,
            IEnumerable<uint> selfOwnedMovieBaseNameHashes = null,
            IEnumerable<uint> selfOwnedAudioRelativePathHashes = null,
            IEnumerable<uint> selfOwnedImageRelativePathHashes = null,
            IEnumerable<uint> selfOwnedMovieRelativePathHashes = null)
        {
            allBaseNameHashArray = MaterializeHashes(allBaseNameHashes);
            audioBaseNameHashArray = MaterializeHashes(audioBaseNameHashes);
            imageBaseNameHashArray = MaterializeHashes(imageBaseNameHashes);
            movieBaseNameHashArray = MaterializeHashes(movieBaseNameHashes);
            audioRelativePathHashArray = MaterializeHashes(audioRelativePathHashes);
            imageRelativePathHashArray = MaterializeHashes(imageRelativePathHashes);
            movieRelativePathHashArray = MaterializeHashes(movieRelativePathHashes);
            selfOwnedAllBaseNameHashArray = MaterializeHashes(selfOwnedAllBaseNameHashes, allBaseNameHashArray);
            selfOwnedAudioBaseNameHashArray = MaterializeHashes(selfOwnedAudioBaseNameHashes, audioBaseNameHashArray);
            selfOwnedImageBaseNameHashArray = MaterializeHashes(selfOwnedImageBaseNameHashes, imageBaseNameHashArray);
            selfOwnedMovieBaseNameHashArray = MaterializeHashes(selfOwnedMovieBaseNameHashes, movieBaseNameHashArray);
            selfOwnedAudioRelativePathHashArray = MaterializeHashes(selfOwnedAudioRelativePathHashes, audioRelativePathHashArray);
            selfOwnedImageRelativePathHashArray = MaterializeHashes(selfOwnedImageRelativePathHashes, imageRelativePathHashArray);
            selfOwnedMovieRelativePathHashArray = MaterializeHashes(selfOwnedMovieRelativePathHashes, movieRelativePathHashArray);
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
                movieRelativePathHashArray,
                selfOwnedAllBaseNameHashArray,
                selfOwnedAudioBaseNameHashArray,
                selfOwnedImageBaseNameHashArray,
                selfOwnedMovieBaseNameHashArray,
                selfOwnedAudioRelativePathHashArray,
                selfOwnedImageRelativePathHashArray,
                selfOwnedMovieRelativePathHashArray);
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

    private readonly Dictionary<uint, string[]> directoriesByHash = new Dictionary<uint, string[]>();

    private readonly Dictionary<uint, string[]> audioDirectoriesByRelativeHash = new Dictionary<uint, string[]>();

    private readonly Dictionary<uint, string[]> imageDirectoriesByRelativeHash = new Dictionary<uint, string[]>();

    private readonly Dictionary<uint, string[]> movieDirectoriesByRelativeHash = new Dictionary<uint, string[]>();

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

    public ReverseLookupMutationResult AddDir(
        string directoryPath,
        IEnumerable<uint> allBaseNameHashes,
        IEnumerable<uint> audioBaseNameHashes,
        IEnumerable<uint> imageBaseNameHashes,
        IEnumerable<uint> movieBaseNameHashes,
        IEnumerable<uint> audioRelativePathHashes,
        IEnumerable<uint> imageRelativePathHashes,
        IEnumerable<uint> movieRelativePathHashes,
        IEnumerable<uint> selfOwnedAllBaseNameHashes = null,
        IEnumerable<uint> selfOwnedAudioBaseNameHashes = null,
        IEnumerable<uint> selfOwnedImageBaseNameHashes = null,
        IEnumerable<uint> selfOwnedMovieBaseNameHashes = null,
        IEnumerable<uint> selfOwnedAudioRelativePathHashes = null,
        IEnumerable<uint> selfOwnedImageRelativePathHashes = null,
        IEnumerable<uint> selfOwnedMovieRelativePathHashes = null)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return ReverseLookupMutationResult.Empty;
        }

        Entry entry = new Entry(
            allBaseNameHashes,
            audioBaseNameHashes,
            imageBaseNameHashes,
            movieBaseNameHashes,
            audioRelativePathHashes,
            imageRelativePathHashes,
            movieRelativePathHashes,
            selfOwnedAllBaseNameHashes,
            selfOwnedAudioBaseNameHashes,
            selfOwnedImageBaseNameHashes,
            selfOwnedMovieBaseNameHashes,
            selfOwnedAudioRelativePathHashes,
            selfOwnedImageRelativePathHashes,
            selfOwnedMovieRelativePathHashes);
        return SetEntry(directoryPath, entry);
    }

    public ReverseLookupMutationResult AddDir(string directoryPath, IEnumerable<string> fileNames)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return ReverseLookupMutationResult.Empty;
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
        return SetEntry(directoryPath, new Entry(
            allBaseNameHashes,
            audioBaseNameHashes,
            imageBaseNameHashes,
            movieBaseNameHashes,
            audioRelativePathHashes,
            imageRelativePathHashes,
            movieRelativePathHashes,
            allBaseNameHashes,
            audioBaseNameHashes,
            imageBaseNameHashes,
            movieBaseNameHashes,
            audioRelativePathHashes,
            imageRelativePathHashes,
            movieRelativePathHashes));
    }

    public ReverseLookupMutationResult AddDirHashed(string directoryPath, IEnumerable<uint> allBaseNameHashes)
    {
        return AddDir(directoryPath, allBaseNameHashes, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());
    }

    public ReverseLookupMutationResult AddDir(string directoryPath, BmsScanResult scanResult)
    {
        if (scanResult == null || string.IsNullOrWhiteSpace(directoryPath))
        {
            return ReverseLookupMutationResult.Empty;
        }
        return SetEntry(directoryPath, CreateEntry(scanResult, directoryPath));
    }

    public bool RemoveDir(string directoryPath)
    {
        return RemoveDirWithResult(directoryPath).Changed;
    }

    internal ReverseLookupMutationResult RemoveDirWithResult(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return ReverseLookupMutationResult.Empty;
        }
        Entry removedEntry = null;
        lock (lockEntries)
        {
            if (entries.TryGetValue(directoryPath, out removedEntry))
            {
                entries.Remove(directoryPath);
            }
        }
        if (removedEntry == null)
        {
            return ReverseLookupMutationResult.Empty;
        }
        return ApplyReverseLookupMutation(
            addedDirectoryPath: null,
            addedEntry: null,
            removedDirectoryPath: directoryPath,
            removedEntry: removedEntry,
            replacedDirectoryCount: 0);
    }

    public bool ReplaceDir(string oldPath, string newPath)
    {
        return ReplaceDirWithResult(oldPath, newPath).Changed;
    }

    internal ReverseLookupMutationResult ReplaceDirWithResult(string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
        {
            return ReverseLookupMutationResult.Empty;
        }
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            return ReverseLookupMutationResult.Empty;
        }
        Entry entry = null;
        Entry overwrittenEntry = null;
        lock (lockEntries)
        {
            if (!entries.TryGetValue(oldPath, out entry))
            {
                return ReverseLookupMutationResult.Empty;
            }
            entries.Remove(oldPath);
            entries.TryGetValue(newPath, out overwrittenEntry);
            entries[newPath] = entry;
        }
        ReverseLookupMutationResult result = ReverseLookupMutationResult.Empty;
        if (overwrittenEntry != null)
        {
            result = result.Combine(ApplyReverseLookupMutation(
                addedDirectoryPath: null,
                addedEntry: null,
                removedDirectoryPath: newPath,
                removedEntry: overwrittenEntry,
                replacedDirectoryCount: 0));
        }
        result = result.Combine(ApplyReverseLookupMutation(
            addedDirectoryPath: newPath,
            addedEntry: entry,
            removedDirectoryPath: oldPath,
            removedEntry: entry,
            replacedDirectoryCount: 1));
        return result;
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

    public IReadOnlyCollection<string> GetDirectoriesByAudioRelativeHash(uint relativePathHash)
    {
        if (relativePathHash == 0u)
        {
            return Array.Empty<string>();
        }
        EnsureRelativeDirectoriesByHashes(audioDirectoriesByRelativeHash, (Entry entry) => entry?.AudioRelativePathHashArray, new[] { relativePathHash });
        lock (lockLazyDirectoriesByHash)
        {
            if (audioDirectoriesByRelativeHash.TryGetValue(relativePathHash, out string[] directories))
            {
                return directories;
            }
        }
        return Array.Empty<string>();
    }

    public IReadOnlyCollection<string> GetDirectoriesByImageRelativeHash(uint relativePathHash)
    {
        if (relativePathHash == 0u)
        {
            return Array.Empty<string>();
        }
        EnsureRelativeDirectoriesByHashes(imageDirectoriesByRelativeHash, (Entry entry) => entry?.ImageRelativePathHashArray, new[] { relativePathHash });
        lock (lockLazyDirectoriesByHash)
        {
            if (imageDirectoriesByRelativeHash.TryGetValue(relativePathHash, out string[] directories))
            {
                return directories;
            }
        }
        return Array.Empty<string>();
    }

    public IReadOnlyCollection<string> GetDirectoriesByMovieRelativeHash(uint relativePathHash)
    {
        if (relativePathHash == 0u)
        {
            return Array.Empty<string>();
        }
        EnsureRelativeDirectoriesByHashes(movieDirectoriesByRelativeHash, (Entry entry) => entry?.MovieRelativePathHashArray, new[] { relativePathHash });
        lock (lockLazyDirectoriesByHash)
        {
            if (movieDirectoriesByRelativeHash.TryGetValue(relativePathHash, out string[] directories))
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

    public void EnsureAudioRelativeDirectoriesByHashes(IEnumerable<uint> hashes)
    {
        EnsureRelativeDirectoriesByHashes(audioDirectoriesByRelativeHash, (Entry entry) => entry?.AudioRelativePathHashArray, hashes);
    }

    public void EnsureImageRelativeDirectoriesByHashes(IEnumerable<uint> hashes)
    {
        EnsureRelativeDirectoriesByHashes(imageDirectoriesByRelativeHash, (Entry entry) => entry?.ImageRelativePathHashArray, hashes);
    }

    public void EnsureMovieRelativeDirectoriesByHashes(IEnumerable<uint> hashes)
    {
        EnsureRelativeDirectoriesByHashes(movieDirectoriesByRelativeHash, (Entry entry) => entry?.MovieRelativePathHashArray, hashes);
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

    private static IEnumerable<uint> TryGetHashes(Dictionary<string, uint[]> hashesByDirectory, string directoryPath, Dictionary<string, uint[]> fallbackHashesByDirectory)
    {
        if (hashesByDirectory != null && hashesByDirectory.TryGetValue(directoryPath, out uint[] hashes) && hashes != null && hashes.Length > 0)
        {
            return hashes;
        }
        return TryGetHashes(fallbackHashesByDirectory, directoryPath);
    }

    private ReverseLookupMutationResult SetEntry(string directoryPath, Entry entry)
    {
        Entry oldEntry = null;
        lock (lockEntries)
        {
            entries.TryGetValue(directoryPath, out oldEntry);
            entries[directoryPath] = entry ?? new Entry();
        }
        return ApplyReverseLookupMutation(
            addedDirectoryPath: directoryPath,
            addedEntry: entry ?? new Entry(),
            removedDirectoryPath: oldEntry == null ? null : directoryPath,
            removedEntry: oldEntry,
            replacedDirectoryCount: 0);
    }

    private void InvalidateLazyReverseLookupCache()
    {
        lock (lockLazyDirectoriesByHash)
        {
            directoriesByHash.Clear();
            audioDirectoriesByRelativeHash.Clear();
            imageDirectoriesByRelativeHash.Clear();
            movieDirectoriesByRelativeHash.Clear();
            warmupState = null;
            isFullReverseLookupBuilt = false;
            warmupVersion++;
        }
        Interlocked.Exchange(ref lazyHashBuildMs, 0L);
        Interlocked.Exchange(ref lazyHashLookupCount, 0L);
    }

    private ReverseLookupMutationResult ApplyReverseLookupMutation(
        string addedDirectoryPath,
        Entry addedEntry,
        string removedDirectoryPath,
        Entry removedEntry,
        int replacedDirectoryCount)
    {
        bool hasAdded = !string.IsNullOrWhiteSpace(addedDirectoryPath) && addedEntry != null;
        bool hasRemoved = !string.IsNullOrWhiteSpace(removedDirectoryPath) && removedEntry != null;
        if (!hasAdded && !hasRemoved && replacedDirectoryCount <= 0)
        {
            return ReverseLookupMutationResult.Empty;
        }

        int updatedHashCount = 0;
        bool cancelledWarmup;
        bool maintainedFullReverseLookup;
        bool requiresDeferredWarmup;
        lock (lockLazyDirectoriesByHash)
        {
            cancelledWarmup = warmupState != null;
            if (cancelledWarmup)
            {
                warmupState = null;
            }

            if (hasRemoved)
            {
                updatedHashCount += RemoveDirectoryFromCachedHashes(directoriesByHash, removedDirectoryPath, removedEntry.AllBaseNameHashArray);
                updatedHashCount += RemoveDirectoryFromCachedHashes(audioDirectoriesByRelativeHash, removedDirectoryPath, removedEntry.AudioRelativePathHashArray);
                updatedHashCount += RemoveDirectoryFromCachedHashes(imageDirectoriesByRelativeHash, removedDirectoryPath, removedEntry.ImageRelativePathHashArray);
                updatedHashCount += RemoveDirectoryFromCachedHashes(movieDirectoriesByRelativeHash, removedDirectoryPath, removedEntry.MovieRelativePathHashArray);
            }

            if (hasAdded)
            {
                updatedHashCount += AddDirectoryToCachedHashes(directoriesByHash, addedDirectoryPath, addedEntry.AllBaseNameHashArray, addMissingKeys: isFullReverseLookupBuilt);
                updatedHashCount += AddDirectoryToCachedHashes(audioDirectoriesByRelativeHash, addedDirectoryPath, addedEntry.AudioRelativePathHashArray, addMissingKeys: false);
                updatedHashCount += AddDirectoryToCachedHashes(imageDirectoriesByRelativeHash, addedDirectoryPath, addedEntry.ImageRelativePathHashArray, addMissingKeys: false);
                updatedHashCount += AddDirectoryToCachedHashes(movieDirectoriesByRelativeHash, addedDirectoryPath, addedEntry.MovieRelativePathHashArray, addMissingKeys: false);
            }

            maintainedFullReverseLookup = isFullReverseLookupBuilt;
            requiresDeferredWarmup = !isFullReverseLookupBuilt;
            warmupVersion++;
        }

        return new ReverseLookupMutationResult(
            changed: true,
            addedDirectoryCount: hasAdded ? 1 : 0,
            removedDirectoryCount: hasRemoved ? 1 : 0,
            replacedDirectoryCount: replacedDirectoryCount,
            updatedHashCount: updatedHashCount,
            cancelledWarmup: cancelledWarmup,
            maintainedFullReverseLookup: maintainedFullReverseLookup,
            requiresDeferredWarmup: requiresDeferredWarmup);
    }

    private static int AddDirectoryToCachedHashes(Dictionary<uint, string[]> directoriesByTargetHash, string directoryPath, IEnumerable<uint> hashes, bool addMissingKeys)
    {
        int updatedHashCount = 0;
        foreach (uint hash in EnumerateLookupHashes(hashes))
        {
            if (directoriesByTargetHash.TryGetValue(hash, out string[] directories))
            {
                if (directories.Any((string dir) => string.Equals(dir, directoryPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                directoriesByTargetHash[hash] = directories
                    .Concat(new[] { directoryPath })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                updatedHashCount++;
            }
            else if (addMissingKeys)
            {
                directoriesByTargetHash[hash] = new[] { directoryPath };
                updatedHashCount++;
            }
        }
        return updatedHashCount;
    }

    private static int RemoveDirectoryFromCachedHashes(Dictionary<uint, string[]> directoriesByTargetHash, string directoryPath, IEnumerable<uint> hashes)
    {
        int updatedHashCount = 0;
        foreach (uint hash in EnumerateLookupHashes(hashes))
        {
            if (!directoriesByTargetHash.TryGetValue(hash, out string[] directories))
            {
                continue;
            }
            string[] nextDirectories = directories
                .Where((string dir) => !string.Equals(dir, directoryPath, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (nextDirectories.Length == directories.Length)
            {
                continue;
            }
            directoriesByTargetHash[hash] = nextDirectories;
            updatedHashCount++;
        }
        return updatedHashCount;
    }

    private static IEnumerable<uint> EnumerateLookupHashes(IEnumerable<uint> hashes)
    {
        return hashes?
            .Where((uint hash) => hash != 0u)
            .Distinct() ?? Enumerable.Empty<uint>();
    }

    private void EnsureRelativeDirectoriesByHashes(Dictionary<uint, string[]> targetDirectoriesByHash, Func<Entry, uint[]> hashArraySelector, IEnumerable<uint> hashes)
    {
        uint[] requestedHashes = hashes?
            .Where((uint hash) => hash != 0u)
            .Distinct()
            .ToArray() ?? Array.Empty<uint>();
        if (requestedHashes.Length == 0)
        {
            return;
        }

        HashSet<uint> missingHashes = new HashSet<uint>(requestedHashes);
        lock (lockLazyDirectoriesByHash)
        {
            missingHashes.RemoveWhere((uint hash) => targetDirectoriesByHash.ContainsKey(hash));
        }
        if (missingHashes.Count == 0)
        {
            return;
        }

        KeyValuePair<string, Entry>[] entrySnapshot = SnapshotEntries();
        Dictionary<uint, List<string>> builtDirectories = new Dictionary<uint, List<string>>();
        foreach (KeyValuePair<string, Entry> entryPair in entrySnapshot)
        {
            uint[] relativeHashes = hashArraySelector?.Invoke(entryPair.Value);
            if (relativeHashes == null || relativeHashes.Length == 0)
            {
                continue;
            }
            foreach (uint hash in relativeHashes)
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

        lock (lockLazyDirectoriesByHash)
        {
            foreach (uint hash in missingHashes)
            {
                if (targetDirectoriesByHash.ContainsKey(hash))
                {
                    continue;
                }
                if (builtDirectories.TryGetValue(hash, out List<string> directories))
                {
                    targetDirectoriesByHash[hash] = directories
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                else
                {
                    targetDirectoriesByHash[hash] = Array.Empty<string>();
                }
            }
        }
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
            TryGetHashes(scanResult?.MovieRelativePathHashesByChartDirectory, directoryPath),
            TryGetHashes(scanResult?.SelfOwnedAllResourceBaseNameHashesByChartDirectory, directoryPath, scanResult?.AllResourceBaseNameHashesByChartDirectory),
            TryGetHashes(scanResult?.SelfOwnedAudioBaseNameHashesByChartDirectory, directoryPath, scanResult?.AudioBaseNameHashesByChartDirectory),
            TryGetHashes(scanResult?.SelfOwnedImageBaseNameHashesByChartDirectory, directoryPath, scanResult?.ImageBaseNameHashesByChartDirectory),
            TryGetHashes(scanResult?.SelfOwnedMovieBaseNameHashesByChartDirectory, directoryPath, scanResult?.MovieBaseNameHashesByChartDirectory),
            TryGetHashes(scanResult?.SelfOwnedAudioRelativePathHashesByChartDirectory, directoryPath, scanResult?.AudioRelativePathHashesByChartDirectory),
            TryGetHashes(scanResult?.SelfOwnedImageRelativePathHashesByChartDirectory, directoryPath, scanResult?.ImageRelativePathHashesByChartDirectory),
            TryGetHashes(scanResult?.SelfOwnedMovieRelativePathHashesByChartDirectory, directoryPath, scanResult?.MovieRelativePathHashesByChartDirectory));
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
