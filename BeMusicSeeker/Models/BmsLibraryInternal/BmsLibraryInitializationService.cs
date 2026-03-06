using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryInitializationService
{
    public ScoreTableLoadResult LoadScoreTable(BmsLibraryDbGateway dbGateway)
    {
        if (dbGateway == null || string.IsNullOrWhiteSpace(dbGateway.ScoreDbPath))
        {
            return new ScoreTableLoadResult();
        }
        try
        {
            return dbGateway.LoadScoresAndPlayerId();
        }
        catch
        {
            return new ScoreTableLoadResult();
        }
    }

    public InstallTableLoadResult LoadInstallTable(BmsLibraryDbGateway dbGateway)
    {
        InstallTableLoadResult result = new InstallTableLoadResult();
        if (dbGateway == null)
        {
            return result;
        }
        try
        {
            List<BMSPackage> packages = dbGateway.LoadInstallPackages();
            result.PendingPackages.AddRange(packages.Where((BMSPackage pkg) => pkg != null && (File.Exists(pkg.path) || Directory.Exists(pkg.path)) && pkg.BMSFiles.Count > 0));
            result.StalePackages.AddRange(packages.Except(result.PendingPackages));
            result.StaleInstallPaths.AddRange(result.StalePackages.Where((BMSPackage pkg) => !string.IsNullOrWhiteSpace(pkg.path)).Select((BMSPackage pkg) => pkg.path));
        }
        catch
        {
        }
        return result;
    }

    public InitializationExecutionResult RunInitialize(
        List<Action> tasksContinuation,
        SemaphoreSlim semaphore,
        Action phase1,
        Action phase2,
        Action phase3)
    {
        InitializationExecutionResult result = new InitializationExecutionResult();
        Stopwatch stopwatchTotal = Stopwatch.StartNew();
        List<Task> continuationTasks = new List<Task>();
        Stopwatch stopwatchPhase1 = Stopwatch.StartNew();
        phase1?.Invoke();
        stopwatchPhase1.Stop();
        result.Phase1MinLoadMs = stopwatchPhase1.ElapsedMilliseconds;

        long waitBeforeContinuationStartMs = 0L;
        long waitForContinuationCompleteMs = 0L;
        Stopwatch stopwatchWaitBeforeContinuationStart = Stopwatch.StartNew();
        semaphore?.Wait();
        stopwatchWaitBeforeContinuationStart.Stop();
        waitBeforeContinuationStartMs = stopwatchWaitBeforeContinuationStart.ElapsedMilliseconds;

        if (tasksContinuation != null)
        {
            for (int i = 0; i < tasksContinuation.Count; i++)
            {
                continuationTasks.Add(Task.Run(tasksContinuation[i]).Logging("Initialize"));
            }
        }

        Thread.Yield();

        Stopwatch stopwatchPhase2 = Stopwatch.StartNew();
        phase2?.Invoke();
        stopwatchPhase2.Stop();
        result.Phase2ScanMaintMs = stopwatchPhase2.ElapsedMilliseconds;

        Stopwatch stopwatchPhase3 = Stopwatch.StartNew();
        phase3?.Invoke();
        stopwatchPhase3.Stop();
        result.Phase3InstallMaintenanceMs = stopwatchPhase3.ElapsedMilliseconds;

        if (semaphore != null && tasksContinuation != null && tasksContinuation.Count > 0)
        {
            Stopwatch stopwatchWaitForContinuationComplete = Stopwatch.StartNew();
            semaphore.Wait();
            stopwatchWaitForContinuationComplete.Stop();
            waitForContinuationCompleteMs = stopwatchWaitForContinuationComplete.ElapsedMilliseconds;
        }
        result.WaitContinuationMs = waitBeforeContinuationStartMs + waitForContinuationCompleteMs;

        Task.WaitAll(continuationTasks.ToArray());
        stopwatchTotal.Stop();
        result.TotalMs = stopwatchTotal.ElapsedMilliseconds;
        return result;
    }
}
