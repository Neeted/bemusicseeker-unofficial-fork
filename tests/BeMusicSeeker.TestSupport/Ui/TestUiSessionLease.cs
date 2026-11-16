using System.Diagnostics;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.TestSupport.Ui;

/// <summary>同じ利用者・Windows sessionの画面資源を、実行元worktreeによらず一つのmoduleに貸し出します。</summary>
public sealed class TestUiSessionLease : IDisposable
{
    private readonly FileStream handle;

    private TestUiSessionLease(FileStream handle) => this.handle = handle;

    /// <summary>非同期の終端でも解放できる排他file handleを期限内に取得します。</summary>
    public static Task<TestUiSessionLease> AcquireAsync(ProcessDeadline deadline, CancellationToken cancellationToken = default)
        => AcquireFileAsync(SessionPath, deadline, cancellationToken);

    /// <summary>runやworktreeでは分割しない、現在のユーザーとWindows sessionに固有のpathです。</summary>
    internal static string SessionPath
    {
        get
        {
            using var current = Process.GetCurrentProcess();
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeMusicSeeker.Tests", $"ui-session-{current.SessionId}.lock");
        }
    }

    /// <summary>排他fileの取得とOS回収を検査する境界です。通常moduleは固定session pathを使います。</summary>
    internal static async Task<TestUiSessionLease> AcquireFileAsync(string path, ProcessDeadline deadline, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new ArgumentException("排他fileのdirectoryがありません。", nameof(path)));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.Remaining == TimeSpan.Zero)
            {
                throw new TimeoutException("画面テストのsession排他の取得期限を超えました。");
            }
            try
            {
                var lease = new TestUiSessionLease(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                if (deadline.Remaining == TimeSpan.Zero)
                {
                    lease.Dispose();
                    throw new TimeoutException("画面テストのsession排他の取得期限を超えました。");
                }
                return lease;
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                TimeSpan remaining = deadline.Remaining;
                if (remaining == TimeSpan.Zero)
                {
                    throw new TimeoutException("画面テストのsession排他を取得できませんでした。", error);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50, remaining.TotalMilliseconds)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Windowと子processの終端後にhandleだけを解放します。共有fileは削除しません。</summary>
    public void Dispose() => handle.Dispose();
}
