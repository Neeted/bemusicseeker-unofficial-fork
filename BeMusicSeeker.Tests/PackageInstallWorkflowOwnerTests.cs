using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public void TryEnqueue_BeforeLibraryAttachRejectsAndDeletesOwnedIngressRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageInstallWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int diagnosticReports = 0;
        int mutationCalls = 0;
        var owner = CreateOwner(
            (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref mutationCalls);
                return [];
            },
            action =>
            {
                action();
                return true;
            },
            _ => Interlocked.Increment(ref diagnosticReports));

        bool accepted = owner.TryEnqueue(CreateOwnedRequest(root, "before-attach.zip"));

        Assert.IsFalse(accepted);
        Assert.IsTrue(owner.IsIdle);
        Assert.AreEqual(0, mutationCalls);
        Assert.AreEqual(0, diagnosticReports);
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public void AcquireAndTryEnqueueDroppedPaths_StagesTransientSourceThroughMutationPort()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageInstallWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        string systemTempRoot = Path.Combine(root, "system-temp");
        string ingressRoot = Path.Combine(root, "managed-ingress");
        string source = Path.Combine(systemTempRoot, "archiver", "nested", "chart.bms");
        string songDbPath = Path.Combine(root, "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "borrowed-chart");
        File.WriteAllBytes(songDbPath, []);
        using var mutationEntered = new ManualResetEventSlim(false);
        using var allowMutationRead = new ManualResetEventSlim(false);
        using var mutationFinished = new ManualResetEventSlim(false);
        string? installedPath = null;
        string? installedContents = null;
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var materializer = new DroppedInstallIngressMaterializer(
                systemTempRoot,
                _ => false,
                () =>
                {
                    Directory.CreateDirectory(ingressRoot);
                    return ingressRoot;
                },
                DeleteOwnedRoot);
            var owner = CreateOwner(
                (_, paths, _, _, _) =>
                {
                    installedPath = paths.Single();
                    mutationEntered.Set();
                    Assert.IsTrue(allowMutationRead.Wait(5000));
                    installedContents = File.ReadAllText(installedPath);
                    mutationFinished.Set();
                    return [];
                },
                action =>
                {
                    action();
                    return true;
                },
                droppedInstallIngressMaterializer: materializer);
            owner.AttachLibrary(library);

            DroppedInstallIngressAcquisitionResult result =
                owner.AcquireAndTryEnqueueDroppedPaths([source]);

            Assert.IsTrue(result.Succeeded, result.Exception?.ToString());
            Assert.IsTrue(mutationEntered.Wait(5000));
            File.Delete(source);
            allowMutationRead.Set();
            Assert.IsTrue(mutationFinished.Wait(5000));
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000));
            Assert.AreEqual(
                Path.Combine(ingressRoot, "archiver", "nested", "chart.bms"),
                installedPath);
            Assert.AreEqual("borrowed-chart", installedContents);
            Assert.IsFalse(File.Exists(source));
            Assert.IsTrue(
                File.Exists(installedPath),
                "Installer handoff keeps the managed source in the pending-package lifecycle.");
        }
        finally
        {
            allowMutationRead.Set();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Enqueue_PublishesCompletionAfterLiveInstallReturnsAndContinuesAfterFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        using var firstInstallEntered = new ManualResetEventSlim(false);
        using var releaseFirstInstall = new ManualResetEventSlim(false);
        using var secondInstallEntered = new ManualResetEventSlim(false);
        using var releaseSecondInstall = new ManualResetEventSlim(false);
        using var secondFinished = new ManualResetEventSlim(false);
        using var completed = new ManualResetEventSlim(false);
        using var terminalInactive = new ManualResetEventSlim(false);
        var observationLock = new object();
        PackageInstallWorkflowOwner? owner = null;
        bool workerStarted = false;
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var calls = new List<string>();
            var failures = new List<PackageInstallFailure>();
            var eventOrder = new List<string>();
            owner = CreateOwner(
                (current, paths, token, onPath, onArchive) =>
                {
                    string displayName = Path.GetFileName(paths.FirstOrDefault() ?? string.Empty);
                    lock (observationLock)
                    {
                        calls.Add(displayName);
                    }
                    if (displayName == "first.zip")
                    {
                        firstInstallEntered.Set();
                        if (!releaseFirstInstall.Wait(5000))
                        {
                            throw new TimeoutException("The first install barrier was not released.");
                        }
                        throw new InvalidOperationException("first failed");
                    }
                    secondInstallEntered.Set();
                    if (!releaseSecondInstall.Wait(5000))
                    {
                        throw new TimeoutException("The second install barrier was not released.");
                    }
                    secondFinished.Set();
                    return [new ChartPackage()];
                },
                action =>
                {
                    action();
                    return true;
                });
            owner.AttachLibrary(library);
            owner.StatusChanged += snapshot =>
            {
                if (!snapshot.IsActive)
                {
                    lock (observationLock)
                    {
                        eventOrder.Add("inactive");
                    }
                    terminalInactive.Set();
                }
            };
            owner.FailurePublished += failure =>
            {
                lock (observationLock)
                {
                    failures.Add(failure);
                }
            };
            owner.CompletionPublished += _ =>
            {
                lock (observationLock)
                {
                    eventOrder.Add("completion");
                }
                completed.Set();
            };
            owner.Enqueue([Path.Combine(root, "first.zip")]);
            workerStarted = true;
            Assert.IsTrue(firstInstallEntered.Wait(5000), "The first batch did not start.");
            owner.Enqueue([Path.Combine(root, "second.zip")]);
            releaseFirstInstall.Set();

            Assert.IsTrue(secondInstallEntered.Wait(5000), "The following batch did not start after failure.");
            Assert.IsFalse(completed.IsSet, "Completion must not be published before the live install returns.");
            Assert.IsFalse(terminalInactive.IsSet, "The queue must remain active while the following batch is running.");
            Assert.IsFalse(owner.IsIdle, "The workflow must not become idle before the live install returns.");
            releaseSecondInstall.Set();

            Assert.IsTrue(secondFinished.Wait(5000), "The following batch did not run after failure.");
            Assert.IsTrue(terminalInactive.Wait(5000), "The workflow did not publish its terminal inactive status.");
            string[] callSnapshot;
            PackageInstallFailure[] failureSnapshot;
            string[] eventOrderSnapshot;
            lock (observationLock)
            {
                callSnapshot = calls.ToArray();
                failureSnapshot = failures.ToArray();
                eventOrderSnapshot = eventOrder.ToArray();
            }

            Assert.IsTrue(completed.IsSet, "The successful following batch must publish a completion receipt.");
            Assert.AreEqual(1, failureSnapshot.Length);
            CollectionAssert.AreEqual(new[] { "first.zip", "second.zip" }, callSnapshot);
            CollectionAssert.AreEqual(new[] { "completion", "inactive" }, eventOrderSnapshot);
            Assert.IsTrue(owner.IsIdle, "The workflow must be idle after its terminal inactive status.");
        }
        finally
        {
            releaseFirstInstall.Set();
            releaseSecondInstall.Set();
            if (workerStarted && owner != null)
            {
                terminalInactive.Wait(5000);
            }
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
    public void CancelAll_WhileWaitingForOperationGateDeletesUnhandedIngressWithoutCallingInstaller()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(root, "song.db");
        string ingressRoot = Path.Combine(root, "ingress");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(ingressRoot);
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            var chartFileOperations = new ChartFileOperationSynchronizer();
            var chartMutationActivity = new ChartMutationActivityOwner();
            int mutationCalls = 0;
            var owner = new PackageInstallWorkflowOwner(
                chartFileOperations,
                chartMutationActivity,
                new DelegatePackageInstallMutationPort((_, _, _, _, _) =>
                {
                    Interlocked.Increment(ref mutationCalls);
                    return [];
                }),
                action =>
                {
                    action();
                    return true;
                });
            owner.AttachLibrary(library);

            using (chartFileOperations.Enter())
            {
                Assert.IsTrue(owner.TryEnqueue(CreateOwnedRequest(ingressRoot, "chart.bms")));
                Assert.IsTrue(SpinWait.SpinUntil(() => chartMutationActivity.IsActive, 5000));
                owner.CancelAll();
            }

            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000));
            Assert.AreEqual(0, mutationCalls);
            Assert.IsFalse(Directory.Exists(ingressRoot));
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
    public void CancelAll_FromSuppressionCallbackWinsBeforeHandoffAndDeletesIngress()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(root, "song.db");
        string ingressRoot = Path.Combine(root, "ingress");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(ingressRoot);
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            int mutationCalls = 0;
            var owner = new PackageInstallWorkflowOwner(
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new DelegatePackageInstallMutationPort((_, _, _, _, _) =>
                {
                    Interlocked.Increment(ref mutationCalls);
                    return [];
                }),
                action =>
                {
                    action();
                    return true;
                });
            owner.RefreshSuppressionChanged += (_, args) =>
            {
                if (args.IsSuppressed)
                {
                    owner.CancelAll();
                }
            };
            owner.AttachLibrary(library);

            Assert.IsTrue(owner.TryEnqueue(CreateOwnedRequest(ingressRoot, "chart.bms")));

            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000));
            Assert.AreEqual(0, mutationCalls);
            Assert.IsFalse(Directory.Exists(ingressRoot));
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

    [TestMethod]
    public void TryEnqueue_AfterShutdownRejectsAndDeletesOwnedIngressRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var owner = CreateOwner((_, _, _, _, _) => [], _ => true);
        var request = new DroppedInstallBatchRequest(
            [Path.Combine(root, "chart.bms")],
            ["chart.bms"],
            [root],
            DeleteOwnedRoot,
            null);

        owner.RequestShutdown();
        bool accepted = owner.TryEnqueue(request);

        Assert.IsFalse(accepted);
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public void RequestShutdown_AfterPhysicalInsertionDrainsRequestWithoutPrivateLockCoordination()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(root, "song.db");
        string ingressRoot = Path.Combine(root, "ingress");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(ingressRoot);
        File.WriteAllBytes(songDbPath, []);
        using var enqueueStatusDispatchEntered = new ManualResetEventSlim(false);
        using var releaseEnqueueStatusDispatch = new ManualResetEventSlim(false);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            int mutationCalls = 0;
            int blockNextDispatch = 0;
            int blockedDispatchConsumed = 0;
            var owner = CreateOwner(
                (_, _, _, _, _) =>
                {
                    Interlocked.Increment(ref mutationCalls);
                    return [];
                },
                action =>
                {
                    if (Volatile.Read(ref blockNextDispatch) != 0
                        && Interlocked.CompareExchange(ref blockedDispatchConsumed, 1, 0) == 0)
                    {
                        enqueueStatusDispatchEntered.Set();
                        Assert.IsTrue(releaseEnqueueStatusDispatch.Wait(5000));
                    }
                    action();
                    return true;
                });
            owner.AttachLibrary(library);
            Volatile.Write(ref blockNextDispatch, 1);

            Task<bool> enqueue = Task.Run(() =>
                owner.TryEnqueue(CreateOwnedRequest(ingressRoot, "chart.bms")));
            Assert.IsTrue(
                enqueueStatusDispatchEntered.Wait(5000),
                "Physical insertion did not reach its lock-free status publication boundary.");

            Task shutdown = Task.Run(owner.RequestShutdown);
            Assert.IsTrue(
                shutdown.Wait(5000),
                "Status publication must not retain the owner or queue lock needed by shutdown.");
            releaseEnqueueStatusDispatch.Set();

            Assert.IsTrue(enqueue.Wait(5000));
            Assert.IsTrue(enqueue.Result, "Physical insertion preceding shutdown remains an accepted transfer.");
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000));
            Assert.AreEqual(0, mutationCalls);
            Assert.IsFalse(Directory.Exists(ingressRoot));
        }
        finally
        {
            releaseEnqueueStatusDispatch.Set();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CancelAll_AfterPhysicalInsertionCannotCancelFreshPostDrainAdmission()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(root, "song.db");
        string ingressRoot = Path.Combine(root, "ingress");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(ingressRoot);
        File.WriteAllBytes(songDbPath, []);
        using var enqueueStatusDispatchEntered = new ManualResetEventSlim(false);
        using var releaseEnqueueStatusDispatch = new ManualResetEventSlim(false);
        using var freshInstallCalled = new ManualResetEventSlim(false);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDbPath))
            {
            }
            var library = new TestBmsLibrary(songDbPath, null, null, string.Empty);
            int mutationCalls = 0;
            int blockNextDispatch = 0;
            int blockedDispatchConsumed = 0;
            var owner = CreateOwner(
                (_, paths, _, _, _) =>
                {
                    Interlocked.Increment(ref mutationCalls);
                    if (paths.Contains("fresh.zip"))
                    {
                        freshInstallCalled.Set();
                    }
                    return [];
                },
                action =>
                {
                    if (Volatile.Read(ref blockNextDispatch) != 0
                        && Interlocked.CompareExchange(ref blockedDispatchConsumed, 1, 0) == 0)
                    {
                        enqueueStatusDispatchEntered.Set();
                        Assert.IsTrue(releaseEnqueueStatusDispatch.Wait(5000));
                    }
                    action();
                    return true;
                });
            owner.AttachLibrary(library);
            Volatile.Write(ref blockNextDispatch, 1);

            Task<bool> enqueue = Task.Run(() =>
                owner.TryEnqueue(CreateOwnedRequest(ingressRoot, "late.zip")));
            Assert.IsTrue(
                enqueueStatusDispatchEntered.Wait(5000),
                "Physical insertion did not reach its lock-free status publication boundary.");

            owner.CancelAll();
            releaseEnqueueStatusDispatch.Set();

            Assert.IsTrue(enqueue.Wait(5000));
            Assert.IsTrue(enqueue.Result);
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000));
            Assert.AreEqual(0, mutationCalls);
            Assert.IsFalse(Directory.Exists(ingressRoot));

            owner.Enqueue(["fresh.zip"]);
            Assert.IsTrue(freshInstallCalled.Wait(5000), "Normal cancellation must not close later admissions.");
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000));
            Assert.AreEqual(1, mutationCalls);
        }
        finally
        {
            releaseEnqueueStatusDispatch.Set();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void AttachLibrary_DeletesOldPendingIngressButPreservesHandedOffActiveIngress()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string firstDb = Path.Combine(root, "first", "song.db");
        string secondDb = Path.Combine(root, "second", "song.db");
        string activeRoot = Path.Combine(root, "active-ingress");
        string pendingRoot = Path.Combine(root, "pending-ingress");
        Directory.CreateDirectory(Path.GetDirectoryName(firstDb)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondDb)!);
        Directory.CreateDirectory(activeRoot);
        Directory.CreateDirectory(pendingRoot);
        File.WriteAllBytes(firstDb, []);
        File.WriteAllBytes(secondDb, []);
        using var activeStarted = new ManualResetEventSlim(false);
        using var releaseActive = new ManualResetEventSlim(false);
        try
        {
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(firstDb))
            {
            }
            using (var _ = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(secondDb))
            {
            }
            var firstLibrary = new TestBmsLibrary(firstDb, null, null, string.Empty);
            var secondLibrary = new TestBmsLibrary(secondDb, null, null, string.Empty);
            var owner = CreateOwner(
                (library, _, token, _, _) =>
                {
                    if (ReferenceEquals(library, firstLibrary))
                    {
                        activeStarted.Set();
                        releaseActive.Wait(5000);
                    }
                    return [];
                },
                _ => true);
            owner.AttachLibrary(firstLibrary);
            Assert.IsTrue(owner.TryEnqueue(CreateOwnedRequest(activeRoot, "active.zip")));
            Assert.IsTrue(activeStarted.Wait(5000));
            Assert.IsTrue(owner.TryEnqueue(CreateOwnedRequest(pendingRoot, "pending.zip")));

            owner.AttachLibrary(secondLibrary);

            Assert.IsTrue(SpinWait.SpinUntil(() => !Directory.Exists(pendingRoot), 5000));
            Assert.IsTrue(Directory.Exists(activeRoot), "Installer handoff must protect the active source from queue cleanup.");
            releaseActive.Set();
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, 5000));
            Assert.IsTrue(Directory.Exists(activeRoot));
        }
        finally
        {
            releaseActive.Set();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static DroppedInstallBatchRequest CreateOwnedRequest(string root, string originalPath)
    {
        return new DroppedInstallBatchRequest(
            [Path.Combine(root, originalPath)],
            [originalPath],
            [root],
            DeleteOwnedRoot,
            null);
    }

    private static void DeleteOwnedRoot(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static PackageInstallWorkflowOwner CreateOwner(
        Func<BMSLibrary, IEnumerable<string>, CancellationToken, Action, Action<string, int, int>, IReadOnlyList<ChartPackage>> installBatch,
        Func<Action, bool> dispatchToUi,
        Action<Exception>? reportNotificationFailure = null,
        DroppedInstallIngressMaterializer? droppedInstallIngressMaterializer = null)
    {
        return new PackageInstallWorkflowOwner(
            new ChartFileOperationSynchronizer(),
            new ChartMutationActivityOwner(),
            new DelegatePackageInstallMutationPort(installBatch),
            dispatchToUi,
            reportNotificationFailure,
            droppedInstallIngressMaterializer);
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
