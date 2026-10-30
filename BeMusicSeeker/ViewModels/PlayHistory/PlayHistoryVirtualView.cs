using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryVirtualView : IList, IChartListViewMetadata, ICustomTableSelectionIdentitySource
{
    private IReadOnlyList<PlayHistoryRow> rows;

    private readonly int distinctFolderCount;
    private ScorePlayHistorySnapshot sourceSnapshot;
    private readonly PlayHistoryPeriodRequest period;
    private readonly string displayTargetIdentity;
    private Dictionary<object, int> selectionIndexes;

    /// <summary>表示行と、選択を持ち越せる同じ取得組・期間・採用対象の文脈を捕捉します。</summary>
    internal PlayHistoryVirtualView(IReadOnlyList<PlayHistoryRow> rows, int distinctFolderCount = -1,
        ScorePlayHistorySnapshot sourceSnapshot = null, PlayHistoryPeriodRequest period = null,
        string displayTargetIdentity = null)
    {
        this.rows = rows ?? [];
        this.distinctFolderCount = distinctFolderCount;
        this.sourceSnapshot = sourceSnapshot;
        this.period = period;
        this.displayTargetIdentity = displayTargetIdentity;
    }

    // 不変viewなので、選択更新通知は差分ではなく既存のview交換終端が担います。
    public event EventHandler SelectionUpdateStarting { add { } remove { } }
    public event EventHandler SelectionUpdateCompleted { add { } remove { } }

    /// <summary>同じ譜面の別プレイを区別する取得時のraw record参照を返します。</summary>
    public object GetSelectionIdentity(int index) => index >= 0 && index < rows.Count
        ? (object)rows[index].Raw ?? rows[index].BeatorajaRaw : null;

    /// <summary>残っている同じプレイだけを復元し、消失した行を旧添字へ移しません。</summary>
    public int IndexOfSelectionIdentity(object identity)
    {
        if (identity == null) { return -1; }
        if (selectionIndexes == null)
        {
            // 全選択の復元でも全件走査を選択行ごとに繰り返さず、view寿命内で参照索引を一度だけ作ります。
            selectionIndexes = new Dictionary<object, int>(rows.Count, ReferenceEqualityComparer.Instance);
            for (int index = 0; index < rows.Count; index++)
            { selectionIndexes.TryAdd(GetSelectionIdentity(index), index); }
        }
        return selectionIndexes.TryGetValue(identity, out int position) ? position : -1;
    }

    /// <summary>要求IDによらず同じ取得組・期間・採用対象だけを選択識別の文脈として共有します。</summary>
    public bool SharesSelectionIdentityScope(ICustomTableSelectionIdentitySource other)
        => sourceSnapshot != null && other is PlayHistoryVirtualView after
            && ReferenceEquals(sourceSnapshot, after.sourceSnapshot)
            && PlayHistoryWorkflowOwner.IsSamePeriod(period, after.period)
            && string.Equals(displayTargetIdentity, after.displayTargetIdentity, StringComparison.Ordinal);

    public int Count => rows.Count;

    public int RowCount => Count;

    public int DistinctFolderCount => distinctFolderCount;

    public int RealizedRowCount => Count;

    public bool IsReadOnly => true;

    public bool IsFixedSize => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object this[int index]
    {
        get
        {
            if (index < 0 || index >= rows.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return rows[index];
        }
        set => throw new NotSupportedException();
    }

    public IEnumerator GetEnumerator()
    {
        return rows.GetEnumerator();
    }

    /// <summary>退役したviewの行・取得組・選択索引を解放します。このviewを再表示には使いません。</summary>
    public void DisposeRealizedRows()
    {
        rows = [];
        sourceSnapshot = null;
        selectionIndexes = null;
    }

    public int Add(object value)
    {
        throw new NotSupportedException();
    }

    public void Clear()
    {
        throw new NotSupportedException();
    }

    public bool Contains(object value)
    {
        return rows.Contains(value);
    }

    public int IndexOf(object value)
    {
        if (value is not PlayHistoryRow row)
        {
            return -1;
        }
        for (int index = 0; index < rows.Count; index++)
        {
            if (ReferenceEquals(rows[index], row))
            {
                return index;
            }
        }
        return -1;
    }

    public void Insert(int index, object value)
    {
        throw new NotSupportedException();
    }

    public void Remove(object value)
    {
        throw new NotSupportedException();
    }

    public void RemoveAt(int index)
    {
        throw new NotSupportedException();
    }

    public void CopyTo(Array array, int index)
    {
        if (array == null)
        {
            throw new ArgumentNullException(nameof(array));
        }
        for (int offset = 0; offset < rows.Count; offset++)
        {
            array.SetValue(rows[offset], index + offset);
        }
    }
}
