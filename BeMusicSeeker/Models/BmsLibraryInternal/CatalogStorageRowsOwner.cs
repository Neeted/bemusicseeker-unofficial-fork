using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns the in-memory BMS/bmson storage rows, their versions, and the shared row write gate.
/// Derived catalog indexes and consumer-specific state remain composed by <see cref="BMSLibrary"/>.
/// </summary>
internal sealed class CatalogStorageRowsOwner
{
    private readonly ReaderWriterLockSlimWrapper writeGate = new();

    private readonly object versionGate = new();

    private readonly Dictionary<string, List<CatalogStorageSequenceEntry<BMSFile>>> bmsEntriesByExactPath =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, List<CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song>>> bmsonEntriesByExactPath =
        new(StringComparer.Ordinal);

    private readonly Dictionary<BMSFile, List<CatalogStorageSequenceEntry<BMSFile>>> bmsEntriesByOwner =
        new(ReferenceComparer<BMSFile>.Instance);

    private readonly List<CatalogStorageSequenceEntry<BMSFile>> bmsNullEntries = [];

    private readonly Dictionary<LR2SongDBExtended.bmson_song, List<CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song>>> bmsonEntriesByOwner =
        new(ReferenceComparer<LR2SongDBExtended.bmson_song>.Instance);

    private readonly ICatalogStorageSequenceWorkObserver sequenceWorkObserver;

    private CatalogStorageIndexedSequence<BMSFile> bmsSequence;

    private CatalogStorageIndexedSequence<LR2SongDBExtended.bmson_song> bmsonSequence;

    private CatalogStorageReadOnlyView<BMSFile> bmsRows;

    private CatalogStorageReadOnlyView<LR2SongDBExtended.bmson_song> bmsonRows;

    private bool bmsonNeedsNormalization;

    private long nextOrdinal;

    private int bmsRowsVersion;

    private int bmsonRowsVersion;

    /// <summary>observer を指定しない通常の storage row owner を作成します。</summary>
    internal CatalogStorageRowsOwner()
        : this(null)
    {
    }

    /// <summary>
    /// storage row owner を作成します。
    /// work observer は実処理の sequence 訪問を検証するときだけ指定します。
    /// </summary>
    /// <param name="sequenceWorkObserver">sequence の実アクセスを受け取る内部 observer。</param>
    internal CatalogStorageRowsOwner(
        ICatalogStorageSequenceWorkObserver sequenceWorkObserver)
    {
        this.sequenceWorkObserver = sequenceWorkObserver;
        bmsSequence = CatalogStorageIndexedSequence<BMSFile>.Empty(
            CompareOrdinalEntries,
            sequenceWorkObserver);
        bmsonSequence = CatalogStorageIndexedSequence<LR2SongDBExtended.bmson_song>.Empty(
            CompareOrdinalEntries,
            sequenceWorkObserver);
        bmsRows = new CatalogStorageReadOnlyView<BMSFile>(bmsSequence);
        bmsonRows = new CatalogStorageReadOnlyView<LR2SongDBExtended.bmson_song>(bmsonSequence);
    }

    /// <summary>catalog storage の single-writer gate を返します。</summary>
    internal ReaderWriterLockSlimWrapper WriteGate => writeGate;

    /// <summary>storage version と view capture を同期する gate を返します。</summary>
    internal object VersionGate => versionGate;

    /// <summary>現在の BMS membership/order を read-only view で返します。</summary>
    internal IReadOnlyList<BMSFile> BmsRows => bmsRows;

    /// <summary>現在の BMSON membership/order を read-only view で返します。</summary>
    internal IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonRows => bmsonRows;

    /// <summary>現在の BMS read-only view を返します。</summary>
    internal IReadOnlyList<BMSFile> GetBmsRowsReadOnly() => bmsRows;

    /// <summary>現在の BMSON read-only view を返します。</summary>
    internal IReadOnlyList<LR2SongDBExtended.bmson_song> GetBmsonRowsReadOnly() => bmsonRows;

    /// <summary>BMS storage view の公開 version を返します。</summary>
    internal int BmsRowsVersion => Volatile.Read(ref bmsRowsVersion);

    /// <summary>BMSON storage view の公開 version を返します。</summary>
    internal int BmsonRowsVersion => Volatile.Read(ref bmsonRowsVersion);

    /// <summary>raw BMS 行を入力順のまま置き換えます。</summary>
    /// <param name="rows">入力順を保持する raw BMS 行。</param>
    internal StorageRowsVersionSnapshot ReplaceBmsRows(IEnumerable<BMSFile> rows)
    {
        using (writeGate.GetWriterGuard())
        {
            lock (versionGate)
            {
                int previousBmsRowsVersion = bmsRowsVersion;
                ReplaceBmsRowsUnsafe(rows);
                IncrementBmsRowsVersion();
                return CreateVersionSnapshot(previousBmsRowsVersion, bmsonRowsVersion);
            }
        }
    }

    /// <summary>raw BMSON 行を入力順のまま置き換えます。</summary>
    /// <param name="rows">入力順を保持する raw BMSON 行。</param>
    internal StorageRowsVersionSnapshot ReplaceBmsonRows(
        IEnumerable<LR2SongDBExtended.bmson_song> rows)
    {
        using (writeGate.GetWriterGuard())
        {
            lock (versionGate)
            {
                int previousBmsonRowsVersion = bmsonRowsVersion;
                ReplaceBmsonRowsUnsafe(rows);
                IncrementBmsonRowsVersion();
                return CreateVersionSnapshot(bmsRowsVersion, previousBmsonRowsVersion);
            }
        }
    }

    /// <summary>
    /// BMS と BMSON の raw 行を一つの version 境界で置き換え、同じ storage view を返します。
    /// </summary>
    /// <param name="nextBmsRows">置換後の raw BMS 行。</param>
    /// <param name="nextBmsonRows">置換後の raw BMSON 行。</param>
    internal CatalogStorageRowsSnapshot ReplaceRowsAndCaptureSnapshot(
        IEnumerable<BMSFile> nextBmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> nextBmsonRows)
    {
        using (writeGate.GetWriterGuard())
        {
            lock (versionGate)
            {
                ReplaceBmsRowsUnsafe(nextBmsRows);
                ReplaceBmsonRowsUnsafe(nextBmsonRows);
                IncrementBmsRowsVersion();
                IncrementBmsonRowsVersion();
                return CreateSnapshot();
            }
        }
    }

    /// <summary>導入が確定した行を未加工 exact path で upsert します。</summary>
    /// <param name="addedTargets">導入確定済みの BMS/BMSON 行。</param>
    internal StorageRowsVersionSnapshot ApplyInstalledTargets(ChartStorageTargetSet addedTargets)
    {
        if (addedTargets == null)
        {
            return CaptureVersionSnapshot();
        }

        using (writeGate.GetWriterGuard())
        {
            lock (versionGate)
            {
                int previousBmsRowsVersion = bmsRowsVersion;
                int previousBmsonRowsVersion = bmsonRowsVersion;
                bool bmsChanged = addedTargets.BmsFiles.Count > 0;
                bool bmsonChanged = addedTargets.BmsonSongs.Count > 0;
                if (bmsChanged)
                {
                    RemoveBmsNullEntries();
                    var addedPaths = new HashSet<string>(StringComparer.Ordinal);
                    foreach (BMSFile file in addedTargets.BmsFiles)
                    {
                        if (file != null && !string.IsNullOrWhiteSpace(file.path))
                        {
                            addedPaths.Add(file.path);
                        }
                    }
                    foreach (string path in addedPaths)
                    {
                        RemoveBmsEntriesAtPath(path);
                    }
                    foreach (BMSFile file in addedTargets.BmsFiles)
                    {
                        if (file != null)
                        {
                            AppendBmsEntry(file);
                        }
                    }
                    RefreshBmsView();
                    IncrementBmsRowsVersion();
                }
                if (bmsonChanged)
                {
                    PrepareBmsonForUpsert();
                    foreach (LR2SongDBExtended.bmson_song song in addedTargets.BmsonSongs)
                    {
                        if (song == null)
                        {
                            continue;
                        }
                        UpsertBmsonEntry(song);
                    }
                    RefreshBmsonView();
                    IncrementBmsonRowsVersion();
                }
                return new StorageRowsVersionSnapshot(
                    previousBmsRowsVersion,
                    previousBmsonRowsVersion,
                    bmsRowsVersion,
                    bmsonRowsVersion);
            }
        }
    }

    /// <summary>
    /// DB commit 後に渡された relocation facts、owner remove、path cleanup、追加行を一 command で反映します。
    /// old path は live owner から再取得せず、relocation request の事実を使います。
    /// </summary>
    /// <param name="relocationRequest">DB commit 済みの old/new path facts。</param>
    /// <param name="removalRequest">owner remove と path cleanup の要求。</param>
    /// <param name="protectedPathFacts">relocation destination として保護する path facts。</param>
    /// <param name="addedBmsFiles">追加する BMS 行。</param>
    /// <param name="addedBmsonSongs">追加する BMSON 行。</param>
    internal StorageRowsVersionSnapshot ApplyCatalogMutation(
        CatalogRelocationRequest relocationRequest,
        CatalogStorageRowsRemovalRequest removalRequest,
        IEnumerable<CatalogRelocationPathFact> protectedPathFacts,
        IEnumerable<BMSFile> addedBmsFiles = null,
        IEnumerable<LR2SongDBExtended.bmson_song> addedBmsonSongs = null)
    {
        List<BMSFile> addedBmsRows = [.. (addedBmsFiles ?? []).Where(file => file != null)];
        List<LR2SongDBExtended.bmson_song> addedBmsonRows =
            [.. (addedBmsonSongs ?? []).Where(song => song != null)];
        using (writeGate.GetWriterGuard())
        {
            lock (versionGate)
            {
                int previousBmsRowsVersion = bmsRowsVersion;
                int previousBmsonRowsVersion = bmsonRowsVersion;
                var removedBmsRows = new HashSet<BMSFile>(
                    removalRequest?.RemovedBmsRows ?? [],
                    ReferenceComparer<BMSFile>.Instance);
                var bmsPathCleanupKeys = new HashSet<string>(
                    removalRequest?.BmsPathCleanupKeys ?? [],
                    StringComparer.Ordinal);
                bmsPathCleanupKeys.ExceptWith(CreateProtectedPathKeys(
                    protectedPathFacts,
                    ChartFileKind.Bms));
                var removedBmsonRows = new HashSet<LR2SongDBExtended.bmson_song>(
                    removalRequest?.RemovedBmsonRows ?? [],
                    ReferenceComparer<LR2SongDBExtended.bmson_song>.Instance);
                var bmsonPathCleanupKeys = new HashSet<string>(
                    removalRequest?.BmsonPathCleanupKeys ?? [],
                    StringComparer.Ordinal);
                bmsonPathCleanupKeys.ExceptWith(CreateProtectedPathKeys(
                    protectedPathFacts,
                    ChartFileKind.Bmson));
                bool bmsRowsRemoved = removalRequest != null
                    && (removedBmsRows.Count > 0 || bmsPathCleanupKeys.Count > 0);
                bool bmsonRowsRemoved = removalRequest != null
                    && (removedBmsonRows.Count > 0 || bmsonPathCleanupKeys.Count > 0);

                ApplyRelocations(relocationRequest);
                if (bmsRowsRemoved)
                {
                    RemoveBmsMatches(removedBmsRows, bmsPathCleanupKeys);
                    RefreshBmsView();
                }
                if (bmsonRowsRemoved)
                {
                    RemoveBmsonMatches(removedBmsonRows, bmsonPathCleanupKeys);
                    RefreshBmsonView();
                }
                if (addedBmsRows.Count > 0)
                {
                    RemoveBmsNullEntries();
                    var addedPaths = new HashSet<string>(StringComparer.Ordinal);
                    foreach (BMSFile file in addedBmsRows)
                    {
                        if (!string.IsNullOrWhiteSpace(file.path))
                        {
                            addedPaths.Add(file.path);
                        }
                    }
                    foreach (string path in addedPaths)
                    {
                        RemoveBmsEntriesAtPath(path);
                    }
                    foreach (BMSFile file in addedBmsRows)
                    {
                        AppendBmsEntry(file);
                    }
                    RefreshBmsView();
                }
                if (addedBmsonRows.Count > 0)
                {
                    PrepareBmsonForUpsert();
                    foreach (LR2SongDBExtended.bmson_song song in addedBmsonRows)
                    {
                        UpsertBmsonEntry(song);
                    }
                    RefreshBmsonView();
                }

                bool bmsRowsRelocated = relocationRequest != null
                    && relocationRequest.BmsPathReplacements.Count > 0;
                bool bmsonRowsRelocated = relocationRequest != null
                    && relocationRequest.BmsonPathReplacements.Count > 0;
                if (bmsRowsRelocated || bmsRowsRemoved || addedBmsRows.Count > 0)
                {
                    IncrementBmsRowsVersion();
                }
                if (bmsonRowsRelocated || bmsonRowsRemoved || addedBmsonRows.Count > 0)
                {
                    IncrementBmsonRowsVersion();
                }
                return new StorageRowsVersionSnapshot(
                    previousBmsRowsVersion,
                    previousBmsonRowsVersion,
                    bmsRowsVersion,
                    bmsonRowsVersion);
            }
        }
    }

    private void ReplaceBmsRowsUnsafe(IEnumerable<BMSFile> rows)
    {
        var entries = new List<CatalogStorageSequenceEntry<BMSFile>>();
        foreach (BMSFile file in rows ?? [])
        {
            entries.Add(CreateEntry(file, file?.path, null));
        }
        bmsSequence = CatalogStorageIndexedSequence<BMSFile>.FromEntries(
            entries,
            CompareOrdinalEntries,
            sequenceWorkObserver);
        bmsEntriesByExactPath.Clear();
        bmsEntriesByOwner.Clear();
        bmsNullEntries.Clear();
        foreach (CatalogStorageSequenceEntry<BMSFile> entry in entries)
        {
            AddBmsLookup(entry);
        }
        RefreshBmsView();
    }

    private void ReplaceBmsonRowsUnsafe(
        IEnumerable<LR2SongDBExtended.bmson_song> rows)
    {
        var entries = new List<CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song>>();
        foreach (LR2SongDBExtended.bmson_song song in rows ?? [])
        {
            entries.Add(CreateEntry(song, song?.path, null));
        }
        bmsonSequence = CatalogStorageIndexedSequence<LR2SongDBExtended.bmson_song>.FromEntries(
            entries,
            CompareOrdinalEntries,
            sequenceWorkObserver);
        bmsonEntriesByExactPath.Clear();
        bmsonEntriesByOwner.Clear();
        foreach (CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song> entry in entries)
        {
            AddBmsonLookup(entry);
        }
        bmsonNeedsNormalization = true;
        RefreshBmsonView();
    }

    private void ApplyRelocations(CatalogRelocationRequest relocationRequest)
    {
        if (relocationRequest == null)
        {
            return;
        }
        bool hasBmsRelocation = relocationRequest.BmsPathReplacements.Count > 0;
        bool hasBmsonRelocation = relocationRequest.BmsonPathReplacements.Count > 0;
        foreach (BmsSongPathReplacement replacement in relocationRequest.BmsPathReplacements)
        {
            ReplaceBmsPathFacts(replacement);
        }
        foreach (BmsonSongPathReplacement replacement in relocationRequest.BmsonPathReplacements)
        {
            ReplaceBmsonPathFacts(replacement);
        }
        if (hasBmsonRelocation)
        {
            // relocation は現在位置を保ち、次の bmson upsert で current path を再正規化します。
            bmsonNeedsNormalization = true;
        }
        if (hasBmsRelocation)
        {
            RefreshBmsView();
        }
        if (hasBmsonRelocation)
        {
            RefreshBmsonView();
        }
    }

    private void ReplaceBmsPathFacts(BmsSongPathReplacement replacement)
    {
        if (replacement?.LiveOwner == null
            || !bmsEntriesByOwner.TryGetValue(replacement.LiveOwner, out List<CatalogStorageSequenceEntry<BMSFile>> entries))
        {
            return;
        }
        foreach (CatalogStorageSequenceEntry<BMSFile> entry in entries.ToArray())
        {
            if (!string.Equals(entry.ExactPath, replacement.OldPath, StringComparison.Ordinal))
            {
                continue;
            }
            var updated = new CatalogStorageSequenceEntry<BMSFile>(
                entry.Value,
                replacement.Song.path,
                entry.SortKey,
                entry.Ordinal);
            ReplaceBmsEntry(entry, updated);
        }
    }

    private void ReplaceBmsonPathFacts(BmsonSongPathReplacement replacement)
    {
        if (replacement?.LiveOwner == null
            || !bmsonEntriesByOwner.TryGetValue(
                replacement.LiveOwner,
                out List<CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song>> entries))
        {
            return;
        }
        foreach (CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song> entry in entries.ToArray())
        {
            if (!string.Equals(entry.ExactPath, replacement.OldPath, StringComparison.Ordinal))
            {
                continue;
            }
            var updated = new CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song>(
                entry.Value,
                replacement.Song.path,
                entry.SortKey,
                entry.Ordinal);
            ReplaceBmsonEntry(entry, updated);
        }
    }

    private void PrepareBmsonForUpsert()
    {
        if (!bmsonNeedsNormalization)
        {
            return;
        }
        var normalized = new List<CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song>>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song> entry in bmsonSequence.EnumerateEntries())
        {
            LR2SongDBExtended.bmson_song song = entry.Value;
            string path = song?.path;
            if (song == null || string.IsNullOrWhiteSpace(path) || !seenPaths.Add(path))
            {
                continue;
            }
            normalized.Add(CreateEntry(song, path, path));
        }
        normalized.Sort(CompareBmsonEntries);
        bmsonSequence = CatalogStorageIndexedSequence<LR2SongDBExtended.bmson_song>.FromEntries(
            normalized,
            CompareBmsonEntries,
            sequenceWorkObserver);
        bmsonEntriesByExactPath.Clear();
        bmsonEntriesByOwner.Clear();
        foreach (CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song> entry in normalized)
        {
            AddBmsonLookup(entry);
        }
        bmsonNeedsNormalization = false;
        RefreshBmsonView();
    }

    private void UpsertBmsonEntry(LR2SongDBExtended.bmson_song song)
    {
        if (song == null)
        {
            return;
        }
        string path = song.path;
        if (path == null)
        {
            throw new ArgumentNullException(nameof(song.path));
        }
        if (bmsonEntriesByExactPath.TryGetValue(
                path,
                out List<CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song>> existing)
            && existing.Count > 0)
        {
            CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song> current = existing[0];
            var replacement = new CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song>(
                song,
                path,
                current.SortKey,
                current.Ordinal);
            ReplaceBmsonEntry(current, replacement);
            return;
        }
        var added = CreateEntry(song, path, path);
        int insertionIndex = bmsonSequence.FindInsertionIndex(added);
        bmsonSequence = bmsonSequence.InsertAt(insertionIndex, added);
        AddBmsonLookup(added);
    }

    private void RemoveBmsMatches(
        ISet<BMSFile> removedOwners,
        ISet<string> pathCleanupKeys)
    {
        var entries = new HashSet<CatalogStorageSequenceEntry<BMSFile>>();
        foreach (BMSFile owner in removedOwners ?? Enumerable.Empty<BMSFile>())
        {
            if (owner != null && bmsEntriesByOwner.TryGetValue(owner, out List<CatalogStorageSequenceEntry<BMSFile>> ownerEntries))
            {
                entries.UnionWith(ownerEntries);
            }
        }
        foreach (string path in pathCleanupKeys ?? Enumerable.Empty<string>())
        {
            if (bmsEntriesByExactPath.TryGetValue(path, out List<CatalogStorageSequenceEntry<BMSFile>> pathEntries))
            {
                entries.UnionWith(pathEntries);
            }
        }
        RemoveBmsEntries(entries);
    }

    private void RemoveBmsonMatches(
        ISet<LR2SongDBExtended.bmson_song> removedOwners,
        ISet<string> pathCleanupKeys)
    {
        var entries = new HashSet<CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song>>();
        foreach (LR2SongDBExtended.bmson_song owner in removedOwners
            ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
        {
            if (owner != null
                && bmsonEntriesByOwner.TryGetValue(
                    owner,
                    out List<CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song>> ownerEntries))
            {
                entries.UnionWith(ownerEntries);
            }
        }
        foreach (string path in pathCleanupKeys ?? Enumerable.Empty<string>())
        {
            if (bmsonEntriesByExactPath.TryGetValue(
                    path,
                    out List<CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song>> pathEntries))
            {
                entries.UnionWith(pathEntries);
            }
        }
        RemoveBmsonEntries(entries);
    }

    private void RemoveBmsEntriesAtPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !bmsEntriesByExactPath.TryGetValue(path, out List<CatalogStorageSequenceEntry<BMSFile>> entries))
        {
            return;
        }
        RemoveBmsEntries([.. entries]);
    }

    private void RemoveBmsEntries(IEnumerable<CatalogStorageSequenceEntry<BMSFile>> entries)
    {
        foreach (CatalogStorageSequenceEntry<BMSFile> entry in entries ?? [])
        {
            int index = bmsSequence.FindIndex(entry);
            if (index < 0)
            {
                continue;
            }
            bmsSequence = bmsSequence.RemoveAt(index);
            RemoveBmsLookup(entry);
        }
    }

    private void RemoveBmsonEntries(IEnumerable<CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song>> entries)
    {
        foreach (CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song> entry in entries ?? [])
        {
            int index = bmsonSequence.FindIndex(entry);
            if (index < 0)
            {
                continue;
            }
            bmsonSequence = bmsonSequence.RemoveAt(index);
            RemoveBmsonLookup(entry);
        }
    }

    private void ReplaceBmsEntry(
        CatalogStorageSequenceEntry<BMSFile> current,
        CatalogStorageSequenceEntry<BMSFile> replacement)
    {
        int index = bmsSequence.FindIndex(current);
        if (index < 0)
        {
            return;
        }
        bmsSequence = bmsSequence.ReplaceAt(index, replacement);
        RemoveBmsLookup(current);
        AddBmsLookup(replacement);
    }

    private void ReplaceBmsonEntry(
        CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song> current,
        CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song> replacement)
    {
        int index = bmsonSequence.FindIndex(current);
        if (index < 0)
        {
            return;
        }
        bmsonSequence = bmsonSequence.ReplaceAt(index, replacement);
        RemoveBmsonLookup(current);
        AddBmsonLookup(replacement);
    }

    private void AppendBmsEntry(BMSFile file)
    {
        CatalogStorageSequenceEntry<BMSFile> entry = CreateEntry(file, file?.path, null);
        bmsSequence = bmsSequence.Append(entry);
        AddBmsLookup(entry);
    }

    private CatalogStorageSequenceEntry<T> CreateEntry<T>(
        T value,
        string exactPath,
        string sortKey)
    {
        return new CatalogStorageSequenceEntry<T>(value, exactPath, sortKey, ++nextOrdinal);
    }

    private void AddBmsLookup(CatalogStorageSequenceEntry<BMSFile> entry)
    {
        AddLookup(bmsEntriesByExactPath, entry.ExactPath, entry);
        if (entry.Value != null)
        {
            AddOwnerLookup(bmsEntriesByOwner, entry.Value, entry);
        }
        else
        {
            bmsNullEntries.Add(entry);
        }
    }

    private void RemoveBmsLookup(CatalogStorageSequenceEntry<BMSFile> entry)
    {
        RemoveLookup(bmsEntriesByExactPath, entry.ExactPath, entry);
        if (entry.Value != null)
        {
            RemoveOwnerLookup(bmsEntriesByOwner, entry.Value, entry);
        }
        else
        {
            bmsNullEntries.Remove(entry);
        }
    }

    private void AddBmsonLookup(CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song> entry)
    {
        AddLookup(bmsonEntriesByExactPath, entry.ExactPath, entry);
        if (entry.Value != null)
        {
            AddOwnerLookup(
                bmsonEntriesByOwner,
                entry.Value,
                entry);
        }
    }

    private void RemoveBmsonLookup(
        CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song> entry)
    {
        RemoveLookup(bmsonEntriesByExactPath, entry.ExactPath, entry);
        if (entry.Value != null)
        {
            RemoveOwnerLookup(bmsonEntriesByOwner, entry.Value, entry);
        }
    }

    private static void AddLookup<T>(
        IDictionary<string, List<CatalogStorageSequenceEntry<T>>> lookup,
        string path,
        CatalogStorageSequenceEntry<T> entry)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        if (!lookup.TryGetValue(path, out List<CatalogStorageSequenceEntry<T>> entries))
        {
            entries = [];
            lookup.Add(path, entries);
        }
        entries.Add(entry);
    }

    private static void AddOwnerLookup<TValue, TKey>(
        IDictionary<TKey, List<CatalogStorageSequenceEntry<TValue>>> lookup,
        TKey key,
        CatalogStorageSequenceEntry<TValue> entry)
    {
        if (key is null)
        {
            return;
        }
        if (!lookup.TryGetValue(key, out List<CatalogStorageSequenceEntry<TValue>> entries))
        {
            entries = [];
            lookup.Add(key, entries);
        }
        entries.Add(entry);
    }

    private static void RemoveLookup<T>(
        IDictionary<string, List<CatalogStorageSequenceEntry<T>>> lookup,
        string path,
        CatalogStorageSequenceEntry<T> entry)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !lookup.TryGetValue(path, out List<CatalogStorageSequenceEntry<T>> entries))
        {
            return;
        }
        entries.Remove(entry);
        if (entries.Count == 0)
        {
            lookup.Remove(path);
        }
    }

    private static void RemoveOwnerLookup<TValue, TKey>(
        IDictionary<TKey, List<CatalogStorageSequenceEntry<TValue>>> lookup,
        TKey key,
        CatalogStorageSequenceEntry<TValue> entry)
    {
        if (key is null || !lookup.TryGetValue(key, out List<CatalogStorageSequenceEntry<TValue>> entries))
        {
            return;
        }
        entries.Remove(entry);
        if (entries.Count == 0)
        {
            lookup.Remove(key);
        }
    }

    private void RefreshBmsView()
    {
        bmsRows = new CatalogStorageReadOnlyView<BMSFile>(bmsSequence);
    }

    private void RemoveBmsNullEntries()
    {
        if (bmsNullEntries.Count == 0)
        {
            return;
        }
        CatalogStorageSequenceEntry<BMSFile>[] entries = bmsNullEntries.ToArray();
        bmsNullEntries.Clear();
        RemoveBmsEntries(entries);
    }

    private void RefreshBmsonView()
    {
        bmsonRows = new CatalogStorageReadOnlyView<LR2SongDBExtended.bmson_song>(bmsonSequence);
    }

    private CatalogStorageRowsSnapshot CreateSnapshot()
    {
        return new CatalogStorageRowsSnapshot(
            bmsRows,
            bmsonRows,
            bmsRowsVersion,
            bmsonRowsVersion);
    }

    private static HashSet<string> CreateProtectedPathKeys(
        IEnumerable<CatalogRelocationPathFact> protectedPathFacts,
        ChartFileKind kind)
    {
        return new HashSet<string>(
            (protectedPathFacts ?? [])
                .Where(fact => fact?.Kind == kind)
                .Select(fact => fact.NewPath)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);
    }

    /// <summary>現在の BMS/BMSON version を一つの snapshot として返します。</summary>
    internal StorageRowsVersionSnapshot CaptureVersionSnapshot()
    {
        lock (versionGate)
        {
            return new StorageRowsVersionSnapshot(bmsRowsVersion, bmsonRowsVersion);
        }
    }

    /// <summary>version と sequence count を O(1) で返します。</summary>
    internal CatalogStorageRowsStateSnapshot CaptureStateSnapshot()
    {
        lock (versionGate)
        {
            return new CatalogStorageRowsStateSnapshot(
                bmsRowsVersion,
                bmsonRowsVersion,
                bmsSequence.Count,
                bmsonSequence.Count);
        }
    }

    /// <summary>現在の sequence root と version だけを捕捉します。</summary>
    internal CatalogStorageRowsSnapshot CaptureSnapshot()
    {
        using IDisposable readGuard = writeGate.IsWriteLockHeld
            || writeGate.IsReadLockHeld
            || writeGate.IsUpgradeableReadLockHeld
            ? null
            : writeGate.GetReaderGuard();
        lock (versionGate)
        {
            return CreateSnapshot();
        }
    }

    private void IncrementBmsRowsVersion()
    {
        Interlocked.Increment(ref bmsRowsVersion);
    }

    private void IncrementBmsonRowsVersion()
    {
        Interlocked.Increment(ref bmsonRowsVersion);
    }

    private StorageRowsVersionSnapshot CreateVersionSnapshot(
        int previousBmsRowsVersion,
        int previousBmsonRowsVersion)
    {
        return new StorageRowsVersionSnapshot(
            previousBmsRowsVersion,
            previousBmsonRowsVersion,
            bmsRowsVersion,
            bmsonRowsVersion);
    }

    private static int CompareOrdinalEntries<T>(
        CatalogStorageSequenceEntry<T> left,
        CatalogStorageSequenceEntry<T> right)
    {
        return left.Ordinal.CompareTo(right.Ordinal);
    }

    private static int CompareBmsonEntries(
        CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song> left,
        CatalogStorageSequenceEntry<LR2SongDBExtended.bmson_song> right)
    {
        int compared = StringComparer.OrdinalIgnoreCase.Compare(left.SortKey, right.SortKey);
        return compared != 0 ? compared : left.Ordinal.CompareTo(right.Ordinal);
    }

    private sealed class ReferenceComparer<T> : IEqualityComparer<T>
        where T : class
    {
        internal static ReferenceComparer<T> Instance { get; } = new();

        public bool Equals(T left, T right) => ReferenceEquals(left, right);

        public int GetHashCode(T value) => value == null ? 0 : RuntimeHelpers.GetHashCode(value);
    }
}

/// <summary>storage sequence の membership・順序・version を同時に捕捉した read view です。</summary>
internal sealed class CatalogStorageRowsSnapshot
{
    /// <summary>sequence view と対応する version を結び付けます。</summary>
    /// <param name="bmsRows">捕捉した BMS read view。</param>
    /// <param name="bmsonRows">捕捉した BMSON read view。</param>
    /// <param name="bmsRowsVersion">捕捉時の BMS version。</param>
    /// <param name="bmsonRowsVersion">捕捉時の BMSON version。</param>
    internal CatalogStorageRowsSnapshot(
        IReadOnlyList<BMSFile> bmsRows,
        IReadOnlyList<LR2SongDBExtended.bmson_song> bmsonRows,
        int bmsRowsVersion,
        int bmsonRowsVersion)
    {
        BmsRows = bmsRows ?? Array.Empty<BMSFile>();
        BmsonRows = bmsonRows ?? Array.Empty<LR2SongDBExtended.bmson_song>();
        BmsRowsVersion = bmsRowsVersion;
        BmsonRowsVersion = bmsonRowsVersion;
    }

    /// <summary>捕捉時の BMS membership/order view。</summary>
    internal IReadOnlyList<BMSFile> BmsRows { get; }

    /// <summary>捕捉時の BMSON membership/order view。</summary>
    internal IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonRows { get; }

    /// <summary>捕捉時の BMS version。</summary>
    internal int BmsRowsVersion { get; }

    /// <summary>捕捉時の BMSON version。</summary>
    internal int BmsonRowsVersion { get; }
}

/// <summary>storage version と sequence count の軽量な状態値です。</summary>
internal readonly struct CatalogStorageRowsStateSnapshot
{
    /// <summary>version と count を結び付けます。</summary>
    /// <param name="bmsRowsVersion">BMS version。</param>
    /// <param name="bmsonRowsVersion">BMSON version。</param>
    /// <param name="bmsRowCount">BMS sequence count。</param>
    /// <param name="bmsonRowCount">BMSON sequence count。</param>
    internal CatalogStorageRowsStateSnapshot(
        int bmsRowsVersion,
        int bmsonRowsVersion,
        int bmsRowCount,
        int bmsonRowCount)
    {
        BmsRowsVersion = bmsRowsVersion;
        BmsonRowsVersion = bmsonRowsVersion;
        BmsRowCount = bmsRowCount;
        BmsonRowCount = bmsonRowCount;
    }

    /// <summary>BMS version。</summary>
    internal int BmsRowsVersion { get; }

    /// <summary>BMSON version。</summary>
    internal int BmsonRowsVersion { get; }

    /// <summary>BMS sequence count。</summary>
    internal int BmsRowCount { get; }

    /// <summary>BMSON sequence count。</summary>
    internal int BmsonRowCount { get; }
}
