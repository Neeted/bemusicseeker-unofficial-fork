using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class TempDirectoryPublisherTests
{
    private const string MarkerName = ".bemusicseeker-temp-session";
    private const string KnownMarker = "BeMusicSeeker managed temporary session v2";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>並列の初回取得でも各返却時に生存leaseが成立し、同一セッション内に独立した領域を作ることを確認する。</summary>
    /// <remarks>所有者を直接分離して実FSと排他Streamを観測する。異なるプロセスと強制終了後の回収はプロセス試験が分担する。</remarks>
    [TestMethod]
    public async Task ConcurrentFirstGetsReturnDistinctDirectoriesWithCompletedSessionMarker()
    {
        await using var scope = new SessionScope();
        TemporaryDirectorySession owner = scope.CreateOwner();
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        var workers = new List<Task<string>>();
        try
        {
            for (int i = 0; i < 2; i++)
            {
                workers.Add(Task.Factory.StartNew(() =>
                {
                    ready.Signal();
                    Assert.IsTrue(start.Wait(Timeout), "並列取得の開始通知がありません。");
                    string path = owner.Get("parallel-first-get");
                    Assert.IsTrue(Directory.Exists(path));
                    Assert.IsTrue(owner.IsManagedPath(path));
                    AssertLeaseHeld(Path.GetDirectoryName(path) ?? throw new AssertFailedException());
                    return path;
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
            }
            Assert.IsTrue(ready.Wait(Timeout), "並列取得workerが準備されません。");
            start.Set();
            string[] paths = await Task.WhenAll(workers).WaitAsync(Timeout);
            Assert.AreEqual(2, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.AreEqual(Path.GetDirectoryName(paths[0]), Path.GetDirectoryName(paths[1]));
        }
        finally
        {
            start.Set();
            await Task.WhenAll(workers).WaitAsync(Timeout);
        }
    }

    /// <summary>所有scopeにGUIDセッションを作り、実在と所有境界を確認する。地域依存の日時・整数書式は管理名に持ち込まない。</summary>
    [TestMethod]
    public async Task GetCreatesManagedDirectory()
    {
        await using var scope = new SessionScope();
        TemporaryDirectorySession owner = scope.CreateOwner();
        string path = owner.Get("unit-test");
        string session = Path.GetDirectoryName(path) ?? throw new AssertFailedException();
        string sessionName = Path.GetFileName(session);
        Assert.IsTrue(sessionName.StartsWith("session-", StringComparison.Ordinal));
        Assert.IsTrue(Guid.TryParseExact(sessionName[8..], "N", out _));
        Assert.AreEqual(scope.Root, Path.GetDirectoryName(session));
        Assert.IsTrue(Directory.Exists(path));
        Assert.IsTrue(owner.IsManagedPath(path));
    }

    /// <summary>自セッション内の実ファイルとleafだけを削除し、別ownerのleaf・scope直下の利用者データ・markerなしのsession風フォルダを保持する。</summary>
    [TestMethod]
    public async Task TryDeleteManagedPathDeletesOnlyManagedPaths()
    {
        await using var scope = new SessionScope();
        TemporaryDirectorySession owner = scope.CreateOwner();
        TemporaryDirectorySession otherOwner = scope.CreateOwner();
        string leaf = owner.Get("delete");
        string file = Path.Combine(leaf, "archive.zip");
        File.WriteAllText(file, "temporary archive");
        string otherLeaf = otherOwner.Get("other");
        string manualFile = Path.Combine(scope.Root, "manual.zip");
        File.WriteAllText(manualFile, "user owned");
        string manualSession = Path.Combine(scope.Root, "session-manual");
        Directory.CreateDirectory(manualSession);
        string manualArchive = Path.Combine(manualSession, "archive.zip");
        File.WriteAllText(manualArchive, "user owned archive");

        Assert.IsTrue(owner.TryDeleteManagedPath(file));
        Assert.IsTrue(owner.TryDeleteManagedPath(leaf));
        Assert.IsFalse(File.Exists(file));
        Assert.IsFalse(Directory.Exists(leaf));
        foreach (string unmanaged in new[] { otherLeaf, manualFile, manualSession })
        {
            Assert.IsFalse(owner.IsManagedPath(unmanaged));
            Assert.IsFalse(owner.TryDeleteManagedPath(unmanaged));
        }
        Assert.IsTrue(Directory.Exists(otherLeaf));
        Assert.AreEqual("user owned", File.ReadAllText(manualFile));
        Assert.AreEqual("user owned archive", File.ReadAllText(manualArchive));
    }

    /// <summary>回収は排他取得できる既知v2だけを削除し、生存owner・未知版・旧marker・markerなし・通常ファイルの内容を保持する。</summary>
    [TestMethod]
    public async Task CleanupStaleDirectoriesDeletesOnlyKnownInactiveSessions()
    {
        await using var scope = new SessionScope();
        TemporaryDirectorySession live = scope.CreateOwner();
        TemporaryDirectorySession cleaner = scope.CreateOwner();
        string liveLeaf = live.Get("live");
        string liveFile = Path.Combine(liveLeaf, "archive.zip");
        File.WriteAllText(liveFile, "live archive");
        string stale = scope.CreateMarkedSession(KnownMarker);
        string unknown = scope.CreateMarkedSession("BeMusicSeeker managed temporary session v3");
        string legacy = scope.CreateMarkedSession("BeMusicSeeker managed temporary session");
        string unmarked = Path.Combine(scope.Root, "session-manual");
        Directory.CreateDirectory(unmarked);
        string manual = Path.Combine(scope.Root, "manual.zip");
        File.WriteAllText(manual, "manual archive");
        var warnings = new List<Exception>();

        await cleaner.StartCleanupStaleDirectoriesAsync(logWarning: (path, error) => warnings.Add(new IOException(path, error))).WaitAsync(Timeout);

        Assert.AreEqual(0, warnings.Count);
        Assert.IsFalse(Directory.Exists(stale));
        Assert.AreEqual("live archive", File.ReadAllText(liveFile));
        AssertLeaseHeld(Path.GetDirectoryName(liveLeaf) ?? throw new AssertFailedException());
        Assert.AreEqual("BeMusicSeeker managed temporary session v3", File.ReadAllText(Path.Combine(unknown, MarkerName)));
        Assert.AreEqual("BeMusicSeeker managed temporary session", File.ReadAllText(Path.Combine(legacy, MarkerName)));
        Assert.IsTrue(Directory.Exists(unmarked));
        Assert.AreEqual("manual archive", File.ReadAllText(manual));
    }

    /// <summary>終了開始で新Getを止め、開始済み回収の実終端までは自leaseとleafを保持し、解放後に削除する。再終了は同じTaskで再起動しない。</summary>
    [TestMethod]
    public async Task RemoveAllStopsGetsAndDrainsCleanupBeforeReleasingLease()
    {
        await using var scope = new SessionScope();
        TemporaryDirectorySession owner = scope.CreateOwner();
        string leaf = owner.Get("terminal");
        string session = Path.GetDirectoryName(leaf) ?? throw new AssertFailedException();
        scope.CreateMarkedSession(KnownMarker);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var warnings = new List<Exception>();
        Task cleanup = owner.StartCleanupStaleDirectoriesAsync(_ =>
        {
            entered.SetResult();
            if (!release.Wait(Timeout)) { throw new TimeoutException("回収終端の解放通知がありません。"); }
        }, (path, error) => warnings.Add(new IOException(path, error)));
        Task? terminal = null;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            terminal = owner.RemoveAllAsync((path, error) => warnings.Add(new IOException(path, error)));
            Assert.AreSame(terminal, owner.RemoveAllAsync());
            Assert.ThrowsExactly<ObjectDisposedException>(() => owner.Get("after-stop"));
            Assert.IsFalse(terminal.IsCompleted);
            Assert.IsFalse(cleanup.IsCompleted);
            Assert.IsTrue(Directory.Exists(leaf));
            AssertLeaseHeld(session);
        }
        finally
        {
            release.Set();
            await cleanup.WaitAsync(Timeout);
            if (terminal != null) { await terminal.WaitAsync(Timeout); }
        }
        Assert.AreEqual(0, warnings.Count);
        Assert.IsFalse(Directory.Exists(session));
        Assert.ThrowsExactly<ObjectDisposedException>(() => owner.Get("after-complete"));
        Assert.AreSame(terminal, owner.RemoveAllAsync());
    }

    private static void AssertLeaseHeld(string session)
    {
        Assert.ThrowsExactly<IOException>(() =>
        {
            using var marker = new FileStream(Path.Combine(session, MarkerName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
    }

    private sealed class SessionScope : IAsyncDisposable
    {
        private readonly List<TemporaryDirectorySession> owners = [];
        private readonly TestWorkspace workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        internal string Root => workspace.RootDirectory;

        internal TemporaryDirectorySession CreateOwner()
        {
            var owner = new TemporaryDirectorySession(Root);
            owners.Add(owner);
            return owner;
        }

        internal string CreateMarkedSession(string content)
        {
            string path = Path.Combine(Root, "session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, MarkerName), content);
            File.WriteAllText(Path.Combine(path, "archive.zip"), "stale archive");
            return path;
        }

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();
            foreach (TemporaryDirectorySession owner in owners)
            {
                try { await owner.RemoveAllAsync((path, error) => failures.Add(new IOException("所有セッションの回収失敗: " + path, error))).WaitAsync(Timeout); }
                catch (Exception ex) { failures.Add(ex); }
            }
            try { workspace.Dispose(); }
            catch (Exception ex) { failures.Add(ex); }
            if (failures.Count != 0) { throw new AggregateException(failures); }
        }
    }
}
