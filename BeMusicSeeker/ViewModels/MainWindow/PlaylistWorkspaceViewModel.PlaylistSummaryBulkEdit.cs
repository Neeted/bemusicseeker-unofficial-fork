using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Livet;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{

    private readonly Func<LR2Config> getLr2Config;
    private readonly Action<string> summaryBulkWarningLog;
    private PlaylistSummaryBulkEditDialogViewModel activeSummaryBulkEditDialog;
    internal event EventHandler<PlaylistSummaryBulkInvalidOutputDirectoryEventArgs> PlaylistSummaryBulkInvalidOutputDirectoryRequested;

    internal PlaylistSummaryBulkEditDialogViewModel ActiveSummaryBulkEditDialog => activeSummaryBulkEditDialog;
    internal bool IsCustomFolderOutputEnabled => GetCustomFolderOutputSettings().OperationModeLR2DB;

    internal PlaylistSummaryBulkEditDialogViewModel OpenSummaryBulkEditDialog(IEnumerable<PlaylistSummaryRow> rows)
    {
        if (!CanOpenPlaylistEditDialog || !ContainsActivePlaylistSummaryRows(rows))
        {
            return null;
        }
        activeSummaryBulkEditDialog = new PlaylistSummaryBulkEditDialogViewModel(this, rows);
        return activeSummaryBulkEditDialog;
    }

    internal void CloseSummaryBulkEditDialog(PlaylistSummaryBulkEditDialogViewModel dialog)
    {
        if (ReferenceEquals(activeSummaryBulkEditDialog, dialog))
        {
            activeSummaryBulkEditDialog = null;
        }
    }

    internal IReadOnlyList<PlaylistCustomFolderOutputBaseOption> CreatePlaylistCustomFolderOutputBaseOptionsForCurrentSettings(
        bool includeNoChange = false)
    {
        CustomFolderOutputSettingsSnapshot settings = GetCustomFolderOutputSettings();
        return PlaylistCustomFolderOutputBaseOptions.Create(
            settings.LR2CustomFolderOutputBaseDir,
            CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(settings.LR2CustomFolderAdditionalOutputBaseDirs),
            includeNoChange);
    }

    private CustomFolderOutputSettingsSnapshot GetCustomFolderOutputSettings()
    {
        return customFolderOutputSettingsProvider()
            ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");
    }

    private LR2Config GetLr2Config()
    {
        return getLr2Config();
    }

    private void RunPlaylistSummaryBulkOperation(Action operation, string routeName)
    {
        if (operation == null)
        {
            return;
        }
        BMSPlaylist tables = GetPlaylistStore();
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = tables.OperationNotificationOwner.BeginSession();
        try
        {
            RunPlaylistSummaryBulkOperationWithinSession(operation);
        }
        finally
        {
            PublishPlaylistOperationNotificationReceipt(notificationSession, routeName);
        }
    }

    private void RunPlaylistSummaryBulkOperationWithinSession(Action operation)
    {
        if (operation == null)
        {
            return;
        }
        BeginPlaylistSyncProgressOperation();
        bool progressEnded = false;
        try
        {
            try
            {
                operation();
            }
            finally
            {
                progressEnded = true;
                EndPlaylistSyncProgressOperation();
            }
        }
        finally
        {
            if (!progressEnded)
            {
                progressEnded = true;
                EndPlaylistSyncProgressOperation();
            }
        }
    }

    private void RequestPlaylistSummaryRefresh(
        string reason,
        bool rebuildAsync = true)
    {
        PublishPlaylistCatalogChanged();
        RequestPlaylistSummaryDataRefresh(
            reason,
            rebuildAsync);
    }

    private void LogPlaylistSummaryBulkWarning(string message)
    {
        summaryBulkWarningLog(message ?? string.Empty);
    }

    private string ResolvePlaylistSummaryCustomFolderOutputDirectory(
        BMSTable bmsTable,
        string routeName,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        try
        {
            settings ??= GetCustomFolderOutputSettings();
            return BMSPlaylist.GetCustomFolderOutputDirectory(
                bmsTable,
                settings.LR2CustomFolderOutputBaseDir,
                settings.LR2CustomFolderOutputBaseDirRootType,
                settings.LR2CustomFolderAdditionalOutputBaseDirs);
        }
        catch (ArgumentNullException)
        {
            RaiseRequiredEvent(
                PlaylistSummaryBulkInvalidOutputDirectoryRequested,
                new PlaylistSummaryBulkInvalidOutputDirectoryEventArgs(routeName),
                nameof(PlaylistSummaryBulkInvalidOutputDirectoryRequested));
            throw;
        }
    }

    public sealed class PlaylistSummaryBulkBooleanOption
    {
        public PlaylistSummaryBulkBooleanOption(string label, bool? value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }

        public bool? Value { get; }
    }

    public sealed class PlaylistSummaryCustomFolderOutputPatch
    {
        public bool? AllSongsFolder { get; set; }

        public bool? UserFolder { get; set; }

        public bool? LevelFolder { get; set; }

        public bool? AlphabetFolder { get; set; }

        public bool? ClearFolder { get; set; }

        public bool? DJLevelFolder { get; set; }

        public bool? CategoryAllFolder { get; set; }

        public bool? OtherFolder { get; set; }

        public bool? RandomFolder { get; set; }

        public bool? BpmSortFolder { get; set; }

        public bool? BpSortFolder { get; set; }

        public bool? PlayCountSortFolder { get; set; }

        public bool? LastPlaySortFolder { get; set; }

        internal IEnumerable<KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>> Enumerate()
        {
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder, AllSongsFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.UserFolder, UserFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.LevelFolder, LevelFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder, AlphabetFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.ClearFolder, ClearFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder, DJLevelFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder, CategoryAllFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.OtherFolder, OtherFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.RandomFolder, RandomFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder, BpmSortFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder, BpSortFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder, PlayCountSortFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder, LastPlaySortFolder);
        }
    }

    /// <summary>
    /// Selects which playlist header fields should be initialized from external playlist metadata.
    /// </summary>
    public sealed class PlaylistSummaryExternalPropertyInitializationOptions
    {
        public bool Name { get; set; }

        public bool Symbol { get; set; }

        public bool CompatPrefix { get; set; }

        public bool OutputDirectory { get; set; }

        public bool HasAnySelection => Name || Symbol || CompatPrefix || OutputDirectory;
    }

    /// <summary>
    /// Holds edit state for applying one operation to multiple playlist summary rows.
    /// </summary>
    public partial class PlaylistSummaryBulkEditDialogViewModel : ViewModel
    {
        private readonly PlaylistWorkspaceViewModel ownerWorkspace;

        private readonly IReadOnlyList<PlaylistSummaryRow> targetRows;

        private readonly IReadOnlyList<BMSTable> targetTables;

        private bool? _outputAllSongsFolder;

        private bool? _outputUserFolder;

        private bool? _outputLevelFolder;

        private bool? _outputAlphabetFolder;

        private bool? _outputClearFolder;

        private bool? _outputDJLevelFolder;

        private bool? _outputCategoryAllFolder;

        private bool? _outputOtherFolder;

        private bool? _outputRandomFolder;

        private bool? _outputBpmSortFolder;

        private bool? _outputBpSortFolder;

        private bool? _outputPlayCountSortFolder;

        private bool? _outputLastPlaySortFolder;

        private PlaylistSummaryBulkBooleanOption _rootFolderOption;

        private PlaylistSummaryBulkBooleanOption _externalSyncOption;

        private PlaylistSummaryBulkBooleanOption _bmtOutputOption;

        private PlaylistCustomFolderOutputBaseOption _outputBaseOption;

        private bool _initializeNameFromExternal;

        private bool _initializeSymbolFromExternal;

        private bool _initializeCompatPrefixFromExternal;

        private bool _initializeOutputDirectoryFromExternal;

        public PlaylistSummaryBulkEditDialogViewModel(PlaylistWorkspaceViewModel owner, IEnumerable<PlaylistSummaryRow> rows)
        {
            ownerWorkspace = owner ?? throw new ArgumentNullException(nameof(owner));
            targetRows = rows?
                .Where(row => row?.TableRef != null)
                .GroupBy(row => row.TableRef)
                .Select(group => group.First())
                .ToList()
                ?? [];
            targetTables = [.. targetRows.Select(row => row.TableRef).Distinct()];
            NoChangeOption = new PlaylistSummaryBulkBooleanOption(BeMusicSeeker.Properties.Resources.Playlist_summary_bulk_no_change, null);
            OnOption = new PlaylistSummaryBulkBooleanOption(BeMusicSeeker.Properties.Resources.Playlist_summary_bulk_on, true);
            OffOption = new PlaylistSummaryBulkBooleanOption(BeMusicSeeker.Properties.Resources.Playlist_summary_bulk_off, false);
            BulkBooleanOptions = [NoChangeOption, OnOption, OffOption];
            _rootFolderOption = NoChangeOption;
            _externalSyncOption = NoChangeOption;
            _bmtOutputOption = NoChangeOption;
            OutputBaseOptions = ownerWorkspace.CreatePlaylistCustomFolderOutputBaseOptionsForCurrentSettings(includeNoChange: true);
            _outputBaseOption = OutputBaseOptions.FirstOrDefault(option => option.IsNoChange) ?? OutputBaseOptions.FirstOrDefault();
            ReloadCustomFolderOutputStates();
        }

        internal PlaylistWorkspaceViewModel OwnerWorkspace => ownerWorkspace;

        public bool IsCustomFolderOutputEnabled => ownerWorkspace.IsCustomFolderOutputEnabled;

        public IReadOnlyList<PlaylistSummaryBulkBooleanOption> BulkBooleanOptions { get; }

        public PlaylistSummaryBulkBooleanOption NoChangeOption { get; }

        public PlaylistSummaryBulkBooleanOption OnOption { get; }

        public PlaylistSummaryBulkBooleanOption OffOption { get; }

        public IReadOnlyList<PlaylistCustomFolderOutputBaseOption> OutputBaseOptions { get; }

        public int TargetCount => targetTables.Count;

        public bool IsLevelFolderBulkApplicable => targetTables.Any(table => table.entry_type != LR2SongDBExtended.playlist.EntryUnitType.Folder);

        public bool? OutputAllSongsFolder
        {
            get => _outputAllSongsFolder;
            set => SetCustomFolderState(ref _outputAllSongsFolder, value, nameof(OutputAllSongsFolder));
        }

        public bool? OutputUserFolder
        {
            get => _outputUserFolder;
            set => SetCustomFolderState(ref _outputUserFolder, value, nameof(OutputUserFolder));
        }

        public bool? OutputLevelFolder
        {
            get => _outputLevelFolder;
            set => SetCustomFolderState(ref _outputLevelFolder, value, nameof(OutputLevelFolder));
        }

        public bool? OutputAlphabetFolder
        {
            get => _outputAlphabetFolder;
            set => SetCustomFolderState(ref _outputAlphabetFolder, value, nameof(OutputAlphabetFolder));
        }

        public bool? OutputClearFolder
        {
            get => _outputClearFolder;
            set => SetCustomFolderState(ref _outputClearFolder, value, nameof(OutputClearFolder));
        }

        public bool? OutputDJLevelFolder
        {
            get => _outputDJLevelFolder;
            set => SetCustomFolderState(ref _outputDJLevelFolder, value, nameof(OutputDJLevelFolder));
        }

        public bool? OutputCategoryAllFolder
        {
            get => _outputCategoryAllFolder;
            set => SetCustomFolderState(ref _outputCategoryAllFolder, value, nameof(OutputCategoryAllFolder));
        }

        public bool? OutputOtherFolder
        {
            get => _outputOtherFolder;
            set => SetCustomFolderState(ref _outputOtherFolder, value, nameof(OutputOtherFolder));
        }

        public bool? OutputRandomFolder
        {
            get => _outputRandomFolder;
            set => SetCustomFolderState(ref _outputRandomFolder, value, nameof(OutputRandomFolder));
        }

        public bool? OutputBpmSortFolder
        {
            get => _outputBpmSortFolder;
            set => SetCustomFolderState(ref _outputBpmSortFolder, value, nameof(OutputBpmSortFolder));
        }

        public bool? OutputBpSortFolder
        {
            get => _outputBpSortFolder;
            set => SetCustomFolderState(ref _outputBpSortFolder, value, nameof(OutputBpSortFolder));
        }

        public bool? OutputPlayCountSortFolder
        {
            get => _outputPlayCountSortFolder;
            set => SetCustomFolderState(ref _outputPlayCountSortFolder, value, nameof(OutputPlayCountSortFolder));
        }

        public bool? OutputLastPlaySortFolder
        {
            get => _outputLastPlaySortFolder;
            set => SetCustomFolderState(ref _outputLastPlaySortFolder, value, nameof(OutputLastPlaySortFolder));
        }

        public bool CanApplyCustomFolderOutput => BuildCustomFolderOutputPatch().Enumerate().Any(item => item.Value.HasValue);

        public PlaylistSummaryBulkBooleanOption RootFolderOption
        {
            get => _rootFolderOption;
            set
            {
                if (_rootFolderOption != value)
                {
                    _rootFolderOption = value ?? NoChangeOption;
                    RaisePropertyChanged(nameof(RootFolderOption));
                    RaisePropertyChanged(nameof(CanApplyRootFolder));
                }
            }
        }

        public bool CanApplyRootFolder => RootFolderOption?.Value.HasValue == true;

        public PlaylistSummaryBulkBooleanOption ExternalSyncOption
        {
            get => _externalSyncOption;
            set
            {
                if (_externalSyncOption != value)
                {
                    _externalSyncOption = value ?? NoChangeOption;
                    RaisePropertyChanged(nameof(ExternalSyncOption));
                    RaisePropertyChanged(nameof(CanApplyExternalSync));
                }
            }
        }

        public bool CanApplyExternalSync => ExternalSyncOption?.Value.HasValue == true;

        public PlaylistSummaryBulkBooleanOption BmtOutputOption
        {
            get => _bmtOutputOption;
            set
            {
                if (_bmtOutputOption != value)
                {
                    _bmtOutputOption = value ?? NoChangeOption;
                    RaisePropertyChanged(nameof(BmtOutputOption));
                    RaisePropertyChanged(nameof(CanApplyBmtOutput));
                }
            }
        }

        public bool CanApplyBmtOutput => BmtOutputOption?.Value.HasValue == true;

        public PlaylistCustomFolderOutputBaseOption OutputBaseOption
        {
            get => _outputBaseOption;
            set
            {
                if (_outputBaseOption != value)
                {
                    _outputBaseOption = value ?? OutputBaseOptions.FirstOrDefault(option => option.IsNoChange);
                    RaisePropertyChanged(nameof(OutputBaseOption));
                    RaisePropertyChanged(nameof(CanApplyOutputBase));
                }
            }
        }

        public bool CanApplyOutputBase => OutputBaseOption != null && !OutputBaseOption.IsNoChange;

        public bool InitializeNameFromExternal
        {
            get => _initializeNameFromExternal;
            set => SetExternalInitializationState(ref _initializeNameFromExternal, value, nameof(InitializeNameFromExternal));
        }

        public bool InitializeSymbolFromExternal
        {
            get => _initializeSymbolFromExternal;
            set => SetExternalInitializationState(ref _initializeSymbolFromExternal, value, nameof(InitializeSymbolFromExternal));
        }

        public bool InitializeCompatPrefixFromExternal
        {
            get => _initializeCompatPrefixFromExternal;
            set => SetExternalInitializationState(ref _initializeCompatPrefixFromExternal, value, nameof(InitializeCompatPrefixFromExternal));
        }

        public bool InitializeOutputDirectoryFromExternal
        {
            get => _initializeOutputDirectoryFromExternal;
            set => SetExternalInitializationState(ref _initializeOutputDirectoryFromExternal, value, nameof(InitializeOutputDirectoryFromExternal));
        }

        public bool CanApplyExternalPropertyInitialization => BuildExternalPropertyInitializationOptions().HasAnySelection;

        public void ReloadCustomFolderOutputStates()
        {
            OutputAllSongsFolder = ResolvePlaylistSummaryCustomFolderOutputState(targetTables, LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder);
            OutputUserFolder = ResolvePlaylistSummaryCustomFolderOutputState(targetTables, LR2SongDBExtended.playlist.CustomFolderType.UserFolder);
            OutputLevelFolder = ResolvePlaylistSummaryCustomFolderOutputState(targetTables, LR2SongDBExtended.playlist.CustomFolderType.LevelFolder);
            OutputAlphabetFolder = ResolvePlaylistSummaryCustomFolderOutputState(targetTables, LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder);
            OutputClearFolder = ResolvePlaylistSummaryCustomFolderOutputState(targetTables, LR2SongDBExtended.playlist.CustomFolderType.ClearFolder);
            OutputDJLevelFolder = ResolvePlaylistSummaryCustomFolderOutputState(targetTables, LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder);
            OutputCategoryAllFolder = ResolvePlaylistSummaryCustomFolderOutputState(targetTables, LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder);
            OutputOtherFolder = ResolvePlaylistSummaryCustomFolderOutputState(targetTables, LR2SongDBExtended.playlist.CustomFolderType.OtherFolder);
            OutputRandomFolder = ResolvePlaylistSummaryCustomFolderOutputState(targetTables, LR2SongDBExtended.playlist.CustomFolderType.RandomFolder);
            OutputBpmSortFolder = ResolvePlaylistSummaryCustomFolderOutputState(targetTables, LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder);
            OutputBpSortFolder = ResolvePlaylistSummaryCustomFolderOutputState(targetTables, LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder);
            OutputPlayCountSortFolder = ResolvePlaylistSummaryCustomFolderOutputState(targetTables, LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder);
            OutputLastPlaySortFolder = ResolvePlaylistSummaryCustomFolderOutputState(targetTables, LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder);
            RaisePropertyChanged(nameof(IsLevelFolderBulkApplicable));
        }

        public void ApplyCustomFolderOutputTypes()
        {
            ownerWorkspace.ApplyPlaylistSummaryCustomFolderOutputTypes(targetRows, BuildCustomFolderOutputPatch());
        }

        public void ApplyRootFolder()
        {
            if (RootFolderOption?.Value is bool value)
            {
                ownerWorkspace.ApplyPlaylistSummaryFlags(targetRows, isRootFolder: value);
            }
        }

        public void ResetRootFolderOption()
        {
            RootFolderOption = NoChangeOption;
        }

        public void ApplyExternalSync()
        {
            if (ExternalSyncOption?.Value is bool value)
            {
                ownerWorkspace.ApplyPlaylistSummaryExternalSync(targetRows, value);
            }
        }

        public void ResetExternalSyncOption()
        {
            ExternalSyncOption = NoChangeOption;
        }

        public void ApplyBmtOutput()
        {
            if (BmtOutputOption?.Value is bool value)
            {
                ownerWorkspace.ApplyPlaylistSummaryBmtOutput(targetRows, value);
            }
        }

        public void ResetBmtOutputOption()
        {
            BmtOutputOption = NoChangeOption;
        }

        public void ApplyOutputBase()
        {
            if (OutputBaseOption != null && !OutputBaseOption.IsNoChange)
            {
                ownerWorkspace.ApplyPlaylistSummaryOutputBase(targetRows, OutputBaseOption.BaseName);
            }
        }

        public void ResetOutputBaseOption()
        {
            OutputBaseOption = OutputBaseOptions.FirstOrDefault(option => option.IsNoChange) ?? OutputBaseOptions.FirstOrDefault();
        }

        public Task ApplyExternalPropertyInitializationAsync()
        {
            return ownerWorkspace.ApplyPlaylistSummaryExternalPropertyInitializationAsync(targetRows, BuildExternalPropertyInitializationOptions());
        }

        public void ResetExternalPropertyInitializationOptions()
        {
            InitializeNameFromExternal = false;
            InitializeSymbolFromExternal = false;
            InitializeCompatPrefixFromExternal = false;
            InitializeOutputDirectoryFromExternal = false;
        }

        private PlaylistSummaryCustomFolderOutputPatch BuildCustomFolderOutputPatch()
        {
            return new PlaylistSummaryCustomFolderOutputPatch
            {
                AllSongsFolder = OutputAllSongsFolder,
                UserFolder = OutputUserFolder,
                LevelFolder = OutputLevelFolder,
                AlphabetFolder = OutputAlphabetFolder,
                ClearFolder = OutputClearFolder,
                DJLevelFolder = OutputDJLevelFolder,
                CategoryAllFolder = OutputCategoryAllFolder,
                OtherFolder = OutputOtherFolder,
                RandomFolder = OutputRandomFolder,
                BpmSortFolder = OutputBpmSortFolder,
                BpSortFolder = OutputBpSortFolder,
                PlayCountSortFolder = OutputPlayCountSortFolder,
                LastPlaySortFolder = OutputLastPlaySortFolder
            };
        }

        private void SetCustomFolderState(ref bool? storage, bool? value, string propertyName)
        {
            if (storage != value)
            {
                storage = value;
                RaisePropertyChanged(propertyName);
                RaisePropertyChanged(nameof(CanApplyCustomFolderOutput));
            }
        }

        private PlaylistSummaryExternalPropertyInitializationOptions BuildExternalPropertyInitializationOptions()
        {
            return new PlaylistSummaryExternalPropertyInitializationOptions
            {
                Name = InitializeNameFromExternal,
                Symbol = InitializeSymbolFromExternal,
                CompatPrefix = InitializeCompatPrefixFromExternal,
                OutputDirectory = InitializeOutputDirectoryFromExternal
            };
        }

        private void SetExternalInitializationState(ref bool storage, bool value, string propertyName)
        {
            if (storage != value)
            {
                storage = value;
                RaisePropertyChanged(propertyName);
                RaisePropertyChanged(nameof(CanApplyExternalPropertyInitialization));
            }
        }
    }

    internal static bool IsPlaylistSummaryCustomFolderOutputEffectiveEnabled(BMSTable table, LR2SongDBExtended.playlist.CustomFolderType type)
    {
        if (table == null)
        {
            return false;
        }
        if (type == LR2SongDBExtended.playlist.CustomFolderType.LevelFolder
            && table.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder)
        {
            return false;
        }
        return LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(table.ignore_folder_output, type);
    }

    internal static bool? ResolvePlaylistSummaryCustomFolderOutputState(IEnumerable<BMSTable> tables, LR2SongDBExtended.playlist.CustomFolderType type)
    {
        if (tables == null)
        {
            return null;
        }
        bool? state = null;
        bool hasValue = false;
        foreach (BMSTable table in tables.Where(table => table != null))
        {
            bool enabled = IsPlaylistSummaryCustomFolderOutputEffectiveEnabled(table, type);
            if (!hasValue)
            {
                state = enabled;
                hasValue = true;
            }
            else if (state != enabled)
            {
                return null;
            }
        }
        return hasValue ? state : null;
    }

    internal static LR2SongDBExtended.playlist.CustomFolderType ApplyPlaylistSummaryCustomFolderOutputPatchToMask(BMSTable table, LR2SongDBExtended.playlist.CustomFolderType currentMask, PlaylistSummaryCustomFolderOutputPatch patch)
    {
        if (table == null)
        {
            throw new ArgumentNullException(nameof(table));
        }
        if (patch == null)
        {
            throw new ArgumentNullException(nameof(patch));
        }
        LR2SongDBExtended.playlist.CustomFolderType mask = currentMask;
        foreach (KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?> item in patch.Enumerate())
        {
            if (!item.Value.HasValue)
            {
                continue;
            }
            if (item.Key == LR2SongDBExtended.playlist.CustomFolderType.LevelFolder
                && table.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder)
            {
                mask |= LR2SongDBExtended.playlist.CustomFolderType.LevelFolder;
                continue;
            }
            if (item.Value.Value)
            {
                mask &= ~item.Key;
            }
            else
            {
                mask |= item.Key;
            }
        }
        if (table.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder)
        {
            mask |= LR2SongDBExtended.playlist.CustomFolderType.LevelFolder;
        }
        return mask;
    }

    private sealed class PlaylistSummaryExternalPropertyInitializationChange
    {
        public BMSTable Table { get; set; }

        public BMSTable ExternalTable { get; set; }

        public string OriginalName { get; set; }

        public string OriginalSymbol { get; set; }

        public string OriginalCompatPrefix { get; set; }

        public string OriginalOutputDir { get; set; }

        public string DesiredName { get; set; }

        public string DesiredSymbol { get; set; }

        public string DesiredCompatPrefix { get; set; }

        public string DesiredOutputDir { get; set; }

        public bool RequiresCompatiblePrefixRewrite => !string.Equals(OriginalCompatPrefix ?? string.Empty, DesiredCompatPrefix ?? string.Empty, StringComparison.Ordinal);

        public bool HeaderChanged =>
            !string.Equals(OriginalName ?? string.Empty, DesiredName ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(OriginalSymbol ?? string.Empty, DesiredSymbol ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(OriginalCompatPrefix ?? string.Empty, DesiredCompatPrefix ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(BMSTable.NormalizeOutputDirectoryName(OriginalOutputDir) ?? string.Empty, BMSTable.NormalizeOutputDirectoryName(DesiredOutputDir) ?? string.Empty, StringComparison.Ordinal);

        public bool BmtProjectionChanged =>
            !string.Equals(OriginalName ?? string.Empty, DesiredName ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(OriginalSymbol ?? string.Empty, DesiredSymbol ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(OriginalCompatPrefix ?? string.Empty, DesiredCompatPrefix ?? string.Empty, StringComparison.Ordinal);

        public bool CustomFolderHeaderProjectionChanged =>
            !string.Equals(OriginalName ?? string.Empty, DesiredName ?? string.Empty, StringComparison.Ordinal);

        public string EffectiveOutputDirBefore => BMSTable.ResolveOutputDirectoryName(OriginalName, OriginalOutputDir);

        public string EffectiveOutputDirAfter => BMSTable.ResolveOutputDirectoryName(DesiredName, DesiredOutputDir);
    }

    internal async Task ApplyPlaylistSummaryExternalPropertyInitializationAsync(IEnumerable<PlaylistSummaryRow> rows, PlaylistSummaryExternalPropertyInitializationOptions options, CancellationToken cancellationToken = default)
    {
        if (rows == null || options?.HasAnySelection != true)
        {
            return;
        }
        BMSPlaylist tables = GetPlaylistStore();
        BMSLibrary library = getPlaylistLibrary();
        List<BMSTable> targetTables = [.. rows
            .Where(row => row?.TableRef != null)
            .Select(row => row.TableRef)
            .Distinct()];
        if (targetTables.Count == 0)
        {
            return;
        }
        CustomFolderOutputSettingsSnapshot settings = customFolderOutputSettingsProvider()
            ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");

        const string reason = "playlist_summary_external_property_initialization";
        bool summaryRefreshRequired = false;
        bool summaryRefreshAttempted = false;
        bool playlistTablesReaderLockHeld = false;
        bool referenceDisplayRefreshRequired = false;
        bool referenceSortInvalidationPublishedByEntryChange = false;
        bool playlistKeywordValueCandidatesChanged = false;
        using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = tables.OperationNotificationOwner.BeginSession();
        BeginPlaylistSyncProgressOperation();
        try
        {
            UpdatePlaylistSummaryExternalPropertyInitializationProgress(0, targetTables.Count, string.Empty);
            List<PlaylistExternalSyncOwner.PlaylistExternalTableLoadResult> loadResults = await tables.ExternalSyncOwner.LoadExternalTableSnapshotsAsync(
                targetTables,
                inheritLocalTableProperties: false,
                UpdatePlaylistSummaryExternalPropertyInitializationLoadProgress,
                reason,
                cancellationToken).ConfigureAwait(false);
            UpdatePlaylistSummaryExternalPropertyInitializationProgress(0, loadResults.Count, string.Empty);

            List<PlaylistSummaryExternalPropertyInitializationChange> pendingChanges = [];
            int inspectedLoadResultCount = 0;
            foreach (PlaylistExternalSyncOwner.PlaylistExternalTableLoadResult result in loadResults)
            {
                inspectedLoadResultCount++;
                BMSTable table = result?.SourceTable;
                BMSTable externalTable = result?.ExternalTable;
                UpdatePlaylistSummaryExternalPropertyInitializationProgress(inspectedLoadResultCount, loadResults.Count, table?.name ?? string.Empty);
                if (result?.Succeeded != true || table == null || externalTable == null)
                {
                    continue;
                }
                if (options.Name && string.IsNullOrWhiteSpace(externalTable.name))
                {
                    LogPlaylistSummaryBulkWarning("playlist_summary_external_property_initialization_skipped reason=blank_external_name table=" + (table.name ?? string.Empty) + " uri=" + result.Uri);
                    continue;
                }

                string originalName = table.name ?? string.Empty;
                string originalSymbol = table.symbol ?? string.Empty;
                string originalCompatPrefix = table.compat_prefix ?? string.Empty;
                string originalOutputDir = table.output_dir;
                string desiredName = options.Name ? externalTable.name : table.name;
                string desiredSymbol = options.Symbol ? (externalTable.symbol ?? string.Empty) : table.symbol;
                string desiredCompatPrefix = options.CompatPrefix ? (externalTable.compat_prefix ?? string.Empty) : table.compat_prefix;
                string desiredOutputDir = options.OutputDirectory ? null : table.output_dir;

                var change = new PlaylistSummaryExternalPropertyInitializationChange
                {
                    Table = table,
                    ExternalTable = externalTable,
                    OriginalName = originalName,
                    OriginalSymbol = originalSymbol,
                    OriginalCompatPrefix = originalCompatPrefix,
                    OriginalOutputDir = originalOutputDir,
                    DesiredName = desiredName ?? string.Empty,
                    DesiredSymbol = desiredSymbol ?? string.Empty,
                    DesiredCompatPrefix = desiredCompatPrefix ?? string.Empty,
                    DesiredOutputDir = desiredOutputDir
                };
                if (!change.HeaderChanged
                    && string.Equals(change.EffectiveOutputDirBefore ?? string.Empty, change.EffectiveOutputDirAfter ?? string.Empty, StringComparison.Ordinal))
                {
                    continue;
                }
                if (change.RequiresCompatiblePrefixRewrite)
                {
                    tables.EnsurePlaylistEntriesLoaded(table, "ApplyPlaylistSummaryExternalPropertyInitialization.ValidateCompatiblePrefix");
                    using (table.ReaderWriterLock.GetReaderGuard())
                    {
                        if (!table.CanRewriteCompatibleFolderPrefix(change.OriginalCompatPrefix, change.DesiredCompatPrefix))
                        {
                            LogPlaylistSummaryBulkWarning("playlist_summary_external_property_initialization_skipped reason=compatible_prefix_collision table=" + (table.name ?? string.Empty) + " uri=" + result.Uri);
                            continue;
                        }
                    }
                }
                pendingChanges.Add(change);
            }
            if (pendingChanges.Count == 0)
            {
                return;
            }

            tables.AcquireReaderLockBMSTables();
            playlistTablesReaderLockHeld = true;
            List<PlaylistSummaryExternalPropertyInitializationChange> acceptedChanges =
                FilterPlaylistSummaryExternalPropertyInitializationOutputConflicts(pendingChanges, settings);
            if (acceptedChanges.Count == 0)
            {
                return;
            }
            acceptedChanges = [.. acceptedChanges.Where(change => tables.ContainsBMSTable(change.Table))];
            if (acceptedChanges.Count == 0)
            {
                return;
            }
            playlistKeywordValueCandidatesChanged = acceptedChanges.Any(change =>
                !string.Equals(change.OriginalName, change.DesiredName, StringComparison.Ordinal));
            summaryRefreshRequired = true;

            var outputDirPathBeforeByTable = new Dictionary<BMSTable, string>();
            var outputBaseDirPathBeforeByTable = new Dictionary<BMSTable, string>();
            var wasRootFolderBeforeByTable = new Dictionary<BMSTable, bool>();
            var entryChangedTables = new List<BMSTable>();
            var outputChangedTables = new List<BMSTable>();
            var headerOnlyTables = new List<BMSTable>();
            var sameOutputReOutputTables = new List<BMSTable>();
            var bmtProjectionTables = new List<BMSTable>();
            for (int changeIndex = 0; changeIndex < acceptedChanges.Count; changeIndex++)
            {
                PlaylistSummaryExternalPropertyInitializationChange change = acceptedChanges[changeIndex];
                BMSTable table = change.Table;
                UpdatePlaylistSummaryExternalPropertyInitializationProgress(changeIndex + 1, acceptedChanges.Count, table?.name ?? string.Empty);
                string beforeEffectiveOutputDir = change.EffectiveOutputDirBefore;
                string afterEffectiveOutputDir = change.EffectiveOutputDirAfter;
                bool outputDirChanged = !string.Equals(beforeEffectiveOutputDir ?? string.Empty, afterEffectiveOutputDir ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                if (outputDirChanged)
                {
                    CapturePlaylistSummaryOutputDirectoryBefore(
                        table,
                        beforeEffectiveOutputDir,
                        outputDirPathBeforeByTable,
                        outputBaseDirPathBeforeByTable,
                        wasRootFolderBeforeByTable,
                        settings);
                }

                bool entryFolderProjectionChanged = false;
                IReadOnlyDictionary<string, string> rewrittenFolders = null;
                if (change.RequiresCompatiblePrefixRewrite)
                {
                    tables.EnsurePlaylistEntriesLoaded(table, "ApplyPlaylistSummaryExternalPropertyInitialization.RewriteCompatiblePrefix");
                    using (table.ReaderWriterLock.GetWriterGuard())
                    {
                        table.compat_prefix = change.DesiredCompatPrefix;
                        entryFolderProjectionChanged = table.RewriteCompatibleFolderPrefix(
                            change.OriginalCompatPrefix,
                            change.DesiredCompatPrefix,
                            out rewrittenFolders);
                    }
                    if (entryFolderProjectionChanged)
                    {
                        RemapCurrentPlaylistDetailFolderSelection(table, rewrittenFolders);
                    }
                }

                table.name = change.DesiredName;
                table.symbol = change.DesiredSymbol;
                if (!change.RequiresCompatiblePrefixRewrite)
                {
                    table.compat_prefix = change.DesiredCompatPrefix;
                }
                if (options.OutputDirectory)
                {
                    table.Output_dir = BMSTable.CreateDefaultOutputDirectoryName(table.name);
                }
                if (library != null && change.BmtProjectionChanged)
                {
                    library.RefreshReferenceDisplayForTable(table);
                    referenceDisplayRefreshRequired = true;
                }

                if (entryFolderProjectionChanged)
                {
                    entryChangedTables.Add(table);
                }
                if (outputDirChanged)
                {
                    outputChangedTables.Add(table);
                }
                if (!entryFolderProjectionChanged && !outputDirChanged)
                {
                    headerOnlyTables.Add(table);
                }
                if (!outputDirChanged && (entryFolderProjectionChanged || change.CustomFolderHeaderProjectionChanged))
                {
                    sameOutputReOutputTables.Add(table);
                }
                if (change.BmtProjectionChanged || entryFolderProjectionChanged)
                {
                    bmtProjectionTables.Add(table);
                }
            }

            List<BMSTable> entryChangedTableList = [.. entryChangedTables.Distinct()];
            if (entryChangedTableList.Count > 0)
            {
                UpdatePlaylistSummaryExternalPropertyInitializationProgress(0, entryChangedTableList.Count, string.Empty);
                tables.CommitBMSTablesWithEntriesToDB(
                    entryChangedTableList,
                    UpdatePlaylistSummaryExternalPropertyInitializationProgress);
                foreach (BMSTable table in entryChangedTableList)
                {
                    PublishEntriesChanged(table, refreshSummaryIfVisible: false);
                }
                referenceSortInvalidationPublishedByEntryChange = true;
            }
            if (outputChangedTables.Count > 0)
            {
                RunPlaylistSummaryBulkOperationWithinSession(
                    () =>
                    {
                        UpdatePlaylistSummaryCustomFolderOutputProgress(0, outputChangedTables.Count, string.Empty);
                        tables.MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
                            outputChangedTables,
                            outputDirPathBeforeByTable,
                            reason,
                            UpdatePlaylistSummaryCustomFolderOutputProgress,
                            wasRootFolderBeforeByTable,
                            outputBaseDirPathBeforeByTable: outputBaseDirPathBeforeByTable,
                            settings: settings);
                    });
                UpdatePlaylistSummaryRootOutputDirectoriesAfterExternalInitialization(
                    outputChangedTables,
                    outputDirPathBeforeByTable,
                    settings);
            }
            List<BMSTable> sameOutputReOutputTableList = [.. sameOutputReOutputTables.Distinct()];
            bool sameOutputReOutputCommitted = sameOutputReOutputTableList.Count > 0 && settings.OperationModeLR2DB;
            if (sameOutputReOutputCommitted)
            {
                RunPlaylistSummaryBulkOperationWithinSession(
                    () =>
                    {
                        UpdatePlaylistSummaryCustomFolderOutputProgress(0, sameOutputReOutputTableList.Count, string.Empty);
                        tables.ReOutputCustomFoldersAndCommitHeadersToDB(
                            sameOutputReOutputTableList,
                            reason,
                            UpdatePlaylistSummaryCustomFolderOutputProgress,
                            settings);
                    });
            }
            List<BMSTable> headerOnlyCommitTables = [.. (sameOutputReOutputCommitted
                ? headerOnlyTables.Except(sameOutputReOutputTables)
                : headerOnlyTables).Distinct()];
            if (headerOnlyCommitTables.Count > 0)
            {
                UpdatePlaylistSummaryExternalPropertyInitializationProgress(0, headerOnlyCommitTables.Count, string.Empty);
                tables.CommitBMSTableHeadersToDB(headerOnlyCommitTables);
                UpdatePlaylistSummaryExternalPropertyInitializationProgress(headerOnlyCommitTables.Count, headerOnlyCommitTables.Count, string.Empty);
            }
            if (bmtProjectionTables.Count > 0)
            {
                UpdatePlaylistSummaryExternalPropertyInitializationProgress(0, bmtProjectionTables.Distinct().Count(), string.Empty);
                tables.BmtOutput.QueueBeatorajaBmtExportForTables(bmtProjectionTables.Distinct(), reason);
            }
            UpdatePlaylistSummaryExternalPropertyInitializationProgress(0, 0, string.Empty);
            summaryRefreshAttempted = true;
            RequestPlaylistSummaryRefresh(reason);
        }
        finally
        {
            try
            {
                if (summaryRefreshRequired && !summaryRefreshAttempted)
                {
                    summaryRefreshAttempted = true;
                    RequestPlaylistSummaryRefresh(reason);
                }
            }
            finally
            {
                if (playlistTablesReaderLockHeld)
                {
                    tables.FreeReaderLockBMSTables();
                    playlistTablesReaderLockHeld = false;
                }
                if (playlistKeywordValueCandidatesChanged)
                {
                    DispatchPlaylistKeywordValueCandidatesChanged();
                }
                if (referenceDisplayRefreshRequired && !referenceSortInvalidationPublishedByEntryChange)
                {
                    RequestPlaylistReferenceSortInvalidation();
                }
                EndPlaylistSyncProgressOperation();
                PublishPlaylistOperationNotificationReceipt(
                    notificationSession,
                    "playlist summary external property initialization notification");
            }
        }
    }

    private void UpdatePlaylistSummaryExternalPropertyInitializationLoadProgress(PlaylistSyncProgressSnapshot snapshot)
    {
        if (snapshot?.IsActive == true)
        {
            ReportPlaylistSyncProgress(snapshot);
        }
    }

    private void UpdatePlaylistSummaryExternalPropertyInitializationProgress(int completedTableCount, int totalTableCount, string currentTableName)
    {
        ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            TotalTableCount = totalTableCount,
            CompletedTableCount = completedTableCount,
            CurrentTableName = currentTableName ?? string.Empty,
            LabelFormat = BeMusicSeeker.Properties.Resources.Playlist_summary_bulk_external_property_initialization + " {0}/{1}",
            SingleLabel = BeMusicSeeker.Properties.Resources.Playlist_summary_bulk_external_property_initialization
        });
    }

    private List<PlaylistSummaryExternalPropertyInitializationChange> FilterPlaylistSummaryExternalPropertyInitializationOutputConflicts(
        IReadOnlyList<PlaylistSummaryExternalPropertyInitializationChange> pendingChanges,
        CustomFolderOutputSettingsSnapshot settings)
    {
        if (pendingChanges == null || pendingChanges.Count == 0)
        {
            return [];
        }
        if (settings?.OperationModeLR2DB != true)
        {
            return [.. pendingChanges];
        }

        var reservedOutputDirectories = new Dictionary<string, BMSTable>(
            StringComparer.OrdinalIgnoreCase);
        foreach (BMSTable table in GetPlaylistStore().BMSTables ?? Enumerable.Empty<BMSTable>())
        {
            ReserveOutputDirectoryOwner(reservedOutputDirectories, table?.Output_dir, table);
        }

        var acceptedChanges = new List<PlaylistSummaryExternalPropertyInitializationChange>();
        foreach (PlaylistSummaryExternalPropertyInitializationChange change in pendingChanges)
        {
            string outputDir = change.EffectiveOutputDirAfter;
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                LogPlaylistSummaryBulkWarning("playlist_summary_external_property_initialization_skipped reason=blank_output_dir table=" + (change.Table?.name ?? string.Empty));
                continue;
            }
            if (reservedOutputDirectories.TryGetValue(outputDir, out BMSTable reservedTable)
                && !ReferenceEquals(reservedTable, change.Table))
            {
                LogPlaylistSummaryBulkWarning("playlist_summary_external_property_initialization_skipped reason=duplicate_output_dir table=" + (change.Table?.name ?? string.Empty) + " outputDir=" + outputDir);
                continue;
            }
            string currentOutputDir = change.EffectiveOutputDirBefore;
            if (!string.Equals(currentOutputDir ?? string.Empty, outputDir ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(currentOutputDir)
                && reservedOutputDirectories.TryGetValue(currentOutputDir, out BMSTable currentOwner)
                && ReferenceEquals(currentOwner, change.Table))
            {
                reservedOutputDirectories.Remove(currentOutputDir);
            }
            reservedOutputDirectories[outputDir] = change.Table;
            acceptedChanges.Add(change);
        }
        return acceptedChanges;
    }

    private static void ReserveOutputDirectoryOwner(Dictionary<string, BMSTable> reservedOutputDirectories, string outputDir, BMSTable table)
    {
        if (reservedOutputDirectories == null || string.IsNullOrWhiteSpace(outputDir))
        {
            return;
        }
        if (!reservedOutputDirectories.ContainsKey(outputDir))
        {
            reservedOutputDirectories.Add(outputDir, table);
        }
    }

    private static void CapturePlaylistSummaryOutputDirectoryBefore(
        BMSTable table,
        string effectiveOutputDirBefore,
        Dictionary<BMSTable, string> outputDirPathBeforeByTable,
        Dictionary<BMSTable, string> outputBaseDirPathBeforeByTable,
        Dictionary<BMSTable, bool> wasRootFolderBeforeByTable,
        CustomFolderOutputSettingsSnapshot settings)
    {
        if (settings?.OperationModeLR2DB != true
            || table == null
            || string.IsNullOrWhiteSpace(effectiveOutputDirBefore))
        {
            return;
        }

        bool beforeBaseDirectoryResolved = table.is_root_folder;
        string beforeBaseDirectory = table.is_root_folder
            ? settings.LR2CustomFolderOutputBaseDirRootType
            : null;
        if (!table.is_root_folder)
        {
            beforeBaseDirectoryResolved = CustomFolderOutputBaseRegistry.TryResolveNormalOutputBaseDirectory(
                table.custom_folder_output_base_name,
                settings.LR2CustomFolderOutputBaseDir,
                settings.LR2CustomFolderAdditionalOutputBaseDirs,
                out beforeBaseDirectory);
            if (!beforeBaseDirectoryResolved)
            {
                beforeBaseDirectory = settings.LR2CustomFolderOutputBaseDir;
                beforeBaseDirectoryResolved = !string.IsNullOrWhiteSpace(beforeBaseDirectory);
            }
        }
        if (!beforeBaseDirectoryResolved || string.IsNullOrWhiteSpace(beforeBaseDirectory))
        {
            return;
        }
        outputDirPathBeforeByTable[table] = Path.Combine(beforeBaseDirectory, effectiveOutputDirBefore);
        outputBaseDirPathBeforeByTable[table] = beforeBaseDirectory;
        wasRootFolderBeforeByTable[table] = table.is_root_folder;
    }

    private void UpdatePlaylistSummaryRootOutputDirectoriesAfterExternalInitialization(
        IEnumerable<BMSTable> outputChangedTables,
        IReadOnlyDictionary<BMSTable, string> outputDirPathBeforeByTable,
        CustomFolderOutputSettingsSnapshot settings)
    {
        LR2Config lr2config = GetLr2Config();
        if (settings?.OperationModeLR2DB != true || lr2config == null)
        {
            return;
        }
        List<BMSTable> rootTables = [.. (outputChangedTables ?? [])
            .Where(table => table != null && table.is_root_folder && !string.IsNullOrWhiteSpace(table.Output_dir))
            .Distinct()];
        if (rootTables.Count == 0)
        {
            return;
        }

        List<string> bmsSearchDirectories = lr2config.GetBMSSearchDirectoriesForChangeTracking();
        IEnumerable<string> removeDirectories = (outputDirPathBeforeByTable ?? new Dictionary<BMSTable, string>())
            .Where(item => item.Key != null && item.Key.is_root_folder)
            .Select(item => item.Value)
            .Where(directory => !string.IsNullOrWhiteSpace(directory));
        IEnumerable<string> addDirectories = rootTables
            .Select(table => ResolvePlaylistSummaryCustomFolderOutputDirectory(
                table,
                "playlist summary root output directory notification",
                settings))
            .Where(directory => !string.IsNullOrWhiteSpace(directory));
        lr2config.SetBMSSearchDirectories(bmsSearchDirectories
            .Except(removeDirectories, StringComparer.OrdinalIgnoreCase)
            .Union(addDirectories)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        lr2config.Save();
    }

    internal void ApplyPlaylistSummaryCustomFolderOutputTypes(IEnumerable<PlaylistSummaryRow> rows, PlaylistSummaryCustomFolderOutputPatch patch)
    {
        if (rows == null || patch == null)
        {
            return;
        }
        BMSPlaylist tables = GetPlaylistStore();
        List<BMSTable> changedTables = [];
        var previousMasks = new Dictionary<BMSTable, LR2SongDBExtended.playlist.CustomFolderType>();
        var nextMasks = new Dictionary<BMSTable, LR2SongDBExtended.playlist.CustomFolderType>();
        foreach (BMSTable table in rows
            .Where(row => row?.TableRef != null)
            .Select(row => row.TableRef)
            .Distinct())
        {
            LR2SongDBExtended.playlist.CustomFolderType nextMask = ApplyPlaylistSummaryCustomFolderOutputPatchToMask(table, table.ignore_folder_output, patch);
            if (nextMask == table.ignore_folder_output)
            {
                continue;
            }
            previousMasks[table] = table.ignore_folder_output;
            nextMasks[table] = nextMask;
            changedTables.Add(table);
        }
        if (changedTables.Count == 0)
        {
            return;
        }
        CustomFolderOutputSettingsSnapshot settings = customFolderOutputSettingsProvider()
            ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");
        foreach (KeyValuePair<BMSTable, LR2SongDBExtended.playlist.CustomFolderType> item in nextMasks)
        {
            item.Key.ignore_folder_output = item.Value;
        }

        RunPlaylistSummaryBulkOperation(
            () =>
            {
                try
                {
                    UpdatePlaylistSummaryCustomFolderOutputProgress(0, changedTables.Count, string.Empty);
                    tables.ReOutputCustomFoldersAndCommitHeadersToDB(
                        changedTables,
                        "playlist_summary_bulk_custom_folder_output_changed",
                        UpdatePlaylistSummaryCustomFolderOutputProgress,
                        settings);
                }
                catch
                {
                    foreach (KeyValuePair<BMSTable, LR2SongDBExtended.playlist.CustomFolderType> item in previousMasks)
                    {
                        item.Key.ignore_folder_output = item.Value;
                    }
                    throw;
                }
            },
            "playlist summary custom folder output notification");
        RequestPlaylistSummaryRefresh("playlist_summary_bulk_custom_folder_output_changed");
    }

    private void UpdatePlaylistSummaryCustomFolderOutputProgress(int completedTableCount, int totalTableCount, string currentTableName)
    {
        ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            TotalTableCount = totalTableCount,
            CompletedTableCount = completedTableCount,
            CurrentTableName = currentTableName ?? string.Empty,
            LabelFormat = BeMusicSeeker.Properties.Resources.Custom_folder_output_progress_label_format,
            SingleLabel = BeMusicSeeker.Properties.Resources.Custom_folder_output_progress_single_label
        });
    }

    internal void ApplyPlaylistSummaryExternalSync(IEnumerable<PlaylistSummaryRow> rows, bool isExternalSync)
    {
        if (rows == null)
        {
            return;
        }
        BMSPlaylist tables = GetPlaylistStore();
        List<BMSTable> changedTables = [];
        foreach (BMSTable table in rows
            .Where(row => row?.TableRef != null)
            .Select(row => row.TableRef)
            .Distinct())
        {
            if (ApplyPlaylistSummaryExternalSyncFlagForTable(table, isExternalSync))
            {
                changedTables.Add(table);
            }
        }
        if (changedTables.Count == 0)
        {
            return;
        }
        RunWithNotifications(
            () => tables.ReOutputCustomFoldersAndCommitHeadersToDB(
                changedTables,
                "playlist_summary_bulk_external_sync_changed"),
            "playlist summary external sync notification");
        tables.BmtOutput.QueueBeatorajaBmtExportForTables(changedTables, "playlist_summary_bulk_external_sync_changed");
        RequestPlaylistSummaryRefresh("playlist_summary_bulk_external_sync_changed");
    }

    internal static bool ApplyPlaylistSummaryExternalSyncFlagForTable(BMSTable table, bool isExternalSync)
    {
        if (table == null)
        {
            return false;
        }
        bool before = table.is_external_sync;
        if (isExternalSync)
        {
            table.EnableExternalSync();
        }
        else
        {
            table.DisableExternalSync();
        }
        return before != table.is_external_sync;
    }

    internal void ApplyPlaylistSummaryFlags(IEnumerable<PlaylistSummaryRow> rows, bool? isExternalSync = null, bool? isRootFolder = null)
    {
        if (rows == null)
        {
            return;
        }
        if (isRootFolder.HasValue)
        {
            ApplyPlaylistSummaryRootFolder(rows, isRootFolder.Value);
            if (isExternalSync.HasValue)
            {
                ApplyPlaylistSummaryExternalSync(rows, isExternalSync.Value);
            }
            return;
        }
        if (isExternalSync.HasValue)
        {
            ApplyPlaylistSummaryExternalSync(rows, isExternalSync.Value);
        }
    }

    internal void ApplyPlaylistSummaryRootFolder(IEnumerable<PlaylistSummaryRow> rows, bool isRootFolder)
    {
        if (rows == null)
        {
            return;
        }
        BMSPlaylist tables = GetPlaylistStore();

        List<BMSTable> changedTables = [.. rows
            .Where(row => row?.TableRef != null)
            .Select(row => row.TableRef)
            .Distinct()
            .Where(table => table.is_root_folder != isRootFolder)];
        if (changedTables.Count == 0)
        {
            return;
        }

        CustomFolderOutputSettingsSnapshot settings = customFolderOutputSettingsProvider()
            ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");

        var outputDirPathBeforeByTable = new Dictionary<BMSTable, string>();
        var outputBaseDirPathBeforeByTable = new Dictionary<BMSTable, string>();
        var wasRootFolderBeforeByTable = new Dictionary<BMSTable, bool>();
        foreach (BMSTable table in changedTables)
        {
            wasRootFolderBeforeByTable[table] = table.is_root_folder;
            bool beforeBaseDirectoryResolved = table.is_root_folder;
            string beforeBaseDirectory = table.is_root_folder
                ? settings.LR2CustomFolderOutputBaseDirRootType
                : null;
            if (!table.is_root_folder)
            {
                beforeBaseDirectoryResolved = CustomFolderOutputBaseRegistry.TryResolveNormalOutputBaseDirectory(
                    table.custom_folder_output_base_name,
                    settings.LR2CustomFolderOutputBaseDir,
                    settings.LR2CustomFolderAdditionalOutputBaseDirs,
                    out beforeBaseDirectory);
            }
            string beforeDirectory = settings.OperationModeLR2DB
                && beforeBaseDirectoryResolved
                && !string.IsNullOrWhiteSpace(table.Output_dir)
                ? Path.Combine(beforeBaseDirectory, table.Output_dir)
                : null;
            if (!string.IsNullOrWhiteSpace(beforeDirectory))
            {
                outputDirPathBeforeByTable[table] = beforeDirectory;
                outputBaseDirPathBeforeByTable[table] = beforeBaseDirectory;
            }
            table.is_root_folder = isRootFolder;
        }

        RunPlaylistSummaryBulkOperation(
            () =>
            {
                UpdatePlaylistSummaryCustomFolderOutputProgress(0, changedTables.Count, string.Empty);
                tables.MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
                    changedTables,
                    outputDirPathBeforeByTable,
                    "playlist_summary_root_folder_changed",
                    UpdatePlaylistSummaryCustomFolderOutputProgress,
                    wasRootFolderBeforeByTable,
                    outputBaseDirPathBeforeByTable: outputBaseDirPathBeforeByTable,
                    settings: settings);
            },
            "playlist summary root folder notification");

        LR2Config lr2config = GetLr2Config();
        if (settings.OperationModeLR2DB && lr2config != null)
        {
            List<string> bmsSearchDirectories = lr2config.GetBMSSearchDirectoriesForChangeTracking();
            if (isRootFolder)
            {
                IEnumerable<string> addDirectories = changedTables
                    .Where(table => !string.IsNullOrWhiteSpace(table.Output_dir))
                    .Select(table => ResolvePlaylistSummaryCustomFolderOutputDirectory(
                        table,
                        "playlist summary root output directory notification",
                        settings))
                    .Where(directory => !string.IsNullOrWhiteSpace(directory));
                lr2config.SetBMSSearchDirectories(bmsSearchDirectories
                    .Union(addDirectories)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                lr2config.SetBMSSearchDirectories(bmsSearchDirectories
                    .Except(outputDirPathBeforeByTable.Values, StringComparer.OrdinalIgnoreCase));
            }
            lr2config.Save();
        }

        RequestPlaylistSummaryRefresh("playlist_properties_bulk_changed");
    }

    internal void ApplyPlaylistSummaryOutputBase(IEnumerable<PlaylistSummaryRow> rows, string outputBaseName)
    {
        if (rows == null)
        {
            return;
        }
        BMSPlaylist tables = GetPlaylistStore();
        string normalizedBaseName = CustomFolderOutputBaseRegistry.NormalizeBaseName(outputBaseName);
        List<BMSTable> changedTables = [.. rows
            .Where(row => row?.TableRef != null)
            .Select(row => row.TableRef)
            .Distinct()
            .Where(table => !string.Equals(
                CustomFolderOutputBaseRegistry.NormalizeBaseName(table.custom_folder_output_base_name),
                normalizedBaseName,
                StringComparison.OrdinalIgnoreCase))];
        if (changedTables.Count == 0)
        {
            return;
        }
        CustomFolderOutputSettingsSnapshot settings = customFolderOutputSettingsProvider()
            ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");
        if (!string.IsNullOrWhiteSpace(normalizedBaseName)
            && !CustomFolderOutputBaseRegistry.ContainsBaseName(
                normalizedBaseName,
                settings.LR2CustomFolderAdditionalOutputBaseDirs))
        {
            return;
        }

        var migrationTables = new List<BMSTable>();
        var outputDirPathBeforeByTable = new Dictionary<BMSTable, string>();
        var outputBaseDirPathBeforeByTable = new Dictionary<BMSTable, string>();
        foreach (BMSTable table in changedTables)
        {
            if (!table.is_root_folder)
            {
                bool beforeBaseDirectoryResolved = CustomFolderOutputBaseRegistry.TryResolveNormalOutputBaseDirectory(
                    table.custom_folder_output_base_name,
                    settings.LR2CustomFolderOutputBaseDir,
                    settings.LR2CustomFolderAdditionalOutputBaseDirs,
                    out string beforeBaseDirectory);
                if (!beforeBaseDirectoryResolved)
                {
                    beforeBaseDirectory = settings.LR2CustomFolderOutputBaseDir;
                    beforeBaseDirectoryResolved = !string.IsNullOrWhiteSpace(beforeBaseDirectory);
                }
                string beforeDirectory = settings.OperationModeLR2DB
                    && beforeBaseDirectoryResolved
                    && !string.IsNullOrWhiteSpace(table.Output_dir)
                    ? Path.Combine(beforeBaseDirectory, table.Output_dir)
                    : null;
                if (!string.IsNullOrWhiteSpace(beforeDirectory))
                {
                    outputDirPathBeforeByTable[table] = beforeDirectory;
                    outputBaseDirPathBeforeByTable[table] = beforeBaseDirectory;
                }
                migrationTables.Add(table);
            }
            table.custom_folder_output_base_name = normalizedBaseName;
        }

        if (migrationTables.Count > 0)
        {
            RunPlaylistSummaryBulkOperation(
                () =>
                {
                    UpdatePlaylistSummaryCustomFolderOutputProgress(0, migrationTables.Count, string.Empty);
                    tables.MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
                        migrationTables,
                        outputDirPathBeforeByTable,
                        "playlist_summary_output_base_changed",
                        UpdatePlaylistSummaryCustomFolderOutputProgress,
                        outputBaseDirPathBeforeByTable: outputBaseDirPathBeforeByTable,
                        settings: settings);
                },
                "playlist summary output base notification");
            List<BMSTable> headerOnlyTables = [.. changedTables.Except(migrationTables)];
            if (headerOnlyTables.Count > 0)
            {
                tables.CommitBMSTableHeadersToDB(headerOnlyTables);
            }
        }
        else
        {
            tables.CommitBMSTableHeadersToDB(changedTables);
        }
        RequestPlaylistSummaryRefresh("playlist_summary_output_base_changed");
    }

}


internal sealed class PlaylistSummaryBulkInvalidOutputDirectoryEventArgs : EventArgs
{
    internal PlaylistSummaryBulkInvalidOutputDirectoryEventArgs(string routeName)
    {
        RouteName = routeName ?? string.Empty;
    }
    internal string RouteName { get; }
}
