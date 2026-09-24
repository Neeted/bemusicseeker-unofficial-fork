using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Ribbit.Util.Extensions;
using SQLite;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns startup readiness boundaries and the deferred external-playlist import queue.
/// </summary>
/// <remarks>
/// Playlist readiness is deliberately independent from install-estimation readiness.  A
/// caller may therefore admit a playlist request as soon as the command is received while
/// still deferring all playlist I/O until the required local playlist hydration boundary.
/// The coordinator also owns the queue lifecycle so shutdown cannot leave a producer or
/// consumer alive after the playlist model has become terminal.
/// </remarks>
internal sealed class StartupReadinessCoordinator
{
    private readonly object syncRoot = new();

    private readonly Queue<Uri> pendingExternalPlaylistImports = new();

    private readonly CancellationTokenSource shutdownCancellation = new();

    private TaskCompletionSource<bool> requiredPlaylistReadiness = CreateCompletedCompletion();

    private TaskCompletionSource<bool> installEstimationReadiness = CreatePendingCompletion();

    private TaskCompletionSource<bool> externalImportDrainCompletion = CreateCompletedCompletion();

    private bool requiredPlaylistReady = true;

    private bool playlistInitializationActive;

    private bool requiredPlaylistReadinessFaulted;

    private bool externalImportDrainActive;

    private bool externalImportDrainStarted;

    private int shutdownRequested;

    /// <summary>
    /// Gets the required local playlist readiness task.
    /// </summary>
    internal Task RequiredPlaylistReadiness
    {
        get
        {
            lock (syncRoot)
            {
                return requiredPlaylistReadiness.Task;
            }
        }
    }

    /// <summary>
    /// Gets the independent install-estimation readiness task.
    /// </summary>
    internal Task InstallEstimationReadiness
    {
        get
        {
            lock (syncRoot)
            {
                return installEstimationReadiness.Task;
            }
        }
    }

    /// <summary>
    /// Gets the cancellation token shared by readiness waiters and deferred imports.
    /// </summary>
    internal CancellationToken ShutdownToken => shutdownCancellation.Token;

    /// <summary>
    /// Gets whether this coordinator has entered terminal shutdown.
    /// </summary>
    internal bool IsShutdownRequested => Volatile.Read(ref shutdownRequested) != 0;

    /// <summary>
    /// Gets whether required playlist hydration has reached its current boundary.
    /// </summary>
    internal bool IsRequiredPlaylistReady
    {
        get
        {
            lock (syncRoot)
            {
                return requiredPlaylistReady;
            }
        }
    }

    /// <summary>
    /// Gets whether the current required playlist readiness boundary faulted.
    /// </summary>
    internal bool IsRequiredPlaylistReadinessFaulted
    {
        get
        {
            lock (syncRoot)
            {
                return requiredPlaylistReadinessFaulted;
            }
        }
    }

    /// <summary>
    /// Gets whether install-estimation readiness has been published independently.
    /// </summary>
    internal bool IsInstallEstimationReady
    {
        get
        {
            lock (syncRoot)
            {
                return installEstimationReadiness.Task.IsCompletedSuccessfully;
            }
        }
    }

    /// <summary>
    /// Gets whether an external import drain is active or queued.
    /// </summary>
    internal bool HasExternalImportWork
    {
        get
        {
            lock (syncRoot)
            {
                return externalImportDrainActive || pendingExternalPlaylistImports.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets the current external import drain completion task.
    /// </summary>
    internal Task ExternalImportDrainCompletion
    {
        get
        {
            lock (syncRoot)
            {
                return externalImportDrainCompletion.Task;
            }
        }
    }

    /// <summary>
    /// Starts a required playlist initialization boundary.
    /// </summary>
    /// <param name="reason">Diagnostic reason for the boundary.</param>
    internal void BeginPlaylistInitialization(string reason = null)
    {
        lock (syncRoot)
        {
            if (IsShutdownRequested || playlistInitializationActive)
            {
                return;
            }
            playlistInitializationActive = true;
            requiredPlaylistReady = false;
            if (!externalImportDrainActive && pendingExternalPlaylistImports.Count == 0)
            {
                requiredPlaylistReadinessFaulted = false;
            }
            requiredPlaylistReadiness = CreatePendingCompletion();
        }
    }

    /// <summary>
    /// Publishes completion of the required local playlist readiness boundary.
    /// </summary>
    internal void MarkRequiredPlaylistReady()
    {
        TaskCompletionSource<bool> completion;
        lock (syncRoot)
        {
            if (IsShutdownRequested || requiredPlaylistReadiness.Task.IsCompleted)
            {
                return;
            }
            playlistInitializationActive = false;
            requiredPlaylistReady = true;
            requiredPlaylistReadinessFaulted = false;
            completion = requiredPlaylistReadiness;
        }
        completion.TrySetResult(true);
    }

    /// <summary>
    /// Faults the current readiness boundary without converting the failure into success.
    /// </summary>
    /// <param name="exception">The readiness failure.</param>
    internal void FailRequiredPlaylistReadiness(Exception exception)
    {
        if (exception == null)
        {
            return;
        }
        TaskCompletionSource<bool> completion;
        lock (syncRoot)
        {
            if (IsShutdownRequested || requiredPlaylistReadiness.Task.IsCompleted)
            {
                return;
            }
            playlistInitializationActive = false;
            requiredPlaylistReady = false;
            requiredPlaylistReadinessFaulted = true;
            completion = requiredPlaylistReadiness;
        }
        completion.TrySetException(exception);
    }

    /// <summary>
    /// Publishes independent install-estimation readiness.
    /// </summary>
    internal void MarkInstallEstimationReady()
    {
        lock (syncRoot)
        {
            if (IsShutdownRequested)
            {
                return;
            }
            installEstimationReadiness.TrySetResult(true);
        }
    }

    /// <summary>
    /// Resets install-estimation readiness for a new startup operation.
    /// </summary>
    internal void ResetInstallEstimationReadiness()
    {
        lock (syncRoot)
        {
            if (IsShutdownRequested || !installEstimationReadiness.Task.IsCompleted)
            {
                return;
            }
            installEstimationReadiness = CreatePendingCompletion();
        }
    }

    /// <summary>
    /// Waits asynchronously for required playlist readiness or terminal cancellation.
    /// </summary>
    /// <param name="cancellationToken">An optional caller cancellation token.</param>
    internal async Task WaitForRequiredPlaylistReadinessAsync(CancellationToken cancellationToken = default)
    {
        Task readinessTask;
        CancellationToken shutdownToken;
        lock (syncRoot)
        {
            if (IsShutdownRequested)
            {
                throw new OperationCanceledException(shutdownCancellation.Token);
            }
            if (requiredPlaylistReady)
            {
                return;
            }
            readinessTask = requiredPlaylistReadiness.Task;
            shutdownToken = shutdownCancellation.Token;
        }

        using CancellationTokenSource linkedCancellation = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken)
            : null;
        await readinessTask.WaitAsync(
            linkedCancellation?.Token ?? shutdownToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically admits external-playlist URI requests into the shared deferred queue.
    /// </summary>
    /// <param name="uris">Absolute URI requests in caller order.</param>
    /// <param name="shouldStartDrain">Whether the caller owns starting the single consumer.</param>
    /// <returns><see langword="true"/> when all valid requests were admitted.</returns>
    internal bool TryAdmitExternalPlaylistImports(
        IEnumerable<Uri> uris,
        out bool shouldStartDrain)
    {
        List<Uri> validUris = [.. (uris ?? []).Where(uri => uri != null && uri.IsAbsoluteUri)];
        shouldStartDrain = false;
        if (validUris.Count == 0)
        {
            return false;
        }

        lock (syncRoot)
        {
            if (IsShutdownRequested || requiredPlaylistReadinessFaulted)
            {
                return false;
            }
            foreach (Uri uri in validUris)
            {
                pendingExternalPlaylistImports.Enqueue(uri);
            }
            if (!externalImportDrainActive)
            {
                externalImportDrainActive = true;
                externalImportDrainCompletion = CreatePendingCompletion();
                shouldStartDrain = true;
            }
            return true;
        }
    }

    /// <summary>
    /// Claims the single external-import consumer after a producer admitted work.
    /// </summary>
    /// <returns><see langword="true"/> when this caller owns the drain lifecycle.</returns>
    internal bool TryBeginExternalPlaylistImportDrain()
    {
        lock (syncRoot)
        {
            if (IsShutdownRequested || !externalImportDrainActive || externalImportDrainStarted)
            {
                return false;
            }
            if (requiredPlaylistReadinessFaulted)
            {
                pendingExternalPlaylistImports.Clear();
                externalImportDrainActive = false;
                externalImportDrainStarted = false;
                externalImportDrainCompletion.TrySetResult(true);
                return false;
            }
            externalImportDrainStarted = true;
            return true;
        }
    }

    /// <summary>
    /// Dequeues one FIFO import batch for the coordinator's single consumer.
    /// </summary>
    internal IReadOnlyList<Uri> DequeueExternalPlaylistImportBatch()
    {
        lock (syncRoot)
        {
            if (requiredPlaylistReadinessFaulted)
            {
                pendingExternalPlaylistImports.Clear();
                externalImportDrainActive = false;
                externalImportDrainStarted = false;
                externalImportDrainCompletion.TrySetResult(true);
                return [];
            }
            if (pendingExternalPlaylistImports.Count == 0)
            {
                externalImportDrainActive = false;
                externalImportDrainStarted = false;
                externalImportDrainCompletion.TrySetResult(true);
                return [];
            }
            List<Uri> batch = [.. pendingExternalPlaylistImports];
            pendingExternalPlaylistImports.Clear();
            return batch;
        }
    }

    /// <summary>
    /// Completes a consumer cycle, preserving a pending cycle if a producer raced the drain.
    /// </summary>
    /// <returns><see langword="true"/> when no follow-up consumer is needed.</returns>
    internal bool CompleteExternalPlaylistImportDrain()
    {
        lock (syncRoot)
        {
            if (IsShutdownRequested || requiredPlaylistReadinessFaulted)
            {
                pendingExternalPlaylistImports.Clear();
                externalImportDrainActive = false;
                externalImportDrainStarted = false;
                externalImportDrainCompletion.TrySetResult(true);
                return true;
            }
            if (pendingExternalPlaylistImports.Count > 0)
            {
                externalImportDrainStarted = false;
                return false;
            }
            externalImportDrainActive = false;
            externalImportDrainStarted = false;
            externalImportDrainCompletion.TrySetResult(true);
            return true;
        }
    }

    /// <summary>
    /// Requests terminal shutdown for readiness waiters and deferred import producers/consumers.
    /// </summary>
    /// <param name="reason">Diagnostic shutdown reason.</param>
    internal void RequestShutdown(string reason = null)
    {
        if (Interlocked.Exchange(ref shutdownRequested, 1) != 0)
        {
            return;
        }

        TaskCompletionSource<bool> readinessCompletion;
        TaskCompletionSource<bool> installCompletion;
        TaskCompletionSource<bool> drainCompletion;
        lock (syncRoot)
        {
            requiredPlaylistReady = false;
            playlistInitializationActive = false;
            pendingExternalPlaylistImports.Clear();
            readinessCompletion = requiredPlaylistReadiness;
            installCompletion = installEstimationReadiness;
            drainCompletion = externalImportDrainCompletion;
            if (!externalImportDrainStarted)
            {
                externalImportDrainActive = false;
                externalImportDrainStarted = false;
                drainCompletion.TrySetResult(true);
            }
            readinessCompletion.TrySetCanceled(shutdownCancellation.Token);
            installCompletion.TrySetCanceled(shutdownCancellation.Token);
        }
        shutdownCancellation.Cancel();
    }

    private static TaskCompletionSource<bool> CreatePendingCompletion()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource<bool> CreateCompletedCompletion()
    {
        TaskCompletionSource<bool> completion = CreatePendingCompletion();
        completion.TrySetResult(true);
        return completion;
    }
}

internal sealed class Lr2NormalFolderMtimeSnapshot(
    IReadOnlyList<string> rootDirectories,
    IReadOnlyDictionary<string, LR2SongDB.folder> existingRowsByPath,
    bool readOnly,
    long dbLockWaitMs,
    long elapsedMs)
{
    public IReadOnlyList<string> RootDirectories { get; } = rootDirectories ?? [];

    public IReadOnlyDictionary<string, LR2SongDB.folder> ExistingRowsByPath { get; } =
        existingRowsByPath ?? new Dictionary<string, LR2SongDB.folder>(StringComparer.OrdinalIgnoreCase);

    public int ExistingRowCount => ExistingRowsByPath.Count;

    public bool ReadOnly { get; } = readOnly;

    public long DbLockWaitMs { get; } = dbLockWaitMs;

    public long ElapsedMs { get; } = elapsedMs;
}

/// <summary>
/// Immutable startup observation for a folder whose timestamp may be the LR2
/// leap-year sentinel. The repair operation revalidates this observation after
/// admission, immediately before changing the filesystem.
/// </summary>
internal sealed class LeapYearFolderRepairCandidate
{
    private readonly FolderCatalogIdentity catalogIdentity;

    /// <summary>
    /// Creates a candidate from the pre-admission filesystem timestamp and
    /// catalog row snapshot.
    /// </summary>
    internal LeapYearFolderRepairCandidate(
        string originalFolderPath,
        string normalizedPath,
        DateTime lastWriteTime,
        LR2SongDB.folder folder)
    {
        OriginalFolderPath = originalFolderPath ?? string.Empty;
        Path = normalizedPath ?? string.Empty;
        LastWriteTime = lastWriteTime;
        catalogIdentity = FolderCatalogIdentity.Capture(folder);
    }

    /// <summary>
    /// Gets the exact path stored by the catalog row. It is used for the
    /// targeted database lookup and is intentionally not normalized.
    /// </summary>
    public string OriginalFolderPath { get; }

    public string Path { get; }

    public DateTime LastWriteTime { get; }

    /// <summary>
    /// Determines whether the current catalog row is the same row observed
    /// before admission. All persisted folder fields are compared so a path
    /// replacement cannot inherit an approval made for the previous row.
    /// </summary>
    internal bool MatchesCatalogRow(LR2SongDB.folder folder)
        => catalogIdentity.Matches(folder);

    private sealed class FolderCatalogIdentity
    {
        private FolderCatalogIdentity(LR2SongDB.folder folder)
        {
            Title = folder?.title;
            Subtitle = folder?.subtitle;
            Category = folder?.category;
            InfoA = folder?.info_a;
            InfoB = folder?.info_b;
            Command = folder?.command;
            Path = NormalizePath(folder?.path);
            Type = folder?.type;
            Banner = folder?.banner;
            Parent = folder?.parent;
            Date = folder?.date;
            Max = folder?.max;
            AddDate = folder?.adddate;
        }

        private string Title { get; }

        private string Subtitle { get; }

        private string Category { get; }

        private string InfoA { get; }

        private string InfoB { get; }

        private string Command { get; }

        private string Path { get; }

        private int? Type { get; }

        private string Banner { get; }

        private string Parent { get; }

        private int? Date { get; }

        private int? Max { get; }

        private int? AddDate { get; }

        internal static FolderCatalogIdentity Capture(LR2SongDB.folder folder)
            => new(folder);

        internal bool Matches(LR2SongDB.folder folder)
        {
            return folder != null
                && string.Equals(Title, folder.title, StringComparison.Ordinal)
                && string.Equals(Subtitle, folder.subtitle, StringComparison.Ordinal)
                && string.Equals(Category, folder.category, StringComparison.Ordinal)
                && string.Equals(InfoA, folder.info_a, StringComparison.Ordinal)
                && string.Equals(InfoB, folder.info_b, StringComparison.Ordinal)
                && string.Equals(Command, folder.command, StringComparison.Ordinal)
                && string.Equals(Path, NormalizePath(folder.path), StringComparison.OrdinalIgnoreCase)
                && Type == folder.type
                && string.Equals(Banner, folder.banner, StringComparison.Ordinal)
                && string.Equals(Parent, folder.parent, StringComparison.Ordinal)
                && Date == folder.date
                && Max == folder.max
                && AddDate == folder.adddate;
        }

        private static string NormalizePath(string path)
            => path?.TrimEnd('\\', '/') ?? string.Empty;
    }
}

/// <summary>
/// Detached outcome of the post-admission leap-year timestamp repair.
/// </summary>
internal sealed class LeapYearFolderRepairResult
{
    public int RepairedCount { get; internal set; }

    public List<string> RepairedPaths { get; } = [];

    public List<(string Path, Exception Exception)> Failures { get; } = [];
}

/// <summary>
/// Loads initialization phases against snapshot inputs owned by BMSLibrary.
/// The facade must acquire the required locks before invoking phase methods.
/// </summary>
internal sealed class BmsLibraryInitializationService
{
    private const string SongCatalogRawSelectSql =
        "SELECT hash, title, subtitle, artist, subartist, genre, tag, path, type, folder, stagefile, banner, backbmp, parent, level, difficulty, maxbpm, minbpm, mode, judge, longnote, bga, random, date, favorite, txt, karinotes, adddate, exlevel FROM song;";

    private const string MaintenanceRawSelectSql =
        "SELECT hash, path, encoding, is_encoding_fixed, wav_files_existing, wav_files_defined, bga_files_existing, bga_files_defined, movie_files_existing, movie_files_defined, is_stagefile_existing, is_stagefile_defined, is_banner_existing, is_banner_defined, is_backbmp_existing, is_backbmp_defined, is_files_warning_ignored, lr2_warning_flags, lr2_resource_max_relative_cp932_bytes, lr2_resource_has_parent_traversal FROM maintenance;";


    private readonly FileScanParseCommitOwner fileScanParseCommitOwner;

    public BmsLibraryInitializationService()
        : this(null, null, null, null)
    {
    }

    internal BmsLibraryInitializationService(int? fileDiffParserDegreeOverride, ChartInfoBuildService chartInfoBuildService = null, int? inlineChartInfoBatchSizeOverride = null, int? fileDiffCommitChunkSizeOverride = null)
    {
        fileScanParseCommitOwner = new FileScanParseCommitOwner(
            fileDiffParserDegreeOverride,
            chartInfoBuildService,
            inlineChartInfoBatchSizeOverride,
            fileDiffCommitChunkSizeOverride);
    }

    /// <summary>
    /// Revalidates approved startup candidates and repairs their timestamps
    /// while the caller owns the mutation lease. This method performs no UI
    /// callback; callers publish warnings and failures after releasing locks.
    /// </summary>
    internal LeapYearFolderRepairResult RepairLeapYearFolderTimestamps(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<LeapYearFolderRepairCandidate> approvedCandidates,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        Func<Exception, string> getDisplayedExceptionMessage = null)
    {
        var result = new LeapYearFolderRepairResult();
        List<LeapYearFolderRepairCandidate> approved = [.. (approvedCandidates ?? [])
            .Where(candidate => candidate != null)
            .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
        if (dbGateway == null || approved.Count == 0)
        {
            return result;
        }

        using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
        if (!TableExists(songDb, SQLiteTable<LR2SongDB.folder>.GetTableName()))
        {
            return result;
        }

        // The initial folder loop already identified the small approved set.
        // Query only those exact catalog paths; do not enumerate unrelated
        // rows or probe their filesystem timestamps again.
        IReadOnlyList<LR2SongDB.folder> existingRows =
            Lr2FolderExistingRowLookup.QueryExactPaths(
                songDb,
                approved.Select(candidate => candidate.OriginalFolderPath));
        var rowsByExactPath = existingRows
            .Where(folder => folder != null && !string.IsNullOrWhiteSpace(folder.path))
            .GroupBy(folder => folder.path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var repairedRows = new List<LR2SongDB.folder>();

        foreach (LeapYearFolderRepairCandidate candidate in approved)
        {
            string directoryPath = candidate.Path?.TrimEnd('\\', '/') ?? string.Empty;
            if (!rowsByExactPath.TryGetValue(candidate.OriginalFolderPath, out LR2SongDB.folder folder)
                || !candidate.MatchesCatalogRow(folder)
                || folder.type != 1
                || !(folder.date < 0 || !folder.date.HasValue)
                || !LongPathFileSystem.DirectoryExists(directoryPath))
            {
                continue;
            }

            DateTime lastWriteTime = LongPathFileSystem.GetLastWriteTime(directoryPath, isDirectory: true);
            if (lastWriteTime != candidate.LastWriteTime
                || !IsLeapYearTimestamp(lastWriteTime))
            {
                continue;
            }

            if (fileMutationService == null)
            {
                result.Failures.Add((
                    directoryPath,
                    new InvalidOperationException(
                        "A file mutation service is required to repair the LR2 leap-year folder timestamp.")));
                continue;
            }

            try
            {
                fileMutationService.SetTimestamps(
                    directoryPath,
                    isDirectory: true,
                    creationTime: null,
                    lastWriteTime: DateTime.Now,
                    targetOnlyFileMutationOptions);
                folder.adddate = null;
                folder.date = null;
                result.RepairedPaths.Add(directoryPath);
                repairedRows.Add(folder);
                result.RepairedCount++;
            }
            catch (Exception exception)
            {
                result.Failures.Add((
                    directoryPath,
                    new IOException(
                        getDisplayedExceptionMessage?.Invoke(exception) ?? exception.Message,
                        exception)));
            }
        }

        if (result.RepairedCount == 0)
        {
            return result;
        }

        songDb.BeginTransaction();
        foreach (LR2SongDB.folder repairedRow in repairedRows)
        {
            songDb.InsertOrReplace(repairedRow, typeof(LR2SongDB.folder));
        }
        songDb.Commit();
        return result;
    }

    public SongTableLoadResult LoadSongTable(
        BmsLibraryDbGateway dbGateway,
        BmsLibraryOptionsSnapshot options,
        IBmsLibraryDialogService dialogService,
        IFileMutationService fileMutationService,
        FileMutationOptions targetOnlyFileMutationOptions,
        Func<Exception, string> getDisplayedExceptionMessage,
        Action<string> logInstallPerformance = null,
        Action<string> logDebugTrace = null)
    {
        var result = new SongTableLoadResult();
        if (dbGateway == null)
        {
            return result;
        }
        using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
        result.Pragmas.AddRange(songDb.TryApplyReadOptimizedPragmas(options?.EnableReadOptimizedPragmas ?? false));
        if (result.Pragmas.Count > 0)
        {
            logInstallPerformance?.Invoke("db_read_pragmas scope=song_tbl_load " + string.Join(" ", result.Pragmas));
        }

        var stopwatchSongTableLoad = Stopwatch.StartNew();
        var stopwatchSongCount = Stopwatch.StartNew();
        try
        {
            result.SongTableCount = Math.Max(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
        }
        catch
        {
        }
        stopwatchSongCount.Stop();
        result.SongCountMs = stopwatchSongCount.ElapsedMilliseconds;

        var stopwatchSongMaterialize = Stopwatch.StartNew();
        List<BMSFile> loadedSongs;
        if (UseRawSongCatalogLoader())
        {
            loadedSongs = LoadSongCatalogRaw(songDb, result);
        }
        else
        {
            result.SongMaterializeMode = "sqlite_net";
            loadedSongs = (result.SongTableCount > 0L && result.SongTableCount <= int.MaxValue)
                ? new List<BMSFile>((int)result.SongTableCount)
                : [];
            using (BMSFile.SuppressPropertyChangedScope())
            {
                foreach (BMSFile item in songDb.Table<BMSFile>())
                {
                    loadedSongs.Add(item);
                }
            }
        }
        stopwatchSongMaterialize.Stop();
        result.SongMaterializeMs = stopwatchSongMaterialize.ElapsedMilliseconds;
        stopwatchSongTableLoad.Stop();
        result.SongTableLoadMs = stopwatchSongTableLoad.ElapsedMilliseconds;

        List<BMSFile> deletedFiles = [];
        logDebugTrace?.Invoke("relative path and invalid md5 check");
        if (!string.IsNullOrWhiteSpace(songDb.LR2RootPath))
        {
            NormalizeSongTable(
                songDb,
                loadedSongs,
                deletedFiles,
                result,
                logInstallPerformance);
        }
        else
        {
            NormalizeStandaloneSongPathCompatibility(songDb, loadedSongs, result);
        }
        logDebugTrace?.Invoke("relative path check end");

        var stopwatchChartDigestMapLoad = Stopwatch.StartNew();
        Dictionary<string, string> chartDigestMap = dbGateway.LoadChartDigestMap(songDb);
        stopwatchChartDigestMapLoad.Stop();
        result.ChartDigestMapLoadMs = stopwatchChartDigestMapLoad.ElapsedMilliseconds;
        foreach (KeyValuePair<string, string> item in chartDigestMap)
        {
            result.ChartDigestMap[item.Key] = item.Value;
        }

        var stopwatchBmsonTableLoad = Stopwatch.StartNew();
        if (TableExists(songDb, SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName()))
        {
            foreach (LR2SongDBExtended.bmson_song item in songDb.Table<LR2SongDBExtended.bmson_song>())
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.path))
                {
                    result.LoadedBmsonSongs.Add(item);
                }
            }
        }
        stopwatchBmsonTableLoad.Stop();
        result.BmsonTableLoadMs = stopwatchBmsonTableLoad.ElapsedMilliseconds;

        HashSet<BMSFile> deletedFileSet = deletedFiles.Count > 0 ? [.. deletedFiles] : null;
        var stopwatchChartDigestApply = Stopwatch.StartNew();
        using (BMSFile.SuppressPropertyChangedScope())
        {
            foreach (BMSFile item in loadedSongs)
            {
                if (deletedFileSet != null && deletedFileSet.Contains(item))
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(item.hash) && result.ChartDigestMap.TryGetValue(item.hash, out string sha256))
                {
                    item.ApplySha256(sha256);
                }
                result.LoadedFiles.Add(item);
            }
        }
        stopwatchChartDigestApply.Stop();
        result.ChartDigestApplyMs = stopwatchChartDigestApply.ElapsedMilliseconds;

        // chart_info is display/search metadata. Loading and applying every row can dominate startup
        // on large libraries, so the application hydrates it after the core install workflow is operable.
        logInstallPerformance?.Invoke(
            "song_tbl_load_projection projection=catalog mode=" + result.SongMaterializeMode
            + " readMs=" + result.SongTableLoadMs
            + " materializeMs=" + result.SongMaterializeMs
            + " rawReadMs=" + result.SongRawReadMs
            + " rawObjectMs=" + result.SongRawObjectMs
            + " rawRows=" + result.SongRawRows
            + " rows=" + result.SongTableCount
            + " chartDigestReadMs=" + result.ChartDigestMapLoadMs
            + " chartDigestApplyMs=" + result.ChartDigestApplyMs
            + " bmsonReadMs=" + result.BmsonTableLoadMs
            + " bmsonRows=" + result.LoadedBmsonSongs.Count
            + " chartDigestRows=" + result.ChartDigestMap.Count);
        logInstallPerformance?.Invoke(
            "song_tbl_load_io song_read_ms=" + result.SongTableLoadMs
            + " song_count_ms=" + result.SongCountMs
            + " song_materialize_ms=" + result.SongMaterializeMs
            + " song_count=" + result.SongTableCount
            + " folder_read_ms=" + result.FolderTableLoadMs
            + " db_write_required=" + result.DbWriteRequired.ToString().ToLowerInvariant()
            + " db_write_ms=" + result.DbWriteMs);
        return result;
    }

    private static bool UseRawSongCatalogLoader()
    {
        string mode = Environment.GetEnvironmentVariable("BMS_SONG_TABLE_LOAD_MODE");
        return !string.Equals(mode, "sqlite_net", StringComparison.OrdinalIgnoreCase);
    }

    private static List<BMSFile> LoadSongCatalogRaw(LR2SongDBExtended songDb, SongTableLoadResult result)
    {
        List<BMSFile> loadedSongs = (result.SongTableCount > 0L && result.SongTableCount <= int.MaxValue)
            ? new List<BMSFile>((int)result.SongTableCount)
            : [];
        result.SongMaterializeMode = "raw_string";
        SQLiteCommand command = songDb.CreateCommand(SongCatalogRawSelectSql);
        long objectTicks = 0L;
        long stopwatchFrequency = Stopwatch.Frequency;
        var totalStopwatch = Stopwatch.StartNew();
        int rawRows = ((LR2SongDBExtended.SQLiteCommandExtended)command).ForEachRawValueAsString(delegate (string[] values)
        {
            long objectStart = Stopwatch.GetTimestamp();
            loadedSongs.Add(BMSFile.FromSongTableRawValues(values));
            objectTicks += Stopwatch.GetTimestamp() - objectStart;
        });
        totalStopwatch.Stop();
        long totalMs = totalStopwatch.ElapsedMilliseconds;
        result.SongRawRows = rawRows;
        result.SongRawObjectMs = stopwatchFrequency > 0L ? objectTicks * 1000L / stopwatchFrequency : 0L;
        result.SongRawReadMs = Math.Max(0L, totalMs - result.SongRawObjectMs);
        return loadedSongs;
    }

    public MaintenanceTableHydrationResult LoadMaintenanceTable(
        BmsLibraryDbGateway dbGateway,
        BmsLibraryOptionsSnapshot options,
        Action<string> logInstallPerformance = null)
    {
        var result = new MaintenanceTableHydrationResult();
        if (dbGateway == null)
        {
            return result;
        }

        var totalStopwatch = Stopwatch.StartNew();
        using LR2SongDBExtended songDb = dbGateway.OpenSongDbReadOnly();
        result.ReadOnly = songDb.IsReadOnlyConnection;
        result.DbLockWaitMs = songDb.ProcessLockWaitMs;
        result.Pragmas.AddRange(songDb.TryApplyReadOptimizedPragmas(options?.EnableReadOptimizedPragmas ?? false));
        if (result.Pragmas.Count > 0)
        {
            logInstallPerformance?.Invoke("db_read_pragmas scope=maintenance_hydration " + string.Join(" ", result.Pragmas));
        }

        var stopwatchMaintenanceTableLoad = Stopwatch.StartNew();
        var stopwatchMaintenanceCount = Stopwatch.StartNew();
        try
        {
            result.MaintenanceTableCount = TableExists(songDb, SQLiteTable<LR2SongDBExtended.maintenance>.GetTableName())
                ? Math.Max(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance;"))
                : 0L;
        }
        catch
        {
        }
        stopwatchMaintenanceCount.Stop();
        result.MaintenanceCountMs = stopwatchMaintenanceCount.ElapsedMilliseconds;

        List<BMSFileMaintenanceInfo> maintenanceInfos = (result.MaintenanceTableCount > 0L && result.MaintenanceTableCount <= int.MaxValue)
            ? new List<BMSFileMaintenanceInfo>((int)result.MaintenanceTableCount)
            : [];
        var stopwatchMaintenanceMaterialize = Stopwatch.StartNew();
        if (result.MaintenanceTableCount > 0L)
        {
            if (UseRawMaintenanceTableLoader())
            {
                LoadMaintenanceTableRaw(songDb, maintenanceInfos, result);
            }
            else
            {
                result.MaintenanceMaterializeMode = "sqlite_net";
                using (BMSFileMaintenanceInfo.SuppressPropertyChangedScope())
                {
                    foreach (BMSFileMaintenanceInfo item in songDb.Table<BMSFileMaintenanceInfo>())
                    {
                        maintenanceInfos.Add(item);
                    }
                }
            }
        }
        stopwatchMaintenanceMaterialize.Stop();
        result.MaintenanceMaterializeMs = stopwatchMaintenanceMaterialize.ElapsedMilliseconds;
        stopwatchMaintenanceTableLoad.Stop();
        result.MaintenanceTableLoadMs = stopwatchMaintenanceTableLoad.ElapsedMilliseconds;

        var stopwatchMaintenanceMapBuild = Stopwatch.StartNew();
        foreach (BMSFileMaintenanceInfo maintenanceInfo in maintenanceInfos)
        {
            if (!string.IsNullOrWhiteSpace(maintenanceInfo.path) && !result.MaintenanceMap.ContainsKey(maintenanceInfo.path))
            {
                result.MaintenanceMap[maintenanceInfo.path] = maintenanceInfo;
            }
        }
        stopwatchMaintenanceMapBuild.Stop();
        result.MaintenanceMapBuildMs = stopwatchMaintenanceMapBuild.ElapsedMilliseconds;
        totalStopwatch.Stop();
        result.TotalMs = totalStopwatch.ElapsedMilliseconds;
        return result;
    }

    private static bool UseRawMaintenanceTableLoader()
    {
        string mode = Environment.GetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE");
        return !string.Equals(mode, "sqlite_net", StringComparison.OrdinalIgnoreCase);
    }

    private static void LoadMaintenanceTableRaw(
        LR2SongDBExtended songDb,
        List<BMSFileMaintenanceInfo> maintenanceInfos,
        MaintenanceTableHydrationResult result)
    {
        result.MaintenanceMaterializeMode = "raw_string";
        SQLiteCommand command = songDb.CreateCommand(MaintenanceRawSelectSql);
        long objectTicks = 0L;
        long stopwatchFrequency = Stopwatch.Frequency;
        var totalStopwatch = Stopwatch.StartNew();
        using (BMSFileMaintenanceInfo.SuppressPropertyChangedScope())
        {
            int rawRows = ((LR2SongDBExtended.SQLiteCommandExtended)command).ForEachRawValueAsString(delegate (string[] values)
            {
                long objectStart = Stopwatch.GetTimestamp();
                maintenanceInfos.Add(CreateMaintenanceInfoFromRawValues(values));
                objectTicks += Stopwatch.GetTimestamp() - objectStart;
            });
            result.MaintenanceRawRows = rawRows;
        }
        totalStopwatch.Stop();
        long totalMs = totalStopwatch.ElapsedMilliseconds;
        result.MaintenanceRawObjectMs = stopwatchFrequency > 0L ? objectTicks * 1000L / stopwatchFrequency : 0L;
        result.MaintenanceRawReadMs = Math.Max(0L, totalMs - result.MaintenanceRawObjectMs);
    }

    private static BMSFileMaintenanceInfo CreateMaintenanceInfoFromRawValues(string[] values)
    {
        return new BMSFileMaintenanceInfo
        {
            hash = GetRawValue(values, 0),
            path = GetRawValue(values, 1),
            encoding = GetRawValue(values, 2),
            is_encoding_fixed = ParseBoolean(GetRawValue(values, 3)),
            wav_files_existing = ParseNullableInt(GetRawValue(values, 4)),
            wav_files_defined = ParseNullableInt(GetRawValue(values, 5)),
            bga_files_existing = ParseNullableInt(GetRawValue(values, 6)),
            bga_files_defined = ParseNullableInt(GetRawValue(values, 7)),
            movie_files_existing = ParseNullableInt(GetRawValue(values, 8)),
            movie_files_defined = ParseNullableInt(GetRawValue(values, 9)),
            is_stagefile_existing = ParseNullableBoolean(GetRawValue(values, 10)),
            is_stagefile_defined = ParseNullableBoolean(GetRawValue(values, 11)),
            is_banner_existing = ParseNullableBoolean(GetRawValue(values, 12)),
            is_banner_defined = ParseNullableBoolean(GetRawValue(values, 13)),
            is_backbmp_existing = ParseNullableBoolean(GetRawValue(values, 14)),
            is_backbmp_defined = ParseNullableBoolean(GetRawValue(values, 15)),
            is_files_warning_ignored = ParseBoolean(GetRawValue(values, 16)),
            lr2_warning_flags = ParseNullableInt(GetRawValue(values, 17)),
            lr2_resource_max_relative_cp932_bytes = ParseNullableInt(GetRawValue(values, 18)),
            lr2_resource_has_parent_traversal = ParseNullableBoolean(GetRawValue(values, 19))
        };
    }

    private static string GetRawValue(string[] values, int index)
    {
        return index >= 0 && index < values.Length ? values[index] : null;
    }

    private static int? ParseNullableInt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
    }

    private static bool ParseBoolean(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && (string.Equals(value, "1", StringComparison.Ordinal)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static bool? ParseNullableBoolean(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : ParseBoolean(value);
    }

    public Lr2NormalFolderMtimeSnapshot LoadNormalFolderMtimeSnapshot(
        BmsLibraryDbGateway dbGateway,
        BmsLibraryOptionsSnapshot options,
        IEnumerable<string> rootDirectories,
        Action<string> logInstallPerformance = null)
    {
        if (dbGateway == null
            || options?.OperationModeLR2DB != true)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        List<string> roots = NormalizeNormalFolderMtimeRoots(rootDirectories);
        if (roots.Count == 0)
        {
            stopwatch.Stop();
            return new Lr2NormalFolderMtimeSnapshot(
                roots,
                new Dictionary<string, LR2SongDB.folder>(StringComparer.OrdinalIgnoreCase),
                readOnly: false,
                dbLockWaitMs: 0L,
                stopwatch.ElapsedMilliseconds);
        }

        using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
        IReadOnlyList<LR2SongDB.folder> rows = Lr2FolderExistingRowLookup.QueryNormalFolderMtimePathPrefixScopes(
            songDb,
            roots.Select(Lr2FolderPath.ToFolderPath));
        Dictionary<string, LR2SongDB.folder> rowsByPath = CreateExistingNormalFolderRowMap(rows);
        stopwatch.Stop();
        var snapshot = new Lr2NormalFolderMtimeSnapshot(
            roots,
            rowsByPath,
            songDb.IsReadOnlyConnection,
            songDb.ProcessLockWaitMs,
            stopwatch.ElapsedMilliseconds);
        logInstallPerformance?.Invoke("lr2_normal_folder_mtime_snapshot_prefetch"
            + " roots=" + snapshot.RootDirectories.Count
            + " existingRows=" + snapshot.ExistingRowCount
            + " readOnly=" + snapshot.ReadOnly.ToString().ToLowerInvariant()
            + " dbLockWaitMs=" + snapshot.DbLockWaitMs
            + " elapsedMs=" + snapshot.ElapsedMs);
        return snapshot;
    }

    public SongTableFileCheckResult ApplyFileScanDiff(
        BmsLibraryDbGateway dbGateway,
        EverythingNative everythingNative,
        BmsLibraryOptionsSnapshot options,
        IEnumerable<BMSFile> currentFiles,
        ChartScanExecutionResult prefetchedScanResult,
        long prefetchedScanElapsedMs,
        Func<ChartScanExecutionResult> executeScan,
        IBmsLibraryDialogService dialogService,
        Action<string> logInstallPerformance = null,
        Action<string> logEverythingScan = null,
        IEnumerable<LR2SongDBExtended.bmson_song> currentBmsonSongs = null,
        Func<ChartScanExecutionResult> executeBmsonScan = null,
        Action scanCompleted = null,
        Action fileDiffStarted = null,
        Action<int, int, string> reportParseProgress = null,
        Action<string> logInstallPerformanceWarn = null,
        Action<IReadOnlyList<LR2SongDBExtended.chart_info>> inlineChartInfoRowsCommitted = null,
        IEnumerable<string> lr2NormalFolderSyncRootDirectories = null,
        IEnumerable<string> lr2FolderDiscoveryRootDirectories = null,
        Lr2BuiltinCustomFolderSettings lr2BuiltinCustomFolderSettings = null,
        Lr2NormalFolderMtimeSnapshot normalFolderMtimeSnapshot = null,
        Func<Lr2NormalFolderMtimeSnapshot> normalFolderMtimeSnapshotProvider = null,
        Action<SongTableFileCheckResult> lr2ScanSurfacePrepared = null,
        bool protectExistingBmsRowsFromLr2SongDbSyncMigration = false,
        IEnumerable<string> lr2FolderExcludedDirectories = null,
        Action<SongTableFileCheckResult> catalogProjectionApplied = null)
    {
        var result = new SongTableFileCheckResult();
        var stopwatchScan = Stopwatch.StartNew();
        ChartScanExecutionResult scanResult = prefetchedScanResult;
        if (scanResult != null && scanResult.Result != null)
        {
            result.PrefetchedScanUsed = true;
        }
        else
        {
            scanResult = executeScan?.Invoke();
        }
        if (scanResult?.Result == null || !scanResult.Success || !scanResult.IsComplete)
        {
            stopwatchScan.Stop();
            result.ScanFallbackUsed = scanResult?.FallbackUsed ?? false;
            result.ScanFallbackReason = GetChartScanFailureReason(scanResult);
            scanCompleted?.Invoke();
            return result;
        }
        bool bmsFileScanSucceeded = scanResult.Success;
        result.ScanFallbackUsed = scanResult.FallbackUsed;
        result.ScanFallbackReason = scanResult.FallbackReason ?? string.Empty;
        if (ShouldSkipEmptyScanWithExistingDb(scanResult, currentFiles, currentBmsonSongs, out int existingBmsCount, out int existingBmsonCount))
        {
            stopwatchScan.Stop();
            result.ScanElapsedMs = stopwatchScan.ElapsedMilliseconds + (result.PrefetchedScanUsed ? prefetchedScanElapsedMs : 0L);
            result.EmptyScanWithExistingDbSkipped = true;
            result.EmptyScanWithExistingDbSkipReason = CreateEmptyScanSkipReason(scanResult, existingBmsCount, existingBmsonCount);
            scanCompleted?.Invoke();
            logInstallPerformance?.Invoke("song_tbl_file_check skipped"
                + " reason=empty_scan_with_existing_db"
                + " existing_bms=" + existingBmsCount
                + " existing_bmson=" + existingBmsonCount
                + " fallback_used=" + result.ScanFallbackUsed.ToString().ToLowerInvariant()
                + " fallback_reason=" + (string.IsNullOrWhiteSpace(result.ScanFallbackReason) ? string.Empty : result.ScanFallbackReason)
                + " native_bridge_reason=" + (string.IsNullOrWhiteSpace(scanResult.NativeBridgeReason) ? string.Empty : scanResult.NativeBridgeReason));
            return result;
        }
        if (bmsFileScanSucceeded)
        {
            result.Lr2ScanSurfaceAvailable = true;
            ApplyLr2FolderScanSurface(
                result,
                options,
                lr2FolderDiscoveryRootDirectories,
                lr2BuiltinCustomFolderSettings,
                lr2FolderExcludedDirectories,
                logEverythingScan,
                everythingNative);
        }
        stopwatchScan.Stop();
        scanCompleted?.Invoke();
        ChartScanResult mergedScanResult = scanResult.Result;
        long bmsonScanElapsedMs = 0L;
        if (executeBmsonScan != null)
        {
            var stopwatchBmsonScan = Stopwatch.StartNew();
            ChartScanExecutionResult bmsonScanResult = executeBmsonScan();
            stopwatchBmsonScan.Stop();
            bmsonScanElapsedMs = stopwatchBmsonScan.ElapsedMilliseconds;
            if (!IsAuthoritativeChartScan(bmsonScanResult))
            {
                result.ScanFallbackUsed = result.ScanFallbackUsed || (bmsonScanResult?.FallbackUsed ?? false);
                result.ScanFallbackReason = GetChartScanFailureReason(bmsonScanResult);
                return result;
            }
            mergedScanResult = MergeScanResults(scanResult.Result, bmsonScanResult.Result);
            result.ScanFallbackUsed = result.ScanFallbackUsed || bmsonScanResult.FallbackUsed;
            if (string.IsNullOrWhiteSpace(result.ScanFallbackReason))
            {
                result.ScanFallbackReason = bmsonScanResult.FallbackReason ?? string.Empty;
            }
        }
        fileDiffStarted?.Invoke();
        result.ScanElapsedMs = stopwatchScan.ElapsedMilliseconds + bmsonScanElapsedMs + (result.PrefetchedScanUsed ? prefetchedScanElapsedMs : 0L);
        result.NativeBridgeMs = scanResult.NativeBridgeMs;
        result.NativeBridgeReason = scanResult.NativeBridgeReason ?? string.Empty;
        result.ManagedDecodeMs = scanResult.ManagedDecodeMs;
        result.ManagedMaterializeMs = scanResult.ManagedMaterializeMs;
        result.BridgeRawBufferBytes = scanResult.BridgeRawBufferBytes;
        bool canUseNativeResourceIndex = executeBmsonScan == null && scanResult.ResourceIndex != null && ReferenceEquals(mergedScanResult, scanResult.Result);
        result.NextResourceIndex = canUseNativeResourceIndex
            ? scanResult.ResourceIndex
            : LibraryResourceIndex.CreateFromScanResult(mergedScanResult);
        result.NextDirectoryResourceLookupCache = result.NextResourceIndex.DirectoryLookupCache;
        result.ResourceIndexBuildMs = result.NextResourceIndex.BuildMs;
        result.DirhashBuildMs = result.NextResourceIndex.BuildMs;
        result.ResourceLookupCacheMs = result.NextResourceIndex.ResourceLookupMs;
        ulong audioResourceKeyEntryCount = canUseNativeResourceIndex ? scanResult.AudioResourceKeyHashCount : CountHashEntries(mergedScanResult.AudioRelativePathHashesByChartDirectory);
        ulong imageResourceKeyEntryCount = canUseNativeResourceIndex ? scanResult.ImageResourceKeyHashCount : CountHashEntries(mergedScanResult.ImageRelativePathHashesByChartDirectory);
        ulong movieResourceKeyEntryCount = canUseNativeResourceIndex ? scanResult.MovieResourceKeyHashCount : CountHashEntries(mergedScanResult.MovieRelativePathHashesByChartDirectory);
        ulong chartRelativeKeyCount = audioResourceKeyEntryCount + imageResourceKeyEntryCount + movieResourceKeyEntryCount;
        logInstallPerformance?.Invoke("resource_index_build source=" + (result.NextResourceIndex.Source ?? "managed")
            + " directories=" + result.NextResourceIndex.DirectoryCount
            + " chartRelativeKeys=" + chartRelativeKeyCount
            + " reverseLookupKeys=" + (result.NextDirectoryResourceLookupCache?.CategoryReverseLookupEntryCount ?? 0)
            + " reverseLookupSource=" + (string.Equals(result.NextResourceIndex.Source, "native_canonical", StringComparison.OrdinalIgnoreCase) ? "native" : "managed")
            + " buildMs=" + result.ResourceIndexBuildMs
            + " lookupMs=" + result.ResourceLookupCacheMs
            + " nativePackMs=" + scanResult.PackMs
            + " managedMaterializeMs=" + result.ManagedMaterializeMs
            + " payloadBytes=" + result.BridgeRawBufferBytes);
        result.AudioResourceKeyHashEntryCount = audioResourceKeyEntryCount;
        result.ImageResourceKeyHashEntryCount = imageResourceKeyEntryCount;
        result.MovieResourceKeyHashEntryCount = movieResourceKeyEntryCount;

        if (bmsFileScanSucceeded)
        {
            result.Lr2ScanNormalFolderDirectoryPaths = [.. Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargetsFromNormalizedDirectories(
                lr2NormalFolderSyncRootDirectories,
                mergedScanResult.ChartDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase))];
            result.Lr2ScanDirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(
                mergedScanResult.DirectoryEntriesByPath ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            result.Lr2ScanNormalFolderDirectoryEntries = Lr2FolderDirectoryEnumerationService.CreateEntriesFromSurface(
                result.Lr2ScanDirectoryEntries,
                result.Lr2ScanNormalFolderDirectoryPaths);
            result.Lr2ScanFolderInfoFilePaths = [.. (mergedScanResult.FolderInfoFilePaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
            result.Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(
                mergedScanResult.FolderInfoFileEntriesByPath ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            result.Lr2ScanTextFileDirectories = [.. (mergedScanResult.ChartDirectoriesWithTextFiles ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
            lr2ScanSurfacePrepared?.Invoke(result);
        }
        result.DirectoryCount = result.NextDirectoryResourceLookupCache?.Count ?? 0;

        fileScanParseCommitOwner.ApplyFileDiff(
            dbGateway,
            options,
            currentFiles,
            currentBmsonSongs,
            mergedScanResult,
            bmsFileScanSucceeded,
            result,
            dialogService,
            logEverythingScan,
            reportParseProgress,
            logInstallPerformance,
            logInstallPerformanceWarn,
            inlineChartInfoRowsCommitted,
            protectExistingBmsRowsFromLr2SongDbSyncMigration,
            catalogProjectionApplied);

        normalFolderMtimeSnapshot ??= normalFolderMtimeSnapshotProvider?.Invoke();
        Lr2NormalFolderMtimeDiffResult normalFolderMtimeDiff = CreateChangedNormalFolderDirectoryPaths(
            dbGateway,
            options,
            lr2NormalFolderSyncRootDirectories,
            result.Lr2ScanNormalFolderDirectoryPaths,
            result.DeletedPaths,
            result.Lr2ScanNormalFolderDirectoryEntries,
            normalFolderMtimeSnapshot);
        if (normalFolderMtimeDiff != null)
        {
            logInstallPerformance?.Invoke("lr2_normal_folder_mtime_diff"
                + " roots=" + normalFolderMtimeDiff.RootCount
                + " directories=" + normalFolderMtimeDiff.DirectoryCount
                + " existingRows=" + normalFolderMtimeDiff.ExistingRowCount
                + " prefetched=" + normalFolderMtimeDiff.PrefetchedSnapshotUsed.ToString().ToLowerInvariant()
                + " changed=" + normalFolderMtimeDiff.ChangedDirectoryPaths.Count
                + " pruneScopes=" + normalFolderMtimeDiff.PruneScopeDirectoryPaths.Count
                + " missingRows=" + normalFolderMtimeDiff.MissingRowCount
                + " missingMetadata=" + normalFolderMtimeDiff.MissingMetadataCount
                + " candidateBuildMs=" + normalFolderMtimeDiff.CandidateBuildMs
                + " existingMapMs=" + normalFolderMtimeDiff.ExistingMapMs
                + " compareMs=" + normalFolderMtimeDiff.CompareMs
                + " elapsedMs=" + normalFolderMtimeDiff.ElapsedMs);
        }
        IReadOnlyList<string> scannedPaths = [.. (mergedScanResult.ChartFilePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path)
                && !FileScanParseCommitOwner.IsBmsonChartPath(path))];
        SyncLr2NormalFoldersIfEnabled(
            dbGateway,
            options,
            lr2NormalFolderSyncRootDirectories,
            Lr2NormalFolderSyncScopeBuilder.CreateForFileDiff(
                lr2NormalFolderSyncRootDirectories,
                scannedPaths,
                normalFolderMtimeDiff?.ChangedDirectoryPaths,
                normalFolderMtimeDiff?.PruneScopeDirectoryPaths),
            result.Lr2ScanNormalFolderDirectoryEntries,
            mergedScanResult.FolderInfoFilePaths,
            mergedScanResult.FolderInfoFileEntriesByPath,
            result,
            logInstallPerformance,
            logInstallPerformanceWarn,
            bmsFileScanSucceeded);

        logInstallPerformance?.Invoke(
            "song_tbl_file_check_breakdown scan_ms=" + result.ScanElapsedMs
            + " native_bridge_ms=" + result.NativeBridgeMs
            + " native_bridge_reason=" + (string.IsNullOrWhiteSpace(result.NativeBridgeReason) ? string.Empty : result.NativeBridgeReason)
            + " fallback_used=" + result.ScanFallbackUsed.ToString().ToLowerInvariant()
            + " fallback_reason=" + (string.IsNullOrWhiteSpace(result.ScanFallbackReason) ? string.Empty : result.ScanFallbackReason)
            + " managed_decode_ms=" + result.ManagedDecodeMs
            + " managed_materialize_ms=" + result.ManagedMaterializeMs
            + " bridge_raw_buffer_bytes=" + result.BridgeRawBufferBytes
            + " resource_index_build_ms=" + result.ResourceIndexBuildMs
            + " resource_index_lookup_ms=" + result.ResourceLookupCacheMs
            + " lazy_hash_cache_entries=" + (result.NextDirectoryResourceLookupCache?.LazyHashCacheEntryCount ?? 0)
            + " lazy_hash_build_ms=" + (result.NextDirectoryResourceLookupCache?.LazyHashBuildMs ?? 0L)
            + " lazy_hash_lookup_count=" + (result.NextDirectoryResourceLookupCache?.LazyHashLookupCount ?? 0L)
            + " diff_ms=" + result.DiffMs
            + " diff_current_index_ms=" + result.DiffCurrentIndexMs
            + " diff_scanned_split_ms=" + result.DiffScannedSplitMs
            + " diff_deleted_ms=" + result.DiffDeletedMs
            + " diff_bms_target_ms=" + result.DiffBmsTargetMs
            + " diff_bmson_target_ms=" + result.DiffBmsonTargetMs
            + " deleted_count=" + result.DeletedPaths.Count
            + " added_count=" + result.AddedFiles.Count
            + " bms_date_only_update_count=" + result.BmsDateOnlyUpdateCount
            + " bms_text_only_update_count=" + result.BmsTextOnlyUpdateCount
            + " bms_moved_hash_relink_count=" + result.BmsMovedHashRelinkCount
            + " bms_moved_hash_relink_ambiguous_count=" + result.BmsMovedHashRelinkAmbiguousCount
            + " bms_added_target_count=" + result.BmsAddedTargetCount
            + " bms_new_insert_path_count=" + result.NewlyInsertedBmsPaths.Count
            + " bms_legacy_existing_protected_count=" + result.BmsLegacyExistingProtectedCount
            + " bmson_deleted_count=" + result.DeletedBmsonPaths.Count
            + " bmson_upsert_count=" + result.AddedBmsonSongs.Count
            + " bmson_upsert_target_count=" + result.BmsonUpsertTargetCount
            + " bms_mtime_fallback_count=" + result.BmsMtimeFallbackCount
            + " bmson_mtime_fallback_count=" + result.BmsonMtimeFallbackCount
            + " file_diff_reader_degree=" + result.FileDiffReaderDegree
            + " file_diff_parser_degree=" + result.FileDiffParserDegree
            + " file_diff_post_parse_worker_degree=" + result.FileDiffPostParseWorkerDegree
            + " read_queue_capacity=" + result.ReadQueueCapacity
            + " parsed_queue_capacity=" + result.ParsedQueueCapacity
            + " post_parse_queue_capacity=" + result.PostParseQueueCapacity
            + " post_parse_result_queue_capacity=" + result.PostParseResultQueueCapacity
            + " commit_queue_capacity=" + result.CommitQueueCapacity
            + " commit_writer_queue_capacity=" + result.CommitWriterQueueCapacity
            + " commit_writer_queue_high_watermark=" + result.CommitWriterQueueHighWatermark
            + " commit_streaming_enabled=" + result.CommitStreamingEnabled.ToString().ToLowerInvariant()
            + " commit_streaming_barrier=" + (result.CommitStreamingBarrierReason ?? string.Empty)
            + " reader_output_wait_ms=" + result.ReaderOutputWaitMs
            + " parser_output_wait_ms=" + result.ParserOutputWaitMs
            + " post_parse_queue_wait_ms=" + result.PostParseQueueWaitMs
            + " post_parse_output_wait_ms=" + result.PostParseOutputWaitMs
            + " commit_queue_wait_ms=" + result.CommitQueueWaitMs
            + " commit_writer_queue_wait_ms=" + result.CommitWriterQueueWaitMs
            + " db_commit_first_chunk_start_ms=" + result.DbCommitFirstChunkStartMs
            + " post_parse_work_item_count=" + result.PostParseWorkItemCount
            + " post_parse_wall_ms=" + result.PostParseWallMs
            + " post_parse_max_item_ms=" + result.PostParseMaxItemMs
            + " newfile_parse_ms=" + result.NewFileParseMs
            + " bms_parse_ms=" + result.BmsParseMs
            + " bmson_parse_ms=" + result.BmsonParseMs
            + " file_diff_read_ms=" + result.FileDiffReadMs
            + " file_diff_digest_ms=" + result.FileDiffDigestMs
            + " file_diff_parse_ms=" + result.FileDiffParseMs
            + " snapshot_queue_high_watermark=" + result.SnapshotQueueHighWatermark
            + " inline_chart_info_target_count=" + result.InlineChartInfoTargetCount
            + " inline_chart_info_success_count=" + result.InlineChartInfoSuccessCount
            + " inline_chart_info_current_skipped_count=" + result.InlineChartInfoCurrentSkippedCount
            + " inline_chart_info_failure_skipped_count=" + result.InlineChartInfoFailureSkippedCount
            + " inline_chart_info_parse_failed_count=" + result.InlineChartInfoParseFailedCount
            + " inline_chart_info_failure_persisted_count=" + result.InlineChartInfoFailurePersistedCount
            + " inline_chart_info_failure_cleared_count=" + result.InlineChartInfoFailureClearedCount
            + " inline_chart_info_index_published_count=" + result.InlineChartInfoIndexPublishedCount
            + " inline_chart_info_parse_ms=" + result.InlineChartInfoParseMs
            + " inline_chart_info_wall_ms=" + result.InlineChartInfoWallMs
            + " inline_chart_info_batch_size=" + result.InlineChartInfoBatchSize
            + " inline_maintenance_degree=" + result.InlineMaintenanceDegree
            + " inline_maintenance_target_count=" + result.InlineMaintenanceTargetCount
            + " inline_maintenance_success_count=" + result.InlineMaintenanceSuccessCount
            + " inline_maintenance_failed_count=" + result.InlineMaintenanceFailedCount
            + " inline_maintenance_bms_count=" + result.InlineMaintenanceBmsCount
            + " inline_maintenance_bmson_count=" + result.InlineMaintenanceBmsonCount
            + " inline_maintenance_ms=" + result.InlineMaintenanceMs
            + " inline_maintenance_wall_ms=" + result.InlineMaintenanceWallMs
            + " inline_health_wall_ms=" + result.InlineHealthWallMs
            + " inline_encoding_wall_ms=" + result.InlineEncodingWallMs
            + " inline_encoding_reload_wall_ms=" + result.InlineEncodingReloadWallMs
            + " inline_encoding_reload_count=" + result.InlineEncodingReloadCount
            + " inline_encoding_detect_count=" + result.InlineEncodingDetectCount
            + " inline_encoding_fast_ascii_count=" + result.InlineEncodingFastAsciiCount
            + " inline_encoding_shift_jis_count=" + result.InlineEncodingShiftJisCount
            + " inline_encoding_shift_jis_question_count=" + result.InlineEncodingShiftJisQuestionCount
            + " inline_encoding_ks_c_5601_count=" + result.InlineEncodingKoreanCount
            + " inline_encoding_ks_c_5601_question_count=" + result.InlineEncodingKoreanQuestionCount
            + " inline_encoding_utf8_count=" + result.InlineEncodingUtf8Count
            + " inline_encoding_unknown_count=" + result.InlineEncodingUnknownCount
            + " inline_encoding_other_count=" + result.InlineEncodingOtherCount
            + " inline_encoding_max_item_ms=" + result.InlineEncodingMaxItemMs
            + " inline_bms_maintenance_wall_ms=" + result.InlineBmsMaintenanceWallMs
            + " inline_bmson_maintenance_wall_ms=" + result.InlineBmsonMaintenanceWallMs
            + " inline_maintenance_cache_hit=" + result.InlineMaintenanceCacheHitCount
            + " inline_maintenance_resource_index_hit=" + result.InlineMaintenanceResourceIndexHitCount
            + " inline_maintenance_resource_set_cache_hit=" + result.InlineMaintenanceResourceSetCacheHitCount
            + " inline_maintenance_file_exists_fallback=" + result.InlineMaintenanceFileExistsFallbackCount
            + " inline_maintenance_shared_resource_cache_entries=" + result.InlineMaintenanceSharedResourceCacheEntries
            + " inline_maintenance_resource_set_cache_entries=" + result.InlineMaintenanceResourceSetCacheEntries
            + " parse_read_bytes_estimate=" + result.ParseReadBytesEstimate
            + " apply_ms=" + result.ApplyMs
            + " db_commit_ms=" + result.DbCommitMs
            + " db_commit_apply_ms=" + result.DbCommitApplyMs
            + " db_commit_schema_ms=" + result.DbCommitSchemaMs
            + " db_commit_bms_delete_ms=" + result.DbCommitBmsDeleteMs
            + " db_commit_bms_date_update_ms=" + result.DbCommitBmsDateUpdateMs
            + " db_commit_bms_upsert_ms=" + result.DbCommitBmsUpsertMs
            + " db_commit_bms_changed=" + result.DbCommitBmsChangedCount
            + " db_commit_bmson_delete_ms=" + result.DbCommitBmsonDeleteMs
            + " db_commit_bmson_upsert_ms=" + result.DbCommitBmsonUpsertMs
            + " db_commit_maintenance_upsert_ms=" + result.DbCommitMaintenanceUpsertMs
            + " db_commit_chart_info_ms=" + result.DbCommitChartInfoMs
            + " db_commit_sqlite_commit_ms=" + result.DbCommitSqliteCommitMs
            + " db_commit_chunks=" + result.DbCommitChunks
            + " db_commit_chunk_size=" + result.DbCommitChunkSize
            + " db_commit_max_chunk_ms=" + result.DbCommitMaxChunkMs
            + " instl_dst_cleanup_ms=" + result.InstlDstCleanupMs
            + " lr2_normal_folder_sync_executed=" + result.Lr2NormalFolderSyncExecuted.ToString().ToLowerInvariant()
            + " lr2_normal_folder_sync_failed=" + result.Lr2NormalFolderSyncFailed.ToString().ToLowerInvariant()
            + " lr2_normal_folder_generated=" + result.Lr2NormalFolderGeneratedCount
            + " lr2_normal_folder_upserted=" + result.Lr2NormalFolderUpsertedCount
            + " lr2_normal_folder_deleted=" + result.Lr2NormalFolderDeletedCount
            + " lr2_normal_folder_skipped_unsupported=" + result.Lr2NormalFolderSkippedUnsupportedPathCount
            + " lr2_normal_folder_skipped_missing_metadata=" + result.Lr2NormalFolderSkippedMissingMetadataCount
            + " lr2_normal_folder_skipped_incompatible_chart=" + result.Lr2NormalFolderSkippedIncompatibleChartPathCount
            + " lr2_normal_folder_metadata_requested=" + result.Lr2NormalFolderMetadataRequestedDirectoryCount
            + " lr2_normal_folder_metadata_resolved=" + result.Lr2NormalFolderMetadataResolvedDirectoryCount
            + " lr2_normal_folderinfo_candidates=" + result.Lr2NormalFolderInfoCandidateCount
            + " lr2_normal_folderinfo_applied=" + result.Lr2NormalFolderInfoAppliedCount
            + " lr2_normal_folderinfo_read_failures=" + result.Lr2NormalFolderInfoReadFailureCount
            + " lr2_normal_folder_sync_ms=" + result.Lr2NormalFolderSyncMs);
        logInstallPerformance?.Invoke(
            "song_tbl_file_check_cache_counts chartDirs=" + result.DirectoryCount
            + " audioResourceKeyEntries=" + result.AudioResourceKeyHashEntryCount
            + " imageResourceKeyEntries=" + result.ImageResourceKeyHashEntryCount
            + " movieResourceKeyEntries=" + result.MovieResourceKeyHashEntryCount);
        logEverythingScan?.Invoke("bms_scan totalMs=" + result.ScanElapsedMs
            + " nativeBridgeMs=" + result.NativeBridgeMs
            + " managedDecodeMs=" + result.ManagedDecodeMs
            + " managedMaterializeMs=" + result.ManagedMaterializeMs
            + " resourceIndexBuildMs=" + result.ResourceIndexBuildMs
            + " resourceIndexLookupMs=" + result.ResourceLookupCacheMs
            + " bridgeReason=" + (string.IsNullOrWhiteSpace(result.NativeBridgeReason) ? string.Empty : result.NativeBridgeReason)
            + " fallback=" + result.ScanFallbackUsed.ToString().ToLowerInvariant()
            + " fallbackReason=" + (string.IsNullOrWhiteSpace(result.ScanFallbackReason) ? string.Empty : result.ScanFallbackReason)
            + " bmsPaths=" + result.BmsPathCount
            + " dirs=" + result.DirectoryCount
            + " prefetched=" + result.PrefetchedScanUsed.ToString().ToLowerInvariant());
        return result;
    }

    private static void ApplyLr2FolderScanSurface(
        SongTableFileCheckResult result,
        BmsLibraryOptionsSnapshot options,
        IEnumerable<string> rootDirectories,
        Lr2BuiltinCustomFolderSettings builtinCustomFolderSettings,
        IEnumerable<string> excludedDirectories,
        Action<string> logEverythingScan,
        EverythingNative everythingNative)
    {
        if (result == null
            || options?.OperationModeLR2DB != true)
        {
            return;
        }

        List<string> lr2FolderDiscoveryDirectories = Lr2FolderFileDiscoveryService.CreateDiscoveryDirectories(
            rootDirectories,
            new[] { options.LR2CustomFolderOutputBaseDir }.Concat(options.LR2CustomFolderAdditionalOutputBaseDirs ?? []),
            options.LR2CustomFolderOutputBaseDirRootType,
            Lr2FolderFileDiscoveryService.CreateBuiltinFolderSourceDirectories(options.LR2RootPath));
        Lr2FolderFileCandidateSnapshot candidates = Lr2FolderFileDiscoveryService.CreateFileCandidates(
            lr2FolderDiscoveryDirectories,
            options.LR2RootPath,
            builtinCustomFolderSettings ?? new Lr2BuiltinCustomFolderSettings(0, 24, false),
            logEverythingScan,
            everythingNative,
            excludedDirectories);

        result.Lr2ScanLr2FolderDiscoveryDirectories = lr2FolderDiscoveryDirectories;
        result.Lr2ScanLr2FolderFilePaths = candidates.Paths;
        result.Lr2ScanLr2FolderFileEntries = candidates.EntriesByPath;
        result.Lr2ScanLr2FolderFileDiscoveryComplete = candidates.DiscoveryComplete;
        IReadOnlyList<string> excludedDirectoryList = [.. (excludedDirectories ?? [])
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)];
        int excludedDirectoryCount = excludedDirectoryList.Count;
        result.Lr2ScanLr2FolderCandidatesAlreadyFiltered = excludedDirectoryCount > 0;
        result.Lr2ScanLr2FolderAppManagedScopeDirectoryCount = excludedDirectoryCount;
        result.Lr2ScanLr2FolderAppManagedScopeDirectories = excludedDirectoryList;
    }

    private static void SyncLr2NormalFoldersIfEnabled(
        BmsLibraryDbGateway dbGateway,
        BmsLibraryOptionsSnapshot options,
        IEnumerable<string> rootDirectories,
        Lr2NormalFolderSyncScope syncInput,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntrySurface,
        IEnumerable<string> folderInfoFilePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> folderInfoFileEntries,
        SongTableFileCheckResult result,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn,
        bool scanCompletedSuccessfully)
    {
        if (dbGateway == null
            || result == null
            || options?.OperationModeLR2DB != true)
        {
            return;
        }

        List<string> roots = [.. (rootDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (roots.Count == 0)
        {
            return;
        }
        if (!scanCompletedSuccessfully)
        {
            logInstallPerformance?.Invoke("lr2_normal_folder_sync skipped reason=incomplete_scan");
            return;
        }
        if (syncInput == null
            || (!result.HasDbDiff && syncInput.DirectoryPaths.Count == 0))
        {
            logInstallPerformance?.Invoke("lr2_normal_folder_sync skipped reason=no_db_diff");
            return;
        }
        if (syncInput.ChartPaths.Count == 0
                && syncInput.DirectoryPaths.Count == 0
                && syncInput.PruneScopeDirectories.Count == 0
                && syncInput.PruneExactDirectories.Count == 0)
        {
            logInstallPerformance?.Invoke("lr2_normal_folder_sync skipped reason=no_normal_folder_mtime_diff");
            return;
        }

        result.Lr2NormalFolderSyncExecuted = true;
        try
        {
            using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
            IReadOnlyCollection<string> directoryMetadataTargets = [.. Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargets(roots, syncInput.ChartPaths)
                .Concat(Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargetsFromDirectories(roots, syncInput.DirectoryPaths))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
            IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries = Lr2FolderDirectoryEnumerationService.CreateEntriesFromSurface(
                directoryEntrySurface,
                directoryMetadataTargets);
            Lr2NormalFolderDbSyncResult syncResult = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = roots,
                ChartPaths = syncInput.ChartPaths,
                DirectoryPaths = directoryMetadataTargets,
                FolderInfoFilePaths = [.. (folderInfoFilePaths ?? [])],
                FolderInfoFileEntries = folderInfoFileEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                DirectoryLastWriteTimeUtcResolver = CreateLastWriteTimeResolver(directoryEntries),
                PruneScopeDirectories = syncInput.PruneScopeDirectories,
                PruneExactDirectories = syncInput.PruneExactDirectories,
                AllowPrune = syncInput.PruneScopeDirectories.Count > 0 || syncInput.PruneExactDirectories.Count > 0,
                UseScopedExistingRows = true
            });
            ApplyLr2NormalFolderSyncResult(result, syncResult);
            logInstallPerformance?.Invoke("lr2_normal_folder_sync done generated=" + syncResult.GeneratedCount
                + " upserted=" + syncResult.UpsertedCount
                + " deleted=" + syncResult.DeletedCount
                + " paths=" + syncInput.ChartPaths.Count
                + " directoryPaths=" + syncInput.DirectoryPaths.Count
                + " pruneScopes=" + syncInput.PruneScopeDirectories.Count
                + " exactPrunes=" + syncInput.PruneExactDirectories.Count
                + " skippedUnsupported=" + syncResult.SkippedUnsupportedPathCount
                + " skippedMissingMetadata=" + syncResult.SkippedMissingMetadataCount
                + " skippedIncompatibleChart=" + syncResult.SkippedIncompatibleChartPathCount
                + " metadataRequested=" + syncResult.MetadataRequestedDirectoryCount
                + " metadataResolved=" + syncResult.MetadataResolvedDirectoryCount
                + " folderInfoCandidates=" + syncResult.FolderInfoCandidateCount
                + " folderInfoApplied=" + syncResult.FolderInfoAppliedCount
                + " folderInfoReadFailures=" + syncResult.FolderInfoReadFailureCount
                + " targetBuildMs=" + syncResult.TargetBuildMs
                + " metadataBuildMs=" + syncResult.MetadataBuildMs
                + " existingReadMs=" + syncResult.ExistingReadMs
                + " rowGenerateMs=" + syncResult.RowGenerateMs
                + " planMs=" + syncResult.PlanMs
                + " writeMs=" + syncResult.WriteMs
                + " elapsedMs=" + syncResult.ElapsedMs);
        }
        catch (Exception ex)
        {
            result.Lr2NormalFolderSyncFailed = true;
            result.Lr2NormalFolderSyncFailureReason = ex.Message ?? ex.GetType().Name;
            logInstallPerformanceWarn?.Invoke("lr2_normal_folder_sync failed reason=" + QuoteLogValue(result.Lr2NormalFolderSyncFailureReason));
        }
    }

    private static Lr2NormalFolderMtimeDiffResult CreateChangedNormalFolderDirectoryPaths(
        BmsLibraryDbGateway dbGateway,
        BmsLibraryOptionsSnapshot options,
        IEnumerable<string> rootDirectories,
        IEnumerable<string> currentNormalFolderDirectoryPaths,
        IEnumerable<string> deletedBmsPaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> currentDirectoryEntries,
        Lr2NormalFolderMtimeSnapshot prefetchedSnapshot = null)
    {
        if (dbGateway == null
            || options?.OperationModeLR2DB != true)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        List<string> roots = NormalizeNormalFolderMtimeRoots(rootDirectories);
        var stopwatchCandidates = Stopwatch.StartNew();
        List<Lr2NormalFolderMtimeCandidate> directoryCandidates = CreateNormalFolderMtimeCandidates(
            currentNormalFolderDirectoryPaths,
            roots,
            currentDirectoryEntries,
            pathsAlreadyNormalized: true);
        List<Lr2NormalFolderMtimeCandidate> deletedParentCandidates = CreateNormalFolderMtimeCandidates(
            (deletedBmsPaths ?? []).Select(path => Lr2FolderPath.SafeGetDirectoryName(path)),
            roots,
            currentDirectoryEntries,
            pathsAlreadyNormalized: false);
        stopwatchCandidates.Stop();
        if (roots.Count == 0 || (directoryCandidates.Count == 0 && deletedParentCandidates.Count == 0))
        {
            stopwatch.Stop();
            return new Lr2NormalFolderMtimeDiffResult(
                roots.Count,
                directoryCandidates.Count,
                existingRowCount: 0,
                missingRowCount: 0,
                missingMetadataCount: 0,
                prefetchedSnapshotUsed: false,
                candidateBuildMs: stopwatchCandidates.ElapsedMilliseconds,
                existingMapMs: 0L,
                compareMs: 0L,
                changedDirectoryPaths: [],
                pruneScopeDirectoryPaths: [],
                stopwatch.ElapsedMilliseconds);
        }

        IReadOnlyCollection<string> exactPaths = [.. directoryCandidates
            .Concat(deletedParentCandidates)
            .Select(candidate => candidate.FolderPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)];
        bool prefetchedSnapshotUsed = CanUseNormalFolderMtimeSnapshot(prefetchedSnapshot, roots);
        var stopwatchExistingMap = Stopwatch.StartNew();
        Dictionary<string, LR2SongDB.folder> existingRowsByPath = prefetchedSnapshotUsed
            ? CreateExistingNormalFolderRowMap(prefetchedSnapshot, exactPaths)
            : CreateExistingNormalFolderRowMapFromDb(dbGateway, exactPaths);
        stopwatchExistingMap.Stop();

        var changed = new List<string>();
        var pruneScopes = new List<string>();
        int missingRowCount = 0;
        int missingMetadataCount = 0;
        var stopwatchCompare = Stopwatch.StartNew();
        foreach (Lr2NormalFolderMtimeCandidate candidate in directoryCandidates)
        {
            Lr2NormalFolderDirectoryChangeState changeState = ResolveNormalFolderDirectoryChangeState(
                existingRowsByPath,
                candidate);
            if (changeState == Lr2NormalFolderDirectoryChangeState.MissingRow)
            {
                missingRowCount++;
                changed.Add(candidate.DirectoryPath);
                continue;
            }
            if (changeState == Lr2NormalFolderDirectoryChangeState.MissingMetadata)
            {
                missingMetadataCount++;
                changed.Add(candidate.DirectoryPath);
                continue;
            }
            if (changeState == Lr2NormalFolderDirectoryChangeState.Changed)
            {
                changed.Add(candidate.DirectoryPath);
            }
        }
        foreach (Lr2NormalFolderMtimeCandidate candidate in deletedParentCandidates)
        {
            if (roots.Any(root => string.Equals(candidate.DirectoryPath, root, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            Lr2NormalFolderDirectoryChangeState changeState = ResolveNormalFolderDirectoryChangeState(
                existingRowsByPath,
                candidate);
            if (changeState == Lr2NormalFolderDirectoryChangeState.MissingRow
                || changeState == Lr2NormalFolderDirectoryChangeState.MissingMetadata
                || changeState == Lr2NormalFolderDirectoryChangeState.Changed)
            {
                pruneScopes.Add(candidate.DirectoryPath);
            }
            if (changeState == Lr2NormalFolderDirectoryChangeState.MissingRow)
            {
                missingRowCount++;
            }
            else if (changeState == Lr2NormalFolderDirectoryChangeState.MissingMetadata)
            {
                missingMetadataCount++;
            }
        }
        stopwatchCompare.Stop();

        stopwatch.Stop();
        return new Lr2NormalFolderMtimeDiffResult(
            roots.Count,
            directoryCandidates.Count,
            existingRowsByPath.Count,
            missingRowCount,
            missingMetadataCount,
            prefetchedSnapshotUsed,
            stopwatchCandidates.ElapsedMilliseconds,
            stopwatchExistingMap.ElapsedMilliseconds,
            stopwatchCompare.ElapsedMilliseconds,
            [.. changed.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            [.. pruneScopes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            stopwatch.ElapsedMilliseconds);
    }

    private static List<Lr2NormalFolderMtimeCandidate> CreateNormalFolderMtimeCandidates(
        IEnumerable<string> directoryPaths,
        IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> currentDirectoryEntries,
        bool pathsAlreadyNormalized)
    {
        var result = new List<Lr2NormalFolderMtimeCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directoryPath in directoryPaths ?? [])
        {
            string directory = pathsAlreadyNormalized
                ? directoryPath
                : Lr2FolderPath.NormalizeDirectoryPath(directoryPath);
            if (string.IsNullOrWhiteSpace(directory)
                || (roots != null && roots.Count > 0 && !IsSameOrDescendantOfAnyNormalizedRoot(directory, roots))
                || !seen.Add(directory))
            {
                continue;
            }

            string folderPath = Lr2FolderPath.ToFolderPathFromNormalizedDirectory(directory);
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                continue;
            }

            int? currentDate = currentDirectoryEntries != null
                && currentDirectoryEntries.TryGetValue(directory, out RootFileEnumerationEntry entry)
                && entry.LastWriteTimeUtc.HasValue
                    ? Lr2SongRowEnricher.ToLr2UnixSeconds(entry.LastWriteTimeUtc.Value)
                    : null;
            result.Add(new Lr2NormalFolderMtimeCandidate(directory, folderPath, currentDate));
        }
        return result;
    }

    private static bool IsSameOrDescendantOfAnyNormalizedRoot(string directory, IReadOnlyList<string> roots)
    {
        foreach (string root in roots ?? [])
        {
            if (Lr2FolderPath.IsSameOrDescendantNormalized(directory, root))
            {
                return true;
            }
        }
        return false;
    }

    private static List<string> NormalizeNormalFolderMtimeRoots(IEnumerable<string> rootDirectories)
    {
        return [.. (rootDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static Dictionary<string, LR2SongDB.folder> CreateExistingNormalFolderRowMapFromDb(
        BmsLibraryDbGateway dbGateway,
        IReadOnlyCollection<string> exactPaths)
    {
        using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
        return CreateExistingNormalFolderRowMap(
            Lr2FolderExistingRowLookup.QueryExactPathsForNormalFolderMtime(songDb, exactPaths));
    }

    private static bool CanUseNormalFolderMtimeSnapshot(
        Lr2NormalFolderMtimeSnapshot snapshot,
        IReadOnlyCollection<string> roots)
    {
        if (snapshot?.ExistingRowsByPath == null || roots == null || roots.Count == 0)
        {
            return false;
        }

        return roots.All(root => snapshot.RootDirectories.Any(snapshotRoot =>
            string.Equals(root, snapshotRoot, StringComparison.OrdinalIgnoreCase)));
    }

    private static Dictionary<string, LR2SongDB.folder> CreateExistingNormalFolderRowMap(
        Lr2NormalFolderMtimeSnapshot snapshot,
        IEnumerable<string> exactPaths)
    {
        var result = new Dictionary<string, LR2SongDB.folder>(StringComparer.OrdinalIgnoreCase);
        if (snapshot?.ExistingRowsByPath == null)
        {
            return result;
        }

        foreach (string exactPath in exactPaths ?? [])
        {
            string key = !string.IsNullOrWhiteSpace(exactPath)
                && Lr2FolderPath.IsDirectorySeparator(exactPath[exactPath.Length - 1])
                    ? exactPath
                    : Lr2FolderPath.ToFolderPath(exactPath);
            if (!string.IsNullOrWhiteSpace(key)
                && snapshot.ExistingRowsByPath.TryGetValue(key, out LR2SongDB.folder row)
                && row != null
                && !result.ContainsKey(key))
            {
                result[key] = row;
            }
        }
        return result;
    }

    private static Lr2NormalFolderDirectoryChangeState ResolveNormalFolderDirectoryChangeState(
        IReadOnlyDictionary<string, LR2SongDB.folder> existingRowsByPath,
        Lr2NormalFolderMtimeCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.FolderPath))
        {
            return Lr2NormalFolderDirectoryChangeState.Unchanged;
        }
        if (existingRowsByPath == null
            || !existingRowsByPath.TryGetValue(candidate.FolderPath, out LR2SongDB.folder existing))
        {
            return Lr2NormalFolderDirectoryChangeState.MissingRow;
        }
        if (!candidate.CurrentDate.HasValue)
        {
            return Lr2NormalFolderDirectoryChangeState.MissingMetadata;
        }
        return existing.date == candidate.CurrentDate.Value
            ? Lr2NormalFolderDirectoryChangeState.Unchanged
            : Lr2NormalFolderDirectoryChangeState.Changed;
    }

    private static Dictionary<string, LR2SongDB.folder> CreateExistingNormalFolderRowMap(IEnumerable<LR2SongDB.folder> rows)
    {
        var result = new Dictionary<string, LR2SongDB.folder>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDB.folder row in rows ?? [])
        {
            if (row?.type != 1)
            {
                continue;
            }
            string path = Lr2FolderPath.ToFolderPath(row.path);
            if (!string.IsNullOrWhiteSpace(path) && !result.ContainsKey(path))
            {
                result[path] = row;
            }
        }
        return result;
    }

    private readonly struct Lr2NormalFolderMtimeCandidate(
        string directoryPath,
        string folderPath,
        int? currentDate)
    {
        public string DirectoryPath { get; } = directoryPath;

        public string FolderPath { get; } = folderPath;

        public int? CurrentDate { get; } = currentDate;
    }

    private sealed class Lr2NormalFolderMtimeDiffResult(
        int rootCount,
        int directoryCount,
        int existingRowCount,
        int missingRowCount,
        int missingMetadataCount,
        bool prefetchedSnapshotUsed,
        long candidateBuildMs,
        long existingMapMs,
        long compareMs,
        IReadOnlyList<string> changedDirectoryPaths,
        IReadOnlyList<string> pruneScopeDirectoryPaths,
        long elapsedMs)
    {
        public int RootCount { get; } = rootCount;

        public int DirectoryCount { get; } = directoryCount;

        public int ExistingRowCount { get; } = existingRowCount;

        public int MissingRowCount { get; } = missingRowCount;

        public int MissingMetadataCount { get; } = missingMetadataCount;

        public bool PrefetchedSnapshotUsed { get; } = prefetchedSnapshotUsed;

        public long CandidateBuildMs { get; } = candidateBuildMs;

        public long ExistingMapMs { get; } = existingMapMs;

        public long CompareMs { get; } = compareMs;

        public IReadOnlyList<string> ChangedDirectoryPaths { get; } = changedDirectoryPaths ?? [];

        public IReadOnlyList<string> PruneScopeDirectoryPaths { get; } = pruneScopeDirectoryPaths ?? [];

        public long ElapsedMs { get; } = elapsedMs;
    }

    private enum Lr2NormalFolderDirectoryChangeState
    {
        Unchanged,
        Changed,
        MissingRow,
        MissingMetadata
    }

    private static Func<string, DateTime?> CreateLastWriteTimeResolver(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath)
    {
        if (entriesByPath == null || entriesByPath.Count == 0)
        {
            return null;
        }

        return path =>
        {
            string key = Lr2FolderPath.NormalizeDirectoryPath(path);
            return !string.IsNullOrWhiteSpace(key)
                && entriesByPath.TryGetValue(key, out RootFileEnumerationEntry entry)
                    ? entry.LastWriteTimeUtc
                    : null;
        };
    }

    private static void ApplyLr2NormalFolderSyncResult(SongTableFileCheckResult result, Lr2NormalFolderDbSyncResult syncResult)
    {
        result.Lr2NormalFolderGeneratedCount = syncResult.GeneratedCount;
        result.Lr2NormalFolderUpsertedCount = syncResult.UpsertedCount;
        result.Lr2NormalFolderDeletedCount = syncResult.DeletedCount;
        result.Lr2NormalFolderSkippedUnsupportedPathCount = syncResult.SkippedUnsupportedPathCount;
        result.Lr2NormalFolderSkippedMissingMetadataCount = syncResult.SkippedMissingMetadataCount;
        result.Lr2NormalFolderSkippedIncompatibleChartPathCount = syncResult.SkippedIncompatibleChartPathCount;
        result.Lr2NormalFolderMetadataRequestedDirectoryCount = syncResult.MetadataRequestedDirectoryCount;
        result.Lr2NormalFolderMetadataResolvedDirectoryCount = syncResult.MetadataResolvedDirectoryCount;
        result.Lr2NormalFolderInfoCandidateCount = syncResult.FolderInfoCandidateCount;
        result.Lr2NormalFolderInfoAppliedCount = syncResult.FolderInfoAppliedCount;
        result.Lr2NormalFolderInfoReadFailureCount = syncResult.FolderInfoReadFailureCount;
        result.Lr2NormalFolderSyncMs = syncResult.ElapsedMs;
    }


    private static bool ShouldSkipEmptyScanWithExistingDb(
        ChartScanExecutionResult scanResult,
        IEnumerable<BMSFile> currentFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> currentBmsonSongs,
        out int existingBmsCount,
        out int existingBmsonCount)
    {
        existingBmsCount = 0;
        existingBmsonCount = 0;
        if (scanResult?.Success != true
            || !scanResult.IsComplete
            || scanResult.Result == null
            || !IsEverythingOrFallbackScanSource(scanResult.ScanSource)
            || (scanResult.Result.ChartFilePaths?.Count ?? 0) > 0)
        {
            return false;
        }

        existingBmsCount = CountExistingBmsPaths(currentFiles);
        existingBmsonCount = CountExistingBmsonPaths(currentBmsonSongs);
        return existingBmsCount > 0 || existingBmsonCount > 0;
    }

    private static int CountExistingBmsPaths(IEnumerable<BMSFile> currentFiles)
    {
        int count = 0;
        foreach (BMSFile file in currentFiles ?? [])
        {
            if (file != null && !string.IsNullOrWhiteSpace(file.path))
            {
                count++;
            }
        }
        return count;
    }

    private static int CountExistingBmsonPaths(IEnumerable<LR2SongDBExtended.bmson_song> currentBmsonSongs)
    {
        int count = 0;
        foreach (LR2SongDBExtended.bmson_song song in currentBmsonSongs ?? [])
        {
            if (song != null && !string.IsNullOrWhiteSpace(song.path))
            {
                count++;
            }
        }
        return count;
    }

    private static bool IsEverythingOrFallbackScanSource(ChartScanSource? scanSource)
    {
        return scanSource == ChartScanSource.Everything
            || scanSource == ChartScanSource.Fallback;
    }

    private static string CreateEmptyScanSkipReason(
        ChartScanExecutionResult scanResult,
        int existingBmsCount,
        int existingBmsonCount)
    {
        string scanSource = scanResult?.ScanSource?.ToString().ToLowerInvariant() ?? "unknown";
        string nativeBridgeReason = string.IsNullOrWhiteSpace(scanResult?.NativeBridgeReason)
            ? string.Empty
            : scanResult.NativeBridgeReason;
        string reason = "empty_scan_with_existing_db"
            + ":existingBms=" + existingBmsCount
            + ":existingBmson=" + existingBmsonCount
            + ":scanSource=" + scanSource
            + (scanResult?.ScanSource == ChartScanSource.Fallback
                ? ":fallbackReason=" + (string.IsNullOrWhiteSpace(scanResult.FallbackReason) ? "unknown" : scanResult.FallbackReason)
                : string.Empty)
            + (string.IsNullOrWhiteSpace(nativeBridgeReason) ? string.Empty : ":nativeBridgeReason=" + nativeBridgeReason);
        return reason;
    }

    private static long TicksToMilliseconds(long ticks)
    {
        return (long)(ticks * 1000.0 / Stopwatch.Frequency);
    }

    private static string QuoteLogValue(string value)
    {
        if (value == null)
        {
            return "\"\"";
        }
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (left >= long.MaxValue || right >= long.MaxValue || long.MaxValue - left < right)
        {
            return long.MaxValue;
        }
        return left + right;
    }

    private static ChartScanResult MergeScanResults(params ChartScanResult[] scanResults)
    {
        var merged = new ChartScanResult();
        foreach (ChartScanResult scanResult in scanResults.Where(scanResult => scanResult != null))
        {
            merged.ChartFilePaths.UnionWith(scanResult.ChartFilePaths ?? new HashSet<string>(StringComparer.Ordinal));
            MergeFileEntryDictionary(merged.ChartFileEntriesByPath, scanResult.ChartFileEntriesByPath);
            merged.ChartDirectories.UnionWith(scanResult.ChartDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            merged.ChartDirectoriesWithTextFiles.UnionWith(scanResult.ChartDirectoriesWithTextFiles ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            merged.FolderInfoFilePaths.UnionWith(scanResult.FolderInfoFilePaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            MergeFileEntryDictionary(merged.DirectoryEntriesByPath, scanResult.DirectoryEntriesByPath);
            MergeFileEntryDictionary(merged.TextFileEntriesByPath, scanResult.TextFileEntriesByPath);
            MergeFileEntryDictionary(merged.FolderInfoFileEntriesByPath, scanResult.FolderInfoFileEntriesByPath);
            MergeHashDictionary(merged.AudioRelativePathHashesByChartDirectory, scanResult.AudioRelativePathHashesByChartDirectory);
            MergeHashDictionary(merged.ImageRelativePathHashesByChartDirectory, scanResult.ImageRelativePathHashesByChartDirectory);
            MergeHashDictionary(merged.MovieRelativePathHashesByChartDirectory, scanResult.MovieRelativePathHashesByChartDirectory);
            MergeHashDictionary(merged.SelfOwnedAudioRelativePathHashesByChartDirectory, scanResult.SelfOwnedAudioRelativePathHashesByChartDirectory);
            MergeHashDictionary(merged.SelfOwnedImageRelativePathHashesByChartDirectory, scanResult.SelfOwnedImageRelativePathHashesByChartDirectory);
            MergeHashDictionary(merged.SelfOwnedMovieRelativePathHashesByChartDirectory, scanResult.SelfOwnedMovieRelativePathHashesByChartDirectory);
        }
        merged.ResourceHashArraysAreSortedDistinct = true;
        return merged;
    }

    private static bool IsAuthoritativeChartScan(ChartScanExecutionResult scanResult)
    {
        return scanResult != null
            && scanResult.Success
            && scanResult.IsComplete
            && scanResult.Result != null;
    }

    private static string GetChartScanFailureReason(ChartScanExecutionResult scanResult)
    {
        if (scanResult == null)
        {
            return "scan_result_missing";
        }
        if (!string.IsNullOrWhiteSpace(scanResult.IncompleteReason))
        {
            return scanResult.IncompleteReason;
        }
        if (!string.IsNullOrWhiteSpace(scanResult.ErrorReason))
        {
            return scanResult.ErrorReason;
        }
        return scanResult.Success ? "scan_incomplete" : "scan_failed";
    }

    private static void MergeFileEntryDictionary(
        Dictionary<string, RootFileEnumerationEntry> destination,
        Dictionary<string, RootFileEnumerationEntry> source)
    {
        if (destination == null || source == null)
        {
            return;
        }

        foreach (KeyValuePair<string, RootFileEnumerationEntry> item in source)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || item.Value == null)
            {
                continue;
            }

            destination[item.Key] = item.Value;
        }
    }

    private static void MergeHashDictionary(Dictionary<string, uint[]> destination, Dictionary<string, uint[]> source)
    {
        foreach (KeyValuePair<string, uint[]> item in source ?? new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase))
        {
            if (!destination.TryGetValue(item.Key, out uint[] existing) || existing == null || existing.Length == 0)
            {
                destination[item.Key] = [.. (item.Value ?? []).Distinct().OrderBy(hash => hash)];
                continue;
            }
            if (item.Value == null || item.Value.Length == 0)
            {
                continue;
            }
            destination[item.Key] = [.. existing.Concat(item.Value).Distinct().OrderBy(hash => hash)];
        }
    }

    private static ulong CountHashEntries(Dictionary<string, uint[]> hashesByDirectory)
    {
        ulong count = 0UL;
        foreach (uint[] hashes in hashesByDirectory?.Values ?? Enumerable.Empty<uint[]>())
        {
            count += (ulong)(hashes?.Length ?? 0);
        }
        return count;
    }

    public ChartDigestBackfillResult BackfillChartDigests(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<BMSFile> currentFiles,
        Action<int, int, string> reportProgress = null,
        Action<string> logInstallPerformance = null)
    {
        var result = new ChartDigestBackfillResult();
        if (dbGateway == null)
        {
            return result;
        }
        var stopwatchTotal = Stopwatch.StartNew();
        List<BMSFile> targetFiles = [.. (currentFiles ?? []).Where(file => file != null && !string.IsNullOrWhiteSpace(file.hash) && string.IsNullOrWhiteSpace(file.sha256) && !string.IsNullOrWhiteSpace(file.path) && LongPathFileSystem.FileExists(file.path))];
        result.TargetCount = targetFiles.Count;
        reportProgress?.Invoke(result.TargetCount, 0, string.Empty);
        if (targetFiles.Count == 0)
        {
            stopwatchTotal.Stop();
            result.TotalMs = stopwatchTotal.ElapsedMilliseconds;
            return result;
        }
        var stopwatchCompute = Stopwatch.StartNew();
        var completedFiles = new List<BMSFile>(targetFiles.Count);
        foreach (BMSFile file in targetFiles)
        {
            try
            {
                reportProgress?.Invoke(result.TargetCount, result.ProcessedCount, file.path);
                file.ApplySha256(BMSFile.GetSHA256Hash(file.path));
                completedFiles.Add(file);
                result.BackfilledCount++;
            }
            catch
            {
                result.FailedCount++;
                result.FailedPaths.Add(file.path);
            }
            finally
            {
                result.ProcessedCount++;
                reportProgress?.Invoke(result.TargetCount, result.ProcessedCount, file.path);
            }
        }
        stopwatchCompute.Stop();
        result.ComputeMs = stopwatchCompute.ElapsedMilliseconds;
        var stopwatchDbCommit = Stopwatch.StartNew();
        if (completedFiles.Count > 0)
        {
            dbGateway.UpsertChartDigests(completedFiles);
        }
        stopwatchDbCommit.Stop();
        result.DbCommitMs = stopwatchDbCommit.ElapsedMilliseconds;
        stopwatchTotal.Stop();
        result.TotalMs = stopwatchTotal.ElapsedMilliseconds;
        logInstallPerformance?.Invoke("chart_digest_backfill total=" + result.TargetCount + " success=" + result.BackfilledCount + " failed=" + result.FailedCount + " computeMs=" + result.ComputeMs + " dbCommitMs=" + result.DbCommitMs + " totalMs=" + result.TotalMs);
        return result;
    }

    public ScoreTableLoadResult LoadScoreTable(BmsLibraryDbGateway dbGateway, BmsLibraryOptionsSnapshot options = null)
    {
        if (dbGateway == null)
        {
            return new ScoreTableLoadResult
            {
                Status = ScoreTableLoadStatus.Failed,
                FailureMessage = "score database gateway is unavailable."
            };
        }

        var result = new ScoreTableLoadResult
        {
            EnableDownloadLr2IrScoreAndDetectUnsent = options?.EnableDownloadLr2IrScoreAndDetectUnsent ?? false
        };
        result.Lr2PlayHistorySchemaCheckResult = CheckLr2PlayHistorySchemaForScoreLoad(dbGateway, options);
        if (options?.UseBeatorajaScoreDb == true)
        {
            result.ActiveScoreSource = ActiveScoreSource.Beatoraja;
            if (!IsBeatorajaScoreDbEnabled(options))
            {
                result.Status = ScoreTableLoadStatus.Failed;
                result.FailureMessage = "configured beatoraja score.db is unavailable.";
                return result;
            }
            try
            {
                var loader = new BeatorajaScoreDbLoader();
                foreach (KeyValuePair<string, BMSScore> score in loader.LoadModeZeroScores(options.BeatorajaScoreDbPath))
                {
                    result.BeatorajaScoresBySha256[score.Key] = score.Value;
                }
                result.Status = ScoreTableLoadStatus.Loaded;
            }
            catch (Exception ex)
            {
                result.Status = ScoreTableLoadStatus.Failed;
                result.FailureMessage = DescribeScoreLoadFailure(ex);
            }
            return result;
        }

        if (!string.IsNullOrWhiteSpace(dbGateway.ScoreDbPath))
        {
            result.ActiveScoreSource = ActiveScoreSource.Lr2;
            try
            {
                ScoreTableLoadResult lr2Result = dbGateway.LoadScoresAndPlayerId();
                result.Scores.AddRange(lr2Result.Scores);
                result.LR2Id = lr2Result.LR2Id;
                result.ReadOnly = lr2Result.ReadOnly;
                result.DbLockWaitMs = lr2Result.DbLockWaitMs;
                result.Status = ScoreTableLoadStatus.Loaded;
            }
            catch (Exception ex)
            {
                result.Status = ScoreTableLoadStatus.Failed;
                result.FailureMessage = DescribeScoreLoadFailure(ex);
            }
        }

        return result;
    }

    private static string DescribeScoreLoadFailure(Exception exception)
    {
        if (exception == null)
        {
            return "score table load failed.";
        }
        string message = exception.Message?.Trim();
        return string.IsNullOrWhiteSpace(message)
            ? exception.GetType().Name
            : exception.GetType().Name + ": " + message;
    }

    private static Lr2PlayHistorySchemaCheckResult CheckLr2PlayHistorySchemaForScoreLoad(
        BmsLibraryDbGateway dbGateway,
        BmsLibraryOptionsSnapshot options)
    {
        if (options?.OperationModeLR2DB != true || string.IsNullOrWhiteSpace(dbGateway?.ScoreDbPath))
        {
            return null;
        }
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Lr2PlayHistorySchemaCheckResult result = dbGateway.CheckLr2PlayHistorySchema(isLr2LinkedProfile: true);
            LogScoreLoadSchemaCheckPerformance(
                "settings_schema_status_score_load_check elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " status=" + result.Status
                + " path=" + (result.ScoreDbPath ?? string.Empty));
            return result;
        }
        catch (Exception ex)
        {
            LogScoreLoadSchemaCheckWarning(ex, "lr2_play_history_schema_check_failed_at_score_load elapsedMs=" + stopwatch.ElapsedMilliseconds + " path=" + dbGateway.ScoreDbPath);
            return null;
        }
    }

    private static void LogScoreLoadSchemaCheckPerformance(string message)
    {
        try
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Info(message);
        }
        catch
        {
        }
    }

    private static void LogScoreLoadSchemaCheckWarning(Exception ex, string message)
    {
        try
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn(ex, message);
        }
        catch
        {
        }
    }

    private static bool IsBeatorajaScoreDbEnabled(BmsLibraryOptionsSnapshot options)
    {
        if (options?.UseBeatorajaScoreDb != true || string.IsNullOrWhiteSpace(options.BeatorajaScoreDbPath))
        {
            return false;
        }
        try
        {
            return string.Equals(Path.GetFileName(options.BeatorajaScoreDbPath), "score.db", StringComparison.OrdinalIgnoreCase)
                && File.Exists(options.BeatorajaScoreDbPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Restores detached pending packages outside the registered BMS roots.
    /// Protected source paths are classified as stale install rows for the existing
    /// startup reconciliation; neither their files nor catalog rows are deleted.
    /// </summary>
    public InstallTableLoadResult LoadInstallTable(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<string> registeredBmsRoots,
        Func<ChartFile, bool> isInstalledChart = null)
    {
        var result = new InstallTableLoadResult();
        if (dbGateway == null)
        {
            throw new ArgumentNullException(nameof(dbGateway));
        }
        var totalStopwatch = Stopwatch.StartNew();
        var loadStopwatch = Stopwatch.StartNew();
        try
        {
            List<ChartPackage> packages = dbGateway.LoadInstallPackages();
            List<string> libraryDirectories = [.. (registeredBmsRoots ?? [])
                .Where(dir => !string.IsNullOrWhiteSpace(dir))];
            var seenCanonicalPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (ChartPackage package in packages ?? [])
            {
                string rawPath = package?.path;
                if (package == null
                    || !TryNormalizePendingPackagePath(rawPath, out string canonicalPath)
                    || libraryDirectories.Any(dir =>
                        LongPathFileSystem.IsSameOrDescendantDirectoryPath(canonicalPath, dir))
                    || (!LongPathFileSystem.FileExists(canonicalPath)
                        && !LongPathFileSystem.DirectoryExists(canonicalPath))
                    || package.ChartEntries == null
                    || package.ChartEntries.Count == 0
                    || !seenCanonicalPaths.Add(canonicalPath))
                {
                    if (package != null)
                    {
                        result.StalePackages.Add(package);
                    }
                    if (!string.IsNullOrWhiteSpace(rawPath))
                    {
                        result.StaleInstallPaths.Add(rawPath);
                    }
                    continue;
                }

                if (!string.Equals(rawPath, canonicalPath, StringComparison.Ordinal))
                {
                    // Keep the raw primary-key path in the reconciliation set.
                    // The owner deletes it and upserts this canonical survivor
                    // in one existing install-table transaction before publish.
                    result.StaleInstallPaths.Add(rawPath);
                }
                package.path = canonicalPath;
                result.PendingPackages.Add(package);
            }
        }
        catch
        {
            totalStopwatch.Stop();
            result.TotalMs = totalStopwatch.ElapsedMilliseconds;
            throw;
        }
        loadStopwatch.Stop();
        result.LoadMs = loadStopwatch.ElapsedMilliseconds;
        var warningStopwatch = Stopwatch.StartNew();
        foreach (ChartPackage pendingPackage in result.PendingPackages)
        {
            bool isSingleFilePackage = !Directory.Exists(pendingPackage.path);
            foreach (PackageChartEntry entry in pendingPackage.ChartEntries)
            {
                ChartFile chart = entry?.Chart;
                if (chart == null)
                {
                    continue;
                }
                bool isBmson = chart.Kind == ChartFileKind.Bmson;
                if (isInstalledChart != null && isInstalledChart(chart))
                {
                    entry.ClearWarningsByCategory(ChartWarningCategory.InstalledState);
                    entry.SetWarning(ChartWarningKind.AlreadyInstalled, Resources.Warning_AlreadyInstalled);
                    result.InstalledWarningCount++;
                }
                else if (isSingleFilePackage)
                {
                    entry.ClearWarningsByCategory(ChartWarningCategory.PackageLayout);
                    entry.SetWarning(isBmson ? ChartWarningKind.SingleBmsonFile : ChartWarningKind.SingleBmsFile, isBmson ? Resources.Warning_SingleBmsonFile : Resources.Warning_SingleBmsFile);
                    result.SingleFileWarningCount++;
                }
            }
            result.StrictWarningCount += BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjectionToEntries(pendingPackage.ChartEntries);
            BmsLibraryPackageInstallService.ApplyNestedChartFileWarnings(pendingPackage);
        }
        warningStopwatch.Stop();
        result.WarningInitMs = warningStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();
        result.TotalMs = totalStopwatch.ElapsedMilliseconds;
        return result;
    }

    private static bool TryNormalizePendingPackagePath(string path, out string normalizedPath)
    {
        normalizedPath = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            normalizedPath = LongPathFileSystem.TrimTrailingDirectorySeparators(
                LongPathFileSystem.NormalizePathForStorage(path.Trim()));
            return !string.IsNullOrWhiteSpace(normalizedPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or IOException)
        {
            normalizedPath = null;
            return false;
        }
    }

    public InitializationExecutionResult RunInitialize(
        List<Action> tasksContinuation,
        SemaphoreSlim semaphore,
        Action phase1,
        Action phase2,
        Action phase3)
    {
        var result = new InitializationExecutionResult();
        var stopwatchTotal = Stopwatch.StartNew();
        List<Task> continuationTasks = [];
        var stopwatchPhase1 = Stopwatch.StartNew();
        phase1?.Invoke();
        stopwatchPhase1.Stop();
        result.Phase1MinLoadMs = stopwatchPhase1.ElapsedMilliseconds;
        long waitForContinuationSignalMs = 0L;
        long waitForContinuationTasksMs = 0L;
        var stopwatchWaitBeforeContinuationStart = Stopwatch.StartNew();
        semaphore?.Wait();
        stopwatchWaitBeforeContinuationStart.Stop();
        long waitBeforeContinuationStartMs = stopwatchWaitBeforeContinuationStart.ElapsedMilliseconds;

        if (tasksContinuation != null)
        {
            for (int i = 0; i < tasksContinuation.Count; i++)
            {
                continuationTasks.Add(Task.Run(tasksContinuation[i]).LoggingAndPropagate("Initialize"));
            }
        }

        Thread.Yield();

        var stopwatchPhase2 = Stopwatch.StartNew();
        phase2?.Invoke();
        stopwatchPhase2.Stop();
        result.Phase2ScanMaintMs = stopwatchPhase2.ElapsedMilliseconds;

        var stopwatchPhase3 = Stopwatch.StartNew();
        phase3?.Invoke();
        stopwatchPhase3.Stop();
        result.Phase3InstallMaintenanceMs = stopwatchPhase3.ElapsedMilliseconds;

        if (semaphore != null && tasksContinuation != null && tasksContinuation.Count > 0)
        {
            var stopwatchWaitForContinuationSignal = Stopwatch.StartNew();
            semaphore.Wait();
            stopwatchWaitForContinuationSignal.Stop();
            waitForContinuationSignalMs = stopwatchWaitForContinuationSignal.ElapsedMilliseconds;
        }

        var stopwatchWaitForContinuationTasks = Stopwatch.StartNew();
        Task.WaitAll([.. continuationTasks]);
        stopwatchWaitForContinuationTasks.Stop();
        waitForContinuationTasksMs = stopwatchWaitForContinuationTasks.ElapsedMilliseconds;

        result.WaitBeforeContinuationStartMs = waitBeforeContinuationStartMs;
        result.WaitForContinuationSignalMs = waitForContinuationSignalMs;
        result.WaitForContinuationTasksMs = waitForContinuationTasksMs;
        result.WaitContinuationMs = waitBeforeContinuationStartMs + waitForContinuationSignalMs + waitForContinuationTasksMs;
        stopwatchTotal.Stop();
        result.TotalMs = stopwatchTotal.ElapsedMilliseconds;
        return result;
    }

    private static void NormalizeStandaloneSongPathCompatibility(LR2SongDBExtended songDb, IEnumerable<BMSFile> loadedSongs, SongTableLoadResult result)
    {
        var stopwatchSongNormalizeLoop = Stopwatch.StartNew();
        foreach (BMSFile song in loadedSongs ?? [])
        {
            if (song == null || string.IsNullOrWhiteSpace(song.hash))
            {
                continue;
            }
            if (Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(song))
            {
                result.UpdatedSongs.Add(song);
                result.CrcRecalculatedCount++;
            }
        }
        stopwatchSongNormalizeLoop.Stop();
        result.SongNormalizeLoopMs = stopwatchSongNormalizeLoop.ElapsedMilliseconds;
        result.DbWriteRequired = result.UpdatedSongs.Count > 0;
        if (!result.DbWriteRequired)
        {
            return;
        }

        var stopwatchDbWrite = Stopwatch.StartNew();
        songDb.BeginTransaction();
        foreach (BMSFile updatedSong in result.UpdatedSongs)
        {
            Lr2SongDbWriter.UpsertGeneratedSong(songDb, updatedSong);
        }
        var stopwatchCommit = Stopwatch.StartNew();
        songDb.Commit();
        stopwatchCommit.Stop();
        result.CommitMs = stopwatchCommit.ElapsedMilliseconds;
        stopwatchDbWrite.Stop();
        result.DbWriteMs = stopwatchDbWrite.ElapsedMilliseconds;
    }

    private static void NormalizeSongTable(
        LR2SongDBExtended songDb,
        List<BMSFile> loadedSongs,
        List<BMSFile> deletedFiles,
        SongTableLoadResult result,
        Action<string> logInstallPerformance)
    {
        int unixtime = (DateTime.Now + new TimeSpan(30, 0, 0, 0)).ToUnixtime();
        var stopwatchSongNormalizeLoop = Stopwatch.StartNew();
        foreach (BMSFile song in loadedSongs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(song.hash))
                {
                    result.DeletedSongPaths.Add(song.path);
                    deletedFiles.Add(song);
                    continue;
                }
                bool wasRelativePath = !Path.IsPathRooted(song.path);
                string originalPath = song.path;
                if (wasRelativePath)
                {
                    if (Lr2SongFolderParentNormalizer.ApplyExpected(song, songDb.LR2RootPath, fixRelativePath: true))
                    {
                        if (Path.IsPathRooted(song.path))
                        {
                            result.DeletedSongPaths.Add(originalPath);
                            result.RelativePathFixedCount++;
                        }
                        result.UpdatedSongs.Add(song);
                        result.CrcRecalculatedCount++;
                    }
                    continue;
                }
                if (song.adddate < 0 || song.adddate > unixtime)
                {
                    song.adddate = null;
                    song.date = null;
                    result.LeapYearDetected = true;
                    result.UpdatedSongs.Add(song);
                    continue;
                }
                if (Lr2SongFolderParentNormalizer.IsLikelyCrcHex(song.folder) && Lr2SongFolderParentNormalizer.IsLikelyCrcHex(song.parent))
                {
                    if (Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(song))
                    {
                        result.UpdatedSongs.Add(song);
                    }
                    result.CrcSkippedCount++;
                    continue;
                }
                if (Lr2SongFolderParentNormalizer.ApplyExpected(song, songDb.LR2RootPath, fixRelativePath: false))
                {
                    result.UpdatedSongs.Add(song);
                }
                result.CrcRecalculatedCount++;
            }
            catch
            {
                result.DeletedSongPaths.Add(song.path);
                deletedFiles.Add(song);
            }
        }
        stopwatchSongNormalizeLoop.Stop();
        result.SongNormalizeLoopMs = stopwatchSongNormalizeLoop.ElapsedMilliseconds;

        var stopwatchFolderTableLoad = Stopwatch.StartNew();
        List<LR2SongDB.folder> folders = [.. songDb.Table<LR2SongDB.folder>()];
        stopwatchFolderTableLoad.Stop();
        result.FolderTableLoadMs = stopwatchFolderTableLoad.ElapsedMilliseconds;

        var stopwatchFolderNormalizeLoop = Stopwatch.StartNew();
        result.UpdatedFolders.AddRange(folders.Where(delegate (LR2SongDB.folder folder)
        {
            try
            {
                if (!Path.IsPathRooted(folder.path))
                {
                    if (!folder.path.StartsWith("LR2files" + Path.DirectorySeparatorChar + "CustomFolder" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !folder.path.StartsWith("LR2files" + Path.DirectorySeparatorChar + "Rival" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        result.DeletedFolderPaths.Add(folder.path);
                        folder.path = Path.Combine(songDb.LR2RootPath, folder.path);
                        string directoryName = Path.GetDirectoryName(folder.path);
                        if (folder.path.EndsWith("\\", StringComparison.Ordinal))
                        {
                            directoryName = Path.GetDirectoryName(directoryName);
                        }
                        if (folder.parent != Lr2SongFolderParentNormalizer.RootParentHash)
                        {
                            folder.parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(directoryName);
                        }
                        return true;
                    }
                }
                else
                {
                    if (folder.adddate < 0 || folder.adddate > unixtime)
                    {
                        folder.adddate = null;
                        folder.date = null;
                        result.LeapYearDetected = true;
                        return true;
                    }
                    if ((folder.date < 0 || !folder.date.HasValue) && folder.type == 1 && !string.IsNullOrWhiteSpace(folder.path))
                    {
                        string originalFolderPath = folder.path;
                        string directoryPath = originalFolderPath.TrimEnd('\\', '/');
                        if (LongPathFileSystem.DirectoryExists(directoryPath))
                        {
                            DateTime lastWriteTime = LongPathFileSystem.GetLastWriteTime(directoryPath, isDirectory: true);
                            if (IsLeapYearTimestamp(lastWriteTime))
                            {
                                result.LeapYearDetected = true;
                                result.LeapYearRepairCandidates.Add(
                                    new LeapYearFolderRepairCandidate(
                                        originalFolderPath,
                                        directoryPath,
                                        lastWriteTime,
                                        folder));
                            }
                        }
                    }
                }
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
            catch
            {
                result.DeletedFolderPaths.Add(folder.path);
            }
            return false;
        }));
        stopwatchFolderNormalizeLoop.Stop();
        result.FolderNormalizeLoopMs = stopwatchFolderNormalizeLoop.ElapsedMilliseconds;

        var stopwatchFixApply = Stopwatch.StartNew();
        result.DbWriteRequired = result.DeletedSongPaths.Count > 0 || result.UpdatedSongs.Count > 0 || result.DeletedFolderPaths.Count > 0 || result.UpdatedFolders.Count > 0;
        if (result.DbWriteRequired)
        {
            var stopwatchDbWrite = Stopwatch.StartNew();
            songDb.BeginTransaction();
            foreach (string deletedSongPath in result.DeletedSongPaths)
            {
                string deletedHash = songDb.ExecuteScalar<string>("SELECT hash FROM song WHERE path = " + BmsLibraryDbGateway.SqlQuote(deletedSongPath) + " LIMIT 1;");
                songDb.Delete<LR2SongDB.song>(deletedSongPath);
                BmsLibraryDbGateway.DeleteChartDigestIfOrphaned(songDb, deletedHash);
            }
            foreach (BMSFile updatedSong in result.UpdatedSongs)
            {
                Lr2SongDbWriter.UpsertGeneratedSong(songDb, updatedSong);
            }
            foreach (string deletedFolderPath in result.DeletedFolderPaths)
            {
                songDb.Delete<LR2SongDB.folder>(deletedFolderPath);
            }
            foreach (LR2SongDB.folder updatedFolder in result.UpdatedFolders)
            {
                songDb.InsertOrReplace(updatedFolder, typeof(LR2SongDB.folder));
            }
            var stopwatchCommit = Stopwatch.StartNew();
            songDb.Commit();
            stopwatchCommit.Stop();
            result.CommitMs = stopwatchCommit.ElapsedMilliseconds;
            stopwatchDbWrite.Stop();
            result.DbWriteMs = stopwatchDbWrite.ElapsedMilliseconds;
        }
        stopwatchFixApply.Stop();
        result.FixApplyMs = stopwatchFixApply.ElapsedMilliseconds;
        logInstallPerformance?.Invoke(
            "song_tbl_load_detail totalSongs=" + loadedSongs.Count
            + " deletedSongs=" + deletedFiles.Count
            + " updatedSongs=" + result.UpdatedSongs.Count
            + " relativePathFixed=" + result.RelativePathFixedCount
            + " crcRecalculated=" + result.CrcRecalculatedCount
            + " crcSkipped=" + result.CrcSkippedCount
            + " updatedFolders=" + result.UpdatedFolders.Count
            + " deletedFolders=" + result.DeletedFolderPaths.Count);
    }

    private static bool IsLeapYearTimestamp(DateTime lastWriteTime)
    {
        if (!DateTime.IsLeapYear(lastWriteTime.Year))
        {
            return false;
        }
        return new DateTime(lastWriteTime.Year, 2, 29) <= lastWriteTime
            && lastWriteTime < new DateTime(lastWriteTime.Year, 3, 2);
    }


    private static bool TableExists(LR2SongDBExtended songDb, string tableName)
    {
        if (songDb == null || string.IsNullOrWhiteSpace(tableName))
        {
            return false;
        }
        return songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = " + BmsLibraryDbGateway.SqlQuote(tableName) + ";") > 0;
    }

    private static DateTime SafeGetLastWriteTimeUtc(string path)
    {
        try
        {
            return LongPathFileSystem.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

}
