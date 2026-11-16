using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

/// <summary>実LR2 ownerの終了receiptと本体・付加資源の失敗を媒体保全へ接続します。</summary>
[TestClass]
[TestCategory("Windows.Settings")]
[DoNotParallelize]
public sealed class Lr2SettingsOwnerFixtureLifetimeTests
{
    /// <summary>本体失敗だけなら通常回収し、実DB close失敗と付加資源解放失敗では両原因と媒体を保全します。</summary>
    /// <remarks>実ドライブの正常回収は旧EnvironmentIntegrationが分担し、ここでは記録用資源の解放回数と所有診断を観測します。</remarks>
    [TestMethod]
    [DataRow("body")]
    [DataRow("shutdown")]
    [DataRow("resource")]
    public void BodyAndCleanupFailuresKeepCausesAndRespectMediaLifetime(string condition)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var bodyFailure = new IOException("body failure");
        var cleanupFailure = new IOException("cleanup failure");
        var resource = new RecordingResource(condition == "resource" ? cleanupFailure : null);
        MainWindowViewModel? owner = null;
        ApplicationComposition? ownedComposition = null;
        string? root = null;
        string[] media = [];
        bool consumersEnded = false;
        try
        {
            Exception observed = Assert.Throws<Exception>(() => Lr2SettingsOwnerTestFixture.WithInitializedLr2Owner(workspace, null,
                (path, values, composition, shell, _, _, _) =>
                {
                    root = path;
                    owner = shell;
                    ownedComposition = composition;
                    media = [values.TableListURL.LocalPath, values.LR2SongDBPath, Path.Combine(path, "settings", "user.config")];
                    foreach (string file in media) { Assert.IsTrue(File.Exists(file), file); }
                    if (condition == "shutdown")
                    {
                        ApplicationDatabaseScope.ConnectionLease lease = composition.DatabaseScope.AcquireConnectionLease();
                        Assert.AreSame(cleanupFailure, Assert.ThrowsExactly<IOException>(() => lease.Close(() => throw cleanupFailure)));
                    }
                    throw bodyFailure;
                }, ownedResource: _ => resource));
            Assert.IsNotNull(root);
            Assert.IsNotNull(owner);
            Assert.IsNotNull(ownedComposition);
            if (condition == "body") { Assert.AreSame(bodyFailure, observed); }
            else
            {
                Assert.IsInstanceOfType<AggregateException>(observed);
                var aggregate = (AggregateException)observed;
                Assert.AreSame(bodyFailure, aggregate.InnerExceptions[0]);
                Assert.AreEqual(2, aggregate.InnerExceptions.Count);
                if (condition == "resource") { Assert.AreSame(cleanupFailure, aggregate.InnerExceptions[1]); }
                else { Assert.IsInstanceOfType<IOException>(aggregate.InnerExceptions[1]); }
                Exception diagnosticOwner = condition == "resource" ? aggregate.InnerExceptions[1] : aggregate;
                string diagnostic = diagnosticOwner.Data["Lr2OwnerPreservedArtifacts"] as string ?? throw new AssertFailedException("保全診断がありません。");
                StringAssert.Contains(diagnostic, root);
                StringAssert.Contains(diagnostic, resource.ToString());
                StringAssert.Contains(diagnostic, "reason=");
            }
            Assert.AreEqual(condition == "shutdown" ? 0 : 1, resource.DisposeCalls);
            workspace.Dispose();
            foreach (string file in media) { Assert.AreEqual(condition != "body", File.Exists(file), file); }
        }
        finally
        {
            if (owner != null && ownedComposition != null)
            {
                TestUiDispatcherHost.Invoke(() =>
                {
                    Task<ShellShutdownWorkflowCompletionReceipt> shutdown = owner.ShellShutdownWorkflow.RequestWindowCloseAsync();
                    Assert.IsTrue(shutdown.IsCompleted, "検査で開始した実終了Taskの終端を確認してから回収します。");
                    ShellShutdownWorkflowCompletionReceipt receipt = shutdown.GetAwaiter().GetResult();
                    Assert.AreEqual(condition != "shutdown", receipt.PreparationSucceeded);
                    Assert.AreEqual(0, ownedComposition.DatabaseScope.ActiveConnectionCount);
                    if (condition == "shutdown")
                    {
                        Assert.AreEqual(1, ownedComposition.DatabaseScope.SqliteCloseFailureCount);
                        owner.SettingDialog.Dispose();
                    }
                    consumersEnded = true;
                });
            }
            if (consumersEnded)
            {
                // 検査が所有するrootであり、実Task・DBと設定購読の終端を上で確認済みです。
                if (Directory.Exists(workspace.RootDirectory)) { Directory.Delete(workspace.RootDirectory, recursive: true); }
            }
            else { workspace.PreserveArtifacts(); }
            workspace.Dispose();
        }
    }

    private sealed class RecordingResource(Exception? failure) : IDisposable
    {
        /// <summary>付加資源の解放を試みた回数です。</summary>
        internal int DisposeCalls { get; private set; }
        /// <summary>一度の解放試行を記録し、指定した元原因で故障させます。</summary>
        public void Dispose()
        {
            DisposeCalls++;
            if (failure != null) { throw failure; }
        }
        /// <summary>保全診断でケース所有の資源を識別します。</summary>
        public override string ToString() => "case-owned-recording-resource";
    }
}
