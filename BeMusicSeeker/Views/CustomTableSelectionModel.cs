using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Views;

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

    internal bool SelectSingle(int index)
    {
        if (!IsValidIndex(index))
        {
            return Clear();
        }
        bool changed = selectedIndices.Count != 1 || !selectedIndices.Contains(index) || CurrentIndex != index || AnchorIndex != index;
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
        selectedIndices.Clear();
        for (int i = start; i <= end; i++)
        {
            selectedIndices.Add(i);
        }
        CurrentIndex = index;
        return !before.SetEquals(selectedIndices);
    }

    internal bool SelectAll()
    {
        if (itemCount <= 0)
        {
            return Clear();
        }
        bool changed = selectedIndices.Count != itemCount || CurrentIndex != 0 || AnchorIndex != 0;
        selectedIndices.Clear();
        for (int i = 0; i < itemCount; i++)
        {
            selectedIndices.Add(i);
        }
        CurrentIndex = 0;
        AnchorIndex = 0;
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

    internal bool Clear()
    {
        if (selectedIndices.Count == 0 && CurrentIndex == -1 && AnchorIndex == -1)
        {
            return false;
        }
        selectedIndices.Clear();
        CurrentIndex = -1;
        AnchorIndex = -1;
        return true;
    }

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
            CurrentIndex = selectedIndices.Count == 0 ? -1 : selectedIndices.Last();
            changed = true;
        }
        if (!IsValidIndex(AnchorIndex))
        {
            AnchorIndex = CurrentIndex;
            changed = true;
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
