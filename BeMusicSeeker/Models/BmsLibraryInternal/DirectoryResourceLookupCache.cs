using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class DirectoryResourceLookupCache
{
    internal readonly struct ReverseLookupMutationResult(
        bool changed,
        int addedDirectoryCount,
        int removedDirectoryCount,
        int replacedDirectoryCount,
        int updatedHashCount,
        bool cancelledWarmup,
        bool maintainedFullReverseLookup,
        bool requiresDeferredWarmup)
    {
        public static ReverseLookupMutationResult Empty => new(
            changed: false,
            addedDirectoryCount: 0,
            removedDirectoryCount: 0,
            replacedDirectoryCount: 0,
            updatedHashCount: 0,
            cancelledWarmup: false,
            maintainedFullReverseLookup: false,
            requiresDeferredWarmup: false);

        public bool Changed { get; } = changed;

        public int AddedDirectoryCount { get; } = addedDirectoryCount;

        public int RemovedDirectoryCount { get; } = removedDirectoryCount;

        public int ReplacedDirectoryCount { get; } = replacedDirectoryCount;

        public int UpdatedHashCount { get; } = updatedHashCount;

        public bool CancelledWarmup { get; } = cancelledWarmup;

        public bool MaintainedFullReverseLookup { get; } = maintainedFullReverseLookup;

        public bool RequiresDeferredWarmup { get; } = requiresDeferredWarmup;

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

    internal sealed class Entry
    {
        private readonly uint[] audioRelativePathHashArray;

        private readonly uint[] imageRelativePathHashArray;

        private readonly uint[] movieRelativePathHashArray;

        private readonly uint[] selfOwnedAudioRelativePathHashArray;

        private readonly uint[] selfOwnedImageRelativePathHashArray;

        private readonly uint[] selfOwnedMovieRelativePathHashArray;

        private ISet<uint> audioRelativePathHashes;

        private ISet<uint> imageRelativePathHashes;

        private ISet<uint> movieRelativePathHashes;

        private ISet<uint> selfOwnedAudioRelativePathHashes;

        private ISet<uint> selfOwnedImageRelativePathHashes;

        private ISet<uint> selfOwnedMovieRelativePathHashes;

        public ISet<uint> AudioRelativePathHashes => GetOrCreateReadOnlySet(ref audioRelativePathHashes, audioRelativePathHashArray);

        public ISet<uint> ImageRelativePathHashes => GetOrCreateReadOnlySet(ref imageRelativePathHashes, imageRelativePathHashArray);

        public ISet<uint> MovieRelativePathHashes => GetOrCreateReadOnlySet(ref movieRelativePathHashes, movieRelativePathHashArray);

        public ISet<uint> SelfOwnedAudioRelativePathHashes => GetOrCreateReadOnlySet(ref selfOwnedAudioRelativePathHashes, selfOwnedAudioRelativePathHashArray);

        public ISet<uint> SelfOwnedImageRelativePathHashes => GetOrCreateReadOnlySet(ref selfOwnedImageRelativePathHashes, selfOwnedImageRelativePathHashArray);

        public ISet<uint> SelfOwnedMovieRelativePathHashes => GetOrCreateReadOnlySet(ref selfOwnedMovieRelativePathHashes, selfOwnedMovieRelativePathHashArray);

        public uint[] AudioRelativePathHashArray => [.. audioRelativePathHashArray];

        public uint[] ImageRelativePathHashArray => [.. imageRelativePathHashArray];

        public uint[] MovieRelativePathHashArray => [.. movieRelativePathHashArray];

        public uint[] SelfOwnedAudioRelativePathHashArray => [.. selfOwnedAudioRelativePathHashArray];

        public uint[] SelfOwnedImageRelativePathHashArray => [.. selfOwnedImageRelativePathHashArray];

        public uint[] SelfOwnedMovieRelativePathHashArray => [.. selfOwnedMovieRelativePathHashArray];

        public int AudioFileNameHashCount => audioRelativePathHashArray.Length;

        public int ImageFileNameHashCount => imageRelativePathHashArray.Length;

        public int MovieFileNameHashCount => movieRelativePathHashArray.Length;

        public bool ContainsAudioRelativePathHash(uint hash)
        {
            return ContainsSortedHash(audioRelativePathHashArray, hash);
        }

        public bool ContainsImageRelativePathHash(uint hash)
        {
            return ContainsSortedHash(imageRelativePathHashArray, hash);
        }

        public bool ContainsMovieRelativePathHash(uint hash)
        {
            return ContainsSortedHash(movieRelativePathHashArray, hash);
        }

        /// <summary>
        /// Enumerates audio hashes without exposing the immutable backing array.
        /// </summary>
        internal IEnumerable<uint> EnumerateAudioRelativePathHashes()
        {
            foreach (uint hash in audioRelativePathHashArray)
            {
                yield return hash;
            }
        }

        /// <summary>
        /// Enumerates image hashes without exposing the immutable backing array.
        /// </summary>
        internal IEnumerable<uint> EnumerateImageRelativePathHashes()
        {
            foreach (uint hash in imageRelativePathHashArray)
            {
                yield return hash;
            }
        }

        /// <summary>
        /// Enumerates movie hashes without exposing the immutable backing array.
        /// </summary>
        internal IEnumerable<uint> EnumerateMovieRelativePathHashes()
        {
            foreach (uint hash in movieRelativePathHashArray)
            {
                yield return hash;
            }
        }

        public Entry()
            : this([], [], [], [], [], [])
        {
        }

        public Entry(
            IEnumerable<uint> audioRelativePathHashes,
            IEnumerable<uint> imageRelativePathHashes,
            IEnumerable<uint> movieRelativePathHashes,
            IEnumerable<uint> selfOwnedAudioRelativePathHashes = null,
            IEnumerable<uint> selfOwnedImageRelativePathHashes = null,
            IEnumerable<uint> selfOwnedMovieRelativePathHashes = null)
            : this(
                audioRelativePathHashes,
                imageRelativePathHashes,
                movieRelativePathHashes,
                selfOwnedAudioRelativePathHashes,
                selfOwnedImageRelativePathHashes,
                selfOwnedMovieRelativePathHashes,
                trustSortedDistinctArrays: false,
                takeOwnership: false)
        {
        }

        private Entry(
            IEnumerable<uint> audioRelativePathHashes,
            IEnumerable<uint> imageRelativePathHashes,
            IEnumerable<uint> movieRelativePathHashes,
            IEnumerable<uint> selfOwnedAudioRelativePathHashes,
            IEnumerable<uint> selfOwnedImageRelativePathHashes,
            IEnumerable<uint> selfOwnedMovieRelativePathHashes,
            bool trustSortedDistinctArrays,
            bool takeOwnership)
        {
            audioRelativePathHashArray = MaterializeHashes(audioRelativePathHashes, trustSortedDistinctArrays, takeOwnership);
            imageRelativePathHashArray = MaterializeHashes(imageRelativePathHashes, trustSortedDistinctArrays, takeOwnership);
            movieRelativePathHashArray = MaterializeHashes(movieRelativePathHashes, trustSortedDistinctArrays, takeOwnership);
            selfOwnedAudioRelativePathHashArray = selfOwnedAudioRelativePathHashes == null
                ? audioRelativePathHashArray
                : MaterializeHashes(selfOwnedAudioRelativePathHashes, trustSortedDistinctArrays, takeOwnership);
            selfOwnedImageRelativePathHashArray = selfOwnedImageRelativePathHashes == null
                ? imageRelativePathHashArray
                : MaterializeHashes(selfOwnedImageRelativePathHashes, trustSortedDistinctArrays, takeOwnership);
            selfOwnedMovieRelativePathHashArray = selfOwnedMovieRelativePathHashes == null
                ? movieRelativePathHashArray
                : MaterializeHashes(selfOwnedMovieRelativePathHashes, trustSortedDistinctArrays, takeOwnership);
        }

        internal static Entry FromSortedDistinctArrays(
            IEnumerable<uint> audioRelativePathHashes,
            IEnumerable<uint> imageRelativePathHashes,
            IEnumerable<uint> movieRelativePathHashes,
            IEnumerable<uint> selfOwnedAudioRelativePathHashes = null,
            IEnumerable<uint> selfOwnedImageRelativePathHashes = null,
            IEnumerable<uint> selfOwnedMovieRelativePathHashes = null,
            bool takeOwnership = false)
        {
            return new Entry(
                audioRelativePathHashes,
                imageRelativePathHashes,
                movieRelativePathHashes,
                selfOwnedAudioRelativePathHashes,
                selfOwnedImageRelativePathHashes,
                selfOwnedMovieRelativePathHashes,
                trustSortedDistinctArrays: true,
                takeOwnership);
        }

        public Entry Clone()
        {
            return new Entry(
                audioRelativePathHashArray,
                imageRelativePathHashArray,
                movieRelativePathHashArray,
                selfOwnedAudioRelativePathHashArray,
                selfOwnedImageRelativePathHashArray,
                selfOwnedMovieRelativePathHashArray);
        }

        private static uint[] MaterializeHashes(
            IEnumerable<uint> hashes,
            bool trustSortedDistinctArrays = false,
            bool takeOwnership = false)
        {
            if (hashes == null)
            {
                return [];
            }
            uint[] hashArray = hashes as uint[] ?? [.. hashes];
            if (trustSortedDistinctArrays)
            {
                return takeOwnership ? hashArray : [.. hashArray];
            }
            if (hashArray.Length <= 1)
            {
                return takeOwnership ? hashArray : [.. hashArray];
            }
            uint[] sorted = [.. hashArray.Distinct()];
            Array.Sort(sorted);
            return sorted;
        }

        internal bool SemanticallyEquals(Entry other)
        {
            return other != null
                && audioRelativePathHashArray.SequenceEqual(other.audioRelativePathHashArray)
                && imageRelativePathHashArray.SequenceEqual(other.imageRelativePathHashArray)
                && movieRelativePathHashArray.SequenceEqual(other.movieRelativePathHashArray)
                && selfOwnedAudioRelativePathHashArray.SequenceEqual(other.selfOwnedAudioRelativePathHashArray)
                && selfOwnedImageRelativePathHashArray.SequenceEqual(other.selfOwnedImageRelativePathHashArray)
                && selfOwnedMovieRelativePathHashArray.SequenceEqual(other.selfOwnedMovieRelativePathHashArray);
        }

        private static HashSet<uint> CreateHashSet(IEnumerable<uint> hashes)
        {
            return hashes == null ? [] : [.. hashes];
        }

        private static ISet<uint> GetOrCreateReadOnlySet(ref ISet<uint> target, uint[] hashes)
        {
            ISet<uint> current = Volatile.Read(ref target);
            if (current != null)
            {
                return current;
            }
            ISet<uint> created = new ReadOnlySet(CreateHashSet(hashes));
            return Interlocked.CompareExchange(ref target, created, null) ?? created;
        }

        private sealed class ReadOnlySet(HashSet<uint> values) : ISet<uint>
        {
            public int Count => values.Count;
            public bool IsReadOnly => true;
            bool ISet<uint>.Add(uint item) => throw new NotSupportedException();
            void ICollection<uint>.Add(uint item) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public bool Contains(uint item) => values.Contains(item);
            public void CopyTo(uint[] array, int arrayIndex) => values.CopyTo(array, arrayIndex);
            public void ExceptWith(IEnumerable<uint> other) => throw new NotSupportedException();
            public IEnumerator<uint> GetEnumerator() => values.GetEnumerator();
            public void IntersectWith(IEnumerable<uint> other) => throw new NotSupportedException();
            public bool IsProperSubsetOf(IEnumerable<uint> other) => values.IsProperSubsetOf(other);
            public bool IsProperSupersetOf(IEnumerable<uint> other) => values.IsProperSupersetOf(other);
            public bool IsSubsetOf(IEnumerable<uint> other) => values.IsSubsetOf(other);
            public bool IsSupersetOf(IEnumerable<uint> other) => values.IsSupersetOf(other);
            public bool Overlaps(IEnumerable<uint> other) => values.Overlaps(other);
            public bool Remove(uint item) => throw new NotSupportedException();
            public bool SetEquals(IEnumerable<uint> other) => values.SetEquals(other);
            public void SymmetricExceptWith(IEnumerable<uint> other) => throw new NotSupportedException();
            public void UnionWith(IEnumerable<uint> other) => throw new NotSupportedException();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private static bool ContainsSortedHash(uint[] hashes, uint hash)
        {
            return hash != 0u && hashes != null && Array.BinarySearch(hashes, hash) >= 0;
        }
    }

    private readonly object lockEntries = new();

    private Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

    private bool entriesRootShared;

    private ResourceReverseLookupMap audioDirectoriesByRelativeHash;

    private ResourceReverseLookupMap imageDirectoriesByRelativeHash;

    private ResourceReverseLookupMap movieDirectoriesByRelativeHash;

    private readonly object lockLazyDirectoriesByHash = new();

    private long lazyHashBuildMs;

    private long lazyHashLookupCount;

    private bool isFullReverseLookupBuilt;

    // Test-only synchronization seam. It is intentionally private so production callers cannot
    // coordinate or delay lookup publication.
    private Action lazyReverseLookupEntrySnapshotCapturedObserver = null;

    /// <summary>
    /// Observes actual directory-root copies in deterministic work-contract tests.
    /// Production leaves this unset; callbacks must not reenter the cache or throw.
    /// </summary>
    internal Action<int> EntriesRootCopiedObserver { get; set; }

    /// <summary>
    /// Observes actual reverse-bucket writes, not attempted or simulated changes.
    /// Production leaves this unset; callbacks must not reenter the cache or throw.
    /// </summary>
    internal Action<ChartResourceKind, uint> ReverseBucketWrittenObserver { get; set; }

    public DirectoryResourceLookupCache()
        : this(
            new ResourceReverseLookupMap(),
            new ResourceReverseLookupMap(),
            new ResourceReverseLookupMap())
    {
    }

    private DirectoryResourceLookupCache(
        ResourceReverseLookupMap audioDirectoriesByRelativeHash,
        ResourceReverseLookupMap imageDirectoriesByRelativeHash,
        ResourceReverseLookupMap movieDirectoriesByRelativeHash)
    {
        this.audioDirectoriesByRelativeHash = audioDirectoriesByRelativeHash;
        this.imageDirectoriesByRelativeHash = imageDirectoriesByRelativeHash;
        this.movieDirectoriesByRelativeHash = movieDirectoriesByRelativeHash;
    }

    /// <summary>
    /// Creates an unpublished structural copy of the current directory and reverse-lookup state.
    /// </summary>
    /// <remarks>
    /// Entries use root-level copy-on-write. Reverse lookups share an owned scan base and
    /// immutable change nodes; writes replace only affected buckets and change-tree paths.
    /// A fork never enumerates or copies the complete reverse lookup.
    /// </remarks>
    internal DirectoryResourceLookupCache CloneForMutation()
    {
        Dictionary<string, Entry> sharedEntries;
        lock (lockEntries)
        {
            entriesRootShared = true;
            sharedEntries = entries;
        }

        ResourceReverseLookupMap sharedAudioReverseLookup;
        ResourceReverseLookupMap sharedImageReverseLookup;
        ResourceReverseLookupMap sharedMovieReverseLookup;
        bool fullReverseLookupBuilt;
        lock (lockLazyDirectoriesByHash)
        {
            sharedAudioReverseLookup = audioDirectoriesByRelativeHash.Fork();
            sharedImageReverseLookup = imageDirectoriesByRelativeHash.Fork();
            sharedMovieReverseLookup = movieDirectoriesByRelativeHash.Fork();
            fullReverseLookupBuilt = isFullReverseLookupBuilt;
        }

        var clone = new DirectoryResourceLookupCache(
            sharedAudioReverseLookup,
            sharedImageReverseLookup,
            sharedMovieReverseLookup)
        {
            entries = sharedEntries,
            entriesRootShared = true,
            isFullReverseLookupBuilt = fullReverseLookupBuilt,
            lazyHashBuildMs = Interlocked.Read(ref lazyHashBuildMs),
            lazyHashLookupCount = Interlocked.Read(ref lazyHashLookupCount),
            EntriesRootCopiedObserver = EntriesRootCopiedObserver,
            ReverseBucketWrittenObserver = ReverseBucketWrittenObserver
        };
        return clone;
    }

    public IEnumerable<string> Keys
    {
        get
        {
            lock (lockEntries)
            {
                return [.. entries.Keys];
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

    public int LazyHashCacheEntryCount
    {
        get
        {
            return 0;
        }
    }

    public int CategoryReverseLookupEntryCount
    {
        get
        {
            lock (lockLazyDirectoriesByHash)
            {
                return audioDirectoriesByRelativeHash.Count + imageDirectoriesByRelativeHash.Count + movieDirectoriesByRelativeHash.Count;
            }
        }
    }

    public static DirectoryResourceLookupCache CreateFromScanResult(ChartScanResult scanResult)
    {
        var cache = new DirectoryResourceLookupCache();
        foreach (string chartDirectory in scanResult?.ChartDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        {
            cache.SetEntry(chartDirectory, CreateEntry(scanResult, chartDirectory));
        }
        return cache;
    }

    public static DirectoryResourceLookupCache CreateFromNativeCanonicalArrays(
        string[] chartDirectories,
        uint[][] audioRelativePathHashesByDirectoryIndex,
        uint[][] imageRelativePathHashesByDirectoryIndex,
        uint[][] movieRelativePathHashesByDirectoryIndex,
        uint[][] selfOwnedAudioRelativePathHashesByDirectoryIndex,
        uint[][] selfOwnedImageRelativePathHashesByDirectoryIndex,
        uint[][] selfOwnedMovieRelativePathHashesByDirectoryIndex,
        Dictionary<uint, string[]> audioRelativeReverseDirectories,
        Dictionary<uint, string[]> imageRelativeReverseDirectories,
        Dictionary<uint, string[]> movieRelativeReverseDirectories)
    {
        var cache = new DirectoryResourceLookupCache(
            new ResourceReverseLookupMap(PrepareNativeReverseMap(audioRelativeReverseDirectories)),
            new ResourceReverseLookupMap(PrepareNativeReverseMap(imageRelativeReverseDirectories)),
            new ResourceReverseLookupMap(PrepareNativeReverseMap(movieRelativeReverseDirectories)));
        int count = chartDirectories?.Length ?? 0;
        for (int i = 0; i < count; i++)
        {
            string chartDirectory = chartDirectories[i];
            if (string.IsNullOrWhiteSpace(chartDirectory))
            {
                continue;
            }
            cache.entries[chartDirectory] = Entry.FromSortedDistinctArrays(
                GetNativeHashes(audioRelativePathHashesByDirectoryIndex, i),
                GetNativeHashes(imageRelativePathHashesByDirectoryIndex, i),
                GetNativeHashes(movieRelativePathHashesByDirectoryIndex, i),
                GetNativeHashes(selfOwnedAudioRelativePathHashesByDirectoryIndex, i),
                GetNativeHashes(selfOwnedImageRelativePathHashesByDirectoryIndex, i),
                GetNativeHashes(selfOwnedMovieRelativePathHashesByDirectoryIndex, i),
                takeOwnership: true);
        }
        cache.isFullReverseLookupBuilt = cache.CategoryReverseLookupEntryCount > 0;
        return cache;
    }

    public ReverseLookupMutationResult AddDir(
        string directoryPath,
        IEnumerable<uint> audioRelativePathHashes,
        IEnumerable<uint> imageRelativePathHashes,
        IEnumerable<uint> movieRelativePathHashes,
        IEnumerable<uint> selfOwnedAudioRelativePathHashes = null,
        IEnumerable<uint> selfOwnedImageRelativePathHashes = null,
        IEnumerable<uint> selfOwnedMovieRelativePathHashes = null)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return ReverseLookupMutationResult.Empty;
        }

        var entry = new Entry(
            audioRelativePathHashes,
            imageRelativePathHashes,
            movieRelativePathHashes,
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

        HashSet<uint> audioRelativePathHashes = [];
        HashSet<uint> imageRelativePathHashes = [];
        HashSet<uint> movieRelativePathHashes = [];
        foreach (string fileName in fileNames ?? [])
        {
            string normalizedPath = ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(fileName);
            string normalizedFileName = ChartResourcePathNormalizer.NormalizeFileNameForLookup(fileName);
            if (string.IsNullOrWhiteSpace(normalizedFileName))
            {
                continue;
            }
            uint resourceKeyHash = ChartResourceKeyHash.GetLookupHash(
                string.IsNullOrWhiteSpace(normalizedPath) ? normalizedFileName : normalizedPath);
            switch (ChartResourcePathNormalizer.ClassifyPath(fileName))
            {
                case ChartResourceKind.Audio:
                    if (!string.IsNullOrWhiteSpace(normalizedPath))
                    {
                        audioRelativePathHashes.Add(resourceKeyHash);
                    }
                    break;
                case ChartResourceKind.Image:
                    if (!string.IsNullOrWhiteSpace(normalizedPath))
                    {
                        imageRelativePathHashes.Add(resourceKeyHash);
                    }
                    break;
                case ChartResourceKind.Movie:
                    if (!string.IsNullOrWhiteSpace(normalizedPath))
                    {
                        movieRelativePathHashes.Add(resourceKeyHash);
                    }
                    break;
            }
        }
        return SetEntry(directoryPath, new Entry(
            audioRelativePathHashes,
            imageRelativePathHashes,
            movieRelativePathHashes,
            audioRelativePathHashes,
            imageRelativePathHashes,
            movieRelativePathHashes));
    }

    public ReverseLookupMutationResult AddDir(string directoryPath, ChartScanResult scanResult)
    {
        if (scanResult == null || string.IsNullOrWhiteSpace(directoryPath))
        {
            return ReverseLookupMutationResult.Empty;
        }
        return SetEntry(directoryPath, CreateEntry(scanResult, directoryPath));
    }

    internal ReverseLookupMutationResult RemoveUnderSourceDirectory(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return ReverseLookupMutationResult.Empty;
        }

        ReverseLookupMutationResult mutation = ReverseLookupMutationResult.Empty;
        List<string> removedDirectories = [.. Keys
            .Where(path => (path + Path.DirectorySeparatorChar).StartsWith(
                sourceDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))];
        foreach (string directory in removedDirectories)
        {
            mutation = mutation.Combine(RemoveDirWithResult(directory));
        }
        return mutation;
    }

    internal ReverseLookupMutationResult AddScanDirectories(ChartScanResult scan)
    {
        ReverseLookupMutationResult mutation = ReverseLookupMutationResult.Empty;
        foreach (string chartDirectory in scan?.ChartDirectories ?? [])
        {
            mutation = mutation.Combine(AddDir(chartDirectory, scan));
        }
        return mutation;
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
                EnsureEntriesRootWritableUnsafe();
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
            EnsureEntriesRootWritableUnsafe();
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

    internal ReverseLookupMutationResult ReplaceDirsWithResult(IEnumerable<KeyValuePair<string, string>> pathReplacements)
    {
        List<KeyValuePair<string, string>> replacements = [.. (pathReplacements ?? [])
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                && !string.IsNullOrWhiteSpace(pair.Value)
                && !string.Equals(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())];
        if (replacements.Count == 0)
        {
            return ReverseLookupMutationResult.Empty;
        }

        Dictionary<string, string> replacementsByOldPath = replacements.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        int replacedDirectoryCount = 0;
        int overwrittenDirectoryCount = 0;
        bool hasExternalOverwrite = false;
        var entriesToMove = new List<Tuple<string, string, Entry>>();
        lock (lockEntries)
        {
            foreach (KeyValuePair<string, string> replacement in replacements)
            {
                if (!entries.TryGetValue(replacement.Key, out Entry entry))
                {
                    continue;
                }
                entriesToMove.Add(Tuple.Create(replacement.Key, replacement.Value, entry));
            }
            foreach (Tuple<string, string, Entry> entryToMove in entriesToMove)
            {
                EnsureEntriesRootWritableUnsafe();
                entries.Remove(entryToMove.Item1);
            }
            foreach (Tuple<string, string, Entry> entryToMove in entriesToMove)
            {
                if (entries.ContainsKey(entryToMove.Item2))
                {
                    overwrittenDirectoryCount++;
                    hasExternalOverwrite = true;
                }
                entries[entryToMove.Item2] = entryToMove.Item3;
                replacedDirectoryCount++;
            }
        }
        if (replacedDirectoryCount == 0)
        {
            return ReverseLookupMutationResult.Empty;
        }

        if (hasExternalOverwrite)
        {
            bool wasFullReverseLookupBuilt = IsFullReverseLookupBuilt;
            InvalidateLazyReverseLookupCache();
            return new ReverseLookupMutationResult(
                changed: true,
                addedDirectoryCount: overwrittenDirectoryCount,
                removedDirectoryCount: overwrittenDirectoryCount,
                replacedDirectoryCount: replacedDirectoryCount,
                updatedHashCount: 0,
                cancelledWarmup: false,
                maintainedFullReverseLookup: false,
                requiresDeferredWarmup: wasFullReverseLookupBuilt);
        }

        int updatedHashCount = 0;
        bool maintainedFullReverseLookup;
        bool requiresDeferredWarmup;
        lock (lockLazyDirectoriesByHash)
        {
            updatedHashCount += RewriteCachedDirectoryPaths(
                ReverseLookupCategory.Audio, replacementsByOldPath);
            updatedHashCount += RewriteCachedDirectoryPaths(
                ReverseLookupCategory.Image, replacementsByOldPath);
            updatedHashCount += RewriteCachedDirectoryPaths(
                ReverseLookupCategory.Movie, replacementsByOldPath);
            maintainedFullReverseLookup = isFullReverseLookupBuilt;
            requiresDeferredWarmup = false;
        }

        return new ReverseLookupMutationResult(
            changed: true,
            addedDirectoryCount: overwrittenDirectoryCount,
            removedDirectoryCount: overwrittenDirectoryCount,
            replacedDirectoryCount: replacedDirectoryCount,
            updatedHashCount: updatedHashCount,
            cancelledWarmup: false,
            maintainedFullReverseLookup: maintainedFullReverseLookup,
            requiresDeferredWarmup: requiresDeferredWarmup);
    }

    public IReadOnlyCollection<string> GetDirectoriesByAudioRelativeHash(uint relativePathHash)
    {
        if (relativePathHash == 0u)
        {
            return [];
        }
        EnsureRelativeDirectoriesByHashes(ReverseLookupCategory.Audio, entry => entry?.EnumerateAudioRelativePathHashes(), [relativePathHash]);
        lock (lockLazyDirectoriesByHash)
        {
            if (audioDirectoriesByRelativeHash.TryGetValue(relativePathHash, out string[] directories))
            {
                return Array.AsReadOnly(directories);
            }
        }
        return [];
    }

    public IReadOnlyCollection<string> GetDirectoriesByImageRelativeHash(uint relativePathHash)
    {
        if (relativePathHash == 0u)
        {
            return [];
        }
        EnsureRelativeDirectoriesByHashes(ReverseLookupCategory.Image, entry => entry?.EnumerateImageRelativePathHashes(), [relativePathHash]);
        lock (lockLazyDirectoriesByHash)
        {
            if (imageDirectoriesByRelativeHash.TryGetValue(relativePathHash, out string[] directories))
            {
                return Array.AsReadOnly(directories);
            }
        }
        return [];
    }

    public IReadOnlyCollection<string> GetDirectoriesByMovieRelativeHash(uint relativePathHash)
    {
        if (relativePathHash == 0u)
        {
            return [];
        }
        EnsureRelativeDirectoriesByHashes(ReverseLookupCategory.Movie, entry => entry?.EnumerateMovieRelativePathHashes(), [relativePathHash]);
        lock (lockLazyDirectoriesByHash)
        {
            if (movieDirectoriesByRelativeHash.TryGetValue(relativePathHash, out string[] directories))
            {
                return Array.AsReadOnly(directories);
            }
        }
        return [];
    }

    public void EnsureAudioRelativeDirectoriesByHashes(IEnumerable<uint> hashes)
    {
        EnsureRelativeDirectoriesByHashes(ReverseLookupCategory.Audio, entry => entry?.EnumerateAudioRelativePathHashes(), hashes);
    }

    public void EnsureImageRelativeDirectoriesByHashes(IEnumerable<uint> hashes)
    {
        EnsureRelativeDirectoriesByHashes(ReverseLookupCategory.Image, entry => entry?.EnumerateImageRelativePathHashes(), hashes);
    }

    public void EnsureMovieRelativeDirectoriesByHashes(IEnumerable<uint> hashes)
    {
        EnsureRelativeDirectoriesByHashes(ReverseLookupCategory.Movie, entry => entry?.EnumerateMovieRelativePathHashes(), hashes);
    }

    public bool IsFullReverseLookupBuilt
    {
        get
        {
            lock (lockLazyDirectoriesByHash)
            {
                return isFullReverseLookupBuilt;
            }
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

    /// <summary>
    /// Compares the semantic directory mappings for a bounded set of paths.
    /// </summary>
    internal bool HasSameDirectoryEntries(
        DirectoryResourceLookupCache other,
        IEnumerable<string> directoryPaths)
    {
        if (other == null)
        {
            return false;
        }
        foreach (string directoryPath in (directoryPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Entry currentEntry = GetEntryOrNull(directoryPath);
            Entry otherEntry = other.GetEntryOrNull(directoryPath);
            if (currentEntry == null || otherEntry == null)
            {
                if (!ReferenceEquals(currentEntry, otherEntry))
                {
                    return false;
                }
                continue;
            }
            if (!currentEntry.SemanticallyEquals(otherEntry))
            {
                return false;
            }
        }
        return true;
    }

    private static IEnumerable<uint> TryGetHashes(Dictionary<string, uint[]> hashesByDirectory, string directoryPath)
    {
        if (hashesByDirectory != null && hashesByDirectory.TryGetValue(directoryPath, out uint[] hashes))
        {
            return hashes ?? [];
        }
        return [];
    }

    private static Dictionary<uint, string[]> PrepareNativeReverseMap(Dictionary<uint, string[]> source)
    {
        if (source == null)
        {
            return [];
        }
        source.Remove(0u);
        return source;
    }

    private static uint[] GetNativeHashes(uint[][] hashesByDirectoryIndex, int directoryIndex)
    {
        if (hashesByDirectoryIndex == null || directoryIndex < 0 || directoryIndex >= hashesByDirectoryIndex.Length)
        {
            return [];
        }
        return hashesByDirectoryIndex[directoryIndex] ?? [];
    }

    private ReverseLookupMutationResult SetEntry(string directoryPath, Entry entry)
    {
        Entry oldEntry = null;
        lock (lockEntries)
        {
            entries.TryGetValue(directoryPath, out oldEntry);
            Entry replacement = entry ?? new Entry();
            if (oldEntry?.SemanticallyEquals(replacement) == true)
            {
                return ReverseLookupMutationResult.Empty;
            }
            EnsureEntriesRootWritableUnsafe();
            entries[directoryPath] = replacement;
        }
        return ApplyReverseLookupMutation(
            addedDirectoryPath: directoryPath,
            addedEntry: entry ?? new Entry(),
            removedDirectoryPath: oldEntry == null ? null : directoryPath,
            removedEntry: oldEntry,
            replacedDirectoryCount: 0);
    }

    private void EnsureEntriesRootWritableUnsafe()
    {
        if (!entriesRootShared)
        {
            return;
        }
        entries = new Dictionary<string, Entry>(entries, StringComparer.OrdinalIgnoreCase);
        entriesRootShared = false;
        EntriesRootCopiedObserver?.Invoke(entries.Count);
    }

    private ResourceReverseLookupMap GetReverseLookupRootUnsafe(ReverseLookupCategory category)
    {
        return category switch
        {
            ReverseLookupCategory.Audio => audioDirectoriesByRelativeHash,
            ReverseLookupCategory.Image => imageDirectoriesByRelativeHash,
            ReverseLookupCategory.Movie => movieDirectoriesByRelativeHash,
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };
    }

    private void SetReverseBucketUnsafe(ReverseLookupCategory category, uint hash, string[] directories)
    {
        if (GetReverseLookupRootUnsafe(category).Set(hash, directories) && ReverseBucketWrittenObserver != null)
        {
            ChartResourceKind kind = category switch
            {
                ReverseLookupCategory.Audio => ChartResourceKind.Audio,
                ReverseLookupCategory.Image => ChartResourceKind.Image,
                ReverseLookupCategory.Movie => ChartResourceKind.Movie,
                _ => throw new ArgumentOutOfRangeException(nameof(category))
            };
            ReverseBucketWrittenObserver?.Invoke(kind, hash);
        }
    }

    private void InvalidateLazyReverseLookupCache()
    {
        lock (lockLazyDirectoriesByHash)
        {
            audioDirectoriesByRelativeHash = new ResourceReverseLookupMap();
            imageDirectoriesByRelativeHash = new ResourceReverseLookupMap();
            movieDirectoriesByRelativeHash = new ResourceReverseLookupMap();
            isFullReverseLookupBuilt = false;
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
        bool maintainedFullReverseLookup;
        bool requiresDeferredWarmup;
        lock (lockLazyDirectoriesByHash)
        {
            updatedHashCount += ApplyDirectoryChangeToCachedHashes(
                ReverseLookupCategory.Audio,
                hasAdded ? addedDirectoryPath : null,
                hasAdded ? addedEntry.EnumerateAudioRelativePathHashes() : null,
                hasRemoved ? removedDirectoryPath : null,
                hasRemoved ? removedEntry.EnumerateAudioRelativePathHashes() : null);
            updatedHashCount += ApplyDirectoryChangeToCachedHashes(
                ReverseLookupCategory.Image,
                hasAdded ? addedDirectoryPath : null,
                hasAdded ? addedEntry.EnumerateImageRelativePathHashes() : null,
                hasRemoved ? removedDirectoryPath : null,
                hasRemoved ? removedEntry.EnumerateImageRelativePathHashes() : null);
            updatedHashCount += ApplyDirectoryChangeToCachedHashes(
                ReverseLookupCategory.Movie,
                hasAdded ? addedDirectoryPath : null,
                hasAdded ? addedEntry.EnumerateMovieRelativePathHashes() : null,
                hasRemoved ? removedDirectoryPath : null,
                hasRemoved ? removedEntry.EnumerateMovieRelativePathHashes() : null);

            maintainedFullReverseLookup = isFullReverseLookupBuilt;
            requiresDeferredWarmup = false;
        }

        return new ReverseLookupMutationResult(
            changed: true,
            addedDirectoryCount: hasAdded ? 1 : 0,
            removedDirectoryCount: hasRemoved ? 1 : 0,
            replacedDirectoryCount: replacedDirectoryCount,
            updatedHashCount: updatedHashCount,
            cancelledWarmup: false,
            maintainedFullReverseLookup: maintainedFullReverseLookup,
            requiresDeferredWarmup: requiresDeferredWarmup);
    }

    /// <summary>
    /// Seals only pending reverse-map changes before the owner publishes a completed mutation.
    /// This does not rebuild the index or change its full/lazy state.
    /// </summary>
    internal void FreezeReverseLookupChanges()
    {
        lock (lockLazyDirectoriesByHash)
        {
            audioDirectoriesByRelativeHash.FreezeChanges();
            imageDirectoriesByRelativeHash.FreezeChanges();
            movieDirectoriesByRelativeHash.FreezeChanges();
        }
    }

    private int ApplyDirectoryChangeToCachedHashes(
        ReverseLookupCategory category,
        string addedDirectoryPath,
        IEnumerable<uint> addedHashes,
        string removedDirectoryPath,
        IEnumerable<uint> removedHashes)
    {
        // Work is bounded by this directory's keys. Skip a write only when the final candidate
        // sequence, including its order, is unchanged. A SelfOwned-only edit can still move this
        // directory to the end of a shared bucket to preserve remove-then-add compatibility.
        HashSet<uint> removed = [.. EnumerateLookupHashes(removedHashes)];
        HashSet<uint> added = [.. EnumerateLookupHashes(addedHashes)];
        if (removed.Count == 0 && added.Count == 0)
        {
            return 0;
        }

        ResourceReverseLookupMap root = GetReverseLookupRootUnsafe(category);
        int updatedHashCount = 0;
        foreach (uint hash in removed.Concat(added).Distinct())
        {
            bool existed = root.TryGetValue(hash, out string[] directories);
            string[] next = directories;
            if (existed && removed.Contains(hash))
            {
                string[] remaining = [.. directories
                    .Where(dir => !string.Equals(dir, removedDirectoryPath, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
                if (remaining.Length != directories.Length)
                {
                    next = remaining;
                    updatedHashCount++;
                }
            }
            if (added.Contains(hash))
            {
                if (existed)
                {
                    if (!next.Any(dir => string.Equals(dir, addedDirectoryPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        next = [.. next.Concat([addedDirectoryPath]).Distinct(StringComparer.OrdinalIgnoreCase)];
                        updatedHashCount++;
                    }
                }
                else if (isFullReverseLookupBuilt)
                {
                    next = [addedDirectoryPath];
                    updatedHashCount++;
                }
            }
            if (next != null)
            {
                // Preserve remove-then-add candidate order and the existing transition count,
                // but do not allocate change nodes when the final candidate list is identical.
                SetReverseBucketUnsafe(category, hash, next);
            }
        }
        return updatedHashCount;
    }

    private int RewriteCachedDirectoryPaths(ReverseLookupCategory category, IReadOnlyDictionary<string, string> replacementsByOldPath)
    {
        ResourceReverseLookupMap directoriesByTargetHash = GetReverseLookupRootUnsafe(category);
        if (directoriesByTargetHash.Count == 0 || replacementsByOldPath == null || replacementsByOldPath.Count == 0)
        {
            return 0;
        }

        int updatedHashCount = 0;
        foreach (uint hash in directoriesByTargetHash.Keys.ToArray())
        {
            directoriesByTargetHash.TryGetValue(hash, out string[] directories);
            if (directories == null || directories.Length == 0)
            {
                continue;
            }

            bool changed = false;
            string[] nextDirectories = new string[directories.Length];
            for (int i = 0; i < directories.Length; i++)
            {
                string directory = directories[i];
                if (!string.IsNullOrWhiteSpace(directory)
                    && replacementsByOldPath.TryGetValue(directory, out string replacement))
                {
                    nextDirectories[i] = replacement;
                    changed = true;
                }
                else
                {
                    nextDirectories[i] = directory;
                }
            }
            if (!changed)
            {
                continue;
            }

            SetReverseBucketUnsafe(category, hash, [.. nextDirectories.Distinct(StringComparer.OrdinalIgnoreCase)]);
            updatedHashCount++;
        }
        return updatedHashCount;
    }

    private static IEnumerable<uint> EnumerateLookupHashes(IEnumerable<uint> hashes)
    {
        return hashes?
            .Where(hash => hash != 0u)
            .Distinct() ?? [];
    }

    private void EnsureRelativeDirectoriesByHashes(
        ReverseLookupCategory category,
        Func<Entry, IEnumerable<uint>> hashSelector,
        IEnumerable<uint> hashes)
    {
        uint[] requestedHashes = hashes?
            .Where(hash => hash != 0u)
            .Distinct()
            .ToArray() ?? [];
        if (requestedHashes.Length == 0)
        {
            return;
        }

        var missingHashes = new HashSet<uint>(requestedHashes);
        lock (lockLazyDirectoriesByHash)
        {
            ResourceReverseLookupMap targetDirectoriesByHash = GetReverseLookupRootUnsafe(category);
            missingHashes.RemoveWhere(hash => targetDirectoriesByHash.ContainsKey(hash));
            if (isFullReverseLookupBuilt)
            {
                return;
            }
        }
        if (missingHashes.Count == 0)
        {
            return;
        }

        KeyValuePair<string, Entry>[] entrySnapshot = SnapshotEntries();
        lazyReverseLookupEntrySnapshotCapturedObserver?.Invoke();
        Dictionary<uint, List<string>> builtDirectories = [];
        foreach (KeyValuePair<string, Entry> entryPair in entrySnapshot)
        {
            IEnumerable<uint> relativeHashes = hashSelector?.Invoke(entryPair.Value);
            if (relativeHashes == null)
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
                    directories = [];
                    builtDirectories[hash] = directories;
                }
                directories.Add(entryPair.Key);
            }
        }

        lock (lockLazyDirectoriesByHash)
        {
            ResourceReverseLookupMap root = GetReverseLookupRootUnsafe(category);
            foreach (uint hash in missingHashes)
            {
                if (root.ContainsKey(hash))
                {
                    continue;
                }
                string[] candidates = builtDirectories.TryGetValue(hash, out List<string> directories)
                    ? [.. directories.Distinct(StringComparer.OrdinalIgnoreCase)]
                    : [];
                SetReverseBucketUnsafe(category, hash, candidates);
            }
            // Finish this lazy fill here; the next install/delete must not freeze its nodes.
            root.FreezeChanges();
        }
    }

    private KeyValuePair<string, Entry>[] SnapshotEntries()
    {
        lock (lockEntries)
        {
            return [.. entries];
        }
    }

    private static Entry CreateEntry(ChartScanResult scanResult, string directoryPath)
    {
        IEnumerable<uint> audioHashes = TryGetHashes(scanResult?.AudioRelativePathHashesByChartDirectory, directoryPath);
        IEnumerable<uint> imageHashes = TryGetHashes(scanResult?.ImageRelativePathHashesByChartDirectory, directoryPath);
        IEnumerable<uint> movieHashes = TryGetHashes(scanResult?.MovieRelativePathHashesByChartDirectory, directoryPath);
        IEnumerable<uint> selfOwnedAudioHashes = TryGetHashesOrNull(scanResult?.SelfOwnedAudioRelativePathHashesByChartDirectory, directoryPath);
        IEnumerable<uint> selfOwnedImageHashes = TryGetHashesOrNull(scanResult?.SelfOwnedImageRelativePathHashesByChartDirectory, directoryPath);
        IEnumerable<uint> selfOwnedMovieHashes = TryGetHashesOrNull(scanResult?.SelfOwnedMovieRelativePathHashesByChartDirectory, directoryPath);
        return scanResult?.ResourceHashArraysAreSortedDistinct == true
            ? Entry.FromSortedDistinctArrays(
                audioHashes,
                imageHashes,
                movieHashes,
                selfOwnedAudioHashes,
                selfOwnedImageHashes,
                selfOwnedMovieHashes)
            : new Entry(
                audioHashes,
                imageHashes,
                movieHashes,
                selfOwnedAudioHashes,
                selfOwnedImageHashes,
                selfOwnedMovieHashes);
    }

    private static IEnumerable<uint> TryGetHashesOrNull(IDictionary<string, uint[]> hashesByDirectory, string directoryPath)
    {
        if (hashesByDirectory != null && hashesByDirectory.TryGetValue(directoryPath, out uint[] hashes))
        {
            return hashes ?? [];
        }
        return null;
    }

    private readonly struct ReverseLookupBuildResult(int addedEntryCount, long elapsedMs)
    {
        public static ReverseLookupBuildResult Empty => new(0, 0L);

        public int AddedEntryCount { get; } = addedEntryCount;

        public long ElapsedMs { get; } = elapsedMs;
    }

    private enum ReverseLookupCategory
    {
        Audio,
        Image,
        Movie
    }
}
