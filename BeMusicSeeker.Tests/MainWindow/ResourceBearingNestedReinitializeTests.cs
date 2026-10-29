using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ResourceBearingNestedReinitializeTests
{
    /// <summary>実VMの同一起動中の再初期化で、修正済み入力を新しい元packageとして再捕捉します。管理入力でも修正完了時点のpath・bytes・install行を保ち、成功導入は要求しません。</summary>
    /// <remarks>Windowは作らず実起動Task・scheduler終端・DBを使います。fixtureの終了準備だけを待ち、Application終端のRemoveAllは呼ばず、当該入力rootだけ回収します。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReinitializeLibraryAsync_ResourceBearingNestedChartsAreReevaluatedWithoutConsumingInput(bool managed)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string source = managed ? TempDirectoryPublisher.Get("nested-reinitialize")
                : Path.Combine(Path.GetTempPath(), "nested-reinitialize-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(source);
            string child = Path.Combine(source, "child");
            Directory.CreateDirectory(child);
            string childChart = Path.Combine(child, "child.bms");
            File.WriteAllText(Path.Combine(source, "parent.bms"), "#TITLE Parent\n#BPM 120\n#WAV01 parent.wav\n");
            File.WriteAllText(childChart, "#TITLE Child\n#BPM 120\n#WAV01 child.wav\n");
            File.WriteAllBytes(Path.Combine(source, "parent.wav"), [1, 2]);
            File.WriteAllBytes(Path.Combine(child, "child.wav"), [3, 4]);
            try
            {
                using var fixture = new MainWindowViewModelStartupProgressTests.CompletionFixture();
                fixture.OptionalRelease.TrySetResult();
                ExecuteSongDbFixtureTransaction(fixture.Library.InitializationSongDbPath, db =>
                {
                    db.InsertOrReplace(new LR2SongDBExtended.install { path = source, delete_parent = false });
                });
                Task<bool> startup = fixture.Start();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(startup, "nested-reinitialize-startup");
                Assert.IsTrue(startup.GetAwaiter().GetResult());
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "nested-reinitialize-startup-background");
                ChartPackage original = fixture.Library.ChartPackagesPending.Single();
                Assert.IsTrue(original.HasResourceBearingNestedCharts);
                File.Move(childChart, childChart + "_");
                Assert.IsTrue(original.HasResourceBearingNestedCharts, "入力修正だけで元packageの捕捉条件を再評価しません。");
                Dictionary<string, byte[]> corrected = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
                string[] ownedBefore = fixture.Library.BmsCharts.Select(chart => chart.Path).ToArray();

                Task reinitialize = fixture.Owner.ReinitializeLibraryAsync();
                fixture.Track(reinitialize);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(reinitialize, "nested-reinitialize-request");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "nested-reinitialize-background");

                ChartPackage current = fixture.Library.ChartPackagesPending.Single();
                Assert.AreNotSame(original, current);
                Assert.AreEqual(source, current.path);
                Assert.IsFalse(current.HasResourceBearingNestedCharts);
                Assert.IsFalse(current.ChartEntries.Any(entry => entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBearingNestedCharts)));
                CollectionAssert.AreEquivalent(corrected.Keys.ToArray(), Directory.GetFiles(source, "*", SearchOption.AllDirectories));
                foreach ((string path, byte[] bytes) in corrected) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
                CollectionAssert.AreEquivalent(ownedBefore, fixture.Library.BmsCharts.Select(chart => chart.Path).ToArray());
                using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Library.InitializationSongDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(source, db.Table<LR2SongDBExtended.install>().Single().path);
                Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
            }
            finally
            {
                if (managed) { Assert.IsTrue(TempDirectoryPublisher.TryDeleteManagedPath(source)); }
                else { Directory.Delete(source, recursive: true); }
            }
        });
    }
}
