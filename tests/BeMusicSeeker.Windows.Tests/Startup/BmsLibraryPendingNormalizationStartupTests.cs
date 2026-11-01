using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed partial class BmsLibraryInitializationInstallTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();
    /// <summary>明示Resetはinstall全行だけを一括削除します。正常・alias・不存在・読取不能と実管理入力を含め、元bytes・導入済曲・他表・導入済一覧を保ちます。最後のDELETE後abortでも全旧行と既存一覧を維持します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ResetPendingPackages_ChangesOnlyInstallAfterAtomicSuccess(bool abortLastDelete)
    {
        WithTemporaryLr2SongDb((root, database) =>
        {
            string managed = TempDirectoryPublisher.Get("pending-restore-reset-model");
            try
            {
                string borrowed = Path.Combine(root, "Borrowed");
                Directory.CreateDirectory(borrowed);
                string chart = Path.Combine(borrowed, "locked.bms");
                File.WriteAllText(chart, CreateValidBmsText("Borrowed"), Encoding.ASCII);
                File.WriteAllText(Path.Combine(managed, "managed.bms"), CreateValidBmsText("Managed"), Encoding.ASCII);
                File.WriteAllText(Path.Combine(managed, "readme.txt"), "保持する同梱物", new UTF8Encoding(false));
                string installedChart = Path.Combine(root, "installed.bms");
                File.WriteAllText(installedChart, CreateValidBmsText("Installed"), Encoding.ASCII);
                string[] keys = [borrowed, Path.Combine(borrowed, "."), chart, Path.Combine(root, "Missing"), managed, "", "   "];
                ExecuteSongDbFixtureTransaction(databaseScope, database, db =>
                {
                    db.CreateTable<LR2SongDBExtended.install>();
                    db.CreateTable<LR2SongDB.song>();
                    db.InsertOrReplace(new LR2SongDB.song { path = installedChart, hash = "kept-owned" });
                    db.Execute("CREATE TABLE reset_keep (value TEXT)");
                    db.Execute("INSERT INTO reset_keep(value) VALUES (?)", "kept");
                    foreach (string key in keys) { db.Insert(new LR2SongDBExtended.install { path = key, delete_parent = key == managed }); }
                    if (abortLastDelete)
                    {
                        db.Execute("CREATE TRIGGER fail_reset AFTER DELETE ON install WHEN (SELECT COUNT(*) FROM install) = 0 BEGIN SELECT RAISE(ABORT, 'reset-install'); END;");
                    }
                });
                var paths = new List<string> { chart, installedChart, Path.Combine(managed, "managed.bms"), Path.Combine(managed, "readme.txt") };
                var bytes = paths.ToDictionary(path => path, File.ReadAllBytes);
                var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), database);
                int publications = 0;
                int? rowsAtPublication = null;
                var owner = new PackageLifecycleOwner(gateway, new TestUiScheduler(() => null), property =>
                {
                    if (property == "ChartPackagesPending")
                    {
                        publications++;
                        rowsAtPublication = gateway.LoadInstallPackages().Count;
                    }
                }, packages => new ObservableCollection<ChartPackage>(packages ?? []), () => { }, _ => { });
                var pending = new ChartPackage { path = borrowed };
                var installed = new ChartPackage { path = Path.GetDirectoryName(installedChart) };
                owner.ReplacePendingPackages([pending]);
                owner.ReplaceInstalledPackages([installed]);
                object previous = owner.PendingPackages;
                object previousInstalled = owner.InstalledPackages;
                publications = 0;
                rowsAtPublication = null;
                using (var held = new FileStream(chart, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    if (abortLastDelete)
                    {
                        PendingPackageRestoreException failure = Assert.ThrowsExactly<PendingPackageRestoreException>(() => owner.ResetPendingPackagesWithoutPublishing(gateway));
                        Exception cause = failure.InnerException ?? throw new AssertFailedException("元のDB原因がありません。");
                        Assert.IsInstanceOfType<SQLite.SQLiteException>(cause);
                        StringAssert.Contains(cause.Message, "reset-install");
                        Assert.AreSame(previous, owner.PendingPackages);
                        Assert.AreEqual(0, publications);
                        Assert.IsNull(rowsAtPublication);
                        CollectionAssert.AreEquivalent(keys, gateway.LoadInstallPackages().Select(row => row.path).ToArray());
                        Assert.IsTrue(gateway.LoadInstallPackages().Single(row => row.path == managed).delete_parent);
                    }
                    else
                    {
                        Action publication = owner.ResetPendingPackagesWithoutPublishing(gateway);
                        Assert.AreEqual(0, publications);
                        publication();
                        Assert.AreEqual(0, owner.PendingPackages.Count);
                        Assert.AreEqual(1, publications);
                        Assert.AreEqual(0, rowsAtPublication);
                        Assert.AreEqual(0, gateway.LoadInstallPackages().Count);
                    }
                    Assert.AreSame(previousInstalled, owner.InstalledPackages);
                    Assert.AreSame(installed, owner.InstalledPackages.Single());
                    using LR2SongDBExtended verify = gateway.OpenSongDbReadOnly();
                    Assert.IsNotNull(verify.Find<LR2SongDB.song>(installedChart));
                    Assert.AreEqual("kept", verify.ExecuteScalar<string>("SELECT value FROM reset_keep"));
                }
                foreach ((string path, byte[] value) in bytes) { CollectionAssert.AreEqual(value, File.ReadAllBytes(path)); }
                Assert.IsTrue(Directory.Exists(managed));
            }
            finally { Assert.IsTrue(TempDirectoryPublisher.TryDeleteManagedPath(managed), "このtestが作った管理rootだけを通常寿命の終端で回収します。"); }
        });
    }
}
