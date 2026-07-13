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
/// Contains playlist property dialog state while preserving the nested public type shape.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Holds editable playlist table properties before they are written back to the playlist model.
    /// </summary>
    public partial class PlaylistPropertyDialogViewModel : ViewModel
    {
        private BMSTable bmsTable;

        private readonly MainWindowViewModel ownerViewModel;

        private readonly bool isForNewTable;

        private DispatcherCollection<string> _folder_order;

        private LR2SongDBExtended.playlist.CustomFolderSortType _folder_sort_key;

        private LR2SongDBExtended.playlist.EntryUnitType _entry_type;

        private string temp_compat_prefix;

        private string temp_name;

        private string temp_symbol;

        private string _compat_prefix;

        private bool _folder_sort_ascending;

        private LR2SongDBExtended.playlist.CustomFolderType _ignore_folder_output;

        private string _name;

        private string _symbol;

        private Uri temp_Page_url;

        private Uri temp_Header_url;

        private IReadOnlyList<string> temp_folder_order;

        private Uri _Page_url;

        private Uri _Header_url;

        private Uri _Data_url;

        private bool temp_is_external_sync;

        private bool _is_external_sync;

        private bool temp_is_root_folder;

        private bool _is_root_folder;

        private string temp_output_dir_full_path;

        private string temp_output_dir;

        private readonly CustomFolderOutputSettingsSnapshot temp_custom_folder_output_settings;

        private string temp_custom_folder_output_base_name;

        private string _output_dir;

        private PlaylistCustomFolderOutputBaseOption _custom_folder_output_base_option;

        private bool _is_auto_folder_sort;

        private IReadOnlyList<PlaylistCustomFolderOutputBaseOption> _outputBaseOptions;

        public IReadOnlyList<PlaylistCustomFolderOutputBaseOption> OutputBaseOptions =>
            _outputBaseOptions ??= MainWindowViewModel.CreatePlaylistCustomFolderOutputBaseOptions(
                temp_custom_folder_output_settings?.LR2CustomFolderOutputBaseDir,
                CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(temp_custom_folder_output_settings?.LR2CustomFolderAdditionalOutputBaseDirs));

        public DispatcherCollection<string> folder_order
        {
            get
            {
                return _folder_order;
            }
            set
            {
                if (_folder_order != value)
                {
                    _folder_order = value;
                    RaisePropertyChanged("folder_order");
                }
            }
        }

        public LR2SongDBExtended.playlist.CustomFolderSortType folder_sort_key
        {
            get
            {
                return _folder_sort_key;
            }
            set
            {
                if (_folder_sort_key != value)
                {
                    _folder_sort_key = value;
                    RaisePropertyChanged("folder_sort_key");
                }
            }
        }

        public IEnumerable<string> folder_sort_key_list
        {
            get
            {
                if (entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder)
                {
                    return CustomFolderSortTypeExt.GetDisplayNames().Except(
                    [
                        LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL.ToDisplayName(),
                        LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE.ToDisplayName()
                    ]);
                }
                return CustomFolderSortTypeExt.GetDisplayNames();
            }
            set
            {
            }
        }

        public LR2SongDBExtended.playlist.EntryUnitType entry_type
        {
            get
            {
                return _entry_type;
            }
            set
            {
                if (_entry_type != value)
                {
                    DisableInvalidOutputFolders(value);
                    _entry_type = value;
                    RaisePropertyChanged("entry_type");
                    RaisePropertyChanged(() => folder_sort_key_list);
                }
            }
        }

        public IEnumerable<string> entry_type_list
        {
            get
            {
                return EntryUnitTypeExt.GetDisplayNames();
            }
            set
            {
            }
        }

        public string compat_prefix
        {
            get
            {
                return _compat_prefix;
            }
            set
            {
                value ??= string.Empty;
                value = value.TrimStart();
                if (!(_compat_prefix == value))
                {
                    _compat_prefix = value;
                    RaisePropertyChanged("compat_prefix");
                }
            }
        }

        public bool folder_sort_ascending
        {
            get
            {
                return _folder_sort_ascending;
            }
            set
            {
                if (_folder_sort_ascending != value)
                {
                    _folder_sort_ascending = value;
                    RaisePropertyChanged("folder_sort_ascending");
                }
            }
        }

        public LR2SongDBExtended.playlist.CustomFolderType ignore_folder_output
        {
            get
            {
                return _ignore_folder_output;
            }
            set
            {
                if (_ignore_folder_output != value)
                {
                    _ignore_folder_output = value;
                    RaisePropertyChanged("ignore_folder_output");
                }
            }
        }

        public DateTime last_update
        {
            get
            {
                return bmsTable.last_update;
            }
            set
            {
            }
        }

        public string name
        {
            get
            {
                if (!IsNameValid())
                {
                    _name = string.Empty;
                }
                return _name;
            }
            set
            {
                value = value.Trim();
                if (!(_name == value))
                {
                    if (IsNameValid(value))
                    {
                        _name = value;
                    }
                    RaisePropertyChanged("name");
                    RaisePropertyChanged(() => output_dir);
                    if (temp_custom_folder_output_settings?.OperationModeLR2DB == true && !IsOutputDirValid())
                    {
                        MainWindowViewModel.ShowUiMessage(BeMusicSeeker.Properties.Resources.Error_OutputFolderNameEmptyOrDuplicateChangePlaylist, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
                    }
                }
            }
        }

        public string symbol
        {
            get
            {
                return _symbol;
            }
            set
            {
                value = value.Trim();
                if (!(_symbol == value))
                {
                    _symbol = value;
                    RaisePropertyChanged("symbol");
                }
            }
        }

        public Uri Page_url
        {
            get
            {
                if (!IsUrlValid(_Page_url))
                {
                    _Page_url = null;
                }
                return _Page_url;
            }
            set
            {
                if (!(_Page_url == value))
                {
                    if (IsUrlValid(value) && value.IsAbsoluteUri)
                    {
                        _Page_url = value;
                    }
                    else if (string.IsNullOrWhiteSpace(value.ToString()))
                    {
                        _Page_url = null;
                    }
                    else
                    {
                        MainWindowViewModel.ShowUiMessage(BeMusicSeeker.Properties.Resources.Error_InvalidPageUriAbsoluteRequired, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
                    }
                    RaisePropertyChanged("Page_url");
                }
            }
        }

        public Uri Header_url
        {
            get
            {
                if (!IsUrlValid(_Header_url))
                {
                    _Header_url = null;
                }
                return _Header_url;
            }
            set
            {
                if (!(_Header_url == value))
                {
                    if (IsUrlValid(value))
                    {
                        _Header_url = value;
                    }
                    else
                    {
                        MainWindowViewModel.ShowUiMessage(BeMusicSeeker.Properties.Resources.Error_InvalidHeaderUri, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
                    }
                    RaisePropertyChanged("Header_url");
                }
            }
        }

        public Uri Data_url
        {
            get
            {
                if (!IsUrlValid(_Data_url))
                {
                    _Data_url = null;
                }
                return _Data_url;
            }
            set
            {
                if (!(_Data_url == value))
                {
                    if (IsUrlValid(value))
                    {
                        _Data_url = value;
                    }
                    else
                    {
                        MainWindowViewModel.ShowUiMessage(BeMusicSeeker.Properties.Resources.Error_InvalidDataUri, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
                    }
                    RaisePropertyChanged("Data_url");
                }
            }
        }

        public bool is_external_sync
        {
            get
            {
                return _is_external_sync;
            }
            set
            {
                if (_is_external_sync == value)
                {
                    return;
                }
                if (!IsExternal_syncValid(value))
                {
                    value = false;
                    MainWindowViewModel.ShowUiMessage(BeMusicSeeker.Properties.Resources.Error_InvalidPageOrHeaderUri, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
                }
                if (!_is_external_sync && value)
                {
                    if (!MainWindowViewModel.ShowUiConfirmation(BeMusicSeeker.Properties.Resources.Confirm_EnablePlaylistSyncModeLoseLocalChanges, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "Playlist sync enable confirmation"))
                    {
                        RaisePropertyChanged("is_external_sync");
                        return;
                    }
                }
                else if (_is_external_sync && !value)
                {
                    if (!MainWindowViewModel.ShowUiConfirmation(BeMusicSeeker.Properties.Resources.Confirm_DisablePlaylistSyncModeRemoteChangesNotApplied, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "Playlist sync disable confirmation"))
                    {
                        RaisePropertyChanged("is_external_sync");
                        return;
                    }
                }
                _is_external_sync = value;
                RaisePropertyChanged("is_external_sync");
            }
        }

        public bool is_root_folder
        {
            get
            {
                return _is_root_folder;
            }
            set
            {
                if (_is_root_folder != value)
                {
                    _is_root_folder = value;
                    RaisePropertyChanged("is_root_folder");
                }
            }
        }

        public string output_dir
        {
            get
            {
                return BMSTable.ResolveOutputDirectoryName(name, _output_dir);
            }
            set
            {
                value = BMSTable.NormalizeOutputDirectoryName(value);
                string defaultOutputDirectoryName = BMSTable.CreateDefaultOutputDirectoryName(name);
                if (!string.IsNullOrWhiteSpace(value)
                    && !string.Equals(defaultOutputDirectoryName, value, StringComparison.Ordinal))
                {
                    if (temp_custom_folder_output_settings?.OperationModeLR2DB != true || IsOutputDirValid(value))
                    {
                        _output_dir = value;
                    }
                    else
                    {
                        MainWindowViewModel.ShowUiMessage(BeMusicSeeker.Properties.Resources.Error_OutputFolderNameEmptyOrDuplicateCheckInput, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
                    }
                }
                else if (string.IsNullOrWhiteSpace(value)
                    || string.Equals(defaultOutputDirectoryName, value, StringComparison.Ordinal))
                {
                    _output_dir = null;
                }
                RaisePropertyChanged("output_dir");
            }
        }

        public PlaylistCustomFolderOutputBaseOption custom_folder_output_base_option
        {
            get
            {
                _custom_folder_output_base_option ??= ResolveOutputBaseOption(bmsTable.custom_folder_output_base_name);
                return _custom_folder_output_base_option;
            }
            set
            {
                if (_custom_folder_output_base_option != value)
                {
                    _custom_folder_output_base_option = value ?? ResolveOutputBaseOption(null);
                    RaisePropertyChanged(nameof(custom_folder_output_base_option));
                }
            }
        }

        public bool is_auto_folder_sort
        {
            get
            {
                return _is_auto_folder_sort;
            }
            set
            {
                if (_is_auto_folder_sort != value)
                {
                    _is_auto_folder_sort = value;
                    RaisePropertyChanged("is_auto_folder_sort");
                }
            }
        }

        private PlaylistCustomFolderOutputBaseOption ResolveOutputBaseOption(string baseName)
        {
            string normalized = CustomFolderOutputBaseRegistry.NormalizeBaseName(baseName);
            IReadOnlyList<PlaylistCustomFolderOutputBaseOption> options = OutputBaseOptions;
            return options.FirstOrDefault(option =>
                    !option.IsNoChange
                    && string.Equals(option.BaseName, normalized, StringComparison.OrdinalIgnoreCase))
                ?? options.FirstOrDefault(option => !option.IsNoChange && option.BaseName == null)
                ?? options.FirstOrDefault();
        }

        public PlaylistPropertyDialogViewModel(MainWindowViewModel _owner, BMSTable _table, bool _isForNewTable = false)
        {
            ownerViewModel = _owner;
            bmsTable = _table ?? throw new ArgumentNullException("_table");
            isForNewTable = _isForNewTable;
            temp_custom_folder_output_settings = ownerViewModel.customFolderOutputSettingsProvider()
                ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");
            ownerViewModel.tables.AcquireWriterLockBMSTables();
            backupTableProperties();
            loadTableProperties();
        }

        private void DisableInvalidOutputFolders()
        {
            DisableInvalidOutputFolders(entry_type);
        }

        private void DisableInvalidOutputFolders(LR2SongDBExtended.playlist.EntryUnitType value)
        {
            if (value != LR2SongDBExtended.playlist.EntryUnitType.Folder)
            {
                return;
            }
            ignore_folder_output |= LR2SongDBExtended.playlist.CustomFolderType.LevelFolder;
            RaisePropertyChanged(() => ignore_folder_output);
            if (folder_sort_key == LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL || folder_sort_key == LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE)
            {
                folder_sort_key = LR2SongDBExtended.playlist.CustomFolderSortType.NONE;
                RaisePropertyChanged(() => folder_sort_key);
            }
        }

        private bool IsNameValid()
        {
            return IsNameValid(_name);
        }

        private bool IsNameValid(string value)
        {
            return true;
        }

        private bool IsUrlValid(Uri value)
        {
            if (!(value == null) && !string.IsNullOrWhiteSpace(value.ToString()))
            {
                return Uri.TryCreate(value.OriginalString, UriKind.RelativeOrAbsolute, out value);
            }
            return true;
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

        private bool IsExternal_syncValid(bool value)
        {
            if (!value)
            {
                return true;
            }
            if (Page_url != null && Page_url.Scheme == "bmseeker")
            {
                return true;
            }
            if (Header_url != null && Data_url != null && ((Page_url != null && Page_url.IsAbsoluteUri) || Header_url.IsAbsoluteUri))
            {
                return true;
            }
            return false;
        }

        private bool IsOutputDirValid()
        {
            return IsOutputDirValid(output_dir);
        }

        private bool IsOutputDirValid(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            if ((from t in ownerViewModel.BMSTables
                 where t != null && t != bmsTable
                 select t.Output_dir).Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        public bool ResetProperties()
        {
            loadTableProperties();
            if (isForNewTable)
            {
                ownerViewModel.tables.RemoveBMSTable(bmsTable);
                return true;
            }
            return CheckValidation();
        }

        public bool CheckValidation()
        {
            if (!IsNameValid())
            {
                return false;
            }
            if ((Page_url != null && (!IsUrlValid(Page_url) || !Page_url.IsAbsoluteUri)) || !IsUrlValid(Header_url) || !IsUrlValid(Data_url))
            {
                return false;
            }
            if (temp_custom_folder_output_settings?.OperationModeLR2DB == true)
            {
                if (!IsOutputDirValid())
                {
                    return false;
                }
                DisableInvalidOutputFolders();
            }
            return true;
        }

        public bool SaveProperties()
        {
            if (CheckValidation())
            {
                if (!string.Equals(bmsTable.compat_prefix, compat_prefix, StringComparison.Ordinal))
                {
                    ownerViewModel.tables.EnsurePlaylistEntriesLoaded(bmsTable, "PlaylistPropertyDialogViewModel.ValidateCompatibleFolderPrefixRewrite");
                    using (bmsTable.ReaderWriterLock.GetReaderGuard())
                    {
                        if (!bmsTable.CanRewriteCompatibleFolderPrefix(bmsTable.compat_prefix, compat_prefix))
                        {
                            return false;
                        }
                    }
                }
                if (is_auto_folder_sort)
                {
                    bmsTable.Folder_order = [];
                }
                else
                {
                    bmsTable.Folder_order = [.. folder_order];
                }
                bmsTable.folder_sort_key = folder_sort_key;
                bmsTable.folder_sort_ascending = folder_sort_ascending;
                bmsTable.ignore_folder_output = ignore_folder_output;
                bmsTable.entry_type = entry_type;
                bmsTable.name = name;
                bmsTable.symbol = symbol;
                bmsTable.Page_url = NormalizeUriTextForStandardStorage(Page_url);
                bmsTable.Header_url = NormalizeUriTextForStandardStorage(Header_url);
                bmsTable.Data_url = NormalizeUriTextForStandardStorage(Data_url);
                if (is_external_sync)
                {
                    bmsTable.EnableExternalSync();
                }
                else
                {
                    bmsTable.DisableExternalSync();
                }
                bmsTable.Output_dir = output_dir;
                bmsTable.custom_folder_output_base_name = custom_folder_output_base_option?.BaseName;
                bmsTable.is_root_folder = is_root_folder;
                bmsTable.compat_prefix = compat_prefix;
                return true;
            }
            return false;
        }

        private void loadTableProperties()
        {
            ownerViewModel?.tables?.EnsurePlaylistEntriesLoaded(bmsTable, "PlaylistPropertyDialogViewModel.loadTableProperties");
            _folder_order = new DispatcherCollection<string>(DispatcherHelper.UIDispatcher);
            _folder_order.AddRange(bmsTable.folder_list);
            RaisePropertyChanged(() => folder_order);
            _folder_sort_key = bmsTable.folder_sort_key;
            RaisePropertyChanged(() => folder_sort_key);
            _folder_sort_ascending = bmsTable.folder_sort_ascending;
            RaisePropertyChanged(() => folder_sort_ascending);
            _ignore_folder_output = bmsTable.ignore_folder_output;
            RaisePropertyChanged(() => ignore_folder_output);
            _entry_type = bmsTable.entry_type;
            RaisePropertyChanged(() => entry_type);
            _name = bmsTable.name;
            RaisePropertyChanged(() => name);
            _symbol = bmsTable.symbol;
            RaisePropertyChanged(() => symbol);
            _Page_url = bmsTable.Page_url;
            RaisePropertyChanged(() => Page_url);
            _Header_url = bmsTable.Header_url;
            RaisePropertyChanged(() => Header_url);
            _Data_url = bmsTable.Data_url;
            RaisePropertyChanged(() => Data_url);
            _is_external_sync = bmsTable.is_external_sync;
            RaisePropertyChanged(() => is_external_sync);
            _output_dir = bmsTable.output_dir;
            RaisePropertyChanged(() => output_dir);
            _outputBaseOptions = MainWindowViewModel.CreatePlaylistCustomFolderOutputBaseOptions(
                temp_custom_folder_output_settings?.LR2CustomFolderOutputBaseDir,
                CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(temp_custom_folder_output_settings?.LR2CustomFolderAdditionalOutputBaseDirs));
            _custom_folder_output_base_option = ResolveOutputBaseOption(bmsTable.custom_folder_output_base_name);
            RaisePropertyChanged(() => OutputBaseOptions);
            RaisePropertyChanged(() => custom_folder_output_base_option);
            _is_root_folder = bmsTable.is_root_folder;
            RaisePropertyChanged(() => is_root_folder);
            _is_auto_folder_sort = bmsTable.Folder_order == null || bmsTable.Folder_order.Count() == 0;
            RaisePropertyChanged(() => is_auto_folder_sort);
            _compat_prefix = bmsTable.compat_prefix;
            RaisePropertyChanged(() => compat_prefix);
        }

        private void backupTableProperties()
        {
            temp_output_dir_full_path = temp_custom_folder_output_settings?.OperationModeLR2DB == true
                ? ownerViewModel.ResolveCustomFolderOutputDirectoryWithNotification(
                    bmsTable,
                    "playlist property output directory notification",
                    temp_custom_folder_output_settings)
                : null;
            temp_output_dir = bmsTable.output_dir;
            temp_is_root_folder = bmsTable.is_root_folder;
            temp_is_external_sync = bmsTable.is_external_sync;
            temp_compat_prefix = bmsTable.compat_prefix;
            temp_custom_folder_output_base_name = CustomFolderOutputBaseRegistry.NormalizeBaseName(bmsTable.custom_folder_output_base_name);
            temp_name = bmsTable.name;
            temp_symbol = bmsTable.symbol;
            temp_Page_url = bmsTable.Page_url;
            temp_Header_url = bmsTable.Header_url;
            temp_folder_order = [.. (bmsTable.Folder_order ?? [])];
        }

        private static bool HasSameUri(Uri left, Uri right)
        {
            return string.Equals(left?.ToString() ?? string.Empty, right?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }

        private static bool HasSameStringSequence(IEnumerable<string> left, IEnumerable<string> right)
        {
            return (left ?? Enumerable.Empty<string>()).SequenceEqual(right ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        }

        internal async Task ApplyPostSaveUpdatesAsync()
        {
            using BMSPlaylist.OperationNotificationScope notificationScope = BMSPlaylist.BeginOperationNotificationScope();
            bool prefixChanged = !string.Equals(temp_compat_prefix, bmsTable.compat_prefix, StringComparison.Ordinal);
            bool outputDirChanged = !string.Equals(
                BMSTable.NormalizeOutputDirectoryName(temp_output_dir),
                BMSTable.NormalizeOutputDirectoryName(bmsTable.output_dir),
                StringComparison.Ordinal);
            bool flag = !string.Equals(temp_name, bmsTable.name, StringComparison.Ordinal) || !string.Equals(temp_symbol, bmsTable.symbol, StringComparison.Ordinal) || prefixChanged;
            bool outputBaseNameChanged = !string.Equals(
                temp_custom_folder_output_base_name,
                CustomFolderOutputBaseRegistry.NormalizeBaseName(bmsTable.custom_folder_output_base_name),
                StringComparison.OrdinalIgnoreCase);
            bool externalResyncApplied = false;
            bool entryFolderProjectionChanged = false;
            IReadOnlyDictionary<string, string> prefixFolderSelectionMap = null;
            if (prefixChanged)
            {
                ownerViewModel.tables.EnsurePlaylistEntriesLoaded(bmsTable, "PlaylistPropertyDialogViewModel.CreateCompatibleFolderPrefixRewriteMap");
                using (bmsTable.ReaderWriterLock.GetReaderGuard())
                {
                    prefixFolderSelectionMap = bmsTable.CreateValidatedCompatibleFolderPrefixRewriteMap(temp_compat_prefix, bmsTable.compat_prefix);
                }
            }
            bool shouldReloadExternalPlaylist = (!temp_is_external_sync && bmsTable.is_external_sync)
                || (bmsTable.is_external_sync && bmsTable.Page_url != null && temp_Page_url != null && bmsTable.Page_url.ToString() != temp_Page_url.ToString());
            if (shouldReloadExternalPlaylist)
            {
                Uri uri = bmsTable.Page_url ?? bmsTable.Header_url;
                if (uri != null && uri.IsAbsoluteUri)
                {
                    DateTime last_update = bmsTable.last_update;
                    BMSTable sourceTable = bmsTable;
                    ownerViewModel.BeginPlaylistSyncProgressOperation();
                    try
                    {
                        ownerViewModel.UpdatePlaylistSyncProgressStatus(new PlaylistSyncProgressSnapshot
                        {
                            IsActive = true,
                            TotalTableCount = 1,
                            CompletedTableCount = 0,
                            CurrentTableName = bmsTable.name,
                            CurrentUri = uri
                        });
                        List<BMSTableEntry> oldEntriesSnapshot;
                        ownerViewModel.tables.EnsurePlaylistEntriesLoaded(bmsTable, "PlaylistPropertyDialogViewModel.ApplyPostSaveUpdatesAsync");
                        using (bmsTable.ReaderWriterLock.GetReaderGuard())
                        {
                            oldEntriesSnapshot = [.. bmsTable.entries];
                        }
                        bmsTable = await ownerViewModel.tables.ResetBMSTableAsync(bmsTable, uri);
                        ownerViewModel.files.ReplaceReferenceBMSTable(sourceTable, bmsTable, oldEntriesSnapshot);
                        ownerViewModel.ReplaceCurrentPlaylistSelectionTable(sourceTable, bmsTable);
                        ownerViewModel.RemapCurrentPlaylistFolderSelection(bmsTable, prefixFolderSelectionMap);
                        ownerViewModel.InvalidateNormalLibraryReferenceTableSortKeys();
                        ownerViewModel.UpdatePlaylistSyncRuntimeStatus(PlaylistSyncAttemptResult.CreateSuccess(sourceTable, bmsTable, uri, bmsTable.last_update != last_update));
                        externalResyncApplied = true;
                        flag = false;
                    }
                    catch (Exception ex)
                    {
                        NLogWrapper.FileLogger?.Warn(ex, "playlist_property_resync_failed table=" + (bmsTable?.name ?? string.Empty) + " uri=" + uri);
                        ownerViewModel.UpdatePlaylistSyncRuntimeStatus(PlaylistSyncAttemptResult.CreateFailure(sourceTable, uri, ex));
                        ownerViewModel.ShowPlaylistLoadFailure(ex);
                    }
                    finally
                    {
                        ownerViewModel.UpdatePlaylistSyncProgressStatus(new PlaylistSyncProgressSnapshot
                        {
                            IsActive = true,
                            TotalTableCount = 1,
                            CompletedTableCount = 1,
                            CurrentTableName = bmsTable.name,
                            CurrentUri = uri
                        });
                        ownerViewModel.EndPlaylistSyncProgressOperation();
                        MainWindowViewModel.FlushPlaylistOperationNotifications(notificationScope, "playlist property external sync notification");
                    }
                    ownerViewModel.RefreshPlaylistSummaryIfVisible("playlist_property_resync", invalidateTableCountCache: true);
                }
            }
            if (prefixChanged && !externalResyncApplied)
            {
                ownerViewModel.tables.EnsurePlaylistEntriesLoaded(bmsTable, "PlaylistPropertyDialogViewModel.RewriteCompatibleFolderPrefix");
                IReadOnlyDictionary<string, string> rewrittenFolders;
                using (bmsTable.ReaderWriterLock.GetWriterGuard())
                {
                    entryFolderProjectionChanged = bmsTable.RewriteCompatibleFolderPrefix(temp_compat_prefix, bmsTable.compat_prefix, out rewrittenFolders);
                }
                if (entryFolderProjectionChanged)
                {
                    ownerViewModel.RemapCurrentPlaylistFolderSelection(bmsTable, rewrittenFolders);
                    flag = true;
                }
            }
            if (flag)
            {
                ownerViewModel.files.RefreshReferenceDisplayForTable(bmsTable);
                ownerViewModel.InvalidateNormalLibraryReferenceTableSortKeys();
                if (entryFolderProjectionChanged)
                {
                    ownerViewModel.ApplyPlaylistEntriesChanged(bmsTable, refreshSummaryIfVisible: true);
                }
                else
                {
                    ownerViewModel.RefreshPlaylistSummaryIfVisible("playlist_property_changed", invalidateTableCountCache: true);
                }
            }
            else if (externalResyncApplied)
            {
                ownerViewModel.ApplyPlaylistEntriesChanged(bmsTable, refreshSummaryIfVisible: true);
            }
            if (entryFolderProjectionChanged)
            {
                ownerViewModel.tables.CommitBMSTableWithEntriesToDB(bmsTable);
            }
            else
            {
                ownerViewModel.tables.CommitBMSTableHeaderToDB(bmsTable);
            }
            if (temp_custom_folder_output_settings?.OperationModeLR2DB == true)
            {
                string customFolderOutputDirectory = ownerViewModel.ResolveCustomFolderOutputDirectoryWithNotification(
                    bmsTable,
                    "playlist property output directory notification",
                    temp_custom_folder_output_settings);
                bool customFolderOutputBaseDirectoryBeforeResolved = temp_is_root_folder;
                string customFolderOutputBaseDirectoryBefore = temp_is_root_folder
                    ? temp_custom_folder_output_settings.LR2CustomFolderOutputBaseDirRootType
                    : null;
                if (!temp_is_root_folder)
                {
                    customFolderOutputBaseDirectoryBeforeResolved = CustomFolderOutputBaseRegistry.TryResolveNormalOutputBaseDirectory(
                        temp_custom_folder_output_base_name,
                        temp_custom_folder_output_settings.LR2CustomFolderOutputBaseDir,
                        temp_custom_folder_output_settings.LR2CustomFolderAdditionalOutputBaseDirs,
                        out customFolderOutputBaseDirectoryBefore);
                }
                try
                {
                    ownerViewModel.tables.MigrateCustomFolderOutputDirectoryWithSettings(
                        bmsTable,
                        temp_output_dir_full_path,
                        customFolderOutputDirectory,
                        wasRootFolderBefore: temp_is_root_folder,
                        rootOutputBaseDirBefore: null,
                        outputBaseDirBefore: customFolderOutputBaseDirectoryBeforeResolved ? customFolderOutputBaseDirectoryBefore : null,
                        inferOutputBaseDirBeforeWhenMissing: customFolderOutputBaseDirectoryBeforeResolved,
                        settings: temp_custom_folder_output_settings);
                }
                finally
                {
                    MainWindowViewModel.FlushPlaylistOperationNotifications(notificationScope, "playlist property custom folder notification");
                }
                List<string> bMSSearchDirectories = ownerViewModel.lr2config.GetBMSSearchDirectoriesForChangeTracking();
                if (!temp_is_root_folder && bmsTable.is_root_folder)
                {
                    ownerViewModel.lr2config.SetBMSSearchDirectories(bMSSearchDirectories.Union([customFolderOutputDirectory]).Distinct(StringComparer.OrdinalIgnoreCase));
                    ownerViewModel.lr2config.Save();
                }
                else if (temp_is_root_folder && !bmsTable.is_root_folder)
                {
                    ownerViewModel.lr2config.SetBMSSearchDirectories(bMSSearchDirectories.Except([temp_output_dir_full_path], StringComparer.OrdinalIgnoreCase));
                    ownerViewModel.lr2config.Save();
                }
                else if (temp_is_root_folder && bmsTable.is_root_folder && !customFolderOutputDirectory.Equals(temp_output_dir_full_path, StringComparison.OrdinalIgnoreCase))
                {
                    ownerViewModel.lr2config.SetBMSSearchDirectories(bMSSearchDirectories
                        .Except([temp_output_dir_full_path], StringComparer.OrdinalIgnoreCase)
                        .Union([customFolderOutputDirectory])
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                    ownerViewModel.lr2config.Save();
                }
            }
            if (outputBaseNameChanged || outputDirChanged)
            {
                ownerViewModel.RefreshPlaylistSummaryIfVisible("playlist_property_output_changed", invalidateTableCountCache: false);
            }
            bool bmtProjectionChanged = flag
                || prefixChanged
                || outputDirChanged
                || externalResyncApplied
                || entryFolderProjectionChanged
                || temp_is_external_sync != bmsTable.is_external_sync
                || !HasSameUri(temp_Page_url, bmsTable.Page_url)
                || !HasSameUri(temp_Header_url, bmsTable.Header_url)
                || !HasSameStringSequence(temp_folder_order, bmsTable.Folder_order);
            if (bmtProjectionChanged)
            {
                ownerViewModel.tables.QueueBeatorajaBmtExportForTable(bmsTable, "PlaylistPropertyDialog.SaveProperties");
            }
            backupTableProperties();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ownerViewModel.tables.FreeWriterLockBMSTables();
            }
        }
    }
}
