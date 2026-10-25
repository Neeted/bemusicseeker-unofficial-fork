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

    private readonly Func<Lr2ConfigOwner> getLr2Config;

    private readonly Func<CustomFolderOutputSettingsSnapshot> getSettings;

    internal event EventHandler<PlaylistPropertyValidationErrorEventArgs> ValidationError;

    internal event EventHandler<PlaylistPropertyExternalSyncConfirmationRequestedEventArgs> ExternalSyncConfirmationRequested;

    internal event EventHandler InvalidOutputDirectoryRequested;

    internal event EventHandler PlaylistPropertySyncStarted;

    internal event EventHandler<PlaylistSyncProgressChangedEventArgs> PlaylistPropertySyncProgressChanged;

    internal event EventHandler PlaylistPropertySyncFinished;

    internal event EventHandler<PlaylistReferenceTableReplacedEventArgs> PlaylistPropertyReferenceTableReplaced;

    internal event EventHandler<PlaylistPropertyFolderSelectionRemappedEventArgs> PlaylistPropertyFolderSelectionRemapped;


    internal event EventHandler<PlaylistSyncResultReportedEventArgs> PlaylistPropertySyncResultReported;

    internal event EventHandler<PlaylistPropertyExternalSyncFailedEventArgs> PlaylistPropertyExternalSyncFailed;

    internal event EventHandler<PlaylistSummaryDataRefreshRequestedEventArgs> PlaylistPropertySummaryDataRefreshRequested;

    internal event EventHandler<PlaylistPropertyEntriesChangedEventArgs> PlaylistPropertyEntriesChanged;

    internal event EventHandler<PlaylistOperationNotificationPresentationRequestedEventArgs> PlaylistOperationNotificationPresentationRequested;

    internal PlaylistPropertySaveService(
        Func<BMSPlaylist> playlistStore,
        Func<BMSLibrary> library,
        Func<Lr2ConfigOwner> lr2Config,
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
        bool isNewTable)
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
            CustomFolderOutputSettingsSnapshot settings = getSettings()
                ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                if (settings.OperationModeLR2DB)
                {
                    try
                    {
                        ResolveCustomFolderOutputDirectory(table, settings);
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
                    PlaylistPropertyValues.Capture(table),
                    (store.BMSTables?.Cast<BMSTable>() ?? Enumerable.Empty<BMSTable>())
                        .Where(candidate => candidate != null && candidate != table && !string.IsNullOrWhiteSpace(candidate.Output_dir))
                        .Select(candidate => settings.OperationModeLR2DB ? ManagedPlaylistOutputPlacement.Resolve(candidate, settings) : candidate.Output_dir)
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
        string storedOutputDirectory,
        string baseName,
        bool isRoot)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }
        session.ThrowIfDisposed();
        if (!session.Settings.OperationModeLR2DB) { return true; }
        return IsPlacementValid(session.Settings, new PlaylistPropertyValues
        {
            Name = playlistName,
            OutputDirectory = storedOutputDirectory,
            CustomFolderOutputBaseName = baseName,
            IsRootFolder = isRoot
        }, session.OtherOutputDirectories);
    }

    /// <summary>入力不備を任意通知します。通知失敗を入力の承認や保存成功へ置き換えません。</summary>
    internal void NotifyValidationError(PlaylistPropertyValidationError error)
    {
        NotifyBestEffort(() => RaiseRequiredEvent(ValidationError,
            new PlaylistPropertyValidationErrorEventArgs(error), "Playlist property validation presentation"));
    }

    /// <summary>外部同期の変更承認を取得します。確認に失敗した場合は承認・取消しへ変換せず停止します。</summary>
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
        NotifyBestEffort(() => RaiseRequiredEvent(InvalidOutputDirectoryRequested,
            EventArgs.Empty, "Playlist property output-directory presentation"));
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

    /// <summary>局所受付後に現参照・最新保存値を検査し、未完了出力を先に終えてから属性保存と必要出力を行います。確定結果と後続失敗を返し、入力不備はnullです。</summary>
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
        return Task.Run(async () =>
        {
            using LibraryFileMutationLease accepted = session.Store.AcquirePlaylistMutationLease("playlist_property_save", capability: capability);
            using LibraryFileMutationCapability authority = accepted.CreateMutationCapability();
            ValidateCurrentSession(session);
            if (session.PendingOutput != null)
            {
                var continuation = new PlaylistPropertySaveCommit(null, session.Table, session.Settings, session.IsNewTable)
                {
                    SavedValues = session.OriginalValues,
                    PendingOutput = session.PendingOutput
                };
                List<Exception> continuationFailures = [];
                CompleteReferencePublication(continuation.PendingOutput, continuationFailures);
                await CompletePendingOutputsAsync(session.Store, continuation, authority, continuationFailures).ConfigureAwait(false);
                continuation.Exception = CombineFailures(continuationFailures.Distinct());
                if (continuation.Exception != null) { return continuation; }
                session.PendingOutput = null;
            }
            PlaylistPropertySaveCommit commit = TrySaveCore(session, values);
            if (commit == null) { return null; }
            try
            {
                if (!commit.IsNewDraft)
                {
                    try { session.Store.CommitPlaylistHeaderEdits(new Dictionary<BMSTable, PlaylistHeaderEdit> { [commit.Table] = commit.HeaderEdit }, authority); }
                    catch (PlaylistMutationPostCommitException failure) { commit.Exception = failure.InnerException ?? failure; }
                    commit.SavedValues = PlaylistPropertyValues.Capture(commit.Table);
                }
                await ApplyPostSaveUpdatesAsync(commit, authority).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                commit.Exception = failure;
            }
            // baselineは初回の属性適用だけに使う。明示継続へ古い保存全体を持ち越さない。
            commit.Baseline = null;
            commit.RequestedValuesSaved = commit.SavedValues != null;
            return commit;
        });
    }

    private void ValidateCurrentSession(PlaylistPropertyEditSession session)
    {
        session.Store.AcquireReaderLockBMSTables();
        try
        {
            using IDisposable tableRead = session.Table.ReaderWriterLock.GetReaderGuard();
            if (!ReferenceEquals(session.Store, GetPlaylistStore())
                || (!session.IsNewTable && !session.Store.ContainsBMSTable(session.Table))
                || !PlaylistPropertyValues.ContentEquals(session.OriginalValues, PlaylistPropertyValues.Capture(session.Table)))
            {
                throw new InvalidOperationException("Playlist properties changed after editing began. Reopen the dialog and apply the edit again.");
            }
        }
        finally { session.Store.FreeReaderLockBMSTables(); }
    }

    private PlaylistPropertySaveCommit TrySaveCore(
        PlaylistPropertyEditSession session,
        PlaylistPropertyValues values)
    {
        BMSTable table = session.Table;
        BMSPlaylist store = session.Store;
        CustomFolderOutputSettingsSnapshot settings = store.GetCustomFolderOutputSettings();
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
                store.ValidateCustomFolderOutputLayout(settings);
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
                var baseline = PlaylistPropertyBaseline.Capture(table, !session.IsNewTable && settings.OperationModeLR2DB
                    ? ResolveCustomFolderOutputDirectory(table, settings) : null, hasPreviousOutput: !session.IsNewTable);
                return new PlaylistPropertySaveCommit(
                    baseline,
                    table,
                    settings,
                    session.IsNewTable)
                {
                    HeaderEdit = CreateHeaderEdit(table, values, pageUrl, headerUrl, dataUrl),
                    PrefixFolderSelectionMap = table.CreateValidatedCompatibleFolderPrefixRewriteMap(table.compat_prefix, values.CompatPrefix)
                };
            }
        }
        finally
        {
            store.FreeWriterLockBMSTables();
        }
    }

    private static PlaylistHeaderEdit CreateHeaderEdit(BMSTable table, PlaylistPropertyValues values,
        Uri pageUrl, Uri headerUrl, Uri dataUrl)
    {
        PlaylistHeaderFields fields = PlaylistHeaderFields.None;
        void Changed(bool changed, PlaylistHeaderFields field) { if (changed) { fields |= field; } }
        string[] order = values.IsAutoFolderSort ? [] : [.. values.FolderOrder ?? []];
        Changed(!(table.Folder_order ?? []).SequenceEqual(order, StringComparer.Ordinal), PlaylistHeaderFields.FolderOrder);
        Changed(table.folder_sort_key != values.FolderSortKey, PlaylistHeaderFields.FolderSortKey);
        Changed(table.folder_sort_ascending != values.FolderSortAscending, PlaylistHeaderFields.FolderSortAscending);
        Changed(table.ignore_folder_output != values.IgnoreFolderOutput, PlaylistHeaderFields.IgnoreFolderOutput);
        Changed(table.entry_type != values.EntryType, PlaylistHeaderFields.EntryType);
        Changed(table.name != values.Name, PlaylistHeaderFields.Name);
        Changed(table.symbol != values.Symbol, PlaylistHeaderFields.Symbol);
        Changed(!HasSameUri(table.Page_url, pageUrl), PlaylistHeaderFields.PageUrl);
        Changed(!HasSameUri(table.Header_url, headerUrl), PlaylistHeaderFields.HeaderUrl);
        Changed(!HasSameUri(table.Data_url, dataUrl), PlaylistHeaderFields.DataUrl);
        Changed(table.is_external_sync != values.IsExternalSync, PlaylistHeaderFields.IsExternalSync);
        Changed(table.output_dir != values.OutputDirectory, PlaylistHeaderFields.OutputDirectory);
        Changed(!string.Equals(table.custom_folder_output_base_name, values.CustomFolderOutputBaseName, StringComparison.OrdinalIgnoreCase), PlaylistHeaderFields.CustomFolderOutputBaseName);
        Changed(table.is_root_folder != values.IsRootFolder, PlaylistHeaderFields.IsRootFolder);
        Changed(table.compat_prefix != values.CompatPrefix, PlaylistHeaderFields.CompatPrefix);
        return new PlaylistHeaderEdit
        {
            Fields = fields,
            FolderOrder = [.. order],
            FolderSortKey = values.FolderSortKey,
            FolderSortAscending = values.FolderSortAscending,
            IgnoreFolderOutput = values.IgnoreFolderOutput,
            EntryType = values.EntryType,
            Name = values.Name,
            Symbol = values.Symbol,
            PageUrl = pageUrl,
            HeaderUrl = headerUrl,
            DataUrl = dataUrl,
            IsExternalSync = values.IsExternalSync,
            OutputDirectory = values.OutputDirectory,
            CustomFolderOutputBaseName = values.CustomFolderOutputBaseName,
            IsRootFolder = values.IsRootFolder,
            CompatPrefix = values.CompatPrefix
        };
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

    /// <summary>受付時に捕捉した設定で保存済み事実に必要な出力・公開・通知を同じ局所権限で終えます。元失敗は確定を戻さず返します。</summary>
    /// <param name="capability">保存から継続する生存局所権限。外側が実終端まで保持します。</param>
    private async Task ApplyPostSaveUpdatesAsync(PlaylistPropertySaveCommit commit, LibraryFileMutationCapability capability = null)
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
            Exception registration = await store.RegisterDraftAsync(table, commit.HeaderEdit, authority).ConfigureAwait(false);
            commit.IsNewDraft = false;
            commit.SavedValues = PlaylistPropertyValues.Capture(table);
            if (registration != null) { commit.Exception = registration; }
        }
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = store.OperationNotificationOwner.BeginSession();
        List<Exception> failures = [];
        if (commit.Exception != null) { failures.Add(commit.Exception); }
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
        bool entryFolderProjectionChanged = commit.PrefixFolderSelectionMap?.Count > 0;
        IReadOnlyDictionary<string, string> prefixFolderSelectionMap = commit.PrefixFolderSelectionMap;

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
                PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt referenceReceipt = null;
                bool externalLastUpdateChanged = false;
                bool externalReloadFailed = false;
                Exception syncWorkflowFailure = null;
                try
                {
                    NotifyBestEffort(() => RaiseRequiredEvent(
                        PlaylistPropertySyncStarted,
                        EventArgs.Empty,
                        "Playlist property sync-start presentation"));
                    NotifyBestEffort(() => RaiseRequiredEvent(
                        PlaylistPropertySyncProgressChanged,
                        new PlaylistSyncProgressChangedEventArgs(new PlaylistSyncProgressSnapshot
                        {
                            IsActive = true,
                            TotalTableCount = 1,
                            CompletedTableCount = 0,
                            CurrentTableName = table.name,
                            CurrentUri = uri
                        }),
                        "Playlist property sync-progress presentation"));
                    try
                    {
                        store.EnsurePlaylistEntriesLoaded(table, "PlaylistPropertySaveService.ApplyPostSaveUpdatesAsync");
                        PlaylistExternalSyncOwner.PlaylistReloadOperationResult reload = await store.ExternalSyncOwner.ReloadAndApplySingleTableAsync(table, uri,
                            "PlaylistPropertySaveService.ApplyPostSaveUpdatesAsync", capability: authority, callerOwnsDerivedOutputs: true).ConfigureAwait(false);
                        PlaylistExternalSyncOwner.PlaylistReloadTargetResult target = reload.Single();
                        if (target.UpdateReceipt != null)
                        {
                            referenceReceipt = target.UpdateReceipt;
                            table = target.ResultTable;
                            commit.Table = table;
                            if (target.StatePersisted) { commit.SavedValues = PlaylistPropertyValues.Capture(table); }
                            externalLastUpdateChanged = target.Updated;
                            externalResyncApplied = true;
                        }
                        externalReloadFailed = target.UpdateReceipt == null;
                        // 一般同期では処理済み取得失敗を再送出しないが、単表保存は保存済み同期未完了として返す。
                        Exception syncFailure = target.IsHandledExternalLoadFailure
                            ? CombineFailures([target.Exception, reload.Exception])
                            : reload.Exception;
                        if (syncFailure != null)
                        {
                            failures.Add(syncFailure);
                            commit.ExternalSyncFailure = new PlaylistPropertyExternalSyncFailedEventArgs(table, uri, syncFailure);
                        }
                    }
                    catch (Exception ex)
                    {
                        externalReloadFailed = true;
                        failures.Add(ex);
                        commit.ExternalSyncFailure = new PlaylistPropertyExternalSyncFailedEventArgs(sourceTable, uri, ex);
                    }
                    if (!externalReloadFailed)
                    {
                        externalResyncApplied = true;
                        displayProjectionChanged = false;
                        commit.PendingOutput = new PlaylistPropertyPendingOutput
                        {
                            ReferenceReceipt = referenceReceipt,
                            NeedsReferencePresentation = true
                        };
                        NotifyBestEffort(() => RaiseRequiredEvent(
                            PlaylistPropertySyncResultReported,
                            new PlaylistSyncResultReportedEventArgs(PlaylistSyncAttemptResult.CreateSuccess(
                                sourceTable,
                                table,
                                uri,
                                externalLastUpdateChanged)),
                            "Playlist property sync-result presentation"));
                    }
                }
                catch (Exception ex)
                {
                    syncWorkflowFailure = ex;
                }
                if (syncWorkflowFailure != null) { failures.Add(syncWorkflowFailure); }
                CompletePlaylistPropertySyncPresentation(table, uri, notificationSession);
                CapturePresentationFailure(() => RaiseRequiredEvent(
                    PlaylistPropertySummaryDataRefreshRequested,
                    new PlaylistSummaryDataRefreshRequestedEventArgs(
                        "playlist_property_resync"),
                    "Playlist property summary refresh"), failures);
            }
        }

        commit.SavedValues = PlaylistPropertyValues.Capture(table);
        PreparePendingOutputs(commit);
        bool bmtProjectionChanged = displayProjectionChanged
            || prefixChanged
            || outputDirectoryChanged
            || externalResyncApplied
            || entryFolderProjectionChanged
            || baseline.IsExternalSync != table.is_external_sync
            || !HasSameUri(baseline.PageUrl, table.Page_url)
            || !HasSameUri(baseline.HeaderUrl, table.Header_url)
            || !HasSameStringSequence(baseline.FolderOrder, table.Folder_order);
        commit.PendingOutput.NeedsBmtExport = bmtProjectionChanged;
        // 参照を必要とする選択・項目通知より先に採用し、公開失敗はこの保存で再試行しない。
        CompleteReferencePublication(commit.PendingOutput, failures);
        if (externalResyncApplied)
        {
            CapturePresentationFailure(() => RaiseRequiredEvent(
                PlaylistPropertyFolderSelectionRemapped,
                new PlaylistPropertyFolderSelectionRemappedEventArgs(table, prefixFolderSelectionMap),
                "Playlist property folder-selection remap"), failures);
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

        if (outputBaseNameChanged || outputDirectoryChanged)
        {
            CapturePresentationFailure(() => RaiseRequiredEvent(
                PlaylistPropertySummaryDataRefreshRequested,
                new PlaylistSummaryDataRefreshRequestedEventArgs(
                    "playlist_property_output_changed"),
                "Playlist property summary refresh"), failures);
        }
        await CompletePendingOutputsAsync(store, commit, authority, failures, notificationSession).ConfigureAwait(false);
        if (failures.Count == 1) { ExceptionDispatchInfo.Capture(failures[0]).Throw(); }
        if (failures.Count > 1) { throw new AggregateException(failures).Flatten(); }
    }

    private void PreparePendingOutputs(PlaylistPropertySaveCommit commit)
    {
        PlaylistPropertyPendingOutput pending = commit.PendingOutput ??= new PlaylistPropertyPendingOutput();
        PlaylistPropertyBaseline baseline = commit.Baseline;
        CustomFolderOutputSettingsSnapshot settings = commit.Settings;
        if (!settings.OperationModeLR2DB) { return; }
        pending.Settings = settings;
        pending.HasPreviousOutput = baseline.HasPreviousOutput && !string.IsNullOrWhiteSpace(baseline.OutputDirectoryPath);
        pending.OldOutputDirectory = baseline.OutputDirectoryPath;
        pending.NewOutputDirectory = ResolveCustomFolderOutputDirectory(commit.Table, settings);
        pending.WasRootFolder = baseline.IsRootFolder;
        pending.IsRootFolder = commit.Table.is_root_folder;
        pending.OldOutputBaseResolved = baseline.IsRootFolder;
        pending.OldOutputBase = baseline.IsRootFolder ? settings.LR2CustomFolderOutputBaseDirRootType : null;
        if (!baseline.IsRootFolder)
        {
            pending.OldOutputBaseResolved = CustomFolderOutputBaseRegistry.TryResolveNormalOutputBaseDirectory(
                baseline.CustomFolderOutputBaseName, settings.LR2CustomFolderOutputBaseDir,
                settings.LR2CustomFolderAdditionalOutputBaseDirs, out string oldBase);
            pending.OldOutputBase = oldBase;
        }
        pending.NeedsLr2Output = true;
        pending.NeedsSearchPathSave = baseline.IsRootFolder != pending.IsRootFolder
            || (pending.IsRootFolder && !string.Equals(pending.OldOutputDirectory, pending.NewOutputDirectory, StringComparison.OrdinalIgnoreCase));
    }

    private void CompleteReferencePublication(PlaylistPropertyPendingOutput pending, ICollection<Exception> failures)
    {
        PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt receipt = pending.ReferenceReceipt;
        if (receipt == null) { return; }
        if (pending.NeedsReferencePresentation)
        {
            try
            {
                RaiseRequiredEvent(PlaylistPropertyReferenceTableReplaced,
                    new PlaylistReferenceTableReplacedEventArgs(receipt.OldTable, receipt.NewTable), "Playlist property reference-table replacement");
                pending.NeedsReferencePresentation = false;
            }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (!pending.NeedsReferencePresentation) { pending.ReferenceReceipt = null; }
    }

    private async Task CompletePendingOutputsAsync(BMSPlaylist store, PlaylistPropertySaveCommit commit,
        LibraryFileMutationCapability authority, ICollection<Exception> failures,
        PlaylistOperationNotificationOwner.OperationNotificationSession existingNotifications = null)
    {
        PlaylistPropertyPendingOutput pending = commit.PendingOutput;
        if (pending == null) { return; }
        bool hasLr2Continuation = pending.Settings?.OperationModeLR2DB == true;
        using PlaylistOperationNotificationOwner.OperationNotificationSession ownedNotifications = existingNotifications == null ? store.OperationNotificationOwner.BeginSession() : null;
        PlaylistOperationNotificationOwner.OperationNotificationSession notifications = existingNotifications ?? ownedNotifications;
        if (pending.NeedsLr2Output)
        {
            try
            {
                PlaylistCustomFolderOutputMaintenanceOwner.CustomFolderBatchOutputResult migration = pending.HasPreviousOutput && !string.IsNullOrWhiteSpace(pending.NewOutputDirectory)
                    ? store.MigrateCustomFolderOutputDirectoryWithSettings(commit.Table,
                    pending.OldOutputDirectory, pending.NewOutputDirectory, pending.WasRootFolder,
                    pending.WasRootFolder ? pending.OldOutputBase : null,
                    pending.OldOutputBaseResolved ? pending.OldOutputBase : null,
                    pending.OldOutputBaseResolved, pending.Settings, authority)
                    : store.ReOutputCustomFolderWithSettings(commit.Table, pending.Settings, authority);
                if (migration.PrimaryException != null) { failures.Add(migration.PrimaryException); }
                else if (!migration.Succeeded)
                {
                    failures.Add(new System.IO.IOException(migration.WarningMessage
                        ?? BeMusicSeeker.Properties.Resources.Msg_error_unexpected));
                }
                else { pending.NeedsLr2Output = false; }
            }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (!pending.NeedsLr2Output && pending.NeedsSearchPathSave)
        {
            try
            {
                Lr2ConfigOwner owner = getLr2Config() ?? throw new InvalidOperationException("LR2 configuration is not available.");
                owner.SaveRoots(new Lr2RootChange(pending.HasPreviousOutput && pending.WasRootFolder ? [pending.OldOutputDirectory] : [],
                    pending.IsRootFolder ? [pending.NewOutputDirectory] : []));
                pending.NeedsSearchPathSave = false;
            }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (!pending.NeedsLr2Output && !pending.NeedsSearchPathSave)
        {
            pending.Settings = null;
            pending.OldOutputDirectory = null;
            pending.NewOutputDirectory = null;
            pending.OldOutputBase = null;
        }
        if (pending.NeedsBmtExport)
        {
            try
            {
                await store.BmtOutput.ExportTablesAsync([commit.Table], "playlist_property_save", authority).ConfigureAwait(false);
                pending.NeedsBmtExport = false;
            }
            catch (Exception failure) { failures.Add(failure); }
        }
        PlaylistOperationNotificationOwner.OperationNotificationReceipt notificationReceipt = notifications.TakeReceipt();
        if (hasLr2Continuation || !notificationReceipt.IsEmpty)
        {
            NotifyBestEffort(() => RaiseRequiredEvent(PlaylistOperationNotificationPresentationRequested,
                new PlaylistOperationNotificationPresentationRequestedEventArgs(notificationReceipt,
                    "playlist property custom folder notification"), "Playlist property notification flushing"));
        }
        if (!pending.HasPendingWork) { commit.PendingOutput = null; }
    }

    /// <summary>確定参照と保存値の採用後に任意の失敗通知を試み、今回の実処理失敗だけを呼出元へ返します。</summary>
    internal void PublishSaveFailure(PlaylistPropertySaveCommit commit)
    {
        if (commit.ExternalSyncFailure != null)
        {
            NotifyBestEffort(() => RaiseRequiredEvent(PlaylistPropertyExternalSyncFailed,
                commit.ExternalSyncFailure, "Playlist property sync-failure presentation"));
        }
        if (commit.Exception != null) { ExceptionDispatchInfo.Capture(commit.Exception).Throw(); }
    }

    private static Exception CombineFailures(IEnumerable<Exception> failures)
    {
        Exception[] actual = [.. failures.Where(failure => failure != null)];
        return actual.Length switch { 0 => null, 1 => actual[0], _ => new AggregateException(actual).Flatten() };
    }

    private void CompletePlaylistPropertySyncPresentation(
        BMSTable table,
        Uri uri,
        PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession)
    {
        NotifyBestEffort(() => RaiseRequiredEvent(
            PlaylistPropertySyncProgressChanged,
            new PlaylistSyncProgressChangedEventArgs(new PlaylistSyncProgressSnapshot
            {
                IsActive = true,
                TotalTableCount = 1,
                CompletedTableCount = 1,
                CurrentTableName = table.name,
                CurrentUri = uri
            }), "Playlist property sync-progress presentation"));
        NotifyBestEffort(() => RaiseRequiredEvent(PlaylistPropertySyncFinished,
            EventArgs.Empty, "Playlist property sync-finish presentation"));
        NotifyBestEffort(() => RaiseRequiredEvent(PlaylistOperationNotificationPresentationRequested,
            new PlaylistOperationNotificationPresentationRequestedEventArgs(notificationSession.TakeReceipt(),
                "playlist property external sync notification"), "Playlist property notification flushing"));
    }

    private static void NotifyBestEffort(Action notification)
    {
        try { notification(); }
        catch (Exception failure)
        {
            try { Ribbit.Logging.NLogWrapper.FileLogger?.Warn(failure, "playlist_property_notification_failed"); }
            catch { /* 診断失敗で保存結果を変えず、再通知もしない。 */ }
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
                .Where(candidate => candidate != null && candidate != table && !string.IsNullOrWhiteSpace(candidate.Output_dir))
                .Select(candidate => settings.OperationModeLR2DB ? ManagedPlaylistOutputPlacement.Resolve(candidate, settings) : candidate.Output_dir));
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
            if (!IsPlacementValid(settings, values, otherOutputDirectories)) { return false; }
            NormalizeFolderSettings(values);
        }
        return true;
    }

    private static bool IsPlacementValid(CustomFolderOutputSettingsSnapshot settings, PlaylistPropertyValues values,
        IEnumerable<string> otherOutputDirectories)
    {
        if (string.IsNullOrWhiteSpace(BMSTable.ResolveOutputDirectoryName(values.Name, values.OutputDirectory))) { return false; }
        string candidate = ManagedPlaylistOutputPlacement.Resolve(values.Name, values.OutputDirectory,
            values.CustomFolderOutputBaseName, values.IsRootFolder, settings);
        return ManagedPlaylistOutputPlacement.IsValid((otherOutputDirectories ?? []).Append(candidate));
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

    /// <summary>開いた時点の正本と属性を固定し、後続保存結果だけで競合基準を更新する編集寿命を作ります。</summary>
    internal PlaylistPropertyEditSession(
        BMSPlaylist store,
        BMSTable table,
        bool isNewTable,
        CustomFolderOutputSettingsSnapshot settings,
        PlaylistPropertyValues values,
        IReadOnlyList<string> otherOutputDirectories)
    {
        Store = store;
        Table = table;
        IsNewTable = isNewTable;
        Settings = settings;
        OriginalValues = values;
        Values = values.Clone();
        OtherOutputDirectories = otherOutputDirectories ?? [];
    }

    internal BMSPlaylist Store { get; }

    /// <summary>最後にDB確定した保存結果が示す、競合判定対象の現行参照です。</summary>
    internal BMSTable Table { get; private set; }

    /// <summary>作成経路で始まったdraftがまだ実登録されていない場合だけtrueです。</summary>
    internal bool IsNewTable { get; private set; }

    internal CustomFolderOutputSettingsSnapshot Settings { get; }

    /// <summary>DB確定結果から画面へ渡す編集用コピーです。</summary>
    internal PlaylistPropertyValues Values { get; private set; }

    /// <summary>最新のDB確定保存値と項目revisionを持つ競合基準です。</summary>
    internal PlaylistPropertyValues OriginalValues { get; private set; }

    /// <summary>編集セッション内で一つだけ保持する、既にDB確定した変更の未完了出力です。</summary>
    internal PlaylistPropertyPendingOutput PendingOutput { get; set; }

    /// <summary>DB確定の証拠を持つ保存結果だけから競合基準と参照を進めます。</summary>
    internal void Adopt(PlaylistPropertySaveCommit commit)
    {
        if (commit.SavedValues != null)
        {
            Table = commit.Table;
            IsNewTable = commit.IsNewDraft;
            OriginalValues = commit.SavedValues;
            Values = commit.SavedValues.Clone();
        }
        PendingOutput = commit.PendingOutput;
    }

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
        PendingOutput = null;
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
    /// <summary>登録前に捕捉した既存表の事実です。新規draftの初回出力には旧先整理を適用しません。</summary>
    internal bool HasPreviousOutput { get; private set; }

    /// <summary>既存表の旧出力先です。未登録draftではnullとし、空名由来の共通基点を旧配置として扱いません。</summary>
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

    /// <summary>属性保存前の旧値と、登録前の作成経路から確定した旧配置の有無を捕捉します。</summary>
    internal static PlaylistPropertyBaseline Capture(BMSTable table, string outputDirectoryPath, bool hasPreviousOutput)
    {
        return new PlaylistPropertyBaseline
        {
            HasPreviousOutput = hasPreviousOutput,
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

/// <summary>保存から必要出力までの確定参照、最新保存値、未完了と元失敗を一方向に返します。</summary>
internal sealed class PlaylistPropertySaveCommit
{
    /// <summary>初回の保存文脈を作ります。DB確定後の採用情報は実処理が設定し、結果返却時に旧baselineを解放します。</summary>
    internal PlaylistPropertySaveCommit(PlaylistPropertyBaseline baseline, BMSTable table,
        CustomFolderOutputSettingsSnapshot settings, bool isNewDraft = false)
    {
        IsNewDraft = isNewDraft;
        Baseline = baseline;
        Table = table;
        Settings = settings;
    }

    /// <summary>実登録前の作成経路。登録確定後はfalseに進め、集合不在から新規へ戻しません。</summary>
    internal bool IsNewDraft { get; set; }
    /// <summary>初回属性保存だけの旧値。結果を返す前に解放し、継続には使いません。</summary>
    internal PlaylistPropertyBaseline Baseline { get; set; }
    /// <summary>実際に採用された保存対象。置換後失敗でも呼出元へ返します。</summary>
    internal BMSTable Table { get; set; }
    /// <summary>この保存で捕捉した適用済み出力配置。</summary>
    internal CustomFolderOutputSettingsSnapshot Settings { get; }
    /// <summary>実DB確定後の競合基準。属性適用だけでは設定しません。</summary>
    internal PlaylistPropertyValues SavedValues { get; set; }
    /// <summary>この要求が所有する未保存header属性です。entry候補は保存writer内で作ります。</summary>
    internal PlaylistHeaderEdit HeaderEdit { get; set; }
    /// <summary>確定したprefix変更に対応する選択名の写像です。全体詳細の値はentry receiptで配送します。</summary>
    internal IReadOnlyDictionary<string, string> PrefixFolderSelectionMap { get; set; }
    /// <summary>次の明示保存で属性適用より先に終える一つの未完了出力。</summary>
    internal PlaylistPropertyPendingOutput PendingOutput { get; set; }
    /// <summary>この保存または継続の元失敗。確定した値を取り消す意味を持ちません。</summary>
    internal Exception Exception { get; set; }
    /// <summary>確定結果の採用後に既存の取得失敗通知へ渡す入力。</summary>
    internal PlaylistPropertyExternalSyncFailedEventArgs ExternalSyncFailure { get; set; }
    /// <summary>今回入力した属性のDB保存まで進んだか。未完了出力だけで停止したときはfalseです。</summary>
    internal bool RequestedValuesSaved { get; set; }
}

/// <summary>一つの確定済み変更に残った出力だけを保持します。成功済み部分と公開済みsnapshotは解放します。</summary>
internal sealed class PlaylistPropertyPendingOutput
{
    /// <summary>未完了のLR2初回出力または移行で使う捕捉済み配置。LR2完了後は解放します。</summary>
    internal CustomFolderOutputSettingsSnapshot Settings { get; set; }
    /// <summary>登録前に旧配置が存在した表だけtrueです。初回出力を移行の旧先整理へ渡しません。</summary>
    internal bool HasPreviousOutput { get; set; }
    /// <summary>旧先の捕捉済み絶対パス。現在値から推測し直しません。</summary>
    internal string OldOutputDirectory { get; set; }
    /// <summary>DB確定済み変更に対応する新先の絶対パス。</summary>
    internal string NewOutputDirectory { get; set; }
    /// <summary>旧出力の管理範囲を限定する捕捉済み基点。</summary>
    internal string OldOutputBase { get; set; }
    /// <summary>旧通常先またはルート基点を捕捉できたか。</summary>
    internal bool OldOutputBaseResolved { get; set; }
    /// <summary>旧先でのルート型。</summary>
    internal bool WasRootFolder { get; set; }
    /// <summary>確定済み新先でのルート型。</summary>
    internal bool IsRootFolder { get; set; }
    /// <summary>ファイル・LR2行・状態と、既存配置がある場合の旧先整理が未完了か。</summary>
    internal bool NeedsLr2Output { get; set; }
    /// <summary>検索パスのディスク保存が未完了か。</summary>
    internal bool NeedsSearchPathSave { get; set; }
    /// <summary>必要BMT出力のTaskが未成功か。</summary>
    internal bool NeedsBmtExport { get; set; }
    /// <summary>未完了の参照公開に必要な旧新事実。公開成功後はsnapshotごと解放します。</summary>
    internal PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt ReferenceReceipt { get; set; }
    /// <summary>ライブラリ参照索引の公開が未完了か。</summary>
    /// <summary>現行画面参照の公開が未完了か。</summary>
    internal bool NeedsReferencePresentation { get; set; }
    /// <summary>次の保存が先に処理する必要出力または置換公開が残るか。</summary>
    internal bool HasPendingWork => NeedsLr2Output || NeedsSearchPathSave || NeedsBmtExport || ReferenceReceipt != null;
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
