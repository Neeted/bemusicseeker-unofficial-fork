using System.ComponentModel;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>
/// 統合候補一覧の管理主体（重複・分裂配置のどちらも）の採用を、固定時間ではなく実際の通知で待つ補助です。
/// 画面の各テストクラスが同じ待ち方を共有します。
/// </summary>
internal static class CandidateListAwaitSupport
{
    /// <summary>実Dispatcherで、統合候補一覧の行集合が指定の行数で採用されるまで待ちます。行集合の入替え通知（Rows）を待ちます。</summary>
    internal static void AwaitRows(DuplicateMergeCandidateListOwner owner, int expectedRowCount)
    {
        var adopted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName == nameof(DuplicateMergeCandidateListOwner.Rows) && owner.RowSet.Count == expectedRowCount) { adopted.TrySetResult(); }
        };
        owner.PropertyChanged += changed;
        try
        {
            if (owner.RowSet.Count == expectedRowCount) { adopted.TrySetResult(); }
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(adopted.Task, "candidate list rebuilt with " + expectedRowCount + " rows");
        }
        finally { owner.PropertyChanged -= changed; }
    }

    /// <summary>実Dispatcherで、統合候補一覧が表示中（採用済み）になるまで、<see cref="DuplicateMergeCandidateListOwner.IsViewActive"/> の通知を待ちます。</summary>
    internal static void AwaitActive(DuplicateMergeCandidateListOwner owner, string stage)
    {
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler changed = (_, args) =>
        { if (args.PropertyName == nameof(DuplicateMergeCandidateListOwner.IsViewActive) && owner.IsViewActive) { active.TrySetResult(); } };
        owner.PropertyChanged += changed;
        try
        {
            if (owner.IsViewActive) { active.TrySetResult(); }
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(active.Task, stage);
        }
        finally { owner.PropertyChanged -= changed; }
    }

    /// <summary>実Dispatcherで、整理候補一覧が表示中（採用済み）になるまで、<see cref="AudioDuplicateCandidateListOwner.IsViewActive"/> の通知を待ちます。</summary>
    internal static void AwaitActive(AudioDuplicateCandidateListOwner owner, string stage)
    {
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler changed = (_, args) =>
        { if (args.PropertyName == nameof(AudioDuplicateCandidateListOwner.IsViewActive) && owner.IsViewActive) { active.TrySetResult(); } };
        owner.PropertyChanged += changed;
        try
        {
            if (owner.IsViewActive) { active.TrySetResult(); }
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(active.Task, stage);
        }
        finally { owner.PropertyChanged -= changed; }
    }

    /// <summary>実Dispatcherで、整理候補一覧の行集合が指定の行数で採用されるまで、行集合の入替え通知（Rows）を待ちます。</summary>
    internal static void AwaitRows(AudioDuplicateCandidateListOwner owner, int expectedRowCount)
    {
        var adopted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler changed = (_, args) =>
        { if (args.PropertyName == nameof(AudioDuplicateCandidateListOwner.Rows) && owner.RowSet.Count == expectedRowCount) { adopted.TrySetResult(); } };
        owner.PropertyChanged += changed;
        try
        {
            if (owner.RowSet.Count == expectedRowCount) { adopted.TrySetResult(); }
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(adopted.Task, "audio duplicate candidate list rebuilt with " + expectedRowCount + " rows");
        }
        finally { owner.PropertyChanged -= changed; }
    }

    /// <summary>実Dispatcherで、コレクションの要素数が指定の値になるまで、変更の通知（CollectionChanged）を待ちます。</summary>
    internal static void AwaitCount(System.Collections.IList items, int expectedCount, string stage)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifying = (System.Collections.Specialized.INotifyCollectionChanged)items;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler changed = (_, _) => { if (items.Count == expectedCount) { reached.TrySetResult(); } };
        notifying.CollectionChanged += changed;
        try
        {
            if (items.Count == expectedCount) { reached.TrySetResult(); }
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(reached.Task, stage);
        }
        finally { notifying.CollectionChanged -= changed; }
    }
}
