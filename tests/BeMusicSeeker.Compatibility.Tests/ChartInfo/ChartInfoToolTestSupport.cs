using System.Runtime.ExceptionServices;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.Tests;

/// <summary>互換toolのroot・子孫・両pipeを同じJobで所有し、操作と失敗回収の期限を分けます。</summary>
internal static class ChartInfoToolTestSupport
{
    /// <summary>実toolを30秒以内に終端させ、失敗時は独立した5秒の回収結果も保持します。</summary>
    internal static async Task<ProcessCompletion> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, string temporaryDirectory, Action preserveUncollectedWorkspace)
    {
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30));
        ProcessDeadline? cleanupDeadline = null;
        ProcessDeadline BeginCleanup() => cleanupDeadline ??= ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(5));
        var request = new ProcessStartRequest(fileName, arguments, workingDirectory,
            new Dictionary<string, string?> { ["TEMP"] = temporaryDirectory, ["TMP"] = temporaryDirectory },
            ProcessOwnershipPolicy.Application);
        OwnedProcess process;
        try { process = OwnedProcess.Start(request, BeginCleanup); }
        catch (OwnedProcessStartException failure) when (failure.CleanupFailure is not null)
        {
            preserveUncollectedWorkspace();
            throw;
        }
        Exception? primary = null;
        ProcessCompletion? completion = null;
        try
        {
            completion = await process.WaitForExitAsync(deadline);
            Assert.AreEqual(0, completion.ExitCode, completion.StandardOutput + Environment.NewLine + completion.StandardError);
        }
        catch (Exception failure)
        {
            primary = failure;
            BeginCleanup();
        }
        finally
        {
            Exception? cleanup = null;
            try
            {
                ProcessStopResult stopped = await process.StopAsync(null, BeginCleanup());
                cleanup = stopped.CleanupFailure;
                if (cleanup is not null && !process.Completion.IsCompletedSuccessfully) { preserveUncollectedWorkspace(); }
            }
            catch (Exception failure)
            {
                cleanup = failure;
                if (!process.Completion.IsCompletedSuccessfully) { preserveUncollectedWorkspace(); }
            }
            try { await process.DisposeAsync(); }
            catch (Exception failure) { cleanup = cleanup is null ? failure : new AggregateException(cleanup, failure); }
            if (cleanup is not null)
            {
                if (primary is null) { primary = cleanup; }
                else { primary.Data["chart-info-tool.cleanup"] = cleanup; }
            }
        }
        if (primary is not null) { ExceptionDispatchInfo.Capture(primary).Throw(); }
        return completion ?? throw new InvalidOperationException("実toolの終端結果がありません。");
    }
}
