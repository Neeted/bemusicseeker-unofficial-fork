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

        private uint[] cachedAllCategoryUnionHashArray;

        private uint[] cachedSelfOwnedCategoryUnionHashArray;

        private HashSet<uint> allBaseNameHashes;

        private HashSet<uint> selfOwnedAllBaseNameHashes;

        public ISet<uint> AllBaseNameHashes => allBaseNameHashes ??= CreateHashSet(AllBaseNameHashArray);

        public ISet<uint> AudioBaseNameHashes => audioBaseNameHashes ??= CreateHashSet(audioBaseNameHashArray);

        public ISet<uint> ImageBaseNameHashes => imageBaseNameHashes ??= CreateHashSet(imageBaseNameHashArray);

        public ISet<uint> MovieBaseNameHashes => movieBaseNameHashes ??= CreateHashSet(movieBaseNameHashArray);

        public ISet<uint> AudioRelativePathHashes => audioRelativePathHashes ??= CreateHashSet(audioRelativePathHashArray);

        public ISet<uint> ImageRelativePathHashes => imageRelativePathHashes ??= CreateHashSet(imageRelativePathHashArray);

        public ISet<uint> MovieRelativePathHashes => movieRelativePathHashes ??= CreateHashSet(movieRelativePathHashArray);

        public ISet<uint> SelfOwnedAllBaseNameHashes => selfOwnedAllBaseNameHashes ??= CreateHashSet(SelfOwnedAllBaseNameHashArray);

        public ISet<uint> SelfOwnedAudioBaseNameHashes => selfOwnedAudioBaseNameHashes ??= CreateHashSet(selfOwnedAudioBaseNameHashArray);

        public ISet<uint> SelfOwnedImageBaseNameHashes => selfOwnedImageBaseNameHashes ??= CreateHashSet(selfOwnedImageBaseNameHashArray);

        public ISet<uint> SelfOwnedMovieBaseNameHashes => selfOwnedMovieBaseNameHashes ??= CreateHashSet(selfOwnedMovieBaseNameHashArray);

        public ISet<uint> SelfOwnedAudioRelativePathHashes => selfOwnedAudioRelativePathHashes ??= CreateHashSet(selfOwnedAudioRelativePathHashArray);

        public ISet<uint> SelfOwnedImageRelativePathHashes => selfOwnedImageRelativePathHashes ??= CreateHashSet(selfOwnedImageRelativePathHashArray);

        public ISet<uint> SelfOwnedMovieRelativePathHashes => selfOwnedMovieRelativePathHashes ??= CreateHashSet(selfOwnedMovieRelativePathHashArray);

        public uint[] AllBaseNameHashArray => cachedAllCategoryUnionHashArray ??= CreateUnionArray(audioBaseNameHashArray, imageBaseNameHashArray, movieBaseNameHashArray);

        public uint[] AudioBaseNameHashArray => audioBaseNameHashArray;

        public uint[] ImageBaseNameHashArray => imageBaseNameHashArray;

        public uint[] MovieBaseNameHashArray => movieBaseNameHashArray;

        public uint[] AudioRelativePathHashArray => audioRelativePathHashArray;

        public uint[] ImageRelativePathHashArray => imageRelativePathHashArray;

        public uint[] MovieRelativePathHashArray => movieRelativePathHashArray;

        public uint[] SelfOwnedAllBaseNameHashArray => cachedSelfOwnedCategoryUnionHashArray ??= CreateUnionArray(selfOwnedAudioBaseNameHashArray, selfOwnedImageBaseNameHashArray, selfOwnedMovieBaseNameHashArray);

        public uint[] SelfOwnedAudioBaseNameHashArray => selfOwnedAudioBaseNameHashArray;

        public uint[] SelfOwnedImageBaseNameHashArray => selfOwnedImageBaseNameHashArray;

        public uint[] SelfOwnedMovieBaseNameHashArray => selfOwnedMovieBaseNameHashArray;

        public uint[] SelfOwnedAudioRelativePathHashArray => selfOwnedAudioRelativePathHashArray;

        public uint[] SelfOwnedImageRelativePathHashArray => selfOwnedImageRelativePathHashArray;

        public uint[] SelfOwnedMovieRelativePathHashArray => selfOwnedMovieRelativePathHashArray;

        public int FileNameHashCount => AllBaseNameHashArray.Length;

        public int AudioFileNameHashCount => audioBaseNameHashArray.Length;

        public int ImageFileNameHashCount => imageBaseNameHashArray.Length;

        public int MovieFileNameHashCount => movieBaseNameHashArray.Length;

        public int SelfOwnedFileNameHashCount => SelfOwnedAllBaseNameHashArray.Length;

        public Entry()
            : this(Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>())
        {
        }

        public Entry(
            IEnumerable<uint> audioBaseNameHashes,
            IEnumerable<uint> imageBaseNameHashes,
            IEnumerable<uint> movieBaseNameHashes,
            IEnumerable<uint> audioRelativePathHashes,
            IEnumerable<uint> imageRelativePathHashes,
            IEnumerable<uint> movieRelativePathHashes,
            IEnumerable<uint> selfOwnedAudioBaseNameHashes = null,
            IEnumerable<uint> selfOwnedImageBaseNameHashes = null,
            IEnumerable<uint> selfOwnedMovieBaseNameHashes = null,
            IEnumerable<uint> selfOwnedAudioRelativePathHashes = null,
            IEnumerable<uint> selfOwnedImageRelativePathHashes = null,
            IEnumerable<uint> selfOwnedMovieRelativePathHashes = null)
            : this(
                audioBaseNameHashes,
                imageBaseNameHashes,
                movieBaseNameHashes,
                audioRelativePathHashes,
                imageRelativePathHashes,
                movieRelativePathHashes,
                selfOwnedAudioBaseNameHashes,
                selfOwnedImageBaseNameHashes,
                selfOwnedMovieBaseNameHashes,
                selfOwnedAudioRelativePathHashes,
                selfOwnedImageRelativePathHashes,
                selfOwnedMovieRelativePathHashes,
                trustSortedDistinctArrays: false)
        {
        }

        private Entry(
            IEnumerable<uint> audioBaseNameHashes,
            IEnumerable<uint> imageBaseNameHashes,
            IEnumerable<uint> movieBaseNameHashes,
            IEnumerable<uint> audioRelativePathHashes,
            IEnumerable<uint> imageRelativePathHashes,
            IEnumerable<uint> movieRelativePathHashes,
            IEnumerable<uint> selfOwnedAudioBaseNameHashes,
            IEnumerable<uint> selfOwnedImageBaseNameHashes,
            IEnumerable<uint> selfOwnedMovieBaseNameHashes,
            IEnumerable<uint> selfOwnedAudioRelativePathHashes,
            IEnumerable<uint> selfOwnedImageRelativePathHashes,
            IEnumerable<uint> selfOwnedMovieRelativePathHashes,
            bool trustSortedDistinctArrays)
        {
            audioBaseNameHashArray = MaterializeHashes(audioBaseNameHashes, trustSortedDistinctArrays);
            imageBaseNameHashArray = MaterializeHashes(imageBaseNameHashes, trustSortedDistinctArrays);
            movieBaseNameHashArray = MaterializeHashes(movieBaseNameHashes, trustSortedDistinctArrays);
            audioRelativePathHashArray = MaterializeHashes(audioRelativePathHashes, trustSortedDistinctArrays);
            imageRelativePathHashArray = MaterializeHashes(imageRelativePathHashes, trustSortedDistinctArrays);
            movieRelativePathHashArray = MaterializeHashes(movieRelativePathHashes, trustSortedDistinctArrays);
            selfOwnedAudioBaseNameHashArray = MaterializeHashes(selfOwnedAudioBaseNameHashes, audioBaseNameHashArray, trustSortedDistinctArrays);
            selfOwnedImageBaseNameHashArray = MaterializeHashes(selfOwnedImageBaseNameHashes, imageBaseNameHashArray, trustSortedDistinctArrays);
            selfOwnedMovieBaseNameHashArray = MaterializeHashes(selfOwnedMovieBaseNameHashes, movieBaseNameHashArray, trustSortedDistinctArrays);
            selfOwnedAudioRelativePathHashArray = MaterializeHashes(selfOwnedAudioRelativePathHashes, audioRelativePathHashArray, trustSortedDistinctArrays);
            selfOwnedImageRelativePathHashArray = MaterializeHashes(selfOwnedImageRelativePathHashes, imageRelativePathHashArray, trustSortedDistinctArrays);
            selfOwnedMovieRelativePathHashArray = MaterializeHashes(selfOwnedMovieRelativePathHashes, movieRelativePathHashArray, trustSortedDistinctArrays);
        }

        internal static Entry FromNativeSorted(
            IEnumerable<uint> audioBaseNameHashes,
            IEnumerable<uint> imageBaseNameHashes,
            IEnumerable<uint> movieBaseNameHashes,
            IEnumerable<uint> audioRelativePathHashes,
            IEnumerable<uint> imageRelativePathHashes,
            IEnumerable<uint> movieRelativePathHashes,
            IEnumerable<uint> selfOwnedAudioBaseNameHashes = null,
            IEnumerable<uint> selfOwnedImageBaseNameHashes = null,
            IEnumerable<uint> selfOwnedMovieBaseNameHashes = null,
            IEnumerable<uint> selfOwnedAudioRelativePathHashes = null,
            IEnumerable<uint> selfOwnedImageRelativePathHashes = null,
            IEnumerable<uint> selfOwnedMovieRelativePathHashes = null)
        {
            return new Entry(
                audioBaseNameHashes,
                imageBaseNameHashes,
                movieBaseNameHashes,
                audioRelativePathHashes,
                imageRelativePathHashes,
                movieRelativePathHashes,
                selfOwnedAudioBaseNameHashes,
                selfOwnedImageBaseNameHashes,
                selfOwnedMovieBaseNameHashes,
                selfOwnedAudioRelativePathHashes,
                selfOwnedImageRelativePathHashes,
                selfOwnedMovieRelativePathHashes,
                trustSortedDistinctArrays: true);
        }

        public Entry Clone()
        {
            return new Entry(
                audioBaseNameHashArray,
                imageBaseNameHashArray,
                movieBaseNameHashArray,
                audioRelativePathHashArray,
                imageRelativePathHashArray,
                movieRelativePathHashArray,
                selfOwnedAudioBaseNameHashArray,
                selfOwnedImageBaseNameHashArray,
                selfOwnedMovieBaseNameHashArray,
                selfOwnedAudioRelativePathHashArray,
                selfOwnedImageRelativePathHashArray,
                selfOwnedMovieRelativePathHashArray);
        }

        private static uint[] MaterializeHashes(IEnumerable<uint> hashes, bool trustSortedDistinctArrays = false)
        {
            if (hashes == null)
            {
                return Array.Empty<uint>();
            }
            uint[] hashArray = hashes as uint[] ?? hashes.ToArray();
            if (trustSortedDistinctArrays)
            {
                return hashArray;
            }
            if (hashArray.Length <= 1)
            {
                return hashArray;
            }
            uint[] sorted = hashArray.Distinct().ToArray();
            Array.Sort(sorted);
            return sorted;
        }

        private static uint[] MaterializeHashes(IEnumerable<uint> hashes, uint[] fallback, bool trustSortedDistinctArrays = false)
        {
            if (hashes == null)
            {
                return fallback ?? Array.Empty<uint>();
            }
            return MaterializeHashes(hashes, trustSortedDistinctArrays);
        }

        private static uint[] CreateUnionArray(params uint[][] hashArrays)
        {
            if (hashArrays == null || hashArrays.Length == 0)
            {
                return Array.Empty<uint>();
            }

            uint[] single = null;
            int nonEmptyCount = 0;
            foreach (uint[] hashArray in hashArrays)
            {
                if (hashArray == null || hashArray.Length == 0)
                {
                    continue;
                }
                single = hashArray;
                nonEmptyCount++;
            }
            if (nonEmptyCount == 0)
            {
                return Array.Empty<uint>();
            }
            if (nonEmptyCount == 1)
            {
                return single;
            }

            uint[] union = hashArrays
                .Where((uint[] hashArray) => hashArray != null && hashArray.Length > 0)
                .SelectMany((uint[] hashArray) => hashArray)
                .Distinct()
                .ToArray();
            Array.Sort(union);
            return union;
        }

        private static HashSet<uint> CreateHashSet(IEnumerable<uint> hashes)
        {
            return hashes == null ? new HashSet<uint>() : new HashSet<uint>(hashes);
        }
    }

    private readonly object lockEntries = new object();

    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<uint, string[]> audioDirectoriesByRelativeHash;

    private readonly Dictionary<uint, string[]> imageDirectoriesByRelativeHash;

    private readonly Dictionary<uint, string[]> movieDirectoriesByRelativeHash;

    private readonly object lockLazyDirectoriesByHash = new object();

    private long lazyHashBuildMs;

    private long lazyHashLookupCount;

    private bool isFullReverseLookupBuilt;

    public DirectoryResourceLookupCache()
        : this(
            new Dictionary<uint, string[]>(),
            new Dictionary<uint, string[]>(),
            new Dictionary<uint, string[]>())
    {
    }

    private DirectoryResourceLookupCache(
        Dictionary<uint, string[]> audioDirectoriesByRelativeHash,
        Dictionary<uint, string[]> imageDirectoriesByRelativeHash,
        Dictionary<uint, string[]> movieDirectoriesByRelativeHash)
    {
        this.audioDirectoriesByRelativeHash = audioDirectoriesByRelativeHash ?? new Dictionary<uint, string[]>();
        this.imageDirectoriesByRelativeHash = imageDirectoriesByRelativeHash ?? new Dictionary<uint, string[]>();
        this.movieDirectoriesByRelativeHash = movieDirectoriesByRelativeHash ?? new Dictionary<uint, string[]>();
    }

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
        DirectoryResourceLookupCache cache = new DirectoryResourceLookupCache();
        foreach (string chartDirectory in scanResult?.ChartDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        {
            cache.SetEntry(chartDirectory, CreateEntry(scanResult, chartDirectory));
        }
        return cache;
    }

    public static DirectoryResourceLookupCache CreateFromNativeCanonical(
        IEnumerable<string> chartDirectories,
        IDictionary<string, uint[]> audioBaseNameHashesByDirectory,
        IDictionary<string, uint[]> imageBaseNameHashesByDirectory,
        IDictionary<string, uint[]> movieBaseNameHashesByDirectory,
        IDictionary<string, uint[]> audioRelativePathHashesByDirectory,
        IDictionary<string, uint[]> imageRelativePathHashesByDirectory,
        IDictionary<string, uint[]> movieRelativePathHashesByDirectory,
        IDictionary<string, uint[]> selfOwnedAudioBaseNameHashesByDirectory,
        IDictionary<string, uint[]> selfOwnedImageBaseNameHashesByDirectory,
        IDictionary<string, uint[]> selfOwnedMovieBaseNameHashesByDirectory,
        IDictionary<string, uint[]> selfOwnedAudioRelativePathHashesByDirectory,
        IDictionary<string, uint[]> selfOwnedImageRelativePathHashesByDirectory,
        IDictionary<string, uint[]> selfOwnedMovieRelativePathHashesByDirectory,
        IDictionary<uint, string[]> audioRelativeReverseDirectories,
        IDictionary<uint, string[]> imageRelativeReverseDirectories,
        IDictionary<uint, string[]> movieRelativeReverseDirectories)
    {
        DirectoryResourceLookupCache cache = new DirectoryResourceLookupCache();
        foreach (string chartDirectory in chartDirectories ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(chartDirectory))
            {
                continue;
            }
            cache.entries[chartDirectory] = Entry.FromNativeSorted(
                TryGetHashes(audioBaseNameHashesByDirectory, chartDirectory),
                TryGetHashes(imageBaseNameHashesByDirectory, chartDirectory),
                TryGetHashes(movieBaseNameHashesByDirectory, chartDirectory),
                TryGetHashes(audioRelativePathHashesByDirectory, chartDirectory),
                TryGetHashes(imageRelativePathHashesByDirectory, chartDirectory),
                TryGetHashes(movieRelativePathHashesByDirectory, chartDirectory),
                TryGetHashes(selfOwnedAudioBaseNameHashesByDirectory, chartDirectory),
                TryGetHashes(selfOwnedImageBaseNameHashesByDirectory, chartDirectory),
                TryGetHashes(selfOwnedMovieBaseNameHashesByDirectory, chartDirectory),
                TryGetHashes(selfOwnedAudioRelativePathHashesByDirectory, chartDirectory),
                TryGetHashes(selfOwnedImageRelativePathHashesByDirectory, chartDirectory),
                TryGetHashes(selfOwnedMovieRelativePathHashesByDirectory, chartDirectory));
        }
        cache.LoadNativeReverseMap(cache.audioDirectoriesByRelativeHash, audioRelativeReverseDirectories);
        cache.LoadNativeReverseMap(cache.imageDirectoriesByRelativeHash, imageRelativeReverseDirectories);
        cache.LoadNativeReverseMap(cache.movieDirectoriesByRelativeHash, movieRelativeReverseDirectories);
        cache.isFullReverseLookupBuilt = cache.CategoryReverseLookupEntryCount > 0;
        return cache;
    }

    public static DirectoryResourceLookupCache CreateFromNativeCanonicalArrays(
        string[] chartDirectories,
        uint[][] audioBaseNameHashesByDirectoryIndex,
        uint[][] imageBaseNameHashesByDirectoryIndex,
        uint[][] movieBaseNameHashesByDirectoryIndex,
        uint[][] audioRelativePathHashesByDirectoryIndex,
        uint[][] imageRelativePathHashesByDirectoryIndex,
        uint[][] movieRelativePathHashesByDirectoryIndex,
        uint[][] selfOwnedAudioBaseNameHashesByDirectoryIndex,
        uint[][] selfOwnedImageBaseNameHashesByDirectoryIndex,
        uint[][] selfOwnedMovieBaseNameHashesByDirectoryIndex,
        uint[][] selfOwnedAudioRelativePathHashesByDirectoryIndex,
        uint[][] selfOwnedImageRelativePathHashesByDirectoryIndex,
        uint[][] selfOwnedMovieRelativePathHashesByDirectoryIndex,
        Dictionary<uint, string[]> audioRelativeReverseDirectories,
        Dictionary<uint, string[]> imageRelativeReverseDirectories,
        Dictionary<uint, string[]> movieRelativeReverseDirectories)
    {
        DirectoryResourceLookupCache cache = new DirectoryResourceLookupCache(
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
                GetNativeHashes(audioBaseNameHashesByDirectoryIndex, i),
                GetNativeHashes(imageBaseNameHashesByDirectoryIndex, i),
                GetNativeHashes(movieBaseNameHashesByDirectoryIndex, i),
                GetNativeHashes(audioRelativePathHashesByDirectoryIndex, i),
                GetNativeHashes(imageRelativePathHashesByDirectoryIndex, i),
                GetNativeHashes(movieRelativePathHashesByDirectoryIndex, i),
                GetNativeHashes(selfOwnedAudioBaseNameHashesByDirectoryIndex, i),
                GetNativeHashes(selfOwnedImageBaseNameHashesByDirectoryIndex, i),
                GetNativeHashes(selfOwnedMovieBaseNameHashesByDirectoryIndex, i),
                GetNativeHashes(selfOwnedAudioRelativePathHashesByDirectoryIndex, i),
                GetNativeHashes(selfOwnedImageRelativePathHashesByDirectoryIndex, i),
                GetNativeHashes(selfOwnedMovieRelativePathHashesByDirectoryIndex, i));
        }
        cache.isFullReverseLookupBuilt = cache.CategoryReverseLookupEntryCount > 0;
        return cache;
    }

    public ReverseLookupMutationResult AddDir(
        string directoryPath,
        IEnumerable<uint> audioBaseNameHashes,
        IEnumerable<uint> imageBaseNameHashes,
        IEnumerable<uint> movieBaseNameHashes,
        IEnumerable<uint> audioRelativePathHashes,
        IEnumerable<uint> imageRelativePathHashes,
        IEnumerable<uint> movieRelativePathHashes,
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
            audioBaseNameHashes,
            imageBaseNameHashes,
            movieBaseNameHashes,
            audioRelativePathHashes,
            imageRelativePathHashes,
            movieRelativePathHashes,
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

        HashSet<uint> audioBaseNameHashes = new HashSet<uint>();
        HashSet<uint> imageBaseNameHashes = new HashSet<uint>();
        HashSet<uint> movieBaseNameHashes = new HashSet<uint>();
        HashSet<uint> audioRelativePathHashes = new HashSet<uint>();
        HashSet<uint> imageRelativePathHashes = new HashSet<uint>();
        HashSet<uint> movieRelativePathHashes = new HashSet<uint>();
        foreach (string fileName in fileNames ?? Enumerable.Empty<string>())
        {
            string normalizedPath = ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(fileName);
            string normalizedFileName = ChartResourcePathNormalizer.NormalizeFileNameForLookup(fileName);
            if (string.IsNullOrWhiteSpace(normalizedFileName))
            {
                continue;
            }
            uint resourceKeyHash = BMSDirectoryFileNameHash.GetLookupHash(
                string.IsNullOrWhiteSpace(normalizedPath) ? normalizedFileName : normalizedPath);
            switch (ChartResourcePathNormalizer.ClassifyPath(fileName))
            {
                case ChartResourceKind.Audio:
                    audioBaseNameHashes.Add(resourceKeyHash);
                    if (!string.IsNullOrWhiteSpace(normalizedPath))
                    {
                        audioRelativePathHashes.Add(resourceKeyHash);
                    }
                    break;
                case ChartResourceKind.Image:
                    imageBaseNameHashes.Add(resourceKeyHash);
                    if (!string.IsNullOrWhiteSpace(normalizedPath))
                    {
                        imageRelativePathHashes.Add(resourceKeyHash);
                    }
                    break;
                case ChartResourceKind.Movie:
                    movieBaseNameHashes.Add(resourceKeyHash);
                    if (!string.IsNullOrWhiteSpace(normalizedPath))
                    {
                        movieRelativePathHashes.Add(resourceKeyHash);
                    }
                    break;
            }
        }
        return SetEntry(directoryPath, new Entry(
            audioBaseNameHashes,
            imageBaseNameHashes,
            movieBaseNameHashes,
            audioRelativePathHashes,
            imageRelativePathHashes,
            movieRelativePathHashes,
            audioBaseNameHashes,
            imageBaseNameHashes,
            movieBaseNameHashes,
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

    private static IEnumerable<uint> TryGetHashes(IDictionary<string, uint[]> hashesByDirectory, string directoryPath)
    {
        if (hashesByDirectory != null && hashesByDirectory.TryGetValue(directoryPath, out uint[] hashes))
        {
            return hashes ?? Array.Empty<uint>();
        }
        return Array.Empty<uint>();
    }

    private void LoadNativeReverseMap(Dictionary<uint, string[]> target, IDictionary<uint, string[]> source)
    {
        target.Clear();
        foreach (KeyValuePair<uint, string[]> item in source ?? new Dictionary<uint, string[]>())
        {
            if (item.Key == 0u)
            {
                continue;
            }
            target[item.Key] = item.Value ?? Array.Empty<string>();
        }
    }

    private static Dictionary<uint, string[]> PrepareNativeReverseMap(Dictionary<uint, string[]> source)
    {
        if (source == null)
        {
            return new Dictionary<uint, string[]>();
        }
        source.Remove(0u);
        return source;
    }

    private static uint[] GetNativeHashes(uint[][] hashesByDirectoryIndex, int directoryIndex)
    {
        if (hashesByDirectoryIndex == null || directoryIndex < 0 || directoryIndex >= hashesByDirectoryIndex.Length)
        {
            return Array.Empty<uint>();
        }
        return hashesByDirectoryIndex[directoryIndex] ?? Array.Empty<uint>();
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

    private KeyValuePair<string, Entry>[] SnapshotEntries()
    {
        lock (lockEntries)
        {
            return entries.ToArray();
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
