using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class TemporaryCopyFilesTestSupport
{
    /// <summary>日常と大規模条件で同じ並列コピーの所有権と終端後の回収を表明します。</summary>
    internal static async Task AssertParallelCopyOwnership(int count, bool observeWorkers)
    {
        using var caseWorkspace1 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace1.RootDirectory, "BeMusicSeeker_TemporaryCopy_" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "Source");
        string destinationDirectory = Path.Combine(root, "Destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        string[] sourceFiles = Enumerable.Range(0, count)
            .Select(index => Path.Combine(sourceDirectory, index.ToString("D4") + ".dat")).ToArray();
        using var release = new ManualResetEventSlim();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        int active = 0;
        Task? operation = null;
        var copies = new temporarilyCopyFiles(transfer: (source, output) =>
        {
            Interlocked.Increment(ref active);
            try
            {
                if (observeWorkers && Interlocked.Increment(ref entered) <= 2)
                {
                    if (Volatile.Read(ref entered) == 2) { bothEntered.TrySetResult(); }
                    release.Wait();
                }
                using FileStream input = File.OpenRead(source);
                input.CopyTo(output);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        try
        {
            foreach (string source in sourceFiles) { File.WriteAllText(source, Path.GetFileName(source)); }
            operation = Task.Run(() => copies.CopyFiles(sourceFiles, sourceDirectory, destinationDirectory,
                parallelDegree: observeWorkers ? 2 : null));
            if (observeWorkers)
            {
                Task first = await Task.WhenAny(bothEntered.Task, operation);
                if (first == operation) { await operation; Assert.Fail("二つの転送入場前にコピーが終端しました。"); }
                await bothEntered.Task;
                Assert.AreEqual(2, Volatile.Read(ref active));
                Assert.IsFalse(operation.IsCompleted);
                release.Set();
            }
            await operation;
            Assert.AreEqual(0, Volatile.Read(ref active));
            foreach (string source in sourceFiles)
            {
                Assert.AreEqual(Path.GetFileName(source), File.ReadAllText(Path.Combine(destinationDirectory, Path.GetFileName(source))));
            }
            copies.Dispose();
            Assert.IsFalse(copies.HasCleanupFailure);
            Assert.AreEqual(0, Directory.GetFiles(destinationDirectory, "*", SearchOption.AllDirectories).Length);
            foreach (string source in sourceFiles) { Assert.AreEqual(Path.GetFileName(source), File.ReadAllText(source)); }
        }
        finally
        {
            release.Set();
            try { if (operation != null) { try { await operation; } catch { /* 本体の元原因を維持して全転送の終端を回収します。 */ } } }
            finally
            {
                copies.Dispose();
                if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
            }
        }
    }

}
