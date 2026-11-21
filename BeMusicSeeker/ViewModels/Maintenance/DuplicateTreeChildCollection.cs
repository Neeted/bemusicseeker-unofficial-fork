using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 重複・分裂配置のツリーの子として表示する、先頭の「統合候補一覧」ノード（0〜1件）と、その後ろのグループ項目を連結した読取り専用の一覧です。
/// 二つの元の集合の変更を、位置を補正した個別の変更として中継します。元の集合の変更を保つため、Resetへ置き換えません。
/// 結果への同期（<see cref="SynchronizeItems"/>、<see cref="SynchronizeCandidateListNode"/>）もこの型が担い、二つのツリーが同じ規則を共有します。
/// WPFの <see cref="System.Windows.Data.CompositeCollection"/> は依存関係オブジェクトを使い、生成したスレッドに結び付くため使いません。
/// 項目の変更はUIスレッドだけで行います。
/// </summary>
internal sealed class DuplicateTreeChildCollection : IList, IReadOnlyList<object>, INotifyCollectionChanged
{
    private readonly ObservableCollection<DuplicateMergeCandidateListNode> head = [];
    private readonly ObservableCollection<DuplicateGroup> tail = [];

    /// <param name="candidateListNode">先頭に置く統合候補一覧のノード。置かないツリーでは <see langword="null"/> です。</param>
    internal DuplicateTreeChildCollection(DuplicateMergeCandidateListNode candidateListNode = null)
    {
        CandidateListNode = candidateListNode;
        head.CollectionChanged += (_, e) => Relay(e, 0);
        tail.CollectionChanged += (_, e) => Relay(e, head.Count);
    }

    /// <summary>グループ項目です。インスタンスは同期を通じて変わりません。</summary>
    internal ObservableCollection<DuplicateGroup> Items => tail;

    /// <summary>先頭に置く統合候補一覧のノードです。項目の有無とは別に常に同じインスタンスで、置かないツリーでは <see langword="null"/> です。</summary>
    internal DuplicateMergeCandidateListNode CandidateListNode { get; }

    /// <summary>
    /// 結果に含まれない項目だけを除き、新しい項目を結果の位置へ挿入します。
    /// 同じインスタンスの項目は除去・置換せず、位置が違う場合だけ移動します。Resetは使いません。
    /// </summary>
    internal void SynchronizeItems(IReadOnlyList<DuplicateGroup> target)
    {
        var wanted = new HashSet<DuplicateGroup>(target);
        for (int i = tail.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(tail[i]))
            {
                tail.RemoveAt(i);
            }
        }
        var present = new HashSet<DuplicateGroup>(tail);
        for (int i = 0; i < target.Count; i++)
        {
            DuplicateGroup group = target[i];
            if (i < tail.Count && ReferenceEquals(tail[i], group))
            {
                continue;
            }
            if (present.Contains(group))
            {
                tail.Move(tail.IndexOf(group), i);
            }
            else
            {
                tail.Insert(i, group);
                present.Add(group);
            }
        }
    }

    /// <summary>
    /// 統合候補一覧のノードを、<paramref name="present"/> が真のときだけ置きます。真の間の同期では、置いたノードを除去・置換しません。
    /// 重複のツリーは結果が一件以上あるとき、分裂配置のツリーは結果が <see langword="null"/> でないとき（0件を含む）に真を渡します。
    /// ノードを持たないツリーでは何もしません。
    /// </summary>
    internal void SynchronizeCandidateListNode(bool present)
    {
        if (CandidateListNode == null) { return; }
        if (present)
        {
            if (head.Count == 0) { head.Add(CandidateListNode); }
            return;
        }
        if (head.Count > 0) { head.RemoveAt(0); }
    }

    /// <summary>項目とノードをすべて取り除きます。</summary>
    internal void Clear()
    {
        tail.Clear();
        SynchronizeCandidateListNode(present: false);
    }

    /// <inheritdoc />
    public event NotifyCollectionChangedEventHandler CollectionChanged;

    public int Count => head.Count + tail.Count;

    public bool IsFixedSize => false;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object this[int index]
    {
        get => index < head.Count ? head[index] : tail[index - head.Count];
        set => throw new NotSupportedException();
    }

    public bool Contains(object value) => IndexOf(value) >= 0;

    public int IndexOf(object value)
    {
        switch (value)
        {
            case DuplicateMergeCandidateListNode node:
                return head.IndexOf(node);
            case DuplicateGroup group:
                int index = tail.IndexOf(group);
                return index < 0 ? -1 : index + head.Count;
            default:
                return -1;
        }
    }

    public void CopyTo(Array array, int index)
    {
        foreach (object item in this) { array.SetValue(item, index++); }
    }

    public IEnumerator GetEnumerator()
    {
        foreach (DuplicateMergeCandidateListNode node in head) { yield return node; }
        foreach (DuplicateGroup group in tail) { yield return group; }
    }

    IEnumerator<object> IEnumerable<object>.GetEnumerator()
    {
        foreach (object item in this) { yield return item; }
    }

    int IList.Add(object value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void IList.Insert(int index, object value) => throw new NotSupportedException();

    void IList.Remove(object value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    private void Relay(NotifyCollectionChangedEventArgs e, int offset)
    {
        NotifyCollectionChangedEventHandler handler = CollectionChanged;
        if (handler == null) { return; }
        handler(this, e.Action switch
        {
            NotifyCollectionChangedAction.Add => new NotifyCollectionChangedEventArgs(e.Action, e.NewItems, e.NewStartingIndex + offset),
            NotifyCollectionChangedAction.Remove => new NotifyCollectionChangedEventArgs(e.Action, e.OldItems, e.OldStartingIndex + offset),
            NotifyCollectionChangedAction.Replace => new NotifyCollectionChangedEventArgs(e.Action, e.NewItems, e.OldItems, e.NewStartingIndex + offset),
            NotifyCollectionChangedAction.Move => new NotifyCollectionChangedEventArgs(e.Action, e.NewItems, e.NewStartingIndex + offset, e.OldStartingIndex + offset),
            _ => new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)
        });
    }
}
