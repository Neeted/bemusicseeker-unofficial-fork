using System.Collections;
using System.ComponentModel;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class CustomTableRowChangeTrackerTests
{
    /// <summary>三行の中央一行だけを保持範囲にした場合はその行の変更だけ通知することを確認し、画面外の全行を購読して再描画する誤りを検出します。</summary>
    [TestMethod]
    public void Rebuild_OnlyTracksRetainedRows()
    {
        int changedCount = 0;
        var row0 = new TestRow();
        var row1 = new TestRow();
        var row2 = new TestRow();
        var tracker = new CustomTableRowChangeTracker(delegate { changedCount++; });
        try
        {
            tracker.Rebuild(new object[] { row0, row1, row2 }, 1, 1);
            row0.RaiseChanged();
            row1.RaiseChanged();
            row2.RaiseChanged();

            Assert.AreEqual(1, changedCount);
            Assert.AreEqual(1, tracker.SubscribedRowCount);
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>二行ずつの保持範囲を一行動かすと外れた行を解除して新しい二行だけ通知することを確認し、移動後の購読残留や新規行の購読漏れを検出します。</summary>
    [TestMethod]
    public void Move_ShiftsSubscriptionsToTheNewRange()
    {
        int changedCount = 0;
        var row1 = new TestRow();
        var row2 = new TestRow();
        var row3 = new TestRow();
        object[] rows = [row1, row2, row3];
        var tracker = new CustomTableRowChangeTracker(delegate { changedCount++; });
        try
        {
            tracker.Rebuild(rows, 0, 2);
            tracker.Move(rows, 1, 2);

            row1.RaiseChanged();
            row2.RaiseChanged();
            row3.RaiseChanged();

            Assert.AreEqual(2, changedCount);
            Assert.AreEqual(2, tracker.SubscribedRowCount);
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>行集合の差替えで旧行を解除し通知可能な新行だけ購読することを確認し、旧集合の通知や通知不能な行を購読数に混ぜる誤りを検出します。</summary>
    [TestMethod]
    public void Rebuild_ReplacesSubscriptionsAndIgnoresNonNotifyRows()
    {
        int changedCount = 0;
        var oldRow = new TestRow();
        var newRow = new TestRow();
        object plainRow = new();
        object[] rows = [newRow, plainRow];
        var tracker = new CustomTableRowChangeTracker(delegate { changedCount++; });
        try
        {
            tracker.Rebuild(new object[] { oldRow }, 0, 1);

            tracker.Rebuild(rows, 0, 2);
            oldRow.RaiseChanged();
            newRow.RaiseChanged();

            Assert.AreEqual(1, changedCount);
            Assert.AreEqual(1, tracker.SubscribedRowCount);
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>重複参照の片方だけが離れても購読と値寿命を保ち、最後の離脱で一回だけ解除することを確認します。</summary>
    [TestMethod]
    public void Move_TracksDuplicateRowReferencesUntilLastDeparture()
    {
        int changedCount = 0;
        var duplicatedRow = new TestRow();
        var otherRow = new TestRow();
        object[] rows = [duplicatedRow, duplicatedRow, otherRow];
        var released = new List<object>();
        var tracker = new CustomTableRowChangeTracker(delegate { changedCount++; }, released.Add);
        try
        {
            tracker.Rebuild(rows, 0, 2);
            tracker.Move(rows, 1, 2);
            duplicatedRow.RaiseChanged();
            otherRow.RaiseChanged();
            Assert.AreEqual(2, changedCount);
            Assert.AreEqual(2, tracker.SubscribedRowCount);
            Assert.AreEqual(0, released.Count);
            tracker.Move(rows, 2, 1);
            duplicatedRow.RaiseChanged();
            otherRow.RaiseChanged();
            Assert.AreEqual(3, changedCount);
            Assert.AreEqual(1, tracker.SubscribedRowCount);
            CollectionAssert.AreEqual(new object[] { duplicatedRow }, released);
        }
        finally { tracker.DetachAllRows(); }
        CollectionAssert.AreEqual(new object[] { duplicatedRow, otherRow }, released);
    }

    /// <summary>同じ範囲への移動は行集合へ一度も触れず、購読も解除しないことを確認し、変化のない更新で全位置を読み直す誤りを検出します。</summary>
    [TestMethod]
    public void Move_SameRangeDoesNotAccessRows()
    {
        var rows = new CountingList(Enumerable.Range(0, 20).Select(_ => (object?)new TestRow()).ToList());
        var tracker = new CustomTableRowChangeTracker(delegate { });
        try
        {
            tracker.Rebuild(rows, 3, 6);
            rows.ResetCounts();

            tracker.Move(rows, 3, 6);

            Assert.AreEqual(0, rows.TotalAccessCount);
            Assert.AreEqual(6, tracker.SubscribedRowCount);
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>下へ重なって動く場合は入る位置だけを一回ずつ読み、重なる位置と出る位置へは触れず、購読の追加は入る行、解除は出る行だけであることを確認します。重なる行を解除して付け直す誤りと、全位置の読み直しを検出します。</summary>
    [TestMethod]
    public void Move_OverlappingDownReadsOnlyEnteringIndices()
    {
        var items = Enumerable.Range(0, 30).Select(_ => new TestRow()).ToList();
        var rows = new CountingList([.. items]);
        var released = new List<object>();
        var tracker = new CustomTableRowChangeTracker(delegate { }, released.Add);
        try
        {
            tracker.Rebuild(rows, 5, 6);
            rows.ResetCounts();
            int[] addsBefore = [.. items.Select(row => row.AddCount)];

            tracker.Move(rows, 8, 6);

            AssertAccessedExactly(rows, 11, 12, 13);
            CollectionAssert.AreEquivalent(new object[] { items[5], items[6], items[7] }, released);
            for (int i = 0; i < items.Count; i++)
            {
                Assert.AreEqual(i is 11 or 12 or 13 ? addsBefore[i] + 1 : addsBefore[i], items[i].AddCount, "購読の追加は入る行だけ。位置 " + i);
            }
            Assert.AreEqual(6, tracker.SubscribedRowCount);
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>上へ重なって動く場合も、入る位置だけを読み、重なる行へ触れないことを確認します。</summary>
    [TestMethod]
    public void Move_OverlappingUpReadsOnlyEnteringIndices()
    {
        var items = Enumerable.Range(0, 30).Select(_ => new TestRow()).ToList();
        var rows = new CountingList([.. items]);
        var released = new List<object>();
        var tracker = new CustomTableRowChangeTracker(delegate { }, released.Add);
        try
        {
            tracker.Rebuild(rows, 10, 6);
            rows.ResetCounts();

            tracker.Move(rows, 8, 6);

            AssertAccessedExactly(rows, 8, 9);
            CollectionAssert.AreEquivalent(new object[] { items[14], items[15] }, released);
            Assert.AreEqual(6, tracker.SubscribedRowCount);
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>範囲の伸縮（上端だけ、下端だけ、両端）では、増えた位置だけを読み、縮んだ位置は行集合へ触れずに解除することを確認します。</summary>
    [TestMethod]
    public void Move_GrowAndShrinkReadsOnlyAddedIndices()
    {
        var items = Enumerable.Range(0, 30).Select(_ => new TestRow()).ToList();
        var rows = new CountingList([.. items]);
        var released = new List<object>();
        var tracker = new CustomTableRowChangeTracker(delegate { }, released.Add);
        try
        {
            tracker.Rebuild(rows, 10, 4);
            rows.ResetCounts();

            tracker.Move(rows, 8, 8);
            AssertAccessedExactly(rows, 8, 9, 14, 15);
            Assert.AreEqual(0, released.Count);
            Assert.AreEqual(8, tracker.SubscribedRowCount);

            rows.ResetCounts();
            tracker.Move(rows, 11, 2);
            Assert.AreEqual(0, rows.TotalAccessCount, "縮むときは、残る行も出る行も行集合から読まない。");
            CollectionAssert.AreEquivalent(new object[] { items[8], items[9], items[10], items[13], items[14], items[15] }, released);
            Assert.AreEqual(2, tracker.SubscribedRowCount);
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>重ならない移動では、新しい範囲の全位置を読んで購読し、旧い範囲の全行を解除することを確認します。</summary>
    [TestMethod]
    public void Move_DisjointRangeRebuildsWholeRange()
    {
        var items = Enumerable.Range(0, 30).Select(_ => new TestRow()).ToList();
        var rows = new CountingList([.. items]);
        var released = new List<object>();
        var tracker = new CustomTableRowChangeTracker(delegate { }, released.Add);
        try
        {
            tracker.Rebuild(rows, 2, 4);
            rows.ResetCounts();

            tracker.Move(rows, 20, 4);

            AssertAccessedExactly(rows, 20, 21, 22, 23);
            CollectionAssert.AreEquivalent(new object[] { items[2], items[3], items[4], items[5] }, released);
            Assert.AreEqual(4, tracker.SubscribedRowCount);
            for (int i = 20; i < 24; i++)
            {
                Assert.AreEqual(1, items[i].AddCount, "新しい範囲の各行を購読する。位置 " + i);
            }
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>差分の移動でも、同じ参照が離れる位置と入る位置の両方にあれば購読と値を保ち（解除も再購読もしない）、完全に離れた後に一回だけ解除することを確認します。</summary>
    [TestMethod]
    public void Move_KeepsReferenceThatLeavesAndEntersInOneMove()
    {
        var shared = new TestRow();
        var other = new TestRow();
        var filler = Enumerable.Range(0, 6).Select(_ => new TestRow()).ToList();
        // 位置 0 と 4 が同じ参照。範囲 [0,3) から [2,5) へ動くと、位置 0 が出て位置 4 が入る。
        object[] items = [shared, filler[0], filler[1], filler[2], shared, other, filler[3]];
        var rows = new CountingList([.. items]);
        var released = new List<object>();
        var tracker = new CustomTableRowChangeTracker(delegate { }, released.Add);
        try
        {
            tracker.Rebuild(rows, 0, 3);
            int addBefore = shared.AddCount;

            tracker.Move(rows, 2, 3);

            CollectionAssert.AreEqual(new object[] { filler[0] }, released, "同じ参照が入るので、出る位置にあっても値寿命を保つ。");
            Assert.AreEqual(addBefore, shared.AddCount, "購読を付け直さない。");
            tracker.Move(rows, 5, 2);
            Assert.AreEqual(1, released.Count(row => ReferenceEquals(row, shared)), "最後の位置が離れた後に一回だけ解除する。");
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>nullの位置と変更通知に対応しない行は、数えても購読せず、範囲の移動で例外なく扱うことを確認します。</summary>
    [TestMethod]
    public void Move_IgnoresNullAndNonNotifyRows()
    {
        var notifying = new TestRow();
        object plain = new();
        object?[] items = [null, notifying, plain, null, new TestRow(), plain];
        var tracker = new CustomTableRowChangeTracker(delegate { });
        try
        {
            tracker.Rebuild(items, 0, 3);
            Assert.AreEqual(1, tracker.SubscribedRowCount);

            tracker.Move(items, 1, 4);
            Assert.AreEqual(2, tracker.SubscribedRowCount);

            tracker.Move(items, 4, 2);
            Assert.AreEqual(1, tracker.SubscribedRowCount);
            Assert.AreEqual(1, notifying.RemoveCount);
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>全体の取り直しでは範囲の全位置を読み、同じ位置で別の行参照に替わっていれば新しい参照を購読して旧い参照を解除し、変わらない参照の購読は保つことを確認します。</summary>
    [TestMethod]
    public void Rebuild_RereadsAllIndicesAndSwapsChangedReferences()
    {
        var kept = new TestRow();
        var replacedOld = new TestRow();
        var replacedNew = new TestRow();
        var list = new List<object?> { kept, replacedOld, new TestRow() };
        var rows = new CountingList(list);
        var released = new List<object>();
        var tracker = new CustomTableRowChangeTracker(delegate { }, released.Add);
        try
        {
            tracker.Rebuild(rows, 0, 3);
            int keptAdds = kept.AddCount;
            rows.ResetCounts();
            list[1] = replacedNew;

            tracker.Rebuild(rows, 0, 3);

            AssertAccessedExactly(rows, 0, 1, 2);
            Assert.AreEqual(keptAdds, kept.AddCount, "変わらない参照を付け直さない。");
            CollectionAssert.AreEqual(new object[] { replacedOld }, released);
            Assert.AreEqual(1, replacedNew.AddCount);
            Assert.AreEqual(3, tracker.SubscribedRowCount);
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>前回と別の行集合へ動かした場合は、重なる位置でも全位置を読み直して作り直すことを確認し、別集合の参照を差分で扱う誤りを検出します。</summary>
    [TestMethod]
    public void Move_DifferentRowSourceRebuilds()
    {
        var firstRows = new CountingList(Enumerable.Range(0, 10).Select(_ => (object?)new TestRow()).ToList());
        var secondRows = new CountingList(Enumerable.Range(0, 10).Select(_ => (object?)new TestRow()).ToList());
        var released = new List<object>();
        var tracker = new CustomTableRowChangeTracker(delegate { }, released.Add);
        try
        {
            tracker.Rebuild(firstRows, 2, 4);

            tracker.Move(secondRows, 3, 4);

            AssertAccessedExactly(secondRows, 3, 4, 5, 6);
            Assert.AreEqual(4, released.Count);
            Assert.AreEqual(4, tracker.SubscribedRowCount);
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>行がない、範囲が空、全体の解除では、全行を解除して以後の移動を全件取得から始めることを確認します。</summary>
    [TestMethod]
    public void Move_EmptyRowsOrDetachReleasesEverything()
    {
        var rows = new CountingList(Enumerable.Range(0, 10).Select(_ => (object?)new TestRow()).ToList());
        var released = new List<object>();
        var tracker = new CustomTableRowChangeTracker(delegate { }, released.Add);
        try
        {
            tracker.Rebuild(rows, 0, 4);
            tracker.Move(null, 0, 4);
            Assert.AreEqual(0, tracker.SubscribedRowCount);
            Assert.AreEqual(4, released.Count);

            tracker.Rebuild(rows, 0, 4);
            tracker.DetachAllRows();
            rows.ResetCounts();
            tracker.Move(rows, 0, 4);

            AssertAccessedExactly(rows, 0, 1, 2, 3);
            Assert.AreEqual(4, tracker.SubscribedRowCount);
        }
        finally { tracker.DetachAllRows(); }
    }

    /// <summary>同じ行の失効要求はDrainまで重複除去し別行を保持して取出し後は空にすることを確認し、重複失効や取出し済みの要求を再利用する誤りを検出します。</summary>
    [TestMethod]
    public void RowInvalidationQueue_CoalescesDuplicateRowsUntilDrain()
    {
        var queue = new CustomTableRowInvalidationQueue();
        object row = new();
        object otherRow = new();

        Assert.IsTrue(queue.Enqueue(row));
        Assert.IsFalse(queue.Enqueue(row));
        Assert.IsTrue(queue.Enqueue(otherRow));

        object[] rows = queue.Drain();

        Assert.AreEqual(2, rows.Length);
        CollectionAssert.Contains(rows, row);
        CollectionAssert.Contains(rows, otherRow);
        Assert.AreEqual(0, queue.Count);
        Assert.AreEqual(0, queue.Drain().Length);
    }

    private static void AssertAccessedExactly(CountingList rows, params int[] indices)
    {
        CollectionAssert.AreEquivalent(indices, rows.AccessedIndices.ToArray(), "行集合へ読んだ位置");
        foreach (int index in indices)
        {
            Assert.AreEqual(1, rows.AccessCount(index), "位置 " + index + " は一回だけ読む。");
        }
    }

    private sealed class TestRow : INotifyPropertyChanged
    {
        private PropertyChangedEventHandler? handlers;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { handlers += value; AddCount++; }
            remove { handlers -= value; RemoveCount++; }
        }

        /// <summary>購読の追加の実回数です。</summary>
        internal int AddCount { get; private set; }

        /// <summary>購読の解除の実回数です。</summary>
        internal int RemoveCount { get; private set; }

        internal void RaiseChanged()
        {
            handlers?.Invoke(this, new PropertyChangedEventArgs(nameof(RaiseChanged)));
        }
    }

    /// <summary>添字ごとの読取り回数を数え、列挙されたら失敗する行集合です。</summary>
    private sealed class CountingList(List<object?> items) : IList
    {
        private readonly Dictionary<int, int> accessCounts = [];

        internal int TotalAccessCount => accessCounts.Values.Sum();

        internal IEnumerable<int> AccessedIndices => accessCounts.Keys;

        internal int AccessCount(int index) => accessCounts.GetValueOrDefault(index);

        internal void ResetCounts() => accessCounts.Clear();

        public object? this[int index]
        {
            get
            {
                accessCounts[index] = accessCounts.GetValueOrDefault(index) + 1;
                return items[index];
            }
            set => throw new NotSupportedException();
        }

        public int Count => items.Count;

        public bool IsFixedSize => true;

        public bool IsReadOnly => true;

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        public IEnumerator GetEnumerator() => throw new NotSupportedException("全行列挙は禁止です。");

        public int Add(object? value) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Contains(object? value) => throw new NotSupportedException();

        public int IndexOf(object? value) => throw new NotSupportedException();

        public void Insert(int index, object? value) => throw new NotSupportedException();

        public void Remove(object? value) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();

        public void CopyTo(Array array, int index) => throw new NotSupportedException();
    }
}
