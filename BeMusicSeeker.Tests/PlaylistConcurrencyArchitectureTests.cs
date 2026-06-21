using System;
using System.IO;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistConcurrencyArchitectureTests
{
    [TestMethod]
    public void ExternalTableRegistration_DoesNotMutateVisibleCollectionInsideRegistrationWriterBlock()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string method = ExtractMethodBody(source, "internal async Task<BMSTable> RegistrateExternalTableAsync");

        StringAssert.Contains(method, "await AddCommittedBMSTableToVisibleCollectionAsync(bMSTable).ConfigureAwait(false)");
        Assert.IsFalse(
            method.Contains("BMSTables.Add(bMSTable);"),
            "External registration must not call DispatcherCollection.Add while the registration writer lock is held.");
    }

    [TestMethod]
    public void ExternalTableRegistration_DoesNotCommitDatabaseInsideRegistrationWriterBlock()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string method = ExtractMethodBody(source, "internal async Task<BMSTable> RegistrateExternalTableAsync");
        string writerBlock = ExtractBlockBody(method, "using (rwlockBMSTables.GetWriterGuard())");

        Assert.IsFalse(
            writerBlock.Contains("CommitBMSTable("),
            "External registration writer lock must not cover playlist DB persistence.");
        StringAssert.Contains(method, "CommitBMSTable(bMSTable);");
        StringAssert.Contains(method, "await AddCommittedBMSTableToVisibleCollectionAsync(bMSTable).ConfigureAwait(false)");
    }

    [TestMethod]
    public void PlaylistVisibleCollectionMutations_AreDispatchedThroughDedicatedBoundary()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));

        StringAssert.Contains(source, "InvokeBMSTablesCollectionMutation");
        StringAssert.Contains(source, "InvokeBMSTablesCollectionMutationAsync");
        StringAssert.Contains(source, "GetBMSTablesDispatcher");
    }

    [TestMethod]
    public void CommittedPlaylistVisibleCollectionReflection_IsNotCanceledAfterDatabaseCommit()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string addMethod = ExtractMethodBody(source, "private async Task AddCommittedBMSTableToVisibleCollectionAsync");
        string invokeAsyncMethod = ExtractMethodBody(source, "private async Task<T> InvokeBMSTablesCollectionMutationAsync<T>");

        Assert.IsFalse(addMethod.Contains("CancellationToken"), "Post-commit visible collection reflection must not accept a cancellation token.");
        Assert.IsFalse(invokeAsyncMethod.Contains("CancellationToken"), "Post-commit dispatcher reflection must not be canceled after DB commit.");
    }

    [TestMethod]
    public void ExternalTableImportContinuation_DoesNotReturnReferenceIndexWorkToUiThread()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string drainMethod = ExtractMethodBody(source, "private async Task DrainExternalPlaylistImportQueueAsync");
        string importMethod = ExtractMethodBody(source, "private async Task<ExternalPlaylistImportOutcome> ImportExternalPlaylistBMSTableCoreAsync");

        StringAssert.Contains(drainMethod, ".ConfigureAwait(false)");
        StringAssert.Contains(importMethod, "await tables.RegistrateExternalTableAsync(uri).ConfigureAwait(false)");
        StringAssert.Contains(importMethod, "if (!tables.ContainsBMSTable(table))");
        StringAssert.Contains(importMethod, "files.AddReferenceBMSTables(table);");
        StringAssert.Contains(importMethod, "files.RemoveReferenceBMSTables(table);");
        StringAssert.Contains(importMethod, "QueuePlaylistSummaryRefreshIfVisible(\"playlist_registered\", invalidateTableCountCache: true)");
        Assert.IsFalse(
            importMethod.Contains("tables.AcquireReaderLockBMSTables();"),
            "Import reference index updates must not hold the playlist collection reader lock while scanning library state.");
    }

    [TestMethod]
    public void ReaderWriterLockSlimWrapper_RawLockApiUpdatesObservableCounts()
    {
        var rwlock = new ReaderWriterLockSlimWrapper();

        rwlock.EnterReadLock();
        try
        {
            Assert.AreEqual(1u, rwlock.LockingReadCount);
        }
        finally
        {
            rwlock.ExitReadLock();
        }
        Assert.AreEqual(0u, rwlock.LockingReadCount);

        rwlock.EnterWriteLock();
        try
        {
            Assert.AreEqual(1u, rwlock.LockingWriteCount);
        }
        finally
        {
            rwlock.ExitWriteLock();
        }
        Assert.AreEqual(0u, rwlock.LockingWriteCount);

        Assert.IsTrue(rwlock.TryEnterUpgradeableReadLock(TimeSpan.Zero));
        try
        {
            Assert.AreEqual(1u, rwlock.LockingWriteCount);
        }
        finally
        {
            rwlock.ExitUpgradeableReadLock();
        }
        Assert.AreEqual(0u, rwlock.LockingWriteCount);
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        int nameIndex = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.IsTrue(nameIndex >= 0, methodName + " was not found.");
        int braceIndex = source.IndexOf('{', nameIndex);
        Assert.IsTrue(braceIndex >= 0, methodName + " body was not found.");

        int depth = 0;
        for (int i = braceIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(braceIndex, i - braceIndex + 1);
                }
            }
        }

        Assert.Fail(methodName + " body was not closed.");
        return string.Empty;
    }

    private static string ExtractBlockBody(string source, string blockHeader)
    {
        int headerIndex = source.IndexOf(blockHeader, StringComparison.Ordinal);
        Assert.IsTrue(headerIndex >= 0, blockHeader + " was not found.");
        int braceIndex = source.IndexOf('{', headerIndex);
        Assert.IsTrue(braceIndex >= 0, blockHeader + " body was not found.");

        int depth = 0;
        for (int i = braceIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(braceIndex, i - braceIndex + 1);
                }
            }
        }

        Assert.Fail(blockHeader + " body was not closed.");
        return string.Empty;
    }

    private static string FindRepositoryRoot()
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "BeMusicSeeker-decomp.sln")))
            {
                return directory;
            }
            DirectoryInfo parent = Directory.GetParent(directory);
            directory = parent == null ? string.Empty : parent.FullName;
        }
        Assert.Fail("Repository root was not found.");
        return string.Empty;
    }
}
