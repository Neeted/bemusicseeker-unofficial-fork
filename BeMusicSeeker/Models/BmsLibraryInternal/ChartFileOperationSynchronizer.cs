using System;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

/// <summary>
/// 一つのownerの論理受付を、受理から後片付けの終端まで所有します。共通ownerは譜面変更・推定・全体LR2・設定適用・必須背景入力更新、別の局所ownerはプレイリスト正本保存・管理出力を担当します。
/// 共通ownerの保持中は新しい試聴開始を拒否し、試聴同士の受理済み開始は既存の順次実行を維持します。
///
/// <para>受付は非待機とし、競合時は経路ごとの Busy 結果を返します。Busy の案内以外の確認、
/// 再生停止、filesystem / catalog の変更は始めません。取り込みの自動推定は同じ受付の継続です。
/// 受理済みの必須背景更新だけは、先行操作の終端を非同期で待てます。</para>
/// </summary>
internal sealed class ChartFileOperationSynchronizer
{
    private readonly object syncRoot = new();

    private TaskCompletionSource<bool> activeLease;

    private bool admissionClosed;

    /// <summary>このownerの受理済み要求が実終端まで受付を所有しているかを返します。</summary>
    internal bool IsActive => Volatile.Read(ref activeLease) != null;

    /// <summary>
    /// 共通の論理受付を待たずに取得します。Close後の新規要求は拒否し、受理済みリースは任意のスレッドから一回解放できます。
    /// </summary>
    /// <param name="lease">取得したリース。Busyまたは受付閉鎖では<see langword="null"/>。</param>
    /// <returns>この呼出しが受付を所有した場合だけ<see langword="true"/>。</returns>
    internal bool TryEnter(out IDisposable lease)
    {
        lock (syncRoot)
        {
            if (admissionClosed || activeLease != null)
            {
                lease = null;
                return false;
            }
            lease = CreateLeaseUnsafe();
            return true;
        }
    }

    /// <summary>新規要求の受付を不可逆に閉じます。受理済みlease・借用能力・必須背景継続は終端まで保持します。</summary>
    internal void CloseAdmission()
    {
        lock (syncRoot) { admissionClosed = true; }
    }

    private LibraryFileMutationLease CreateLeaseUnsafe()
    {
        var token = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        activeLease = token;
        return new LibraryFileMutationLease(this, () => ReferenceEquals(Volatile.Read(ref activeLease), token), () => Release(token));
    }

    /// <summary>受理済みの必須背景更新が、先行操作の実終端を待って受付を取得します。</summary>
    internal async Task<IDisposable> EnterAcceptedBackgroundAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task completion;
            lock (syncRoot)
            {
                // この入口は登録済みの必須処理の継続であり、Close後の新規要求ではありません。
                if (activeLease == null) { return CreateLeaseUnsafe(); }
                completion = activeLease.Task;
            }
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>取得済み共通leaseの所有者と寿命を検査し、内部継続の明示権限を発行します。</summary>
    internal LibraryFileMutationCapability CreateMutationCapability(IDisposable lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease is not LibraryFileMutationLease mutationLease) { throw new InvalidOperationException("Unknown operation lease."); }
        LibraryFileMutationCapability capability = mutationLease.CreateMutationCapability();
        try { capability.Validate(this); return capability; }
        catch { capability.Dispose(); throw; }
    }

    /// <summary>受理済み権限を検査し、受付を解放しない短い内部処理用のleaseを渡します。</summary>
    internal LibraryFileMutationLease Borrow(LibraryFileMutationCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        capability.Validate(this);
        return new LibraryFileMutationLease(this, () => capability.IsValidFor(this), () => { }, capability.PlaylistCapability);
    }

    /// <summary>現在受理済みの処理が権限を解放する実終端を非同期で待ちます。</summary>
    internal Task WaitForIdleAsync()
    {
        lock (syncRoot) { return activeLease?.Task ?? Task.CompletedTask; }
    }

    private void Release(TaskCompletionSource<bool> token)
    {
        lock (syncRoot)
        {
            if (ReferenceEquals(activeLease, token))
            {
                activeLease = null;
                token.TrySetResult(true);
            }
        }
    }
}
