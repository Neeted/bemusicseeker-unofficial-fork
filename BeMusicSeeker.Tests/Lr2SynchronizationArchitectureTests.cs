using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SynchronizationArchitectureTests
{
    [TestMethod]
    public void LibraryCompositionUsesFacadeIndependentDialogBoundary()
    {
        Assert.IsTrue(typeof(IBmsLibraryDialogService).IsAssignableFrom(typeof(ScopedOperationDialogCoordinator)));
        Assert.IsNotNull(typeof(ScopedOperationDialogCoordinator).GetMethod(
            "BeginScope",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.IsNotNull(typeof(ScopedOperationDialogCoordinator).GetMethod(
            "Show",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
    }

    [TestMethod]
    public void LibraryFileOperationOwnerUsesDirectCapabilityComposition()
    {
        Type ownerType = typeof(LibraryFileOperationOwner);
        Assert.IsNull(typeof(BMSLibrary).GetNestedType(
            nameof(LibraryFileOperationOwner),
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
        ConstructorInfo constructor = ownerType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(LibraryFileOperationSynchronization)));
        Assert.IsNotNull(constructor);
        Assert.IsFalse(ownerType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Any(field => field.FieldType == typeof(BMSLibrary)));

        string ownerSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Models",
            "BMSLibrary.LibraryFileOperationOwner.cs");
        Assert.IsFalse(ownerSource.Contains("private readonly BMSLibrary owner"));
        Assert.IsFalse(ownerSource.Contains("ILibraryFileOperationPort"));
        StringAssert.Contains(ownerSource, "LibraryFileOperationSynchronization");
        StringAssert.Contains(ownerSource, "BmsLibraryLibraryFileOperationsService");
        StringAssert.Contains(ownerSource, "PackageLifecycleOwner");

        string mutationBoundarySource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Models",
            "BmsLibraryInternal",
            "LibraryFileOperationMutationBoundary.cs");
        Assert.IsFalse(mutationBoundarySource.Contains("private readonly BMSLibrary library"));
        Assert.IsFalse(mutationBoundarySource.Contains("ILibraryFileOperationPort"));
    }

    [TestMethod]
    public void SynchronizationPortsHaveNoFacadeDependency()
    {
        foreach (MethodInfo method in typeof(ILr2SynchronizationDataPort).GetMethods())
        {
            Assert.IsFalse(ContainsType(method.ReturnType, typeof(LR2SongDBExtended)), method.Name);
            Assert.IsFalse(
                method.GetParameters().Any(parameter => ContainsType(parameter.ParameterType, typeof(LR2SongDBExtended))),
                method.Name);
        }
        Assert.IsNotNull(typeof(ILr2SynchronizationDataPort).GetMethod("CaptureLr2FolderExistingRows"));
        Assert.IsNotNull(typeof(ILr2SynchronizationDataPort).GetMethod("ApplyLr2StartupScanBlockerCleanup"));
        Assert.IsNotNull(typeof(ILr2SynchronizationDataPort).GetMethod("ApplyLr2SongDbSyncStatusMutation"));

        PropertyInfo chartInfoWriter = typeof(Lr2SongDbSyncRequest).GetProperty("ChartInfoChunkWriter");
        Assert.IsNotNull(chartInfoWriter);
        Assert.IsFalse(ContainsType(chartInfoWriter.PropertyType, typeof(LR2SongDBExtended)));
        PropertyInfo songRowsVerifier = typeof(Lr2SongDbSyncRequest).GetProperty("SongRowsSkipVerifier");
        Assert.IsNotNull(songRowsVerifier);
        Assert.IsFalse(ContainsType(songRowsVerifier.PropertyType, typeof(LR2SongDBExtended)));
    }

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

    [TestMethod]
    public void InputRowSnapshotIsExposedAsOneImmutableContract()
    {
        var storageRowsOwner = new CatalogStorageRowsOwner();
        BMSFile file = new()
        {
            path = "chart.bms"
        };
        CatalogStorageRowsSnapshot storageSnapshot = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([file], []);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        ownedCollectionOwner.ApplyBuiltCollection(
            OwnedChartCollectionState.FromStorageRows([file], []),
            storageSnapshot.BmsRowsVersion,
            storageSnapshot.BmsonRowsVersion);
        var mutationOwner = new CatalogMutationOwner(
            storageRowsOwner,
            ownedCollectionOwner,
            null);

        Lr2SongDbSyncInputRowSnapshot snapshot = mutationOwner.CaptureLr2SynchronizationInputRowSnapshot();
        Assert.AreEqual(1, snapshot.SongRows.Count);
        Assert.AreEqual(file.path, snapshot.ChartPaths.Single());
        Assert.AreEqual(ownedCollectionOwner.CollectionVersion, snapshot.OwnedCollectionVersion);
        Assert.AreEqual(storageSnapshot.BmsRowsVersion, snapshot.BmsRowsVersion);
        Assert.AreEqual(storageSnapshot.BmsonRowsVersion, snapshot.BmsonRowsVersion);
    }

    [TestMethod]
    public void MutationWarningUsesScopedOperationDialogCapability()
    {
        Assert.IsTrue(typeof(IBmsLibraryDialogService).IsAssignableFrom(typeof(ScopedOperationDialogCoordinator)));
        MethodInfo showMethod = typeof(Lr2SynchronizationRuntimePort).GetMethod(
            "ShowOperationDialog",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.IsNotNull(showMethod);
        Assert.AreEqual(typeof(UiDialogDefaultResult), showMethod.ReturnType);

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

    [TestMethod]
    public void SearchRootSnapshotRejectsNullOptionsProvider()
    {
        var owner = new Lr2SearchRootSnapshotOwner(
            new Lr2ConfigSnapshotProvider(() => null),
            () => null);

        Assert.ThrowsException<InvalidOperationException>(() => owner.Capture());
    }

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

    private static bool ContainsType(Type candidate, Type expected)
    {
        if (candidate == expected)
        {
            return true;
        }
        if (candidate.IsArray || candidate.IsByRef || candidate.IsPointer)
        {
            return ContainsType(candidate.GetElementType(), expected);
        }
        return candidate.IsGenericType
            && candidate.GetGenericArguments().Any(argument => ContainsType(argument, expected));
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
