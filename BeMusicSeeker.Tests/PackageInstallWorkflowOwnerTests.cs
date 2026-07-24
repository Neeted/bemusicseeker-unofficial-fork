using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PackageInstallWorkflowOwnerTests
{
    [TestMethod]
    public void Enqueue_PublishesCompletionAfterLiveInstallReturnsAndContinuesAfterFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new BMSLibrary(songDbPath, null, null, string.Empty);
            var calls = new List<string>();
            var failures = new List<PackageInstallFailure>();
            var eventOrder = new List<string>();
            var completed = new ManualResetEventSlim(false);
            var secondFinished = new ManualResetEventSlim(false);
            PackageInstallWorkflowOwner? owner = null;
            owner = CreateOwner(
                (current, paths, token, onPath, onArchive) =>
                {
                    string displayName = Path.GetFileName(paths.FirstOrDefault() ?? string.Empty);
                    lock (calls)
                    {
                        calls.Add(displayName);
                    }
                    if (displayName == "first.zip")
                    {
                        throw new InvalidOperationException("first failed");
                    }
                    secondFinished.Set();
                    return [new ChartPackage()];
                },
                action => action());
            owner.StatusChanged += snapshot =>
            {
                if (!snapshot.IsActive)
                {
                    lock (eventOrder)
                    {
                        eventOrder.Add("inactive");
                    }
                }
            };
            owner.FailurePublished += failure => failures.Add(failure);
            owner.CompletionPublished += _ =>
            {
                lock (eventOrder)
                {
                    eventOrder.Add("completion");
                }
                completed.Set();
            };
            owner.AttachLibrary(library);
            lock (eventOrder)
            {
                eventOrder.Clear();
            }
            owner.Enqueue([Path.Combine(root, "first.zip")]);
            owner.Enqueue([Path.Combine(root, "second.zip")]);

            Assert.IsTrue(secondFinished.Wait(5000), "The following batch did not run after failure.");
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000), "The workflow did not become idle.");
            Assert.IsTrue(completed.IsSet, "The successful following batch must publish a completion receipt.");
            Assert.AreEqual(1, failures.Count);
            CollectionAssert.AreEqual(new[] { "first.zip", "second.zip" }, calls);
            CollectionAssert.AreEqual(new[] { "completion", "inactive" }, eventOrder);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void AttachLibrary_InvalidatesCompletionFromPreviousGeneration()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string firstDirectory = Path.Combine(root, "first");
        string secondDirectory = Path.Combine(root, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        string firstDb = Path.Combine(firstDirectory, "song.db");
        string secondDb = Path.Combine(secondDirectory, "song.db");
        File.WriteAllBytes(firstDb, []);
        File.WriteAllBytes(secondDb, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(firstDb))
            {
            }
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(secondDb))
            {
            }
            var first = new BMSLibrary(firstDb, null, null, string.Empty);
            var second = new BMSLibrary(secondDb, null, null, string.Empty);
            var started = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var completion = new ManualResetEventSlim(false);
            var owner = CreateOwner(
                (library, paths, token, onPath, onArchive) =>
                {
                    started.Set();
                    release.Wait(5000);
                    return [new ChartPackage()];
                },
                action => action());
            owner.CompletionPublished += _ => completion.Set();
            owner.AttachLibrary(first);
            owner.Enqueue([Path.Combine(root, "first.zip")]);
            Assert.IsTrue(started.Wait(5000), "The first generation did not start.");
            owner.AttachLibrary(second);
            release.Set();

            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000), "The stale workflow did not drain.");
            Assert.IsFalse(completion.IsSet, "A replaced library generation must not publish a receipt.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CancelAfterLiveApplyReturns_PublishesCompletionReceipt()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new BMSLibrary(songDbPath, null, null, string.Empty);
            var started = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var completion = new ManualResetEventSlim(false);
            var owner = CreateOwner(
                (current, paths, token, onPath, onArchive) =>
                {
                    started.Set();
                    Assert.IsTrue(release.Wait(5000), "The live apply delegate was not released.");
                    Assert.IsTrue(token.IsCancellationRequested, "The test must cancel while live apply is in progress.");
                    return [new ChartPackage()];
                },
                action => action());
            owner.CompletionPublished += receipt =>
            {
                Assert.AreEqual(1, receipt.Packages.Count);
                completion.Set();
            };
            owner.AttachLibrary(library);
            owner.Enqueue([Path.Combine(root, "cancelled-after-apply.zip")]);
            Assert.IsTrue(started.Wait(5000), "The install batch did not start.");
            owner.CancelAll();
            release.Set();

            Assert.IsTrue(completion.Wait(5000), "A completed live apply must still publish its receipt after cancellation.");
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000), "The workflow did not become idle.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void AttachLibrary_EnqueueAfterReplacementUsesNewGenerationQueue()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string firstDirectory = Path.Combine(root, "first");
        string secondDirectory = Path.Combine(root, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        string firstDb = Path.Combine(firstDirectory, "song.db");
        string secondDb = Path.Combine(secondDirectory, "song.db");
        File.WriteAllBytes(firstDb, []);
        File.WriteAllBytes(secondDb, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(firstDb))
            {
            }
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(secondDb))
            {
            }
            var first = new BMSLibrary(firstDb, null, null, string.Empty);
            var second = new BMSLibrary(secondDb, null, null, string.Empty);
            var firstStarted = new ManualResetEventSlim(false);
            var releaseFirst = new ManualResetEventSlim(false);
            var secondCompleted = new ManualResetEventSlim(false);
            var calls = new List<string>();
            var completions = 0;
            var owner = CreateOwner(
                (library, paths, token, onPath, onArchive) =>
                {
                    string displayName = Path.GetFileName(paths.First());
                    lock (calls)
                    {
                        calls.Add(displayName);
                    }
                    if (ReferenceEquals(library, first))
                    {
                        firstStarted.Set();
                        Assert.IsTrue(releaseFirst.Wait(5000), "The replaced generation did not drain.");
                        return [new ChartPackage()];
                    }
                    secondCompleted.Set();
                    return [new ChartPackage()];
                },
                action => action());
            owner.CompletionPublished += _ => Interlocked.Increment(ref completions);
            owner.AttachLibrary(first);
            owner.Enqueue([Path.Combine(root, "first-generation.zip")]);
            Assert.IsTrue(firstStarted.Wait(5000), "The first generation did not start.");

            owner.AttachLibrary(second);
            owner.Enqueue([Path.Combine(root, "second-generation.zip")]);
            // The production owner serializes library mutations through the shared
            // operation gate. Release the retired generation before waiting for
            // the replacement generation to enter that same corridor.
            releaseFirst.Set();
            Assert.IsTrue(secondCompleted.Wait(5000), "The new generation request was lost during replacement.");

            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000), "The replaced workflow did not drain.");
            CollectionAssert.AreEqual(new[] { "first-generation.zip", "second-generation.zip" }, calls);
            Assert.AreEqual(1, completions, "Only the current generation may publish a completion receipt.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void RequestShutdown_PreventsLaterEnqueueFromStartingLiveMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new BMSLibrary(songDbPath, null, null, string.Empty);
            int mutationCalls = 0;
            var owner = CreateOwner(
                (current, paths, token, onPath, onArchive) =>
                {
                    Interlocked.Increment(ref mutationCalls);
                    return [new ChartPackage()];
                },
                action => action());
            owner.AttachLibrary(library);
            owner.RequestShutdown();
            owner.Enqueue([Path.Combine(root, "after-shutdown.zip")]);

            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000), "Shutdown must drain all queue contexts.");
            Assert.AreEqual(0, mutationCalls, "A request submitted after shutdown must not enter live mutation.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void NotificationFailure_DoesNotStopQueueLifecycle()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new BMSLibrary(songDbPath, null, null, string.Empty);
            int mutationCalls = 0;
            var secondFinished = new ManualResetEventSlim(false);
            var owner = CreateOwner(
                (current, paths, token, onPath, onArchive) =>
                {
                    int call = Interlocked.Increment(ref mutationCalls);
                    if (call == 2)
                    {
                        secondFinished.Set();
                    }
                    return [new ChartPackage()];
                },
                action => action());
            owner.AttachLibrary(library);
            owner.StatusChanged += _ => throw new InvalidOperationException("status publication failed");
            owner.CompletionPublished += _ => throw new InvalidOperationException("completion publication failed");
            owner.Enqueue([Path.Combine(root, "first.zip")]);
            owner.Enqueue([Path.Combine(root, "second.zip")]);

            Assert.IsTrue(secondFinished.Wait(5000), "A notification exception must not stop the following batch.");
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000), "The queue must remain drainable after notification failure.");
            Assert.AreEqual(2, mutationCalls);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static PackageInstallWorkflowOwner CreateOwner(
        Func<BMSLibrary, IEnumerable<string>, CancellationToken, Action, Action<string, int, int>, IReadOnlyList<ChartPackage>> installBatch,
        Action<Action> dispatchToUi,
        Action<Exception>? reportNotificationFailure = null)
    {
        return new PackageInstallWorkflowOwner(
            new ChartFileOperationSynchronizer(),
            new ChartMutationActivityOwner(),
            new DelegatePackageInstallMutationPort(installBatch),
            dispatchToUi,
            reportNotificationFailure);
    }
}

internal sealed class DelegatePackageInstallMutationPort : IPackageInstallMutationPort
{
    private readonly Func<BMSLibrary, IEnumerable<string>, CancellationToken, Action, Action<string, int, int>, IReadOnlyList<ChartPackage>> install;

    internal DelegatePackageInstallMutationPort(
        Func<BMSLibrary, IEnumerable<string>, CancellationToken, Action, Action<string, int, int>, IReadOnlyList<ChartPackage>> install)
    {
        this.install = install ?? throw new ArgumentNullException(nameof(install));
    }

    public IReadOnlyList<ChartPackage> Install(
        BMSLibrary library,
        IEnumerable<string> installPaths,
        CancellationToken token,
        Action onEachPathProcessed,
        Action<string, int, int> onEachArchiveExtractStarted)
    {
        return install(library, installPaths, token, onEachPathProcessed, onEachArchiveExtractStarted);
    }
}
