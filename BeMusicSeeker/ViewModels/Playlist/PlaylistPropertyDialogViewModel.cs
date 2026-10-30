using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Livet;

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
                RaisePropertyChanged(nameof(folder_sort_key_list));
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
                RaisePropertyChanged(nameof(output_dir));
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

    /// <summary>ページURLを取得・設定します。nullは明示解除とし、非nullの入力は既存の絶対URI検証・空白解除を維持します。</summary>
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
                if (value == null)
                {
                    _Page_url = null;
                }
                else if (IsUrlValid(value) && value.IsAbsoluteUri)
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

    /// <summary>既存値の単一名表示と、新しい明示出力名の検査・候補更新を行います。</summary>
    public string output_dir
    {
        get
        {
            return BMSTable.ResolveOutputDirectoryName(name, _output_dir);
        }
        set
        {
            if (!BMSTable.IsOutputDirectoryNameInputValid(value))
            {
                saveService.NotifyValidationError(PlaylistPropertyValidationError.OutputDirectoryNameMustBeSingleName);
                return;
            }
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
            ?? (normalized == null ? null : AddUnregisteredOutputBaseOption(normalized))
            ?? options.FirstOrDefault(option => !option.IsNoChange && option.BaseName == null)
            ?? options.FirstOrDefault();
    }

    private PlaylistCustomFolderOutputBaseOption AddUnregisteredOutputBaseOption(string name)
    {
        var option = new PlaylistCustomFolderOutputBaseOption(name, name);
        _outputBaseOptions = OutputBaseOptions.Append(option).ToArray();
        return option;
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
        RaisePropertyChanged(nameof(ignore_folder_output));
        if (folder_sort_key == LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL || folder_sort_key == LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE)
        {
            folder_sort_key = LR2SongDBExtended.playlist.CustomFolderSortType.NONE;
            RaisePropertyChanged(nameof(folder_sort_key));
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
        return saveService.IsOutputDirectoryValid(editSession, name, storedValue, custom_folder_output_base_option?.BaseName, is_root_folder);
    }

    internal async Task<PlaylistPropertyDialogOperationResult> ResetPropertiesAsync()
    {
        if (Interlocked.CompareExchange(ref operationInProgress, 1, 0) != 0)
        {
            return PlaylistPropertyDialogOperationResult.Busy;
        }
        try
        {
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

    /// <summary>同じPで未完了出力を先に終え、保存結果の確定参照と最新値を採用してから元失敗を通知します。継続失敗時は未保存の編集中入力を保ちます。</summary>
    internal async Task<PlaylistPropertyDialogOperationResult> SaveAndApplyAsync()
    {
        if (Interlocked.CompareExchange(ref operationInProgress, 1, 0) != 0)
        {
            return PlaylistPropertyDialogOperationResult.Busy;
        }
        PlaylistPropertySaveCommit commit = null;
        IDisposable admission = null;
        LibraryFileMutationCapability capability = null;
        try
        {
            if (!editSession.Store.TryEnterPlaylistMutation(out admission))
            {
                Volatile.Write(ref operationInProgress, 0);
                return PlaylistPropertyDialogOperationResult.Busy;
            }
            capability = editSession.Store.CreatePlaylistMutationCapability(admission);
        }
        catch
        {
            admission?.Dispose();
            Volatile.Write(ref operationInProgress, 0);
            throw;
        }
        using IDisposable acceptedOperation = admission;
        using LibraryFileMutationCapability acceptedAuthority = capability;
        try
        {
            PlaylistPropertyValues values = CreatePropertyValues();
            (commit, PlaylistPropertyValidationError? validationError) = await saveService.TrySaveAsync(editSession, values, capability);
            if (commit == null)
            {
                return validationError == PlaylistPropertyValidationError.OutputDirectoryNameMustBeSingleName
                    ? PlaylistPropertyDialogOperationResult.OutputDirectoryNameMustBeSingleName
                    : PlaylistPropertyDialogOperationResult.ValidationFailed;
            }
            editSession.Adopt(commit);
            bmsTable = editSession.Table;
            if (commit.RequestedValuesSaved) { loadTableProperties(editSession.Values); }
            // 継続失敗時は、まだDBへ保存していない編集中の値を保持する。
            // Resetの基準は結果に含まれる最新保存値へ更新済み。
            saveService.PublishSaveFailure(commit);
            return PlaylistPropertyDialogOperationResult.Completed;
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
            // 表示用getterの整形で、無変更の旧保存値を新規入力へ変えない。
            OutputDirectory = _output_dir,
            CustomFolderOutputBaseName = custom_folder_output_base_option?.BaseName,
            IsRootFolder = is_root_folder,
            CompatPrefix = compat_prefix
        };
    }

    private void loadTableProperties(PlaylistPropertyValues values)
    {
        _folder_order = new ObservableCollection<string>(values.FolderOrder ?? []);
        RaisePropertyChanged(nameof(folder_order));
        _folder_sort_key = values.FolderSortKey;
        RaisePropertyChanged(nameof(folder_sort_key));
        _folder_sort_ascending = values.FolderSortAscending;
        RaisePropertyChanged(nameof(folder_sort_ascending));
        _ignore_folder_output = values.IgnoreFolderOutput;
        RaisePropertyChanged(nameof(ignore_folder_output));
        _entry_type = values.EntryType;
        RaisePropertyChanged(nameof(entry_type));
        _name = values.Name;
        RaisePropertyChanged(nameof(name));
        _symbol = values.Symbol;
        RaisePropertyChanged(nameof(symbol));
        _Page_url = values.PageUrl;
        RaisePropertyChanged(nameof(Page_url));
        _Header_url = values.HeaderUrl;
        RaisePropertyChanged(nameof(Header_url));
        _Data_url = values.DataUrl;
        RaisePropertyChanged(nameof(Data_url));
        _is_external_sync = values.IsExternalSync;
        RaisePropertyChanged(nameof(is_external_sync));
        _output_dir = values.OutputDirectory;
        RaisePropertyChanged(nameof(output_dir));
        _outputBaseOptions = PlaylistCustomFolderOutputBaseOptions.Create(
            temp_custom_folder_output_settings?.LR2CustomFolderOutputBaseDir,
            CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(temp_custom_folder_output_settings?.LR2CustomFolderAdditionalOutputBaseDirs));
        _custom_folder_output_base_option = ResolveOutputBaseOption(values.CustomFolderOutputBaseName);
        RaisePropertyChanged(nameof(OutputBaseOptions));
        RaisePropertyChanged(nameof(custom_folder_output_base_option));
        _is_root_folder = values.IsRootFolder;
        RaisePropertyChanged(nameof(is_root_folder));
        _is_auto_folder_sort = values.IsAutoFolderSort;
        RaisePropertyChanged(nameof(is_auto_folder_sort));
        _compat_prefix = values.CompatPrefix;
        RaisePropertyChanged(nameof(compat_prefix));
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

/// <summary>画面を閉じられる終端か、入力訂正または受付の再操作が必要かを返します。</summary>
internal enum PlaylistPropertyDialogOperationResult
{
    Completed,
    ValidationFailed,
    OutputDirectoryNameMustBeSingleName,
    Busy
}
