using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>PowerShellを使うプロセス統合テストの起動と後片付けを共有します。</summary>
internal static class PowerShellTestProcess
{
    private const int ProcessTimeoutMilliseconds = 30_000;
    private const int CleanupTimeoutMilliseconds = 5_000;
    private const int StreamTimeoutMilliseconds = 5_000;

    /// <summary>リポジトリのルートを実行中テストの出力位置から探します。</summary>
    internal static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    /// <summary>指定したPowerShellを実行し、標準出力と標準エラーを回収します。</summary>
    internal static PowerShellTestResult Run(
        string repositoryRoot,
        string command,
        string label,
        IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        foreach (KeyValuePair<string, string> entry in environment)
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start(), $"{label} PowerShell process did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        Exception? primaryFailure = null;
        var secondaryDiagnostics = new List<string>();
        try
        {
            if (!process.WaitForExit(ProcessTimeoutMilliseconds))
            {
                primaryFailure = new AssertFailedException(
                    $"{label} PowerShell process exceeded {ProcessTimeoutMilliseconds / 1000} seconds.");
            }
            else if (!Task.WaitAll(
                         new Task[] { outputTask, errorTask },
                         StreamTimeoutMilliseconds))
            {
                primaryFailure = new AssertFailedException(
                    $"{label} PowerShell output did not close within {StreamTimeoutMilliseconds / 1000} seconds.");
            }
            else if (process.ExitCode != 0)
            {
                primaryFailure = new AssertFailedException(
                    $"{label} PowerShell exited with code {process.ExitCode}.");
            }
        }
        catch (Exception exception) when (primaryFailure is null)
        {
            primaryFailure = exception;
        }
        finally
        {
            if (primaryFailure is not null)
            {
                string cleanup = StopProcessTree(process);
                if (!string.Equals(cleanup, "completed", StringComparison.Ordinal))
                {
                    secondaryDiagnostics.Add(cleanup);
                }
            }

            try
            {
                if (!Task.WaitAll(
                        new Task[] { outputTask, errorTask },
                        StreamTimeoutMilliseconds))
                {
                    secondaryDiagnostics.Add(
                        $"Redirected streams remained incomplete at the {StreamTimeoutMilliseconds / 1000}-second cleanup cutoff.");
                }
            }
            catch (Exception exception)
            {
                secondaryDiagnostics.Add(
                    $"Redirected stream failure observed during cleanup: {exception.GetType().Name}: {exception.Message}");
            }
        }

        if (primaryFailure is not null)
        {
            if (secondaryDiagnostics.Count > 0)
            {
                primaryFailure.Data["ProcessSecondaryDiagnostics"] = secondaryDiagnostics.ToArray();
            }
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        return new PowerShellTestResult(
            process.ExitCode,
            outputTask.GetAwaiter().GetResult(),
            errorTask.GetAwaiter().GetResult());
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
        }
        catch (Exception exception)
        {
            diagnostics.Add("managed process-tree termination failed: " + exception.Message);
        }

        try
        {
            if (!process.HasExited && !process.WaitForExit(CleanupTimeoutMilliseconds))
            {
                diagnostics.Add(
                    $"PowerShell PID {process.Id} remained active after {CleanupTimeoutMilliseconds / 1000} seconds.");
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add("process cleanup wait failed: " + exception.Message);
        }

        return diagnostics.Count == 0 ? "completed" : string.Join("; ", diagnostics);
    }
}

/// <summary>PowerShellプロセス統合テストの実行結果です。</summary>
internal readonly record struct PowerShellTestResult(int ExitCode, string Output, string Error);
