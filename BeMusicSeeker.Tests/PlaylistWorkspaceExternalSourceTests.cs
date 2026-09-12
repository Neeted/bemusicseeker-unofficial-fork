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
public sealed class PlaylistWorkspaceExternalSourceTests
{
    [TestInitialize]
    public void TestInitialize()
    {
        TestResourceInitializer.EnsureJapaneseResources();
    }

    [TestMethod]
    public void SubmitExternalPlaylistUriText_AllInvalidReturnsValidationFactsWithoutEnqueueing()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);

        ExternalPlaylistUriSubmissionResult result = workspace.SubmitExternalPlaylistUriText("  not-a-uri  \r\n\t");

        Assert.IsFalse(result.HasValidUris);
        Assert.IsTrue(result.HasInvalidLines);
        CollectionAssert.AreEqual(new[] { "not-a-uri" }, result.InvalidLines.ToList());
    }

    [TestMethod]
    public async Task SubmitExternalPlaylistUriText_QueuesValidUrisInInputOrderAndCompletesImport()
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
            string firstHeaderPath = Path.Combine(tempDirectory, "first.json");
            string firstDataPath = Path.Combine(tempDirectory, "first-data.json");
            string secondHeaderPath = Path.Combine(tempDirectory, "second.json");
            string secondDataPath = Path.Combine(tempDirectory, "second-data.json");
            File.WriteAllText(firstHeaderPath, "{\"name\":\"FirstImport\",\"symbol\":\"F\",\"output_dir\":\"FirstImport\",\"data_url\":\"./first-data.json\"}");
            File.WriteAllText(firstDataPath, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"First song\",\"artist\":\"Artist\",\"level\":\"1\"}]");
            File.WriteAllText(secondHeaderPath, "{\"name\":\"SecondImport\",\"symbol\":\"S\",\"output_dir\":\"SecondImport\",\"data_url\":\"./second-data.json\"}");
            File.WriteAllText(secondDataPath, "[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Second song\",\"artist\":\"Artist\",\"level\":\"2\"}]");

            TestBmsPlaylist playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            var library = new TestBmsLibrary(songDbPath);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library);
            var summaryReady = new TaskCompletionSource<ExternalPlaylistImportQueueSummary>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.ExternalPlaylistImportQueueSummaryReady += (_, request) =>
                summaryReady.TrySetResult(request.Summary);

            string firstUri = new Uri(firstHeaderPath).AbsoluteUri;
            string secondUri = new Uri(secondHeaderPath).AbsoluteUri;
            ExternalPlaylistUriSubmissionResult submission = workspace.SubmitExternalPlaylistUriText(
                firstUri + "\r\nnot-a-uri\r\n \r\n" + secondUri);

            Assert.IsTrue(submission.HasValidUris);
            Assert.AreEqual(2, submission.ValidUriCount);
            CollectionAssert.AreEqual(new[] { "not-a-uri" }, submission.InvalidLines.ToList());
            ExternalPlaylistImportQueueSummary summary = await summaryReady.Task
                .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            Assert.AreEqual(2, summary.ImportedCount);
            Assert.AreEqual(0, summary.FailedCount);
            CollectionAssert.AreEqual(
                new[] { firstUri, secondUri },
                summary.Outcomes.Select(outcome => outcome.Uri.AbsoluteUri).ToArray());
            CollectionAssert.AreEqual(
                new[] { "FirstImport", "SecondImport" },
                playlist.BMSTables.Select(table => table.name).ToArray());
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
    public void ExternalPlaylistSourceRequestsRejectUnavailableStoreBeforeQueueing()
    {
        PlaylistWorkspaceViewModel unavailableWorkspace = CreateDetailWorkspace(out _);

        Assert.IsFalse(unavailableWorkspace.TryEnqueueExternalPlaylistCollectionImport(
            new BMSTableSimple { url = new Uri("https://example.test/collection.json") }));
        Assert.IsFalse(unavailableWorkspace.TryEnqueueBuiltInExternalPlaylistImport("https://example.test/built-in.json"));
    }

    [TestMethod]
    public void PlaylistRootContextMenuAvailability_UsesWorkspaceOwnedStateSnapshot()
    {
        PlaylistWorkspaceViewModel unavailableWorkspace = CreateDetailWorkspace(out _);
        PlaylistRootContextMenuAvailability unavailable =
            unavailableWorkspace.CapturePlaylistRootContextMenuAvailability();

        Assert.IsFalse(unavailable.CanCreatePlaylist);
        Assert.IsFalse(unavailable.CanLoadPlaylistUri);
        Assert.IsFalse(unavailable.CanLoadPlaylistCollection);
        Assert.IsFalse(unavailable.CanLoadBuiltInTables);

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
            PlaylistWorkspaceViewModel initializedWorkspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);
            PlaylistRootContextMenuAvailability initialized =
                initializedWorkspace.CapturePlaylistRootContextMenuAvailability();

            Assert.IsTrue(initialized.CanCreatePlaylist);
            Assert.IsTrue(initialized.CanLoadPlaylistUri);
            Assert.IsTrue(initialized.CanLoadPlaylistCollection);
            Assert.IsTrue(initialized.CanLoadBuiltInTables);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(databasePath)!))
            {
                Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SettingsWorkspacePort_ExposesCanonicalImmutablePlaylistSnapshot()
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
            var table = new BMSTable
            {
                playlist_id = 1,
                name = "Initial",
                symbol = "I"
            };
            var playlist = new TestBmsPlaylist(databasePath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);
            ISettingsDialogWorkspacePort settingsPort = workspace;

            Assert.IsTrue(settingsPort.HasPlaylistTables);
            PlaylistTablePresentationSnapshot snapshot = settingsPort.CapturePlaylistPresentationSnapshots().Single();
            table.name = "Changed";

            Assert.AreEqual("Initial", snapshot.Name);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(databasePath)!))
            {
                Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SettingsWorkspacePort_CatalogVersionDefersSettingsFanoutUntilDialogIsVisible()
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
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
            var catalogNotificationQueue = new Queue<Action>();
            workspace.ConfigureCatalogNotificationQueue(catalogNotificationQueue.Enqueue);
            SettingsDialogViewModel dialog = viewModel.SettingDialog;
            ISettingsDialogWorkspacePort settingsPort = workspace;
            var settingNotifications = new List<string>();
            dialog.PropertyChanged += (_, eventArgs) =>
                settingNotifications.Add(eventArgs.PropertyName!);
            dialog.SetPresentationActive(active: true);
            settingNotifications.Clear();
            int catalogEventCount = 0;
            settingsPort.PlaylistCatalogChanged += (_, _) => catalogEventCount++;
            long presentationVersion = settingsPort.PlaylistCatalogVersion;

            workspace.PlaylistSummaryText = "summary";
            workspace.PublishColumnPresentation(
                workspace.CommitColumnPresentationWithoutNotification(
                    Visibility.Collapsed,
                    new PlaylistSummaryColumnSettings()));
            workspace.IsPlaylistDetailViewActive = true;
            workspace.SetPlaylistSummaryMode(enabled: true);

            Assert.AreEqual(0, catalogEventCount);
            Assert.AreEqual(presentationVersion, settingsPort.PlaylistCatalogVersion);
            CollectionAssert.DoesNotContain(
                settingNotifications,
                nameof(SettingsDialogViewModel.LR2ConfigBMSDirectories));
            CollectionAssert.DoesNotContain(
                settingNotifications,
                nameof(SettingsDialogViewModel.AvailableBMSDirectories));

            long previousVersion = settingsPort.PlaylistCatalogVersion;
            dialog.SetPresentationActive(active: false);
            var playlist = new TestBmsPlaylist(databasePath)
            {
                BMSTables = new ObservableCollection<BMSTable>(
                [
                    new BMSTable
                    {
                        playlist_id = 1,
                        name = "Catalog",
                        symbol = "C",
                    }
                ])
            };
            workspace.RefreshPlaylistTreeTables(playlist);

            Assert.IsTrue(settingsPort.PlaylistCatalogVersion > previousVersion);
            Assert.AreEqual(1, catalogNotificationQueue.Count);
            catalogNotificationQueue.Dequeue()();
            Assert.AreEqual(1, catalogEventCount);
            CollectionAssert.DoesNotContain(
                settingNotifications,
                nameof(SettingsDialogViewModel.LR2ConfigBMSDirectories));
            CollectionAssert.DoesNotContain(
                settingNotifications,
                nameof(SettingsDialogViewModel.AvailableBMSDirectories));

            dialog.SetPresentationActive(active: true);

            Assert.AreEqual(
                1,
                settingNotifications.Count(name =>
                    name == nameof(SettingsDialogViewModel.LR2ConfigBMSDirectories)));
            Assert.AreEqual(
                1,
                settingNotifications.Count(name =>
                    name == nameof(SettingsDialogViewModel.AvailableBMSDirectories)));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(databasePath)!))
            {
                Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SettingsWorkspacePort_CatalogCallbackIsQueuedAndCoalescesLatestVersion()
    {
        var presentationQueue = new Queue<Action>();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            dispatchPresentation: presentationQueue.Enqueue);
        ISettingsDialogWorkspacePort settingsPort = workspace;
        int eventCount = 0;
        long publishedVersion = 0L;
        settingsPort.PlaylistCatalogChanged += (_, eventArgs) =>
        {
            eventCount++;
            publishedVersion = eventArgs.Version;
        };

        workspace.RefreshPlaylistTreeTables(null);
        workspace.RefreshPlaylistTreeTables(null);

        Assert.AreEqual(0, eventCount);
        Assert.AreEqual(1, presentationQueue.Count);
        long latestVersion = settingsPort.PlaylistCatalogVersion;

        presentationQueue.Dequeue()();

        Assert.AreEqual(1, eventCount);
        Assert.AreEqual(latestVersion, publishedVersion);
    }

    [TestMethod]
    public async Task SettingsWorkspacePort_CatalogMutationPublishesTypedVersion()
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
                "Catalog entry")
            {
                playlist_id = 7820,
                folder = "Folder"
            };
            BMSTable table = new()
            {
                playlist_id = 7820,
                name = "Catalog target",
                symbol = "CAT",
                compat_prefix = string.Empty,
                Page_url = new Uri("https://example.test/catalog"),
                Header_url = new Uri("https://example.test/catalog.json"),
                Data_url = new Uri("https://example.test/catalog-data.json"),
                Output_dir = "CatalogTarget",
                is_root_folder = true,
                custom_folder_output_base_name = "OldBase",
                entries = [entry],
                Folder_order = ["Folder"]
            };
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                seed.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
            }

            TestBmsPlaylist playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var library = new TestBmsLibrary(songDbPath);
            var propertySaveService = new PlaylistPropertySaveService(
                () => playlist,
                () => library,
                () => null!,
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            var catalogNotificationQueue = new Queue<Action>();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                propertySaveService: propertySaveService,
                catalogNotificationQueue: catalogNotificationQueue.Enqueue);
            workspace.PlaylistReferenceSortInvalidationRequested += (_, _) => { };
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };

            ISettingsDialogWorkspacePort settingsPort = workspace;
            var publishedEvents = new List<PlaylistCatalogChangedEventArgs>();
            settingsPort.PlaylistCatalogChanged += (_, eventArgs) => publishedEvents.Add(eventArgs);

            long CompletePublishedMutation(long previousVersion, int previousEventCount)
            {
                Assert.IsTrue(
                    settingsPort.PlaylistCatalogVersion > previousVersion,
                    "The catalog version did not advance for the mutation.");
                Assert.IsTrue(
                    catalogNotificationQueue.Count > 0,
                    "The mutation did not queue a typed catalog notification.");
                while (catalogNotificationQueue.Count > 0)
                {
                    catalogNotificationQueue.Dequeue()();
                }
                Assert.AreEqual(previousEventCount + 1, publishedEvents.Count);
                PlaylistCatalogChangedEventArgs latestEvent = publishedEvents[^1];
                Assert.AreEqual(settingsPort.PlaylistCatalogVersion, latestEvent.Version);
                Assert.IsTrue(latestEvent.Version > previousVersion);
                return latestEvent.Version;
            }

            long previousVersion = settingsPort.PlaylistCatalogVersion;
            int previousEventCount = publishedEvents.Count;
            workspace.RefreshPlaylistTreeTables(playlist);
            Assert.AreSame(playlist.BMSTables, workspace.PlaylistTreeTables);
            CompletePublishedMutation(previousVersion, previousEventCount);

            previousVersion = settingsPort.PlaylistCatalogVersion;
            previousEventCount = publishedEvents.Count;
            PlaylistSummaryPropertyEditCompletion nameEdit =
                await workspace.CompleteSummaryPropertyEditAsync(
                    new PlaylistSummaryRow { TableRef = table },
                    nameof(PlaylistSummaryRow.Name),
                    "Catalog renamed",
                    commit: true);
            Assert.IsTrue(nameEdit.IsApplied);
            Assert.AreEqual("Catalog renamed", table.name);
            CompletePublishedMutation(previousVersion, previousEventCount);

            previousVersion = settingsPort.PlaylistCatalogVersion;
            previousEventCount = publishedEvents.Count;
            PlaylistSummaryPropertyEditCompletion entriesEdit =
                await workspace.CompleteSummaryPropertyEditAsync(
                    new PlaylistSummaryRow { TableRef = table },
                    nameof(PlaylistSummaryRow.CompatPrefix),
                    "★",
                    commit: true);
            Assert.IsTrue(entriesEdit.IsApplied);
            Assert.AreEqual("★Folder", table.entries.Single().folder);
            CompletePublishedMutation(previousVersion, previousEventCount);

            previousVersion = settingsPort.PlaylistCatalogVersion;
            previousEventCount = publishedEvents.Count;
            workspace.ApplyPlaylistSummaryOutputBase(
                [new PlaylistSummaryRow { TableRef = table }],
                outputBaseName: string.Empty);
            Assert.IsTrue(string.IsNullOrWhiteSpace(table.custom_folder_output_base_name));
            CompletePublishedMutation(previousVersion, previousEventCount);

            for (int index = 1; index < publishedEvents.Count; index++)
            {
                Assert.IsTrue(publishedEvents[index - 1].Version < publishedEvents[index].Version);
            }
            Assert.AreEqual(
                settingsPort.PlaylistCatalogVersion,
                publishedEvents[^1].Version);
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
    public void SettingsWorkspacePort_CatalogCallbackFailureDoesNotBlockNextVersion()
    {
        var presentationQueue = new Queue<Action>();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            dispatchPresentation: presentationQueue.Enqueue);
        ISettingsDialogWorkspacePort settingsPort = workspace;
        bool fail = true;
        int successfulCount = 0;
        settingsPort.PlaylistCatalogChanged += (_, _) =>
        {
            if (fail)
            {
                fail = false;
                throw new InvalidOperationException("subscriber failed");
            }
            successfulCount++;
        };

        workspace.RefreshPlaylistTreeTables(null);
        Assert.ThrowsException<InvalidOperationException>(() => presentationQueue.Dequeue()());

        workspace.RefreshPlaylistTreeTables(null);
        Assert.AreEqual(1, presentationQueue.Count);
        presentationQueue.Dequeue()();

        Assert.AreEqual(1, successfulCount);
    }

    [TestMethod]
    public void SettingsWorkspacePort_DeferredCollectionMutationStillPublishesCatalogVersion()
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
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                presentationRefreshDeferredProvider: request =>
                    request.Kind == PlaylistPresentationRefreshKind.Tree);
            ISettingsDialogWorkspacePort settingsPort = workspace;
            int eventCount = 0;
            settingsPort.PlaylistCatalogChanged += (_, _) => eventCount++;
            workspace.RefreshPlaylistTreeTables(playlist);
            long attachedVersion = settingsPort.PlaylistCatalogVersion;

            playlist.BMSTables.Add(new BMSTable
            {
                playlist_id = 2,
                name = "Deferred",
                symbol = "D",
            });

            Assert.AreEqual(2, eventCount);
            Assert.IsTrue(settingsPort.PlaylistCatalogVersion > attachedVersion);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(databasePath)!))
            {
                Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
            }
        }
    }


    [TestMethod]
    public void SettingsWorkspacePort_DelegatesBackgroundPublishRequestsToAttachedPlaylist()
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
            var scheduled = new List<string>();
            playlist.StartupBackgroundTaskScheduler = (kind, reason, dependency, work) =>
            {
                scheduled.Add(kind + ":" + reason);
                return true;
            };
            int providerCalls = 0;
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () =>
                {
                    providerCalls++;
                    return playlist;
                });
            ISettingsDialogWorkspacePort settingsPort = workspace;
            providerCalls = 0;

            settingsPort.SchedulePlaylistUrlCompletionRefresh("settings-test");
            settingsPort.QueueBeatorajaBmtExportAll("settings-test", null);

            CollectionAssert.AreEqual(
                new[] { "playlist_url_completion:settings-test", "beatoraja_bmt_export_all:settings-test" },
                scheduled);
            Assert.AreEqual(2, providerCalls);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(databasePath)!))
            {
                Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SettingsWorkspacePort_BackgroundPublishRequestsAreNoOpWhenStoreDetached()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistStoreProvider: () => null!);
        ISettingsDialogWorkspacePort settingsPort = workspace;

        settingsPort.SchedulePlaylistUrlCompletionRefresh("detached-test");
        settingsPort.QueueBeatorajaBmtExportAll("detached-test", null);
    }

    [TestMethod]
    public void ExternalPlaylistSourceRequestsPreserveNullSourceAndMalformedUriContracts()
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
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist);

            Assert.IsFalse(workspace.TryEnqueueExternalPlaylistCollectionImport(new BMSTableSimple()));
            Assert.ThrowsException<UriFormatException>(
                () => workspace.TryEnqueueBuiltInExternalPlaylistImport("http://["));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(databasePath)!))
            {
                Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ExternalPlaylistSourceRequests_QueueCatalogAndBuiltInImportsInOrder()
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
            string firstHeaderPath = Path.Combine(tempDirectory, "catalog.json");
            string firstDataPath = Path.Combine(tempDirectory, "catalog-data.json");
            string secondHeaderPath = Path.Combine(tempDirectory, "walkure.json");
            string secondDataPath = Path.Combine(tempDirectory, "walkure-data.json");
            File.WriteAllText(firstHeaderPath, "{\"name\":\"CatalogImport\",\"symbol\":\"C\",\"output_dir\":\"CatalogImport\",\"data_url\":\"./catalog-data.json\"}");
            File.WriteAllText(firstDataPath, "[{\"md5\":\"cccccccccccccccccccccccccccccccc\",\"title\":\"Catalog song\",\"artist\":\"Artist\",\"level\":\"3\"}]");
            File.WriteAllText(secondHeaderPath, "{\"name\":\"WalkureImport\",\"symbol\":\"W\",\"output_dir\":\"WalkureImport\",\"data_url\":\"./walkure-data.json\"}");
            File.WriteAllText(secondDataPath, "[{\"md5\":\"dddddddddddddddddddddddddddddddd\",\"title\":\"Walkure song\",\"artist\":\"Artist\",\"level\":\"4\"}]");

            TestBmsPlaylist playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            var library = new TestBmsLibrary(songDbPath);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library);
            var summaryReady = new TaskCompletionSource<ExternalPlaylistImportQueueSummary>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.ExternalPlaylistImportQueueSummaryReady += (_, request) =>
                summaryReady.TrySetResult(request.Summary);

            bool catalogAccepted = workspace.TryEnqueueExternalPlaylistCollectionImport(
                new BMSTableSimple { url = new Uri(firstHeaderPath) });
            bool builtInAccepted = workspace.TryEnqueueBuiltInExternalPlaylistImport(
                new Uri(secondHeaderPath).AbsoluteUri);

            Assert.IsTrue(catalogAccepted);
            Assert.IsTrue(builtInAccepted);
            ExternalPlaylistImportQueueSummary summary = await summaryReady.Task
                .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            Assert.AreEqual(2, summary.ImportedCount);
            CollectionAssert.AreEqual(
                new[] { new Uri(firstHeaderPath).AbsoluteUri, new Uri(secondHeaderPath).AbsoluteUri },
                summary.Outcomes.Select(outcome => outcome.Uri.AbsoluteUri).ToArray());
            CollectionAssert.AreEqual(
                new[] { "CatalogImport", "WalkureImport" },
                playlist.BMSTables.Select(table => table.name).ToArray());
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
    public async Task ExternalTableListCatalog_SlowFetchDoesNotBlockCallerAndPublishesAfterCompletion()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var fetchGate = new TaskCompletionSource<IReadOnlyList<BMSTableSimple>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var table = new BMSTableSimple
        {
            tag1 = "Deferred",
            name = "Loaded",
            url = new Uri("https://example.test/table")
        };

        Task loadTask = workspace.LoadExternalTableCollectionAsync(
            new Uri("https://example.test/catalog"),
            (_, _) => fetchGate.Task);

        Assert.IsFalse(loadTask.IsCompleted);
        fetchGate.SetResult([table]);
        await loadTask.ConfigureAwait(false);

        Assert.IsNotNull(workspace.BMSExternalTableListExt);
        Assert.AreEqual("Deferred", workspace.BMSExternalTableListExt.Children[0].name);
        Assert.AreEqual("Loaded", workspace.BMSExternalTableListExt.Children[0].Children[0].name);
    }

    [TestMethod]
    public async Task ExternalTableListCatalog_ShutdownCancelsAndDoesNotPublishStaleCatalog()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var cancellationObserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var staleCatalog = new[]
        {
            new BMSTableSimple
            {
                tag1 = "Stale",
                name = "Must not publish",
                url = new Uri("https://example.test/stale")
            }
        };

        Task loadTask = workspace.LoadExternalTableCollectionAsync(
            new Uri("https://example.test/catalog"),
            async (_, cancellationToken) =>
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(
                    () => cancellationObserved.TrySetResult(true));
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return staleCatalog;
            });

        Assert.IsFalse(loadTask.IsCompleted);
        workspace.CancelExternalTableCollectionLoadForShutdown();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await loadTask.ConfigureAwait(false);

        Assert.IsNull(workspace.BMSExternalTableListExt);
    }

    [TestMethod]
    public async Task ExternalTableListCatalog_NewRequestCancelsAndRejectsStaleCompletion()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var firstGate = new TaskCompletionSource<IReadOnlyList<BMSTableSimple>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource<IReadOnlyList<BMSTableSimple>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task firstLoad = workspace.LoadExternalTableCollectionAsync(
            new Uri("https://example.test/first"),
            (_, _) => firstGate.Task);
        Task secondLoad = workspace.LoadExternalTableCollectionAsync(
            new Uri("https://example.test/second"),
            (_, _) => secondGate.Task);

        secondGate.SetResult(
        [
            new BMSTableSimple
            {
                tag1 = "Current",
                name = "Second",
                url = new Uri("https://example.test/current")
            }
        ]);
        await secondLoad.ConfigureAwait(false);
        firstGate.SetResult(
        [
            new BMSTableSimple
            {
                tag1 = "Stale",
                name = "First",
                url = new Uri("https://example.test/stale")
            }
        ]);
        await firstLoad.ConfigureAwait(false);

        Assert.AreEqual("Current", workspace.BMSExternalTableListExt.Children[0].name);
        Assert.AreEqual("Second", workspace.BMSExternalTableListExt.Children[0].Children[0].name);
        Assert.IsNotNull(workspace.BMSExternalTableListExt);
    }

    [TestMethod]
    public void ExternalTableListCatalog_PreservesHierarchyOrderAndLeafMenuShape()
    {
        var firstLeaf = new BMSTableSimple
        {
            tag1 = "First",
            name = "direct",
            url = new Uri("https://example.test/direct")
        };
        var secondLeaf = new BMSTableSimple
        {
            tag1 = "Second",
            name = "second",
            url = new Uri("https://example.test/second")
        };
        var nestedZ = new BMSTableSimple
        {
            tag1 = "First",
            tag2 = "Zeta",
            name = "z-name",
            url = new Uri("https://example.test/z")
        };
        var nestedA = new BMSTableSimple
        {
            tag1 = "First",
            tag2 = "Alpha",
            name = "a-name",
            url = new Uri("https://example.test/a")
        };

        BMSTableSimpleCategorized catalog = PlaylistWorkspaceViewModel.BuildExternalTableListCatalog(
            [firstLeaf, nestedZ, nestedA, secondLeaf]);

        Assert.AreEqual(2, catalog.Children.Count);
        Assert.AreEqual("First", catalog.Children[0].name);
        Assert.AreEqual("Second", catalog.Children[1].name);
        Assert.AreEqual(3, catalog.Children[0].Children.Count);
        Assert.AreEqual("direct", catalog.Children[0].Children[0].name);
        Assert.IsNotNull(catalog.Children[0].Children[1].Children);
        Assert.AreEqual("Alpha", catalog.Children[0].Children[1].name);
        Assert.AreEqual("Zeta", catalog.Children[0].Children[2].name);
        Assert.IsNotNull(catalog.Children[0].Children[1].Children[0].url);
        Assert.IsNotNull(catalog.Children[0].Children[2].Children[0].url);
        Assert.IsNull(catalog.Children[0].Children[0].Children);
    }

}
