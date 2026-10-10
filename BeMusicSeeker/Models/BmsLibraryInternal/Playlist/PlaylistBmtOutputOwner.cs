using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Newtonsoft.Json.Linq;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// beatoraja の playlist BMT ファイル、manifest、config URL 同期を所有します。
/// <para>
/// 準備した immutable な table snapshot は playlist aggregate / hydration owner から取得し、
/// 変更元の生存P権限の下でdurable file mutationとconfig反映を直接待ちます。
/// </para>
/// </summary>
internal sealed class PlaylistBmtOutputOwner
{
    internal sealed class OrderedProjectionProgressPublisher
    {
        private readonly object syncRoot = new();

        private readonly Queue<(int Completed, int Total, string TableName)> pending = new();

        private readonly int total;

        private readonly Action<int, int, string> publish;

        private int completed;

        private bool draining;

        private Exception publicationFailure;

        internal OrderedProjectionProgressPublisher(int total, Action<int, int, string> publish)
        {
            this.total = total;
            this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        }

        internal void PublishNext(string tableName)
        {
            bool shouldDrain;
            lock (syncRoot)
            {
                if (publicationFailure != null)
                {
                    throw new InvalidOperationException(
                        "Playlist BMT projection progress publication previously failed.",
                        publicationFailure);
                }
                pending.Enqueue((++completed, total, tableName));
                shouldDrain = !draining;
                if (shouldDrain)
                {
                    draining = true;
                }
            }
            if (!shouldDrain)
            {
                return;
            }

            while (true)
            {
                (int Completed, int Total, string TableName) fact;
                lock (syncRoot)
                {
                    if (pending.Count == 0)
                    {
                        draining = false;
                        return;
                    }
                    fact = pending.Dequeue();
                }
                try
                {
                    publish(fact.Completed, fact.Total, fact.TableName);
                }
                catch (Exception exception)
                {
                    lock (syncRoot)
                    {
                        publicationFailure = exception;
                        pending.Clear();
                        draining = false;
                    }
                    throw;
                }
            }
        }
    }

    private readonly PlaylistAggregatePersistenceOwner playlistAggregatePersistenceOwner;

    private readonly PlaylistEntriesHydrationOwner playlistEntriesHydrationOwner;

    private readonly Func<BeatorajaBmtOptionsSnapshot> optionsProvider;

    private readonly Func<Func<BmtSongHashResolveRequest, Tuple<string, string>>> songHashResolverFactory;

    private readonly Action<string> logPerformance;
    private readonly Action<Exception, string> logWarning;
    private readonly Func<bool> isShutdownRequested;
    private readonly ChartFileOperationSynchronizer mutationAdmission;
    private long exportProgressOperationSeed;

    internal PlaylistBmtOutputOwner(
        PlaylistAggregatePersistenceOwner playlistAggregatePersistenceOwner,
        PlaylistEntriesHydrationOwner playlistEntriesHydrationOwner,
        Func<BeatorajaBmtOptionsSnapshot> optionsProvider,
        Func<Func<BmtSongHashResolveRequest, Tuple<string, string>>> songHashResolverFactory,
        Action<string> logPerformance,
        Action<Exception, string> logWarning,
        Func<bool> isShutdownRequested,
        ChartFileOperationSynchronizer mutationAdmission)
    {
        this.playlistAggregatePersistenceOwner = playlistAggregatePersistenceOwner ?? throw new ArgumentNullException(nameof(playlistAggregatePersistenceOwner));
        this.playlistEntriesHydrationOwner = playlistEntriesHydrationOwner ?? throw new ArgumentNullException(nameof(playlistEntriesHydrationOwner));
        this.optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
        this.songHashResolverFactory = songHashResolverFactory;
        this.logPerformance = logPerformance;
        this.logWarning = logWarning;
        this.isShutdownRequested = isShutdownRequested ?? throw new ArgumentNullException(nameof(isShutdownRequested));
        this.mutationAdmission = mutationAdmission ?? throw new ArgumentNullException(nameof(mutationAdmission));
    }

    /// <summary>専用件数通知へ渡す実行中要求を捕捉します。</summary>
    internal Func<string, long, OperationProgressRequest> ProgressRequestFactory { get; set; }

    /// <summary>同じ実行周のBMT要求の開始と終端を通知します。</summary>
    internal Action<OperationProgressRequest, bool> RequestProgressReporter { get; set; }

    internal Action<PlaylistSyncProgressSnapshot> ExportProgressReporter { get; set; }

    /// <summary>元の操作 session に依存せず、ファイル操作 lock の外で失敗事実を配送します。</summary>
    internal Action<IReadOnlyList<BmtTableExportService.FileOperationFailure>> FailureReporter { get; set; }

    internal bool IsShutdownRequested => isShutdownRequested();

    internal void RequestShutdown(string reason)
        => Log("beatoraja_bmt_output shutdown requested reason=" + FormatTextForLog(reason));

    internal static int NormalizePersistedBeatorajaBmtPlaylistSettings(IEnumerable<BMSTable> tables)
    {
        List<BMSTable> tableList = [.. (tables ?? []).Where(table => table != null)];
        return NormalizeBeatorajaBmtSortOrder(tableList) + NormalizeBeatorajaBmtOutputTargets(tableList);
    }

    internal static int NormalizeBeatorajaBmtSortOrder(IEnumerable<BMSTable> tables)
    {
        List<BMSTable> orderedTables = [.. (tables ?? [])
            .Where(table => table != null)
            .OrderBy(table => IsValidBeatorajaBmtSort(table.bmt_sort) ? 0 : 1)
            .ThenBy(table => GetBeatorajaBmtSortOrTail(table.bmt_sort))
            .ThenBy(table => table.name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(table => table.playlist_id ?? int.MaxValue)];
        int changedCount = 0;
        for (int index = 0; index < orderedTables.Count; index++)
        {
            int normalizedSort = index + 1;
            if (orderedTables[index].bmt_sort != normalizedSort)
            {
                orderedTables[index].bmt_sort = normalizedSort;
                changedCount++;
            }
        }
        return changedCount;
    }

    internal static int ResolveNextBeatorajaBmtSort(IEnumerable<BMSTable> tables)
    {
        return ((tables ?? [])
            .Where(table => table != null && IsValidBeatorajaBmtSort(table.bmt_sort))
            .Select(table => table.bmt_sort.Value)
            .DefaultIfEmpty(0)
            .Max()) + 1;
    }

    /// <param name="reason">既存の出力要求理由。</param>
    /// <param name="cleanupTablePath">旧出力先の後片付け対象。</param>
    /// <param name="originatingRequest">項目読込み受領や外部同期の実行周から引き継ぐ表示発生元。出力自身の要求版と操作IDは保持します。</param>
    /// <summary>全表の必要出力を同じPで直接待ちます。管理台帳の全量cleanupはこの全体要求だけが行います。</summary>
    /// <param name="capability">外側が公開とcleanup終端まで保持する生存P権限。nullは新規非待機受付です。</param>
    internal Task ExportAllAsync(string reason, string cleanupTablePath = null,
        OperationProgressRequest originatingRequest = null, LibraryFileMutationCapability capability = null)
        => ExecuteAsync(null, [], reason, true, cleanupTablePath, originatingRequest, capability);

    /// <summary>確定した変更表と削除表だけを一括出力します。部分集合で他の管理表をstale削除しません。</summary>
    /// <param name="capability">変更元が保持する生存P権限。内部継続は取得し直さず借用します。</param>
    internal Task ExportTablesAsync(IEnumerable<BMSTable> tables, string reason,
        LibraryFileMutationCapability capability = null, IEnumerable<BMSTable> removedTables = null)
        => ExecuteAsync(tables, removedTables ?? [], reason, false, null, null, capability);

    /// <summary>確定した削除表の管理ファイルとURLを、同じPで回収します。</summary>
    internal Task RemoveTablesAsync(IEnumerable<BMSTable> tables, string reason, LibraryFileMutationCapability capability = null)
        => ExecuteAsync([], tables, reason, false, null, null, capability);

    /// <summary>保存済み全表順序と管理台帳からbeatoraja設定のURL順を更新し、実終端を返します。</summary>
    internal Task SyncUrlsAsync(string reason, LibraryFileMutationCapability capability = null)
        => ExecuteAsync([], [], reason, false, null, null, capability);

    private async Task ExecuteAsync(IEnumerable<BMSTable> tables, IEnumerable<BMSTable> removedTables,
        string reason, bool all, string cleanupTablePath, OperationProgressRequest originatingRequest,
        LibraryFileMutationCapability capability)
    {
        using LibraryFileMutationLease accepted = capability != null ? mutationAdmission.Borrow(capability)
            : mutationAdmission.TryEnter(out IDisposable lease) ? (LibraryFileMutationLease)lease
            : throw new InvalidOperationException(Resources.Warn_LibraryOperationBusy);
        BeatorajaBmtOptionsSnapshot options = GetOptions();
        IReadOnlyList<BMSTable> changed = tables == null ? GetTablesSnapshot() : [.. tables.Where(table => table != null).Distinct()];
        IReadOnlyList<BMSTable> removed = [.. removedTables.Where(table => table != null).Distinct()];
        long requestVersion = Interlocked.Increment(ref exportProgressOperationSeed);
        OperationProgressRequest request = ProgressRequestFactory?.Invoke("playlist_bmt_output", requestVersion)
            ?? new OperationProgressRequest(0, 0, "playlist_bmt_output", requestVersion);
        if (request != null && originatingRequest != null)
        {
            request = request with { Generation = originatingRequest.Generation, OperationToken = originatingRequest.OperationToken };
        }
        RequestProgressReporter?.Invoke(request, true);
        try
        {
            await Task.Run(() => Execute(changed, removed, options, reason, all, cleanupTablePath, request)).ConfigureAwait(false);
        }
        finally
        {
            RequestProgressReporter?.Invoke(request, false);
        }
    }

    private void Execute(IReadOnlyList<BMSTable> changed, IReadOnlyList<BMSTable> removed,
        BeatorajaBmtOptionsSnapshot options, string reason, bool all, string cleanupTablePath, OperationProgressRequest request)
    {
        if (IsShutdownRequested) { throw new OperationCanceledException("Playlist BMT output was cancelled by shutdown."); }
        string outputPath = GetTablePath(options);
        bool enabled = IsOutputEnabled(options);
        List<BmtTableExportService.FileOperationFailure> failures = [];
        long operationId = Interlocked.Increment(ref exportProgressOperationSeed);
        bool progressStarted = false;
        Exception primaryFailure = null;
        Exception notificationFailure = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(cleanupTablePath)
                && (!enabled || !IsSameTablePath(cleanupTablePath, outputPath))
                && (enabled || !options.KeepBeatorajaBmtFilesWhenOutputDisabled))
            {
                BmtTableExportService.ExportResult cleanup = BmtTableExportService.CleanupManagedFiles(cleanupTablePath);
                failures.AddRange(cleanup.Failures);
                SyncManagedTableUrls(cleanupTablePath, cleanup.PreviousManagedTables, cleanup.CurrentManagedTables, options);
            }
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                List<BmtTableExportService.ManagedTableUrlEntry> previous = BmtTableExportService.ReadManagedTableUrls(outputPath);
                if (!enabled && !options.KeepBeatorajaBmtFilesWhenOutputDisabled && all)
                {
                    failures.AddRange(BmtTableExportService.CleanupManagedFiles(outputPath).Failures);
                }
                foreach (BMSTable table in removed.Concat(changed.Where(table => !IsBeatorajaBmtOutputTarget(table)
                    || (!enabled && !options.KeepBeatorajaBmtFilesWhenOutputDisabled))))
                {
                    BmtTableExportService.ExportResult deletion = BmtTableExportService.RemoveManagedPlaylist(outputPath, GetPlaylistIdentity(table));
                    failures.AddRange(deletion.Failures);
                }
                if (enabled)
                {
                    List<BMSTable> outputTables = [.. changed.Where(IsBeatorajaBmtOutputTarget)
                    .OrderBy(table => GetBeatorajaBmtSortOrTail(table.bmt_sort))
                    .ThenBy(table => table.name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(table => table.playlist_id ?? int.MaxValue)];
                    List<Tuple<BMSTable, BmtTableExportService.PlaylistExportMetadata>> targets = [.. outputTables
                    .Select(table => Tuple.Create(table, BmtTableExportService.CreatePlaylistExportMetadata(table)))];
                    BmtTableExportService.ExportPlan plan = BmtTableExportService.CreateExportPlan(outputPath,
                        targets.Select(target => target.Item2), cleanupStaleManagedFiles: all);
                    List<BMSTable> projectionTables = [.. targets.Where(target => plan.RequiresProjection(target.Item2)).Select(target => target.Item1)];
                    BeatorajaBmtHashOutputMode hashMode = BmtTableExportService.NormalizeHashOutputMode(options.BeatorajaBmtHashOutputMode);
                    Func<BmtSongHashResolveRequest, Tuple<string, string>> resolver = projectionTables.Count == 0 || hashMode == BeatorajaBmtHashOutputMode.Original
                        ? null : songHashResolverFactory?.Invoke();
                    if (projectionTables.Count > 0)
                    {
                        progressStarted = true;
                        ReportProgress(operationId, true, projectionTables.Count, 0, string.Empty, request);
                    }
                    List<Tuple<string, JObject>> data = BuildTableDataSetSnapshot(projectionTables, reason, hashMode, resolver,
                        progressStarted ? (completed, total, name) => ReportProgress(operationId, true, total, completed, name, request) : null);
                    BmtTableExportService.ExportResult export = BmtTableExportService.ExportTableDataSet(outputPath, data, plan,
                        progressStarted ? (completed, total, name) => ReportProgress(operationId, true,
                            projectionTables.Count + total, projectionTables.Count + completed, name, request) : null);
                    failures.AddRange(export.Failures);
                    Log("beatoraja_bmt_output completed reason=" + FormatTextForLog(reason)
                        + " requested=" + changed.Count + " projected=" + projectionTables.Count
                        + " written=" + export.WrittenCount + " skipped=" + export.SkippedWriteCount + " removed=" + export.RemovedCount);
                }
                SyncManagedTableUrls(outputPath, previous, enabled || options.KeepBeatorajaBmtFilesWhenOutputDisabled
                    ? BmtTableExportService.ReadManagedTableUrls(outputPath) : [], options);
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            AddFailure(failures, outputPath, exception);
        }
        finally
        {
            try
            {
                if (progressStarted) { ReportProgress(operationId, false, 0, 0, string.Empty, request); }
            }
            catch (Exception exception) { notificationFailure = exception; }
            try { ReportFailures(failures); }
            catch (Exception exception) { notificationFailure = notificationFailure == null ? exception : new AggregateException(notificationFailure, exception); }
        }
        if (primaryFailure is OperationCanceledException && notificationFailure == null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        if (failures.Count > 0 || notificationFailure != null)
        {
            List<Exception> causes = [];
            if (primaryFailure != null) { causes.Add(primaryFailure); }
            else { causes.AddRange(failures.Select(failure => new System.IO.IOException(failure.Path + ": " + failure.Cause))); }
            if (notificationFailure != null) { causes.Add(notificationFailure); }
            throw new PlaylistMutationPostCommitException(reason, causes.Count == 1 ? causes[0] : new AggregateException(causes));
        }
    }

    private BeatorajaBmtOptionsSnapshot GetOptions()
    {
        return optionsProvider() ?? throw new InvalidOperationException("beatoraja BMT options provider returned null.");
    }

    private static bool IsOutputEnabled(BeatorajaBmtOptionsSnapshot options)
    {
        return options.EnableBeatorajaBmtOutput && !string.IsNullOrWhiteSpace(GetTablePath(options));
    }

    private string GetTablePath()
    {
        return GetTablePath(GetOptions());
    }

    private static string GetTablePath(BeatorajaBmtOptionsSnapshot options)
    {
        if (!string.IsNullOrWhiteSpace(options.BeatorajaRootPath) && BeatorajaConfigService.IsBeatorajaRootPathValid(options.BeatorajaRootPath))
        {
            return BeatorajaConfigService.GetTablePath(options.BeatorajaRootPath);
        }
        return options.BeatorajaBmtTablePath;
    }

    private void SyncManagedTableUrls(
        string tablePath,
        IEnumerable<BmtTableExportService.ManagedTableUrlEntry> previousManagedTables,
        IEnumerable<BmtTableExportService.ManagedTableUrlEntry> currentManagedTables,
        BeatorajaBmtOptionsSnapshot options)
    {
        if (string.IsNullOrWhiteSpace(options.BeatorajaRootPath) || !BeatorajaConfigService.IsBeatorajaRootPathValid(options.BeatorajaRootPath))
        {
            return;
        }
        string configuredTablePath = BeatorajaConfigService.GetTablePath(options.BeatorajaRootPath);
        if (!string.IsNullOrWhiteSpace(tablePath) && !IsSameTablePath(tablePath, configuredTablePath))
        {
            return;
        }
        List<string> previousUrls = [.. (previousManagedTables ?? [])
            .Select(entry => entry?.Url)
            .Where(url => !string.IsNullOrWhiteSpace(url))];
        IReadOnlyDictionary<string, BeatorajaBmtTableUrlSortKey> sortKeys = CreateTableUrlSortKeysSnapshot();
        List<string> currentUrls = options.RegisterBeatorajaBmtUrls
            ? BuildBeatorajaManagedTableUrlsForConfigSync(currentManagedTables, sortKeys)
            : [];
        BeatorajaConfigService.SyncTableUrls(options.BeatorajaRootPath, currentUrls, previousUrls);
    }

    private static bool IsSameTablePath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        return string.Equals(LongPathFileSystem.NormalizePathForStorage(left), LongPathFileSystem.NormalizePathForStorage(right), StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyDictionary<string, BeatorajaBmtTableUrlSortKey> CreateTableUrlSortKeysSnapshot()
    {
        return GetTablesSnapshot()
            .Where(table => table?.playlist_id.HasValue == true)
            .GroupBy(GetPlaylistIdentity, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    BMSTable table = group.First();
                    return new BeatorajaBmtTableUrlSortKey
                    {
                        Sort = GetBeatorajaBmtSortOrTail(table.bmt_sort),
                        Name = table.name ?? string.Empty,
                        PlaylistId = table.playlist_id ?? int.MaxValue
                    };
                },
                StringComparer.Ordinal);
    }

    internal static List<string> BuildBeatorajaManagedTableUrlsForConfigSync(
        IEnumerable<BmtTableExportService.ManagedTableUrlEntry> currentManagedTables,
        IReadOnlyDictionary<string, BeatorajaBmtTableUrlSortKey> sortKeys)
    {
        return [.. (currentManagedTables ?? [])
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Url))
            .OrderBy(entry => ResolveTableUrlSortKey(entry, sortKeys).IsKnown ? 0 : 1)
            .ThenBy(entry => ResolveTableUrlSortKey(entry, sortKeys).Sort)
            .ThenBy(entry => ResolveTableUrlSortKey(entry, sortKeys).Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => ResolveTableUrlSortKey(entry, sortKeys).PlaylistId)
            .ThenBy(entry => entry.PlaylistIdentity ?? string.Empty, StringComparer.Ordinal)
            .Select(entry => entry.Url)];
    }

    private static BeatorajaBmtTableUrlSortKey ResolveTableUrlSortKey(
        BmtTableExportService.ManagedTableUrlEntry entry,
        IReadOnlyDictionary<string, BeatorajaBmtTableUrlSortKey> sortKeys)
    {
        if (entry != null
            && !string.IsNullOrEmpty(entry.PlaylistIdentity)
            && sortKeys != null
            && sortKeys.TryGetValue(entry.PlaylistIdentity, out BeatorajaBmtTableUrlSortKey key))
        {
            return key;
        }
        return new BeatorajaBmtTableUrlSortKey
        {
            IsKnown = false,
            Sort = int.MaxValue,
            Name = entry?.Name ?? string.Empty,
            PlaylistId = int.MaxValue
        };
    }

    internal sealed class BeatorajaBmtTableUrlSortKey
    {
        public bool IsKnown { get; set; } = true;

        public int Sort { get; set; }

        public string Name { get; set; }

        public int PlaylistId { get; set; }
    }

    private List<BMSTable> GetTablesSnapshot()
    {
        return [.. (playlistAggregatePersistenceOwner.GetActiveCollectionSnapshot()?.Tables ?? [])
            .Where(table => table != null)];
    }

    private static string GetPlaylistIdentity(BMSTable table)
    {
        return table?.playlist_id?.ToString(CultureInfo.InvariantCulture);
    }

    private List<Tuple<string, JObject>> BuildTableDataSetSnapshot(
        List<BMSTable> tablesSnapshot,
        string reason,
        BeatorajaBmtHashOutputMode hashOutputMode,
        Func<BmtSongHashResolveRequest, Tuple<string, string>> hashResolverFunc,
        Action<int, int, string> progressReporter)
    {
        if (tablesSnapshot == null || tablesSnapshot.Count == 0)
        {
            return [];
        }
        var projectionInputs = new BeatorajaBmtTableProjectionInput[tablesSnapshot.Count];
        for (int index = 0; index < tablesSnapshot.Count; index++)
        {
            projectionInputs[index] = CreateProjectionInput(tablesSnapshot[index], reason, index);
        }
        var projectionResults = new Tuple<string, JObject>[projectionInputs.Length];
        OrderedProjectionProgressPublisher orderedProgress =
            progressReporter == null
                ? null
                : new OrderedProjectionProgressPublisher(projectionInputs.Length, progressReporter);
        Parallel.ForEach(
            projectionInputs,
            new ParallelOptions { MaxDegreeOfParallelism = ResolveProjectionDegree(projectionInputs.Length) },
            input =>
            {
                if (input?.Snapshot != null)
                {
                    BmtTableExportService.ISongHashResolver hashResolver = hashResolverFunc == null ? null : new BeatorajaBmtSongHashResolver(hashResolverFunc);
                    JObject tableData = BmtTableExportService.BuildTableData(input.Snapshot, hashResolver, hashOutputMode);
                    if (tableData != null)
                    {
                        projectionResults[input.Index] = Tuple.Create(input.PlaylistIdentity, tableData);
                    }
                }
                orderedProgress?.PublishNext(input?.TableName);
            });
        return [.. projectionResults.Where(result => result != null)];
    }

    private BeatorajaBmtTableProjectionInput CreateProjectionInput(BMSTable table, string reason, int index)
    {
        if (table == null)
        {
            return new BeatorajaBmtTableProjectionInput { Index = index };
        }
        playlistEntriesHydrationOwner.EnsurePlaylistEntriesLoaded(table, reason ?? "BeatorajaBmtExport");
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            return new BeatorajaBmtTableProjectionInput
            {
                Index = index,
                PlaylistIdentity = GetPlaylistIdentity(table),
                TableName = table.name,
                Snapshot = BmtTableExportService.CreateProjectionSnapshot(table)
            };
        }
    }

    private static int ResolveProjectionDegree(int count)
    {
        return Math.Max(1, Math.Min(Math.Min(Environment.ProcessorCount, 4), Math.Max(count, 1)));
    }

    private void ReportProgress(long operationId, bool isActive, int totalCount, int completedCount, string currentTableName, OperationProgressRequest request = null)
    {
        ExportProgressReporter?.Invoke(new PlaylistSyncProgressSnapshot
        {
            IsActive = isActive,
            OperationId = operationId,
            Source = "bmt",
            Request = request,
            TotalTableCount = totalCount,
            CompletedTableCount = completedCount,
            CurrentTableName = currentTableName ?? string.Empty,
            LabelFormat = Resources.Beatoraja_bmt_export_progress_label_format,
            SingleLabel = Resources.Beatoraja_bmt_export_progress_single_label
        });
    }

    private static bool IsBeatorajaBmtOutputTarget(BMSTable table)
    {
        return table?.is_bmt_output != false;
    }

    private static bool IsValidBeatorajaBmtSort(int? sort)
    {
        return sort.HasValue && sort.Value > 0;
    }

    private static int GetBeatorajaBmtSortOrTail(int? sort)
    {
        return IsValidBeatorajaBmtSort(sort) ? sort.Value : int.MaxValue;
    }

    private static int NormalizeBeatorajaBmtOutputTargets(IEnumerable<BMSTable> tables)
    {
        int changedCount = 0;
        foreach (BMSTable table in (tables ?? []).Where(table => table != null && !table.is_bmt_output.HasValue))
        {
            table.is_bmt_output = true;
            changedCount++;
        }
        return changedCount;
    }

    private static string FormatTextForLog(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
    }

    private static string FormatBool(bool value)
    {
        return value.ToString().ToLowerInvariant();
    }

    private void Log(string message)
    {
        logPerformance?.Invoke(message);
    }

    private static void AddFailure(List<BmtTableExportService.FileOperationFailure> failures, string path, Exception exception)
    {
        failures.Add(new BmtTableExportService.FileOperationFailure(path,
            exception.Message + (exception.InnerException == null ? string.Empty : Environment.NewLine + exception.GetBaseException().Message)));
    }

    private void ReportFailures(List<BmtTableExportService.FileOperationFailure> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        foreach (BmtTableExportService.FileOperationFailure failure in failures)
        {
            logWarning?.Invoke(new System.IO.IOException(failure.Cause), "beatoraja_bmt_failed path=" + FormatTextForLog(failure.Path));
        }

        FailureReporter?.Invoke(Array.AsReadOnly(failures.ToArray()));
    }

    private sealed class BeatorajaBmtSongHashResolver(Func<BmtSongHashResolveRequest, Tuple<string, string>> resolve)
        : BmtTableExportService.ISongHashResolver
    {
        public BmtTableExportService.SongHashResolution Resolve(BmtSongHashResolveRequest request)
        {
            Tuple<string, string> resolved = resolve?.Invoke(request);
            return resolved == null ? null : new BmtTableExportService.SongHashResolution(resolved.Item1, resolved.Item2);
        }
    }

    private sealed class BeatorajaBmtTableProjectionInput
    {
        public int Index { get; set; }

        public string PlaylistIdentity { get; set; }

        public string TableName { get; set; }

        public BmtTableExportService.TableDataProjectionSnapshot Snapshot { get; set; }
    }
}
