using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.PlaylistWorkspaceFixtureFactory;
using static BeMusicSeeker.Tests.PlaylistWorkspaceTestDataSupport;
using PlaylistWorkspaceViewModelTests = BeMusicSeeker.Tests.PlaylistWorkspaceExternalSourceTests;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistWorkspaceActionWorkflowTests
{
    [TestInitialize]
    public void TestInitialize()
    {
        TestResourceInitializer.EnsureJapaneseResources();
    }

    [TestMethod]
    public void PlaylistTreeSnapshot_ReleasesReaderLockAfterCapture()
    {

        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable first = new() { name = "first" };
            BMSTable second = new() { name = "second" };
            TestBmsPlaylist playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([first, second])
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);
            workspace.RefreshPlaylistTreeTables(playlist);

            List<BMSTable> snapshot = workspace.CapturePlaylistTreeTablesSnapshot();

            CollectionAssert.AreEqual(new[] { first, second }, snapshot);
            Task<bool> writerProbe = Task.Run(() =>
            {
                playlist.AcquireWriterLockBMSTables();
                try
                {
                    return true;
                }
                finally
                {
                    playlist.FreeWriterLockBMSTables();
                }
            });
            bool writerCompleted = writerProbe.Wait(TimeSpan.FromSeconds(2));
            try
            {
                Assert.IsTrue(writerCompleted);
                Assert.IsTrue(writerProbe.GetAwaiter().GetResult());
            }
            finally
            {
                if (!writerCompleted)
                {
                    writerProbe.Wait(TimeSpan.FromSeconds(2));
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }



    [TestMethod]
    public void PlaylistFolderContextMenuAvailability_PreservesEditabilityPolicy()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        BMSTable localTable = new();

        PlaylistFolderContextMenuAvailability normal =
            workspace.CapturePlaylistFolderContextMenuAvailability(
                localTable,
                PlaylistFolderNode.CreateFolder("Folder"));
        Assert.IsTrue(normal.CanDelete);
        Assert.IsTrue(normal.CanRename);

        PlaylistFolderContextMenuAvailability special =
            workspace.CapturePlaylistFolderContextMenuAvailability(
                localTable,
                PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned));
        Assert.IsFalse(special.CanDelete);
        Assert.IsFalse(special.CanRename);

        PlaylistFolderContextMenuAvailability external =
            workspace.CapturePlaylistFolderContextMenuAvailability(
                new BMSTable { is_external_sync = true },
                PlaylistFolderNode.CreateFolder("Folder"));
        Assert.IsFalse(external.CanDelete);
        Assert.IsFalse(external.CanRename);

        PlaylistFolderContextMenuAvailability missing =
            workspace.CapturePlaylistFolderContextMenuAvailability(localTable, null);
        Assert.IsFalse(missing.CanDelete);
        Assert.IsFalse(missing.CanRename);
    }

    [TestMethod]
    public void PlaylistTableContextMenuAvailability_PreservesAllActionPolicyFields()
    {
        PlaylistWorkspaceViewModel unavailableWorkspace = CreateDetailWorkspace(out _);
        PlaylistTableContextMenuAvailability unavailable =
            unavailableWorkspace.CapturePlaylistTableContextMenuAvailability(
                new BMSTable { Page_url = new Uri("https://example.test/table") });
        Assert.IsFalse(unavailable.CanOpenProperty);

        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            TestBmsPlaylist playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);

            BMSTable normalTable = new()
            {
                Page_url = new Uri("https://example.test/table")
            };
            PlaylistTableContextMenuAvailability normal =
                workspace.CapturePlaylistTableContextMenuAvailability(normalTable);
            Assert.IsTrue(normal.CanReload);
            Assert.IsTrue(normal.CanOpenPage);
            Assert.IsTrue(normal.CanCreateFolder);
            Assert.IsTrue(normal.CanOverwriteLevel);
            Assert.IsTrue(normal.CanRemoveTable);
            Assert.IsTrue(normal.CanOpenProperty);

            BMSTable externalTable = new()
            {
                Page_url = new Uri("https://example.test/external"),
                is_external_sync = true
            };
            PlaylistTableContextMenuAvailability external =
                workspace.CapturePlaylistTableContextMenuAvailability(externalTable);
            Assert.IsTrue(external.CanReload);
            Assert.IsTrue(external.CanOpenPage);
            Assert.IsFalse(external.CanCreateFolder);
            Assert.IsTrue(external.CanOverwriteLevel);
            Assert.IsTrue(external.CanRemoveTable);
            Assert.IsTrue(external.CanOpenProperty);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void PlaylistTableExternalLinks_PreserveNormalHeaderAndSpecialRoutes()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);

        BMSTable pageTable = new() { Page_url = new Uri("https://example.test/table") };
        PlaylistTableContextMenuAvailability pageAvailability =
            workspace.CapturePlaylistTableContextMenuAvailability(pageTable);
        Assert.IsTrue(pageAvailability.CanReload);
        Assert.IsTrue(pageAvailability.CanOpenPage);
        Assert.IsTrue(workspace.TryResolvePlaylistTablePageUri(pageTable, out Uri pageUri));
        Assert.AreEqual("https://example.test/table", pageUri.ToString());

        BMSTable headerTable = new() { Header_url = new Uri("https://example.test/header.json") };
        PlaylistTableContextMenuAvailability headerAvailability =
            workspace.CapturePlaylistTableContextMenuAvailability(headerTable);
        Assert.IsTrue(headerAvailability.CanReload);
        Assert.IsTrue(headerAvailability.CanOpenPage);
        Assert.IsTrue(workspace.TryResolvePlaylistTablePageUri(headerTable, out Uri headerUri));
        Assert.AreEqual("https://example.test/header.json", headerUri.ToString());

        BMSTable estimationTable = new() { Page_url = new Uri("bmseeker:table.estimation") };
        Assert.IsTrue(workspace.CapturePlaylistTableContextMenuAvailability(estimationTable).CanOpenPage);
        Assert.IsTrue(workspace.TryResolvePlaylistTablePageUri(estimationTable, out Uri estimationUri));
        Assert.AreEqual("http://walkure.net/hakkyou/bms.html", estimationUri.ToString());

        BMSTable recommendedTable = new() { Page_url = new Uri("bmseeker:table.recommended") };
        Assert.IsTrue(workspace.CapturePlaylistTableContextMenuAvailability(recommendedTable).CanOpenPage);
        Assert.IsFalse(workspace.TryResolvePlaylistTablePageUri(recommendedTable, out _));

        BMSTable unsupportedTable = new() { Page_url = new Uri("bmseeker:table.other") };
        Assert.IsTrue(workspace.CapturePlaylistTableContextMenuAvailability(unsupportedTable).CanOpenPage);
        Assert.IsFalse(workspace.TryResolvePlaylistTablePageUri(unsupportedTable, out _));
    }

    [TestMethod]
    public void PlaylistTableExternalLinks_UseLibraryPlayerIdForRecommendedRoute()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            BMSLibrary library = CreateLibraryWithLr2Id(songDbPath);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistLibraryProvider: () => library);

            BMSTable recommendedTable = new() { Page_url = new Uri("bmseeker:table.recommended") };
            Assert.IsTrue(workspace.TryResolvePlaylistTablePageUri(recommendedTable, out Uri recommendedUri));
            Assert.AreEqual(
                "http://walkure.net/hakkyou/recommended_mypage.html?playerid=123",
                recommendedUri.ToString());

        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaylistExternalLinkActions_UseWorkspaceBrowserBoundaryAndPreserveFailureContracts()
    {
        var openedUris = new List<Uri>();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            browserOpenSink: openedUris.Add);
        Uri summaryUri = new("https://example.test/summary");
        BMSTable table = new() { Page_url = new Uri("https://example.test/table") };

        await workspace.OpenPlaylistSummaryUriAsync(summaryUri);
        Assert.IsTrue(workspace.OpenPlaylistTablePage(table));

        CollectionAssert.AreEqual(
            new[] { summaryUri, table.Page_url },
            openedUris);

        PlaylistWorkspaceViewModel failingWorkspace = CreateDetailWorkspace(
            out _,
            browserOpenSink: _ => throw new InvalidOperationException("launch failed"));
        await failingWorkspace.OpenPlaylistSummaryUriAsync(summaryUri);
        Assert.ThrowsException<InvalidOperationException>(
            () => failingWorkspace.OpenPlaylistTablePage(table));
    }

    [TestMethod]
    public void PlaylistWorkspaceResolvesCurrentTableIdentityOutsideTheView()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable active = new()
            {
                playlist_id = 42,
                name = "Current",
                Page_url = new Uri("https://example.test/table")
            };
            TestBmsPlaylist playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([active])
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);
            BMSTable stale = new()
            {
                playlist_id = 42,
                name = "Old",
                Page_url = active.Page_url
            };

            Assert.AreSame(active, workspace.ResolveActivePlaylistTable(stale));
            Assert.AreSame(
                active,
                workspace.ResolveActivePlaylistSummaryTable(new PlaylistSummaryRow
                {
                    TableRef = stale,
                    Name = active.name
                }));

            BMSTable deletedPersistentTable = new()
            {
                playlist_id = 99,
                name = active.name,
                Page_url = active.Page_url
            };
            Assert.IsNull(workspace.ResolveActivePlaylistTable(deletedPersistentTable));

            BMSTable transientTable = new()
            {
                name = active.name,
                Page_url = active.Page_url
            };
            Assert.AreSame(active, workspace.ResolveActivePlaylistTable(transientTable));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaylistPropertySave_WaitsOffUiThreadBehindAWriter()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable table = new() { name = "Edit target" };
            TestBmsPlaylist playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => null!,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot());
            PlaylistPropertyEditSession session =
                await service.CreateEditSessionAsync(table);
            Assert.IsNotNull(session);
            var writerAcquired = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseWriter = new ManualResetEventSlim();
            Task writer = Task.Run(() =>
            {
                playlist.AcquireWriterLockBMSTables();
                try
                {
                    writerAcquired.SetResult(true);
                    releaseWriter.Wait();
                }
                finally
                {
                    playlist.FreeWriterLockBMSTables();
                }
            });
            await writerAcquired.Task;
            try
            {
                Assert.IsTrue(playlist.IsWriteLockHeldAnyBMSTable);
                Task<PlaylistPropertySaveCommit> saveTask =
                    service.TrySaveAsync(session, session.Values);
                Assert.IsFalse(saveTask.IsCompleted);
                releaseWriter.Set();
                Assert.IsNotNull(await saveTask);
            }
            finally
            {
                releaseWriter.Set();
                await writer;
                session.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaylistPropertySave_RejectsAStaleDetachedSessionWithoutOverwritingConcurrentChanges()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable table = new()
            {
                name = "Initial name",
                symbol = "OLD"
            };
            TestBmsPlaylist playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => null!,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot());
            using PlaylistPropertyEditSession session =
                await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            session.Values.Name = "Dialog edit";
            table.symbol = "CONCURRENT";

            InvalidOperationException conflict = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await service.TrySaveAsync(session, session.Values));

            StringAssert.Contains(conflict.Message, "changed after editing began");
            Assert.AreEqual("Initial name", table.name);
            Assert.AreEqual("CONCURRENT", table.symbol);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaylistPropertySave_RollsBackAllValuesWhenAPropertyNotificationFails()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable table = new()
            {
                name = "Initial name",
                symbol = "OLD",
                folder_sort_key = LR2SongDBExtended.playlist.CustomFolderSortType.NONE,
                entries =
                [
                    new BMSTableEntry { folder = "A" },
                    new BMSTableEntry { folder = "B" }
                ],
                Folder_order = ["B"]
            };
            TestBmsPlaylist playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => null!,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot());
            using PlaylistPropertyEditSession session =
                await service.CreateEditSessionAsync(table)
                ?? throw new AssertFailedException("Playlist edit session was not created.");
            session.Values.Name = "Dialog edit";
            session.Values.Symbol = "NEW";
            session.Values.FolderSortKey = LR2SongDBExtended.playlist.CustomFolderSortType.TITLE;
            table.PropertyChanged += (_, change) =>
            {
                if (string.Equals(change.PropertyName, "name", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("test property notification failure");
                }
            };

            await Assert.ThrowsExceptionAsync<AggregateException>(
                async () => await service.TrySaveAsync(session, session.Values));

            Assert.AreEqual("Initial name", table.name);
            Assert.AreEqual("OLD", table.symbol);
            Assert.AreEqual(
                LR2SongDBExtended.playlist.CustomFolderSortType.NONE,
                table.folder_sort_key);
            CollectionAssert.AreEqual(new[] { "B" }, table.Folder_order);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task CompleteSummaryPropertyEdit_RetriesPendingFollowUpWithoutReapplyingPrefixRewrite()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTableEntry entry = new TestablePlaylistEntry(
                "abababababababababababababababab",
                "Alpha")
            {
                playlist_id = 7301,
                folder = "Alpha"
            };
            BMSTableEntry prefixedEntry = new TestablePlaylistEntry(
                "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd",
                "Prefixed Alpha")
            {
                playlist_id = 7301,
                folder = "★Alpha"
            };
            BMSTable table = new()
            {
                playlist_id = 7301,
                name = "Inline prefix retry",
                symbol = "IPR",
                compat_prefix = string.Empty,
                entries = [entry, prefixedEntry],
                Folder_order = ["Alpha", "★Alpha"]
            };
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                seed.InsertOrReplace(prefixedEntry, typeof(LR2SongDBExtended.playlist_entry));
            }
            TestBmsPlaylist playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var library = new TestBmsLibrary(songDbPath);
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => library,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
            {
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                propertySaveService: service,
                playlistWorkspaceDialogService: dialogs);
            workspace.PlaylistReferenceSortInvalidationRequested += (_, _) => { };
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            int prefixPresentationCount = 0;
            service.PlaylistPropertyFolderSelectionRemapped += (_, _) =>
            {
                if (Interlocked.Increment(ref prefixPresentationCount) == 1)
                {
                    throw new InvalidOperationException("test inline prefix presentation failure");
                }
            };
            PlaylistSummaryRow row = new() { TableRef = table };

            PlaylistSummaryPropertyEditCompletion failedCompletion =
                await workspace.CompleteSummaryPropertyEditAsync(
                    row,
                    nameof(PlaylistSummaryRow.CompatPrefix),
                    "★",
                    commit: true);

            Assert.IsTrue(failedCompletion.RefreshRequired);
            Assert.IsFalse(failedCompletion.IsApplied);
            StringAssert.Contains(
                dialogs.LastMessageRequest.MessageBoxText,
                BeMusicSeeker.Properties.Resources.Msg_error_unexpected);
            CollectionAssert.AreEqual(
                new[] { "★Alpha", "★★Alpha" },
                table.entries.Select(candidate => candidate.folder).ToArray());
            Assert.IsTrue((await workspace.CompleteSummaryPropertyEditAsync(
                row,
                nameof(PlaylistSummaryRow.CompatPrefix),
                "★",
                commit: true)).IsApplied);
            Assert.AreEqual(2, prefixPresentationCount);
            CollectionAssert.AreEqual(
                new[] { "★Alpha", "★★Alpha" },
                table.entries.Select(candidate => candidate.folder).ToArray());
            PlaylistSummaryPropertyEditCompletion invalidCompletion =
                await workspace.CompleteSummaryPropertyEditAsync(
                    row,
                    "Unsupported",
                    "value",
                    commit: true);
            Assert.IsTrue(invalidCompletion.RefreshRequired);
            Assert.IsFalse(invalidCompletion.IsApplied);
            Assert.AreEqual(
                BeMusicSeeker.Properties.Resources.Msg_invalid_setting,
                dialogs.LastMessageRequest.MessageBoxText);

            dialogs.MessageResult = UiDialogResult.NotShown(UiDialogStatus.DispatcherUnavailable);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                workspace.CompleteSummaryPropertyEditAsync(
                    row,
                    "Unsupported",
                    "value",
                    commit: true));

            dialogs.MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
            PlaylistSummaryPropertyEditCompletion noOpCompletion =
                await workspace.CompleteSummaryPropertyEditAsync(
                    row,
                    nameof(PlaylistSummaryRow.Name),
                    "ignored",
                    commit: false);
            Assert.IsFalse(noOpCompletion.IsApplied);
            Assert.IsFalse(noOpCompletion.RefreshRequired);
            using var verify = new LR2SongDBExtended(songDbPath);
            CollectionAssert.AreEqual(
                new[] { "★Alpha", "★★Alpha" },
                new[]
                {
                    verify.ExecuteScalar<string>(
                        "SELECT folder FROM playlist_entry WHERE playlist_id = ? AND md5 = ? AND is_removed = 0;",
                        table.playlist_id,
                        entry.md5),
                    verify.ExecuteScalar<string>(
                        "SELECT folder FROM playlist_entry WHERE playlist_id = ? AND md5 = ? AND is_removed = 0;",
                        table.playlist_id,
                        prefixedEntry.md5)
                });
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task CreatePlaylistPropertyDialog_RejectsConcurrentOpenWithoutLeavingAnOrphanTable()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var visibleTables = new ObservableCollection<BMSTable>();
            TestBmsPlaylist playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = visibleTables
            };
            var service = new PlaylistPropertySaveService(
                () => playlist,
                () => null!,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot());
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                propertySaveService: service);
            var firstTableAdded = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseFirstCreation = new ManualResetEventSlim();
            visibleTables.CollectionChanged += (_, change) =>
            {
                if (change.Action == NotifyCollectionChangedAction.Add)
                {
                    firstTableAdded.TrySetResult(true);
                    releaseFirstCreation.Wait();
                }
            };

            Task<PlaylistPropertyDialogViewModel> firstOpen =
                workspace.CreatePlaylistPropertyDialogAsync();
            await firstTableAdded.Task;
            PlaylistPropertyDialogViewModel secondDialog =
                await workspace.CreatePlaylistPropertyDialogAsync();
            Assert.IsNull(secondDialog);
            releaseFirstCreation.Set();
            dialog = await firstOpen;

            Assert.IsNotNull(dialog);
            Assert.AreEqual(1, visibleTables.Count);
            Assert.AreEqual(
                PlaylistPropertyDialogOperationResult.Completed,
                await dialog.ResetPropertiesAsync());
            Assert.AreEqual(0, visibleTables.Count);
        }
        finally
        {
            dialog?.Dispose();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }


    [TestMethod]
    public void InstallPackageReferenceAttachment_AttachesBmsAndBmsonAndRaisesOneInvalidation()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            TestBmsPlaylist playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            BMSLibrary library = new TestBmsLibrary(songDbPath);
            const string bmsMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string bmsonSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            BMSFile bmsFile = BMSFile.FromSongTableRawValues(CreateSongTableRow(bmsMd5, @"C:\Installed\bms-chart.bms"));
            BMSFile bmsonIdentity = BMSFile.FromSongTableRawValues(CreateSongTableRow(null, @"C:\Installed\bmson-chart.bmson"));
            BMSTable table = new() { name = "Installed references", symbol = "P" };
            table.entries.Add(new BMSTableEntry(bmsFile));
            BMSTableEntry bmsonTableEntry = new BMSTableEntry(bmsonIdentity);
            bmsonTableEntry.MarkAsBmsonPlaylistIdentity(bmsonSha256);
            table.entries.Add(bmsonTableEntry);
            playlist.BMSTables.Add(table);

            LR2SongDBExtended.bmson_song bmsonSong = new()
            {
                path = @"C:\Installed\bmson-chart.bmson",
                sha256 = bmsonSha256,
                title = "Bmson chart",
                artist = "Artist"
            };
            ChartPackage package = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    bmsFile,
                    @"C:\Installed",
                    "Bms chart",
                    "Artist"),
                PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong)));
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library);
            workspace.RefreshPlaylistTreeTables(playlist);
            library.AddReferenceBMSTables([table]);
            int invalidationCount = 0;
            workspace.PlaylistReferenceSortInvalidationRequested += (_, _) => invalidationCount++;

            workspace.AttachInstalledPackageReferences([package]);

            Assert.AreEqual(1, invalidationCount);
            Assert.AreEqual("P", library.GetPlaylistReferenceDisplay(package.ChartEntries[0].Chart).Symbols);
            Assert.AreEqual("Installed references", library.GetPlaylistReferenceDisplay(package.ChartEntries[0].Chart).Names);
            Assert.AreEqual("P", library.GetPlaylistReferenceDisplay(package.ChartEntries[1].Chart).Symbols);
            Assert.AreEqual("Installed references", library.GetPlaylistReferenceDisplay(package.ChartEntries[1].Chart).Names);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void InstallPackageReferenceAttachment_EmptyOrMissingPlaylistIsNoOp()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        int invalidationCount = 0;
        workspace.PlaylistReferenceSortInvalidationRequested += (_, _) => invalidationCount++;

        workspace.AttachInstalledPackageReferences([]);
        workspace.AttachInstalledPackageReferences([ChartPackage.FromChartEntries([])]);

        Assert.AreEqual(0, invalidationCount);
    }

    [TestMethod]
    public void InstallPackageReferenceAttachment_ReleasesReaderLockWhenLibraryFails()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            TestBmsPlaylist playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([new BMSTable { name = "Lock target" }])
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => null!);
            workspace.RefreshPlaylistTreeTables(playlist);

            Assert.ThrowsException<InvalidOperationException>(() =>
                workspace.AttachInstalledPackageReferences([ChartPackage.FromChartEntries([])]));

            playlist.AcquireReaderLockBMSTables();
            playlist.FreeReaderLockBMSTables();
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaylistWorkspaceSummaryCellActionRequiresExternalSyncConfirmation()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        var workspace = CreateDetailWorkspace(out _, playlistWorkspaceDialogService: dialogs);
        var table = new BMSTable { is_external_sync = false };

        await workspace.ApplyPlaylistSummaryCellActionAsync(
            [
                new PlaylistSummaryRow { TableRef = table },
                new PlaylistSummaryRow { TableRef = table }
            ],
            "IsExternalSync",
            value: true);

        Assert.IsNotNull(dialogs.LastConfirmationRequest);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Confirm_EnablePlaylistSyncModeLoseLocalChanges,
            dialogs.LastConfirmationRequest.MessageBoxText);
        Assert.IsFalse(table.is_external_sync);
    }

    [TestMethod]
    public async Task PlaylistWorkspaceSummaryCellActionAcceptsDisableConfirmationAndMutatesTable()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            var playlist = new TestBmsPlaylist(databasePath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            var table = new BMSTable
            {
                name = "Summary table",
                Output_dir = "Summary table",
                is_external_sync = true,
                Folder_order = []
            };
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistWorkspaceDialogService: dialogs);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };

            await workspace.ApplyPlaylistSummaryCellActionAsync(
                [new PlaylistSummaryRow { TableRef = table }],
                "IsExternalSync",
                value: false);

            Assert.IsNotNull(dialogs.LastConfirmationRequest);
            Assert.AreEqual(
                BeMusicSeeker.Properties.Resources.Confirm_DisablePlaylistSyncModeRemoteChangesNotApplied,
                dialogs.LastConfirmationRequest.MessageBoxText);
            Assert.IsFalse(table.is_external_sync);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
    }

    [TestMethod]
    public async Task PlaylistWorkspaceSummaryCellActionPropagatesDialogFailureWithoutMutation()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
        {
            ConfirmationResult = UiDialogResult.NotShown(UiDialogStatus.DispatcherUnavailable)
        };
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistStoreProvider: () => new TestBmsPlaylist(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
            playlistWorkspaceDialogService: dialogs);
        var table = new BMSTable { is_external_sync = false };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => workspace.ApplyPlaylistSummaryCellActionAsync(
                [new PlaylistSummaryRow { TableRef = table }],
                "IsExternalSync",
                value: true));

        Assert.IsNotNull(dialogs.LastConfirmationRequest);
        Assert.IsFalse(table.is_external_sync);
    }

    [TestMethod]
    public async Task PlaylistWorkspaceSummaryRemovalRequiresConfirmation()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
        var workspace = CreateDetailWorkspace(out _, playlistWorkspaceDialogService: dialogs);
        var table = new BMSTable();

        await workspace.PlaylistRemovalWorkflow.RemoveSummaryRowsAsync(
            [
                new PlaylistSummaryRow { TableRef = table },
                new PlaylistSummaryRow { TableRef = table }
            ]);

        Assert.IsNotNull(dialogs.LastConfirmationRequest);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_remove_playlist, dialogs.LastConfirmationRequest.MessageBoxText);
        await workspace.PlaylistRemovalWorkflow.RemoveSummaryRowsAsync(Array.Empty<PlaylistSummaryRow>());
    }

    [TestMethod]
    public async Task PlaylistWorkspaceFolderRemovalRequiresConfirmationAndRejectsInvalidTargets()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
        var workspace = CreateDetailWorkspace(out _, playlistWorkspaceDialogService: dialogs);
        var table = new BMSTable();
        await workspace.PlaylistRemovalWorkflow.RemoveFolderAsync(table, PlaylistFolderNode.CreateFolder("Folder"));
        Assert.IsNotNull(dialogs.LastConfirmationRequest);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_remove_folder, dialogs.LastConfirmationRequest.MessageBoxText);

        UiConfirmationRequest confirmationRequest = dialogs.LastConfirmationRequest;
        await workspace.PlaylistRemovalWorkflow.RemoveFolderAsync(
            table,
            PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned));
        Assert.AreSame(confirmationRequest, dialogs.LastConfirmationRequest);

        table.is_external_sync = true;
        await workspace.PlaylistRemovalWorkflow.RemoveFolderAsync(table, PlaylistFolderNode.CreateFolder("Folder"));
        Assert.AreSame(confirmationRequest, dialogs.LastConfirmationRequest);
    }

    [TestMethod]
    public async Task PlaylistRemovalWorkflowRejectsExternalSyncRaceAfterConfirmation()
    {
        var table = new BMSTable();
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
            ConfirmationFactory = _ =>
            {
                table.is_external_sync = true;
                return UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
            }
        };
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistWorkspaceDialogService: dialogs);
        var rejectedKinds = new List<PlaylistWorkspaceMutationKind>();
        workspace.MutationRejected += (_, request) => rejectedKinds.Add(request.Kind);

        await workspace.PlaylistRemovalWorkflow.RemoveFolderAsync(
            table,
            PlaylistFolderNode.CreateFolder("Folder"));

        CollectionAssert.AreEqual(
            new[] { PlaylistWorkspaceMutationKind.RemoveFolder },
            rejectedKinds);
    }

    [TestMethod]
    public async Task PlaylistWorkspaceTableRemovalConfirmationIsOwnedByWorkflowOwner()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistWorkspaceDialogService: dialogs);
        var table = new BMSTable();

        bool selectionApplied = false;
        await workspace.PlaylistRemovalWorkflow.RemoveTreeTableAsync(null, () => selectionApplied = true);
        Assert.IsFalse(selectionApplied);
        await workspace.PlaylistRemovalWorkflow.RemoveTreeTableAsync(table, () => selectionApplied = true);
        Assert.IsFalse(selectionApplied);
        Assert.IsNotNull(dialogs.LastConfirmationRequest);

        dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            workspace.PlaylistRemovalWorkflow.RemoveTreeTableAsync(table, () => selectionApplied = true));
        Assert.IsTrue(selectionApplied);
    }

    [TestMethod]
    public async Task PlaylistTableLevelOverwriteWorkflowOwnsRecommendationAndConfirmation()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
        int libraryProviderCalls = 0;
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistLibraryProvider: () =>
            {
                libraryProviderCalls++;
                return null!;
            },
            playlistWorkspaceDialogService: dialogs);
        var recommendedTable = new BMSTable { Page_url = new Uri("bmseeker:table.recommended") };
        var normalTable = new BMSTable { Page_url = new Uri("https://example.test/table") };

        await workspace.PlaylistTableLevelOverwriteWorkflow.OverwriteAsync(null);
        await workspace.PlaylistTableLevelOverwriteWorkflow.OverwriteAsync(recommendedTable);
        Assert.IsNotNull(dialogs.LastMessageRequest);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Msg_override_level_error_recommended,
            dialogs.LastMessageRequest.MessageBoxText);
        Assert.IsNull(dialogs.LastConfirmationRequest);
        Assert.AreEqual(0, libraryProviderCalls);

        await workspace.PlaylistTableLevelOverwriteWorkflow.OverwriteAsync(normalTable);
        Assert.IsNotNull(dialogs.LastConfirmationRequest);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Msg_override_level_warning,
            dialogs.LastConfirmationRequest.MessageBoxText);
        Assert.AreEqual(0, libraryProviderCalls);

        dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            workspace.PlaylistTableLevelOverwriteWorkflow.OverwriteAsync(normalTable));
        Assert.AreEqual(1, libraryProviderCalls);
    }

    [TestMethod]
    public async Task PlaylistWorkspaceSummaryColumnResetRequiresConfirmation()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistWorkspaceDialogService: dialogs);
        var originalSettings = workspace.PlaylistSummaryColumnsSettings;
        int notificationCount = 0;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings))
            {
                notificationCount++;
            }
        };

        await workspace.ResetPlaylistSummaryColumnsToDefaultAsync();
        Assert.IsNotNull(dialogs.LastConfirmationRequest);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Msg_init_column_settings,
            dialogs.LastConfirmationRequest.MessageBoxText);
        Assert.AreEqual(MessageBoxButton.OKCancel, dialogs.LastConfirmationRequest.Button);
        Assert.AreEqual(MessageBoxResult.Cancel, dialogs.LastConfirmationRequest.DefaultResult);
        Assert.AreSame(originalSettings, workspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(0, notificationCount);

        dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
        await workspace.ResetPlaylistSummaryColumnsToDefaultAsync();
        Assert.AreNotSame(originalSettings, workspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(1, notificationCount);
    }

    [TestMethod]
    public async Task PlaylistWorkspaceSummaryColumnResetPropagatesDialogFailure()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
        {
            ConfirmationResult = UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable)
        };
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistWorkspaceDialogService: dialogs);
        PlaylistSummaryColumnSettings originalSettings = workspace.PlaylistSummaryColumnsSettings;

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => workspace.ResetPlaylistSummaryColumnsToDefaultAsync());

        Assert.AreSame(originalSettings, workspace.PlaylistSummaryColumnsSettings);
    }

    [TestMethod]
    public async Task PlaylistWorkspaceRecommendedImportShowsMissingLr2IdMessageWithoutEnqueueing()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            var playlist = new TestBmsPlaylist(databasePath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistWorkspaceDialogService: dialogs);

            Assert.IsFalse(await workspace.EnqueueRecommendedPlaylistImportAsync(
                "https://example.test/recommended?mode=update"));
            Assert.IsNotNull(dialogs.LastMessageRequest);
            Assert.AreEqual(
                BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_error,
                dialogs.LastMessageRequest.MessageBoxText);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
    }

    [TestMethod]
    public async Task PlaylistWorkspaceRecommendedImportAcceptsConfirmationAndEnqueues()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            var playlist = new TestBmsPlaylist(databasePath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            BMSLibrary library = CreateLibraryWithLr2Id(databasePath);
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                playlistWorkspaceDialogService: dialogs);
            var summaryReady = new TaskCompletionSource<ExternalPlaylistImportQueueSummary>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.ExternalPlaylistImportQueueSummaryReady += (_, request) =>
                summaryReady.TrySetResult(request.Summary);

            Assert.IsTrue(await workspace.EnqueueRecommendedPlaylistImportAsync(
                "bmseeker:table.unsupported?mode=readonly"));
            Assert.IsNotNull(dialogs.LastConfirmationRequest);
            ExternalPlaylistImportQueueSummary summary = await summaryReady.Task
                .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Assert.AreEqual(1, summary.FailedCount);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
    }

    [TestMethod]
    public async Task PlaylistWorkspaceRecommendedImportRejectsOrCancelsAfterShowingValidConfirmation()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            BMSLibrary library = CreateLibraryWithLr2Id(databasePath);
            string lr2RootPath = Path.Combine(Path.GetDirectoryName(databasePath)!, "lr2");
            string configPath = Path.Combine(lr2RootPath, "LR2files", "Config", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(
                configPath,
                "<config><system><customfolder>0</customfolder><titleflash>24</titleflash></system><jukebox /></config>",
                Encoding.UTF8);
            LR2Config config = new(configPath);
            var playlist = new TestBmsPlaylist(databasePath, () => config)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No)
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                playlistWorkspaceDialogService: dialogs);

            Assert.IsFalse(await workspace.EnqueueRecommendedPlaylistImportAsync(
                "bmseeker:table.recommended?mode=update"));
            dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel);
            Assert.IsFalse(await workspace.EnqueueRecommendedPlaylistImportAsync(
                "bmseeker:table.recommended?mode=update"));
            Assert.IsNotNull(dialogs.LastConfirmationRequest);
            StringAssert.Contains(
                dialogs.LastConfirmationRequest.MessageBoxText,
                "LR2ID: 123");
            StringAssert.Contains(
                dialogs.LastConfirmationRequest.MessageBoxText,
                BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_update_mode);
            Assert.AreEqual(MessageBoxButton.OKCancel, dialogs.LastConfirmationRequest.Button);
            Assert.AreEqual(MessageBoxResult.OK, dialogs.LastConfirmationRequest.DefaultResult);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
    }

    [TestMethod]
    public async Task PlaylistWorkspaceRecommendedImportRechecksInitializationLockAfterConfirmation()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        Exception? primaryFailure = null;
        try
        {
            BMSLibrary library = CreateLibraryWithLr2Id(databasePath);
            string lr2RootPath = Path.Combine(Path.GetDirectoryName(databasePath)!, "lr2");
            string configPath = Path.Combine(lr2RootPath, "LR2files", "Config", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(
                configPath,
                "<config><system><customfolder>0</customfolder><titleflash>24</titleflash></system><jukebox /></config>",
                Encoding.UTF8);
            LR2Config config = new(configPath);
            var playlist = new TestBmsPlaylist(databasePath, () => config)
            {
                BMSTables = new ObservableCollection<BMSTable>([
                    new BMSTable { playlist_id = 8201, name = "initialization barrier" }])
            };
            var dialogs = new BlockingConfirmationDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                playlistWorkspaceDialogService: dialogs);
            workspace.RefreshPlaylistTreeTables(playlist);

            var initializationEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseInitialization = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            playlist.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(BMSPlaylist.IsWriteLockHeldBMSTablesInitializeMin)
                    && playlist.IsWriteLockHeldBMSTablesInitializeMin
                    && initializationEntered.TrySetResult(true))
                {
                    releaseInitialization.Task
                        .WaitAsync(TimeSpan.FromSeconds(5))
                        .GetAwaiter()
                        .GetResult();
                }
            };

            Task<bool>? importTask = null;
            Task? initializationTask = null;
            try
            {
                importTask = workspace.EnqueueRecommendedPlaylistImportAsync(
                    "bmseeker:table.recommended?mode=update");
                initializationTask = Task.Run(() => playlist.Initialize(
                    reloadExtPlaylist: false,
                    queueBeatorajaBmtExportAfterHydration: false));

                await Task.WhenAll(
                        dialogs.ConfirmationShown.Task,
                        initializationEntered.Task)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsTrue(initializationEntered.Task.IsCompletedSuccessfully);
                Assert.IsTrue(playlist.IsWriteLockHeldBMSTablesInitializeMin);
                Assert.IsFalse(importTask.IsCompleted, "The import must remain in confirmation while initialization is active.");

                dialogs.ReleaseConfirmation();
                Assert.IsFalse(await importTask, "The second lock check must reject enqueueing after initialization begins.");
                Assert.IsFalse(initializationTask.IsCompleted, "The scheduler barrier must keep initialization active until released.");

                releaseInitialization.TrySetResult(true);
                await Task.WhenAll(importTask, initializationTask)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(playlist.IsWriteLockHeldBMSTablesInitializeMin);
            }
            catch (Exception failure)
            {
                primaryFailure = failure;
                throw;
            }
            finally
            {
                dialogs.ReleaseConfirmation();
                releaseInitialization.TrySetResult(true);
                Task[] pendingTasks = [
                    .. new[] { importTask, initializationTask }
                        .Where(task => task != null)
                        .Cast<Task>()];
                if (pendingTasks.Length > 0)
                {
                    try
                    {
                        await Task.WhenAll(pendingTasks).WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch when (primaryFailure != null)
                    {
                    }
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
            }
            catch when (primaryFailure != null)
            {
            }
        }
    }

    [TestMethod]
    public async Task PlaylistWorkspaceRecommendedImportPropagatesUnavailableConfirmation()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            BMSLibrary library = CreateLibraryWithLr2Id(databasePath);
            var playlist = new TestBmsPlaylist(databasePath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
            {
                ConfirmationResult = UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable)
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                playlistWorkspaceDialogService: dialogs);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => workspace.EnqueueRecommendedPlaylistImportAsync(
                    "bmseeker:table.recommended?mode=readonly"));
            Assert.IsNotNull(dialogs.LastConfirmationRequest);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
    }

    [TestMethod]
    public void PlaylistWorkspaceEntrySnapshotPreservesReferencesAfterTableMutation()
    {
        Assert.ThrowsException<ArgumentNullException>(
            () => PlaylistWorkspaceViewModel.SnapshotPlaylistEntriesExceptDummy(null));

        var entry = new TestablePlaylistEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "entry");
        var dummy = new TestablePlaylistEntry("00000000000000000000000000000000", "dummy");
        var table = new BMSTable { entries = [entry, dummy] };

        IReadOnlyList<BMSTableEntry> snapshot = PlaylistWorkspaceViewModel.SnapshotPlaylistEntriesExceptDummy(table);
        table.entries.Clear();

        CollectionAssert.AreEqual(new[] { entry }, snapshot.ToArray());
    }

    [TestMethod]
    public async Task PlaylistReloadCleanupReadinessAndSnapshotAreOwnedByWorkspace()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var stopwatch = Stopwatch.StartNew();

        await workspace.WaitForPlaylistReloadCleanupReadinessAsync(
            waitForSummaryRefresh: true,
            waitForDetailRefresh: true,
            requestedAtTimestamp: 0L);

        stopwatch.Stop();
        Assert.IsTrue(stopwatch.ElapsedMilliseconds < 1000L);
        PlaylistReloadCleanupSnapshot snapshot = workspace.CapturePlaylistReloadCleanupSnapshot();
        Assert.IsFalse(snapshot.SummaryAlive);
        Assert.AreEqual(0, snapshot.SummaryRowCount);
        Assert.IsFalse(snapshot.PreviousDetailRows.SourceAlive);
        Assert.AreEqual(0, snapshot.PreviousDetailRows.SourceRowCount);
        Assert.IsFalse(snapshot.PreviousDetailRows.ViewAlive);
        Assert.AreEqual(0, snapshot.PreviousDetailRows.ViewRowCount);

        workspace.IsPlaylistSummaryMode = true;
        var firstSummaryRows = new ObservableCollection<PlaylistSummaryRow>
        {
            new PlaylistSummaryRow { PlaylistId = 1 }
        };
        Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = firstSummaryRows,
            PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
        }));
        var secondSummaryRows = new ObservableCollection<PlaylistSummaryRow>
        {
            new PlaylistSummaryRow { PlaylistId = 2 }
        };
        Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = secondSummaryRows,
            PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
        }));
        snapshot = workspace.CapturePlaylistReloadCleanupSnapshot();
        Assert.IsFalse(snapshot.SummaryAlive);
        Assert.AreEqual(0, snapshot.SummaryRowCount);
    }

    [TestMethod]
    public void PlaylistReloadCleanupSkipsSingleReloadInsideWorkspace()
    {
        var log = new List<string>();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            reloadCleanupLog: log.Add);

        Assert.IsFalse(workspace.QueuePlaylistReloadCleanup(isFullReload: false, tableCount: 1));
        Assert.IsTrue(workspace.IsPlaylistReloadCleanupIdle);
        StringAssert.Contains(log[0], "reason=not_full_reload");
    }

    [TestMethod]
    public async Task PlaylistReloadCleanupProcessesLatestPendingFullReload()
    {
        var log = new List<string>();
        var dispatcherEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcherRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int garbageCollectionCount = 0;
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            reloadCleanupDispatcherIdleWaiter: async () =>
            {
                dispatcherEntered.TrySetResult(true);
                await dispatcherRelease.Task.ConfigureAwait(false);
            },
            reloadCleanupGarbageCollector: () => Interlocked.Increment(ref garbageCollectionCount),
            reloadCleanupLog: log.Add);

        Assert.IsTrue(workspace.QueuePlaylistReloadCleanup(isFullReload: true, tableCount: 1));
        await dispatcherEntered.Task.ConfigureAwait(false);
        Task cleanupIdle = workspace.WaitForPlaylistReloadCleanupIdleAsync();
        Assert.IsTrue(workspace.QueuePlaylistReloadCleanup(isFullReload: true, tableCount: 2));
        Assert.IsTrue(workspace.QueuePlaylistReloadCleanup(isFullReload: true, tableCount: 3));
        Assert.IsFalse(cleanupIdle.IsCompleted);
        dispatcherRelease.TrySetResult(true);

        await cleanupIdle.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.IsTrue(workspace.IsPlaylistReloadCleanupIdle);
        Assert.AreEqual(2, garbageCollectionCount);
        Assert.IsTrue(log.Exists(message => message.Contains("completed cleanupId=") && message.Contains("tableCount=1")));
        Assert.IsTrue(log.Exists(message => message.Contains("completed cleanupId=") && message.Contains("tableCount=3")));
        Assert.IsFalse(log.Exists(message => message.Contains("completed cleanupId=") && message.Contains("tableCount=2")));
    }

    [TestMethod]
    public void PlaylistTreeExpansionState_IsOwnedByWorkspaceAndRaisesOnlyOnChange()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        List<string> propertyNames = [];
        workspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        Assert.IsTrue(workspace.IsPlaylistTreeExpanded);
        workspace.IsPlaylistTreeExpanded = true;
        Assert.AreEqual(0, propertyNames.Count);

        workspace.IsPlaylistTreeExpanded = false;
        workspace.IsPlaylistTreeExpanded = true;

        CollectionAssert.AreEqual(
            new[]
            {
                nameof(PlaylistWorkspaceViewModel.IsPlaylistTreeExpanded),
                nameof(PlaylistWorkspaceViewModel.IsPlaylistTreeExpanded)
            },
            propertyNames);
        Assert.IsTrue(workspace.IsPlaylistTreeExpanded);
    }

    [TestMethod]
    public void PlaylistLockState_IsOwnedByWorkspaceAndPreservesPreInitializationFallbacks()
    {
        PlaylistWorkspaceViewModel uninitializedWorkspace = CreateDetailWorkspace(out _);

        Assert.IsTrue(uninitializedWorkspace.IsWriteLockHeldBMSTables);
        Assert.IsTrue(uninitializedWorkspace.IsWriteLockHeldBMSTablesInitializeMin);
        Assert.IsTrue(uninitializedWorkspace.IsWriteLockHeldAnyBMSTable);
        Assert.IsFalse(uninitializedWorkspace.IsPlaylistUpdating);

        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            var playlist = new TestBmsPlaylist(databasePath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);

            Assert.AreEqual(playlist.IsWriteLockHeldBMSTables, workspace.IsWriteLockHeldBMSTables);
            Assert.AreEqual(playlist.IsWriteLockHeldBMSTablesInitializeMin, workspace.IsWriteLockHeldBMSTablesInitializeMin);
            Assert.AreEqual(playlist.IsWriteLockHeldAnyBMSTable, workspace.IsWriteLockHeldAnyBMSTable);
            Assert.AreEqual(playlist.IsPlaylistUpdating, workspace.IsPlaylistUpdating);

            playlist.AcquireWriterLockBMSTables();
            try
            {
                Assert.IsTrue(workspace.IsWriteLockHeldBMSTables);
            }
            finally
            {
                playlist.FreeWriterLockBMSTables();
            }

            var table = new BMSTable();
            playlist.BMSTables.Add(table);
            using (table.ReaderWriterLock.GetWriterGuard())
            {
                Assert.IsTrue(workspace.IsWriteLockHeldAnyBMSTable);
            }

            playlist.IsPlaylistUpdating = true;
            Assert.IsTrue(workspace.IsPlaylistUpdating);
            playlist.IsPlaylistUpdating = false;
            Assert.IsFalse(workspace.IsPlaylistUpdating);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
    }

    [TestMethod]
    public void Constructor_RequiresExplicitCustomFolderOutputSettingsProvider()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            null,
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
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
    }

}
