using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Tests;

/// <summary>
/// Provides the narrow stateless factories and synchronization port shared by the split playlist fixtures.
/// </summary>
internal static class BmsPlaylistTestSupport
{
    internal static byte[] CreateUtf8BomBytes(string text)
    {
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
    }

    internal static BMSPlaylist CreatePlaylist(
        string songDbPath,
        ILr2PlaylistFolderSynchronizationPort synchronization = null!,
        Func<LR2Config> getLr2Config = null!)
    {
        return new TestBmsPlaylist(
            songDbPath,
            getLr2Config,
            null,
            null,
            null,
            () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(Settings.Default),
            () => BeatorajaBmtOptionsSnapshot.CreateCurrent(Settings.Default),
            () => CustomFolderOutputSettingsSnapshot.CreateCurrent(Settings.Default),
            synchronization ?? new TestLr2PlaylistFolderSynchronizationPort(songDbPath));
    }

    /// <summary>
    /// Creates a synchronization port with the supplied immutable physical output snapshot.
    /// </summary>
    internal static TestLr2PlaylistFolderSynchronizationPort CreateDeterministicLr2PlaylistFolderSynchronizationPort(
        string songDbPath,
        CustomFolderOutputPhysicalSurface physicalSurface)
    {
        ArgumentNullException.ThrowIfNull(physicalSurface);
        return new TestLr2PlaylistFolderSynchronizationPort(songDbPath)
        {
            PhysicalSurfaceFactory = () => physicalSurface
        };
    }

    internal static PlaylistWorkspaceViewModel CreatePlaylistWorkspace(
        BMSPlaylist playlist,
        BMSLibrary library,
        Func<CustomFolderOutputSettingsSnapshot>? settingsProvider = null,
        Func<LR2Config>? lr2ConfigProvider = null,
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
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
            PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler,
            PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        workspace.ConfigureCatalogNotificationQueue(action => action());
        PlaylistWorkspaceTestPorts.AttachImmediatePlaylistPresentationRouter(workspace);
        workspace.PlaylistPropertyValidationError += (_, _) => { };
        workspace.PlaylistPropertyExternalSyncConfirmationRequested += (_, _) => { };
        workspace.PlaylistPropertyInvalidOutputDirectoryRequested += (_, _) => { };
        workspace.PlaylistPropertyExternalSyncFailed += (_, _) => { };
        workspace.PlaylistSyncProgressChanged += (_, _) => { };
        workspace.PlaylistDetailReloadRefreshRequested += (_, _) => { };
        workspace.PlaylistReferenceSortInvalidationRequested += (_, _) => { };
        workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
        workspace.RefreshPlaylistTreeTables(playlist, library);
        return workspace;
    }

    internal static string CreateTempSongDbPath(string tempDirectory)
    {
        string tempSongDbPath = Path.Combine(tempDirectory, "song.db");
        using (var db = new LR2SongDBExtended(tempSongDbPath))
        {
            db.CreateTable<LR2SongDB.song>();
            db.CreateTable<LR2SongDB.folder>();
        }
        return tempSongDbPath;
    }

    internal static string ReadShiftJisText(string path)
    {
        using FileStream stream = LongPathFileSystem.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.GetEncoding("shift_jis"), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static LR2Config CreateLr2Config(string lr2RootPath, params string[] bmsRoots)
    {
        string configDirectory = Path.Combine(lr2RootPath, "LR2files", "Config");
        Directory.CreateDirectory(configDirectory);
        string pathElements = string.Join(
            string.Empty,
            (bmsRoots ?? [])
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => "<path>" + EscapeXml(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar) + "</path>"));
        string configPath = Path.Combine(configDirectory, "config.xml");
        File.WriteAllText(
            configPath,
            "<config><system><customfolder>0</customfolder><titleflash>24</titleflash></system><jukebox>" + pathElements + "</jukebox></config>",
            Encoding.UTF8);
        return new LR2Config(configPath);
    }

    internal static string EscapeXml(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? string.Empty;
    }

    internal static BMSTableEntry CreateEntry(string md5, string folder)
    {
        return new BMSTableEntry(JObject.Parse("{\"md5\":\"" + md5 + "\",\"title\":\"" + md5 + "\",\"level\":\"" + folder + "\"}"));
    }

    internal static BMSTableEntry CreateEntryWithLevel(string md5, int level)
    {
        return new BMSTableEntry(JObject.Parse("{\"md5\":\"" + md5 + "\",\"title\":\"" + md5 + "\",\"level\":" + level + "}"));
    }
}

internal sealed class TestLr2PlaylistFolderSynchronizationPort : ILr2PlaylistFolderSynchronizationPort
{
    private readonly string songDbPath;

    internal TestLr2PlaylistFolderSynchronizationPort(string songDbPath)
    {
        this.songDbPath = songDbPath;
    }

    internal List<string> Operations { get; } = [];

    internal Func<CustomFolderOutputPhysicalSurface> PhysicalSurfaceFactory { get; set; } = null!;

    internal Exception Failure { get; set; } = null!;

    public CustomFolderOutputPhysicalSurface GetCurrentAppManagedCustomFolderOutputPhysicalSurface()
    {
        return PhysicalSurfaceFactory?.Invoke()
            ?? new CustomFolderOutputPhysicalSurface(
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                discoveryComplete: false);
    }

    public Lr2FolderFileDbSyncResult SyncPlaylistLr2FolderFileRows(
        string operation,
        Lr2FolderFileDbSyncRequest request)
    {
        Operations.Add(operation);
        if (Failure != null)
        {
            throw Failure;
        }

        using var songDb = new LR2SongDBExtended(songDbPath);
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, request);
            songDb.Commit();
            return result;
        }
        catch
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }
}
