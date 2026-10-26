using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Views;

/// <summary>表の選択集合・主行・範囲基点を管理します。</summary>
/// <remarks>選択操作のbool戻り値は集合または主行の変化を表し、表の選択通知に使います。基点だけの更新は通知せず、次の範囲選択へ反映します。</remarks>
internal sealed class CustomTableSelectionModel
{
    private readonly SortedSet<int> selectedIndices = [];
    private int itemCount;

    internal int CurrentIndex { get; private set; } = -1;

    internal int AnchorIndex { get; private set; } = -1;

    internal IReadOnlyCollection<int> SelectedIndices => selectedIndices;

    internal void SetItemCount(int count)
    {
        itemCount = count < 0 ? 0 : count;
        CoerceToItemCount();
    }

    internal bool IsSelected(int index)
    {
        return selectedIndices.Contains(index);
    }

    /// <summary>指定行だけを選び主行と基点を揃え、集合または主行の変化を返します。無効行は選択を解除します。</summary>
    internal bool SelectSingle(int index)
    {
        if (!IsValidIndex(index))
        {
            return Clear();
        }
        bool changed = selectedIndices.Count != 1 || !selectedIndices.Contains(index) || CurrentIndex != index;
        selectedIndices.Clear();
        selectedIndices.Add(index);
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
        bool changed;
        if (selectedIndices.Contains(index))
        {
            selectedIndices.Remove(index);
            CurrentIndex = selectedIndices.Count == 0 ? -1 : selectedIndices.Last();
            changed = true;
        }
        else
        {
            selectedIndices.Add(index);
            CurrentIndex = index;
            changed = true;
        }
        AnchorIndex = index;
        return changed;
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
        var before = new HashSet<int>(selectedIndices);
        int previousCurrentIndex = CurrentIndex;
        selectedIndices.Clear();
        for (int i = start; i <= end; i++)
        {
            selectedIndices.Add(i);
        }
        CurrentIndex = index;
        return !before.SetEquals(selectedIndices) || previousCurrentIndex != CurrentIndex;
    }

    /// <summary>全行を選び主行と基点を先頭へ揃え、集合または主行の変化を返します。空の表は選択を解除します。</summary>
    internal bool SelectAll()
    {
        if (itemCount <= 0)
        {
            return Clear();
        }
        bool changed = selectedIndices.Count != itemCount || CurrentIndex != 0;
        selectedIndices.Clear();
        for (int i = 0; i < itemCount; i++)
        {
            selectedIndices.Add(i);
        }
        CurrentIndex = 0;
        AnchorIndex = 0;
        return changed;
    }

    internal bool SelectIndices(IEnumerable<int> indices, int currentIndex)
        => RestoreIndices(indices, currentIndex, -1);

    /// <summary>識別から復元した選択・主行・基点を採用し、集合または主行の変化を返します。主行消失時は残選択の先頭、基点消失時は主行を使います。</summary>
    internal bool RestoreIndices(IEnumerable<int> indices, int currentIndex, int anchorIndex)
    {
        var nextIndices = new SortedSet<int>((indices ?? []).Where(IsValidIndex));
        if (nextIndices.Count == 0)
        {
            return Clear();
        }
        int nextCurrentIndex = nextIndices.Contains(currentIndex) ? currentIndex : nextIndices.First();
        int nextAnchorIndex = IsValidIndex(anchorIndex) ? anchorIndex : nextCurrentIndex;
        bool changed = !selectedIndices.SetEquals(nextIndices) || CurrentIndex != nextCurrentIndex;
        selectedIndices.Clear();
        foreach (int index in nextIndices)
        {
            selectedIndices.Add(index);
        }
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
        if (selectedIndices.Contains(index))
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
        if (selectedIndices.Count > 1 && selectedIndices.Contains(index))
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
        bool changed = selectedIndices.Count != 0 || CurrentIndex != -1;
        selectedIndices.Clear();
        CurrentIndex = -1;
        AnchorIndex = -1;
        return changed;
    }

    /// <summary>行数から外れた選択を除き、主行と基点を補正して集合または主行の変化を返します。基点だけの補正は変更に含めません。</summary>
    internal bool CoerceToItemCount()
    {
        int[] outOfRange = [.. selectedIndices.Where(index => !IsValidIndex(index))];
        foreach (int index in outOfRange)
        {
            selectedIndices.Remove(index);
        }
        bool changed = outOfRange.Length > 0;
        if (!IsValidIndex(CurrentIndex))
        {
            int nextCurrentIndex = selectedIndices.Count == 0 ? -1 : selectedIndices.Last();
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
}
