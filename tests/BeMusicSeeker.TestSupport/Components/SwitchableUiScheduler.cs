using BeMusicSeeker.Models;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>予約の受理・拒否・例外を切り替える予約先の動作です。</summary>
internal enum SwitchableUiSchedulerMode
{
    /// <summary>予約を受理し、操作をその場で実行します。</summary>
    Accept,

    /// <summary>予約を受理せず、拒否された操作を返します。</summary>
    Reject,

    /// <summary>予約の呼出しで例外を投げます。</summary>
    Throw
}

/// <summary>
/// 待たない通知の予約の受理・拒否・例外を、ケースの途中で切り替える予約先です。
/// 受理した操作はその場で実行するため、投函の結果は予約の呼出しの直後に観測できます。
/// </summary>
internal sealed class SwitchableUiScheduler : IUiScheduler
{
    /// <summary>次の予約の動作です。</summary>
    internal SwitchableUiSchedulerMode Mode { get; set; } = SwitchableUiSchedulerMode.Accept;

    public bool IsAvailable => true;

    public bool CanExecuteInline => false;

    public bool CheckAccess() => false;

    public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
    {
        switch (Mode)
        {
            case SwitchableUiSchedulerMode.Throw:
                throw new InvalidOperationException("予約に失敗しました。");
            case SwitchableUiSchedulerMode.Reject:
                return new Operation(accepted: false);
            default:
                action();
                return new Operation(accepted: true);
        }
    }

    public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => throw new NotSupportedException();

    public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => throw new NotSupportedException();

    public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => throw new NotSupportedException();

    public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => throw new NotSupportedException();

    private sealed class Operation(bool accepted) : IUiScheduledOperation
    {
        public bool IsAccepted => accepted;

        public bool IsCompleted => accepted;

        public bool IsAborted => false;

        public string RejectionReason => accepted ? string.Empty : "test_rejected";

        public Task Completion => Task.CompletedTask;

        public void Abort()
        {
        }
    }
}
