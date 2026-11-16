using System.Collections.ObjectModel;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryStateApplierTestSupport;
using PackageStateMutationApplier = BeMusicSeeker.Models.BmsLibraryInternal.PackageLifecycleOwner.PackageStateMutationApplier;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryCatalogRelocationTests
{
    /// <summary>
    /// BMS・bmsonとフォルダの移転を実DBへ保存し、導入済み項目と導入先を同じ新パスへ反映して公開前の通知を抑止する。
    /// </summary>
    /// <remarks>本番のDB確定と現在値・パッケージ公開を接続し、移転結果の保存と公開を確認する。</remarks>
    [TestMethod]
    public void ApplyCatalogRelocation_UpdatesStorageRowsAndInstalledPackages()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var caseWorkspace1 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            string tempRootPath = Path.Combine(caseWorkspace1.RootDirectory, "BeMusicSeeker_StateApplier_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "OldFolder");
            string newDirectoryPath = Path.Combine(tempRootPath, "NewFolder");
            Directory.CreateDirectory(oldDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            string oldChartPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newChartPath = Path.Combine(newDirectoryPath, "chart.bms");
            string oldBmsonPath = Path.Combine(oldDirectoryPath, "chart.bmson");
            string newBmsonPath = Path.Combine(newDirectoryPath, "chart.bmson");
            File.WriteAllText(oldChartPath, "#PLAYER 1");
            File.WriteAllText(newChartPath, "#PLAYER 1");
            File.WriteAllText(Path.Combine(newDirectoryPath, "readme.txt"), "text group");
            File.WriteAllText(oldBmsonPath, "{}");
            File.WriteAllText(newBmsonPath, "{}");
            try
            {
                ChartFile movedFile = ChartTestValues.Empty() with
                {
                    Path = newChartPath
                };
                movedFile = movedFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                ChartFile installLinkedFile = ChartTestValues.Empty() with
                {
                    Path = Path.Combine(tempRootPath, "pending_chart.bms")
                };
                ChartPackage installedPackage = ChartPackageTestExtensions.CreatePackage([movedFile]);
                installedPackage.path = oldDirectoryPath;
                installedPackage.delete_parent = false;
                using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
                {
                    songDb.RunInTransaction(() =>
                    {
                        songDb.CreateTable<LR2SongDB.song>();
                        songDb.CreateTable<LR2SongDB.folder>();
                        songDb.CreateTable<LR2SongDBExtended.maintenance>();
                        songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                        ChartFile oldRow = ChartTestValues.Empty() with
                        {
                            Path = oldChartPath
                        };
                        oldRow = oldRow with { Md5 = movedFile.Md5, Txt = 0 };
                        songDb.InsertOrReplace(ChartTestValues.CreateBmsStorageRow(oldRow, "stale-parent"), typeof(LR2SongDB.song));
                        songDb.InsertOrReplace(new LR2SongDB.folder
                        {
                            path = oldDirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                            title = "OldFolder",
                            parent = "stale-parent",
                            type = 1
                        }, typeof(LR2SongDB.folder));
                        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(ChartTestValues.Empty(ChartFileKind.Bmson) with
                        {
                            Token = new OwnedChartToken(),
                            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                            Path = oldBmsonPath,
                            Folder = oldDirectoryPath
                        }), typeof(LR2SongDBExtended.bmson_song));
                    });
                }

                List<ChartFile> libraryFiles = [movedFile];
                List<ChartFile> bmsonSongs =
                [
                    ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken(),
                        Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                        Path = oldBmsonPath,
                        Folder = oldDirectoryPath
                    }
                ];
                ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
                ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([installedPackage]);
                var callbacks = new TrackingCallbacks();
                PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);
                var folderPathChanges = new List<LibraryFolderPathChange>();
                folderPathChanges.Add(new LibraryFolderPathChange
                {
                    OldFolderPath = oldDirectoryPath,
                    NewFolderPath = newDirectoryPath
                });
                var pathChanges = new List<LibraryChartPathChange>();
                pathChanges.Add(new LibraryChartPathChange
                {
                    Chart = (movedFile),
                    OldPath = oldChartPath,
                    NewPath = newChartPath
                });
                pathChanges.Add(new LibraryChartPathChange
                {
                    Chart = (bmsonSongs[0]),
                    OldPath = oldBmsonPath,
                    NewPath = newBmsonPath
                });
                var installDestinationChanges = new List<LibraryInstallDestinationChange>();
                installDestinationChanges.Add(new LibraryInstallDestinationChange
                {
                    Chart = ChartFileProjection.WithPackageState(
                        (installLinkedFile),
                        oldDirectoryPath,
                        "Old destination title",
                        "Old destination artist",
                        [Path.Combine(tempRootPath, "Candidate")],
                        []),
                    NewInstallDestination = newDirectoryPath
                });
                var packagePathChanges = new List<LibraryInstalledPackagePathChange>();
                packagePathChanges.Add(new LibraryInstalledPackagePathChange
                {
                    Package = installedPackage,
                    NewPath = newDirectoryPath
                });

                LibraryCatalogMutationFacts catalogFacts = new([], pathChanges, folderPathChanges);
                LibraryPackageReferenceFacts packageFacts = new(installDestinationChanges, packagePathChanges);
                CatalogMutationReceipt relocationReceipt = ApplyCatalogRelocation(songDbPath, catalogFacts, callbacks, out CatalogOwnedCollectionOwner currentOwner);
                Assert.IsTrue(relocationReceipt.Applied);
                Assert.AreEqual(2, relocationReceipt.PathFacts.Count);
                PackageChartEntry installedEntry = installedPackage.ChartEntries.Single();
                var publishedProperties = new List<string?>();
                System.ComponentModel.PropertyChangedEventHandler published = (_, args) => publishedProperties.Add(args.PropertyName);
                installedPackage.PropertyChanged += published;
                try
                {
                    BmsLibraryStateApplyResult packageResult = applier.ApplyPackageReferenceFacts(packageFacts);
                    Assert.AreEqual(0, publishedProperties.Count, "適用中にはパスとheaderを公開しません。");
                    Assert.AreEqual(0, callbacks.InstalledPackagesChangedCount, "純移転は所属集合を変更しません。");
                    packageResult.PublishPackagePaths();
                    CollectionAssert.AreEqual(new[] { nameof(ChartPackage.path), nameof(ChartPackage.DisplayTitle) }, publishedProperties);
                }
                finally
                {
                    installedPackage.PropertyChanged -= published;
                }
                Assert.AreSame(installedEntry, installedPackage.ChartEntries.Single());
                Assert.AreEqual(newChartPath, installedEntry.Chart.Path);

                Assert.AreEqual(newChartPath, movedFile.Path);
                Assert.IsNull(movedFile.Txt);
                Assert.AreEqual(1, currentOwner.BmsRows.Single().Txt);
                ChartFile appliedInstallDestinationChart = packageFacts.CreateAppliedInstallDestinationChartSnapshots().Single();
                Assert.AreSame(installLinkedFile.Token, appliedInstallDestinationChart.Token);
                Assert.AreEqual(newDirectoryPath, appliedInstallDestinationChart.InstallDestination);
                Assert.AreEqual("Old destination title", appliedInstallDestinationChart.InstallDestinationTitle);
                Assert.AreEqual("Old destination artist", appliedInstallDestinationChart.InstallDestinationArtist);
                CollectionAssert.AreEqual(new[] { Path.Combine(tempRootPath, "Candidate") }, appliedInstallDestinationChart.InstallDestinationSuggestions.ToArray());
                Assert.AreEqual(newDirectoryPath, installedPackage.path);
                Assert.AreEqual(0, callbacks.InstalledPackagesChangedCount);
                Assert.AreEqual(oldBmsonPath, bmsonSongs[0].Path);
                Assert.AreEqual(newBmsonPath, currentOwner.BmsonRows.Single().Path);
                using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
                Assert.IsTrue(verifySongDb.Table<LR2SongDB.song>().Any(file => file.path == newChartPath));
                Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT txt FROM song WHERE path = ?;", newChartPath));
                Assert.IsFalse(verifySongDb.Table<LR2SongDB.song>().Any(file => file.path == oldChartPath));
                LR2SongDB.folder movedFolder = verifySongDb.Table<LR2SongDB.folder>().Single(folder => folder.path == newDirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                LR2SongDB.song movedSong = verifySongDb.Query<LR2SongDB.song>("SELECT * FROM song WHERE folder = ?;", Lr2StoredPathReference.Compute(movedFolder.path)).Single();
                Assert.AreEqual(newChartPath, movedSong.path);
                Assert.AreEqual(movedFolder.parent, movedSong.parent);
                Assert.IsTrue(verifySongDb.Table<LR2SongDBExtended.bmson_song>().Any(song => song.path == newBmsonPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

}
