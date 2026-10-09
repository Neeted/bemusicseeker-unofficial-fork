using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlaylistPropertySaveService
{
    private readonly Func<BMSPlaylist> getPlaylistStore;

    private readonly Func<BMSLibrary> getLibrary;

    private readonly Func<LR2Config> getLr2Config;

    private readonly Func<CustomFolderOutputSettingsSnapshot> getSettings;

    internal event EventHandler<PlaylistPropertyValidationErrorEventArgs> ValidationError;

    internal event EventHandler<PlaylistPropertyExternalSyncConfirmationRequestedEventArgs> ExternalSyncConfirmationRequested;

    internal event EventHandler InvalidOutputDirectoryRequested;

    internal event EventHandler PlaylistPropertySyncStarted;

    internal event EventHandler<PlaylistSyncProgressChangedEventArgs> PlaylistPropertySyncProgressChanged;

    internal event EventHandler PlaylistPropertySyncFinished;

    internal event EventHandler<PlaylistReferenceTableReplacedEventArgs> PlaylistPropertyReferenceTableReplaced;

    internal event EventHandler<PlaylistPropertyFolderSelectionRemappedEventArgs> PlaylistPropertyFolderSelectionRemapped;

    internal event EventHandler PlaylistPropertyReferenceSortInvalidationRequested;

    internal event EventHandler<PlaylistSyncResultReportedEventArgs> PlaylistPropertySyncResultReported;

    internal event EventHandler<PlaylistPropertyExternalSyncFailedEventArgs> PlaylistPropertyExternalSyncFailed;

    internal event EventHandler<PlaylistSummaryDataRefreshRequestedEventArgs> PlaylistPropertySummaryDataRefreshRequested;

    internal event EventHandler<PlaylistPropertyEntriesChangedEventArgs> PlaylistPropertyEntriesChanged;

    internal event EventHandler<PlaylistOperationNotificationPresentationRequestedEventArgs> PlaylistOperationNotificationPresentationRequested;

    internal PlaylistPropertySaveService(
        Func<BMSPlaylist> playlistStore,
        Func<BMSLibrary> library,
        Func<LR2Config> lr2Config,
        Func<CustomFolderOutputSettingsSnapshot> settings)
    {
        getPlaylistStore = playlistStore ?? throw new ArgumentNullException(nameof(playlistStore));
        getLibrary = library ?? throw new ArgumentNullException(nameof(library));
        getLr2Config = lr2Config ?? throw new ArgumentNullException(nameof(lr2Config));
        getSettings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    internal Task<PlaylistPropertyEditSession> CreateEditSessionAsync(
        BMSTable table,
        bool isNewTable = false)
    {
        return Task.Run(() => CreateEditSessionCore(table, isNewTable));
    }

    private PlaylistPropertyEditSession CreateEditSessionCore(
        BMSTable table,
        bool isNewTable,
        CustomFolderOutputSettingsSnapshot settingsOverride = null,
        PlaylistPropertyBaseline baselineOverride = null)
    {
        if (table == null)
        {
            throw new ArgumentNullException(nameof(table));
        }
        BMSPlaylist store = GetPlaylistStore();
        store.AcquireReaderLockBMSTables();
        try
        {
            if (!isNewTable && store.BMSTables?.Cast<BMSTable>().Any(candidate => ReferenceEquals(candidate, table)) != true)
            {
                return null;
            }
            store.EnsurePlaylistEntriesLoaded(
                table,
                "PlaylistPropertySaveService.CreateEditSession");
            CustomFolderOutputSettingsSnapshot settings = settingsOverride ?? getSettings()
                ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                string outputDirectoryPath = null;
                if (baselineOverride == null && settings.OperationModeLR2DB)
                {
                    try
                    {
                        outputDirectoryPath = ResolveCustomFolderOutputDirectory(table, settings);
                    }
                    catch (ArgumentNullException)
                    {
                        RaiseInvalidOutputDirectoryRequested();
                        throw;
                    }
                }
                return new PlaylistPropertyEditSession(
                    store,
                    table,
                    isNewTable,
                    settings,
                    baselineOverride ?? PlaylistPropertyBaseline.Capture(table, outputDirectoryPath),
                    PlaylistPropertyValues.Capture(table),
                    (store.BMSTables?.Cast<BMSTable>() ?? Enumerable.Empty<BMSTable>())
                        .Where(candidate => candidate != null && candidate != table)
                        .Select(candidate => candidate.Output_dir)
                        .ToArray());
            }
        }
        finally
        {
            store.FreeReaderLockBMSTables();
        }
    }

    internal bool ContainsActiveTable(BMSTable table)
    {
        return table != null
            && GetPlaylistStore().BMSTables?.Cast<BMSTable>().Any(candidate => ReferenceEquals(candidate, table)) == true;
    }

    internal bool IsValid(PlaylistPropertyEditSession session, PlaylistPropertyValues values)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }
        if (values == null)
        {
            throw new ArgumentNullException(nameof(values));
        }
        session.ThrowIfDisposed();
        if (!IsValidForPresentation(session, values))
        {
            return false;
        }
        return true;
    }

    internal bool IsOutputDirectoryValid(
        PlaylistPropertyEditSession session,
        string playlistName,
        string storedOutputDirectory)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }
        session.ThrowIfDisposed();
        string effectiveName = BMSTable.ResolveOutputDirectoryName(playlistName, storedOutputDirectory);
        return !string.IsNullOrWhiteSpace(effectiveName)
            && !session.OtherOutputDirectories.Contains(
                effectiveName,
                StringComparer.OrdinalIgnoreCase);
    }

    internal void NotifyValidationError(PlaylistPropertyValidationError error)
    {
        EventHandler<PlaylistPropertyValidationErrorEventArgs> handler = ValidationError
            ?? throw new InvalidOperationException("Playlist property validation presentation is not configured.");
        handler(this, new PlaylistPropertyValidationErrorEventArgs(error));
    }

    internal bool ConfirmExternalSyncChange(bool enable)
    {
        EventHandler<PlaylistPropertyExternalSyncConfirmationRequestedEventArgs> handler = ExternalSyncConfirmationRequested
            ?? throw new InvalidOperationException("Playlist property external-sync confirmation is not configured.");
        var request = new PlaylistPropertyExternalSyncConfirmationRequestedEventArgs(enable);
        handler(this, request);
        return request.Confirmed;
    }

    private void RaiseInvalidOutputDirectoryRequested()
    {
        EventHandler handler = InvalidOutputDirectoryRequested
            ?? throw new InvalidOperationException("Playlist property output-directory presentation is not configured.");
        handler(this, EventArgs.Empty);
    }

    private void RaiseRequiredEvent(EventHandler handler, EventArgs args, string contractName)
    {
        (handler ?? throw new InvalidOperationException(contractName + " is not configured."))(this, args);
    }

    private void RaiseRequiredEvent<TEventArgs>(
        EventHandler<TEventArgs> handler,
        TEventArgs args,
        string contractName)
        where TEventArgs : EventArgs
    {
        (handler ?? throw new InvalidOperationException(contractName + " is not configured."))(this, args);
    }

    /// <summary>局所受付後に現配置・正本対象を固定して保存し、受理済み権限は借用します。確定結果を返し、入力不備は検証結果とします。</summary>
    /// <param name="capability">外側が公開・必須通知・cleanup実終端まで保持する生存局所権限。nullは新規非待機受付です。</param>
    internal Task<PlaylistPropertySaveCommit> TrySaveAsync(
        PlaylistPropertyEditSession session,
        PlaylistPropertyValues values, LibraryFileMutationCapability capability = null)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }
        if (values == null)
        {
            throw new ArgumentNullException(nameof(values));
        }
        session.ThrowIfDisposed();
        return Task.Run(() =>
        {
            using LibraryFileMutationLease accepted = session.Store.AcquirePlaylistMutationLease("playlist_property_save", capability: capability);
            return TrySaveCore(session, values);
        });
    }

    private PlaylistPropertySaveCommit TrySaveCore(
        PlaylistPropertyEditSession session,
        PlaylistPropertyValues values)
    {
        BMSTable table = session.Table;
        BMSPlaylist store = session.Store;
        CustomFolderOutputSettingsSnapshot settings = store.GetCustomFolderOutputSettings();
        var baseline = PlaylistPropertyBaseline.Capture(table, settings.OperationModeLR2DB
            ? ResolveCustomFolderOutputDirectory(table, settings)
            : null);
        store.AcquireWriterLockBMSTables();
        try
        {
            if (!ReferenceEquals(store, GetPlaylistStore()) || (!session.IsNewTable
                && store.BMSTables?.Cast<BMSTable>().Any(candidate => ReferenceEquals(candidate, table)) != true))
            {
                return null;
            }
            if (!string.Equals(table.compat_prefix, values.CompatPrefix, StringComparison.Ordinal))
            {
                store.EnsurePlaylistEntriesLoaded(
                    table,
                    "PlaylistPropertySaveService.ValidateCompatibleFolderPrefixRewrite");
            }
            using (table.ReaderWriterLock.GetWriterGuard())
            {
                if (!PlaylistPropertyValues.ContentEquals(
                    session.OriginalValues,
                    PlaylistPropertyValues.Capture(table)))
                {
                    throw new InvalidOperationException(
                        "Playlist properties changed after editing began. Reopen the dialog and apply the edit again.");
                }
                if (!IsValid(table, store, settings, values))
                {
                    return null;
                }
                if (!string.Equals(table.compat_prefix, values.CompatPrefix, StringComparison.Ordinal)
                    && !table.CanRewriteCompatibleFolderPrefix(
                        table.compat_prefix,
                        values.CompatPrefix))
                {
                    return null;
                }

                Uri pageUrl = NormalizeUriTextForStandardStorage(values.PageUrl);
                Uri headerUrl = NormalizeUriTextForStandardStorage(values.HeaderUrl);
                Uri dataUrl = NormalizeUriTextForStandardStorage(values.DataUrl);
                var originalValues = PlaylistPropertyValues.Capture(table);
                try
                {
                    ApplyPropertyValues(table, values, pageUrl, headerUrl, dataUrl);
                }
                catch (Exception applicationFailure)
                {
                    try
                    {
                        RestorePropertyValues(table, originalValues);
                    }
                    catch (Exception rollbackFailure)
                    {
                        throw new AggregateException(applicationFailure, rollbackFailure).Flatten();
                    }
                    ExceptionDispatchInfo.Capture(applicationFailure).Throw();
                    throw new InvalidOperationException("Playlist property application failure propagation unexpectedly returned.");
                }
                return new PlaylistPropertySaveCommit(
                    baseline,
                    table,
                    settings,
                    values, session.IsNewTable);
            }
        }
        finally
        {
            store.FreeWriterLockBMSTables();
        }
    }

    private static void ApplyPropertyValues(
        BMSTable table,
        PlaylistPropertyValues values,
        Uri pageUrl,
        Uri headerUrl,
        Uri dataUrl)
    {
        List<string> folderOrder = values.IsAutoFolderSort
            ? []
            : [.. values.FolderOrder ?? []];
        if (!(table.Folder_order ?? []).SequenceEqual(folderOrder, StringComparer.Ordinal))
        {
            table.Folder_order = folderOrder;
        }
        table.folder_sort_key = values.FolderSortKey;
        table.folder_sort_ascending = values.FolderSortAscending;
        table.ignore_folder_output = values.IgnoreFolderOutput;
        table.entry_type = values.EntryType;
        table.name = values.Name;
        table.symbol = values.Symbol;
        table.Page_url = pageUrl;
        table.Header_url = headerUrl;
        table.Data_url = dataUrl;
        if (values.IsExternalSync)
        {
            table.EnableExternalSync();
        }
        else
        {
            table.DisableExternalSync();
        }
        table.Output_dir = values.OutputDirectory;
        table.custom_folder_output_base_name = values.CustomFolderOutputBaseName;
        table.is_root_folder = values.IsRootFolder;
        table.compat_prefix = values.CompatPrefix;
    }

    private static void RestorePropertyValues(BMSTable table, PlaylistPropertyValues values)
    {
        var failures = new List<Exception>();
        void Restore(Action restore)
        {
            try
            {
                restore();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        Restore(() => table.Folder_order = [.. values.PersistedFolderOrder ?? []]);
        Restore(() => table.folder_sort_key = values.FolderSortKey);
        Restore(() => table.folder_sort_ascending = values.FolderSortAscending);
        Restore(() => table.ignore_folder_output = values.IgnoreFolderOutput);
        Restore(() => table.entry_type = values.EntryType);
        Restore(() => table.name = values.Name);
        Restore(() => table.symbol = values.Symbol);
        Restore(() => table.Page_url = values.PageUrl);
        Restore(() => table.Header_url = values.HeaderUrl);
        Restore(() => table.Data_url = values.DataUrl);
        Restore(() =>
        {
            if (values.IsExternalSync)
            {
                table.EnableExternalSync();
            }
            else
            {
                table.DisableExternalSync();
            }
        });
        Restore(() => table.Output_dir = values.OutputDirectory);
        Restore(() => table.custom_folder_output_base_name = values.CustomFolderOutputBaseName);
        Restore(() => table.is_root_folder = values.IsRootFolder);
        Restore(() => table.compat_prefix = values.CompatPrefix);
        if (failures.Count > 0)
        {
            throw new AggregateException(failures).Flatten();
        }
    }

    internal async Task<PlaylistPropertyValues> ResetAsync(PlaylistPropertyEditSession session)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }
        session.ThrowIfDisposed();
        await Task.CompletedTask.ConfigureAwait(false);
        return session.Values;
    }

    internal Task<PlaylistPropertyEditSession> ReconcileFailedSaveAsync(
        PlaylistPropertyEditSession session,
        PlaylistPropertySaveCommit commit)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }
        session.ThrowIfDisposed();
        return Task.Run(() =>
        {
            BMSTable activeTable = commit.Table
                ?? throw new InvalidOperationException("Failed playlist save has no active reconciliation target.");
            return CreateEditSessionCore(
                activeTable,
                !session.Store.ContainsBMSTable(activeTable),
                session.Settings,
                baselineOverride: null)
                ?? throw new InvalidOperationException("Failed playlist save target is no longer active.");
        });
    }

    /// <summary>受付時に捕捉した設定で保存済み事実に必要な出力・公開・通知を同じ局所権限で終えます。元失敗は確定を戻さず返します。</summary>
    /// <param name="capability">保存から継続する生存局所権限。外側が実終端まで保持します。</param>
    internal async Task ApplyPostSaveUpdatesAsync(PlaylistPropertySaveCommit commit, LibraryFileMutationCapability capability = null)
    {
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }
        BMSPlaylist store = GetPlaylistStore();
        using LibraryFileMutationLease accepted = store.AcquirePlaylistMutationLease("playlist_property_followup", capability: capability);
        using LibraryFileMutationCapability authority = accepted.CreateMutationCapability();
        PlaylistPropertyBaseline baseline = commit.Baseline;
        CustomFolderOutputSettingsSnapshot settings = commit.Settings;
        BMSTable table = commit.Table;
        if (commit.IsNewDraft)
        {
            await store.RegisterDraftAsync(table, authority).ConfigureAwait(false);
        }
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = store.OperationNotificationOwner.BeginSession();
        List<Exception> failures = [];
        bool prefixChanged = !string.Equals(baseline.CompatPrefix, table.compat_prefix, StringComparison.Ordinal);
        bool outputDirectoryChanged = !string.Equals(
            BMSTable.NormalizeOutputDirectoryName(baseline.OutputDirectory),
            BMSTable.NormalizeOutputDirectoryName(table.output_dir),
            StringComparison.Ordinal);
        bool displayProjectionChanged = !string.Equals(baseline.Name, table.name, StringComparison.Ordinal)
            || !string.Equals(baseline.Symbol, table.symbol, StringComparison.Ordinal)
            || prefixChanged;
        bool outputBaseNameChanged = !string.Equals(
            baseline.CustomFolderOutputBaseName,
            CustomFolderOutputBaseRegistry.NormalizeBaseName(table.custom_folder_output_base_name),
            StringComparison.OrdinalIgnoreCase);
        bool externalResyncApplied = false;
        bool entryFolderProjectionChanged = false;
        CompatibleFolderPrefixRewritePlan prefixRewritePlan = null;
        IReadOnlyDictionary<string, string> prefixFolderSelectionMap = null;
        if (prefixChanged)
        {
            store.EnsurePlaylistEntriesLoaded(table, "PlaylistPropertySaveService.CreateCompatibleFolderPrefixRewriteMap");
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                prefixRewritePlan = table.CreateCompatibleFolderPrefixRewritePlan(baseline.CompatPrefix, table.compat_prefix);
                prefixFolderSelectionMap = prefixRewritePlan.FolderMap;
            }
        }

        bool shouldReloadExternalPlaylist = (!baseline.IsExternalSync && table.is_external_sync)
            || (table.is_external_sync
                && table.Page_url != null
                && baseline.PageUrl != null
                && table.Page_url.ToString() != baseline.PageUrl.ToString());
        if (shouldReloadExternalPlaylist)
        {
            Uri uri = table.Page_url ?? table.Header_url;
            if (uri != null && uri.IsAbsoluteUri)
            {
                BMSTable sourceTable = table;
                IReadOnlyList<BMSTableEntry> oldEntries = null;
                bool externalLastUpdateChanged = false;
                bool externalReloadFailed = false;
                Exception syncWorkflowFailure = null;
                try
                {
                    CapturePresentationFailure(() => RaiseRequiredEvent(
                        PlaylistPropertySyncStarted,
                        EventArgs.Empty,
                        "Playlist property sync-start presentation"), failures);
                    CapturePresentationFailure(() => RaiseRequiredEvent(
                        PlaylistPropertySyncProgressChanged,
                        new PlaylistSyncProgressChangedEventArgs(new PlaylistSyncProgressSnapshot
                        {
                            IsActive = true,
                            TotalTableCount = 1,
                            CompletedTableCount = 0,
                            CurrentTableName = table.name,
                            CurrentUri = uri
                        }),
                        "Playlist property sync-progress presentation"), failures);
                    try
                    {
                        DateTime lastUpdate = table.last_update;
                        store.EnsurePlaylistEntriesLoaded(table, "PlaylistPropertySaveService.ApplyPostSaveUpdatesAsync");
                        using (table.ReaderWriterLock.GetReaderGuard()) { oldEntries = [.. table.entries]; }
                        table = await store.ExternalSyncOwner.ReloadAndApplySingleTableAsync(table, uri,
                            "PlaylistPropertySaveService.ApplyPostSaveUpdatesAsync", capability: authority);
                        commit.Table = table;
                        externalLastUpdateChanged = table.last_update != lastUpdate;
                    }
                    catch (Exception ex)
                    {
                        externalReloadFailed = true;
                        failures.Add(ex);
                        CapturePresentationFailure(() => RaiseRequiredEvent(PlaylistPropertyExternalSyncFailed,
                            new PlaylistPropertyExternalSyncFailedEventArgs(sourceTable, uri, ex),
                            "Playlist property sync-failure presentation"), failures);
                    }
                    if (!externalReloadFailed)
                    {
                        externalResyncApplied = true;
                        displayProjectionChanged = false;
                        CapturePresentationFailure(() => GetLibrary().ReplaceReferenceBMSTable(
                            sourceTable, table, oldEntries), failures);
                        CapturePresentationFailure(() => RaiseRequiredEvent(
                            PlaylistPropertyReferenceTableReplaced,
                            new PlaylistReferenceTableReplacedEventArgs(
                                sourceTable,
                                table),
                            "Playlist property reference-table replacement"), failures);
                        CapturePresentationFailure(() => RaiseRequiredEvent(
                            PlaylistPropertyFolderSelectionRemapped,
                            new PlaylistPropertyFolderSelectionRemappedEventArgs(table, prefixFolderSelectionMap),
                            "Playlist property folder-selection remap"), failures);
                        CapturePresentationFailure(() => RaiseRequiredEvent(
                            PlaylistPropertyReferenceSortInvalidationRequested,
                            EventArgs.Empty,
                            "Playlist property reference-sort invalidation"), failures);
                        CapturePresentationFailure(() => RaiseRequiredEvent(
                            PlaylistPropertySyncResultReported,
                            new PlaylistSyncResultReportedEventArgs(PlaylistSyncAttemptResult.CreateSuccess(
                                sourceTable,
                                table,
                                uri,
                                externalLastUpdateChanged)),
                            "Playlist property sync-result presentation"), failures);
                    }
                }
                catch (Exception ex)
                {
                    syncWorkflowFailure = ex;
                }
                CapturePresentationFailure(() => CompletePlaylistPropertySyncPresentation(
                    table, uri, notificationSession, syncWorkflowFailure), failures);
                CapturePresentationFailure(() => RaiseRequiredEvent(
                    PlaylistPropertySummaryDataRefreshRequested,
                    new PlaylistSummaryDataRefreshRequestedEventArgs(
                        "playlist_property_resync"),
                    "Playlist property summary refresh"), failures);
            }
        }

        if (prefixChanged && !externalResyncApplied)
        {
            store.EnsurePlaylistEntriesLoaded(table, "PlaylistPropertySaveService.RewriteCompatibleFolderPrefix");
            using (table.ReaderWriterLock.GetWriterGuard())
            {
                entryFolderProjectionChanged = prefixRewritePlan.FolderMap.Count > 0;
                CapturePresentationFailure(() => table.ApplyCompatibleFolderPrefixRewritePlan(prefixRewritePlan), failures);
            }
        }
        if (entryFolderProjectionChanged)
        {
            store.CommitBMSTableWithEntriesToDB(table, authority);
        }
        else
        {
            store.CommitBMSTableHeaderToDB(table, authority);
        }

        if (prefixChanged && !externalResyncApplied)
        {
            IReadOnlyDictionary<string, string> rewrittenFolders = prefixFolderSelectionMap;
            if (entryFolderProjectionChanged)
            {
                CapturePresentationFailure(() => RaiseRequiredEvent(
                    PlaylistPropertyFolderSelectionRemapped,
                    new PlaylistPropertyFolderSelectionRemappedEventArgs(table, rewrittenFolders),
                    "Playlist property folder-selection remap"), failures);
                displayProjectionChanged = true;
            }
        }

        if (displayProjectionChanged)
        {
            CapturePresentationFailure(() => GetLibrary().RefreshReferenceDisplayForTable(table), failures);
            CapturePresentationFailure(() => RaiseRequiredEvent(
                PlaylistPropertyReferenceSortInvalidationRequested,
                EventArgs.Empty,
                "Playlist property reference-sort invalidation"), failures);
            if (entryFolderProjectionChanged)
            {
                CapturePresentationFailure(() => RaiseRequiredEvent(
                    PlaylistPropertyEntriesChanged,
                    new PlaylistPropertyEntriesChangedEventArgs(table),
                    "Playlist property entries-changed publication"), failures);
            }
            else
            {
                CapturePresentationFailure(() => RaiseRequiredEvent(
                    PlaylistPropertySummaryDataRefreshRequested,
                    new PlaylistSummaryDataRefreshRequestedEventArgs(
                        "playlist_property_changed"),
                    "Playlist property summary refresh"), failures);
            }
        }
        else if (externalResyncApplied)
        {
            CapturePresentationFailure(() => RaiseRequiredEvent(
                PlaylistPropertyEntriesChanged,
                new PlaylistPropertyEntriesChangedEventArgs(table),
                "Playlist property entries-changed publication"), failures);
        }

        if (settings.OperationModeLR2DB)
        {
            try
            {
                string customFolderOutputDirectory;
                try
                {
                    customFolderOutputDirectory = ResolveCustomFolderOutputDirectory(table, settings);
                }
                catch (ArgumentNullException)
                {
                    RaiseInvalidOutputDirectoryRequested();
                    throw;
                }
                bool outputBaseDirectoryBeforeResolved = baseline.IsRootFolder;
                string outputBaseDirectoryBefore = baseline.IsRootFolder
                    ? settings.LR2CustomFolderOutputBaseDirRootType
                    : null;
                if (!baseline.IsRootFolder)
                {
                    outputBaseDirectoryBeforeResolved = CustomFolderOutputBaseRegistry.TryResolveNormalOutputBaseDirectory(
                        baseline.CustomFolderOutputBaseName,
                        settings.LR2CustomFolderOutputBaseDir,
                        settings.LR2CustomFolderAdditionalOutputBaseDirs,
                        out outputBaseDirectoryBefore);
                }
                try
                {
                    store.MigrateCustomFolderOutputDirectoryWithSettings(
                        table,
                        baseline.OutputDirectoryPath,
                        customFolderOutputDirectory,
                        wasRootFolderBefore: baseline.IsRootFolder,
                        rootOutputBaseDirBefore: null,
                        outputBaseDirBefore: outputBaseDirectoryBeforeResolved ? outputBaseDirectoryBefore : null,
                        inferOutputBaseDirBeforeWhenMissing: outputBaseDirectoryBeforeResolved,
                        settings: settings, capability: authority);
                }
                finally
                {
                    CapturePresentationFailure(() => RaiseRequiredEvent(
                        PlaylistOperationNotificationPresentationRequested,
                        new PlaylistOperationNotificationPresentationRequestedEventArgs(
                            notificationSession.TakeReceipt(),
                            "playlist property custom folder notification"),
                        "Playlist property notification flushing"), failures);
                }
                LR2Config config = getLr2Config()
                    ?? throw new InvalidOperationException("LR2 configuration is not available.");
                List<string> searchDirectories = config.GetBMSSearchDirectoriesForChangeTracking();
                if (!baseline.IsRootFolder && table.is_root_folder)
                {
                    config.SetBMSSearchDirectories(searchDirectories
                        .Union([customFolderOutputDirectory])
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                    config.Save();
                }
                else if (baseline.IsRootFolder && !table.is_root_folder)
                {
                    config.SetBMSSearchDirectories(searchDirectories
                        .Except([baseline.OutputDirectoryPath], StringComparer.OrdinalIgnoreCase));
                    config.Save();
                }
                else if (baseline.IsRootFolder
                    && table.is_root_folder
                    && !customFolderOutputDirectory.Equals(baseline.OutputDirectoryPath, StringComparison.OrdinalIgnoreCase))
                {
                    config.SetBMSSearchDirectories(searchDirectories
                        .Except([baseline.OutputDirectoryPath], StringComparer.OrdinalIgnoreCase)
                        .Union([customFolderOutputDirectory])
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                    config.Save();
                }

            }
            catch (Exception failure) { failures.Add(failure); }
        }

        if (outputBaseNameChanged || outputDirectoryChanged)
        {
            CapturePresentationFailure(() => RaiseRequiredEvent(
                PlaylistPropertySummaryDataRefreshRequested,
                new PlaylistSummaryDataRefreshRequestedEventArgs(
                    "playlist_property_output_changed"),
                "Playlist property summary refresh"), failures);
        }
        bool bmtProjectionChanged = displayProjectionChanged
            || prefixChanged
            || outputDirectoryChanged
            || externalResyncApplied
            || entryFolderProjectionChanged
            || baseline.IsExternalSync != table.is_external_sync
            || !HasSameUri(baseline.PageUrl, table.Page_url)
            || !HasSameUri(baseline.HeaderUrl, table.Header_url)
            || !HasSameStringSequence(baseline.FolderOrder, table.Folder_order);
        if (bmtProjectionChanged)
        {
            try { await store.BmtOutput.ExportTablesAsync([table], "PlaylistPropertySaveService.ApplyPostSaveUpdatesAsync", authority).ConfigureAwait(false); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count == 1) { ExceptionDispatchInfo.Capture(failures[0]).Throw(); }
        if (failures.Count > 1) { throw new AggregateException(failures).Flatten(); }
    }

    private void CompletePlaylistPropertySyncPresentation(
        BMSTable table,
        Uri uri,
        PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession,
        Exception workflowFailure)
    {
        var failures = new List<Exception>();
        if (workflowFailure != null)
        {
            failures.Add(workflowFailure);
        }
        CapturePresentationFailure(
            () => RaiseRequiredEvent(
                PlaylistPropertySyncProgressChanged,
                new PlaylistSyncProgressChangedEventArgs(new PlaylistSyncProgressSnapshot
                {
                    IsActive = true,
                    TotalTableCount = 1,
                    CompletedTableCount = 1,
                    CurrentTableName = table.name,
                    CurrentUri = uri
                }),
                "Playlist property sync-progress presentation"),
            failures);
        CapturePresentationFailure(
            () => RaiseRequiredEvent(
                PlaylistPropertySyncFinished,
                EventArgs.Empty,
                "Playlist property sync-finish presentation"),
            failures);
        CapturePresentationFailure(
            () => RaiseRequiredEvent(
                PlaylistOperationNotificationPresentationRequested,
                new PlaylistOperationNotificationPresentationRequestedEventArgs(
                    notificationSession.TakeReceipt(),
                    "playlist property external sync notification"),
                "Playlist property notification flushing"),
            failures);
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1)
        {
            throw new AggregateException(failures).Flatten();
        }
    }

    private static void CapturePresentationFailure(Action action, ICollection<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    private static bool HasSameUri(Uri left, Uri right)
    {
        return string.Equals(left?.ToString() ?? string.Empty, right?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    private static bool HasSameStringSequence(IEnumerable<string> left, IEnumerable<string> right)
    {
        return (left ?? Enumerable.Empty<string>()).SequenceEqual(
            right ?? Enumerable.Empty<string>(),
            StringComparer.Ordinal);
    }

    private static bool IsValid(
        BMSTable table,
        BMSPlaylist store,
        CustomFolderOutputSettingsSnapshot settings,
        PlaylistPropertyValues values)
    {
        return IsValid(
            settings,
            values,
            (store.BMSTables?.Cast<BMSTable>() ?? Enumerable.Empty<BMSTable>())
                .Where(candidate => candidate != null && candidate != table)
                .Select(candidate => candidate.Output_dir));
    }

    private static bool IsValidForPresentation(
        PlaylistPropertyEditSession session,
        PlaylistPropertyValues values)
    {
        return IsValid(
            session.Settings,
            values,
            session.OtherOutputDirectories);
    }

    private static bool IsValid(
        CustomFolderOutputSettingsSnapshot settings,
        PlaylistPropertyValues values,
        IEnumerable<string> otherOutputDirectories)
    {
        if ((values.PageUrl != null && (!IsUrlValid(values.PageUrl) || !values.PageUrl.IsAbsoluteUri))
            || !IsUrlValid(values.HeaderUrl)
            || !IsUrlValid(values.DataUrl))
        {
            return false;
        }
        if (values.IsExternalSync
            && !(values.PageUrl?.Scheme == "bmseeker"
                || (values.HeaderUrl != null
                    && values.DataUrl != null
                    && ((values.PageUrl?.IsAbsoluteUri == true) || values.HeaderUrl.IsAbsoluteUri))))
        {
            return false;
        }
        if (settings.OperationModeLR2DB)
        {
            string effectiveOutputDirectory = BMSTable.ResolveOutputDirectoryName(
                values.Name,
                values.OutputDirectory);
            if (string.IsNullOrWhiteSpace(effectiveOutputDirectory)
                || (otherOutputDirectories ?? Enumerable.Empty<string>())
                    .Contains(effectiveOutputDirectory, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
            NormalizeFolderSettings(values);
        }
        return true;
    }

    private static void NormalizeFolderSettings(PlaylistPropertyValues values)
    {
        values.IgnoreFolderOutput = NormalizeIgnoreFolderOutput(values.EntryType, values.IgnoreFolderOutput);
        if (values.EntryType == LR2SongDBExtended.playlist.EntryUnitType.Folder
            && (values.FolderSortKey == LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL
                || values.FolderSortKey == LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE))
        {
            values.FolderSortKey = LR2SongDBExtended.playlist.CustomFolderSortType.NONE;
        }
    }

    private static LR2SongDBExtended.playlist.CustomFolderType NormalizeIgnoreFolderOutput(
        LR2SongDBExtended.playlist.EntryUnitType entryType,
        LR2SongDBExtended.playlist.CustomFolderType value)
    {
        return entryType == LR2SongDBExtended.playlist.EntryUnitType.Folder
            ? value | LR2SongDBExtended.playlist.CustomFolderType.LevelFolder
            : value;
    }

    private static bool IsUrlValid(Uri value)
    {
        return value == null
            || string.IsNullOrWhiteSpace(value.ToString())
            || Uri.TryCreate(value.OriginalString, UriKind.RelativeOrAbsolute, out _);
    }

    private static Uri NormalizeUriTextForStandardStorage(Uri value)
    {
        if (value == null)
        {
            return null;
        }
        return value.IsAbsoluteUri
            ? new Uri(value.AbsoluteUri, UriKind.Absolute)
            : new Uri(value.OriginalString, UriKind.RelativeOrAbsolute);
    }

    internal static string NormalizeSummaryOutputDirectory(string playlistName, string outputDirectoryName)
    {
        string normalized = BMSTable.NormalizeOutputDirectoryName(outputDirectoryName);
        string defaultName = BMSTable.CreateDefaultOutputDirectoryName(playlistName);
        return !string.IsNullOrWhiteSpace(normalized)
            && !string.Equals(defaultName, normalized, StringComparison.Ordinal)
            ? normalized
            : null;
    }

    internal static string ResolveCustomFolderOutputDirectory(
        BMSTable table,
        CustomFolderOutputSettingsSnapshot settings)
    {
        return BMSPlaylist.GetCustomFolderOutputDirectory(
            table,
            settings.LR2CustomFolderOutputBaseDir,
            settings.LR2CustomFolderOutputBaseDirRootType,
            settings.LR2CustomFolderAdditionalOutputBaseDirs);
    }

    private BMSPlaylist GetPlaylistStore()
    {
        return getPlaylistStore()
            ?? throw new InvalidOperationException("Playlist persistence is not available.");
    }

    private BMSLibrary GetLibrary()
    {
        return getLibrary()
            ?? throw new InvalidOperationException("Playlist library is not available.");
    }
}

internal sealed class PlaylistPropertyEditSession : IDisposable
{
    private bool disposed;

    internal PlaylistPropertyEditSession(
        BMSPlaylist store,
        BMSTable table,
        bool isNewTable,
        CustomFolderOutputSettingsSnapshot settings,
        PlaylistPropertyBaseline baseline,
        PlaylistPropertyValues values,
        IReadOnlyList<string> otherOutputDirectories)
    {
        Store = store;
        Table = table;
        IsNewTable = isNewTable;
        Settings = settings;
        Baseline = baseline;
        OriginalValues = values;
        Values = values.Clone();
        OtherOutputDirectories = otherOutputDirectories ?? [];
    }

    internal BMSPlaylist Store { get; }

    internal BMSTable Table { get; }

    internal bool IsNewTable { get; }

    internal CustomFolderOutputSettingsSnapshot Settings { get; }

    internal PlaylistPropertyBaseline Baseline { get; }

    internal PlaylistPropertyValues Values { get; }

    internal PlaylistPropertyValues OriginalValues { get; }

    internal IReadOnlyList<string> OtherOutputDirectories { get; }

    internal void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(PlaylistPropertyEditSession));
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
    }
}

internal sealed class PlaylistPropertyValues
{
    internal IReadOnlyList<string> FolderOrder { get; set; }

    internal IReadOnlyList<string> PersistedFolderOrder { get; set; }

    internal int EntriesRevision { get; set; }

    internal bool IsAutoFolderSort { get; set; }

    internal LR2SongDBExtended.playlist.CustomFolderSortType FolderSortKey { get; set; }

    internal bool FolderSortAscending { get; set; }

    internal LR2SongDBExtended.playlist.CustomFolderType IgnoreFolderOutput { get; set; }

    internal LR2SongDBExtended.playlist.EntryUnitType EntryType { get; set; }

    internal string Name { get; set; }

    internal string Symbol { get; set; }

    internal Uri PageUrl { get; set; }

    internal Uri HeaderUrl { get; set; }

    internal Uri DataUrl { get; set; }

    internal bool IsExternalSync { get; set; }

    internal string OutputDirectory { get; set; }

    internal string CustomFolderOutputBaseName { get; set; }

    internal bool IsRootFolder { get; set; }

    internal string CompatPrefix { get; set; }

    internal static PlaylistPropertyValues Capture(BMSTable table)
    {
        return new PlaylistPropertyValues
        {
            FolderOrder = [.. table.folder_list],
            PersistedFolderOrder = [.. table.Folder_order ?? []],
            EntriesRevision = table.PlaylistEntriesRevision,
            IsAutoFolderSort = table.Folder_order == null || !table.Folder_order.Any(),
            FolderSortKey = table.folder_sort_key,
            FolderSortAscending = table.folder_sort_ascending,
            IgnoreFolderOutput = table.ignore_folder_output,
            EntryType = table.entry_type,
            Name = table.name,
            Symbol = table.symbol,
            PageUrl = table.Page_url,
            HeaderUrl = table.Header_url,
            DataUrl = table.Data_url,
            IsExternalSync = table.is_external_sync,
            OutputDirectory = table.output_dir,
            CustomFolderOutputBaseName = CustomFolderOutputBaseRegistry.NormalizeBaseName(table.custom_folder_output_base_name),
            IsRootFolder = table.is_root_folder,
            CompatPrefix = table.compat_prefix
        };
    }

    internal static bool ContentEquals(
        PlaylistPropertyValues left,
        PlaylistPropertyValues right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left == null || right == null)
        {
            return false;
        }
        return (left.FolderOrder ?? []).SequenceEqual(
                right.FolderOrder ?? [],
                StringComparer.Ordinal)
            && (left.PersistedFolderOrder ?? []).SequenceEqual(
                right.PersistedFolderOrder ?? [],
                StringComparer.Ordinal)
            && left.EntriesRevision == right.EntriesRevision
            && left.IsAutoFolderSort == right.IsAutoFolderSort
            && left.FolderSortKey == right.FolderSortKey
            && left.FolderSortAscending == right.FolderSortAscending
            && left.IgnoreFolderOutput == right.IgnoreFolderOutput
            && left.EntryType == right.EntryType
            && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && string.Equals(left.Symbol, right.Symbol, StringComparison.Ordinal)
            && string.Equals(left.PageUrl?.ToString(), right.PageUrl?.ToString(), StringComparison.Ordinal)
            && string.Equals(left.HeaderUrl?.ToString(), right.HeaderUrl?.ToString(), StringComparison.Ordinal)
            && string.Equals(left.DataUrl?.ToString(), right.DataUrl?.ToString(), StringComparison.Ordinal)
            && left.IsExternalSync == right.IsExternalSync
            && string.Equals(left.OutputDirectory, right.OutputDirectory, StringComparison.Ordinal)
            && string.Equals(left.CustomFolderOutputBaseName, right.CustomFolderOutputBaseName, StringComparison.OrdinalIgnoreCase)
            && left.IsRootFolder == right.IsRootFolder
            && string.Equals(left.CompatPrefix, right.CompatPrefix, StringComparison.Ordinal);
    }

    internal PlaylistPropertyValues Clone()
    {
        return new PlaylistPropertyValues
        {
            FolderOrder = [.. FolderOrder ?? []],
            PersistedFolderOrder = [.. PersistedFolderOrder ?? []],
            EntriesRevision = EntriesRevision,
            IsAutoFolderSort = IsAutoFolderSort,
            FolderSortKey = FolderSortKey,
            FolderSortAscending = FolderSortAscending,
            IgnoreFolderOutput = IgnoreFolderOutput,
            EntryType = EntryType,
            Name = Name,
            Symbol = Symbol,
            PageUrl = PageUrl,
            HeaderUrl = HeaderUrl,
            DataUrl = DataUrl,
            IsExternalSync = IsExternalSync,
            OutputDirectory = OutputDirectory,
            CustomFolderOutputBaseName = CustomFolderOutputBaseName,
            IsRootFolder = IsRootFolder,
            CompatPrefix = CompatPrefix
        };
    }
}

internal sealed class PlaylistPropertyBaseline
{
    internal string OutputDirectoryPath { get; private set; }

    internal string OutputDirectory { get; private set; }

    internal bool IsRootFolder { get; private set; }

    internal bool IsExternalSync { get; private set; }

    internal string CompatPrefix { get; private set; }

    internal string CustomFolderOutputBaseName { get; private set; }

    internal string Name { get; private set; }

    internal string Symbol { get; private set; }

    internal Uri PageUrl { get; private set; }

    internal Uri HeaderUrl { get; private set; }

    internal IReadOnlyList<string> FolderOrder { get; private set; }

    internal static PlaylistPropertyBaseline Capture(BMSTable table, string outputDirectoryPath)
    {
        return new PlaylistPropertyBaseline
        {
            OutputDirectoryPath = outputDirectoryPath,
            OutputDirectory = table.output_dir,
            IsRootFolder = table.is_root_folder,
            IsExternalSync = table.is_external_sync,
            CompatPrefix = table.compat_prefix,
            CustomFolderOutputBaseName = CustomFolderOutputBaseRegistry.NormalizeBaseName(table.custom_folder_output_base_name),
            Name = table.name,
            Symbol = table.symbol,
            PageUrl = table.Page_url,
            HeaderUrl = table.Header_url,
            FolderOrder = [.. table.Folder_order ?? []]
        };
    }
}

internal sealed class PlaylistPropertySaveCommit
{
    internal PlaylistPropertySaveCommit(
        PlaylistPropertyBaseline baseline,
        BMSTable table,
        CustomFolderOutputSettingsSnapshot settings,
        PlaylistPropertyValues appliedValues, bool isNewDraft = false)
    {
        IsNewDraft = isNewDraft;
        Baseline = baseline;
        Table = table;
        Settings = settings;
        AppliedValues = appliedValues ?? throw new ArgumentNullException(nameof(appliedValues));
    }

    /// <summary>保存開始時に正本外だった新規draftです。</summary>
    internal bool IsNewDraft { get; }

    internal PlaylistPropertyBaseline Baseline { get; }

    internal BMSTable Table { get; set; }

    internal CustomFolderOutputSettingsSnapshot Settings { get; }

    internal PlaylistPropertyValues AppliedValues { get; set; }


}

internal enum PlaylistPropertyValidationError
{
    OutputDirectoryChangedByPlaylistName,
    InvalidOutputDirectory,
    InvalidPageUri,
    InvalidHeaderUri,
    InvalidDataUri,
    InvalidExternalSyncUris
}

internal sealed class PlaylistPropertyValidationErrorEventArgs : EventArgs
{
    internal PlaylistPropertyValidationErrorEventArgs(PlaylistPropertyValidationError error)
    {
        Error = error;
    }

    internal PlaylistPropertyValidationError Error { get; }
}

internal sealed class PlaylistPropertyExternalSyncConfirmationRequestedEventArgs : EventArgs
{
    internal PlaylistPropertyExternalSyncConfirmationRequestedEventArgs(bool enable)
    {
        Enable = enable;
    }

    internal bool Enable { get; }

    internal bool Confirmed { get; set; }
}

internal sealed class PlaylistPropertyFolderSelectionRemappedEventArgs : EventArgs
{
    internal PlaylistPropertyFolderSelectionRemappedEventArgs(
        BMSTable table,
        IReadOnlyDictionary<string, string> rewrittenFolders)
    {
        Table = table;
        RewrittenFolders = rewrittenFolders;
    }

    internal BMSTable Table { get; }

    internal IReadOnlyDictionary<string, string> RewrittenFolders { get; }
}

internal sealed class PlaylistPropertyExternalSyncFailedEventArgs : EventArgs
{
    internal PlaylistPropertyExternalSyncFailedEventArgs(BMSTable table, Uri uri, Exception exception)
    {
        Table = table;
        Uri = uri;
        Exception = exception;
    }

    internal BMSTable Table { get; }

    internal Uri Uri { get; }

    internal Exception Exception { get; }
}

internal sealed class PlaylistPropertyEntriesChangedEventArgs : EventArgs
{
    internal PlaylistPropertyEntriesChangedEventArgs(
        BMSTable table,
        bool refreshSummaryIfVisible = true)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
        RefreshSummaryIfVisible = refreshSummaryIfVisible;
    }

    internal BMSTable Table { get; }

    internal bool RefreshSummaryIfVisible { get; }
}
