using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PackageInstallWorkflowOwnerTests
{
    [TestMethod]
    public void ProductionPackageInstallDispatcher_QueuesWithoutSynchronousUiWait()
    {
        string source = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string method = SourceTextTestHelper.ExtractMethodBody(
            source,
            "private bool TryDispatchPackageInstallUi(Action action)");

        StringAssert.Contains(method, "uiScheduler.Schedule(");
        Assert.IsFalse(method.Contains("uiScheduler.Invoke(", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("InvokeMainChartListPresentationAction(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ActiveProgressBurst_QueuesOneLatestStatusBeforeCompletionAndInactive()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageInstallWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        var notifications = new Queue<Action>();
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var published = new List<string>();
            var owner = CreateOwner(
                (current, paths, token, onPath, onArchive) =>
                {
                    for (int index = 1; index <= 20; index++)
                    {
                        onArchive("archive-" + index + ".zip", index, 20);
                        onPath();
                    }
                    return [new ChartPackage()];
                },
                action =>
                {
                    lock (notifications)
                    {
                        notifications.Enqueue(action);
                    }
                    return true;
                });
            owner.StatusChanged += snapshot => published.Add(
                snapshot.IsActive
                    ? "active:" + snapshot.CompletedPathCount
                    : "inactive");
            owner.CompletionPublished += _ => published.Add("completed");
            owner.AttachLibrary(library);
            DrainNotifications(notifications);
            published.Clear();

            owner.Enqueue(Enumerable.Range(1, 20).Select(index => "batch-" + index + ".zip"));

            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000));
            lock (notifications)
            {
                Assert.AreEqual(
                    3,
                    notifications.Count,
                    "One coalesced active status, completion, and inactive terminal status are expected.");
            }
            DrainNotifications(notifications);

            Assert.AreEqual(
                "active:20|completed|inactive",
                string.Join("|", published));
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
    public void GenerationReplacement_OldDrainCannotConsumeNewActiveStatus()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageInstallWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        string firstRoot = Path.Combine(root, "first");
        string secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        string firstDb = Path.Combine(firstRoot, "song.db");
        string secondDb = Path.Combine(secondRoot, "song.db");
        File.WriteAllBytes(firstDb, []);
        File.WriteAllBytes(secondDb, []);
        var notifications = new Queue<Action>();
        using var firstStarted = new ManualResetEventSlim(false);
        using var secondStarted = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        using var releaseSecond = new ManualResetEventSlim(false);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(firstDb))
            {
            }
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(secondDb))
            {
            }
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var second = new TestBmsLibrary(secondDb, null, null, string.Empty);
            var published = new List<string>();
            var owner = CreateOwner(
                (library, paths, token, onPath, onArchive) =>
                {
                    if (ReferenceEquals(library, first))
                    {
                        firstStarted.Set();
                        releaseFirst.Wait(5000);
                    }
                    else
                    {
                        secondStarted.Set();
                        releaseSecond.Wait(5000);
                    }
                    return [];
                },
                action =>
                {
                    lock (notifications)
                    {
                        notifications.Enqueue(action);
                    }
                    return true;
                });
            owner.StatusChanged += snapshot =>
                published.Add(snapshot.IsActive ? "active" : "inactive");
            owner.AttachLibrary(first);
            DrainNotifications(notifications);
            published.Clear();

            owner.Enqueue(["first.zip"]);
            Assert.IsTrue(firstStarted.Wait(5000));
            owner.AttachLibrary(second);
            owner.Enqueue(["second.zip"]);
            releaseFirst.Set();
            Assert.IsTrue(secondStarted.Wait(5000));
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                lock (notifications)
                {
                    return notifications.Count >= 3;
                }
            }, 5000));

            DrainNotifications(notifications);

            Assert.AreEqual(
                "inactive|active",
                string.Join("|", published),
                "The old drain must be stale; replacement inactive must precede the new generation's active status.");

            releaseFirst.Set();
            releaseSecond.Set();
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000));
            DrainNotifications(notifications);
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SameGenerationStatusSequence_DropsLateTerminalAndSegmentsRapidReentry()
    {
        var notifications = new Queue<Action>();
        var published = new List<string>();
        var owner = CreateOwner(
            (_, _, _, _, _) => [],
            action =>
            {
                notifications.Enqueue(action);
                return true;
            });
        owner.StatusChanged += snapshot =>
            published.Add(snapshot.IsActive ? "active" : "inactive");
        FieldInfo queuesField = typeof(PackageInstallWorkflowOwner).GetField(
            "queueProcessors",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object context = ((IList)queuesField.GetValue(owner)!)[0]!;
        MethodInfo publish = typeof(PackageInstallWorkflowOwner).GetMethod(
            "PublishQueueStatus",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        publish.Invoke(owner, [context, new DropInstallQueueStatusSnapshot { Sequence = 1, IsActive = true }]);
        publish.Invoke(owner, [context, new DropInstallQueueStatusSnapshot { Sequence = 3, IsActive = false }]);
        publish.Invoke(owner, [context, new DropInstallQueueStatusSnapshot { Sequence = 4, IsActive = true }]);
        publish.Invoke(owner, [context, new DropInstallQueueStatusSnapshot { Sequence = 2, IsActive = false }]);

        Assert.AreEqual(3, notifications.Count);
        DrainNotifications(notifications);

        Assert.AreEqual("active|inactive|active", string.Join("|", published));
    }

    [TestMethod]
    public void EnqueueBeforeLibraryAttach_ReportsDiagnosticFailure()
    {
        int diagnosticReports = 0;
        var owner = CreateOwner(
            (library, paths, token, onPath, onArchive) => throw new InvalidOperationException("library was not attached"),
            action =>
            {
                action();
                return true;
            },
            _ => Interlocked.Increment(ref diagnosticReports));

        owner.Enqueue(["before-attach.zip"]);

        Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000));
        Assert.AreEqual(1, diagnosticReports);
    }

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
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
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
                action =>
                {
                    action();
                    return true;
                });
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
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var second = new TestBmsLibrary(secondDb, null, null, string.Empty);
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
                action =>
                {
                    action();
                    return true;
                });
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
    public void AttachLibrary_RechecksGenerationBeforeMutationAfterGateWait()
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
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var second = new TestBmsLibrary(secondDb, null, null, string.Empty);
            var chartFileOperations = new ChartFileOperationSynchronizer();
            var chartMutationActivity = new ChartMutationActivityOwner();
            int mutationCalls = 0;
            int staleFailureReports = 0;
            int throwOnInactive = 0;
            chartMutationActivity.ActivityChanged += (_, _) =>
            {
                if (Volatile.Read(ref throwOnInactive) != 0 && !chartMutationActivity.IsActive)
                {
                    throw new InvalidOperationException("stale cleanup failed");
                }
            };
            var owner = new PackageInstallWorkflowOwner(
                chartFileOperations,
                chartMutationActivity,
                new DelegatePackageInstallMutationPort(
                    (library, paths, token, onPath, onArchive) =>
                    {
                        Interlocked.Increment(ref mutationCalls);
                        return [new ChartPackage()];
                    }),
                action =>
                {
                    Task.Run(action);
                    return true;
                },
                _ => Interlocked.Increment(ref staleFailureReports));
            owner.AttachLibrary(first);

            using (chartFileOperations.Enter())
            {
                owner.Enqueue([Path.Combine(root, "first-generation.zip")]);
                Assert.IsTrue(SpinWait.SpinUntil(() => chartMutationActivity.IsActive, 5000));
                Volatile.Write(ref throwOnInactive, 1);
                owner.AttachLibrary(second);
            }

            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000));
            Assert.AreEqual(0, mutationCalls);
            Assert.AreEqual(1, staleFailureReports);
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
    public void DelayedFailurePublication_ReportsDiagnosticsAfterGenerationChanges()
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
        var notifications = new Queue<Action>();
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(firstDb))
            {
            }
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(secondDb))
            {
            }
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var second = new TestBmsLibrary(secondDb, null, null, string.Empty);
            int failurePublished = 0;
            int diagnosticReports = 0;
            var owner = new PackageInstallWorkflowOwner(
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort(
                    (library, paths, token, onPath, onArchive) => throw new InvalidOperationException("install failed")),
                action =>
                {
                    lock (notifications)
                    {
                        notifications.Enqueue(action);
                    }
                    return true;
                },
                _ => Interlocked.Increment(ref diagnosticReports));
            owner.FailurePublished += _ => Interlocked.Increment(ref failurePublished);
            owner.AttachLibrary(first);
            owner.Enqueue([Path.Combine(root, "failed-generation.zip")]);

            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000));
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                lock (notifications)
                {
                    return notifications.Count > 0;
                }
            }, 5000));
            owner.AttachLibrary(second);
            DrainNotifications(notifications);

            Assert.AreEqual(0, failurePublished);
            Assert.AreEqual(1, diagnosticReports);
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
    public void DispatcherRejection_ReportsOriginalInstallFailure()
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
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var diagnosticReports = new List<Exception>();
            var owner = new PackageInstallWorkflowOwner(
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort(
                    (current, paths, token, onPath, onArchive) => throw new InvalidOperationException("install failed")),
                _ => false,
                exception =>
                {
                    lock (diagnosticReports)
                    {
                        diagnosticReports.Add(exception);
                    }
                });
            owner.AttachLibrary(library);
            owner.Enqueue([Path.Combine(root, "dispatcher-rejected.zip")]);

            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000));
            lock (diagnosticReports)
            {
                Assert.IsTrue(
                    diagnosticReports.Any(exception => exception?.Message == "install failed"),
                    "The original install exception must be reported when UI dispatch is rejected.");
            }
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
    public void DispatcherException_ReportsOriginalInstallFailure()
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
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var diagnosticReports = new List<Exception>();
            var owner = new PackageInstallWorkflowOwner(
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort(
                    (current, paths, token, onPath, onArchive) => throw new InvalidOperationException("install failed")),
                _ => throw new InvalidOperationException("dispatcher failed"),
                exception =>
                {
                    lock (diagnosticReports)
                    {
                        diagnosticReports.Add(exception);
                    }
                });
            owner.AttachLibrary(library);
            owner.Enqueue([Path.Combine(root, "dispatcher-threw.zip")]);

            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000));
            lock (diagnosticReports)
            {
                Assert.IsTrue(
                    diagnosticReports.Any(exception => exception?.Message == "install failed"),
                    "The original install exception must be reported when UI dispatch throws.");
                Assert.IsTrue(
                    diagnosticReports.Any(exception => exception?.Message == "dispatcher failed"),
                    "The dispatcher exception must remain observable alongside the install failure.");
            }
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
    public void FailureNotificationException_ReportsInstallAndNotificationFailures()
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
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var diagnosticReports = new List<Exception>();
            var owner = CreateOwner(
                (current, paths, token, onPath, onArchive) => throw new InvalidOperationException("install failed"),
                action =>
                {
                    action();
                    return true;
                },
                exception =>
                {
                    lock (diagnosticReports)
                    {
                        diagnosticReports.Add(exception);
                    }
                });
            owner.FailurePublished += _ => throw new InvalidOperationException("failure notification failed");
            owner.AttachLibrary(library);
            owner.Enqueue([Path.Combine(root, "notification-failed.zip")]);

            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000));
            lock (diagnosticReports)
            {
                Assert.IsTrue(diagnosticReports.Any(exception => exception?.Message == "install failed"));
                Assert.IsTrue(diagnosticReports.Any(exception => exception?.Message == "failure notification failed"));
            }
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
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
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
                action =>
                {
                    action();
                    return true;
                });
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
            var first = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var second = new TestBmsLibrary(secondDb, null, null, string.Empty);
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
                action =>
                {
                    action();
                    return true;
                });
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
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            int mutationCalls = 0;
            var owner = CreateOwner(
                (current, paths, token, onPath, onArchive) =>
                {
                    Interlocked.Increment(ref mutationCalls);
                    return [new ChartPackage()];
                },
                action =>
                {
                    action();
                    return true;
                });
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
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
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
                action =>
                {
                    action();
                    return true;
                });
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
        Func<Action, bool> dispatchToUi,
        Action<Exception>? reportNotificationFailure = null)
    {
        return new PackageInstallWorkflowOwner(
            new ChartFileOperationSynchronizer(),
            new ChartMutationActivityOwner(),
            new DelegatePackageInstallMutationPort(installBatch),
            dispatchToUi,
            reportNotificationFailure);
    }

    private static void DrainNotifications(Queue<Action> notifications)
    {
        while (true)
        {
            Action notification;
            lock (notifications)
            {
                if (notifications.Count == 0)
                {
                    return;
                }
                notification = notifications.Dequeue();
            }
            notification();
        }
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
