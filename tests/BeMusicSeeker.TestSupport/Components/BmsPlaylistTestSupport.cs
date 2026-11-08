using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// プレイリストfixtureが共有する、状態を持たない準備処理と同期境界です。
/// </summary>
internal static class BmsPlaylistTestSupport
{
    /// <summary>固定上流から採取した原観測と期待値を、計算せずテスト出力から読みます。</summary>
    internal static JObject ReadWalkureCase(string name) => (JObject)JArray.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "TestData", "Walkure", "math-cases.json")))
        .Single(row => row.Value<string>("name") == name);

    /// <summary>独立入力だけを変更不能なスコア境界へ渡します。期待実力値は含めません。</summary>
    internal static WalkureScoreInput ReadWalkureInput(string name = "mixed") => new(ScoreTableLoadStatus.Loaded,
        ((JArray)(ReadWalkureCase(name)["observations"] ?? throw new FormatException())).ToImmutableDictionary(
            row => row.Value<string>("md5") ?? throw new FormatException(),
            row => Enum.Parse<WalkureLamp>(row.Value<string>("clearLamp") ?? throw new FormatException(), true), StringComparer.OrdinalIgnoreCase));

    internal static byte[] CreateUtf8BomBytes(string text)
    {
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
    }

    /// <summary>同じfixtureのDB所有者へ、独立したプレイリストと同期境界を接続します。</summary>
    /// <param name="databaseScope">接続と書込み排他を共有するfixtureのDB所有者。</param>
    /// <param name="modelUiScheduler">実保存後のモデル通知先。省略時は既存Dispatcher境界を使います。</param>
    internal static BMSPlaylist CreatePlaylist(
        ApplicationDatabaseScope databaseScope,
        string songDbPath,
        ILr2PlaylistFolderSynchronizationPort synchronization = null!,
        Lr2ConfigOwner getLr2Config = null!,
        Settings? settings = null, IUiScheduler? modelUiScheduler = null)
    {
        settings ??= TestSettingsFactory.CreateIsolatedSettings();
        return new TestBmsPlaylist(
            databaseScope,
            songDbPath,
            getLr2Config,
            null,
            null,
            () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(settings),
            () => BeatorajaBmtOptionsSnapshot.CreateCurrent(settings),
            () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings),
            synchronization ?? new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath), uiScheduler: modelUiScheduler);
    }

    internal static PlaylistWorkspaceViewModel CreatePlaylistWorkspace(
        BMSPlaylist playlist,
        BMSLibrary library,
        Func<CustomFolderOutputSettingsSnapshot>? settingsProvider = null,
        Func<Lr2ConfigOwner>? lr2ConfigProvider = null,
        Action<string>? summaryWarningLog = null)
    {
        settingsProvider ??= () => new CustomFolderOutputSettingsSnapshot();
        lr2ConfigProvider ??= () => null!;
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            settingsProvider,
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
            () => playlist,
            new PlaylistPropertySaveService(
                () => playlist,
                () => library,
                lr2ConfigProvider,
                settingsProvider),
            () => library,
            lr2ConfigProvider,
            summaryWarningLog ?? (_ => { }),
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (_, _) => { },
            (_, _) => false,
            (_, _) => false,
            PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler,
            PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck,
            UndeclaredDialogs.Create());
        workspace.ConfigureCatalogNotificationQueue(action => action());
        PlaylistWorkspaceTestPorts.AttachImmediatePlaylistPresentationRouter(workspace);
        workspace.PlaylistPropertyValidationError += (_, _) => { };
        workspace.PlaylistPropertyExternalSyncConfirmationRequested += (_, _) => { };
        workspace.PlaylistPropertyInvalidOutputDirectoryRequested += (_, _) => { };
        workspace.PlaylistPropertyExternalSyncFailed += (_, _) => { };
        workspace.PlaylistSyncProgressChanged += (_, _) => { };
        workspace.PlaylistDetailReloadRefreshRequested += (_, _) => { };

        workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
        workspace.RefreshPlaylistTreeTables(playlist, library);
        return workspace;
    }

    internal static string ReadShiftJisText(string path)
    {
        using FileStream stream = LongPathFileSystem.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.GetEncoding("shift_jis"), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static BMSTableEntry CreateEntryWithLevel(string md5, int level)
    {
        return new BMSTableEntry(JObject.Parse("{\"md5\":\"" + md5 + "\",\"title\":\"" + md5 + "\",\"level\":" + level + "}"));
    }
}
