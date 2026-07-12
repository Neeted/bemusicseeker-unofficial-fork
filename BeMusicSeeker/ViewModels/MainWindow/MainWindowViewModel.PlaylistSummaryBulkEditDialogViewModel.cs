using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Codeplex.Data;
using Livet;
using Livet.Commands;
using Livet.EventListeners;
using Microsoft.VisualBasic.FileIO;
using NLog;
using Ribbit.BMS;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Ribbit.Net;
using Ribbit.Util;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Contains playlist-summary bulk-edit dialog state while preserving the nested public type shape.
/// </summary>
public partial class MainWindowViewModel
{
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
        private readonly MainWindowViewModel ownerViewModel;

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

        public PlaylistSummaryBulkEditDialogViewModel(MainWindowViewModel owner, IEnumerable<PlaylistSummaryRow> rows)
        {
            ownerViewModel = owner ?? throw new ArgumentNullException(nameof(owner));
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
            OutputBaseOptions = ownerViewModel.CreatePlaylistCustomFolderOutputBaseOptionsForCurrentSettings(includeNoChange: true);
            _outputBaseOption = OutputBaseOptions.FirstOrDefault(option => option.IsNoChange) ?? OutputBaseOptions.FirstOrDefault();
            ReloadCustomFolderOutputStates();
        }

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
            ownerViewModel.ApplyPlaylistSummaryCustomFolderOutputTypes(targetRows, BuildCustomFolderOutputPatch());
        }

        public void ApplyRootFolder()
        {
            if (RootFolderOption?.Value is bool value)
            {
                ownerViewModel.ApplyPlaylistSummaryFlags(targetRows, isRootFolder: value);
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
                ownerViewModel.ApplyPlaylistSummaryExternalSync(targetRows, value);
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
                ownerViewModel.ApplyPlaylistSummaryBmtOutput(targetRows, value);
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
                ownerViewModel.ApplyPlaylistSummaryOutputBase(targetRows, OutputBaseOption.BaseName);
            }
        }

        public void ResetOutputBaseOption()
        {
            OutputBaseOption = OutputBaseOptions.FirstOrDefault(option => option.IsNoChange) ?? OutputBaseOptions.FirstOrDefault();
        }

        public Task ApplyExternalPropertyInitializationAsync()
        {
            return ownerViewModel.ApplyPlaylistSummaryExternalPropertyInitializationAsync(targetRows, BuildExternalPropertyInitializationOptions());
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
}
