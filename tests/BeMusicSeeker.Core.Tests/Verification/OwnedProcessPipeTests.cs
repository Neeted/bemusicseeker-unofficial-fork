using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>root と Job の終端から独立した、両出力の回収契約を検査します。</summary>
[TestClass]
public sealed class OwnedProcessPipeTests
{
    /// <summary>root と Job が終了済みでも片側 reader が未完了なら完了を拒否し、先行側の内容と共有期限後の失敗を保持します。片側を読まない実装を検出します。</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OnePendingPipePreventsCompletionAfterRootAndJobExit(bool stdoutCompletesFirst)
    {
        const string stdoutText = "標準出力🙂";
        const string stderrText = "標準エラー🎵";
        var stageDeadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10));
        using var job = WindowsProcessJob.Create();
        using var realOutput = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var realError = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "ProcessFixture.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        info.ArgumentList.Add("exit");
        // Job が起動時から root を所有する。低位 constructor への引渡し失敗も外側の using が回収する。
        using Process root = job.Start(info, realOutput, realError);
        realOutput.DisposeLocalCopyOfClientHandle();
        realError.DisposeLocalCopyOfClientHandle();
        using var completedStream = new MemoryStream(Encoding.UTF8.GetBytes(stdoutCompletesFirst ? stdoutText : stderrText));
        using var pendingStream = new GatedReadStream(stdoutCompletesFirst ? stderrText : stdoutText);
        using var stdout = new StreamReader(stdoutCompletesFirst ? completedStream : pendingStream, new UTF8Encoding(false), false);
        using var stderr = new StreamReader(stdoutCompletesFirst ? pendingStream : completedStream, new UTF8Encoding(false), false);
        await using var process = new OwnedProcess(root, job, stdout, stderr);
        using var jobWaitCancellation = new CancellationTokenSource(stageDeadline.Remaining);
        Task jobExit = job.WaitForEmptyAsync(jobWaitCancellation.Token);
        try
        {
            Assert.AreEqual(0, await process.WaitForRootExitAsync(stageDeadline));
            Assert.IsTrue(root.HasExited);
            await jobExit.WaitAsync(stageDeadline.Remaining);
            await pendingStream.Reading.WaitAsync(stageDeadline.Remaining);
            Task<string> completed = stdoutCompletesFirst ? process.StandardOutput : process.StandardError;
            Task<string> pending = stdoutCompletesFirst ? process.StandardError : process.StandardOutput;
            Assert.AreEqual(stdoutCompletesFirst ? stdoutText : stderrText, await completed.WaitAsync(stageDeadline.Remaining));
            Assert.IsFalse(pending.IsCompleted);
            Assert.IsFalse(process.Completion.IsCompleted);

            var waitDeadline = ProcessDeadline.FromTimeout(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<TimeoutException>(() => process.WaitForExitAsync(waitDeadline)).WaitAsync(stageDeadline.Remaining);
            Exception? firstFailure = process.PrimaryFailure;
            Assert.AreEqual(TimeSpan.Zero, waitDeadline.Remaining);
            Assert.IsFalse(pending.IsCompleted);
            await Assert.ThrowsAsync<TimeoutException>(() => process.WaitForExitAsync(waitDeadline));
            pendingStream.Release();
            ProcessCompletion result = await process.WaitForExitAsync(stageDeadline);
            Assert.AreEqual(stdoutText, result.StandardOutput);
            Assert.AreEqual(stderrText, result.StandardError);
            await Assert.ThrowsAsync<TimeoutException>(() => process.WaitForExitAsync(waitDeadline));
            Assert.AreSame(firstFailure, process.PrimaryFailure);
        }
        finally
        {
            pendingStream.Release();
            var cleanupDeadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10));
            jobWaitCancellation.Cancel();
            try
            {
                await process.StopAsync(null, cleanupDeadline);
            }
            finally
            {
                try
                {
                    await jobExit.WaitAsync(cleanupDeadline.Remaining);
                }
                catch (OperationCanceledException) when (jobWaitCancellation.IsCancellationRequested)
                {
                    // 実終端の確認は Stop が担い、この観測用待機だけを finally で打ち切る。
                }
            }
        }
        Assert.IsNull(process.CleanupFailure);
    }

    private sealed class GatedReadStream : Stream
    {
        private readonly MemoryStream content;
        private readonly TaskCompletionSource<bool> reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>UTF-8 の内容を所有し、解放されるまで読取りだけを保留します。</summary>
        internal GatedReadStream(string text) => content = new MemoryStream(Encoding.UTF8.GetBytes(text));

        /// <summary>実際に reader が非同期読取りへ入った通知です。</summary>
        internal Task Reading => reading.Task;

        /// <summary>保留した読取りを解放します。finally でも繰り返し呼べます。</summary>
        internal void Release() => released.TrySetResult(true);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading.TrySetResult(true);
            await released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Release();
                content.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
