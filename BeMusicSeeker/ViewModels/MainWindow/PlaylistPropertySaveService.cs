using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

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

    internal event EventHandler<PlaylistPropertyNotificationsFlushRequestedEventArgs> PlaylistPropertyNotificationsFlushRequested;

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

    internal PlaylistPropertyEditSession BeginEdit(BMSTable table, bool isNewTable = false)
    {
        if (table == null)
        {
            throw new ArgumentNullException(nameof(table));
        }
        BMSPlaylist store = GetPlaylistStore();
        CustomFolderOutputSettingsSnapshot settings = getSettings()
            ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");
        store.AcquireWriterLockBMSTables();
        try
        {
            store.EnsurePlaylistEntriesLoaded(table, "PlaylistPropertySaveService.BeginEdit");
            string outputDirectoryPath = null;
            if (settings.OperationModeLR2DB)
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
                PlaylistPropertyBaseline.Capture(table, outputDirectoryPath),
                PlaylistPropertyValues.Capture(table));
        }
        catch
        {
            store.FreeWriterLockBMSTables();
            throw;
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
        if (!IsValid(session.Table, session.Store, session.Settings, values))
        {
            return false;
        }
        if (string.Equals(session.Table.compat_prefix, values.CompatPrefix, StringComparison.Ordinal))
        {
            return true;
        }
        session.Store.EnsurePlaylistEntriesLoaded(
            session.Table,
            "PlaylistPropertySaveService.ValidateCompatibleFolderPrefixRewrite");
        using (session.Table.ReaderWriterLock.GetReaderGuard())
        {
            return session.Table.CanRewriteCompatibleFolderPrefix(
                session.Table.compat_prefix,
                values.CompatPrefix);
        }
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
            && !(session.Store.BMSTables?.Cast<BMSTable>() ?? Enumerable.Empty<BMSTable>())
                .Where(candidate => candidate != null && candidate != session.Table)
                .Select(candidate => candidate.Output_dir)
                .Contains(effectiveName, StringComparer.OrdinalIgnoreCase);
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

    internal bool TrySave(
        PlaylistPropertyEditSession session,
        PlaylistPropertyValues values,
        out PlaylistPropertySaveCommit commit)
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
        BMSTable table = session.Table;
        BMSPlaylist store = session.Store;
        if (!IsValid(session, values))
        {
            commit = null;
            return false;
        }

        table.Folder_order = values.IsAutoFolderSort
            ? []
            : [.. values.FolderOrder ?? []];
        table.folder_sort_key = values.FolderSortKey;
        table.folder_sort_ascending = values.FolderSortAscending;
        table.ignore_folder_output = values.IgnoreFolderOutput;
        table.entry_type = values.EntryType;
        table.name = values.Name;
        table.symbol = values.Symbol;
        table.Page_url = NormalizeUriTextForStandardStorage(values.PageUrl);
        table.Header_url = NormalizeUriTextForStandardStorage(values.HeaderUrl);
        table.Data_url = NormalizeUriTextForStandardStorage(values.DataUrl);
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
        commit = new PlaylistPropertySaveCommit(session.Baseline, table, session.Settings);
        return true;
    }

    internal PlaylistPropertyValues Reset(PlaylistPropertyEditSession session)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }
        session.ThrowIfDisposed();
        if (session.IsNewTable)
        {
            session.Store.RemoveBMSTable(session.Table);
        }
        return PlaylistPropertyValues.Capture(session.Table);
    }

    internal async Task ApplyPostSaveUpdatesAsync(PlaylistPropertySaveCommit commit)
    {
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }
        BMSPlaylist store = GetPlaylistStore();
        PlaylistPropertyBaseline baseline = commit.Baseline;
        CustomFolderOutputSettingsSnapshot settings = commit.Settings;
        BMSTable table = commit.Table;
        using PlaylistOperationNotificationOwner.OperationNotificationScope notificationScope = store.OperationNotificationOwner.BeginScope();
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
        IReadOnlyDictionary<string, string> prefixFolderSelectionMap = null;
        if (prefixChanged)
        {
            store.EnsurePlaylistEntriesLoaded(table, "PlaylistPropertySaveService.CreateCompatibleFolderPrefixRewriteMap");
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                prefixFolderSelectionMap = table.CreateValidatedCompatibleFolderPrefixRewriteMap(
                    baseline.CompatPrefix,
                    table.compat_prefix);
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
                DateTime lastUpdate = table.last_update;
                BMSTable sourceTable = table;
                RaiseRequiredEvent(
                    PlaylistPropertySyncStarted,
                    EventArgs.Empty,
                    "Playlist property sync-start presentation");
                try
                {
                    RaiseRequiredEvent(
                        PlaylistPropertySyncProgressChanged,
                        new PlaylistSyncProgressChangedEventArgs(new PlaylistSyncProgressSnapshot
                        {
                            IsActive = true,
                            TotalTableCount = 1,
                            CompletedTableCount = 0,
                            CurrentTableName = table.name,
                            CurrentUri = uri
                        }),
                        "Playlist property sync-progress presentation");
                    List<BMSTableEntry> oldEntries;
                    store.EnsurePlaylistEntriesLoaded(table, "PlaylistPropertySaveService.ApplyPostSaveUpdatesAsync");
                    using (table.ReaderWriterLock.GetReaderGuard())
                    {
                        oldEntries = [.. table.entries];
                    }
                    table = await store.ExternalSyncOwner.ReloadAndApplySingleTableAsync(table, uri, "PlaylistPropertySaveService.ApplyPostSaveUpdatesAsync");
                    commit.Table = table;
                    GetLibrary().ReplaceReferenceBMSTable(sourceTable, table, oldEntries);
                    RaiseRequiredEvent(
                        PlaylistPropertyReferenceTableReplaced,
                        new PlaylistReferenceTableReplacedEventArgs(
                            sourceTable,
                            table),
                        "Playlist property reference-table replacement");
                    RaiseRequiredEvent(
                        PlaylistPropertyFolderSelectionRemapped,
                        new PlaylistPropertyFolderSelectionRemappedEventArgs(table, prefixFolderSelectionMap),
                        "Playlist property folder-selection remap");
                    RaiseRequiredEvent(
                        PlaylistPropertyReferenceSortInvalidationRequested,
                        EventArgs.Empty,
                        "Playlist property reference-sort invalidation");
                    RaiseRequiredEvent(
                        PlaylistPropertySyncResultReported,
                        new PlaylistSyncResultReportedEventArgs(PlaylistSyncAttemptResult.CreateSuccess(
                            sourceTable,
                            table,
                            uri,
                            table.last_update != lastUpdate)),
                        "Playlist property sync-result presentation");
                    externalResyncApplied = true;
                    displayProjectionChanged = false;
                }
                catch (Exception ex)
                {
                    RaiseRequiredEvent(
                        PlaylistPropertyExternalSyncFailed,
                        new PlaylistPropertyExternalSyncFailedEventArgs(sourceTable, uri, ex),
                        "Playlist property sync-failure presentation");
                }
                finally
                {
                    RaiseRequiredEvent(
                        PlaylistPropertySyncProgressChanged,
                        new PlaylistSyncProgressChangedEventArgs(new PlaylistSyncProgressSnapshot
                        {
                            IsActive = true,
                            TotalTableCount = 1,
                            CompletedTableCount = 1,
                            CurrentTableName = table.name,
                            CurrentUri = uri
                        }),
                        "Playlist property sync-progress presentation");
                    RaiseRequiredEvent(
                        PlaylistPropertySyncFinished,
                        EventArgs.Empty,
                        "Playlist property sync-finish presentation");
                    RaiseRequiredEvent(
                        PlaylistPropertyNotificationsFlushRequested,
                        new PlaylistPropertyNotificationsFlushRequestedEventArgs(
                            notificationScope,
                            "playlist property external sync notification"),
                        "Playlist property notification flushing");
                }
                RaiseRequiredEvent(
                    PlaylistPropertySummaryDataRefreshRequested,
                    new PlaylistSummaryDataRefreshRequestedEventArgs(
                        "playlist_property_resync"),
                    "Playlist property summary refresh");
            }
        }

        if (prefixChanged && !externalResyncApplied)
        {
            store.EnsurePlaylistEntriesLoaded(table, "PlaylistPropertySaveService.RewriteCompatibleFolderPrefix");
            IReadOnlyDictionary<string, string> rewrittenFolders;
            using (table.ReaderWriterLock.GetWriterGuard())
            {
                entryFolderProjectionChanged = table.RewriteCompatibleFolderPrefix(
                    baseline.CompatPrefix,
                    table.compat_prefix,
                    out rewrittenFolders);
            }
            if (entryFolderProjectionChanged)
            {
                RaiseRequiredEvent(
                    PlaylistPropertyFolderSelectionRemapped,
                    new PlaylistPropertyFolderSelectionRemappedEventArgs(table, rewrittenFolders),
                    "Playlist property folder-selection remap");
                displayProjectionChanged = true;
            }
        }

        if (displayProjectionChanged)
        {
            GetLibrary().RefreshReferenceDisplayForTable(table);
            RaiseRequiredEvent(
                PlaylistPropertyReferenceSortInvalidationRequested,
                EventArgs.Empty,
                "Playlist property reference-sort invalidation");
            if (entryFolderProjectionChanged)
            {
                RaiseRequiredEvent(
                    PlaylistPropertyEntriesChanged,
                    new PlaylistPropertyEntriesChangedEventArgs(table),
                    "Playlist property entries-changed publication");
            }
            else
            {
                RaiseRequiredEvent(
                    PlaylistPropertySummaryDataRefreshRequested,
                    new PlaylistSummaryDataRefreshRequestedEventArgs(
                        "playlist_property_changed"),
                    "Playlist property summary refresh");
            }
        }
        else if (externalResyncApplied)
        {
            RaiseRequiredEvent(
                PlaylistPropertyEntriesChanged,
                new PlaylistPropertyEntriesChangedEventArgs(table),
                "Playlist property entries-changed publication");
        }

        if (entryFolderProjectionChanged)
        {
            store.CommitBMSTableWithEntriesToDB(table);
        }
        else
        {
            store.CommitBMSTableHeaderToDB(table);
        }

        if (settings.OperationModeLR2DB)
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
                    settings: settings);
            }
            finally
            {
                RaiseRequiredEvent(
                    PlaylistPropertyNotificationsFlushRequested,
                    new PlaylistPropertyNotificationsFlushRequestedEventArgs(
                        notificationScope,
                        "playlist property custom folder notification"),
                    "Playlist property notification flushing");
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

        if (outputBaseNameChanged || outputDirectoryChanged)
        {
            RaiseRequiredEvent(
                PlaylistPropertySummaryDataRefreshRequested,
                new PlaylistSummaryDataRefreshRequestedEventArgs(
                    "playlist_property_output_changed"),
                "Playlist property summary refresh");
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
            store.BmtOutput.QueueBeatorajaBmtExportForTable(table, "PlaylistPropertySaveService.ApplyPostSaveUpdatesAsync");
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
                || (store.BMSTables?.Cast<BMSTable>() ?? Enumerable.Empty<BMSTable>())
                    .Where(candidate => candidate != null && candidate != table)
                    .Select(candidate => candidate.Output_dir)
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
        PlaylistPropertyValues values)
    {
        Store = store;
        Table = table;
        IsNewTable = isNewTable;
        Settings = settings;
        Baseline = baseline;
        Values = values;
    }

    internal BMSPlaylist Store { get; }

    internal BMSTable Table { get; }

    internal bool IsNewTable { get; }

    internal CustomFolderOutputSettingsSnapshot Settings { get; }

    internal PlaylistPropertyBaseline Baseline { get; }

    internal PlaylistPropertyValues Values { get; }

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
        Store.FreeWriterLockBMSTables();
    }
}

internal sealed class PlaylistPropertyValues
{
    internal IReadOnlyList<string> FolderOrder { get; set; }

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
        CustomFolderOutputSettingsSnapshot settings)
    {
        Baseline = baseline;
        Table = table;
        Settings = settings;
    }

    internal PlaylistPropertyBaseline Baseline { get; }

    internal BMSTable Table { get; set; }

    internal CustomFolderOutputSettingsSnapshot Settings { get; }
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

internal sealed class PlaylistPropertyNotificationsFlushRequestedEventArgs : EventArgs
{
    internal PlaylistPropertyNotificationsFlushRequestedEventArgs(
        PlaylistOperationNotificationOwner.OperationNotificationScope scope,
        string routeName)
    {
        Scope = scope;
        RouteName = routeName;
    }

    internal PlaylistOperationNotificationOwner.OperationNotificationScope Scope { get; }

    internal string RouteName { get; }
}
