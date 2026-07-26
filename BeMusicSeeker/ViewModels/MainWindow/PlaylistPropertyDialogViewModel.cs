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
/// Holds editable playlist table properties for one playlist property edit session.
/// </summary>
public sealed partial class PlaylistPropertyDialogViewModel : ViewModel
{
    private BMSTable bmsTable;

    private readonly PlaylistPropertySaveService saveService;

    private PlaylistPropertyEditSession editSession;

    private readonly bool isForNewTable;

    private int operationInProgress;

    private PlaylistPropertySaveCommit incompletePostSaveCommit;

    private ObservableCollection<string> _folder_order;

    private LR2SongDBExtended.playlist.CustomFolderSortType _folder_sort_key;

    private LR2SongDBExtended.playlist.EntryUnitType _entry_type;

    private string _compat_prefix;

    private bool _folder_sort_ascending;

    private LR2SongDBExtended.playlist.CustomFolderType _ignore_folder_output;

    private string _name;

    private string _symbol;

    private Uri _Page_url;

    private Uri _Header_url;

    private Uri _Data_url;

    private bool _is_external_sync;

    private bool _is_root_folder;

    private readonly CustomFolderOutputSettingsSnapshot temp_custom_folder_output_settings;

    private string _output_dir;

    private PlaylistCustomFolderOutputBaseOption _custom_folder_output_base_option;

    private bool _is_auto_folder_sort;

    private IReadOnlyList<PlaylistCustomFolderOutputBaseOption> _outputBaseOptions;

    public IReadOnlyList<PlaylistCustomFolderOutputBaseOption> OutputBaseOptions =>
        _outputBaseOptions ??= PlaylistCustomFolderOutputBaseOptions.Create(
            temp_custom_folder_output_settings?.LR2CustomFolderOutputBaseDir,
            CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(temp_custom_folder_output_settings?.LR2CustomFolderAdditionalOutputBaseDirs));

    public bool OperationModeLR2DB => temp_custom_folder_output_settings?.OperationModeLR2DB == true;

    public ObservableCollection<string> folder_order
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
                    saveService.NotifyValidationError(PlaylistPropertyValidationError.OutputDirectoryChangedByPlaylistName);
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
                    saveService.NotifyValidationError(PlaylistPropertyValidationError.InvalidPageUri);
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
                    saveService.NotifyValidationError(PlaylistPropertyValidationError.InvalidHeaderUri);
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
                    saveService.NotifyValidationError(PlaylistPropertyValidationError.InvalidDataUri);
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
                saveService.NotifyValidationError(PlaylistPropertyValidationError.InvalidExternalSyncUris);
            }
            if (!_is_external_sync && value)
            {
                if (!saveService.ConfirmExternalSyncChange(enable: true))
                {
                    RaisePropertyChanged("is_external_sync");
                    return;
                }
            }
            else if (_is_external_sync && !value)
            {
                if (!saveService.ConfirmExternalSyncChange(enable: false))
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
                    saveService.NotifyValidationError(PlaylistPropertyValidationError.InvalidOutputDirectory);
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

    internal PlaylistPropertyDialogViewModel(
        PlaylistPropertySaveService service,
        PlaylistPropertyEditSession preparedSession)
    {
        saveService = service ?? throw new ArgumentNullException(nameof(service));
        editSession = preparedSession ?? throw new ArgumentNullException(nameof(preparedSession));
        bmsTable = editSession.Table;
        isForNewTable = editSession.IsNewTable;
        temp_custom_folder_output_settings = editSession.Settings;
        loadTableProperties(editSession.Values);
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
        string storedValue = PlaylistPropertySaveService.NormalizeSummaryOutputDirectory(name, value);
        return saveService.IsOutputDirectoryValid(editSession, name, storedValue);
    }

    internal async Task<PlaylistPropertyDialogOperationResult> ResetPropertiesAsync()
    {
        if (Interlocked.CompareExchange(ref operationInProgress, 1, 0) != 0)
        {
            return PlaylistPropertyDialogOperationResult.Busy;
        }
        try
        {
            if (incompletePostSaveCommit != null)
            {
                throw new InvalidOperationException(
                    "Playlist save follow-up is incomplete. Retry Save before closing the dialog.");
            }
            PlaylistPropertyValues values = await saveService.ResetAsync(editSession);
            loadTableProperties(values);
            return isForNewTable || CheckValidation()
                ? PlaylistPropertyDialogOperationResult.Completed
                : PlaylistPropertyDialogOperationResult.ValidationFailed;
        }
        finally
        {
            Volatile.Write(ref operationInProgress, 0);
        }
    }

    public bool CheckValidation()
    {
        return saveService.IsValid(editSession, CreatePropertyValues());
    }

    internal async Task<PlaylistPropertyDialogOperationResult> SaveAndApplyAsync()
    {
        if (Interlocked.CompareExchange(ref operationInProgress, 1, 0) != 0)
        {
            return PlaylistPropertyDialogOperationResult.Busy;
        }
        PlaylistPropertySaveCommit commit = null;
        try
        {
            PlaylistPropertyValues values = CreatePropertyValues();
            if (incompletePostSaveCommit != null)
            {
                if (!PlaylistPropertyValues.ContentEquals(
                    incompletePostSaveCommit.AppliedValues,
                    values))
                {
                    throw new InvalidOperationException(
                        "Playlist save follow-up must be retried before changing properties again.");
                }
                if (!await saveService.IsRetryTargetCurrentAsync(
                    editSession,
                    incompletePostSaveCommit))
                {
                    throw new InvalidOperationException(
                        "Playlist properties changed while save follow-up was pending. Reopen the dialog before saving again.");
                }
                commit = incompletePostSaveCommit;
            }
            else
            {
                commit = await saveService.TrySaveAsync(editSession, values);
                if (commit == null)
                {
                    return PlaylistPropertyDialogOperationResult.ValidationFailed;
                }
            }
            await Task.Run(() => saveService.ApplyPostSaveUpdatesAsync(commit));
            bmsTable = commit.Table;
            incompletePostSaveCommit = null;
            return PlaylistPropertyDialogOperationResult.Completed;
        }
        catch (Exception saveFailure) when (commit != null)
        {
            incompletePostSaveCommit = commit;
            try
            {
                PlaylistPropertyEditSession reconciledSession =
                    await saveService.ReconcileFailedSaveAsync(editSession, commit);
                editSession.Dispose();
                editSession = reconciledSession;
                bmsTable = reconciledSession.Table;
                loadTableProperties(reconciledSession.Values);
                commit.AppliedValues = CreatePropertyValues();
            }
            catch (Exception reconciliationFailure)
            {
                throw new AggregateException(saveFailure, reconciliationFailure).Flatten();
            }
            ExceptionDispatchInfo.Capture(saveFailure).Throw();
            throw new InvalidOperationException("Playlist save failure propagation unexpectedly returned.");
        }
        finally
        {
            Volatile.Write(ref operationInProgress, 0);
        }
    }

    private PlaylistPropertyValues CreatePropertyValues()
    {
        return new PlaylistPropertyValues
        {
            FolderOrder = [.. folder_order],
            IsAutoFolderSort = is_auto_folder_sort,
            FolderSortKey = folder_sort_key,
            FolderSortAscending = folder_sort_ascending,
            IgnoreFolderOutput = ignore_folder_output,
            EntryType = entry_type,
            Name = name,
            Symbol = symbol,
            PageUrl = Page_url,
            HeaderUrl = Header_url,
            DataUrl = Data_url,
            IsExternalSync = is_external_sync,
            OutputDirectory = output_dir,
            CustomFolderOutputBaseName = custom_folder_output_base_option?.BaseName,
            IsRootFolder = is_root_folder,
            CompatPrefix = compat_prefix
        };
    }

    private void loadTableProperties(PlaylistPropertyValues values)
    {
        _folder_order = new ObservableCollection<string>(values.FolderOrder ?? []);
        RaisePropertyChanged(() => folder_order);
        _folder_sort_key = values.FolderSortKey;
        RaisePropertyChanged(() => folder_sort_key);
        _folder_sort_ascending = values.FolderSortAscending;
        RaisePropertyChanged(() => folder_sort_ascending);
        _ignore_folder_output = values.IgnoreFolderOutput;
        RaisePropertyChanged(() => ignore_folder_output);
        _entry_type = values.EntryType;
        RaisePropertyChanged(() => entry_type);
        _name = values.Name;
        RaisePropertyChanged(() => name);
        _symbol = values.Symbol;
        RaisePropertyChanged(() => symbol);
        _Page_url = values.PageUrl;
        RaisePropertyChanged(() => Page_url);
        _Header_url = values.HeaderUrl;
        RaisePropertyChanged(() => Header_url);
        _Data_url = values.DataUrl;
        RaisePropertyChanged(() => Data_url);
        _is_external_sync = values.IsExternalSync;
        RaisePropertyChanged(() => is_external_sync);
        _output_dir = values.OutputDirectory;
        RaisePropertyChanged(() => output_dir);
        _outputBaseOptions = PlaylistCustomFolderOutputBaseOptions.Create(
            temp_custom_folder_output_settings?.LR2CustomFolderOutputBaseDir,
            CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(temp_custom_folder_output_settings?.LR2CustomFolderAdditionalOutputBaseDirs));
        _custom_folder_output_base_option = ResolveOutputBaseOption(values.CustomFolderOutputBaseName);
        RaisePropertyChanged(() => OutputBaseOptions);
        RaisePropertyChanged(() => custom_folder_output_base_option);
        _is_root_folder = values.IsRootFolder;
        RaisePropertyChanged(() => is_root_folder);
        _is_auto_folder_sort = values.IsAutoFolderSort;
        RaisePropertyChanged(() => is_auto_folder_sort);
        _compat_prefix = values.CompatPrefix;
        RaisePropertyChanged(() => compat_prefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            editSession?.Dispose();
            editSession = null;
        }
    }
}

internal enum PlaylistPropertyDialogOperationResult
{
    Completed,
    ValidationFailed,
    Busy
}
