using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("ProcessIntegration")]
public sealed class VerificationProcessLifecycleTests
{
    [TestMethod]
    public void NormalExitPreservesCompletedStandardOutputAndError()
    {
        using JsonDocument result = RunProbe("normal");
        Assert.AreEqual(0, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("stdout-complete", result.RootElement.GetProperty("stdout").GetString());
        Assert.AreEqual("stderr-complete", result.RootElement.GetProperty("stderr").GetString());
        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            ReadStringArray(result.RootElement.GetProperty("secondaryDiagnostics")));
        CollectionAssert.AreEqual(
            Array.Empty<int>(),
            ReadIntArray(result.RootElement.GetProperty("remainingOwnedProcessIds")));
    }

    [TestMethod]
    public void RootExitWithOwnedDescendantReturnsBoundedCleanupDiagnostic()
    {
        using JsonDocument result = RunProbe("descendant-root");
        Assert.AreEqual(0, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.IsFalse(result.RootElement.GetProperty("processTimedOut").GetBoolean());
        string[] diagnostics = ReadStringArray(result.RootElement.GetProperty("secondaryDiagnostics"));
        Assert.IsTrue(
            ContainsDiagnostic(diagnostics, "owned-descendant-cleanup")
            || ContainsDiagnostic(diagnostics, "stream-drain-timeout"),
            "The inherited-handle probe must retain a bounded stream or owned-descendant diagnostic.");
        CollectionAssert.AreEqual(
            Array.Empty<int>(),
            ReadIntArray(result.RootElement.GetProperty("remainingOwnedProcessIds")));
    }

    [TestMethod]
    public void NonzeroExitPreservesPrimaryFailureAlongsideCleanupDiagnostic()
    {
        using JsonDocument result = RunProbe("nonzero-descendant");
        Assert.AreEqual(7, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("nonzero-exit", result.RootElement.GetProperty("primaryFailureKind").GetString());
        Assert.AreEqual("primary-stderr", result.RootElement.GetProperty("stderr").GetString());
        string[] diagnostics = ReadStringArray(result.RootElement.GetProperty("secondaryDiagnostics"));
        Assert.IsTrue(ContainsDiagnostic(diagnostics, "owned-descendant-cleanup"));
        CollectionAssert.AreEqual(
            Array.Empty<int>(),
            ReadIntArray(result.RootElement.GetProperty("remainingOwnedProcessIds")));
    }

    private static JsonDocument RunProbe(string scenario)
    {
        string repositoryRoot = FindRepositoryRoot();
        string diagnosticsDirectory = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerVerificationProcessLifecycle",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(diagnosticsDirectory);
        string probePath = Path.Combine(
            repositoryRoot,
            "scripts",
            "test-fixtures",
            "verification-process-lifecycle-probe.ps1");
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(probePath);
        startInfo.ArgumentList.Add("-Scenario");
        startInfo.ArgumentList.Add(scenario);
        startInfo.ArgumentList.Add("-DiagnosticsDirectory");
        startInfo.ArgumentList.Add(diagnosticsDirectory);

        using var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start(), "The lifecycle probe process did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        const int processTimeoutMilliseconds = 30_000;
        const int streamTimeoutMilliseconds = 5_000;
        try
        {
            if (!process.WaitForExit(processTimeoutMilliseconds))
            {
                string cleanup = StopProcessTree(process);
                Assert.Fail(
                    $"The lifecycle probe exceeded {processTimeoutMilliseconds / 1000}s. "
                    + $"Cleanup: {cleanup}; stdout: {GetCompletedTaskValue(outputTask)}; "
                    + $"stderr: {GetCompletedTaskValue(errorTask)}");
            }

            Assert.IsTrue(
                Task.WaitAll(new Task[] { outputTask, errorTask }, streamTimeoutMilliseconds),
                $"The lifecycle probe streams did not close within {streamTimeoutMilliseconds / 1000}s. "
                + $"stdout: {GetCompletedTaskValue(outputTask)}; stderr: {GetCompletedTaskValue(errorTask)}");
            Assert.AreEqual(0, process.ExitCode, errorTask.GetAwaiter().GetResult());
            return JsonDocument.Parse(outputTask.GetAwaiter().GetResult().Trim());
        }
        finally
        {
            if (!process.HasExited)
            {
                StopProcessTree(process);
            }
            TryDeleteDirectory(diagnosticsDirectory);
        }
    }

    private static string StopProcessTree(Process process)
    {
        var diagnostics = new List<string>();
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            if (!process.HasExited && !process.WaitForExit(5_000))
            {
                diagnostics.Add($"probe PID {process.Id} remained active after cleanup.");
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add("probe cleanup failed: " + exception.Message);
        }
        return diagnostics.Count == 0 ? "completed" : string.Join("; ", diagnostics);
    }

    private static string GetCompletedTaskValue(Task<string> task)
    {
        return task.Status == TaskStatus.RanToCompletion ? task.GetAwaiter().GetResult() : "<unavailable>";
    }

    private static string[] ReadStringArray(JsonElement array)
    {
        var values = new List<string>();
        foreach (JsonElement value in array.EnumerateArray())
        {
            values.Add(value.GetString()!);
        }
        return values.ToArray();
    }

    private static int[] ReadIntArray(JsonElement array)
    {
        var values = new List<int>();
        foreach (JsonElement value in array.EnumerateArray())
        {
            values.Add(value.GetInt32());
        }
        return values.ToArray();
    }

    private static bool ContainsDiagnostic(IEnumerable<string> diagnostics, string value)
    {
        foreach (string diagnostic in diagnostics)
        {
            if (diagnostic.Contains(value, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Probe assertions remain authoritative; temporary diagnostics are best-effort.
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
