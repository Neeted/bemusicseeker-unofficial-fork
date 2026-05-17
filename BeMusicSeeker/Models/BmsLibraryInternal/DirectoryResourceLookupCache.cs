using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        private HashSet<uint> audioRelativePathHashes;

        private HashSet<uint> imageRelativePathHashes;

        private HashSet<uint> movieRelativePathHashes;

        private HashSet<uint> selfOwnedAudioRelativePathHashes;

        private HashSet<uint> selfOwnedImageRelativePathHashes;

        private HashSet<uint> selfOwnedMovieRelativePathHashes;

        public ISet<uint> AudioRelativePathHashes => audioRelativePathHashes ??= CreateHashSet(audioRelativePathHashArray);

        public ISet<uint> ImageRelativePathHashes => imageRelativePathHashes ??= CreateHashSet(imageRelativePathHashArray);

        public ISet<uint> MovieRelativePathHashes => movieRelativePathHashes ??= CreateHashSet(movieRelativePathHashArray);

        public ISet<uint> SelfOwnedAudioRelativePathHashes => selfOwnedAudioRelativePathHashes ??= CreateHashSet(selfOwnedAudioRelativePathHashArray);

        public ISet<uint> SelfOwnedImageRelativePathHashes => selfOwnedImageRelativePathHashes ??= CreateHashSet(selfOwnedImageRelativePathHashArray);

        public ISet<uint> SelfOwnedMovieRelativePathHashes => selfOwnedMovieRelativePathHashes ??= CreateHashSet(selfOwnedMovieRelativePathHashArray);

        public uint[] AudioRelativePathHashArray => audioRelativePathHashArray;

        public uint[] ImageRelativePathHashArray => imageRelativePathHashArray;

        public uint[] MovieRelativePathHashArray => movieRelativePathHashArray;

        public uint[] SelfOwnedAudioRelativePathHashArray => selfOwnedAudioRelativePathHashArray;

        public uint[] SelfOwnedImageRelativePathHashArray => selfOwnedImageRelativePathHashArray;

        public uint[] SelfOwnedMovieRelativePathHashArray => selfOwnedMovieRelativePathHashArray;

        public int AudioFileNameHashCount => audioRelativePathHashArray.Length;

        public int ImageFileNameHashCount => imageRelativePathHashArray.Length;

        public int MovieFileNameHashCount => movieRelativePathHashArray.Length;

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
                trustSortedDistinctArrays: false)
        {
        }

        private Entry(
            IEnumerable<uint> audioRelativePathHashes,
            IEnumerable<uint> imageRelativePathHashes,
            IEnumerable<uint> movieRelativePathHashes,
            IEnumerable<uint> selfOwnedAudioRelativePathHashes,
            IEnumerable<uint> selfOwnedImageRelativePathHashes,
            IEnumerable<uint> selfOwnedMovieRelativePathHashes,
            bool trustSortedDistinctArrays)
        {
            audioRelativePathHashArray = MaterializeHashes(audioRelativePathHashes, trustSortedDistinctArrays);
            imageRelativePathHashArray = MaterializeHashes(imageRelativePathHashes, trustSortedDistinctArrays);
            movieRelativePathHashArray = MaterializeHashes(movieRelativePathHashes, trustSortedDistinctArrays);
            selfOwnedAudioRelativePathHashArray = selfOwnedAudioRelativePathHashes == null
                ? audioRelativePathHashArray
                : MaterializeHashes(selfOwnedAudioRelativePathHashes, trustSortedDistinctArrays);
            selfOwnedImageRelativePathHashArray = selfOwnedImageRelativePathHashes == null
                ? imageRelativePathHashArray
                : MaterializeHashes(selfOwnedImageRelativePathHashes, trustSortedDistinctArrays);
            selfOwnedMovieRelativePathHashArray = selfOwnedMovieRelativePathHashes == null
                ? movieRelativePathHashArray
                : MaterializeHashes(selfOwnedMovieRelativePathHashes, trustSortedDistinctArrays);
        }

        internal static Entry FromNativeSorted(
            IEnumerable<uint> audioRelativePathHashes,
            IEnumerable<uint> imageRelativePathHashes,
            IEnumerable<uint> movieRelativePathHashes,
            IEnumerable<uint> selfOwnedAudioRelativePathHashes = null,
            IEnumerable<uint> selfOwnedImageRelativePathHashes = null,
            IEnumerable<uint> selfOwnedMovieRelativePathHashes = null)
        {
            return new Entry(
                audioRelativePathHashes,
                imageRelativePathHashes,
                movieRelativePathHashes,
                selfOwnedAudioRelativePathHashes,
                selfOwnedImageRelativePathHashes,
                selfOwnedMovieRelativePathHashes,
                trustSortedDistinctArrays: true);
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

        private static uint[] MaterializeHashes(IEnumerable<uint> hashes, bool trustSortedDistinctArrays = false)
        {
            if (hashes == null)
            {
                return [];
            }
            uint[] hashArray = hashes as uint[] ?? [.. hashes];
            if (trustSortedDistinctArrays)
            {
                return hashArray;
            }
            if (hashArray.Length <= 1)
            {
                return hashArray;
            }
            uint[] sorted = [.. hashArray.Distinct()];
            Array.Sort(sorted);
            return sorted;
        }

        private static HashSet<uint> CreateHashSet(IEnumerable<uint> hashes)
        {
            return hashes == null ? [] : [.. hashes];
        }
    }

    private readonly object lockEntries = new();

    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<uint, string[]> audioDirectoriesByRelativeHash;

    private readonly Dictionary<uint, string[]> imageDirectoriesByRelativeHash;

    private readonly Dictionary<uint, string[]> movieDirectoriesByRelativeHash;

    private readonly object lockLazyDirectoriesByHash = new();

    private long lazyHashBuildMs;

    private long lazyHashLookupCount;

    private bool isFullReverseLookupBuilt;

    public DirectoryResourceLookupCache()
        : this(
            [],
            [],
            [])
    {
    }

    private DirectoryResourceLookupCache(
        Dictionary<uint, string[]> audioDirectoriesByRelativeHash,
        Dictionary<uint, string[]> imageDirectoriesByRelativeHash,
        Dictionary<uint, string[]> movieDirectoriesByRelativeHash)
    {
        this.audioDirectoriesByRelativeHash = audioDirectoriesByRelativeHash ?? [];
        this.imageDirectoriesByRelativeHash = imageDirectoriesByRelativeHash ?? [];
        this.movieDirectoriesByRelativeHash = movieDirectoriesByRelativeHash ?? [];
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

    public static DirectoryResourceLookupCache CreateFromScanResult(BmsScanResult scanResult)
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
            PrepareNativeReverseMap(audioRelativeReverseDirectories),
            PrepareNativeReverseMap(imageRelativeReverseDirectories),
            PrepareNativeReverseMap(movieRelativeReverseDirectories));
        int count = chartDirectories?.Length ?? 0;
        for (int i = 0; i < count; i++)
        {
            string chartDirectory = chartDirectories[i];
            if (string.IsNullOrWhiteSpace(chartDirectory))
            {
                continue;
            }
            cache.entries[chartDirectory] = Entry.FromNativeSorted(
                GetNativeHashes(audioRelativePathHashesByDirectoryIndex, i),
                GetNativeHashes(imageRelativePathHashesByDirectoryIndex, i),
                GetNativeHashes(movieRelativePathHashesByDirectoryIndex, i),
                GetNativeHashes(selfOwnedAudioRelativePathHashesByDirectoryIndex, i),
                GetNativeHashes(selfOwnedImageRelativePathHashesByDirectoryIndex, i),
                GetNativeHashes(selfOwnedMovieRelativePathHashesByDirectoryIndex, i));
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

    public IReadOnlyCollection<string> GetDirectoriesByAudioRelativeHash(uint relativePathHash)
    {
        if (relativePathHash == 0u)
        {
            return [];
        }
        EnsureRelativeDirectoriesByHashes(audioDirectoriesByRelativeHash, entry => entry?.AudioRelativePathHashArray, [relativePathHash]);
        lock (lockLazyDirectoriesByHash)
        {
            if (audioDirectoriesByRelativeHash.TryGetValue(relativePathHash, out string[] directories))
            {
                return directories;
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
        EnsureRelativeDirectoriesByHashes(imageDirectoriesByRelativeHash, entry => entry?.ImageRelativePathHashArray, [relativePathHash]);
        lock (lockLazyDirectoriesByHash)
        {
            if (imageDirectoriesByRelativeHash.TryGetValue(relativePathHash, out string[] directories))
            {
                return directories;
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
        EnsureRelativeDirectoriesByHashes(movieDirectoriesByRelativeHash, entry => entry?.MovieRelativePathHashArray, [relativePathHash]);
        lock (lockLazyDirectoriesByHash)
        {
            if (movieDirectoriesByRelativeHash.TryGetValue(relativePathHash, out string[] directories))
            {
                return directories;
            }
        }
        return [];
    }

    public void EnsureAudioRelativeDirectoriesByHashes(IEnumerable<uint> hashes)
    {
        EnsureRelativeDirectoriesByHashes(audioDirectoriesByRelativeHash, entry => entry?.AudioRelativePathHashArray, hashes);
    }

    public void EnsureImageRelativeDirectoriesByHashes(IEnumerable<uint> hashes)
    {
        EnsureRelativeDirectoriesByHashes(imageDirectoriesByRelativeHash, entry => entry?.ImageRelativePathHashArray, hashes);
    }

    public void EnsureMovieRelativeDirectoriesByHashes(IEnumerable<uint> hashes)
    {
        EnsureRelativeDirectoriesByHashes(movieDirectoriesByRelativeHash, entry => entry?.MovieRelativePathHashArray, hashes);
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
            audioDirectoriesByRelativeHash.Clear();
            imageDirectoriesByRelativeHash.Clear();
            movieDirectoriesByRelativeHash.Clear();
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
            if (hasRemoved)
            {
                updatedHashCount += RemoveDirectoryFromCachedHashes(audioDirectoriesByRelativeHash, removedDirectoryPath, removedEntry.AudioRelativePathHashArray);
                updatedHashCount += RemoveDirectoryFromCachedHashes(imageDirectoriesByRelativeHash, removedDirectoryPath, removedEntry.ImageRelativePathHashArray);
                updatedHashCount += RemoveDirectoryFromCachedHashes(movieDirectoriesByRelativeHash, removedDirectoryPath, removedEntry.MovieRelativePathHashArray);
            }

            if (hasAdded)
            {
                updatedHashCount += AddDirectoryToCachedHashes(audioDirectoriesByRelativeHash, addedDirectoryPath, addedEntry.AudioRelativePathHashArray, addMissingKeys: isFullReverseLookupBuilt);
                updatedHashCount += AddDirectoryToCachedHashes(imageDirectoriesByRelativeHash, addedDirectoryPath, addedEntry.ImageRelativePathHashArray, addMissingKeys: isFullReverseLookupBuilt);
                updatedHashCount += AddDirectoryToCachedHashes(movieDirectoriesByRelativeHash, addedDirectoryPath, addedEntry.MovieRelativePathHashArray, addMissingKeys: isFullReverseLookupBuilt);
            }

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

    private static int AddDirectoryToCachedHashes(Dictionary<uint, string[]> directoriesByTargetHash, string directoryPath, IEnumerable<uint> hashes, bool addMissingKeys)
    {
        int updatedHashCount = 0;
        foreach (uint hash in EnumerateLookupHashes(hashes))
        {
            if (directoriesByTargetHash.TryGetValue(hash, out string[] directories))
            {
                if (directories.Any(dir => string.Equals(dir, directoryPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                directoriesByTargetHash[hash] = [.. directories
                    .Concat([directoryPath])
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
                updatedHashCount++;
            }
            else if (addMissingKeys)
            {
                directoriesByTargetHash[hash] = [directoryPath];
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
            string[] nextDirectories = [.. directories
                .Where(dir => !string.Equals(dir, directoryPath, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
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
            .Where(hash => hash != 0u)
            .Distinct() ?? [];
    }

    private void EnsureRelativeDirectoriesByHashes(Dictionary<uint, string[]> targetDirectoriesByHash, Func<Entry, uint[]> hashArraySelector, IEnumerable<uint> hashes)
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
        Dictionary<uint, List<string>> builtDirectories = [];
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
                    directories = [];
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
                    targetDirectoriesByHash[hash] = [.. directories.Distinct(StringComparer.OrdinalIgnoreCase)];
                }
                else
                {
                    targetDirectoriesByHash[hash] = [];
                }
            }
        }
    }

    private KeyValuePair<string, Entry>[] SnapshotEntries()
    {
        lock (lockEntries)
        {
            return [.. entries];
        }
    }

    private static Entry CreateEntry(BmsScanResult scanResult, string directoryPath)
    {
        return new Entry(
            TryGetHashes(scanResult?.AudioRelativePathHashesByChartDirectory, directoryPath),
            TryGetHashes(scanResult?.ImageRelativePathHashesByChartDirectory, directoryPath),
            TryGetHashes(scanResult?.MovieRelativePathHashesByChartDirectory, directoryPath),
            TryGetHashesOrNull(scanResult?.SelfOwnedAudioRelativePathHashesByChartDirectory, directoryPath),
            TryGetHashesOrNull(scanResult?.SelfOwnedImageRelativePathHashesByChartDirectory, directoryPath),
            TryGetHashesOrNull(scanResult?.SelfOwnedMovieRelativePathHashesByChartDirectory, directoryPath));
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
}
