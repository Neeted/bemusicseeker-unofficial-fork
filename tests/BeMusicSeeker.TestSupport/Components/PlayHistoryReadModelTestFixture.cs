using System.Collections.ObjectModel;
using System.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using SQLite;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>分割した同機能caseが実際に共用する入力・観測のhelperだけをまとめます。</summary>
internal static class PlayHistoryReadModelTestFixture
{

    /// <remarks>CoreとWindowsのcaseで同じ入力準備・観測を共用します。渡されたモデルや媒体の寿命と回収は呼出元が所有し、既存の戻り値・失敗伝播を維持します。</remarks>
    internal static long UtcEpoch(int year, int month, int day)
    {
        return UtcEpoch(year, month, day, 0);
    }

    /// <remarks>CoreとWindowsのcaseで同じ入力準備・観測を共用します。渡されたモデルや媒体の寿命と回収は呼出元が所有し、既存の戻り値・失敗伝播を維持します。</remarks>
    internal static long UtcEpoch(int year, int month, int day, int hour)
    {
        return new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    }

    /// <summary>同じ履歴読取り条件をCoreとWindowsで共用するため、このcallbackだけのworkspaceとscore DBパスを用意します。</summary>
    /// <param name="action">score DBパスを受ける同期callback。開始したTask・モデル・DB接続は呼出元の既存終端経路で媒体回収前に解放します。</param>
    /// <remarks>callback後の既存finallyが媒体を削除してからworkspaceをDisposeします。callback・媒体回収・workspace回収の失敗はtry/finallyの順で伝播し、新しい集約や失敗置換は追加しません。</remarks>
    internal static void WithScoreDb(Action<string> action)
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directoryPath = Path.Combine(caseWorkspace.RootDirectory, "BeMusicSeeker_PlayHistoryReadModel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            action(Path.Combine(directoryPath, "score.db"));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    /// <remarks>databaseScopeは呼出元caseの専有ownerを借用し、新しいownerを作成・破棄しません。返すlibrary/snapshotや依存closureも同じownerを参照し、実Task・モデル・接続の終端と媒体回収は呼出元と既存fixtureの寿命に従います。失敗は元の本文どおり伝播します。</remarks>
    internal static void CreateInstalledScoreDb(ApplicationDatabaseScope databaseScope, string scoreDbPath)
    {
        CreateBaseScoreDb(databaseScope, scoreDbPath);
        Lr2PlayHistorySchemaCheckResult result = new Lr2PlayHistorySchemaService(databaseScope).InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);
        Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, result.Status);
    }

    /// <remarks>databaseScopeは呼出元caseの専有ownerを借用し、新しいownerを作成・破棄しません。返すlibrary/snapshotや依存closureも同じownerを参照し、実Task・モデル・接続の終端と媒体回収は呼出元と既存fixtureの寿命に従います。失敗は元の本文どおり伝播します。</remarks>
    internal static void CreateBaseScoreDb(ApplicationDatabaseScope databaseScope, string scoreDbPath)
    {
        using SQLiteConnectionEx db = databaseScope.OpenProviderConnection(scoreDbPath);
        db.CreateTable<LR2ScoreDB.score>();
        db.CreateTable<LR2ScoreDB.player>();
    }

    /// <remarks>CoreとWindowsのcaseで同じ入力準備・観測を共用します。渡されたモデルや媒体の寿命と回収は呼出元が所有し、既存の戻り値・失敗伝播を維持します。</remarks>
    internal static void InsertHistory(SQLiteConnection db, long historyId, string hash, long playedAt, bool finalized, int? newExscore)
    {
        db.Execute(
            "INSERT INTO bms_lr2_play_history (history_id, hash, played_at, finalized, score_write_type, new_playcount, playcount_delta, new_exscore, new_totalnotes) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?);",
            historyId, hash, playedAt, finalized ? 1 : 0, "update", 1, 1, newExscore, 100);
    }

    /// <summary>表示consumerの入力を実モデルの許可取得で準備します。BeginRequestはこの入力の表示要求であり、製品の取得通知接続を補完しません。</summary>
    /// <remarks>databaseScopeは呼出元caseの専有ownerを借用し、新しいownerを作成・破棄しません。返すlibrary/snapshotや依存closureも同じownerを参照し、実Task・モデル・接続の終端と媒体回収は呼出元と既存fixtureの寿命に従います。失敗は元の本文どおり伝播します。</remarks>
    internal static BMSLibrary.ScoreSnapshot AcquireHistorySnapshot(ApplicationDatabaseScope databaseScope, string scoreDbPath, bool beatoraja = false, bool isLr2LinkedProfile = true)
    {
        ScoreTableLoadResult acquired = new BmsLibraryInitializationService().LoadScoreTable(
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), Path.Combine(Path.GetDirectoryName(scoreDbPath) ?? string.Empty, "song.db"), scoreDbPath), new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = isLr2LinkedProfile,
                UseBeatorajaScoreDb = beatoraja,
                BeatorajaScoreDbPath = beatoraja ? scoreDbPath : string.Empty
            });
        return new BMSLibrary.ScoreSnapshot
        {
            ActiveScoreSource = acquired.ActiveScoreSource,
            ScoreDbPath = acquired.ScoreDbPath,
            LoadStatus = acquired.Status,
            Scores = acquired.Scores,
            ScoresBySha256 = acquired.BeatorajaScoresBySha256,
            PlayHistorySnapshot = acquired.PlayHistorySnapshot,
            SourceGeneration = 1
        };
    }

    /// <remarks>databaseScopeは呼出元caseの専有ownerを借用し、新しいownerを作成・破棄しません。返すlibrary/snapshotや依存closureも同じownerを参照し、実Task・モデル・接続の終端と媒体回収は呼出元と既存fixtureの寿命に従います。失敗は元の本文どおり伝播します。</remarks>
    internal static TestBmsLibrary CreateHistoryLibrary(ApplicationDatabaseScope databaseScope, string scoreDbPath)
    {
        string songDbPath = Path.Combine(Path.GetDirectoryName(scoreDbPath)!, "song.db");
        using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
        {
            songDb.CreateTable<LR2SongDB.song>();
            BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
            BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        }
        return new TestBmsLibrary(databaseScope, songDbPath);
    }

    /// <remarks>CoreとWindowsのcaseで同じ入力準備・観測を共用します。渡されたモデルや媒体の寿命と回収は呼出元が所有し、既存の戻り値・失敗伝播を維持します。</remarks>
    internal static PlayHistoryViewExecutionResult ExecuteHistoryRead(PlayHistoryWorkflowOwner owner,
        PlayHistoryViewExecutionDependencies dependencies, PlayHistoryPeriodRequest? period = null)
    {
        PlayHistoryDisplayTargetItem displayTarget = PlayHistoryDisplayTargetItem.All;
        PlayHistoryViewRequest request = owner.BeginRequest(period ?? PlayHistoryPeriodRequest.All(), string.Empty,
            displayTarget.Identity, owner.DisplayTargetRevision,
            dependencies.PlaylistWorkspace.PrepareDetailSourceRetirementWithoutPublishing());
        return owner.ExecuteView(new PlayHistoryViewExecutionRequest(MainViewUpdateMode.PlayHistorySelected,
            MainViewUpdateMode.PlayHistorySelected, null, Stopwatch.StartNew(), request, string.Empty, displayTarget));
    }

    /// <remarks>databaseScopeは呼出元caseの専有ownerを借用し、新しいownerを作成・破棄しません。返すlibrary/snapshotや依存closureも同じownerを参照し、実Task・モデル・接続の終端と媒体回収は呼出元と既存fixtureの寿命に従います。失敗は元の本文どおり伝播します。</remarks>
    internal static PlayHistoryViewExecutionDependencies CreateViewExecutionDependencies(ApplicationDatabaseScope databaseScope,
        Func<PlayHistoryReadSourceContext>? sourceResolver = null,
        Func<Action, bool>? invokePresentation = null,
        BMSLibrary? library = null)
    {
        return new PlayHistoryViewExecutionDependencies(
            () => library,
            () => null,
            sourceResolver ?? (() => PlayHistoryReadSourceContext.FromSnapshot(AcquireHistorySnapshot(databaseScope, string.Empty, isLr2LinkedProfile: false))),
            invokePresentation ?? (action =>
            {
                action();
                return true;
            }),
            (_, _) => { },
            () => MainViewUpdateMode.PlayHistorySelected,
            new MainChartListViewModel(),
            new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                () => new CustomFolderOutputSettingsSnapshot(),
                PlaylistWorkspaceFixturePorts.CreateUrlAcquisitionWorkflow(),
                PlaylistWorkspaceFixturePorts.CreateExternalPackageLookupService(),
                PlaylistWorkspaceFixturePorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceFixturePorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceFixturePorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceFixturePorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceFixturePorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportInfoLog,
                PlaylistWorkspaceFixturePorts.PlaylistSummaryColumnSettingsStore,
                PlaylistWorkspaceFixturePorts.PlaylistSummaryBmtSortCoordinator,
                PlaylistWorkspaceFixturePorts.KeywordSearchHistorySettingsStore,
                PlaylistWorkspaceFixturePorts.KeywordSearchFavoritesSettingsStore,
                PlaylistWorkspaceFixturePorts.PlaylistStoreProvider,
                PlaylistWorkspaceFixturePorts.PlaylistPropertySaveService,
                () => null!,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck,
                UndeclaredDialogs.Create()));
    }

    /// <remarks>CoreとWindowsのcaseで同じ入力準備・観測を共用します。渡されたモデルや媒体の寿命と回収は呼出元が所有し、既存の戻り値・失敗伝播を維持します。</remarks>
    internal const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

}
