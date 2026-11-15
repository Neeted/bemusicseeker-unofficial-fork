using System.Collections.Generic;

namespace BeMusicSeeker.Views;

/// <summary>表の選択集合・主行・範囲基点を管理します。</summary>
/// <remarks>
/// 選択集合は、重ならず隣接もしない昇順の添字区間として保持します。全選択や広い範囲選択でも行数や選択行数に比例する処理・割当を持たず、
/// 区間の数に比例する処理だけで済ませるためです。識別からの復元だけは入力件数に比例します。
/// 選択操作のbool戻り値は集合または主行の変化を表し、表の選択通知に使います。基点だけの更新は通知せず、次の範囲選択へ反映します。
/// </remarks>
internal sealed class CustomTableSelectionModel
{
    /// <summary>両端を含む添字区間です。</summary>
    private readonly record struct Interval(int Start, int End)
    {
        internal int Length => End - Start + 1;
    }

    private List<Interval> intervals = [];
    private int selectedCount;
    private int itemCount;

    internal int CurrentIndex { get; private set; } = -1;

    internal int AnchorIndex { get; private set; } = -1;

    /// <summary>選択行数です。行そのものがnullでも、モデル上の添字として数えます。</summary>
    internal int SelectedCount => selectedCount;

    /// <summary>選択された添字を、重複なく昇順に列挙します。対象行が必要な境界だけが呼びます。</summary>
    internal IEnumerable<int> SelectedIndices
    {
        get
        {
            // 列挙中に集合が変わらない呼出し側だけを想定し、区間の複製を避ける。
            List<Interval> snapshot = intervals;
            for (int i = 0; i < snapshot.Count; i++)
            {
                Interval interval = snapshot[i];
                for (int index = interval.Start; index <= interval.End; index++)
                {
                    yield return index;
                }
            }
        }
    }

    internal void SetItemCount(int count)
    {
        itemCount = count < 0 ? 0 : count;
        CoerceToItemCount();
    }

    internal bool IsSelected(int index)
    {
        return FindContaining(index) >= 0;
    }

    /// <summary>指定行だけを選び主行と基点を揃え、集合または主行の変化を返します。無効行は選択を解除します。</summary>
    internal bool SelectSingle(int index)
    {
        if (!IsValidIndex(index))
        {
            return Clear();
        }
        bool changed = selectedCount != 1 || intervals[0].Start != index || CurrentIndex != index;
        SetSingleInterval(index, index);
        CurrentIndex = index;
        AnchorIndex = index;
        return changed;
    }

    internal bool Toggle(int index)
    {
        if (!IsValidIndex(index))
        {
            return false;
        }
        int containing = FindContaining(index);
        if (containing >= 0)
        {
            RemoveIndexFrom(containing, index);
            CurrentIndex = selectedCount == 0 ? -1 : intervals[^1].End;
        }
        else
        {
            AddIndex(index);
            CurrentIndex = index;
        }
        AnchorIndex = index;
        return true;
    }

    /// <summary>基点から指定行までを選び、集合または主行の変化を返します。基点がなければ指定行を範囲の起点にし、無効行は選択を解除します。</summary>
    internal bool SelectRange(int index)
    {
        if (!IsValidIndex(index))
        {
            return Clear();
        }
        int anchor = IsValidIndex(AnchorIndex) ? AnchorIndex : index;
        int start = anchor < index ? anchor : index;
        int end = anchor < index ? index : anchor;
        bool sameSet = intervals.Count == 1 && intervals[0].Start == start && intervals[0].End == end;
        int previousCurrentIndex = CurrentIndex;
        SetSingleInterval(start, end);
        CurrentIndex = index;
        return !sameSet || previousCurrentIndex != CurrentIndex;
    }

    /// <summary>全行を選び主行と基点を先頭へ揃え、集合または主行の変化を返します。空の表は選択を解除します。</summary>
    internal bool SelectAll()
    {
        if (itemCount <= 0)
        {
            return Clear();
        }
        bool changed = selectedCount != itemCount || CurrentIndex != 0;
        SetSingleInterval(0, itemCount - 1);
        CurrentIndex = 0;
        AnchorIndex = 0;
        return changed;
    }

    internal bool SelectIndices(IEnumerable<int> indices, int currentIndex)
        => RestoreIndices(indices, currentIndex, -1);

    /// <summary>識別から復元した選択・主行・基点を採用し、集合または主行の変化を返します。主行消失時は残選択の先頭、基点消失時は主行を使います。</summary>
    internal bool RestoreIndices(IEnumerable<int> indices, int currentIndex, int anchorIndex)
    {
        List<int> valid = [];
        foreach (int index in indices ?? [])
        {
            if (IsValidIndex(index))
            {
                valid.Add(index);
            }
        }
        if (valid.Count == 0)
        {
            return Clear();
        }
        valid.Sort();
        List<Interval> next = [];
        int nextCount = 0;
        int previous = -1;
        foreach (int index in valid)
        {
            if (index == previous)
            {
                continue;
            }
            nextCount++;
            previous = index;
            int last = next.Count - 1;
            if (last >= 0 && next[last].End + 1 == index)
            {
                next[last] = next[last] with { End = index };
            }
            else
            {
                next.Add(new Interval(index, index));
            }
        }
        int nextCurrentIndex = ContainsIn(next, currentIndex) ? currentIndex : next[0].Start;
        int nextAnchorIndex = IsValidIndex(anchorIndex) ? anchorIndex : nextCurrentIndex;
        bool changed = !SameIntervals(intervals, next) || CurrentIndex != nextCurrentIndex;
        intervals = next;
        selectedCount = nextCount;
        CurrentIndex = nextCurrentIndex;
        AnchorIndex = nextAnchorIndex;
        return changed;
    }

    internal bool MoveCurrent(int delta)
    {
        int nextIndex = GetMovedIndex(delta);
        return SelectSingle(nextIndex);
    }

    internal bool ExtendRangeBy(int delta)
    {
        int nextIndex = GetMovedIndex(delta);
        return SelectRange(nextIndex);
    }

    internal bool SelectForRightClick(int index)
    {
        if (!IsValidIndex(index))
        {
            return Clear();
        }
        if (IsSelected(index))
        {
            bool changed = CurrentIndex != index;
            CurrentIndex = index;
            return changed;
        }
        return SelectSingle(index);
    }

    internal bool SelectForLeftMouseDownDragCandidate(int index)
    {
        if (!IsValidIndex(index))
        {
            return Clear();
        }
        if (selectedCount > 1 && IsSelected(index))
        {
            bool changed = CurrentIndex != index;
            CurrentIndex = index;
            return changed;
        }
        return SelectSingle(index);
    }

    internal bool SyncCurrentIndex(int index)
    {
        return SelectSingle(index);
    }

    /// <summary>選択集合・主行・基点を解除し、集合または主行の変化を返します。</summary>
    internal bool Clear()
    {
        bool changed = selectedCount != 0 || CurrentIndex != -1;
        intervals.Clear();
        selectedCount = 0;
        CurrentIndex = -1;
        AnchorIndex = -1;
        return changed;
    }

    /// <summary>行数から外れた選択を除き、主行と基点を補正して集合または主行の変化を返します。基点だけの補正は変更に含めません。</summary>
    internal bool CoerceToItemCount()
    {
        bool changed = false;
        int firstRemoved = intervals.Count;
        while (firstRemoved > 0 && intervals[firstRemoved - 1].Start >= itemCount)
        {
            firstRemoved--;
            selectedCount -= intervals[firstRemoved].Length;
            changed = true;
        }
        if (firstRemoved < intervals.Count)
        {
            intervals.RemoveRange(firstRemoved, intervals.Count - firstRemoved);
        }
        if (intervals.Count > 0 && intervals[^1].End >= itemCount)
        {
            Interval last = intervals[^1];
            selectedCount -= last.End - (itemCount - 1);
            intervals[^1] = last with { End = itemCount - 1 };
            changed = true;
        }
        if (!IsValidIndex(CurrentIndex))
        {
            int nextCurrentIndex = selectedCount == 0 ? -1 : intervals[^1].End;
            changed |= CurrentIndex != nextCurrentIndex;
            CurrentIndex = nextCurrentIndex;
        }
        if (!IsValidIndex(AnchorIndex))
        {
            AnchorIndex = CurrentIndex;
        }
        return changed;
    }

    private bool IsValidIndex(int index)
    {
        return index >= 0 && index < itemCount;
    }

    private int GetMovedIndex(int delta)
    {
        if (itemCount <= 0)
        {
            return -1;
        }
        int current = IsValidIndex(CurrentIndex) ? CurrentIndex : (delta >= 0 ? -1 : itemCount);
        int nextIndex = current + delta;
        if (nextIndex < 0)
        {
            return 0;
        }
        if (nextIndex >= itemCount)
        {
            return itemCount - 1;
        }
        return nextIndex;
    }

    private void SetSingleInterval(int start, int end)
    {
        intervals.Clear();
        intervals.Add(new Interval(start, end));
        selectedCount = end - start + 1;
    }

    /// <summary>指定添字を含む区間の位置を返します。含まれなければ-1です。</summary>
    private int FindContaining(int index)
    {
        int candidate = FindFirstStartingAfter(intervals, index) - 1;
        return candidate >= 0 && intervals[candidate].End >= index ? candidate : -1;
    }

    private static bool ContainsIn(List<Interval> list, int index)
    {
        int candidate = FindFirstStartingAfter(list, index) - 1;
        return candidate >= 0 && list[candidate].End >= index;
    }

    /// <summary>開始が指定添字より大きい最初の区間の位置を二分探索で返します。なければ区間数です。</summary>
    private static int FindFirstStartingAfter(List<Interval> list, int index)
    {
        int low = 0;
        int high = list.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (list[middle].Start > index)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }
        return low;
    }

    private void RemoveIndexFrom(int position, int index)
    {
        Interval interval = intervals[position];
        if (interval.Start == interval.End)
        {
            intervals.RemoveAt(position);
        }
        else if (index == interval.Start)
        {
            intervals[position] = interval with { Start = index + 1 };
        }
        else if (index == interval.End)
        {
            intervals[position] = interval with { End = index - 1 };
        }
        else
        {
            intervals[position] = interval with { End = index - 1 };
            intervals.Insert(position + 1, new Interval(index + 1, interval.End));
        }
        selectedCount--;
    }

    private void AddIndex(int index)
    {
        int next = FindFirstStartingAfter(intervals, index);
        int previous = next - 1;
        bool joinPrevious = previous >= 0 && intervals[previous].End + 1 == index;
        bool joinNext = next < intervals.Count && intervals[next].Start == index + 1;
        if (joinPrevious && joinNext)
        {
            intervals[previous] = intervals[previous] with { End = intervals[next].End };
            intervals.RemoveAt(next);
        }
        else if (joinPrevious)
        {
            intervals[previous] = intervals[previous] with { End = index };
        }
        else if (joinNext)
        {
            intervals[next] = intervals[next] with { Start = index };
        }
        else
        {
            intervals.Insert(next, new Interval(index, index));
        }
        selectedCount++;
    }

    private static bool SameIntervals(List<Interval> left, List<Interval> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }
        return true;
    }
}
