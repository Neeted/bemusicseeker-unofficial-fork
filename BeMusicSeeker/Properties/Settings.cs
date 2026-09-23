using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Ribbit.BMS;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Ribbit.Windows;

namespace BeMusicSeeker.Properties;

[CompilerGenerated]
[GeneratedCode("Microsoft.VisualStudio.Editors.SettingsDesigner.SettingsSingleFileGenerator", "15.3.0.0")]
[SettingsProvider(typeof(PortableSettingsProvider))]
internal sealed class Settings : ApplicationSettingsBase
{
    internal const double DefaultTreeViewWidth = 250d;

    internal const double MinTreeViewWidth = 160d;

    internal const string DefaultAppearanceTheme = AppThemeService.Light;

    internal const double DefaultCustomTableFontSize = 11d;

    internal const double MinCustomTableFontSize = 8d;

    internal const double MaxCustomTableFontSize = 18d;

    internal const double DefaultCustomTableRowHeight = 19d;

    internal const double MinCustomTableRowHeight = 16d;

    internal const double MaxCustomTableRowHeight = 40d;

    internal const double DefaultCustomTableHeaderHeight = 22d;

    internal const double MinCustomTableHeaderHeight = 18d;

    internal const double MaxCustomTableHeaderHeight = 48d;

    internal const string DefaultTableListUrl = "https://script.google.com/macros/s/AKfycbzaQbcI9UZDcDlSHHl2NHilhmePrNrwxRdOFkmIXsfnbfksKKmAB3V65WZ8jPWU-7E/exec?table=tablelist";

    internal const string LegacyTableListUrl = "http://www.ribbit.xyz/bms/tables/table_info.json";

    private static Settings defaultInstance = (Settings)SettingsBase.Synchronized(new Settings());

    public static Settings Default => defaultInstance;

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("True")]
    public bool OperationModeLR2DB
    {
        get
        {
            return (bool)this["OperationModeLR2DB"];
        }
        set
        {
            this["OperationModeLR2DB"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string LR2RootPath
    {
        get
        {
            return (string)this["LR2RootPath"];
        }
        set
        {
            this["LR2RootPath"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string LR2SongDBPath
    {
        get
        {
            return (string)this["LR2SongDBPath"];
        }
        set
        {
            this["LR2SongDBPath"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string LR2ConfigXmlPath
    {
        get
        {
            return (string)this["LR2ConfigXmlPath"];
        }
        set
        {
            this["LR2ConfigXmlPath"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool UseBeatorajaScoreDb
    {
        get
        {
            return (bool)this["UseBeatorajaScoreDb"];
        }
        set
        {
            this["UseBeatorajaScoreDb"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string BeatorajaRootPath
    {
        get
        {
            return (string)this["BeatorajaRootPath"];
        }
        set
        {
            this["BeatorajaRootPath"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string BeatorajaPlayerId
    {
        get
        {
            return (string)this["BeatorajaPlayerId"];
        }
        set
        {
            this["BeatorajaPlayerId"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("True")]
    public bool RegisterBeatorajaBmtUrls
    {
        get
        {
            return (bool)this["RegisterBeatorajaBmtUrls"];
        }
        set
        {
            this["RegisterBeatorajaBmtUrls"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string BeatorajaScoreDbPath
    {
        get
        {
            return (string)this["BeatorajaScoreDbPath"];
        }
        set
        {
            this["BeatorajaScoreDbPath"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool EnableBeatorajaBmtOutput
    {
        get
        {
            return (bool)this["EnableBeatorajaBmtOutput"];
        }
        set
        {
            this["EnableBeatorajaBmtOutput"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool KeepBeatorajaBmtFilesWhenOutputDisabled
    {
        get
        {
            return (bool)this["KeepBeatorajaBmtFilesWhenOutputDisabled"];
        }
        set
        {
            this["KeepBeatorajaBmtFilesWhenOutputDisabled"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("Original")]
    public string BeatorajaBmtHashOutputMode
    {
        get
        {
            return (string)this["BeatorajaBmtHashOutputMode"];
        }
        set
        {
            this["BeatorajaBmtHashOutputMode"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string BeatorajaBmtTablePath
    {
        get
        {
            return (string)this["BeatorajaBmtTablePath"];
        }
        set
        {
            this["BeatorajaBmtTablePath"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string uBMplayPath
    {
        get
        {
            return (string)this["uBMplayPath"];
        }
        set
        {
            this["uBMplayPath"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool UsePlayeruBMplay
    {
        get
        {
            return (bool)this["UsePlayeruBMplay"];
        }
        set
        {
            this["UsePlayeruBMplay"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string BMSRootPath
    {
        get
        {
            return (string)this["BMSRootPath"];
        }
        set
        {
            this["BMSRootPath"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string StandaloneBmsRootPaths
    {
        get
        {
            return (string)this["StandaloneBmsRootPaths"];
        }
        set
        {
            this["StandaloneBmsRootPaths"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue(DefaultTableListUrl)]
    public Uri TableListURL
    {
        get
        {
            return (Uri)this["TableListURL"];
        }
        set
        {
            this["TableListURL"] = value;
        }
    }

    /// <summary>
    /// プレイリストの URL1/URL2 が空欄または既知のリンク切れサイトのときに、外部マッピングからランタイム補完を試みるかどうかを取得または設定します。
    /// </summary>
    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("True")]
    public bool EnablePlaylistUrlCompletion
    {
        get
        {
            return (bool)this["EnablePlaylistUrlCompletion"];
        }
        set
        {
            this["EnablePlaylistUrlCompletion"] = value;
        }
    }

    /// <summary>
    /// 補完値が見つかった場合に、既存の URL1/URL2 を画面表示上書きして補完値を優先するかどうかを取得または設定します。
    /// </summary>
    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool OverwritePlaylistUrlsWithCompletion
    {
        get
        {
            return (bool)this["OverwritePlaylistUrlsWithCompletion"];
        }
        set
        {
            this["OverwritePlaylistUrlsWithCompletion"] = value;
        }
    }

    /// <summary>
    /// Stella Uploader (Full) の URL1/URL2 対応をプレイリスト URL 補完に使うかどうかを取得または設定します。
    /// </summary>
    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("True")]
    public bool EnableStellaFullPlaylistUrlCompletion
    {
        get
        {
            return (bool)this["EnableStellaFullPlaylistUrlCompletion"];
        }
        set
        {
            this["EnableStellaFullPlaylistUrlCompletion"] = value;
        }
    }

    /// <summary>
    /// MD5 と URL の対応 TSV を取得する URI またはローカルファイルパスを取得または設定します。
    /// </summary>
    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("https://raw.githubusercontent.com/Neeted/bemusicseeker-unofficial-fork/main/bms-md5-url-map.tsv")]
    public string PlaylistMd5UrlMappingTsvUri
    {
        get
        {
            return (string)this["PlaylistMd5UrlMappingTsvUri"];
        }
        set
        {
            this["PlaylistMd5UrlMappingTsvUri"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string LR2CustomFolderOutputBaseDir
    {
        get
        {
            return (string)this["LR2CustomFolderOutputBaseDir"];
        }
        set
        {
            this["LR2CustomFolderOutputBaseDir"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("[]")]
    public string LR2CustomFolderAdditionalOutputBaseDirs
    {
        get
        {
            return (string)this["LR2CustomFolderAdditionalOutputBaseDirs"];
        }
        set
        {
            this["LR2CustomFolderAdditionalOutputBaseDirs"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string LR2CustomFolderOutputBaseDirRootType
    {
        get
        {
            return (string)this["LR2CustomFolderOutputBaseDirRootType"];
        }
        set
        {
            this["LR2CustomFolderOutputBaseDirRootType"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("102")]
    public int PlaylistDefaultIgnoreFolderOutput
    {
        get
        {
            return (int)this["PlaylistDefaultIgnoreFolderOutput"];
        }
        set
        {
            this["PlaylistDefaultIgnoreFolderOutput"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue(null)]
    public SerializableVersion AssemblyVersion
    {
        get
        {
            return (SerializableVersion)this["AssemblyVersion"];
        }
        set
        {
            this["AssemblyVersion"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("50")]
    public int uBMplayVolume
    {
        get
        {
            return (int)this["uBMplayVolume"];
        }
        set
        {
            this["uBMplayVolume"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("True")]
    public bool FolderSkipPlayMode
    {
        get
        {
            return (bool)this["FolderSkipPlayMode"];
        }
        set
        {
            this["FolderSkipPlayMode"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool RepeatPlayMode
    {
        get
        {
            return (bool)this["RepeatPlayMode"];
        }
        set
        {
            this["RepeatPlayMode"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool SinglePlayMode
    {
        get
        {
            return (bool)this["SinglePlayMode"];
        }
        set
        {
            this["SinglePlayMode"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    public CustomTableColumnSettings StandardCustomTableColumnSettings
    {
        get
        {
            return (CustomTableColumnSettings)this["StandardCustomTableColumnSettings"];
        }
        set
        {
            this["StandardCustomTableColumnSettings"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    public CustomTableColumnSettings UnregisteredCustomTableColumnSettings
    {
        get
        {
            return (CustomTableColumnSettings)this["UnregisteredCustomTableColumnSettings"];
        }
        set
        {
            this["UnregisteredCustomTableColumnSettings"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    public CustomTableColumnSettings ZeroNoteCustomTableColumnSettings
    {
        get
        {
            return (CustomTableColumnSettings)this["ZeroNoteCustomTableColumnSettings"];
        }
        set
        {
            this["ZeroNoteCustomTableColumnSettings"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    public CustomTableColumnSettings PlaylistCustomTableColumnSettings
    {
        get
        {
            return (CustomTableColumnSettings)this["PlaylistCustomTableColumnSettings"];
        }
        set
        {
            this["PlaylistCustomTableColumnSettings"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    public CustomTableColumnSettings FullScanCustomTableColumnSettings
    {
        get
        {
            return (CustomTableColumnSettings)this["FullScanCustomTableColumnSettings"];
        }
        set
        {
            this["FullScanCustomTableColumnSettings"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    public CustomTableColumnSettings DuplicateCustomTableColumnSettings
    {
        get
        {
            return (CustomTableColumnSettings)this["DuplicateCustomTableColumnSettings"];
        }
        set
        {
            this["DuplicateCustomTableColumnSettings"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    public CustomTableColumnSettings EncodingCustomTableColumnSettings
    {
        get
        {
            return (CustomTableColumnSettings)this["EncodingCustomTableColumnSettings"];
        }
        set
        {
            this["EncodingCustomTableColumnSettings"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    public CustomTableColumnSettings InstallCustomTableColumnSettings
    {
        get
        {
            return (CustomTableColumnSettings)this["InstallCustomTableColumnSettings"];
        }
        set
        {
            this["InstallCustomTableColumnSettings"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    public CustomTableColumnSettings ChartInfoParseErrorCustomTableColumnSettings
    {
        get
        {
            return (CustomTableColumnSettings)this["ChartInfoParseErrorCustomTableColumnSettings"];
        }
        set
        {
            this["ChartInfoParseErrorCustomTableColumnSettings"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    public CustomTableColumnSettings PlayHistoryCustomTableColumnSettings
    {
        get
        {
            return (CustomTableColumnSettings)this["PlayHistoryCustomTableColumnSettings"];
        }
        set
        {
            this["PlayHistoryCustomTableColumnSettings"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string BMSInstallDir
    {
        get
        {
            return (string)this["BMSInstallDir"];
        }
        set
        {
            this["BMSInstallDir"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    public Win32API.WINDOWPLACEMENT WindowPlacement
    {
        get
        {
            return (Win32API.WINDOWPLACEMENT)this["WindowPlacement"];
        }
        set
        {
            this["WindowPlacement"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("250")]
    public double TreeViewWidth
    {
        get
        {
            return NormalizeTreeViewWidth((double)this["TreeViewWidth"]);
        }
        set
        {
            this["TreeViewWidth"] = NormalizeTreeViewWidth(value);
        }
    }

    internal static double NormalizeTreeViewWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width < MinTreeViewWidth)
        {
            return DefaultTreeViewWidth;
        }
        return width;
    }

    internal static double NormalizeRange(double value, double minimum, double maximum, double defaultValue)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return defaultValue;
        }
        return Math.Max(minimum, Math.Min(maximum, value));
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool UsePlayerLR2body
    {
        get
        {
            return (bool)this["UsePlayerLR2body"];
        }
        set
        {
            this["UsePlayerLR2body"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("640,360")]
    public Point LR2bodyResolution
    {
        get
        {
            return (Point)this["LR2bodyResolution"];
        }
        set
        {
            this["LR2bodyResolution"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    public Win32API.WINDOWPLACEMENT LR2bodyWindowPlacement
    {
        get
        {
            return (Win32API.WINDOWPLACEMENT)this["LR2bodyWindowPlacement"];
        }
        set
        {
            this["LR2bodyWindowPlacement"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("True")]
    public bool IsSaveLR2bodyWindowPosition
    {
        get
        {
            return (bool)this["IsSaveLR2bodyWindowPosition"];
        }
        set
        {
            this["IsSaveLR2bodyWindowPosition"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string BMIIDXViewPath
    {
        get
        {
            return (string)this["BMIIDXViewPath"];
        }
        set
        {
            this["BMIIDXViewPath"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool UsePlayerBMIIDXView
    {
        get
        {
            return (bool)this["UsePlayerBMIIDXView"];
        }
        set
        {
            this["UsePlayerBMIIDXView"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool IsLR2BackupEnabled
    {
        get
        {
            return (bool)this["IsLR2BackupEnabled"];
        }
        set
        {
            this["IsLR2BackupEnabled"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("All")]
    public Backup.Target LR2BackupTarget
    {
        get
        {
            return (Backup.Target)this["LR2BackupTarget"];
        }
        set
        {
            this["LR2BackupTarget"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string LR2BackupPath
    {
        get
        {
            return (string)this["LR2BackupPath"];
        }
        set
        {
            this["LR2BackupPath"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("7")]
    public int LR2BackupSpan
    {
        get
        {
            return (int)this["LR2BackupSpan"];
        }
        set
        {
            this["LR2BackupSpan"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("1")]
    public int LR2BackupNum
    {
        get
        {
            return (int)this["LR2BackupNum"];
        }
        set
        {
            this["LR2BackupNum"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("TITLE_SMALL")]
    public PlayerPanelState PlayerPanelState
    {
        get
        {
            return (PlayerPanelState)this["PlayerPanelState"];
        }
        set
        {
            this["PlayerPanelState"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool UseExternalPanelImage
    {
        get
        {
            return (bool)this["UseExternalPanelImage"];
        }
        set
        {
            this["UseExternalPanelImage"] = value;
        }
    }

    [UserScopedSetting]
    [DefaultSettingValue(DefaultAppearanceTheme)]
    public string AppearanceTheme
    {
        get
        {
            string rawTheme = (string)this["AppearanceTheme"];
            string normalizedTheme = AppThemeService.NormalizeTheme(rawTheme);
            if (!string.Equals(rawTheme, normalizedTheme, StringComparison.Ordinal))
            {
                this["AppearanceTheme"] = normalizedTheme;
            }
            return normalizedTheme;
        }
        set
        {
            this["AppearanceTheme"] = AppThemeService.NormalizeTheme(value);
        }
    }

    [UserScopedSetting]
    [DefaultSettingValue("11")]
    public double CustomTableFontSize
    {
        get
        {
            double value = NormalizeRange((double)this["CustomTableFontSize"], MinCustomTableFontSize, MaxCustomTableFontSize, DefaultCustomTableFontSize);
            if (!((double)this["CustomTableFontSize"]).Equals(value))
            {
                this["CustomTableFontSize"] = value;
            }
            return value;
        }
        set
        {
            this["CustomTableFontSize"] = NormalizeRange(value, MinCustomTableFontSize, MaxCustomTableFontSize, DefaultCustomTableFontSize);
        }
    }

    [UserScopedSetting]
    [DefaultSettingValue("19")]
    public double CustomTableRowHeight
    {
        get
        {
            double value = NormalizeRange((double)this["CustomTableRowHeight"], MinCustomTableRowHeight, MaxCustomTableRowHeight, DefaultCustomTableRowHeight);
            if (!((double)this["CustomTableRowHeight"]).Equals(value))
            {
                this["CustomTableRowHeight"] = value;
            }
            return value;
        }
        set
        {
            this["CustomTableRowHeight"] = NormalizeRange(value, MinCustomTableRowHeight, MaxCustomTableRowHeight, DefaultCustomTableRowHeight);
        }
    }

    [UserScopedSetting]
    [DefaultSettingValue("22")]
    public double CustomTableHeaderHeight
    {
        get
        {
            double value = NormalizeRange((double)this["CustomTableHeaderHeight"], MinCustomTableHeaderHeight, MaxCustomTableHeaderHeight, DefaultCustomTableHeaderHeight);
            if (!((double)this["CustomTableHeaderHeight"]).Equals(value))
            {
                this["CustomTableHeaderHeight"] = value;
            }
            return value;
        }
        set
        {
            this["CustomTableHeaderHeight"] = NormalizeRange(value, MinCustomTableHeaderHeight, MaxCustomTableHeaderHeight, DefaultCustomTableHeaderHeight);
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string StagefilePath
    {
        get
        {
            return (string)this["StagefilePath"];
        }
        set
        {
            this["StagefilePath"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("True")]
    public bool UseOnlyShiftJISChars
    {
        get
        {
            return (bool)this["UseOnlyShiftJISChars"];
        }
        set
        {
            this["UseOnlyShiftJISChars"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("[%ARTIST%] %TITLE%")]
    public string FolderNameFormat
    {
        get
        {
            return (string)this["FolderNameFormat"];
        }
        set
        {
            this["FolderNameFormat"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("True")]
    public bool ShowScoreViewerRegisterConfirmMsg
    {
        get
        {
            return (bool)this["ShowScoreViewerRegisterConfirmMsg"];
        }
        set
        {
            this["ShowScoreViewerRegisterConfirmMsg"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("True")]
    public bool ShowDiffBMSInstallConfirmMsg
    {
        get
        {
            return (bool)this["ShowDiffBMSInstallConfirmMsg"];
        }
        set
        {
            this["ShowDiffBMSInstallConfirmMsg"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("True")]
    public bool ShowDuplicateFileCheckConfirmMsg
    {
        get
        {
            return (bool)this["ShowDuplicateFileCheckConfirmMsg"];
        }
        set
        {
            this["ShowDuplicateFileCheckConfirmMsg"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("True")]
    public bool ShowRecommUpdatedMsg
    {
        get
        {
            return (bool)this["ShowRecommUpdatedMsg"];
        }
        set
        {
            this["ShowRecommUpdatedMsg"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("True")]
    public bool ScanBmsFilesOnStartup
    {
        get
        {
            return (bool)this["ScanBmsFilesOnStartup"];
        }
        set
        {
            this["ScanBmsFilesOnStartup"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool SkipInitPlaylistLoad
    {
        get
        {
            return (bool)this["SkipInitPlaylistLoad"];
        }
        set
        {
            this["SkipInitPlaylistLoad"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    public PlaylistSummaryColumnSettings PlaylistSummaryColumnsSettings
    {
        get
        {
            return (PlaylistSummaryColumnSettings)this["PlaylistSummaryColumnsSettings"];
        }
        set
        {
            this["PlaylistSummaryColumnsSettings"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("True")]
    public bool StartupSelectInstallPending
    {
        get
        {
            return (bool)this["StartupSelectInstallPending"];
        }
        set
        {
            this["StartupSelectInstallPending"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("True")]
    public bool EnableReadOptimizedPragmas
    {
        get
        {
            return (bool)this["EnableReadOptimizedPragmas"];
        }
        set
        {
            this["EnableReadOptimizedPragmas"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool EstimateOfflineScoreRanking
    {
        get
        {
            return (bool)this["EstimateOfflineScoreRanking"];
        }
        set
        {
            this["EstimateOfflineScoreRanking"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool UpdateLr2IrRankingCacheOnStartup
    {
        get
        {
            return (bool)this["UpdateLr2IrRankingCacheOnStartup"];
        }
        set
        {
            this["UpdateLr2IrRankingCacheOnStartup"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool EnableDownloadLr2IrScoreAndDetectUnsent
    {
        get
        {
            return (bool)this["EnableDownloadLr2IrScoreAndDetectUnsent"];
        }
        set
        {
            this["EnableDownloadLr2IrScoreAndDetectUnsent"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("0")]
    public EncoderType Encoder
    {
        get
        {
            return (EncoderType)this["Encoder"];
        }
        set
        {
            this["Encoder"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("0")]
    public SampleRate EncoderSampleRate
    {
        get
        {
            return (SampleRate)this["EncoderSampleRate"];
        }
        set
        {
            this["EncoderSampleRate"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("0")]
    public SampleFormat EncoderFormat
    {
        get
        {
            return (SampleFormat)this["EncoderFormat"];
        }
        set
        {
            this["EncoderFormat"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("0")]
    public BMSAutoPlayWriter.Normalization EncoderNormalization
    {
        get
        {
            return (BMSAutoPlayWriter.Normalization)this["EncoderNormalization"];
        }
        set
        {
            this["EncoderNormalization"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("0.8")]
    public float EncoderQuality
    {
        get
        {
            return (float)this["EncoderQuality"];
        }
        set
        {
            this["EncoderQuality"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string EncoderExeDir
    {
        get
        {
            return (string)this["EncoderExeDir"];
        }
        set
        {
            this["EncoderExeDir"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("1")]
    public float EncoderAmplifier
    {
        get
        {
            return (float)this["EncoderAmplifier"];
        }
        set
        {
            this["EncoderAmplifier"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("[%ARTIST%] %TITLE%")]
    public string EncodeFileNameFormat
    {
        get
        {
            return (string)this["EncodeFileNameFormat"];
        }
        set
        {
            this["EncodeFileNameFormat"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("1")]
    public BassAudioPlayer.DeviceDriver PlayerDriver
    {
        get
        {
            return (BassAudioPlayer.DeviceDriver)this["PlayerDriver"];
        }
        set
        {
            this["PlayerDriver"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string PlayerDevice
    {
        get
        {
            return (string)this["PlayerDevice"];
        }
        set
        {
            this["PlayerDevice"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("0")]
    public SampleRate PlayerSampleRate
    {
        get
        {
            return (SampleRate)this["PlayerSampleRate"];
        }
        set
        {
            this["PlayerSampleRate"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("0")]
    public SampleFormat PlayerFormat
    {
        get
        {
            return (SampleFormat)this["PlayerFormat"];
        }
        set
        {
            this["PlayerFormat"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("0")]
    public float PlayerBufferSize
    {
        get
        {
            return (float)this["PlayerBufferSize"];
        }
        set
        {
            this["PlayerBufferSize"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string PlayerDeviceName
    {
        get
        {
            return (string)this["PlayerDeviceName"];
        }
        set
        {
            this["PlayerDeviceName"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool PlayerWASAPIParam
    {
        get
        {
            return (bool)this["PlayerWASAPIParam"];
        }
        set
        {
            this["PlayerWASAPIParam"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("ja-JP")]
    public string Lang
    {
        get
        {
            return (string)this["Lang"];
        }
        set
        {
            this["Lang"] = value;
        }
    }

    // 言語ドロップダウンの表示名を保持する（同一カルチャ名の重複対策）
    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string LangDisplayName
    {
        get
        {
            return (string)this["LangDisplayName"];
        }
        set
        {
            this["LangDisplayName"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool AutoInstall
    {
        get
        {
            return (bool)this["AutoInstall"];
        }
        set
        {
            this["AutoInstall"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool KeepInstallablePackagesPending
    {
        get
        {
            return (bool)this["KeepInstallablePackagesPending"];
        }
        set
        {
            this["KeepInstallablePackagesPending"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("0")]
    public int PendingInstallEstimateMaxParallelPackages
    {
        get
        {
            return (int)this["PendingInstallEstimateMaxParallelPackages"];
        }
        set
        {
            this["PendingInstallEstimateMaxParallelPackages"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool AutoApplyAmbiguousInstallDestination
    {
        get
        {
            return (bool)this["AutoApplyAmbiguousInstallDestination"];
        }
        set
        {
            this["AutoApplyAmbiguousInstallDestination"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string KeywordSearchHistory
    {
        get
        {
            return (string)this["KeywordSearchHistory"];
        }
        set
        {
            this["KeywordSearchHistory"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string PlaylistSummaryKeywordSearchHistory
    {
        get
        {
            return (string)this["PlaylistSummaryKeywordSearchHistory"];
        }
        set
        {
            this["PlaylistSummaryKeywordSearchHistory"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string KeywordSearchFavorites
    {
        get
        {
            return (string)this["KeywordSearchFavorites"];
        }
        set
        {
            this["KeywordSearchFavorites"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string PlaylistSummaryKeywordSearchFavorites
    {
        get
        {
            return (string)this["PlaylistSummaryKeywordSearchFavorites"];
        }
        set
        {
            this["PlaylistSummaryKeywordSearchFavorites"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string PlayHistoryDisplayTargetSetsJson
    {
        get
        {
            return (string)this["PlayHistoryDisplayTargetSetsJson"];
        }
        set
        {
            this["PlayHistoryDisplayTargetSetsJson"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("")]
    public string PlayHistorySelectedDisplayTargetIdentity
    {
        get
        {
            return (string)this["PlayHistorySelectedDisplayTargetIdentity"];
        }
        set
        {
            this["PlayHistorySelectedDisplayTargetIdentity"] = value;
        }
    }

    /// <summary>
    /// 右クリック外部 action の web/program 定義を一つの atomic JSON として取得または設定します。
    /// </summary>
    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue(RightClickActionSettingsDefaults.SerializedJson)]
    public string RightClickActionsJson
    {
        get
        {
            return (string)this["RightClickActionsJson"];
        }
        set
        {
            this["RightClickActionsJson"] = value;
        }
    }

    /// <summary>
    /// 推定先への通常インストール後に、元の保留パッケージフォルダを残り物ごと削除するかどうかを取得または設定します。
    /// </summary>
    /// <remarks>
    /// 既定値は false で、既所持譜面などが source に残る場合は従来どおりフォルダ削除をスキップします。
    /// </remarks>
    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool DeletePendingPackageSourceAfterInstall
    {
        get
        {
            return (bool)this["DeletePendingPackageSourceAfterInstall"];
        }
        set
        {
            this["DeletePendingPackageSourceAfterInstall"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("True")]
    public bool EnableSmartComponentOverwrite
    {
        get
        {
            return (bool)this["EnableSmartComponentOverwrite"];
        }
        set
        {
            this["EnableSmartComponentOverwrite"] = value;
        }
    }

    [UserScopedSetting]
    [DebuggerNonUserCode]
    [DefaultSettingValue("False")]
    public bool KeepSmartOverwriteProtectedFilesByRenaming
    {
        get
        {
            return (bool)this["KeepSmartOverwriteProtectedFilesByRenaming"];
        }
        set
        {
            this["KeepSmartOverwriteProtectedFilesByRenaming"] = value;
        }
    }

    public Settings()
    {
        base.SettingsLoaded += SettingsLoadedEventHandler;
    }

    private void SettingChangingEventHandler(object sender, SettingChangingEventArgs e)
    {
    }

    private void SettingsSavingEventHandler(object sender, CancelEventArgs e)
    {
    }

    private void SettingsLoadedEventHandler(object sender, SettingsLoadedEventArgs e)
    {
        Settings settings = (Settings)sender;
        double rawTreeViewWidth = (double)settings["TreeViewWidth"];
        double normalizedTreeViewWidth = NormalizeTreeViewWidth(rawTreeViewWidth);
        if (!normalizedTreeViewWidth.Equals(rawTreeViewWidth))
        {
            settings["TreeViewWidth"] = normalizedTreeViewWidth;
        }
        string rawAppearanceTheme = settings["AppearanceTheme"] as string;
        string normalizedAppearanceTheme = AppThemeService.NormalizeTheme(rawAppearanceTheme);
        if (!string.Equals(rawAppearanceTheme, normalizedAppearanceTheme, StringComparison.Ordinal))
        {
            settings["AppearanceTheme"] = normalizedAppearanceTheme;
        }
        NormalizeDoubleSetting(settings, "CustomTableFontSize", MinCustomTableFontSize, MaxCustomTableFontSize, DefaultCustomTableFontSize);
        NormalizeDoubleSetting(settings, "CustomTableRowHeight", MinCustomTableRowHeight, MaxCustomTableRowHeight, DefaultCustomTableRowHeight);
        NormalizeDoubleSetting(settings, "CustomTableHeaderHeight", MinCustomTableHeaderHeight, MaxCustomTableHeaderHeight, DefaultCustomTableHeaderHeight);
        EnsureColumnSettingDefaults(settings);
        if (settings.WindowPlacement.NormalPosition.Left >= settings.WindowPlacement.NormalPosition.Right || settings.WindowPlacement.NormalPosition.Top >= settings.WindowPlacement.NormalPosition.Bottom)
        {
            Win32API.WINDOWPLACEMENT windowPlacement = settings.WindowPlacement;
            windowPlacement.NormalPosition = new Win32API.RECT(0, 0, 1000, 800);
            settings.WindowPlacement = windowPlacement;
        }
    }

    private static void NormalizeDoubleSetting(Settings settings, string key, double minimum, double maximum, double defaultValue)
    {
        double rawValue = settings[key] is double value ? value : defaultValue;
        double normalizedValue = NormalizeRange(rawValue, minimum, maximum, defaultValue);
        if (!normalizedValue.Equals(rawValue))
        {
            settings[key] = normalizedValue;
        }
    }

    internal static void EnsureColumnSettingDefaults(Settings settings)
    {
        if (settings.StandardCustomTableColumnSettings == null)
        {
            settings.StandardCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        }
        if (settings.UnregisteredCustomTableColumnSettings == null)
        {
            settings.UnregisteredCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.UNREGISTERED);
        }
        if (settings.ZeroNoteCustomTableColumnSettings == null)
        {
            settings.ZeroNoteCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.ZERO_NOTE);
        }
        if (settings.PlaylistCustomTableColumnSettings == null)
        {
            settings.PlaylistCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAYLIST);
        }
        if (settings.FullScanCustomTableColumnSettings == null)
        {
            settings.FullScanCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.FULLSCAN);
        }
        if (settings.DuplicateCustomTableColumnSettings == null)
        {
            settings.DuplicateCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.DUPLICATE);
        }
        if (settings.EncodingCustomTableColumnSettings == null)
        {
            settings.EncodingCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.ENCODING);
        }
        if (settings.InstallCustomTableColumnSettings == null)
        {
            settings.InstallCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.INSTALL);
        }
        if (settings.ChartInfoParseErrorCustomTableColumnSettings == null)
        {
            settings.ChartInfoParseErrorCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.CHART_INFO_PARSE_ERROR);
        }
        if (settings.PlayHistoryCustomTableColumnSettings == null)
        {
            settings.PlayHistoryCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
        }
        if (settings.PlaylistSummaryColumnsSettings == null)
        {
            settings.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
        }
        settings.StandardCustomTableColumnSettings.EnsureChartInfoColumnDefaults(CustomTableColumnSettings.ViewKind.STANDARD);
        settings.UnregisteredCustomTableColumnSettings.EnsureChartInfoColumnDefaults(CustomTableColumnSettings.ViewKind.UNREGISTERED);
        settings.ZeroNoteCustomTableColumnSettings.EnsureChartInfoColumnDefaults(CustomTableColumnSettings.ViewKind.ZERO_NOTE);
        settings.PlaylistCustomTableColumnSettings.EnsureChartInfoColumnDefaults(CustomTableColumnSettings.ViewKind.PLAYLIST);
        settings.FullScanCustomTableColumnSettings.EnsureChartInfoColumnDefaults(CustomTableColumnSettings.ViewKind.FULLSCAN);
        settings.DuplicateCustomTableColumnSettings.EnsureChartInfoColumnDefaults(CustomTableColumnSettings.ViewKind.DUPLICATE);
        settings.EncodingCustomTableColumnSettings.EnsureChartInfoColumnDefaults(CustomTableColumnSettings.ViewKind.ENCODING);
        settings.InstallCustomTableColumnSettings.EnsureChartInfoColumnDefaults(CustomTableColumnSettings.ViewKind.INSTALL);
        settings.ChartInfoParseErrorCustomTableColumnSettings.EnsureChartInfoColumnDefaults(CustomTableColumnSettings.ViewKind.CHART_INFO_PARSE_ERROR);
        settings.PlayHistoryCustomTableColumnSettings.EnsurePlayHistoryColumnDefaults();
    }
}
