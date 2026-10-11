using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SynchronizationArchitectureTests
{
    /// <summary>
    /// 保証：フォルダ移動の受付後に決めた順序で必要lockだけを保持・逆順解放し、変更権限の明示失効後は再利用を拒否する。
    /// </summary>
    [TestMethod]
    public void FolderMoveSnapshotScope_UsesCanonicalLocksAndInvalidatesCapabilityBeforeRelease()
    {
        List<string> events = [];
        var boundary = new RecordingMutationBoundary(events);
        ReaderWriterLockSlimWrapper initializedAll = CreateObservedLock("initialized-all", events);
        ReaderWriterLockSlimWrapper initializedMin = CreateObservedLock("initialized-min", events);
        ReaderWriterLockSlimWrapper pendingInstall = CreateObservedLock("pending-install", events);
        ReaderWriterLockSlimWrapper bmsFiles = CreateObservedLock("bms-files", events);
        LibraryFileMutationLease? scope = null;

        try
        {
            var synchronization = new LibraryFileOperationSynchronization(
                boundary,
                initializedAll,
                initializedMin,
                pendingInstall,
                bmsFiles);

            scope = synchronization.EnterFolderMoveWriteScope();

            Assert.IsNotNull(scope);
            LibraryFileMutationCapability capability = scope.CreateMutationCapability();
            CollectionAssert.AreEqual(
                new[]
                {
                    "mutation-reservation.enter:library_folder_move:True"
                },
                events);

            using (synchronization.EnterFolderMoveSnapshotScope())
            {
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "mutation-reservation.enter:library_folder_move:True",
                        "initialized-min.read.enter",
                        "pending-install.write.enter",
                        "bms-files.write.enter"
                    },
                    events);
                Assert.AreEqual(1u, initializedMin.LockingReadCount);
                Assert.AreEqual(1u, pendingInstall.LockingWriteCount);
                Assert.AreEqual(1u, bmsFiles.LockingWriteCount);
                Assert.AreEqual(0u, initializedAll.LockingReadCount);
                Assert.AreEqual(0u, initializedAll.LockingWriteCount);
            }

            Assert.AreEqual(0u, initializedMin.LockingReadCount);
            Assert.AreEqual(0u, pendingInstall.LockingWriteCount);
            Assert.AreEqual(0u, bmsFiles.LockingWriteCount);
            capability.Validate(boundary);
            capability.Dispose();
            Assert.ThrowsException<InvalidOperationException>(() => capability.Validate(boundary));
            scope.Dispose();
            scope = null;

            CollectionAssert.AreEqual(
                new[]
                {
                    "mutation-reservation.enter:library_folder_move:True",
                    "initialized-min.read.enter",
                    "pending-install.write.enter",
                    "bms-files.write.enter",
                    "bms-files.write.exit",
                    "pending-install.write.exit",
                    "initialized-min.read.exit",
                    "mutation-lease.dispose"
                },
                events);
            Assert.AreEqual(0u, initializedMin.LockingReadCount);
            Assert.AreEqual(0u, pendingInstall.LockingWriteCount);
            Assert.AreEqual(0u, bmsFiles.LockingWriteCount);
            Assert.IsFalse(boundary.LeaseActive);

            using LibraryFileMutationLease freshLease = synchronization.EnterFolderMoveWriteScope();
            using LibraryFileMutationCapability freshCapability = freshLease.CreateMutationCapability();
            freshCapability.Validate(boundary);
            Assert.IsTrue(boundary.LeaseActive);
        }
        finally
        {
            scope?.Dispose();
            initializedAll.Dispose();
            initializedMin.Dispose();
            pendingInstall.Dispose();
            bmsFiles.Dispose();
        }
    }

    /// <summary>
    /// 保証：フォルダ移動の受付拒否は各lock取得より先に終わり、予約・読み書きlock・活動中権限を残さない。
    /// </summary>
    [TestMethod]
    public void FolderMoveAdmission_RejectionDoesNotAcquireLocks()
    {
        List<string> events = [];
        var boundary = new RecordingMutationBoundary(events)
        {
            RejectReservation = true
        };
        ReaderWriterLockSlimWrapper initializedAll = CreateObservedLock("initialized-all", events);
        ReaderWriterLockSlimWrapper initializedMin = CreateObservedLock("initialized-min", events);
        ReaderWriterLockSlimWrapper pendingInstall = CreateObservedLock("pending-install", events);
        ReaderWriterLockSlimWrapper bmsFiles = CreateObservedLock("bms-files", events);

        try
        {
            var synchronization = new LibraryFileOperationSynchronization(
                boundary,
                initializedAll,
                initializedMin,
                pendingInstall,
                bmsFiles);

            LibraryFileMutationLease? scope = synchronization.EnterFolderMoveWriteScope();

            Assert.IsNull(scope);
            CollectionAssert.AreEqual(
                new[]
                {
                    "mutation-reservation.enter:library_folder_move:True"
                },
                events);
            Assert.IsNull(boundary.Reservation);
            Assert.AreEqual(0u, initializedAll.LockingReadCount);
            Assert.AreEqual(0u, initializedAll.LockingWriteCount);
            Assert.AreEqual(0u, initializedMin.LockingReadCount);
            Assert.AreEqual(0u, initializedMin.LockingWriteCount);
            Assert.AreEqual(0u, pendingInstall.LockingReadCount);
            Assert.AreEqual(0u, pendingInstall.LockingWriteCount);
            Assert.AreEqual(0u, bmsFiles.LockingReadCount);
            Assert.AreEqual(0u, bmsFiles.LockingWriteCount);

            scope = null;
        }
        finally
        {
            initializedAll.Dispose();
            initializedMin.Dispose();
            pendingInstall.Dispose();
            bmsFiles.Dispose();
        }
    }

    /// <summary>
    /// 保証：LR2譜面情報の権限を保持しても元管理主体を強参照せず、退役した主体をGC可能にする。
    /// </summary>
    [TestMethod]
    public void ChartInfoCapabilityDoesNotRetainOwner()
    {
        Lr2ChartInfoCapability capability;
        WeakReference ownerReference = CreateChartInfoCapabilityOwnerReference(out capability);
        for (int attempt = 0; attempt < 3 && ownerReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.IsFalse(ownerReference.IsAlive);
        GC.KeepAlive(capability);
    }

    /// <summary>
    /// 保証：LR2同期入力の行とパスは捕捉後の所持集合変更に影響されず、同期途中で新旧データを混在させない。
    /// </summary>
    [TestMethod]
    public void InputRowSnapshotIsExposedAsOneImmutableContract()
    {
        var storageRowsOwner = new CatalogOwnedCollectionOwner();
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = "chart.bms",
            Md5 = new string('a', 32)
        };
        storageRowsOwner.ReplaceChartsAndCaptureSnapshot([file], []);
        var mutationOwner = new CatalogMutationOwner(storageRowsOwner, null);

        Lr2SongDbSyncInputRowSnapshot snapshot = mutationOwner.CaptureLr2SynchronizationInputRowSnapshot();
        Assert.AreEqual(1, snapshot.SongRows.Count);
        Assert.AreEqual(file.Path, snapshot.ChartPaths.Single());
        storageRowsOwner.ReplaceCharts([], [], replaceBmson: true);
        Assert.AreEqual(file.Md5, snapshot.SongRows.Single().Md5);
        Assert.AreEqual(file.Path, snapshot.ChartPaths.Single());
    }

    /// <summary>
    /// 保証：変更scope中の警告はFlushまで実通知を遅らせ、scope外の警告は直ちに通知してlock中の対話待ちを防ぐ。
    /// </summary>
    [TestMethod]
    public void MutationWarningUsesScopedOperationDialogCapability()
    {
        Assert.IsTrue(typeof(IBmsLibraryDialogService).IsAssignableFrom(typeof(ScopedOperationDialogCoordinator)));
        MethodInfo? showMethod = typeof(Lr2SynchronizationRuntimePort).GetMethod(
            "ShowOperationDialog",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.IsNotNull(showMethod);
        Assert.AreEqual(typeof(UiDialogDefaultResult), showMethod!.ReturnType);

        var dialogs = new RecordingDialogService();
        var coordinator = new ScopedOperationDialogCoordinator(dialogs);
        using (ScopedOperationDialogCoordinator.Scope scope = coordinator.BeginScope())
        {
            UiDialogDefaultResult result = coordinator.Show(
                "warning",
                "caption",
                UiDialogButton.OK,
                UiDialogIcon.Exclamation,
                UiDialogDefaultResult.OK);

            Assert.AreEqual(UiDialogDefaultResult.OK, result);
            Assert.AreEqual(0, dialogs.CallCount);
            Assert.AreEqual(1, scope.Messages.Count);
            scope.Flush();
            Assert.AreEqual(1, dialogs.CallCount);
        }

        coordinator.Show(
            "after",
            "caption",
            UiDialogButton.OK,
            UiDialogIcon.Exclamation,
            UiDialogDefaultResult.OK);
        Assert.AreEqual(2, dialogs.CallCount);
    }

    /// <summary>
    /// 保証：LR2検索先から管理出力基点と子孫を除外し、設定読込み失敗時は旧検索先を使わず、独立モードではLR2設定失敗に依存しない。
    /// </summary>
    [TestMethod]
    public void SearchRootSnapshotPreservesExcludedDescendantsAndClearsAfterConfigFailure()
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_Lr2SearchRoot_" + Guid.NewGuid().ToString("N"));
        string excludedRoot = Path.Combine(tempRoot, "generated");
        string excludedChild = Path.Combine(excludedRoot, "nested");
        string retainedRoot = Path.Combine(tempRoot, "songs");
        Directory.CreateDirectory(excludedChild);
        Directory.CreateDirectory(retainedRoot);

        try
        {
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDirRootType = excludedRoot
            };
            var owner = new Lr2SearchRootSnapshotOwner(
                new Lr2ConfigSnapshotProvider(() => null),
                () => options)
            {
                SearchTargets = [excludedRoot, excludedChild, retainedRoot]
            };

            CollectionAssert.AreEqual(new[] { retainedRoot }, owner.Capture().Roots.ToArray());

            var throwingOwner = new Lr2SearchRootSnapshotOwner(
                new Lr2ConfigSnapshotProvider(() => throw new InvalidOperationException("config unavailable")),
                () => options)
            {
                SearchTargets = [retainedRoot]
            };

            CollectionAssert.AreEqual(Array.Empty<string>(), throwingOwner.Capture().Roots.ToArray());

            string malformedConfigPath = Path.Combine(tempRoot, "config.xml");
            File.WriteAllText(malformedConfigPath, "<root />");
            var malformedConfigOwner = new Lr2SearchRootSnapshotOwner(
                new Lr2ConfigSnapshotProvider(() => new BeMusicSeeker.Models.LR2.LR2Config(malformedConfigPath)),
                () => options)
            {
                SearchTargets = [retainedRoot]
            };

            CollectionAssert.AreEqual(Array.Empty<string>(), malformedConfigOwner.Capture().Roots.ToArray());

            var normalModeOptions = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = false
            };
            var normalModeOwner = new Lr2SearchRootSnapshotOwner(
                new Lr2ConfigSnapshotProvider(() => throw new InvalidOperationException("config unavailable")),
                () => normalModeOptions)
            {
                SearchTargets = [retainedRoot]
            };

            CollectionAssert.AreEqual(new[] { retainedRoot }, normalModeOwner.Capture().Roots.ToArray());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// 保証：必須の操作設定providerが値を返さない場合は検索先捕捉を失敗させ、暗黙の既定設定で走査しない。
    /// </summary>
    [TestMethod]
    public void SearchRootSnapshotRejectsNullOptionsProvider()
    {
        var owner = new Lr2SearchRootSnapshotOwner(
            new Lr2ConfigSnapshotProvider(() => null),
            () => null);

        Assert.ThrowsException<InvalidOperationException>(() => owner.Capture());
    }

    /// <summary>
    /// 保証：長い管理出力パスの子孫も検索先から除外し、通常パス長を超えた出力を譜面として再取込みしない。
    /// </summary>
    [TestMethod]
    public void SearchRootSnapshotExcludesLongCustomOutputDescendants()
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_Lr2LongSearchRoot_" + Guid.NewGuid().ToString("N"));
        string excludedRoot = Path.Combine(
            tempRoot,
            string.Join(Path.DirectorySeparatorChar.ToString(), Enumerable.Repeat(new string('x', 24), 12)));
        string excludedChild = Path.Combine(excludedRoot, "nested");
        LongPathFileSystem.CreateDirectory(excludedChild);

        try
        {
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2CustomFolderAdditionalOutputBaseDirs = [excludedRoot]
            };
            var owner = new Lr2SearchRootSnapshotOwner(
                new Lr2ConfigSnapshotProvider(() => null),
                () => options)
            {
                SearchTargets = [excludedRoot, excludedChild]
            };

            CollectionAssert.AreEqual(Array.Empty<string>(), owner.Capture().Roots.ToArray());
        }
        finally
        {
            if (LongPathFileSystem.DirectoryExists(tempRoot))
            {
                LongPathFileSystem.DeleteDirectory(tempRoot, recursive: true);
            }
        }
    }

    private static WeakReference CreateChartInfoCapabilityOwnerReference(
        out Lr2ChartInfoCapability capability)
    {
        CatalogChartInfoOwner owner = new(
            _ => { },
            () => false,
            (_, _) => false,
            () => null,
            _ => { });
        WeakReference reference = new(owner);
        capability = new Lr2ChartInfoCapability(owner);
        return reference;
    }

    private static ReaderWriterLockSlimWrapper CreateObservedLock(
        string name,
        List<string> events)
    {
        var gate = new ReaderWriterLockSlimWrapper();
        gate.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ReaderWriterLockSlimWrapper.LockingReadCount))
            {
                events.Add($"{name}.read.{(gate.LockingReadCount > 0 ? "enter" : "exit")}");
            }
            else if (args.PropertyName == nameof(ReaderWriterLockSlimWrapper.LockingWriteCount))
            {
                events.Add($"{name}.write.{(gate.LockingWriteCount > 0 ? "enter" : "exit")}");
            }
        };
        return gate;
    }

    private sealed class RecordingMutationBoundary : ILibraryFileOperationMutationBoundary
    {
        private readonly List<string> events;

        internal RecordingMutationBoundary(List<string> events)
        {
            this.events = events;
        }

        internal bool RejectReservation { get; init; }

        internal LibraryFileMutationLease? Reservation { get; private set; }

        internal bool LeaseActive { get; private set; } = true;

        public LibraryFileMutationLease? TryBeginMutation(string operation, bool showMessage, LibraryFileMutationCapability? capability = null)
        {
            events.Add($"mutation-reservation.enter:{operation}:{showMessage}");
            if (RejectReservation)
            {
                return null!;
            }

            // A new reservation starts a new capability lifetime.  The
            // previous lease's release callback leaves the boundary inactive
            // until admission succeeds again.
            LeaseActive = true;
            Reservation = new LibraryFileMutationLease(
                this,
                () => LeaseActive,
                () =>
                {
                    LeaseActive = false;
                    events.Add("mutation-lease.dispose");
                });
            return Reservation;
        }

        public bool TryBlockMutation(string operation, bool showMessage, LibraryFileMutationCapability? capability = null)
        {
            events.Add($"mutation-block:{operation}:{showMessage}");
            return false;
        }

    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        internal int CallCount { get; private set; }

        public UiDialogDefaultResult Show(
            string messageBoxText,
            string caption,
            UiDialogButton button,
            UiDialogIcon icon,
            UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
        {
            CallCount++;
            return defaultResult;
        }
    }
}
