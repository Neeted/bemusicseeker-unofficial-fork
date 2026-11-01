using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;


/// <summary>外部表取込みの実受付・登録・通知とsettingsへのcatalog配送を確認します。</summary>
/// <remarks>通信中の受付と実DB・BMT・configの一括反映はplaylistとworkspaceの実接続が対象です。個別JSON・BMT内容は下位parser/exportケース、実Window配送はMainWindowケースへ分担します。</remarks>
[TestClass]
public sealed class PlaylistWorkspaceExternalSourceTests
{
    // MSTestが各ケースの実行前に必ず注入します。
    public TestContext TestContext { get; set; } = null!;

    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>表示だけの変更はcatalog通知を出さず、catalog更新はqueue経由で届け、設定非表示中の更新を再表示時に一回だけ反映します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void SettingsWorkspacePort_CatalogVersionDefersSettingsFanoutUntilDialogIsVisible()
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string databasePath = Path.Combine(
            migrationWorkspace.RootDirectory,
            "BeMusicSeekerTests",
            Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
            ApplicationDatabaseScope databaseScope = MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope;
            PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
            var catalogNotificationQueue = new Queue<Action>();
            workspace.ConfigureCatalogNotificationQueue(catalogNotificationQueue.Enqueue);
            using SettingsDialogViewModel dialog = viewModel.SettingDialog;
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
            var playlist = new TestBmsPlaylist(databaseScope, databasePath)
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

}
