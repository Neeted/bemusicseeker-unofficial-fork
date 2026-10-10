using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 捕捉した起動入力からLibraryとPlaylistの組を構築します。
/// </summary>
internal interface IStartupLibraryFactory
{
    /// <summary>
    /// 変更不能な起動profileからLibraryを構築します。
    /// </summary>
    /// <param name="libraryProfile">今回の起動で捕捉したprofile。</param>
    /// <returns>構築したLibrary。</returns>
    BMSLibrary CreateBmsLibrary(LibraryProfile libraryProfile);

    /// <summary>
    /// 同じprofileから構築したLibraryへPlaylistを接続します。
    /// </summary>
    /// <param name="libraryProfile">今回の起動で捕捉したprofile。</param>
    /// <param name="library">同じ<paramref name="libraryProfile"/>から構築したLibrary。</param>
    /// <returns>構築したPlaylist。</returns>
    BMSPlaylist CreateBmsPlaylist(LibraryProfile libraryProfile, BMSLibrary library);
}

/// <summary>
/// 構築済みの組を画面側の利用主体へ接続します。
/// </summary>
internal interface IStartupLibraryApplicationPort
{
    /// <summary>
    /// 組の構築成功後にLibraryを接続し、受理済み初期化の権限を同じ操作へ渡します。
    /// </summary>
    /// <param name="library">起動入力から構築したライブラリ。</param>
    /// <param name="capability">受理済み設定・再初期化の生存権限。通常起動ではnullです。</param>
    void AttachStartupLibrary(BMSLibrary library, LibraryFileMutationCapability capability = null);

    /// <summary>
    /// 構築が完了したLibraryとPlaylistを画面の利用主体へ接続します。
    /// </summary>
    /// <param name="services">今回構築した組。</param>
    void AttachStartupServices(StartupLibraryServices services);
}

/// <summary>
/// 事前検査、配置・DB準備、バックアップとモデルの組の構築・退役順を所有します。
/// </summary>
internal sealed class StartupLibraryConstructionOwner
{
    private readonly IStartupLibraryFactory factory;

    /// <summary>
    /// モデルの組を構築する依存を接続します。
    /// </summary>
    /// <param name="factory">LibraryとPlaylistを構築するfactory。</param>
    internal StartupLibraryConstructionOwner(IStartupLibraryFactory factory)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    private static BmsLibraryOptionsSnapshot CreateStartupDirectoryPreflightOptions(
        StartupSettingsSnapshot startupSettings,
        CustomFolderOutputSettingsSnapshot customFolderSettings)
    {
        if (startupSettings == null)
        {
            throw new ArgumentNullException(nameof(startupSettings));
        }

        return new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = startupSettings.OperationModeLR2DB,
            LR2RootPath = startupSettings.LR2RootPath,
            LR2CustomFolderOutputBaseDir = startupSettings.LR2CustomFolderOutputBaseDir,
            LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry
                .DeserializeBaseDirectories(startupSettings.LR2CustomFolderAdditionalOutputBaseDirs),
            LR2CustomFolderAdditionalOutputBaseDirsSerialized = startupSettings.LR2CustomFolderAdditionalOutputBaseDirs,
            LR2CustomFolderOutputBaseDirRootType = customFolderSettings?.LR2CustomFolderOutputBaseDirRootType
        };
    }

    private static LibraryDirectoryPreflightRequest CreateStartupDirectoryPreflightRequest(
        StartupSettingsSnapshot startupSettings,
        CustomFolderOutputSettingsSnapshot customFolderSettings,
        LR2Config startupLr2Config)
    {
        if (startupSettings == null)
        {
            throw new ArgumentNullException(nameof(startupSettings));
        }

        BmsLibraryOptionsSnapshot options = CreateStartupDirectoryPreflightOptions(
            startupSettings,
            customFolderSettings);
        IReadOnlyList<string> registeredRoots = startupSettings.OperationModeLR2DB
            ? (startupLr2Config ?? throw new ArgumentNullException(nameof(startupLr2Config)))
                .GetBMSSearchDirectoriesForChangeTracking()
            : startupSettings.StandaloneBmsRootPaths;
        return new LibraryDirectoryPreflightService().CreateRequest(
            registeredRoots,
            registeredRoots,
            options);
    }

    /// <summary>設定XMLと登録先を副作用のない作業スレッドで検査し、同じXML入力を構築へ渡します。</summary>
    internal static Task<(bool ConfigValid, LR2Config Config)> PrepareDirectoriesAsync(
        StartupSettingsSnapshot startupSettings,
        CustomFolderOutputSettingsSnapshot customFolderSettings)
    {
        return Task.Run(() =>
        {
            LR2Config config = null;
            if (startupSettings.OperationModeLR2DB
                && !LR2Config.TryLoad(startupSettings.LR2ConfigXmlPath, out config))
            {
                // 検査入力を構成できない場合は設定不備へ戻し、空ルートでの検査成功にしません。
                return (ConfigValid: false, Config: (LR2Config)null);
            }

            LibraryDirectoryPreflightRequest request = CreateStartupDirectoryPreflightRequest(
                startupSettings,
                customFolderSettings,
                config);
            new LibraryDirectoryPreflightService().EnsureAvailable(request, probeOutputBases: true);
            return (ConfigValid: true, Config: config);
        }).LoggingAndPropagate("StartupDirectoryPreflight");
    }

    /// <summary>
    /// 検証済みの起動設定から、ライブラリが利用するモード別のプロファイルを構成します。
    /// LR2設定はディレクトリ検査で読み込んだ同じインスタンスを使用します。
    /// </summary>
    internal static LibraryProfile CreateProfile(
        StartupSettingsSnapshot startupSettings,
        LR2Config startupLr2Config, ApplicationPathSnapshot paths)
    {
        if (startupSettings.OperationModeLR2DB)
        {
            LR2Config lr2config = startupLr2Config ?? throw new ArgumentNullException(nameof(startupLr2Config));
            if (lr2config.EnsureDatabaseAutoReloadManualOnly()) { lr2config.Save(); }
            string scoreDbPath = Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(startupSettings.LR2RootPath, lr2config.GetPlayerId);
            return new LibraryProfile(
                operationModeLR2DB: true,
                songDbPath: startupSettings.LR2SongDBPath,
                searchRoots: [],
                lr2ConfigProvider: () => lr2config,
                lr2ScoreDbPath: scoreDbPath,
                canWriteLr2Config: true,
                canOutputLr2Folders: true,
                canUseLr2Backup: true,
                canUseLr2IrScore: true);
        }

        StandaloneLibraryDatabaseEnsureResult standaloneSongDb = StandaloneLibraryDatabase.EnsurePortableSongDb(
            paths);
        return new LibraryProfile(
            operationModeLR2DB: false,
            songDbPath: standaloneSongDb.SongDbPath,
            searchRoots: startupSettings.StandaloneBmsRootPaths,
            lr2ConfigProvider: null,
            lr2ScoreDbPath: null,
            canWriteLr2Config: false,
            canOutputLr2Folders: false,
            canUseLr2Backup: false,
            canUseLr2IrScore: false,
            startupRequiredFileScanReason: standaloneSongDb.RequiresInitialLibraryBuild ? standaloneSongDb.InitialLibraryBuildReason : null);
    }

    /// <summary>検証済み設定の通常出力先を、親受付内で同じLR2設定へ一度登録します。</summary>
    internal static void RepairCustomFolderSearchRoots(
        StartupSettingsSnapshot startupSettings,
        LR2Config startupLr2Config)
    {
        if (!startupSettings.OperationModeLR2DB)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(startupLr2Config);
        CustomFolderOutputBaseSearchRootSyncResult result =
            CustomFolderOutputBaseSearchRootSyncService.RepairNormalOutputBaseRoots(
                startupLr2Config,
                startupSettings.LR2CustomFolderOutputBaseDir,
                startupSettings.LR2CustomFolderAdditionalOutputBaseDirs);
        if (result.Changed)
        {
            startupLr2Config.Save();
        }
    }

    /// <summary>既存の非致命的バックアップを実行し、受付外へ渡す変更不能な通知を返します。</summary>
    internal static async Task<IReadOnlyList<UiMessageRequest>> PrepareBackupAsync(StartupSettingsSnapshot startupSettings)
    {
        List<UiMessageRequest> notifications = [];
        if (startupSettings.OperationModeLR2DB && startupSettings.IsLR2BackupEnabled)
        {
            Backup.Target lR2BackupTarget = startupSettings.LR2BackupTarget;
            string songDBPath = null;
            List<string> scoreDBPaths = [];
            if (lR2BackupTarget.HasFlag(Backup.Target.SongDB) && LongPathFileSystem.FileExists(startupSettings.LR2SongDBPath))
            {
                songDBPath = startupSettings.LR2SongDBPath;
            }
            string scoreDirectoryPath = Path.Combine(startupSettings.LR2RootPath, "LR2files", "Database", "Score");
            if (lR2BackupTarget.HasFlag(Backup.Target.ScoreDB) && LongPathFileSystem.DirectoryExists(scoreDirectoryPath))
            {
                try
                {
                    scoreDBPaths = [.. LongPathFileSystem.EnumerateFiles(scoreDirectoryPath, "*.db", System.IO.SearchOption.AllDirectories)];
                }
                catch
                {
                    scoreDBPaths = [];
                }
            }
            Backup.BackupSaveResult backupSaveResult = null;
            backupSaveResult = await Task.Run(delegate
            {
                try
                {
                    Backup.BackupSaveResult result = Backup.SaveSelectedBackupsWithResult(
                        startupSettings.LR2BackupPath,
                        new TimeSpan(startupSettings.LR2BackupSpan, 0, 0, 0),
                        startupSettings.LR2BackupNum,
                        lR2BackupTarget,
                        startupSettings.LR2ConfigXmlPath,
                        startupSettings.LR2SongDBPath,
                        scoreDirectoryPath);
                    if (result.Saved)
                    {
                        try
                        {
                            Backup.RebuildDatabase(songDBPath, scoreDBPaths);
                        }
                        catch
                        {
                        }
                    }
                    return result;
                }
                catch (Exception ex)
                {
                    return Backup.BackupSaveResult.Failure(ex);
                }
            }).LoggingAndPropagate("Initialize");
            if (backupSaveResult != null)
            {
                foreach (string warning in backupSaveResult.Warnings)
                {
                    notifications.Add(new(warning, BeMusicSeeker.Properties.Resources.Warning, UiDialogButton.OK, UiDialogIcon.Exclamation, UiDialogDefaultResult.OK));
                }
                if (backupSaveResult.FailureException != null)
                {
                    notifications.Add(new(BeMusicSeeker.Properties.Resources.Msg_failed_backups + Environment.NewLine + backupSaveResult.FailureException.Message, BeMusicSeeker.Properties.Resources.Error, UiDialogButton.OK, UiDialogIcon.Hand, UiDialogDefaultResult.OK));
                }
            }
        }
        return Array.AsReadOnly(notifications.ToArray());
    }

    /// <summary>
    /// 捕捉した起動入力でライブラリとプレイリストを順に構築・接続し、共通権限を初期化へ渡します。
    /// </summary>
    /// <param name="libraryProfile">今回捕捉した変更不能なprofile。</param>
    /// <param name="applicationPort">画面への接続境界。</param>
    /// <param name="capability">受理済み設定・再初期化の生存権限。通常起動ではnullです。</param>
    /// <param name="previousLibrary">構築成功後に停止する旧Library。構築失敗では生存させます。</param>
    /// <param name="previousPlaylist">構築成功後に停止する旧Playlist。</param>
    /// <returns>画面へ接続した新しい組。</returns>
    internal StartupLibraryServices CreateAndApply(
        LibraryProfile libraryProfile,
        IStartupLibraryApplicationPort applicationPort, LibraryFileMutationCapability capability = null,
        BMSLibrary previousLibrary = null, BMSPlaylist previousPlaylist = null)
    {
        if (libraryProfile == null)
        {
            throw new ArgumentNullException(nameof(libraryProfile));
        }
        if (applicationPort == null)
        {
            throw new ArgumentNullException(nameof(applicationPort));
        }

        BMSLibrary library = factory.CreateBmsLibrary(libraryProfile)
            ?? throw new InvalidOperationException("Startup library factory returned null.");
        BMSPlaylist playlist;
        try { playlist = factory.CreateBmsPlaylist(libraryProfile, library) ?? throw new InvalidOperationException("Startup playlist factory returned null."); }
        catch { if (!ReferenceEquals(previousLibrary, library)) { library.RequestStop("construction_failed"); } throw; }
        StartupLibraryServices services = new(libraryProfile, library, playlist);
        try { if (!libraryProfile.OperationModeLR2DB) { library.SearchTargets.AddRange(libraryProfile.SearchRoots); } }
        catch
        {
            if (!ReferenceEquals(previousPlaylist, playlist)) { playlist.RequestStop("construction_failed"); }
            if (!ReferenceEquals(previousLibrary, library)) { library.RequestStop("construction_failed"); }
            throw;
        }
        if (!ReferenceEquals(previousLibrary, library)) { previousLibrary?.RequestStop("service_replaced"); }
        if (!ReferenceEquals(previousPlaylist, playlist)) { previousPlaylist?.RequestStop("service_replaced"); }
        applicationPort.AttachStartupLibrary(library, capability);
        applicationPort.AttachStartupServices(services);
        return services;
    }
}

/// <summary>
/// LibraryとPlaylistの構築成功後に渡す変更不能な組です。
/// </summary>
internal sealed class StartupLibraryServices
{
    /// <summary>
    /// 同じprofileで構築したモデルの組を保持します。
    /// </summary>
    /// <param name="profile">両モデルで使用したprofile。</param>
    /// <param name="library">構築したLibrary。</param>
    /// <param name="playlist">構築したPlaylist。</param>
    internal StartupLibraryServices(LibraryProfile profile, BMSLibrary library, BMSPlaylist playlist)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Library = library ?? throw new ArgumentNullException(nameof(library));
        Playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
    }

    /// <summary>
    /// 両モデルで使用したprofileを取得します。
    /// </summary>
    internal LibraryProfile Profile { get; }

    /// <summary>
    /// Playlistに接続したLibraryを取得します。
    /// </summary>
    internal BMSLibrary Library { get; }

    /// <summary>
    /// 同じ<see cref="Library"/>へ接続したPlaylistを取得します。
    /// </summary>
    internal BMSPlaylist Playlist { get; }
}
