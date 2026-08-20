using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistWorkspaceViewModelTests
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
    public void ExternalPlaylistSourceRequestsApplyLockGateBeforeUriConstruction()
    {
        PlaylistWorkspaceViewModel lockedWorkspace = CreateDetailWorkspace(out _);

        Assert.IsFalse(lockedWorkspace.TryEnqueueExternalPlaylistCollectionImport(
            new BMSTableSimple { url = new Uri("https://example.test/collection.json") }));
        Assert.IsFalse(lockedWorkspace.TryEnqueueBuiltInExternalPlaylistImport("http://["));
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
                settingNotifications.Add(eventArgs.PropertyName);
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
        Assert.IsTrue(IsExternalTableListLoading(workspace));
        fetchGate.SetResult([table]);
        await loadTask.ConfigureAwait(false);

        Assert.IsFalse(IsExternalTableListLoading(workspace));
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

        Assert.IsFalse(IsExternalTableListLoading(workspace));
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
        Assert.IsFalse(IsExternalTableListLoading(workspace));
    }

    private static bool IsExternalTableListLoading(PlaylistWorkspaceViewModel workspace)
    {
        FieldInfo field = typeof(PlaylistWorkspaceViewModel).GetField(
            "isLoadingExternalTableList",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (bool)field.GetValue(workspace);
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
            BMSLibrary library = new TestBmsLibrary(songDbPath);
            typeof(BMSLibrary)
                .GetField("_LR2ID", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(library, 123);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library);

            BMSTable normalTable = new()
            {
                Page_url = new Uri("https://example.test/table")
            };
            PlaylistTableContextMenuAvailability normal =
                workspace.CapturePlaylistTableContextMenuAvailability(normalTable);
            Assert.IsTrue(normal.CanReload);
            Assert.IsTrue(normal.CanOpenPage);
            Assert.IsFalse(normal.CanOpenClearLamp);
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
            Assert.IsTrue(external.CanOpenClearLamp);
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
    public void PlaylistTableExternalLinks_UseLibraryPlayerIdForRecommendedAndClearLampRoutes()
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
            BMSLibrary library = new TestBmsLibrary(songDbPath);
            typeof(BMSLibrary)
                .GetField("_LR2ID", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(library, 123);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistLibraryProvider: () => library);

            BMSTable recommendedTable = new() { Page_url = new Uri("bmseeker:table.recommended") };
            Assert.IsTrue(workspace.TryResolvePlaylistTablePageUri(recommendedTable, out Uri recommendedUri));
            Assert.AreEqual(
                "http://walkure.net/hakkyou/recommended_mypage.html?playerid=123",
                recommendedUri.ToString());

            BMSTable clearLampTable = new()
            {
                Page_url = new Uri("https://example.test/table?a=1&b=%E6%97%A5%E6%9C%AC"),
                is_external_sync = true
            };
            Assert.IsTrue(workspace.CapturePlaylistTableContextMenuAvailability(clearLampTable).CanOpenClearLamp);
            Assert.IsTrue(workspace.TryResolvePlaylistTableClearLampUri(clearLampTable, out Uri clearLampUri));
            string expectedTableQuery = Uri.EscapeDataString(clearLampTable.Page_url.ToString());
            Assert.AreEqual(
                "http://xyzzz.net/bms/clearlamp?lr2ID=123&table_url=" + expectedTableQuery,
                clearLampUri.OriginalString);

            clearLampTable.Page_url = new Uri("bmseeker:table.recommended");
            Assert.IsFalse(workspace.CapturePlaylistTableContextMenuAvailability(clearLampTable).CanOpenClearLamp);
            Assert.IsFalse(workspace.TryResolvePlaylistTableClearLampUri(clearLampTable, out _));
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
    public void PlaylistTableExternalLinks_SkipLibraryLookupForIneligibleClearLampTables()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistLibraryProvider: () => throw new InvalidOperationException("library lookup should not run"));

        PlaylistTableContextMenuAvailability nullAvailability =
            workspace.CapturePlaylistTableContextMenuAvailability(null);
        Assert.IsFalse(nullAvailability.CanOpenClearLamp);
        Assert.IsFalse(workspace.TryResolvePlaylistTableClearLampUri(null, out _));
        PlaylistTableContextMenuAvailability specialAvailability =
            workspace.CapturePlaylistTableContextMenuAvailability(
                new BMSTable { Page_url = new Uri("bmseeker:table.recommended"), is_external_sync = true });
        Assert.IsFalse(specialAvailability.CanOpenClearLamp);
        Assert.IsFalse(workspace.TryResolvePlaylistTableClearLampUri(
            new BMSTable { Page_url = new Uri("bmseeker:table.recommended"), is_external_sync = true },
            out _));
        PlaylistTableContextMenuAvailability localAvailability =
            workspace.CapturePlaylistTableContextMenuAvailability(
                new BMSTable { Page_url = new Uri("https://example.test/table"), is_external_sync = false });
        Assert.IsFalse(localAvailability.CanOpenClearLamp);
        Assert.IsFalse(workspace.TryResolvePlaylistTableClearLampUri(
            new BMSTable { Page_url = new Uri("https://example.test/table"), is_external_sync = false },
            out _));
        Assert.IsFalse(workspace.TryResolvePlaylistTablePageUri(
            new BMSTable { Page_url = new Uri("bmseeker:table.other") },
            out _));
        Assert.ThrowsException<InvalidOperationException>(() =>
            workspace.CapturePlaylistTableContextMenuAvailability(
                new BMSTable { Page_url = new Uri("https://example.test/table"), is_external_sync = true }));
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
            BMSLibrary library = new TestBmsLibrary(databasePath);
            typeof(BMSLibrary)
                .GetField("_LR2ID", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(library, 123);
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
            BMSLibrary library = new TestBmsLibrary(databasePath);
            typeof(BMSLibrary)
                .GetField("_LR2ID", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(library, 123);
            var playlist = new TestBmsPlaylist(databasePath)
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
            IDisposable? initializationGuard = null;
            dialogs.ConfirmationFactory = _ =>
            {
                object initializationLock = typeof(BMSPlaylist)
                    .GetField("rwlockBMSTablesInitializeMin", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(playlist)!;
                initializationGuard = (IDisposable)initializationLock
                    .GetType()
                    .GetMethod("GetWriterGuard", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                    .Invoke(initializationLock, null)!;
                return UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
            };
            try
            {
                Assert.IsFalse(await workspace.EnqueueRecommendedPlaylistImportAsync(
                    "bmseeker:table.recommended?mode=update"));
            }
            finally
            {
                initializationGuard?.Dispose();
            }
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
            BMSLibrary library = new TestBmsLibrary(databasePath);
            typeof(BMSLibrary)
                .GetField("_LR2ID", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(library, 123);
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

    [TestMethod]
    public void BuildDetailSourceRows_FiltersFolderAndRemovedEntriesInsideWorkspace()
    {
        var workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        var included = new TestablePlaylistEntry("11111111111111111111111111111111", "included") { folder = "target" };
        var otherFolder = new TestablePlaylistEntry("22222222222222222222222222222222", "other") { folder = "other" };
        var removed = new TestablePlaylistEntry("33333333333333333333333333333333", "removed") { folder = "target", is_removed = true };
        var table = new BMSTable
        {
            entries = [included, otherFolder, removed]
        };
        string cancellationStage = string.Empty;

        PlaylistSourceBuildResult result = workspace.BuildDetailSourceRows(
            table,
            "target",
            onlyNotOwned: false,
            new PlaylistLibraryIndexSnapshot { ResolveIndex = PlaylistLibraryResolveIndexSnapshot.Empty },
            CancellationToken.None,
            ref cancellationStage);

        Assert.AreEqual(1, result.SourceRows.Count);
        Assert.AreSame(included, result.SourceRows[0].Entry);
        Assert.AreEqual(1, dataSource.EnsureEntriesLoadedCallCount);
        Assert.AreEqual("source_row_materialize", cancellationStage);
    }

    [TestMethod]
    public void TryPatchDetailSourceChartInfo_ReplacesCurrentGenerationWithoutMutatingOldRow()
    {
        var workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        var oldInfo = new LR2SongDBExtended.chart_info
        {
            sha256 = new string('a', 64),
            parser_version = 1,
            updated_at = new DateTime(2026, 1, 1)
        };
        var newInfo = new LR2SongDBExtended.chart_info
        {
            sha256 = oldInfo.sha256,
            parser_version = 2,
            updated_at = new DateTime(2026, 2, 1)
        };
        var entry = new TestablePlaylistEntry("44444444444444444444444444444444", "patch");
        entry.SetSha256(oldInfo.sha256);
        var oldRow = new PlaylistDetailSourceRow(entry, resolvedChart: null, entryChartInfo: oldInfo);
        workspace.DetailViewState.Source.Rows = [oldRow];
        workspace.DetailBuildState.RequestVersion = 7;
        dataSource.ChartInfo = newInfo;
        var request = new PlaylistBuildRequest
        {
            RequestVersion = 7,
            Identity = PlaylistRequestFactory.CreateIdentity(
                new BMSTable(),
                null,
                PlaylistDetailFilter.PlaylistFilter,
                null,
                ChartModeFilter.All,
                null,
                libraryIndexVersion: 1,
                playlistRevision: 1,
                scoreSnapshotVersion: 1,
                chartInfoIndexVersion: 2,
                hasResolvedSelection: true)
        };

        bool patched = workspace.TryPatchDetailSourceChartInfo(
            request,
            CancellationToken.None,
            out int sourceCount,
            out int dependencyCount,
            out int patchedCount,
            out _);

        Assert.IsTrue(patched);
        Assert.AreEqual(1, sourceCount);
        Assert.AreEqual(1, dependencyCount);
        Assert.AreEqual(1, patchedCount);
        Assert.AreSame(oldInfo, oldRow.EntryChartInfo);
        Assert.AreNotSame(oldRow, workspace.DetailViewState.Source.Rows[0]);
        Assert.AreSame(newInfo, workspace.DetailViewState.Source.Rows[0].EntryChartInfo);
        Assert.AreEqual(2, workspace.DetailViewState.Source.LastBuiltChartInfoIndexVersion);
    }

    [TestMethod]
    public void TryPatchDetailSourceChartInfo_RejectsStaleRequestWithoutReplacingSource()
    {
        var workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        var oldInfo = new LR2SongDBExtended.chart_info
        {
            sha256 = new string('b', 64),
            parser_version = 1,
            updated_at = new DateTime(2026, 1, 1)
        };
        var entry = new TestablePlaylistEntry("55555555555555555555555555555555", "stale");
        var oldRow = new PlaylistDetailSourceRow(entry, resolvedChart: null, entryChartInfo: oldInfo);
        List<PlaylistDetailSourceRow> oldRows = [oldRow];
        workspace.DetailViewState.Source.Rows = oldRows;
        workspace.DetailBuildState.RequestVersion = 8;
        dataSource.ChartInfo = new LR2SongDBExtended.chart_info
        {
            sha256 = oldInfo.sha256,
            parser_version = 2,
            updated_at = new DateTime(2026, 2, 1)
        };
        var staleRequest = new PlaylistBuildRequest
        {
            RequestVersion = 7,
            Identity = PlaylistRequestFactory.CreateIdentity(
                new BMSTable(), null, PlaylistDetailFilter.PlaylistFilter, null,
                ChartModeFilter.All, null, 1, 1, 1, 2, hasResolvedSelection: true)
        };

        bool patched = workspace.TryPatchDetailSourceChartInfo(
            staleRequest,
            CancellationToken.None,
            out _, out _, out _, out _);

        Assert.IsFalse(patched);
        Assert.AreSame(oldRows, workspace.DetailViewState.Source.Rows);
        Assert.AreSame(oldInfo, oldRow.EntryChartInfo);
    }

    [TestMethod]
    public void BuildDetailSourceRows_OnlyNotOwnedExcludesResolvedLibraryCharts()
    {
        var workspace = CreateDetailWorkspace(out _);
        var owned = new TestablePlaylistEntry("66666666666666666666666666666666", "owned");
        var missing = new TestablePlaylistEntry("77777777777777777777777777777777", "missing");
        var table = new BMSTable { entries = [owned, missing] };
        PlaylistLibraryResolveIndexSnapshot resolveIndex = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
            [LibraryChartRef.FromPath(LibraryChartKind.Bms, @"C:\songs\owned.bms", owned.md5, null)]);
        string cancellationStage = string.Empty;

        PlaylistSourceBuildResult result = workspace.BuildDetailSourceRows(
            table,
            null,
            onlyNotOwned: true,
            new PlaylistLibraryIndexSnapshot { ResolveIndex = resolveIndex },
            CancellationToken.None,
            ref cancellationStage);

        Assert.AreEqual(1, result.SourceRows.Count);
        Assert.AreSame(missing, result.SourceRows[0].Entry);
    }

    [TestMethod]
    public void BuildDetailSourceRows_CancellationIsNotHidden()
    {
        var workspace = CreateDetailWorkspace(out _);
        var table = new BMSTable
        {
            entries = [new TestablePlaylistEntry("88888888888888888888888888888888", "cancel")]
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        string cancellationStage = string.Empty;

        Assert.ThrowsException<OperationCanceledException>(() => workspace.BuildDetailSourceRows(
            table,
            null,
            onlyNotOwned: false,
            new PlaylistLibraryIndexSnapshot { ResolveIndex = PlaylistLibraryResolveIndexSnapshot.Empty },
            cancellation.Token,
            ref cancellationStage));
    }

    [TestMethod]
    public async Task RequestDetailRefresh_OwnsRequestThroughRebuildAndTerminalApply()
    {
        var logs = new List<string>();
        var mainChartList = new MainChartListViewModel(action => action());
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            mainChartList,
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            logs.Add,
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        var dataSource = new FakePlaylistDetailDataSource();
        workspace.SetDetailDataSource(dataSource);
        var entry = new TestablePlaylistEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "request-entry");
        var table = new BMSTable { entries = [entry] };
        workspace.RequestDetailSelection(table);
        workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot("request", ChartModeFilter.All));
        workspace.InitializePlaylistDetailSort(new ChartListSortParameters
        {
            ColumnsName = "TITLE",
            Direction = System.ComponentModel.ListSortDirection.Ascending
        });

        int requestVersion = workspace.RequestDetailRefresh(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);

        await workspace.WaitForDetailRequestCompletionAsync(requestVersion)
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(requestVersion, workspace.DetailBuildState.RequestVersion);
        Assert.AreSame(table, workspace.DetailViewState.Source.CurrentTable);
        Assert.AreEqual(1, workspace.DetailViewState.Source.Rows.Count);
        Assert.AreSame(entry, workspace.DetailViewState.Source.Rows[0].Entry);
        Assert.AreEqual(1, dataSource.EnsureEntriesLoadedCallCount);
        Assert.AreEqual("REQUEST", workspace.DetailViewState.View.CurrentIdentity?.KeywordFilter);
        Assert.IsTrue(mainChartList.LastCompletion.RequestId > 0L);
        Assert.AreEqual(MainViewUpdateMode.PlaylistFilterSelected, mainChartList.LastCompletion.Mode);
        Assert.IsTrue(logs.Exists(log => log.Contains("main_view_build mode=PlaylistFilterSelected")
            && log.Contains("isPlaylistDetailView=true")));
    }

    [TestMethod]
    public void RequestDetailRefresh_AfterShutdownIsIgnoredWithoutStartingWorker()
    {
        var workspace = CreateDetailWorkspace(out _);
        workspace.CancelDetailBuilds();
        workspace.RequestDetailSelection(new BMSTable());

        workspace.RequestDetailRefresh(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);

        Assert.IsTrue(workspace.IsDetailBuildIdle);
        Assert.IsTrue(workspace.DetailBuildState.ShutdownCancellationRequested);
        Assert.IsFalse(workspace.DetailBuildState.WorkerRunning);
        Assert.IsNull(workspace.DetailBuildState.PendingRequest);
    }

    [TestMethod]
    public void RequestDetailRefresh_WithoutOwnerSelectionIsIgnored()
    {
        var workspace = CreateDetailWorkspace(out _);
        int initialRequestVersion = workspace.DetailBuildState.RequestVersion;

        int requestVersion = workspace.RequestDetailRefresh(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);

        Assert.AreEqual(0, requestVersion);
        Assert.IsTrue(workspace.IsDetailBuildIdle);
        Assert.AreEqual(initialRequestVersion, workspace.DetailBuildState.RequestVersion);
        Assert.IsNull(workspace.DetailBuildState.PendingRequest);
    }

    [TestMethod]
    public void PlaylistSyncStatusOwner_ReplacesSourceKeyAndUsesSourceFallback()
    {
        var workspace = CreateDetailWorkspace(out _);
        var sourceTable = new BMSTable { playlist_id = 1 };
        var resultTable = new BMSTable { playlist_id = 2 };

        workspace.RecordPlaylistSyncResult(
            PlaylistSyncAttemptResult.CreateSuccess(
                sourceTable,
                resultTable,
                new Uri("https://example.test/table"),
                updated: true));

        IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> snapshot = workspace.CapturePlaylistSyncStatusSnapshot();
        Assert.IsFalse(snapshot.ContainsKey("id:1"));
        Assert.IsTrue(snapshot.ContainsKey("id:2"));
        Assert.AreEqual(PlaylistSyncStatusKind.Updated, snapshot["id:2"].Kind);

        var fallbackSourceTable = new BMSTable { name = "fallback" };
        workspace.RecordPlaylistSyncResult(
            PlaylistSyncAttemptResult.CreateSuccess(
                fallbackSourceTable,
                new BMSTable(),
                new Uri("https://example.test/fallback"),
                updated: false));

        snapshot = workspace.CapturePlaylistSyncStatusSnapshot();
        Assert.IsTrue(snapshot.ContainsKey("name:fallback"));
        Assert.AreEqual(PlaylistSyncStatusKind.Ok, snapshot["name:fallback"].Kind);
    }

    [TestMethod]
    public void PlaylistSyncStatusOwner_CapturesIsolatedSnapshots()
    {
        var workspace = CreateDetailWorkspace(out _);
        var table = new BMSTable { playlist_id = 3 };
        workspace.RecordPlaylistSyncResult(
            PlaylistSyncAttemptResult.CreateSuccess(
                table,
                table,
                new Uri("https://example.test/initial"),
                updated: false));

        IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> firstSnapshot = workspace.CapturePlaylistSyncStatusSnapshot();
        workspace.RecordPlaylistSyncResult(
            PlaylistSyncAttemptResult.CreateFailure(
                table,
                new Uri("https://example.test/failure"),
                new InvalidOperationException("failure")));
        IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> secondSnapshot = workspace.CapturePlaylistSyncStatusSnapshot();

        Assert.AreNotSame(firstSnapshot["id:3"], secondSnapshot["id:3"]);
        Assert.AreEqual(PlaylistSyncStatusKind.Ok, firstSnapshot["id:3"].Kind);
        Assert.AreEqual(PlaylistSyncStatusKind.UnknownError, secondSnapshot["id:3"].Kind);
        workspace.RecordPlaylistSyncResult(null);
        Assert.AreEqual(PlaylistSyncStatusKind.Ok, firstSnapshot["id:3"].Kind);
    }

    [TestMethod]
    public void PlaylistSyncProgressOwner_SuppressesInactiveUntilLastScopeEnds()
    {
        var workspace = CreateDetailWorkspace(out _);
        List<PlaylistSyncProgressSnapshot> snapshots = [];
        workspace.PlaylistSyncProgressChanged += (_, request) => snapshots.Add(request.Snapshot);

        workspace.BeginPlaylistSyncProgressOperation();
        workspace.BeginPlaylistSyncProgressOperation();
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            TotalTableCount = 2,
            CompletedTableCount = 1,
            CurrentTableName = "active"
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false
        });

        Assert.AreEqual(1, snapshots.Count);
        Assert.IsTrue(snapshots[0].IsActive);

        workspace.EndPlaylistSyncProgressOperation();
        Assert.AreEqual(1, snapshots.Count);

        workspace.EndPlaylistSyncProgressOperation();
        Assert.AreEqual(2, snapshots.Count);
        Assert.IsFalse(snapshots[1].IsActive);
    }

    [TestMethod]
    public void PlaylistSyncProgressOwner_TracksBeatorajaOperationIdsExactlyOnce()
    {
        var workspace = CreateDetailWorkspace(out _);
        List<PlaylistSyncProgressSnapshot> snapshots = [];
        workspace.PlaylistSyncProgressChanged += (_, request) => snapshots.Add(request.Snapshot);

        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            OperationId = 41,
            TotalTableCount = 1,
            CompletedTableCount = 0
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            OperationId = 41,
            TotalTableCount = 1,
            CompletedTableCount = 1
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            OperationId = 99
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            OperationId = 41
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            OperationId = 41
        });

        Assert.AreEqual(3, snapshots.Count);
        Assert.IsTrue(snapshots[0].IsActive);
        Assert.IsTrue(snapshots[1].IsActive);
        Assert.IsFalse(snapshots[2].IsActive);
    }

    [TestMethod]
    public void PlaylistSyncProgressOwner_BeatorajaCompletionDoesNotClearManualScope()
    {
        var workspace = CreateDetailWorkspace(out _);
        List<PlaylistSyncProgressSnapshot> snapshots = [];
        workspace.PlaylistSyncProgressChanged += (_, request) => snapshots.Add(request.Snapshot);

        workspace.BeginPlaylistSyncProgressOperation();
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            OperationId = 7,
            TotalTableCount = 1,
            CompletedTableCount = 0
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            OperationId = 7
        });

        Assert.AreEqual(1, snapshots.Count);
        workspace.EndPlaylistSyncProgressOperation();
        Assert.AreEqual(2, snapshots.Count);
        Assert.IsFalse(snapshots[1].IsActive);
    }

    [TestMethod]
    public void TreeSelection_NormalFolderAndNotOwnedUseCanonicalDetailSelection()
    {
        var workspace = CreateDetailWorkspace(out _);
        var table = new BMSTable();
        PlaylistTreeSelectionActivatedEventArgs? request = null;
        workspace.TreeSelectionActivated += (_, e) => request = e;

        workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder("Folder A"));

        PlaylistTreeSelectionActivatedEventArgs folderRequest = request
            ?? throw new AssertFailedException("Folder selection activation was not raised.");
        Assert.IsFalse(folderRequest.IsSummary);
        Assert.AreSame(table, folderRequest.Detail.Table);
        Assert.AreEqual("Folder A", folderRequest.Detail.FolderName);
        Assert.AreEqual(PlaylistDetailFilter.PlaylistFilter, folderRequest.Detail.Filter);
        Assert.AreEqual(1L, folderRequest.SelectionRevision);
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailSelection(folderRequest.Detail, folderRequest.SelectionRevision));

        workspace.RequestDetailSelection(
            table,
            PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned));

        PlaylistTreeSelectionActivatedEventArgs notOwnedRequest = request
            ?? throw new AssertFailedException("Not-owned selection activation was not raised.");
        Assert.AreSame(table, notOwnedRequest.Detail.Table);
        Assert.IsNull(notOwnedRequest.Detail.FolderName);
        Assert.AreEqual(PlaylistDetailFilter.PlaylistNotOwnedFilterSelected, notOwnedRequest.Detail.Filter);
        Assert.AreEqual(2L, notOwnedRequest.SelectionRevision);
        Assert.IsFalse(workspace.IsCurrentPlaylistDetailSelection(folderRequest.Detail, folderRequest.SelectionRevision));
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailSelection(notOwnedRequest.Detail, notOwnedRequest.SelectionRevision));
    }

    [TestMethod]
    public void TreeSelection_SummaryCanBeRequestedRepeatedly()
    {
        var workspace = CreateDetailWorkspace(out _);
        int requestCount = 0;
        workspace.TreeSelectionActivated += (_, request) =>
        {
            Assert.IsTrue(request.IsSummary);
            requestCount++;
        };

        workspace.RequestSummarySelection();
        workspace.RequestSummarySelection();

        Assert.AreEqual(2, requestCount);
        Assert.IsNull(workspace.CapturePlaylistDetailSelection());
    }

    [TestMethod]
    public void TreeSelectionActivationsPublishOnCallerThread()
    {
        var workspace = CreateDetailWorkspace(out _);
        int callerThreadId = Thread.CurrentThread.ManagedThreadId;
        List<int> requestThreadIds = [];
        workspace.TreeSelectionActivated += (_, _) => requestThreadIds.Add(Thread.CurrentThread.ManagedThreadId);

        workspace.RequestSummarySelection();
        workspace.RequestDetailSelection(new BMSTable());

        CollectionAssert.AreEqual(new[] { callerThreadId, callerThreadId }, requestThreadIds);
    }

    [TestMethod]
    public void TreeSelection_StaleSummaryIsNotActivatedAfterDetailSelection()
    {
        Queue<Action> pendingActions = new();
        var workspace = CreateDetailWorkspace(
            out _,
            dispatchPresentation: action => pendingActions.Enqueue(action));
        List<PlaylistTreeSelectionActivatedEventArgs> activations = [];
        workspace.TreeSelectionActivated += (_, request) => activations.Add(request);

        workspace.RequestSummarySelection();
        workspace.RequestDetailSelection(new BMSTable());

        Assert.AreEqual(2, pendingActions.Count);
        pendingActions.Dequeue()();
        Assert.AreEqual(0, activations.Count);
        pendingActions.Dequeue()();
        Assert.AreEqual(1, activations.Count);
        Assert.IsFalse(activations[0].IsSummary);
    }

    [TestMethod]
    public void TreeSelection_CurrentSummaryActivationOwnsPresentation()
    {
        int presentationRefreshRequestCount = 0;
        var workspace = CreateDetailWorkspace(
            out _,
            presentationRefreshDeferredProvider: request =>
            {
                presentationRefreshRequestCount++;
                return request.Kind == PlaylistPresentationRefreshKind.SummaryPresentation;
            });
        PlaylistTreeSelectionActivatedEventArgs? activation = null;
        workspace.TreeSelectionActivated += (_, request) =>
        {
            if (request.IsSummary)
            {
                activation = request;
            }
        };

        workspace.RequestSummarySelection();
        PlaylistTreeSelectionActivatedEventArgs currentSummary = activation
            ?? throw new AssertFailedException("Summary selection activation was not raised.");

        Assert.IsTrue(currentSummary.SummaryModeChanged);
        Assert.IsFalse(workspace.IsPlaylistSummaryMode);
        Assert.IsTrue(workspace.IsPlaylistSummaryModeRequested);
        Assert.AreEqual(string.Empty, workspace.GridHeaderText);
        Assert.AreEqual(1, presentationRefreshRequestCount);
        Assert.IsTrue(workspace.HasDeferredPlaylistSummaryPresentationRefresh());
    }

    [TestMethod]
    public void TreeSelection_ReentrantDetailSelectionPreventsStaleSummaryRefresh()
    {
        int summaryRefreshCount = 0;
        var workspace = CreateDetailWorkspace(
            out _,
            presentationRefreshDeferredProvider: request =>
            {
                if (request.Kind == PlaylistPresentationRefreshKind.SummaryPresentation)
                {
                    summaryRefreshCount++;
                }
                return false;
            });
        var detailTable = new BMSTable();
        bool redirected = false;
        workspace.TreeSelectionActivated += (_, request) =>
        {
            if (request.IsSummary && !redirected)
            {
                redirected = true;
                workspace.RequestDetailSelection(detailTable);
            }
        };

        workspace.RequestSummarySelection();

        Assert.IsTrue(redirected);
        Assert.IsFalse(workspace.IsPlaylistSummaryMode);
        Assert.AreEqual(0, summaryRefreshCount);
        Assert.AreSame(detailTable, workspace.CapturePlaylistDetailSelection().Table);
    }

    [TestMethod]
    public void TreeSelection_CurrentDetailActivationIsRejectedAfterClear()
    {
        Queue<Action> pendingActions = new();
        var workspace = CreateDetailWorkspace(
            out _,
            dispatchPresentation: action => pendingActions.Enqueue(action));
        int activationCount = 0;
        workspace.TreeSelectionActivated += (_, _) => activationCount++;

        workspace.RequestDetailSelection(new BMSTable());
        workspace.SetPlaylistSummaryMode(enabled: true);
        workspace.ClearPlaylistDetailSelection();

        Assert.AreEqual(1, pendingActions.Count);
        pendingActions.Dequeue()();
        Assert.AreEqual(0, activationCount);
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_summary_header, workspace.GridHeaderText);
    }

    [TestMethod]
    public void TreeSelection_CurrentDetailActivationOwnsSummaryTransition()
    {
        var workspace = CreateDetailWorkspace(out _);
        var table = new BMSTable();
        PlaylistTreeSelectionActivatedEventArgs? activation = null;
        workspace.TreeSelectionActivated += (_, request) =>
        {
            if (!request.IsSummary)
            {
                activation = request;
            }
        };
        workspace.SetPlaylistSummaryMode(enabled: true);
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest buildRequest));

        workspace.RequestDetailSelection(table);
        PlaylistTreeSelectionActivatedEventArgs selected = activation
            ?? throw new AssertFailedException("Detail selection activation was not raised.");
        Assert.IsTrue(selected.SummaryModeChanged);
        Assert.IsTrue(buildRequest.CancellationToken.IsCancellationRequested);
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.IsFalse(workspace.IsPlaylistSummaryModeRequested);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_summary_header, workspace.GridHeaderText);
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailSelection(selected.Detail, selected.SelectionRevision));
        workspace.CompletePlaylistSummaryDataBuild(buildRequest);
    }

    [TestMethod]
    public async Task RequestDetailRefresh_StaleInputUsesCurrentOwnerSelection()
    {
        var workspace = CreateDetailWorkspace(out _);
        workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot("old", ChartModeFilter.All));
        var oldTable = new BMSTable();
        var currentTable = new BMSTable();
        PlaylistTreeSelectionActivatedEventArgs? request = null;
        workspace.TreeSelectionActivated += (_, selectionRequest) =>
        {
            if (!selectionRequest.IsSummary)
            {
                request = selectionRequest;
            }
        };
        workspace.RequestDetailSelection(oldTable);
        _ = request
            ?? throw new AssertFailedException("Initial detail selection request was not raised.");

        workspace.RequestDetailSelection(currentTable);
        workspace.RequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("current", ChartModeFilter.All));
        int requestVersion = workspace.RequestDetailRefresh(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);

        Assert.IsTrue(requestVersion > 0);
        await workspace.WaitForDetailRequestCompletionAsync(requestVersion)
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreSame(currentTable, workspace.DetailViewState.Source.CurrentTable);
        Assert.AreEqual("CURRENT", workspace.DetailViewState.View.CurrentIdentity?.KeywordFilter);
    }

    [TestMethod]
    public void TreeSelection_NullTableSelectionRemainsResolvedAsSelectionState()
    {
        var workspace = CreateDetailWorkspace(out _);
        PlaylistTreeSelectionActivatedEventArgs? request = null;
        workspace.TreeSelectionActivated += (_, e) => request = e;

        workspace.RequestDetailSelection(null);

        PlaylistTreeSelectionActivatedEventArgs detailRequest = request
            ?? throw new AssertFailedException("Null-table detail selection activation was not raised.");
        Assert.IsFalse(detailRequest.IsSummary);
        Assert.IsNotNull(detailRequest.Detail);
        Assert.IsNull(detailRequest.Detail.Table);
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailSelection(
            detailRequest.Detail,
            detailRequest.SelectionRevision));
        Assert.IsNotNull(workspace.CapturePlaylistDetailSelection());
        Assert.IsNull(workspace.CapturePlaylistDetailSelection().Table);
    }

    [TestMethod]
    public void TreeSelection_OwnerPreservesReplacementRemapAndContentRevision()
    {
        var workspace = CreateDetailWorkspace(out _);
        var oldTable = new BMSTable();
        var newTable = new BMSTable();
        PlaylistTreeSelectionActivatedEventArgs? request = null;
        workspace.TreeSelectionActivated += (_, e) => request = e;

        workspace.RequestDetailSelection(oldTable, PlaylistFolderNode.CreateFolder("Folder A"));
        PlaylistTreeSelectionActivatedEventArgs selected = request
            ?? throw new AssertFailedException("Detail selection activation was not raised.");

        Assert.IsTrue(workspace.ReplaceCurrentPlaylistDetailSelectionTable(oldTable, newTable));
        PlaylistDetailSelection replaced = workspace.CapturePlaylistDetailSelection()
            ?? throw new AssertFailedException("Replaced detail selection was not retained.");
        Assert.AreSame(newTable, replaced.Table);
        Assert.AreEqual("Folder A", replaced.FolderName);
        Assert.IsFalse(workspace.IsCurrentPlaylistDetailSelection(selected.Detail, selected.SelectionRevision));

        Assert.IsTrue(workspace.RemapCurrentPlaylistDetailFolderSelection(
            newTable,
            new Dictionary<string, string> { ["Folder A"] = "Folder B" }));
        Assert.AreEqual("Folder B", workspace.CapturePlaylistDetailSelection().FolderName);

        workspace.RequestDetailSelection(newTable, PlaylistFolderNode.CreateFolder(string.Empty));
        Assert.IsTrue(workspace.RemapCurrentPlaylistDetailFolderSelection(
            newTable,
            new Dictionary<string, string> { [string.Empty] = "Root Renamed" }));
        Assert.AreEqual("Root Renamed", workspace.CapturePlaylistDetailSelection().FolderName);

        workspace.RequestDetailSelection(
            newTable,
            PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned));
        Assert.IsFalse(workspace.RemapCurrentPlaylistDetailFolderSelection(
            newTable,
            new Dictionary<string, string> { [string.Empty] = "Not Owned Renamed" }));
        Assert.IsNull(workspace.CapturePlaylistDetailSelection().FolderName);

        workspace.DetailViewState.Source.PlaylistContentRevision = 9;
        Assert.IsTrue(workspace.MarkCurrentPlaylistDetailEntriesChanged(newTable, "test_entries_changed"));
        Assert.AreEqual(10, workspace.DetailViewState.Source.PlaylistContentRevision);
        Assert.IsFalse(workspace.MarkCurrentPlaylistDetailEntriesChanged(oldTable, "test_non_current_entries_changed"));
    }

    [TestMethod]
    public void RequestPlaylistDetailScoreSnapshotRefresh_DefersDuringEditAndPreservesHighestVersion()
    {
        var workspace = CreateDetailWorkspace(out _);
        workspace.IsPlaylistDetailViewActive = true;
        workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion = 3;
        workspace.DetailViewState.Source.IsPlaylistCellEditing = true;
        var refreshes = new List<PlaylistDetailScoreSnapshotRefreshRequestedEventArgs>();
        workspace.PlaylistDetailScoreSnapshotRefreshRequested += (_, request) => refreshes.Add(request);

        workspace.RequestPlaylistDetailScoreSnapshotRefresh(5);
        workspace.RequestPlaylistDetailScoreSnapshotRefresh(4);

        Assert.AreEqual(2, refreshes.Count);
        Assert.IsTrue(refreshes[0].DeferredByEdit);
        Assert.IsFalse(refreshes[0].RefreshRequired);
        Assert.IsTrue(refreshes[1].DeferredByEdit);
        Assert.IsFalse(refreshes[1].RefreshRequired);
        Assert.AreEqual(5, workspace.DetailViewState.Source.PendingScoreSnapshotRefreshVersion);
        workspace.DetailViewState.Source.IsPlaylistCellEditing = false;
        workspace.RequestPlaylistDetailScoreSnapshotRefresh(5);
        Assert.AreEqual(3, refreshes.Count);
        Assert.IsTrue(refreshes[2].RefreshRequired);
        Assert.IsTrue(refreshes[2].DeferredByEdit == false);
        Assert.AreEqual(5, refreshes[2].ScoreSnapshotVersion);
        Assert.AreEqual(3, refreshes[2].LastBuiltVersion);
    }

    [TestMethod]
    public void RequestPlaylistDetailScoreSnapshotRefresh_IgnoresStaleOrInactiveRequests()
    {
        var workspace = CreateDetailWorkspace(out _);
        workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion = 3;
        var refreshes = new List<PlaylistDetailScoreSnapshotRefreshRequestedEventArgs>();
        workspace.PlaylistDetailScoreSnapshotRefreshRequested += (_, request) => refreshes.Add(request);

        workspace.RequestPlaylistDetailScoreSnapshotRefresh(3);
        workspace.RequestPlaylistDetailScoreSnapshotRefresh(4);
        Assert.AreEqual(0, refreshes.Count);

        workspace.IsPlaylistDetailViewActive = true;
        workspace.IsPlaylistSummaryMode = true;
        workspace.RequestPlaylistDetailScoreSnapshotRefresh(4);
        Assert.AreEqual(0, refreshes.Count);
    }

    [TestMethod]
    public void RequestPlaylistDetailReloadRefresh_PublishesReloadOpportunity()
    {
        var workspace = CreateDetailWorkspace(out _);
        int refreshCount = 0;
        workspace.PlaylistDetailReloadRefreshRequested += (_, _) => refreshCount++;

        Assert.IsTrue(workspace.ShouldRefreshPlaylistDetailAfterReload(MainViewUpdateMode.PlaylistFilterSelected));
        workspace.RequestPlaylistDetailReloadRefresh();
        Assert.AreEqual(1, refreshCount);

        Assert.IsFalse(workspace.ShouldRefreshPlaylistDetailAfterReload(MainViewUpdateMode.PlayHistorySelected));
        workspace.RequestPlaylistDetailReloadRefresh();
        Assert.AreEqual(2, refreshCount);

        workspace.IsPlaylistSummaryMode = true;
        workspace.RequestPlaylistDetailReloadRefresh();
        Assert.AreEqual(3, refreshCount);
    }

    [TestMethod]
    public void SetDetailDataSource_ReinitializeUsesReplacementSource()
    {
        var workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource firstSource);
        var replacementSource = new FakePlaylistDetailDataSource();
        using var activeBuildCancellation = new CancellationTokenSource();
        PlaylistRequestIdentity identity = PlaylistRequestFactory.CreateIdentity(
            new BMSTable(), null, PlaylistDetailFilter.PlaylistFilter, null,
            ChartModeFilter.All, null, 1, 1, 1, 1, hasResolvedSelection: true);
        workspace.DetailBuildState.RequestVersion = 10;
        workspace.DetailBuildState.CurrentBuildCancellation = activeBuildCancellation;
        workspace.DetailBuildState.CurrentBuildRequest = new PlaylistBuildRequest { Identity = identity };
        workspace.DetailBuildState.WorkerRunning = true;
        workspace.DetailViewState.Source.CurrentIdentity = identity.SourceIdentity;
        workspace.SetDetailDataSource(replacementSource);
        Assert.AreEqual(11, workspace.DetailBuildState.RequestVersion);
        var replacementRequest = new PlaylistBuildRequest { Identity = identity };
        PlaylistBuildQueueRegisterResult registerResult = PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            workspace.DetailBuildState,
            replacementRequest,
            currentViewIdentity: null,
            lastBuiltScoreSnapshotVersion: 0,
            isShutdownRequested: false);
        var table = new BMSTable
        {
            entries = [new TestablePlaylistEntry("99999999999999999999999999999999", "replacement")]
        };
        string cancellationStage = string.Empty;

        workspace.BuildDetailSourceRows(
            table,
            null,
            onlyNotOwned: false,
            new PlaylistLibraryIndexSnapshot { ResolveIndex = PlaylistLibraryResolveIndexSnapshot.Empty },
            CancellationToken.None,
            ref cancellationStage);

        Assert.AreEqual(0, firstSource.EnsureEntriesLoadedCallCount);
        Assert.AreEqual(1, replacementSource.EnsureEntriesLoadedCallCount);
        Assert.AreEqual(12, workspace.DetailBuildState.RequestVersion);
        Assert.IsTrue(activeBuildCancellation.IsCancellationRequested);
        Assert.IsNull(workspace.DetailViewState.Source.CurrentIdentity);
        Assert.IsTrue(registerResult.Enqueued);
        Assert.AreSame(replacementRequest, workspace.DetailBuildState.PendingRequest);
    }

    [TestMethod]
    public async Task DetailRequestCompletionTracksAppliedSupersededAndShutdownTerminalStates()
    {
        var state = new PlaylistDetailBuildState();
        PlaylistRequestIdentity firstIdentity = PlaylistRequestFactory.CreateIdentity(
            new BMSTable(), null, PlaylistDetailFilter.PlaylistFilter, "first",
            ChartModeFilter.All, null, 1, 1, 1, 1, hasResolvedSelection: true);
        PlaylistRequestIdentity secondIdentity = PlaylistRequestFactory.CreateIdentity(
            new BMSTable(), null, PlaylistDetailFilter.PlaylistFilter, "second",
            ChartModeFilter.All, null, 1, 1, 1, 1, hasResolvedSelection: true);
        var first = new PlaylistBuildRequest { Identity = firstIdentity };
        PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state, first, currentViewIdentity: null, lastBuiltScoreSnapshotVersion: 0, isShutdownRequested: false);
        Task firstCompletion = PlaylistDetailBuildQueueCoordinator.WaitForRequestCompletionAsync(
            state, first.RequestVersion);
        Assert.IsFalse(firstCompletion.IsCompleted);
        var duplicate = new PlaylistBuildRequest { Identity = firstIdentity };
        PlaylistBuildQueueRegisterResult duplicateResult = PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state, duplicate, currentViewIdentity: null, lastBuiltScoreSnapshotVersion: 0, isShutdownRequested: false);
        Assert.IsFalse(duplicateResult.Enqueued);
        Assert.AreEqual(first.RequestVersion, duplicate.RequestVersion);

        var second = new PlaylistBuildRequest { Identity = secondIdentity };
        PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state, second, currentViewIdentity: null, lastBuiltScoreSnapshotVersion: 0, isShutdownRequested: false);
        await firstCompletion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Task secondCompletion = PlaylistDetailBuildQueueCoordinator.WaitForRequestCompletionAsync(
            state, second.RequestVersion);
        Task detailIdle = PlaylistDetailBuildQueueCoordinator.WaitForIdleAsync(state);
        Assert.IsFalse(secondCompletion.IsCompleted);
        Assert.IsFalse(detailIdle.IsCompleted);

        PlaylistDetailBuildQueueCoordinator.CancelForShutdown(state);
        await secondCompletion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsFalse(detailIdle.IsCompleted);
        Assert.IsFalse(PlaylistDetailBuildQueueCoordinator.TryTakeNextRequestOrStopWorker(state, out _));
        await detailIdle.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.IsTrue(PlaylistDetailBuildQueueCoordinator
            .WaitForRequestCompletionAsync(state, first.RequestVersion).IsCompleted);
        Assert.IsTrue(PlaylistDetailBuildQueueCoordinator
            .WaitForRequestCompletionAsync(state, state.RequestVersion + 1).IsCompleted);
    }

    [TestMethod]
    public async Task RequestDetailRefresh_DataSourceFailureCompletesRequestAndStopsWorker()
    {
        var failureEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        dataSource.EnsureEntriesLoadedAction = () =>
        {
            failureEntered.TrySetResult(true);
            failureRelease.Task.GetAwaiter().GetResult();
            throw new InvalidOperationException("detail data source failed");
        };
        var table = new BMSTable
        {
            entries = [new TestablePlaylistEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "failure")]
        };
        workspace.RequestDetailSelection(table);
        int requestVersion = workspace.RequestDetailRefresh(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);
        Task requestCompletion = workspace.WaitForDetailRequestCompletionAsync(requestVersion);
        Task workerIdle = workspace.WaitForDetailBuildIdleAsync();

        try
        {
            await failureEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsFalse(requestCompletion.IsCompleted);
            Assert.IsFalse(workerIdle.IsCompleted);
        }
        finally
        {
            failureRelease.TrySetResult(true);
        }

        await Task.WhenAll(requestCompletion, workerIdle)
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(1, dataSource.EnsureEntriesLoadedCallCount);
        Assert.IsTrue(workspace.IsDetailBuildIdle);
        Assert.IsNull(workspace.DetailViewState.Source.CurrentTable);
        Assert.AreEqual(0, workspace.DetailViewState.Source.Rows.Count);
    }

    [TestMethod]
    public async Task DetailSourceRetirementCompletesSupersededRequestWithoutDiscardingItsWaiter()
    {
        var state = new PlaylistDetailBuildState();
        var request = new PlaylistBuildRequest
        {
            Identity = PlaylistRequestFactory.CreateIdentity(
                new BMSTable(), null, PlaylistDetailFilter.PlaylistFilter, null,
                ChartModeFilter.All, null, 1, 1, 1, 1, hasResolvedSelection: true)
        };
        PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state, request, currentViewIdentity: null, lastBuiltScoreSnapshotVersion: 0, isShutdownRequested: false);
        Task requestCompletion = PlaylistDetailBuildQueueCoordinator.WaitForRequestCompletionAsync(
            state, request.RequestVersion);

        PlaylistSourceRetirementRequest retirement = state.PrepareSourceRetirement();

        await requestCompletion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsTrue(PlaylistDetailBuildQueueCoordinator
            .WaitForRequestCompletionAsync(state, retirement.RequestVersion).IsCompleted);
    }

    [TestMethod]
    public async Task PlaylistLibraryIndexPrewarm_UsesWorkspaceSchedulerAndRuntimeCache()
    {
        var queuedWork = new List<Func<Task>>();
        var workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource, (_, work) =>
        {
            queuedWork.Add(work);
            return true;
        });

        workspace.SchedulePlaylistLibraryIndexPrewarm("initialize_completed");

        Assert.AreEqual(1, queuedWork.Count);
        await queuedWork[0]().ConfigureAwait(false);
        Assert.AreEqual(1, dataSource.ResolveIndexCallCount);

        dataSource.RuntimeState = new BMSLibrary.PlaylistLibraryResolveIndexRuntimeState
        {
            IsCached = true,
            OwnedCollectionVersion = (int)dataSource.OwnedChartCollectionVersion,
            BuildElapsedMs = 12L
        };
        PlaylistLibraryIndexReadinessSnapshot readiness = workspace.CapturePlaylistLibraryIndexReadinessSnapshot();
        Assert.AreEqual("cached", readiness.State);
        Assert.AreEqual(12L, readiness.BuildElapsedMs);
    }

    [TestMethod]
    public async Task PlaylistLibraryIndexPrewarm_DuplicateRefreshDefersUntilUiPriorityEnds()
    {
        var workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource, (_, _) => true);
        workspace.BeginDuplicateRefreshPriorityWindow("test");

        workspace.InvalidatePlaylistLibraryIndexSnapshot(
            "owned_collection_changed",
            startupReadyOperable: true,
            MainViewUpdateMode.DuplicateFilterSelected);

        Assert.IsNull(workspace.GetPlaylistLibraryIndexPrewarmTask());
        workspace.ReleaseDuplicateRefreshPriorityWindow("test_done");
        Task prewarmTask = workspace.GetPlaylistLibraryIndexPrewarmTask();
        Assert.IsNotNull(prewarmTask);
        await prewarmTask.ConfigureAwait(false);
        Assert.AreEqual(1, dataSource.ResolveIndexCallCount);
    }

    [TestMethod]
    public void QueueExternalPlaylistSync_SchedulerRejectionPublishesSkippedLifecycle()
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
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            int schedulerCalls = 0;
            var queued = new List<PlaylistExternalSyncRequestEventArgs>();
            var completed = new List<PlaylistExternalSyncCompletionEventArgs>();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                externalSyncScheduler: (_, _) =>
                {
                    schedulerCalls++;
                    return false;
                });
            workspace.PlaylistExternalSyncQueued += (_, request) => queued.Add(request);
            workspace.PlaylistExternalSyncCompleted += (_, request) => completed.Add(request);
            workspace.RefreshPlaylistTreeTables(playlist);

            workspace.QueueExternalPlaylistSync(
                "test_rejection",
                fromReloadTables: true,
                publishReferenceReceipt: false,
                operationToken: 11L);

            Assert.AreEqual(1, schedulerCalls);
            Assert.AreEqual(1, queued.Count);
            Assert.AreEqual("test_rejection", queued[0].Reason);
            Assert.AreEqual(11L, queued[0].OperationToken);
            Assert.AreEqual(1, completed.Count);
            Assert.IsTrue(completed[0].WasSkipped);
            Assert.IsFalse(completed[0].Succeeded);
            Assert.IsFalse(completed[0].PublishesReferenceReceipt);
            Assert.IsTrue(workspace.IsDeferredExternalPlaylistSyncIdle);
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
    public void PlaylistExternalSyncReceiptSubscription_FollowsPlaylistStoreReplacement()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string firstDirectory = Path.Combine(tempDirectory, "first");
            string secondDirectory = Path.Combine(tempDirectory, "second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            string firstSongDbPath = Path.Combine(firstDirectory, "song.db");
            string secondSongDbPath = Path.Combine(secondDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(firstSongDbPath))
            using (var __ = new LR2SongDBExtended(secondSongDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(firstSongDbPath);
            PlaylistPersistenceRepository.EnsureSchema(secondSongDbPath);
            var firstPlaylist = new TestBmsPlaylist(firstSongDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([new BMSTable { name = "first" }])
            };
            var secondTable = new BMSTable { name = "second" };
            var secondPlaylist = new TestBmsPlaylist(secondSongDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([secondTable])
            };
            BMSPlaylist currentPlaylist = firstPlaylist;
            BMSLibrary currentLibrary = new TestBmsLibrary(firstSongDbPath);
            var secondLibrary = new TestBmsLibrary(secondSongDbPath);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => currentPlaylist,
                playlistLibraryProvider: () => currentLibrary);
            int sortInvalidationCount = 0;
            workspace.PlaylistReferenceSortInvalidationRequested += (_, _) => sortInvalidationCount++;

            workspace.RefreshPlaylistTreeTables(firstPlaylist, currentLibrary);
            currentPlaylist = secondPlaylist;
            currentLibrary = secondLibrary;
            workspace.RefreshPlaylistTreeTables(secondPlaylist, currentLibrary);

            PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt receipt = new(
                new BMSTable { name = "old" },
                secondTable,
                updated: true,
                referenceEntriesChanged: false,
                oldEntriesSnapshot: null,
                newEntriesSnapshot: null,
                uri: null,
                reason: "test");
            FieldInfo eventField = typeof(PlaylistExternalSyncOwner).GetField(
                "PlaylistTableUpdateReceiptPublished",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(eventField);
            MulticastDelegate? oldOwnerHandlers = eventField.GetValue(firstPlaylist.ExternalSyncOwner) as MulticastDelegate;
            oldOwnerHandlers?.DynamicInvoke(
                firstPlaylist.ExternalSyncOwner,
                new PlaylistExternalSyncOwner.PlaylistTableUpdateReceiptPublishedEventArgs(receipt));
            Assert.AreEqual(0, sortInvalidationCount);

            MulticastDelegate? currentOwnerHandlers = eventField.GetValue(secondPlaylist.ExternalSyncOwner) as MulticastDelegate;
            Assert.IsNotNull(currentOwnerHandlers);
            currentOwnerHandlers!.DynamicInvoke(
                secondPlaylist.ExternalSyncOwner,
                new PlaylistExternalSyncOwner.PlaylistTableUpdateReceiptPublishedEventArgs(receipt));
            Assert.AreEqual(1, sortInvalidationCount);
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
    public void PlaylistTreeStoreReplacement_WaitsForCurrentHydrationApplyBeforeChangingSource()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string firstDirectory = Path.Combine(tempDirectory, "first");
            string secondDirectory = Path.Combine(tempDirectory, "second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            string firstSongDbPath = Path.Combine(firstDirectory, "song.db");
            string secondSongDbPath = Path.Combine(secondDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(firstSongDbPath))
            using (var __ = new LR2SongDBExtended(secondSongDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(firstSongDbPath);
            PlaylistPersistenceRepository.EnsureSchema(secondSongDbPath);
            var firstTables = new ObservableCollection<BMSTable>(
                [new BMSTable { name = "first" }]);
            var secondTables = new ObservableCollection<BMSTable>(
                [new BMSTable { name = "second" }]);
            var firstPlaylist = new TestBmsPlaylist(firstSongDbPath) { BMSTables = firstTables };
            var secondPlaylist = new TestBmsPlaylist(secondSongDbPath) { BMSTables = secondTables };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => firstPlaylist);
            workspace.RefreshPlaylistTreeTables(firstPlaylist);

            var receipt = new PlaylistHydrationCompletionReceipt(requestAlreadyPublished: true);
            Assert.IsTrue(receipt.TryBegin());
            FieldInfo receiptField = typeof(PlaylistWorkspaceViewModel).GetField(
                "playlistHydrationCompletionReceipt",
                BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo generationField = typeof(PlaylistWorkspaceViewModel).GetField(
                "playlistTreeNotificationGeneration",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(receiptField);
            Assert.IsNotNull(generationField);
            receiptField!.SetValue(workspace, receipt);
            long generation = (long)generationField!.GetValue(workspace)!;

            using var applyEntered = new ManualResetEventSlim();
            using var releaseApply = new ManualResetEventSlim();
            Task apply = Task.Run(() =>
                workspace.ExecuteCurrentPlaylistHydrationNotification(
                    firstPlaylist,
                    firstTables,
                    generation,
                    receipt,
                    () =>
                    {
                        applyEntered.Set();
                        releaseApply.Wait();
                    }));
            Assert.IsTrue(applyEntered.Wait(TimeSpan.FromSeconds(5)));

            Task replacement = Task.Run(() =>
                workspace.RefreshPlaylistTreeTables(secondPlaylist));
            Assert.IsFalse(
                replacement.Wait(TimeSpan.FromMilliseconds(100)),
                "Store replacement must wait for the current terminal hydration apply.");
            Assert.AreSame(firstTables, workspace.PlaylistTreeTables);

            releaseApply.Set();
            Task.WhenAll(apply, replacement).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.AreSame(secondTables, workspace.PlaylistTreeTables);
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
    public void DiscardDeferredExternalPlaylistSyncForShutdown_PublishesSkippedLifecycle()
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
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            Func<Task>? scheduledWork = null;
            var completed = new List<PlaylistExternalSyncCompletionEventArgs>();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                externalSyncScheduler: (_, work) =>
                {
                    scheduledWork = work;
                    return true;
                });
            workspace.PlaylistExternalSyncCompleted += (_, request) => completed.Add(request);
            workspace.RefreshPlaylistTreeTables(playlist);

            workspace.QueueExternalPlaylistSync(
                "test_shutdown_discard",
                fromReloadTables: false,
                publishReferenceReceipt: true,
                operationToken: 17L);

            Assert.IsNotNull(scheduledWork);
            workspace.DiscardDeferredExternalPlaylistSyncForShutdown("test_shutdown");

            Assert.AreEqual(1, completed.Count);
            Assert.AreEqual("test_shutdown_discard", completed[0].Reason);
            Assert.AreEqual(1, completed[0].Version);
            Assert.AreEqual(17L, completed[0].OperationToken);
            Assert.IsTrue(completed[0].PublishesReferenceReceipt);
            Assert.IsTrue(completed[0].WasSkipped);
            Assert.IsFalse(completed[0].Succeeded);
            Assert.IsTrue(workspace.IsDeferredExternalPlaylistSyncIdle);
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
    public void QueueExternalPlaylistSync_CoalescesAndRunsLatestRequestSnapshot()
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
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            int schedulerCalls = 0;
            Func<Task>? scheduledWork = null;
            var queued = new List<PlaylistExternalSyncRequestEventArgs>();
            var completed = new List<PlaylistExternalSyncCompletionEventArgs>();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                externalSyncScheduler: (_, work) =>
                {
                    schedulerCalls++;
                    scheduledWork = work;
                    return true;
                });
            workspace.PlaylistExternalSyncQueued += (_, request) => queued.Add(request);
            workspace.PlaylistExternalSyncCompleted += (_, request) => completed.Add(request);
            workspace.RefreshPlaylistTreeTables(playlist);

            workspace.QueueExternalPlaylistSync(
                "first_request",
                fromReloadTables: false,
                publishReferenceReceipt: false,
                operationToken: 1L);
            workspace.QueueExternalPlaylistSync(
                "latest_request",
                fromReloadTables: false,
                publishReferenceReceipt: true,
                operationToken: 2L);

            Assert.AreEqual(1, schedulerCalls);
            Assert.IsNotNull(scheduledWork);
            Assert.AreEqual(2, queued.Count);
            Assert.AreEqual("latest_request", queued[1].Reason);
            Assert.AreEqual(2L, queued[1].OperationToken);
            Assert.IsTrue(queued[1].PublishesReferenceReceipt);

            scheduledWork!().GetAwaiter().GetResult();

            Assert.IsTrue(workspace.IsDeferredExternalPlaylistSyncIdle);
            Assert.IsTrue(completed.Any(request =>
                request.Version == 2
                && request.Succeeded
                && request.PublishesReferenceReceipt));
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
    public void QueuePlaylistReferenceApply_SchedulerRejectionPublishesSkippedLifecycle()
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
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            int schedulerCalls = 0;
            var queued = new List<PlaylistReferenceApplyQueuedEventArgs>();
            var completed = new List<PlaylistReferenceApplyCompletedEventArgs>();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                referenceApplyScheduler: (_, _) =>
                {
                    schedulerCalls++;
                    return false;
                });
            workspace.PlaylistReferenceApplyWorkflow.Queued += (_, request) => queued.Add(request);
            workspace.PlaylistReferenceApplyWorkflow.Completed += (_, request) => completed.Add(request);

            workspace.PlaylistReferenceApplyWorkflow.Queue("test_rejection", 11L);

            Assert.AreEqual(1, schedulerCalls);
            Assert.AreEqual(1, queued.Count);
            Assert.AreEqual("test_rejection", queued[0].Reason);
            Assert.AreEqual(11L, queued[0].OperationToken);
            Assert.AreEqual(1, completed.Count);
            Assert.IsTrue(completed[0].WasSkipped);
            Assert.IsFalse(completed[0].Succeeded);
            Assert.AreEqual(1, workspace.PlaylistReferenceApplyWorkflow.LastCompletedVersion);
            Assert.IsTrue(workspace.PlaylistReferenceApplyWorkflow.IsIdle);
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
    public void QueuePlaylistReferenceApply_CoalescesAndRunsLatestRequestSnapshot()
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
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            var hydrationTable = new BMSTable { name = "Hydration" };
            hydrationTable.MarkEntriesNotLoaded();
            playlist.BMSTables.Add(hydrationTable);
            playlist.StartupBackgroundTaskScheduler = (_, _, _, _) => true;
            playlist.QueueDeferredPlaylistEntriesHydration("queued_before_reference");
            var library = new TestBmsLibrary(songDbPath);
            int schedulerCalls = 0;
            Func<Task>? scheduledWork = null;
            var queued = new List<PlaylistReferenceApplyQueuedEventArgs>();
            var completed = new List<PlaylistReferenceApplyCompletedEventArgs>();
            var presentation = new List<PlaylistReferenceApplyPresentationRequestedEventArgs>();
            var lifecycle = new List<string>();
            bool hydrationArbitrationObservedAfterLifecycle = false;
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                referenceApplyScheduler: (_, work) =>
                {
                    schedulerCalls++;
                    scheduledWork = work;
                    return true;
                },
                presentationRefreshDeferredProvider: request =>
                {
                    if (request.Kind == PlaylistPresentationRefreshKind.HydrationCompleted)
                    {
                        hydrationArbitrationObservedAfterLifecycle = lifecycle.Contains("hydration");
                    }
                    return false;
                });
            workspace.RefreshPlaylistTreeTables(playlist);
            workspace.PlaylistReferenceApplyWorkflow.Queued += (_, request) => queued.Add(request);
            workspace.PlaylistReferenceApplyWorkflow.Completed += (_, request) => completed.Add(request);
            workspace.PlaylistEntriesHydrationCompleted += (_, _) => lifecycle.Add("hydration");
            workspace.PlaylistReferenceApplyWorkflow.PresentationRequested += (_, request) =>
            {
                lifecycle.Add("presentation");
                presentation.Add(request);
            };

            workspace.PlaylistReferenceApplyWorkflow.Queue("first_request", 1L);
            workspace.PlaylistReferenceApplyWorkflow.Queue("latest_request", 2L);

            Assert.AreEqual(1, schedulerCalls);
            Assert.IsNotNull(scheduledWork);
            Assert.AreEqual(2, queued.Count);
            Assert.AreEqual("latest_request", queued[1].Reason);
            Assert.AreEqual(2L, queued[1].OperationToken);

            scheduledWork!().GetAwaiter().GetResult();

            Assert.IsTrue(workspace.PlaylistReferenceApplyWorkflow.IsIdle);
            Assert.AreEqual(1, completed.Count);
            Assert.AreEqual(2, completed[0].Version);
            Assert.AreEqual("latest_request", completed[0].Reason);
            Assert.AreEqual(2L, completed[0].OperationToken);
            Assert.IsTrue(completed[0].Succeeded);
            Assert.AreEqual(1, presentation.Count);
            Assert.AreEqual(2, presentation[0].Version);
            Assert.AreEqual(2L, presentation[0].OperationToken);
            Assert.IsTrue(
                hydrationArbitrationObservedAfterLifecycle,
                "Hydration lifecycle must be published before presentation arbitration.");
            CollectionAssert.AreEqual(new[] { "hydration", "presentation" }, lifecycle);
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
    public void PlaylistTreeHydrationReceipt_RequestsPresentationRefreshAfterSnapshotApply()
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
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[]
                    {
                        new BMSTable
                        {
                            playlist_id = 7053,
                            name = "ReceiptPresentation"
                        }
                    })
            };
            playlist.BMSTables[0].MarkEntriesNotLoaded();
            Task scheduledWork = null!;
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                scheduledWork = work();
                return true;
            };
            var library = new TestBmsLibrary(songDbPath);
            Func<Task>? referenceApplyWork = null;
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                referenceApplyScheduler: (_, work) =>
                {
                    referenceApplyWork = work;
                    return true;
                });
            var presentations = new List<PlaylistReferenceApplyPresentationRequestedEventArgs>();
            workspace.PlaylistReferenceApplyWorkflow.PresentationRequested += (_, request) => presentations.Add(request);

            workspace.RefreshPlaylistTreeTables(playlist);
            playlist.QueueDeferredPlaylistEntriesHydration("receipt_presentation");

            Assert.IsNotNull(scheduledWork);
            scheduledWork.GetAwaiter().GetResult();
            Assert.IsNotNull(referenceApplyWork);
            referenceApplyWork!().GetAwaiter().GetResult();
            Assert.AreEqual(1, presentations.Count);
            Assert.AreEqual("receipt_presentation", presentations[0].Reason);
            Assert.AreEqual(1, presentations[0].Version);
            Assert.AreEqual(0L, presentations[0].OperationToken);
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
    public void PlaylistTreeHydrationReceipt_DoesNotApplySnapshotInvalidatedBeforeWorkspaceConsumption()
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
            var table = new BMSTable
            {
                playlist_id = 7054,
                name = "StaleReceipt"
            };
            table.MarkEntriesNotLoaded();
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            Task hydrationWork = null!;
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                hydrationWork = work();
                return true;
            };
            playlist.PlaylistEntriesHydrationReceiptPublished += (_, _) =>
            {
                using (table.ReaderWriterLock.GetWriterGuard())
                {
                    table.MarkEntriesNotLoaded();
                }
            };
            int referenceApplyScheduleCount = 0;
            var library = new TestBmsLibrary(songDbPath);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                referenceApplyScheduler: (_, _) =>
                {
                    referenceApplyScheduleCount++;
                    return true;
                });
            workspace.RefreshPlaylistTreeTables(playlist);

            playlist.QueueDeferredPlaylistEntriesHydration("stale_receipt");

            Assert.IsNotNull(hydrationWork);
            hydrationWork.GetAwaiter().GetResult();
            Assert.AreEqual(
                0,
                referenceApplyScheduleCount,
                "An invalidated model receipt must not wake reference application with an old snapshot.");
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
    public void DiscardPlaylistReferenceApplyForShutdown_PublishesSkippedLifecycle()
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
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            Func<Task>? scheduledWork = null;
            var completed = new List<PlaylistReferenceApplyCompletedEventArgs>();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                referenceApplyScheduler: (_, work) =>
                {
                    scheduledWork = work;
                    return true;
                });
            workspace.PlaylistReferenceApplyWorkflow.Completed += (_, request) => completed.Add(request);

            workspace.PlaylistReferenceApplyWorkflow.Queue("test_shutdown_discard", 17L);
            Assert.IsNotNull(scheduledWork);

            workspace.PlaylistReferenceApplyWorkflow.DiscardForShutdown("test_shutdown");

            Assert.AreEqual(1, completed.Count);
            Assert.AreEqual("test_shutdown_discard", completed[0].Reason);
            Assert.AreEqual(1, completed[0].Version);
            Assert.AreEqual(17L, completed[0].OperationToken);
            Assert.IsTrue(completed[0].WasSkipped);
            Assert.IsFalse(completed[0].Succeeded);
            Assert.IsTrue(workspace.PlaylistReferenceApplyWorkflow.IsIdle);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static PlaylistWorkspaceViewModel CreateDetailWorkspace(
        out FakePlaylistDetailDataSource dataSource,
        Func<string, Func<Task>, bool>? prewarmScheduler = null,
        Func<bool>? reloadCleanupStartupOperableProvider = null,
        Func<MainViewUpdateMode>? reloadCleanupCurrentTreeModeProvider = null,
        Func<Task>? reloadCleanupDispatcherIdleWaiter = null,
        Func<bool>? reloadCleanupShutdownRequestedProvider = null,
        Action? reloadCleanupGarbageCollector = null,
        Action<string>? reloadCleanupLog = null,
        Action<Exception, string>? reloadFailureLog = null,
        Func<BMSPlaylist>? playlistStoreProvider = null,
        Func<PlaylistPresentationRefreshRequestedEventArgs, bool>? presentationRefreshDeferredProvider = null,
        Func<string, Func<Task>, bool>? externalSyncScheduler = null,
        Func<string, Func<Task>, bool>? referenceApplyScheduler = null,
        Func<BMSLibrary>? playlistLibraryProvider = null,
        Action<Action>? dispatchPresentation = null,
        Action<Action>? catalogNotificationQueue = null,
        PlaylistSummaryBmtSortCoordinator? playlistSummaryBmtSort = null,
        Action<PlaylistSummarySelectionRestoreRequest>? selectionRestoreSink = null,
        Func<Action, Task>? restoreUiApplyScheduler = null,
        Func<bool>? restoreUiThreadCheck = null,
        Action<Uri>? browserOpenSink = null,
        PlaylistPropertySaveService? propertySaveService = null,
        IUiDialogService? playlistWorkspaceDialogService = null)
    {
        var workspace = new PlaylistWorkspaceViewModel(
            dispatchPresentation ?? (action => action()),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            browserOpenSink ?? PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
                playlistSummaryBmtSort ?? PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            playlistStoreProvider ?? PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            propertySaveService ?? PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            playlistLibraryProvider ?? (() => null!),
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            prewarmScheduler ?? ((_, _) => false),
            reloadCleanupStartupOperableProvider ?? (() => true),
            reloadCleanupCurrentTreeModeProvider ?? (() => MainViewUpdateMode.FolderFilterSelected),
            reloadCleanupDispatcherIdleWaiter ?? (() => Task.CompletedTask),
            reloadCleanupShutdownRequestedProvider ?? (() => false),
            reloadCleanupGarbageCollector ?? (() => { }),
            reloadCleanupLog ?? (_ => { }),
            reloadFailureLog ?? ((_, _) => { }),
            externalSyncScheduler ?? ((_, _) => false),
            referenceApplyScheduler ?? ((_, _) => false),
             restoreUiApplyScheduler ?? PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler,
             restoreUiThreadCheck ?? PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck,
             playlistWorkspaceDialogService);
        workspace.ConfigureCatalogNotificationQueue(
            catalogNotificationQueue ?? dispatchPresentation ?? (action => action()));
        workspace.PlaylistPresentationRefreshRequested += (_, request) =>
        {
            switch (request.Kind)
            {
                case PlaylistPresentationRefreshKind.SummaryData:
                    bool dataDeferred = presentationRefreshDeferredProvider?.Invoke(request) == true;
                    workspace.ApplyPlaylistSummaryDataRefresh(dataDeferred, request.RebuildAsync);
                    break;
                case PlaylistPresentationRefreshKind.SummaryPresentation:
                    bool presentationDeferred = presentationRefreshDeferredProvider?.Invoke(request) == true;
                    workspace.ApplyPlaylistSummaryPresentationRefresh(presentationDeferred);
                    break;
                case PlaylistPresentationRefreshKind.Tree:
                    bool treeDeferred = presentationRefreshDeferredProvider?.Invoke(request) == true;
                    workspace.ApplyPlaylistTreePresentationRefresh(request.Reason, treeDeferred);
                    break;
                case PlaylistPresentationRefreshKind.HydrationCompleted:
                    if (request.HydrationSourceStore != null
                        && !workspace.IsCurrentPlaylistTreeNotificationSnapshot(
                            request.HydrationSourceStore,
                            request.HydrationSourceTables,
                            request.HydrationNotificationGeneration))
                    {
                        break;
                    }
                    if (!workspace.TryBeginPlaylistHydrationNotification(
                        request.HydrationSourceStore,
                        request.HydrationSourceTables,
                        request.HydrationNotificationGeneration,
                        request.HydrationCompletionReceipt))
                    {
                        break;
                    }
                    workspace.PublishPlaylistEntriesHydrationCompleted(
                        request.HydrationVersion,
                        request.HydrationSourceStore,
                        request.HydrationSourceTables,
                        request.HydrationNotificationGeneration,
                        request.HydrationCompletionReceipt);
                    bool hydrationDeferred = presentationRefreshDeferredProvider?.Invoke(request) == true;
                    workspace.ApplyPlaylistEntriesHydrationCompleted(
                        request.HydrationVersion,
                        hydrationDeferred,
                        request.HydrationSourceStore,
                        request.HydrationSourceTables,
                        request.HydrationNotificationGeneration,
                        request.HydrationCompletionReceipt);
                    break;
                default:
                    throw new InvalidOperationException("Unknown test refresh request kind.");
            }
        };
        workspace.PlaylistEntriesHydrationRequested += (_, request) =>
        {
            if (!workspace.TryBeginPlaylistHydrationNotification(
                request.SourceStore,
                request.SourceTables,
                request.Generation,
                request.CompletionReceipt))
            {
                return;
            }
            workspace.ExecuteCurrentPlaylistHydrationNotification(
                request.SourceStore,
                request.SourceTables,
                request.Generation,
                request.CompletionReceipt,
                () => { });
        };
        workspace.PlaylistSummarySelectionRestoreRequested +=
            selectionRestoreSink ?? PlaylistWorkspaceTestPorts.PlaylistSummarySelectionRestoreSink;
        dataSource = new FakePlaylistDetailDataSource();
        workspace.SetDetailDataSource(dataSource);
        return workspace;
    }

    private static PlaylistWorkspaceViewModel CreateBackupWorkspace(
        string songDbPath,
        IEnumerable<BMSTable> tables,
        out BMSPlaylist playlist,
        out List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications,
        Func<Action, Task>? restoreUiApplyScheduler = null,
        Func<bool>? restoreUiThreadCheck = null)
    {
        PlaylistPersistenceRepository.EnsureSchema(songDbPath);
        TestBmsPlaylist createdPlaylist = new TestBmsPlaylist(
            songDbPath,
            null,
            null,
            null,
            null,
            () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(Settings.Default),
            () => new BeatorajaBmtOptionsSnapshot(),
            () => new CustomFolderOutputSettingsSnapshot())
        {
            BMSTables = new ObservableCollection<BMSTable>(tables)
        };
        createdPlaylist.StartupBackgroundTaskScheduler = (_, _, _, _) => false;
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistStoreProvider: () => createdPlaylist,
            restoreUiApplyScheduler: restoreUiApplyScheduler,
            restoreUiThreadCheck: restoreUiThreadCheck);
        workspace.RefreshPlaylistTreeTables(createdPlaylist);
        List<PlaylistOperationNotificationPresentationRequestedEventArgs> capturedNotifications = [];
        workspace.PlaylistOperationNotificationPresentationRequested +=
            (_, request) => capturedNotifications.Add(request);
        playlist = createdPlaylist;
        notifications = capturedNotifications;
        return workspace;
    }

    private static string CreatePlaylistRestoreDump(int playlistId, string name, string symbol)
    {
        string playlistSql = "INSERT INTO playlist (playlist_id, name, symbol, folder_order, folder_sort_key, folder_sort_ascending, entry_type, page_url, header_url, data_url, compat_prefix, last_update, org_name, org_symbol, ignore_folder_output, is_external_sync, output_dir, is_root_folder) VALUES ("
            + playlistId
            + ", '"
            + name
            + "', '"
            + symbol
            + "', '', 0, 1, 0, NULL, NULL, NULL, NULL, 638857728000000000, NULL, NULL, 0, 0, NULL, 0);";
        return string.Join("\v" + Environment.NewLine, [playlistSql, string.Empty, string.Empty]);
    }

    private sealed class FakePlaylistDetailDataSource : IPlaylistDetailDataSource
    {
        internal int EnsureEntriesLoadedCallCount { get; private set; }

        internal int ResolveIndexCallCount { get; private set; }

        internal BMSLibrary.PlaylistLibraryResolveIndexRuntimeState RuntimeState { get; set; } = new();

        internal LR2SongDBExtended.chart_info ChartInfo { get; set; } = null!;

        internal Action? EnsureEntriesLoadedAction { get; set; }

        public int ChartInfoIndexVersion => 1;

        public int ScoreSnapshotVersion => 1;

        public long OwnedChartCollectionVersion => 1;

        public void EnsureEntriesLoaded(BMSTable table, string reason)
        {
            EnsureEntriesLoadedCallCount++;
            EnsureEntriesLoadedAction?.Invoke();
        }

        public BMSLibrary.ScoreSnapshot GetScoreSnapshot()
        {
            return null!;
        }

        public PlaylistLibraryResolveIndexSnapshot GetResolveIndexSnapshot(
            CancellationToken cancellationToken,
            out bool cacheHit,
            out int staleRetryCount)
        {
            ResolveIndexCallCount++;
            cacheHit = true;
            staleRetryCount = 0;
            return PlaylistLibraryResolveIndexSnapshot.Empty;
        }

        public BMSLibrary.PlaylistLibraryResolveIndexRuntimeState GetResolveIndexRuntimeState()
        {
            return RuntimeState;
        }

        public LR2SongDBExtended.chart_info ResolveChartInfo(string sha256, string md5)
        {
            return ChartInfo;
        }

        public PlaylistDetailSourceRow CreateSourceRow(
            BMSTableEntry entry,
            ChartFile resolvedChart,
            BMSScore score,
            LR2SongDBExtended.chart_info chartInfo,
            LibraryChartRef resolvedChartRef)
        {
            return new PlaylistDetailSourceRow(
                entry,
                resolvedChart,
                scoreSnapshot: score,
                entryChartInfo: chartInfo,
                resolvedChartRef: resolvedChartRef);
        }
    }

    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        internal TestablePlaylistEntry(string md5Value, string titleValue)
        {
            md5 = md5Value;
            title = titleValue;
        }

        internal void SetSha256(string value)
        {
            sha256 = value;
        }
    }

    [TestMethod]
    public void PlaylistSummaryConfiguration_IsOwnedByPlaylistWorkspace()
    {
        var viewModel = MainWindowViewModelTestFactory.Create();
        var columns = new PlaylistSummaryColumnSettings();
        var propertyNames = new List<string>();
        var rootPropertyNames = new List<string>();
        viewModel.PlaylistWorkspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);
        viewModel.PropertyChanged += (_, e) => rootPropertyNames.Add(e.PropertyName);

        PlaylistColumnPresentationCommit columnCommit =
            viewModel.PlaylistWorkspace.CommitColumnPresentationWithoutNotification(
                Visibility.Collapsed,
                columns);
        viewModel.PlaylistWorkspace.PublishColumnPresentation(columnCommit);
        viewModel.PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist = Visibility.Visible;
        viewModel.PlaylistWorkspace.GridHeaderText = "Playlist summary";
        viewModel.PlaylistWorkspace.PlaylistSummaryKeywordFilter = "title:test";
        viewModel.PlaylistWorkspace.PlaylistSummaryOwnedFilter = PlaylistOwnedFilter.OwnedComplete;

        Assert.AreSame(columns, viewModel.PlaylistWorkspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(Visibility.Visible, viewModel.PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist);
        Assert.AreEqual("Playlist summary", viewModel.PlaylistWorkspace.GridHeaderText);
        Assert.AreEqual("title:test", viewModel.PlaylistWorkspace.PlaylistSummaryKeywordFilter);
        Assert.AreEqual(PlaylistOwnedFilter.OwnedComplete, viewModel.PlaylistWorkspace.PlaylistSummaryOwnedFilter);
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.ColumnSettingsVisibilityForPlaylist));
        CollectionAssert.DoesNotContain(rootPropertyNames, nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings));
        CollectionAssert.DoesNotContain(rootPropertyNames, nameof(PlaylistWorkspaceViewModel.ColumnSettingsVisibilityForPlaylist));
    }

    [TestMethod]
    public void MainTablePresentationCommit_CombinesWorkspaceStateBeforePublishingNotifications()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        var columns = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var summaryColumns = new PlaylistSummaryColumnSettings();
        var selection = new MainChartListColumnSelection(
            columns,
            reused: false,
            elapsedMs: 0L,
            MainViewUpdateMode.FolderFilterSelected,
            Visibility.Visible,
            summaryColumns);
        var propertyNames = new List<string>();
        workspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        PlaylistMainTablePresentationCommit commit = workspace.CommitMainTablePresentationWithoutNotification(
            selection,
            playlistDetailActive: true,
            playlistSummaryActive: false);

        Assert.AreEqual(Visibility.Visible, workspace.ColumnSettingsVisibilityForPlaylist);
        Assert.AreSame(summaryColumns, workspace.PlaylistSummaryColumnsSettings);
        Assert.IsTrue(workspace.IsPlaylistDetailViewActive);
        Assert.AreEqual(0, propertyNames.Count);

        workspace.PublishMainTablePresentation(commit);

        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.ColumnSettingsVisibilityForPlaylist));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.IsPlaylistDetailViewActive));
    }

    [TestMethod]
    public void PlaylistWorkspaceSummarySortRequestDrainsOwnerPresentationState()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        workspace.PlaylistPresentationRefreshRequested += (_, request) =>
        {
            if (request.Kind == PlaylistPresentationRefreshKind.SummaryData)
            {
                workspace.ApplyPlaylistSummaryDataRefresh(deferred: false, request.RebuildAsync);
            }
            else if (request.Kind == PlaylistPresentationRefreshKind.SummaryPresentation)
            {
                workspace.ApplyPlaylistSummaryPresentationRefresh(deferred: false);
            }
        };
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(
            new[]
            {
                new PlaylistSummaryRow { TotalCharts = 1 },
                new PlaylistSummaryRow { TotalCharts = 3 }
            },
            dataGeneration));
        long presentationGenerationBefore = workspace.CurrentPlaylistSummaryPresentationGeneration;
        int callerThreadId = Thread.CurrentThread.ManagedThreadId;
        int propertyChangedCount = 0;
        int propertyChangedThreadId = 0;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummarySortParameters))
            {
                propertyChangedCount++;
                propertyChangedThreadId = Thread.CurrentThread.ManagedThreadId;
            }
        };

        workspace.RequestPlaylistSummarySort(
            nameof(PlaylistSummaryRow.TotalCharts),
            System.ComponentModel.ListSortDirection.Descending);

        Assert.AreEqual(1, propertyChangedCount);
        Assert.AreEqual(callerThreadId, propertyChangedThreadId);
        Assert.AreEqual(nameof(PlaylistSummaryRow.TotalCharts), workspace.PlaylistSummarySortParameters.ColumnsName);
        Assert.AreEqual(System.ComponentModel.ListSortDirection.Descending, workspace.PlaylistSummarySortParameters.Direction);
        Assert.IsTrue(workspace.CurrentPlaylistSummaryPresentationGeneration > presentationGenerationBefore);
        Assert.AreEqual(2, workspace.PlaylistSummaryView.Count);
        Assert.AreEqual(3, workspace.PlaylistSummaryView[0].TotalCharts);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryFiltersDrainVisiblePresentationAndStayDeferredWhenHidden()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(
            new[]
            {
                new PlaylistSummaryRow { Name = "alpha", TotalCharts = 2, OwnedCharts = 2 },
                new PlaylistSummaryRow { Name = "beta", TotalCharts = 3, OwnedCharts = 1 }
            },
            dataGeneration));

        workspace.RequestPlaylistSummaryPresentationRefresh();
        Assert.AreEqual(2, workspace.PlaylistSummaryView.Count);
        long initialPresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
        workspace.PlaylistSummaryKeywordFilter = "alpha";
        long keywordPresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
        Assert.IsTrue(keywordPresentationGeneration > initialPresentationGeneration);
        Assert.AreEqual(1, workspace.PlaylistSummaryView.Count);
        Assert.AreEqual("alpha", workspace.PlaylistSummaryView[0].Name);
        Assert.AreEqual(
            string.Format(
                BeMusicSeeker.Properties.Resources.Playlist_summary_format,
                2,
                1),
            workspace.PlaylistSummaryText);

        workspace.PlaylistSummaryKeywordFilter = string.Empty;
        Assert.AreEqual(2, workspace.PlaylistSummaryView.Count);
        workspace.PlaylistSummaryOwnedFilter = PlaylistOwnedFilter.OwnedComplete;
        long ownedPresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
        Assert.IsTrue(ownedPresentationGeneration > keywordPresentationGeneration);
        Assert.AreEqual(1, workspace.PlaylistSummaryView.Count);
        Assert.AreEqual("alpha", workspace.PlaylistSummaryView[0].Name);

        Assert.IsTrue(workspace.SetPlaylistSummaryMode(enabled: false));
        long hiddenPresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
        workspace.PlaylistSummaryKeywordFilter = string.Empty;
        workspace.PlaylistSummaryOwnedFilter = PlaylistOwnedFilter.All;
        Assert.AreEqual(hiddenPresentationGeneration, workspace.CurrentPlaylistSummaryPresentationGeneration);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryPresentationRefreshRespectsShellSuppressionGate()
    {
        bool suppressed = true;
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            presentationRefreshDeferredProvider: request =>
                request.Kind == PlaylistPresentationRefreshKind.SummaryPresentation && suppressed);
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(
            new[]
            {
                new PlaylistSummaryRow { Name = "alpha", TotalCharts = 2, OwnedCharts = 2 },
                new PlaylistSummaryRow { Name = "beta", TotalCharts = 3, OwnedCharts = 1 }
            },
            dataGeneration));

        suppressed = false;
        workspace.RequestPlaylistSummaryPresentationRefresh();
        suppressed = true;
        long initialPresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
        workspace.PlaylistSummaryKeywordFilter = "alpha";
        Assert.AreEqual(initialPresentationGeneration, workspace.CurrentPlaylistSummaryPresentationGeneration);
        Assert.AreEqual(2, workspace.PlaylistSummaryView.Count);
        Assert.IsTrue(workspace.HasDeferredPlaylistSummaryPresentationRefresh());

        suppressed = false;
        workspace.PlaylistSummaryKeywordFilter = "beta";
        Assert.IsTrue(workspace.CurrentPlaylistSummaryPresentationGeneration > initialPresentationGeneration);
        Assert.AreEqual(1, workspace.PlaylistSummaryView.Count);
        Assert.AreEqual("beta", workspace.PlaylistSummaryView[0].Name);
        Assert.IsFalse(workspace.HasDeferredPlaylistSummaryPresentationRefresh());
    }

    [TestMethod]
    public void PlaylistWorkspaceDetailSortRequestCommitsOwnerStateBeforeEvent()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        int raisedCount = 0;
        MainChartListSortRequestedEventArgs? observedRequest = null;
        workspace.PlaylistDetailSortChanged += (_, request) =>
        {
            raisedCount++;
            observedRequest = request;
            Assert.AreEqual(nameof(PlaylistDetailRow.Level), workspace.PlaylistDetailSortParameters.ColumnsName);
            Assert.AreEqual(System.ComponentModel.ListSortDirection.Descending, workspace.PlaylistDetailSortParameters.Direction);
        };

        workspace.RequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Level),
            System.ComponentModel.ListSortDirection.Descending);

        Assert.AreEqual(1, raisedCount);
        Assert.IsNotNull(observedRequest);
        Assert.AreEqual(MainChartListSortTarget.Regular, observedRequest.Target);
        Assert.AreEqual(1L, observedRequest.OwnerRevision);

        workspace.RequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Level),
            System.ComponentModel.ListSortDirection.Descending);

        Assert.AreEqual(1, raisedCount);
    }

    [TestMethod]
    public void PlaylistWorkspaceRoutesSharedDetailSortOnlyWhileDetailIsActive()
    {
        var workspace = CreateDetailWorkspace(out _);
        workspace.InitializePlaylistDetailSort(new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.Title),
            Direction = System.ComponentModel.ListSortDirection.Ascending
        });
        int raisedCount = 0;
        workspace.PlaylistDetailSortChanged += (_, _) => raisedCount++;

        Assert.IsFalse(workspace.TryRequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Level),
            System.ComponentModel.ListSortDirection.Descending));
        workspace.IsPlaylistDetailViewActive = true;
        Assert.IsTrue(workspace.TryRequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Level),
            System.ComponentModel.ListSortDirection.Descending));
        Assert.AreEqual(1, raisedCount);
        Assert.AreEqual(nameof(PlaylistDetailRow.Level), workspace.PlaylistDetailSortParameters.ColumnsName);

        workspace.IsPlaylistSummaryMode = true;
        Assert.IsFalse(workspace.TryRequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Artist),
            System.ComponentModel.ListSortDirection.Ascending));
        Assert.AreEqual(nameof(PlaylistDetailRow.Level), workspace.PlaylistDetailSortParameters.ColumnsName);

        workspace.IsPlaylistSummaryMode = false;
        workspace.IsPlaylistDetailViewActive = false;
        Assert.IsFalse(workspace.TryRequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Artist),
            System.ComponentModel.ListSortDirection.Ascending));
    }

    [TestMethod]
    public void PlaylistWorkspaceDetailSortInitializationUsesFallbackOnlyOnce()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        var initialSort = new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.Level),
            Direction = System.ComponentModel.ListSortDirection.Descending
        };

        workspace.InitializePlaylistDetailSort(initialSort);

        Assert.AreEqual(nameof(PlaylistDetailRow.Level), workspace.PlaylistDetailSortParameters.ColumnsName);
        Assert.AreEqual(System.ComponentModel.ListSortDirection.Descending, workspace.PlaylistDetailSortParameters.Direction);
        Assert.AreNotSame(initialSort, workspace.PlaylistDetailSortParameters);

        workspace.InitializePlaylistDetailSort(new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.Title),
            Direction = System.ComponentModel.ListSortDirection.Ascending
        });

        Assert.AreEqual(nameof(PlaylistDetailRow.Level), workspace.PlaylistDetailSortParameters.ColumnsName);
        Assert.AreEqual(System.ComponentModel.ListSortDirection.Descending, workspace.PlaylistDetailSortParameters.Direction);
    }

    [TestMethod]
    public void PlaylistWorkspaceDetailFilterRequestCommitsOwnerStateBeforeEventAndRejectsNone()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        workspace.InitializePlaylistDetailFilter(
            new ChartListFilterSnapshot("  title:Alpha  ", ChartModeFilter.All));
        int raisedCount = 0;
        PlaylistDetailFilterChangedEventArgs? firstRequest = null;
        PlaylistDetailFilterChangedEventArgs? secondRequest = null;
        workspace.PlaylistDetailFilterChanged += (_, request) =>
        {
            raisedCount++;
            if (raisedCount == 1)
            {
                firstRequest = request;
            }
            else
            {
                secondRequest = request;
            }
            Assert.AreEqual("  title:Beta  ", workspace.PlaylistDetailFilterSnapshot.KeywordFilter);
        };

        workspace.RequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("  title:Beta  ", ChartModeFilter.All));

        Assert.AreEqual(1, raisedCount);
        Assert.IsNotNull(firstRequest);
        Assert.AreEqual(MainViewUpdateMode.KeywordFilterUpdated, firstRequest.UpdateMode);
        Assert.AreEqual(1L, firstRequest.OwnerRevision);
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailFilterRequest(firstRequest));

        workspace.RequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("  title:Beta  ", ChartModeFilter.All));
        Assert.AreEqual(1, raisedCount);

        workspace.RequestPlaylistDetailFilter(
            MainViewUpdateMode.ModeFilterUpdated,
            new ChartListFilterSnapshot("  title:Beta  ", ChartModeFilter._7KEYS));

        Assert.AreEqual(2, raisedCount);
        Assert.IsNotNull(secondRequest);
        Assert.AreEqual(MainViewUpdateMode.ModeFilterUpdated, secondRequest.UpdateMode);
        Assert.AreEqual(2L, secondRequest.OwnerRevision);
        Assert.IsFalse(workspace.IsCurrentPlaylistDetailFilterRequest(firstRequest));
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailFilterRequest(secondRequest));
        Assert.AreEqual(ChartModeFilter._7KEYS, workspace.PlaylistDetailFilterSnapshot.ModeFilter);

        workspace.RequestPlaylistDetailFilter(
            MainViewUpdateMode.ModeFilterUpdated,
            new ChartListFilterSnapshot("  title:Beta  ", ChartModeFilter.None));

        Assert.AreEqual(2, raisedCount);
        Assert.AreEqual(ChartModeFilter._7KEYS, workspace.PlaylistDetailFilterSnapshot.ModeFilter);
    }

    [TestMethod]
    public void PlaylistWorkspaceRoutesSharedDetailFilterOnlyWhileDetailIsActive()
    {
        var workspace = CreateDetailWorkspace(out _);
        workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot("initial", ChartModeFilter.All));
        int raisedCount = 0;
        workspace.PlaylistDetailFilterChanged += (_, _) => raisedCount++;

        Assert.IsFalse(workspace.TryRequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("before-detail", ChartModeFilter.All)));
        Assert.AreEqual("initial", workspace.PlaylistDetailFilterSnapshot.KeywordFilter);

        workspace.IsPlaylistDetailViewActive = true;
        Assert.IsTrue(workspace.TryRequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("detail", ChartModeFilter.All)));
        Assert.AreEqual(1, raisedCount);
        Assert.AreEqual("detail", workspace.PlaylistDetailFilterSnapshot.KeywordFilter);

        Assert.IsTrue(workspace.TryRequestPlaylistDetailFilter(
            MainViewUpdateMode.ModeFilterUpdated,
            new ChartListFilterSnapshot("detail", ChartModeFilter.None)));
        Assert.AreEqual(1, raisedCount);
        Assert.AreEqual(ChartModeFilter.All, workspace.PlaylistDetailFilterSnapshot.ModeFilter);

        workspace.IsPlaylistSummaryMode = true;
        Assert.IsFalse(workspace.TryRequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("summary", ChartModeFilter.All)));
        Assert.AreEqual("detail", workspace.PlaylistDetailFilterSnapshot.KeywordFilter);

        workspace.IsPlaylistSummaryMode = false;
        workspace.IsPlaylistDetailViewActive = false;
        Assert.IsFalse(workspace.TryRequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("after-detail", ChartModeFilter.All)));
    }

    [TestMethod]
    public void PlaylistWorkspaceDetailFilterInitializationResynchronizesAcrossDetailEntries()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);

        int raisedCount = 0;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(workspace.PlaylistDetailFilterSnapshot))
            {
                raisedCount++;
            }
        };

        workspace.InitializePlaylistDetailFilter(
            new ChartListFilterSnapshot("title:Alpha", ChartModeFilter.All));
        workspace.InitializePlaylistDetailFilter(
            new ChartListFilterSnapshot("title:Beta", ChartModeFilter._7KEYS));
        workspace.InitializePlaylistDetailFilter(
            new ChartListFilterSnapshot("title:Beta", ChartModeFilter._7KEYS));

        Assert.AreEqual(2, raisedCount);
        Assert.AreEqual("title:Beta", workspace.PlaylistDetailFilterSnapshot.KeywordFilter);
        Assert.AreEqual(ChartModeFilter._7KEYS, workspace.PlaylistDetailFilterSnapshot.ModeFilter);
    }

    [TestMethod]
    public void PlaylistWorkspaceKeywordWarning_IsOwnedByPlaylistWorkspace()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        var propertyNames = new List<string>();
        workspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        workspace.PlaylistSummaryKeywordFilter = "memo:warning";

        StringAssert.Contains(workspace.PlaylistSummaryKeywordSearchWarningText, "memo");
        Assert.IsTrue(workspace.HasPlaylistSummaryKeywordSearchWarning);
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.PlaylistSummaryKeywordSearchWarningText));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.HasPlaylistSummaryKeywordSearchWarning));
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryModeTransitionOwnsHeaderTextAndCancellation()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        var propertyNames = new List<string>();
        workspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);
        workspace.GridHeaderText = "stale header";
        workspace.PlaylistSummaryText = "stale summary";

        Assert.IsTrue(workspace.SetPlaylistSummaryMode(enabled: true));
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_summary_header, workspace.GridHeaderText);
        Assert.AreEqual("stale summary", workspace.PlaylistSummaryText);
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.GridHeaderText));

        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest request));
        propertyNames.Clear();

        Assert.IsTrue(workspace.SetPlaylistSummaryMode(enabled: false));
        Assert.IsTrue(request.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(workspace.IsPlaylistSummaryMode);
        Assert.AreEqual(string.Empty, workspace.GridHeaderText);
        Assert.AreEqual(string.Empty, workspace.PlaylistSummaryText);
        CollectionAssert.AreEqual(
            new[]
            {
                nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode),
                nameof(PlaylistWorkspaceViewModel.GridHeaderText),
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryText)
            },
            propertyNames);

        Assert.IsFalse(workspace.SetPlaylistSummaryMode(enabled: false));
        workspace.CompletePlaylistSummaryDataBuild(request);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryApplyCommitsRowsAndTextWithoutSelectionRestore()
    {
        var viewModel = MainWindowViewModelTestFactory.Create();
        PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        workspace.InvalidatePlaylistSummaryCache();
        long cacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;
        var rows = new ObservableCollection<PlaylistSummaryRow> { new() { TotalCharts = 3 } };
        var notifications = new List<string>();
        workspace.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        bool applied = workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = rows,
            SummaryText = "3 charts / 1 playlist",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration,
            CacheGeneration = cacheGeneration
        });

        Assert.IsTrue(applied);
        CollectionAssert.AreEqual(
            new[] { nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView), nameof(PlaylistWorkspaceViewModel.PlaylistSummaryText) },
            notifications);
        CollectionAssert.AreEqual(rows, workspace.PlaylistSummaryView);
        Assert.AreEqual("3 charts / 1 playlist", workspace.PlaylistSummaryText);
        Assert.IsTrue(
            workspace.TryGetAppliedPlaylistSummaryPerformanceInteraction(
                out PerformanceInteraction interaction));
        Assert.AreEqual(dataGeneration, interaction.InteractionId);
        Assert.AreEqual(presentationGeneration, interaction.Generation);

        workspace.BeginPlaylistSummaryPresentationGeneration();

        Assert.IsTrue(
            workspace.TryGetAppliedPlaylistSummaryPerformanceInteraction(
                out PerformanceInteraction retainedInteraction));
        Assert.AreEqual(interaction, retainedInteraction);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryApplyRejectsStaleGenerationAndInactiveMode()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        workspace.InvalidatePlaylistSummaryCache();
        long cacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;
        var originalRows = workspace.PlaylistSummaryView;

        Assert.IsFalse(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            SummaryText = "stale presentation",
            PresentationGeneration = presentationGeneration - 1,
            DataRebuildGeneration = dataGeneration,
            CacheGeneration = cacheGeneration
        }));

        long currentDataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        Assert.IsFalse(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            SummaryText = "stale data",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration,
            CacheGeneration = cacheGeneration
        }));

        workspace.InvalidatePlaylistSummaryCache();
        long currentCacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;
        Assert.IsFalse(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            SummaryText = "stale cache",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = currentDataGeneration,
            CacheGeneration = currentCacheGeneration - 1
        }));

        workspace.IsPlaylistSummaryMode = false;
        Assert.IsFalse(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            SummaryText = "inactive",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = currentDataGeneration,
            CacheGeneration = currentCacheGeneration
        }));
        Assert.AreSame(originalRows, workspace.PlaylistSummaryView);
        Assert.AreEqual(string.Empty, workspace.PlaylistSummaryText);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryPublishFailureAggregatesPropertyPublishFailure()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck)
        {
            IsPlaylistSummaryMode = true
        };
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        var rows = new ObservableCollection<PlaylistSummaryRow> { new() };
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView))
            {
                throw new InvalidOperationException("rows binding failed");
            }
        };
        Assert.ThrowsException<PlaylistSummaryPublishException>(() => workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = rows,
            SummaryText = "committed",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration
        }));

        CollectionAssert.AreEqual(rows, workspace.PlaylistSummaryView);
        Assert.AreEqual("committed", workspace.PlaylistSummaryText);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryApplyKeepsStableSourceAndSkipsSamePresentationIdentity()
    {
        var workspace = MainWindowViewModelTestFactory.Create().PlaylistWorkspace;
        workspace.IsPlaylistSummaryMode = true;
        ObservableCollection<PlaylistSummaryRow> stableSource = workspace.PlaylistSummaryView;
        int resetCount = 0;
        int sourceNotificationCount = 0;
        stableSource.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                resetCount++;
            }
        };
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView))
            {
                sourceNotificationCount++;
            }
        };
        var identity = new PlaylistSummaryPresentationIdentity(
            sourceVersion: 11L,
            keywordFilter: string.Empty,
            PlaylistOwnedFilter.All,
            nameof(PlaylistSummaryRow.Name),
            ListSortDirection.Ascending);

        Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = [new PlaylistSummaryRow { PlaylistId = 1 }],
            SummaryText = "first",
            Identity = identity,
            PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
        }));
        Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = [new PlaylistSummaryRow { PlaylistId = 2 }],
            SummaryText = "first",
            Identity = identity,
            PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
        }));

        Assert.AreSame(stableSource, workspace.PlaylistSummaryView);
        Assert.AreEqual(1, workspace.PlaylistSummaryView[0].PlaylistId);
        Assert.AreEqual(1, resetCount);
        Assert.AreEqual(1, sourceNotificationCount);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryTerminalCommitsRowsBeforeVisibleMode()
    {
        var viewModel = MainWindowViewModelTestFactory.Create();
        PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
        var publicationOrder = new List<string>();
        workspace.PlaylistSummaryView.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                publicationOrder.Add("rows");
            }
        };
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode))
            {
                publicationOrder.Add("mode");
            }
        };

        Assert.IsTrue(workspace.RequestPlaylistSummaryMode(enabled: true));
        Assert.IsFalse(workspace.IsPlaylistSummaryMode);
        Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = [new PlaylistSummaryRow { PlaylistId = 3 }],
            SummaryText = "ready",
            Identity = new PlaylistSummaryPresentationIdentity(
                sourceVersion: 17L,
                keywordFilter: string.Empty,
                PlaylistOwnedFilter.All,
                nameof(PlaylistSummaryRow.Name),
                ListSortDirection.Ascending),
            PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
        }));

        CollectionAssert.AreEqual(new[] { "rows", "mode" }, publicationOrder);
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.IsFalse(workspace.IsPlaylistDetailViewActive);
        Assert.AreEqual(
            MainViewOperationSection.Playlist,
            viewModel.MainChartList.CurrentOperationContext.OperationSection);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryCacheSurvivesModeExitUntilCatalogInvalidation()
    {
        var workspace = MainWindowViewModelTestFactory.Create().PlaylistWorkspace;
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(
            [new PlaylistSummaryRow { PlaylistId = 7 }],
            dataGeneration));

        workspace.IsPlaylistSummaryMode = false;
        List<PlaylistSummaryRow> cachedRows =
            workspace.GetPlaylistSummaryRowsCacheSnapshot(out long cacheGeneration, out long cachedDataGeneration);

        Assert.IsNotNull(cachedRows);
        Assert.AreEqual(7, cachedRows[0].PlaylistId);
        Assert.AreEqual(dataGeneration, cachedDataGeneration);
        Assert.AreEqual(workspace.CurrentPlaylistSummaryRowsCacheGeneration, cacheGeneration);

        workspace.InvalidatePlaylistSummaryCache();
        Assert.IsNull(workspace.GetPlaylistSummaryRowsCacheSnapshot(out _, out _));
    }

    [TestMethod]
    public async Task PlaylistWorkspaceSummarySelectionRestoreEventFailureAggregatesWithPropertyFailure()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var first = new BMSTable { playlist_id = 1, name = "First", symbol = "F", bmt_sort = 1 };
            var second = new BMSTable { playlist_id = 2, name = "Second", symbol = "S", bmt_sort = 2 };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([first, second])
            };
            var restoreRequests = new List<PlaylistSummarySelectionRestoreRequest>();
            PlaylistSummaryBmtSortCoordinator bmtSort = new(() => playlist, () => playlist.BMSTables);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistSummaryBmtSort: bmtSort,
                presentationRefreshDeferredProvider: _ => true,
                selectionRestoreSink: request =>
                {
                    restoreRequests.Add(request);
                    throw new InvalidOperationException("selection restore failed");
                });
            workspace.IsPlaylistSummaryMode = true;

            await workspace.DropSummaryRowsInBmtOrderAsync(
                [
                    new PlaylistSummaryRow { PlaylistId = first.playlist_id, TableRef = first },
                    new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second }
                ],
                [new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second }],
                visibleInsertIndex: 0,
                currentPlaylistId: second.playlist_id);
            long dataRebuildGeneration = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
            Assert.IsTrue(dataRebuildGeneration > 0L);
            Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest buildRequest));
            try
            {
                workspace.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView))
                    {
                        throw new InvalidOperationException("rows binding failed");
                    }
                };
                PlaylistSummaryPublishException exception = Assert.ThrowsException<PlaylistSummaryPublishException>(() =>
                    workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
                    {
                        Rows = new ObservableCollection<PlaylistSummaryRow>(),
                        PresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration,
                        DataRebuildGeneration = buildRequest.Generation,
                        CacheGeneration = buildRequest.CacheGeneration
                    }));

                AggregateException? aggregate = exception.InnerException as AggregateException;
                Assert.IsNotNull(aggregate);
                Assert.AreEqual(2, aggregate!.InnerExceptions.Count);
                Assert.AreEqual(1, restoreRequests.Count);
                CollectionAssert.AreEquivalent(new[] { second.playlist_id }, restoreRequests[0].PlaylistIds.ToArray());
                Assert.AreEqual(second.playlist_id, restoreRequests[0].CurrentPlaylistId);
            }
            finally
            {
                workspace.CompletePlaylistSummaryDataBuild(buildRequest);
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
    public async Task PlaylistWorkspaceSummarySelectionRestoreMissingSubscriberAggregatesWithPropertyFailure()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var first = new BMSTable { playlist_id = 1, name = "First", symbol = "F", bmt_sort = 1 };
            var second = new BMSTable { playlist_id = 2, name = "Second", symbol = "S", bmt_sort = 2 };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([first, second])
            };
            Action<PlaylistSummarySelectionRestoreRequest> restoreHandler = _ => { };
            PlaylistSummaryBmtSortCoordinator bmtSort = new(() => playlist, () => playlist.BMSTables);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistSummaryBmtSort: bmtSort,
                presentationRefreshDeferredProvider: _ => true,
                selectionRestoreSink: restoreHandler);
            workspace.PlaylistSummarySelectionRestoreRequested -= restoreHandler;
            workspace.IsPlaylistSummaryMode = true;

            await workspace.DropSummaryRowsInBmtOrderAsync(
                [
                    new PlaylistSummaryRow { PlaylistId = first.playlist_id, TableRef = first },
                    new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second }
                ],
                [new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second }],
                visibleInsertIndex: 0,
                currentPlaylistId: second.playlist_id);
            Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest buildRequest));
            try
            {
                workspace.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView))
                    {
                        throw new InvalidOperationException("rows binding failed");
                    }
                };
                PlaylistSummaryPublishException exception = Assert.ThrowsException<PlaylistSummaryPublishException>(() =>
                    workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
                    {
                        Rows = new ObservableCollection<PlaylistSummaryRow>(),
                        PresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration,
                        DataRebuildGeneration = buildRequest.Generation,
                        CacheGeneration = buildRequest.CacheGeneration
                    }));

                AggregateException? aggregate = exception.InnerException as AggregateException;
                Assert.IsNotNull(aggregate);
                Assert.AreEqual(2, aggregate!.InnerExceptions.Count);
            }
            finally
            {
                workspace.CompletePlaylistSummaryDataBuild(buildRequest);
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
    public async Task PlaylistWorkspaceSummarySelectionRestoreDoesNotReachUnsubscribedView()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var first = new BMSTable { playlist_id = 1, name = "First", symbol = "F", bmt_sort = 1 };
            var second = new BMSTable { playlist_id = 2, name = "Second", symbol = "S", bmt_sort = 2 };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([first, second])
            };
            Queue<Action> pendingActions = new();
            int invocationCount = 0;
            Action<PlaylistSummarySelectionRestoreRequest> restoreHandler = _ => invocationCount++;
            PlaylistSummaryBmtSortCoordinator bmtSort = new(() => playlist, () => playlist.BMSTables);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                dispatchPresentation: action => pendingActions.Enqueue(action),
                playlistStoreProvider: () => playlist,
                playlistSummaryBmtSort: bmtSort,
                presentationRefreshDeferredProvider: _ => true,
                selectionRestoreSink: restoreHandler);
            workspace.IsPlaylistSummaryMode = true;

            await workspace.DropSummaryRowsInBmtOrderAsync(
                [
                    new PlaylistSummaryRow { PlaylistId = first.playlist_id, TableRef = first },
                    new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second }
                ],
                [new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second }],
                visibleInsertIndex: 0,
                currentPlaylistId: second.playlist_id);
            Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest buildRequest));
            try
            {
                Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
                {
                    Rows = new ObservableCollection<PlaylistSummaryRow>(),
                    PresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration,
                    DataRebuildGeneration = buildRequest.Generation,
                    CacheGeneration = buildRequest.CacheGeneration
                }));
            }
            finally
            {
                workspace.CompletePlaylistSummaryDataBuild(buildRequest);
            }

            Assert.IsTrue(pendingActions.Count > 0);
            workspace.PlaylistSummarySelectionRestoreRequested -= restoreHandler;
            while (pendingActions.Count > 0)
            {
                pendingActions.Dequeue()();
            }
            Assert.AreEqual(0, invocationCount);
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
    public async Task PlaylistWorkspaceBackupPlaylistAsync_PublishesSuccessWarningAndFailureReceipts()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            PlaylistWorkspaceViewModel unavailableWorkspace = CreateDetailWorkspace(out _);
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> unavailableNotifications = [];
            unavailableWorkspace.PlaylistOperationNotificationPresentationRequested +=
                (_, request) => unavailableNotifications.Add(request);
            string unavailablePath = Path.Combine(tempDirectory, "unavailable-backup.sql");

            await unavailableWorkspace.BackupPlaylistAsync(unavailablePath);

            Assert.IsFalse(File.Exists(unavailablePath));
            Assert.AreEqual(1, unavailableNotifications.Count);
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning,
                unavailableNotifications[0].Receipt.Notifications.Single().Severity);
            StringAssert.Contains(
                unavailableNotifications[0].Receipt.Notifications.Single().Message,
                BeMusicSeeker.Properties.Resources.Msg_warn_playlist_backup);

            string successDbDirectory = Path.Combine(tempDirectory, "success");
            Directory.CreateDirectory(successDbDirectory);
            string successDbPath = Path.Combine(successDbDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(successDbPath))
            {
            }
            BMSPlaylist successPlaylist;
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> successNotifications;
            PlaylistWorkspaceViewModel successWorkspace = CreateBackupWorkspace(
                successDbPath,
                [new BMSTable { playlist_id = 1, name = "Backup", symbol = "B", bmt_sort = 1 }],
                out successPlaylist,
                out successNotifications);
            string expectedDump = successPlaylist.GetPlaylistDump();
            string successPath = Path.Combine(tempDirectory, "backup.sql");

            await successWorkspace.BackupPlaylistAsync(successPath);

            Assert.IsTrue(File.Exists(successPath));
            Assert.AreEqual(expectedDump, File.ReadAllText(successPath));
            Assert.AreEqual(1, successNotifications.Count);
            Assert.AreEqual("playlist backup notification", successNotifications[0].RouteName);
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information,
                successNotifications[0].Receipt.Notifications.Single().Severity);
            StringAssert.Contains(
                successNotifications[0].Receipt.Notifications.Single().Message,
                BeMusicSeeker.Properties.Resources.Msg_success_playlist_backup);

            string emptyDbDirectory = Path.Combine(tempDirectory, "empty");
            Directory.CreateDirectory(emptyDbDirectory);
            string emptyDbPath = Path.Combine(emptyDbDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(emptyDbPath))
            {
            }
            BMSPlaylist emptyPlaylist;
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> emptyNotifications;
            PlaylistWorkspaceViewModel emptyWorkspace = CreateBackupWorkspace(
                emptyDbPath,
                [],
                out emptyPlaylist,
                out emptyNotifications);
            string emptyPath = Path.Combine(tempDirectory, "empty-backup.sql");

            await emptyWorkspace.BackupPlaylistAsync(emptyPath);

            Assert.IsFalse(File.Exists(emptyPath));
            Assert.AreEqual(1, emptyNotifications.Count);
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning,
                emptyNotifications[0].Receipt.Notifications.Single().Severity);
            StringAssert.Contains(
                emptyNotifications[0].Receipt.Notifications.Single().Message,
                BeMusicSeeker.Properties.Resources.Msg_warn_playlist_backup);

            string failureDbDirectory = Path.Combine(tempDirectory, "failure");
            Directory.CreateDirectory(failureDbDirectory);
            string failureDbPath = Path.Combine(failureDbDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(failureDbPath))
            {
            }
            BMSPlaylist failurePlaylist;
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> failureNotifications;
            PlaylistWorkspaceViewModel failureWorkspace = CreateBackupWorkspace(
                failureDbPath,
                [new BMSTable { playlist_id = 2, name = "Failure", symbol = "F", bmt_sort = 1 }],
                out failurePlaylist,
                out failureNotifications);
            string invalidPath = Path.Combine(tempDirectory, "missing", "backup.sql");

            await failureWorkspace.BackupPlaylistAsync(invalidPath);

            Assert.IsFalse(File.Exists(invalidPath));
            Assert.AreEqual(1, failureNotifications.Count);
            PlaylistOperationNotificationOwner.OperationNotification failureNotification =
                failureNotifications[0].Receipt.Notifications.Single();
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error,
                failureNotification.Severity);
            StringAssert.Contains(
                failureNotification.Message,
                BeMusicSeeker.Properties.Resources.Msg_failed_playlist_backup);
            StringAssert.Contains(failureNotification.Message, "missing");
            Assert.IsFalse(failureNotifications.Any(request => request.Receipt.Notifications.Any(
                notification => notification.Message.IndexOf(
                    BeMusicSeeker.Properties.Resources.Msg_success_playlist_backup,
                    StringComparison.Ordinal) >= 0)));
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
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_ReplacesTablesAndPublishesSuccess()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Execute("INSERT INTO playlist (playlist_id, name, symbol, folder_order, folder_sort_key, folder_sort_ascending, entry_type, page_url, header_url, data_url, compat_prefix, last_update, org_name, org_symbol, ignore_folder_output, is_external_sync, output_dir, is_root_folder) VALUES (1, 'Before restore', 'before', '', 0, 1, 0, NULL, NULL, NULL, NULL, 638857728000000000, NULL, NULL, 0, 0, NULL, 0);");
            }

            int schedulerCallCount = 0;
            BMSPlaylist? scheduledPlaylist = null;
            BMSPlaylist playlist;
            PlaylistWorkspaceViewModel workspace = CreateBackupWorkspace(
                songDbPath,
                [new BMSTable { playlist_id = 1, name = "Before restore", symbol = "before" }],
                out playlist,
                out List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications,
                action =>
                {
                    schedulerCallCount++;
                    action();
                    return Task.CompletedTask;
                });
            scheduledPlaylist = playlist;
            string backupPath = Path.Combine(tempDirectory, "restore.sql");
            File.WriteAllText(backupPath, CreatePlaylistRestoreDump(2, "After restore", "after"), Encoding.UTF8);

            await workspace.RestorePlaylistBackupAsync(backupPath);

            Assert.AreEqual(1, schedulerCallCount);
            Assert.AreEqual(1, notifications.Count);
            Assert.AreEqual("playlist restore notification", notifications[0].RouteName);
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information,
                notifications[0].Receipt.Notifications.Single().Severity);
            Assert.AreEqual("After restore", playlist.BMSTables.Single().name);
            using (var verify = new LR2SongDBExtended(songDbPath))
            {
                BMSTable restored = verify.Table<BMSTable>().Single();
                Assert.AreEqual(2, restored.playlist_id);
                Assert.AreEqual("After restore", restored.name);
            }
            Assert.IsFalse(LR2SongDBExtended.IsProcessLockEnteredByCurrentThread());
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
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_RejectsBackgroundSchedulerBeforeDatabaseApply()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Execute("INSERT INTO playlist (playlist_id, name, symbol, folder_order, folder_sort_key, folder_sort_ascending, entry_type, page_url, header_url, data_url, compat_prefix, last_update, org_name, org_symbol, ignore_folder_output, is_external_sync, output_dir, is_root_folder) VALUES (1, 'Before background restore', 'before', '', 0, 1, 0, NULL, NULL, NULL, NULL, 638857728000000000, NULL, NULL, 0, 0, NULL, 0);");
            }

            PlaylistWorkspaceViewModel workspace = CreateBackupWorkspace(
                songDbPath,
                [new BMSTable { playlist_id = 1, name = "Before background restore", symbol = "before" }],
                out BMSPlaylist _,
                out List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications,
                action => Task.Run(action),
                restoreUiThreadCheck: () => false);
            string backupPath = Path.Combine(tempDirectory, "restore.sql");
            File.WriteAllText(backupPath, CreatePlaylistRestoreDump(2, "Should not apply", "rejected"), Encoding.UTF8);

            await workspace.RestorePlaylistBackupAsync(backupPath);

            Assert.AreEqual(1, notifications.Count);
            PlaylistOperationNotificationOwner.OperationNotification failure = notifications[0].Receipt.Notifications.Single();
            Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error, failure.Severity);
            StringAssert.Contains(failure.Message, "Playlist restore requires the configured UI thread.");
            using (var verify = new LR2SongDBExtended(songDbPath))
            {
                BMSTable existing = verify.Table<BMSTable>().Single();
                Assert.AreEqual(1, existing.playlist_id);
                Assert.AreEqual("Before background restore", existing.name);
            }
            Assert.AreEqual("Before background restore", workspace.CapturePlaylistTreeTablesSnapshot().Single().name);
            Assert.IsFalse(LR2SongDBExtended.IsProcessLockEnteredByCurrentThread());
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
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_RollsBackInvalidDumpAndReleasesLock()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Execute("INSERT INTO playlist (playlist_id, name, symbol, folder_order, folder_sort_key, folder_sort_ascending, entry_type, page_url, header_url, data_url, compat_prefix, last_update, org_name, org_symbol, ignore_folder_output, is_external_sync, output_dir, is_root_folder) VALUES (1, 'Keep on failure', 'keep', '', 0, 1, 0, NULL, NULL, NULL, NULL, 638857728000000000, NULL, NULL, 0, 0, NULL, 0);");
            }

            PlaylistWorkspaceViewModel workspace = CreateBackupWorkspace(
                songDbPath,
                [new BMSTable { playlist_id = 1, name = "Keep on failure", symbol = "keep" }],
                out BMSPlaylist _,
                out List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications);
            string invalidBackupPath = Path.Combine(tempDirectory, "invalid.sql");
            File.WriteAllText(
                invalidBackupPath,
                string.Join("\v" + Environment.NewLine, ["THIS IS NOT SQL", string.Empty, string.Empty]),
                Encoding.UTF8);

            await workspace.RestorePlaylistBackupAsync(invalidBackupPath);

            Assert.AreEqual(1, notifications.Count);
            PlaylistOperationNotificationOwner.OperationNotification failure = notifications[0].Receipt.Notifications.Single();
            Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error, failure.Severity);
            StringAssert.Contains(failure.Message, BeMusicSeeker.Properties.Resources.Msg_failed_playlist_restore);
            using (var verify = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual("Keep on failure", verify.Table<BMSTable>().Single().name);
            }
            Assert.IsFalse(LR2SongDBExtended.IsProcessLockEnteredByCurrentThread());
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
    public async Task PlaylistWorkspaceRestorePlaylistBackupAsync_PropagatesFileReadFailureWithoutReceipt()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications = [];
        workspace.PlaylistOperationNotificationPresentationRequested +=
            (_, request) => notifications.Add(request);
        string missingPath = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests) + "-" + Guid.NewGuid().ToString("N") + ".sql");

        await Assert.ThrowsExceptionAsync<FileNotFoundException>(
            () => workspace.RestorePlaylistBackupAsync(missingPath));

        Assert.AreEqual(0, notifications.Count);
    }

    [TestMethod]
    public async Task PlaylistWorkspaceExportPlaylistTableAsync_WritesJsonAndPreservesDataUrl()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            BMSTable table = new BMSTable
            {
                name = "Export",
                symbol = "EX",
                Folder_order = []
            };
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistWorkspaceDialogService: dialogs);
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications = [];
            workspace.PlaylistOperationNotificationPresentationRequested +=
                (_, request) => notifications.Add(request);
            string headerPath = Path.Combine(tempDirectory, "header.json");
            string dataPath = Path.Combine(tempDirectory, "data.json");
            Uri originalDataUrl = table.Data_url;
            table.Data_url = new Uri(Path.GetFileName(dataPath), UriKind.Relative);
            string expectedHeader = table.HeaderToJson();
            string expectedData = (string)table.DataToJson();
            table.Data_url = originalDataUrl;

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, headerPath));
            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, dataPath));
            await workspace.ExportPlaylistTableAsync(table);

            Assert.IsTrue(File.Exists(headerPath));
            Assert.IsTrue(File.Exists(dataPath));
            Assert.AreEqual(expectedHeader, File.ReadAllText(headerPath));
            Assert.AreEqual(expectedData, File.ReadAllText(dataPath));
            Assert.IsNull(table.Data_url);
            Assert.AreEqual(1, notifications.Count);
            Assert.IsTrue(notifications[0].Receipt.IsEmpty);
            Assert.AreEqual(2, dialogs.SaveFilePickerRequests.Count);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Save_header_file, dialogs.SaveFilePickerRequests[0].Title);
            Assert.AreEqual("header.json", dialogs.SaveFilePickerRequests[0].FileName);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Json_file_exts, dialogs.SaveFilePickerRequests[0].Filter);
            Assert.AreEqual(".json", dialogs.SaveFilePickerRequests[0].DefaultExtension);
            Assert.IsTrue(dialogs.SaveFilePickerRequests[0].AddExtension);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Save_data_file, dialogs.SaveFilePickerRequests[1].Title);
            Assert.AreEqual("data.json", dialogs.SaveFilePickerRequests[1].FileName);

            Uri persistedDataUrl = new Uri("https://example.test/export-data.json");
            table.Data_url = persistedDataUrl;
            string persistedHeaderPath = Path.Combine(tempDirectory, "persisted-header.json");
            string persistedDataPath = Path.Combine(tempDirectory, "persisted-data.json");
            string expectedPersistedHeader = table.HeaderToJson();

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, persistedHeaderPath));
            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, persistedDataPath));
            await workspace.ExportPlaylistTableAsync(table);

            Assert.AreEqual(persistedDataUrl, table.Data_url);
            Assert.AreEqual(expectedPersistedHeader, File.ReadAllText(persistedHeaderPath));
            Assert.AreEqual(expectedData, File.ReadAllText(persistedDataPath));
            Assert.AreEqual(2, notifications.Count);
            Assert.IsTrue(notifications[1].Receipt.IsEmpty);
            Assert.AreEqual("export-data.json", dialogs.SaveFilePickerRequests[3].FileName);
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
    public async Task PlaylistWorkspaceExportPlaylistTableAsync_PublishesFileFailureAndPreservesHeaderFirstOrder()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            BMSTable table = new BMSTable { name = "Export failure", Folder_order = [] };
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistWorkspaceDialogService: dialogs);
            List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications = [];
            workspace.PlaylistOperationNotificationPresentationRequested +=
                (_, request) => notifications.Add(request);
            string headerPath = Path.Combine(tempDirectory, "partial-header.json");
            string dataDirectory = Path.Combine(tempDirectory, "data-directory");
            Directory.CreateDirectory(dataDirectory);

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, headerPath));
            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, dataDirectory));
            await workspace.ExportPlaylistTableAsync(table);

            Assert.IsTrue(File.Exists(headerPath));
            Assert.IsTrue(Directory.Exists(dataDirectory));
            Assert.IsNull(table.Data_url);
            Assert.AreEqual(1, notifications.Count);
            PlaylistOperationNotificationOwner.OperationNotification failure =
                notifications[0].Receipt.Notifications.Single();
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error,
                failure.Severity);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_failed_save_playlist, failure.Message);

            notifications.Clear();
            string invalidHeaderPath = Path.Combine(tempDirectory, "missing", "header.json");
            string validDataPath = Path.Combine(tempDirectory, "unwritten-data.json");

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, invalidHeaderPath));
            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, validDataPath));
            await workspace.ExportPlaylistTableAsync(table);

            Assert.IsFalse(File.Exists(invalidHeaderPath));
            Assert.IsFalse(File.Exists(validDataPath));
            Assert.AreEqual(1, notifications.Count);
            Assert.AreEqual(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error,
                notifications[0].Receipt.Notifications.Single().Severity);
            Assert.AreEqual(
                BeMusicSeeker.Properties.Resources.Msg_failed_save_playlist,
                notifications[0].Receipt.Notifications.Single().Message);
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
    public async Task PlaylistWorkspaceExportPlaylistTableAsync_PickerCancellationStopsBeforeWriting()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistWorkspaceDialogService: dialogs);
            var notifications = new List<PlaylistOperationNotificationPresentationRequestedEventArgs>();
            workspace.PlaylistOperationNotificationPresentationRequested +=
                (_, request) => notifications.Add(request);
            BMSTable table = new BMSTable { name = "Export cancelled", Folder_order = [] };
            string headerPath = Path.Combine(tempDirectory, "cancelled-header.json");
            string dataPath = Path.Combine(tempDirectory, "cancelled-data.json");

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.CancelledByUser));
            await workspace.ExportPlaylistTableAsync(table);

            Assert.AreEqual(1, dialogs.SaveFilePickerRequests.Count);
            Assert.IsFalse(File.Exists(headerPath));
            Assert.IsFalse(File.Exists(dataPath));
            Assert.AreEqual(0, notifications.Count);

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, headerPath));
            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.CancelledByUser));
            await workspace.ExportPlaylistTableAsync(table);

            Assert.AreEqual(3, dialogs.SaveFilePickerRequests.Count);
            Assert.IsFalse(File.Exists(headerPath));
            Assert.IsFalse(File.Exists(dataPath));
            Assert.AreEqual(0, notifications.Count);
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
    public async Task PlaylistWorkspaceExportPlaylistTableAsync_PickerFailurePropagatesBeforeWriting()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistWorkspaceDialogService: dialogs);
            var notifications = new List<PlaylistOperationNotificationPresentationRequestedEventArgs>();
            workspace.PlaylistOperationNotificationPresentationRequested +=
                (_, request) => notifications.Add(request);
            BMSTable table = new BMSTable { name = "Export picker failure", Folder_order = [] };
            string headerPath = Path.Combine(tempDirectory, "failed-header.json");
            string dataPath = Path.Combine(tempDirectory, "failed-data.json");
            var headerError = new IOException("header picker unavailable");

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Failed, error: headerError));
            InvalidOperationException headerException = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => workspace.ExportPlaylistTableAsync(table));
            Assert.AreSame(headerError, headerException.InnerException);
            Assert.AreEqual(1, dialogs.SaveFilePickerRequests.Count);
            Assert.IsFalse(File.Exists(headerPath));
            Assert.IsFalse(File.Exists(dataPath));
            Assert.AreEqual(0, notifications.Count);

            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Accepted, headerPath));
            var dataError = new IOException("data picker unavailable");
            dialogs.SaveFilePickerResults.Enqueue(new UiSaveFilePickerResult(UiDialogStatus.Failed, error: dataError));
            InvalidOperationException dataException = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => workspace.ExportPlaylistTableAsync(table));
            Assert.AreSame(dataError, dataException.InnerException);
            Assert.AreEqual(3, dialogs.SaveFilePickerRequests.Count);
            Assert.IsFalse(File.Exists(headerPath));
            Assert.IsFalse(File.Exists(dataPath));
            Assert.AreEqual(0, notifications.Count);
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
    public void PlaylistWorkspaceSummaryApplyDoesNotPublishSelectionWithoutPendingRestore()
    {
        var restoreRequests = new List<PlaylistSummarySelectionRestoreRequest>();
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck)
        {
            IsPlaylistSummaryMode = true
        };
        workspace.PlaylistSummarySelectionRestoreRequested += restoreRequests.Add;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration
        }));

        Assert.AreEqual(0, restoreRequests.Count);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryDataBuildCancelsSupersededAndHiddenWork()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck)
        {
            IsPlaylistSummaryMode = true
        };

        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest first));
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest second));

        Assert.IsTrue(first.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(second.CancellationToken.IsCancellationRequested);
        Assert.IsTrue(second.Generation > first.Generation);

        workspace.IsPlaylistSummaryMode = false;

        Assert.IsTrue(second.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(workspace.TryBeginPlaylistSummaryDataBuild(out _));
        workspace.CompletePlaylistSummaryDataBuild(first);
        workspace.CompletePlaylistSummaryDataBuild(second);
        Assert.IsTrue(workspace.IsPlaylistSummaryDataBuildIdle);
    }

    [TestMethod]
    public async Task PlaylistSummaryDataBuildIdleWaitIncludesEveryOverlappingBuild()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        workspace.IsPlaylistSummaryMode = true;
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest first));
        Task idle = workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest second));

        workspace.CompletePlaylistSummaryDataBuild(first);
        Assert.IsFalse(idle.IsCompleted);
        workspace.CompletePlaylistSummaryDataBuild(second);

        await idle.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    [TestMethod]
    public void PlaylistWorkspaceTableCountCacheReusesContentKeyAcrossDataGenerations()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        var expected = new PlaylistSummaryCountResult
        {
            ScannedEntries = 4,
            TotalCharts = 3,
            OwnedCharts = 2
        };
        var table = new BMSTable
        {
            playlist_id = 1
        };
        Assert.IsTrue(workspace.CatalogSummaryOwner.TrySetTableCount(
            table,
            ownedSnapshotVersion: 1,
            countResult: expected,
            expectedGeneration: workspace.CatalogSummaryOwner.TableCountCacheGeneration));

        workspace.BeginPlaylistSummaryDataRebuildGeneration();
        workspace.BeginPlaylistSummaryDataRebuildGeneration();

        Assert.IsTrue(workspace.CatalogSummaryOwner.TryGetTableCount(table, ownedSnapshotVersion: 1, out PlaylistSummaryCountResult actual));
        Assert.AreEqual(expected.ScannedEntries, actual.ScannedEntries);
        Assert.AreEqual(expected.TotalCharts, actual.TotalCharts);
        Assert.AreEqual(expected.OwnedCharts, actual.OwnedCharts);

        workspace.CatalogSummaryOwner.InvalidateTableCounts();
        Assert.IsFalse(workspace.CatalogSummaryOwner.TryGetTableCount(table, ownedSnapshotVersion: 1, out _));
    }

    [TestMethod]
    public void PlaylistWorkspaceTableCountCacheRejectsResultFromBuildBeforeInvalidation()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        workspace.IsPlaylistSummaryMode = true;
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest staleBuild));
        var staleResult = new PlaylistSummaryCountResult
        {
            ScannedEntries = 4,
            TotalCharts = 3,
            OwnedCharts = 2
        };

        long tableCountGeneration = workspace.CatalogSummaryOwner.TableCountCacheGeneration;
        workspace.CatalogSummaryOwner.InvalidateTableCounts();

        var table = new BMSTable
        {
            playlist_id = 1
        };
        Assert.IsFalse(workspace.CatalogSummaryOwner.TrySetTableCount(
            table,
            ownedSnapshotVersion: 1,
            countResult: staleResult,
            expectedGeneration: tableCountGeneration));
        Assert.IsFalse(workspace.CatalogSummaryOwner.TryGetTableCount(table, ownedSnapshotVersion: 1, out _));
        workspace.CompletePlaylistSummaryDataBuild(staleBuild);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryDataBuildCannotRestartAfterShutdownStop()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck)
        {
            IsPlaylistSummaryMode = true
        };
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest activeBuild));
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();

        workspace.StopPlaylistSummaryDataBuild();

        Assert.IsTrue(activeBuild.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(workspace.TryBeginPlaylistSummaryDataBuild(out _));
        Assert.IsFalse(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = activeBuild.Generation
        }));
        workspace.CompletePlaylistSummaryDataBuild(activeBuild);
        Assert.IsTrue(workspace.IsPlaylistSummaryDataBuildIdle);
    }

    [TestMethod]
    public void PlaylistWorkspaceDeferredDataRefreshCancelsBuildAndDominatesPresentation()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck)
        {
            IsPlaylistSummaryMode = true
        };
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest activeBuild));
        workspace.RequestDeferredPlaylistSummaryPresentationRefresh();

        long nextBuildGeneration = workspace.RequestPlaylistSummaryDataRefresh().NextBuildGeneration;
        workspace.RequestDeferredPlaylistSummaryPresentationRefresh();

        Assert.IsTrue(activeBuild.CancellationToken.IsCancellationRequested);
        Assert.AreEqual(workspace.CurrentPlaylistSummaryDataRebuildGeneration + 1L, nextBuildGeneration);
        Assert.IsTrue(workspace.HasDeferredPlaylistSummaryRefresh());
        Assert.AreEqual(
            PlaylistSummaryDeferredRefreshKind.Data,
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false));
        Assert.AreEqual(
            PlaylistSummaryDeferredRefreshKind.None,
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false));
        workspace.CompletePlaylistSummaryDataBuild(activeBuild);
    }

    [TestMethod]
    public void PlaylistWorkspaceDeferredRefreshIsAtomicWithExternalDataPriorityAndModeExit()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck)
        {
            IsPlaylistSummaryMode = true
        };
        workspace.RequestDeferredPlaylistSummaryPresentationRefresh();

        Assert.AreEqual(
            PlaylistSummaryDeferredRefreshKind.Data,
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: true));

        workspace.RequestDeferredPlaylistSummaryPresentationRefresh();
        workspace.IsPlaylistSummaryMode = false;
        workspace.IsPlaylistSummaryMode = true;

        Assert.AreEqual(
            PlaylistSummaryDeferredRefreshKind.None,
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false));
    }

    [TestMethod]
    public void PlaylistWorkspaceDataRefreshRequestOwnsVisibilityAndDeferralDecision()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        long hiddenDataGeneration = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
        long hiddenCacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;

        PlaylistSummaryDataRefreshRequestResult hidden = workspace.RequestPlaylistSummaryDataRefresh();

        Assert.IsFalse(hidden.Queued);
        Assert.AreEqual(0L, hidden.NextBuildGeneration);
        Assert.IsTrue(workspace.CurrentPlaylistSummaryDataRebuildGeneration > hiddenDataGeneration);
        Assert.IsTrue(workspace.CurrentPlaylistSummaryRowsCacheGeneration > hiddenCacheGeneration);

        workspace.IsPlaylistSummaryMode = true;
        PlaylistSummaryDataRefreshRequestResult visible = workspace.RequestPlaylistSummaryDataRefresh();
        Assert.IsTrue(visible.Queued);
        Assert.AreEqual(workspace.CurrentPlaylistSummaryDataRebuildGeneration + 1L, visible.NextBuildGeneration);

        PlaylistSummaryDataRefreshRequestResult coalesced = workspace.RequestPlaylistSummaryDataRefresh();
        Assert.IsTrue(coalesced.Queued);
        Assert.AreEqual(workspace.CurrentPlaylistSummaryDataRebuildGeneration + 1L, coalesced.NextBuildGeneration);
        Assert.AreEqual(
            PlaylistSummaryDeferredRefreshKind.Data,
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false));
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryDataRefreshUsesShellGateAndDrainsWhenAllowed()
    {
        bool deferred = true;
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            presentationRefreshDeferredProvider: _ => deferred);
        workspace.IsPlaylistSummaryMode = true;

        long initialGeneration = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
        long deferredGeneration = workspace.RequestPlaylistSummaryDataRefresh(
            "test_deferred_summary_data",
            rebuildAsync: false);

        Assert.AreEqual(initialGeneration + 2L, deferredGeneration);
        Assert.IsTrue(
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false)
                == PlaylistSummaryDeferredRefreshKind.Data);

        deferred = false;
        long drainedGeneration = workspace.RequestPlaylistSummaryDataRefresh(
            "test_drained_summary_data",
            rebuildAsync: false);

        Assert.IsTrue(drainedGeneration > deferredGeneration);
        Assert.IsTrue(
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false)
                == PlaylistSummaryDeferredRefreshKind.None);
        Assert.IsTrue(workspace.IsPlaylistSummaryDataBuildIdle);

        workspace.IsPlaylistSummaryMode = false;
        long hiddenGenerationBefore = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
        Assert.AreEqual(
            0L,
            workspace.RequestPlaylistSummaryDataRefresh(
                "test_hidden_summary_data",
                rebuildAsync: false));
        Assert.IsTrue(workspace.CurrentPlaylistSummaryDataRebuildGeneration > hiddenGenerationBefore);
    }

    [TestMethod]
    public void PlaylistWorkspaceDeferredRefreshDrainOwnsHiddenAndPresentationRoutes()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);

        Assert.AreEqual(
            0L,
            workspace.DrainDeferredPlaylistSummaryRefresh(
                dataRefreshRequired: true,
                rebuildAsync: false));

        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        var rows = new List<PlaylistSummaryRow> { new() };
        Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(rows, dataGeneration));
        workspace.RequestDeferredPlaylistSummaryPresentationRefresh();

        long presentationGenerationBefore = workspace.CurrentPlaylistSummaryPresentationGeneration;
        Assert.AreEqual(
            0L,
            workspace.DrainDeferredPlaylistSummaryRefresh(
                dataRefreshRequired: false,
                rebuildAsync: false));
        Assert.IsTrue(workspace.CurrentPlaylistSummaryPresentationGeneration > presentationGenerationBefore);
        Assert.AreEqual(1, workspace.PlaylistSummaryView.Count);
    }

    [TestMethod]
    public async Task PlaylistDropReferenceIndex_IsUpdatedBeforeInvalidationNotification()
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
                playlist_id = 7815,
                name = "Drop target",
                symbol = "DROP",
                Output_dir = "DropTarget"
            };
            var playlist = new TestBmsPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot(),
                null)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var library = new TestBmsLibrary(songDbPath);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library);
            workspace.RefreshPlaylistTreeTables(playlist);
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };

            const string md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
            ChartFile chart = ChartFileProjection.FromBmsFile(
                BMSFile.FromSongTableRawValues(CreateSongTableRow(md5, @"C:\Library\drop-chart.bms")));
            LibraryChartRow libraryRow = LibraryChartRow.FromChartFile(chart);
            PlaylistReferenceDisplay? displayObservedDuringInvalidation = null;
            workspace.PlaylistReferenceSortInvalidationRequested += (_, _) =>
                displayObservedDuringInvalidation = library.GetPlaylistReferenceDisplay(chart);

            await workspace.AddRowsToFolderAsync(
                [libraryRow],
                table,
                PlaylistFolderNode.CreateFolder("Imported"));

            Assert.IsNotNull(displayObservedDuringInvalidation);
            Assert.AreEqual("DROP", displayObservedDuringInvalidation.Symbols);
            Assert.AreEqual("Drop target", displayObservedDuringInvalidation.Names);
            Assert.AreEqual("DROP", library.GetPlaylistReferenceDisplay(chart).Symbols);
            Assert.AreEqual("Drop target", library.GetPlaylistReferenceDisplay(chart).Names);
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
    public void PlaylistWorkspaceDropPolicyRejectsMixedExternalAndSpecialTargets()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        var table = new BMSTable();
        var entry = new TestablePlaylistEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder");
        var playlistRow = new PlaylistDetailSourceRow(entry, resolvedChart: null).CreateViewRow();
        var specialFolder = PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned);

        Assert.IsTrue(workspace.CanAcceptDrop([playlistRow], table, PlaylistFolderNode.CreateFolder("Folder")));
        Assert.IsFalse(workspace.CanAcceptDrop([playlistRow, new PlaylistSummaryRow()], table));
        Assert.IsFalse(workspace.CanAcceptDrop([playlistRow], table, specialFolder));

        table.is_external_sync = true;
        Assert.IsFalse(workspace.CanAcceptDrop([playlistRow], table));
    }

    [TestMethod]
    public async Task PlaylistWorkspaceExternalMutationRejectsBeforePersistenceAccess()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        var table = new BMSTable { is_external_sync = true };
        var rejectedKinds = new List<PlaylistWorkspaceMutationKind>();
        workspace.MutationRejected += (_, request) => rejectedKinds.Add(request.Kind);

        await workspace.CreateFolderAsync(table);
        await workspace.RenameFolderAsync(table, PlaylistFolderNode.CreateFolder("Folder"), "Renamed");
        await workspace.AddRowsToFolderAsync([], table);
        await workspace.DeleteSelectedEntriesAsync([
            new PlaylistDetailSourceRow(new BMSTableEntry { parent = table }, resolvedChart: null)]);

        CollectionAssert.AreEqual(
            new[]
            {
                PlaylistWorkspaceMutationKind.CreateFolder,
                PlaylistWorkspaceMutationKind.RenameFolder,
                PlaylistWorkspaceMutationKind.AddEntries,
                PlaylistWorkspaceMutationKind.RemoveEntries
            },
            rejectedKinds);
    }

    [TestMethod]
    public async Task PlaylistWorkspaceDeleteSelectedEntries_IgnoresEmptyAndNonPlaylistRows()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);

        await workspace.DeleteSelectedEntriesAsync([]);
        await workspace.DeleteSelectedEntriesAsync([null!, new object()]);
    }

    [TestMethod]
    public async Task PlaylistWorkspaceSpecialFolderMutationIsIgnoredWithoutPersistenceAccess()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        var table = new BMSTable();
        PlaylistFolderNode specialFolder = PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned);

        await workspace.RenameFolderAsync(table, specialFolder, "Renamed");
        await workspace.PlaylistRemovalWorkflow.RemoveFolderAsync(table, specialFolder);
        await workspace.AddRowsToFolderAsync([], table, specialFolder);
    }

    private static string[] CreateSongTableRow(string? md5, string path)
    {
        string[] values = new string[30];
        if (md5 != null)
        {
            values[0] = md5;
        }
        values[1] = "Installed chart";
        values[3] = "Artist";
        values[7] = path;
        return values;
    }
}
