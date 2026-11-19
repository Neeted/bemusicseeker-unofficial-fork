using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 重複ツリーの子として表示する、先頭の「統合候補一覧」ノード（0〜1件）と、その後ろのグループ項目を連結した読取り専用の一覧です。
/// 二つの元の集合の変更を、位置を補正した個別の変更として中継します。元の集合の変更を保つため、Resetへ置き換えません。
/// WPFの <see cref="System.Windows.Data.CompositeCollection"/> は依存関係オブジェクトを使い、生成したスレッドに結び付くため使いません。
/// </summary>
internal sealed class DuplicateTreeChildCollection : IList, IReadOnlyList<object>, INotifyCollectionChanged
{
    private readonly ObservableCollection<DuplicateMergeCandidateListNode> head;
    private readonly ObservableCollection<DuplicateGroup> tail;

    internal DuplicateTreeChildCollection(
        ObservableCollection<DuplicateMergeCandidateListNode> head, ObservableCollection<DuplicateGroup> tail)
    {
        this.head = head ?? throw new ArgumentNullException(nameof(head));
        this.tail = tail ?? throw new ArgumentNullException(nameof(tail));
        head.CollectionChanged += (_, e) => Relay(e, 0);
        tail.CollectionChanged += (_, e) => Relay(e, this.head.Count);
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
