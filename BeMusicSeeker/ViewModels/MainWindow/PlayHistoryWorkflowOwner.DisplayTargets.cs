using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlayHistoryWorkflowOwner
{
    private readonly ObservableCollection<PlayHistoryDisplayTargetItem> displayTargets = [];

    private readonly List<PlayHistoryDisplayTargetSet> displayTargetSets = [];

    private PlayHistoryDisplayTargetItem selectedDisplayTarget;

    private string preferredDisplayTargetIdentity = PlayHistoryDisplayTargetItem.All.Identity;

    private bool isRefreshingDisplayTargets;

    private Action<string> persistDisplayTargetIdentity;

    /// <summary>
    /// Gets the display-target catalog used by the play-history toolbar.
    /// </summary>
    public ObservableCollection<PlayHistoryDisplayTargetItem> DisplayTargets => displayTargets;

    /// <summary>
    /// Gets or sets the selected play-history display target.
    /// </summary>
    public PlayHistoryDisplayTargetItem SelectedDisplayTarget
    {
        get => selectedDisplayTarget ?? PlayHistoryDisplayTargetItem.All;
        set
        {
            PlayHistoryDisplayTargetItem next = value ?? PlayHistoryDisplayTargetItem.All;
            if (!ReferenceEquals(selectedDisplayTarget, next)
                && !string.Equals(selectedDisplayTarget?.Identity, next.Identity, StringComparison.Ordinal))
            {
                ApplyDisplayTargetSelection(next, persistIdentity: true, queueRefreshWhenSelectionChanges: true);
            }
        }
    }

    /// <summary>
    /// Gets or sets the selected play-history display-target identity.
    /// </summary>
    public string SelectedDisplayTargetIdentity
    {
        get => SelectedDisplayTarget.Identity;
        set
        {
            if (isRefreshingDisplayTargets || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string identity = NormalizeDisplayTargetIdentity(value);
            PlayHistoryDisplayTargetItem selected = FindDisplayTarget(identity);
            if (selected != null)
            {
                ApplyDisplayTargetSelection(selected, persistIdentity: true, queueRefreshWhenSelectionChanges: true);
            }
        }
    }

    /// <summary>
    /// Requests the root composition layer to queue a display-target read refresh.
    /// The owner has already advanced its display-target revision before raising this event.
    /// </summary>
    internal event EventHandler DisplayTargetRefreshRequested;

    internal void ConfigureDisplayTargetPersistence(Action<string> persistIdentity)
    {
        persistDisplayTargetIdentity = persistIdentity ?? throw new ArgumentNullException(nameof(persistIdentity));
    }

    internal void RestoreDisplayTargetIdentity(string identity)
    {
        preferredDisplayTargetIdentity = NormalizeDisplayTargetIdentity(identity);
    }

    internal void ReplaceDisplayTargetSets(
        IEnumerable<PlayHistoryDisplayTargetSet> targetSets,
        IEnumerable<BMSTable> tables,
        bool queueRefreshWhenSelectionChanges)
    {
        displayTargetSets.Clear();
        displayTargetSets.AddRange(PlayHistoryDisplayTargetSetStore.Deserialize(
            PlayHistoryDisplayTargetSetStore.Serialize(targetSets)));
        ReplaceDisplayTargetCatalog(tables, queueRefreshWhenSelectionChanges);
    }

    internal void ReplaceDisplayTargetSetsFromSettings(
        string serializedTargetSets,
        IEnumerable<BMSTable> tables,
        bool queueRefreshWhenSelectionChanges)
    {
        displayTargetSets.Clear();
        displayTargetSets.AddRange(PlayHistoryDisplayTargetSetStore.Deserialize(serializedTargetSets));
        ReplaceDisplayTargetCatalog(tables, queueRefreshWhenSelectionChanges);
    }

    internal void ReplaceDisplayTargetCatalog(
        IEnumerable<BMSTable> tables,
        bool queueRefreshWhenSelectionChanges = true)
    {
        PlayHistoryDisplayTargetItem previousSelected = SelectedDisplayTarget;
        string selectedIdentity = NormalizeDisplayTargetIdentity(preferredDisplayTargetIdentity);
        List<PlayHistoryDisplayTargetItem> nextItems = [PlayHistoryDisplayTargetItem.All];
        nextItems.AddRange(displayTargetSets.Select(PlayHistoryDisplayTargetItem.FromTargetSet));
        nextItems.AddRange(displayTargetSets.Select(PlayHistoryDisplayTargetItem.FromTargetSetProjectionOnly));
        nextItems.AddRange((tables ?? Enumerable.Empty<BMSTable>())
            .Where(table => table != null)
            .OrderBy(table => table.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(table => table.symbol ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(PlayHistoryDisplayTargetItem.FromPlaylist));

        isRefreshingDisplayTargets = true;
        try
        {
            displayTargets.Clear();
            foreach (PlayHistoryDisplayTargetItem item in nextItems)
            {
                displayTargets.Add(item);
            }

            PlayHistoryDisplayTargetItem selected = nextItems.FirstOrDefault(item =>
                string.Equals(item.Identity, selectedIdentity, StringComparison.Ordinal))
                ?? PlayHistoryDisplayTargetItem.All;
            ApplyDisplayTargetSelection(
                selected,
                persistIdentity: false,
                queueRefreshWhenSelectionChanges
                    && (previousSelected.UsesProjection || selected.UsesProjection));
            RaisePropertyChanged(nameof(DisplayTargets));
            RaisePropertyChanged(nameof(SelectedDisplayTarget));
            RaisePropertyChanged(nameof(SelectedDisplayTargetIdentity));
        }
        finally
        {
            isRefreshingDisplayTargets = false;
        }
    }

    private static string NormalizeDisplayTargetIdentity(string identity)
    {
        return string.IsNullOrWhiteSpace(identity)
            ? PlayHistoryDisplayTargetItem.All.Identity
            : identity.Trim();
    }

    private PlayHistoryDisplayTargetItem FindDisplayTarget(string identity)
    {
        string normalizedIdentity = NormalizeDisplayTargetIdentity(identity);
        return displayTargets.FirstOrDefault(item =>
            string.Equals(item.Identity, normalizedIdentity, StringComparison.Ordinal));
    }

    private void ApplyDisplayTargetSelection(
        PlayHistoryDisplayTargetItem selected,
        bool persistIdentity,
        bool queueRefreshWhenSelectionChanges)
    {
        PlayHistoryDisplayTargetItem previousSelected = SelectedDisplayTarget;
        PlayHistoryDisplayTargetItem next = selected ?? PlayHistoryDisplayTargetItem.All;
        string nextIdentity = NormalizeDisplayTargetIdentity(next.Identity);
        bool identityChanged = !string.Equals(previousSelected.Identity, nextIdentity, StringComparison.Ordinal);
        bool selectedItemChanged = !ReferenceEquals(selectedDisplayTarget, next);
        if (persistIdentity)
        {
            if (persistDisplayTargetIdentity == null)
            {
                throw new InvalidOperationException("Display-target persistence must be configured before selecting a target.");
            }
            preferredDisplayTargetIdentity = nextIdentity;
            persistDisplayTargetIdentity(nextIdentity);
        }

        if (identityChanged || selectedItemChanged)
        {
            lock (PresentationState.SyncRoot)
            {
                if (selectedItemChanged)
                {
                    selectedDisplayTarget = next;
                }
                AdvanceDisplayTargetRevision(nextIdentity);
            }
        }

        if (selectedItemChanged)
        {
            RaisePropertyChanged(nameof(SelectedDisplayTarget));
            RaisePropertyChanged(nameof(SelectedDisplayTargetIdentity));
        }
        else if (identityChanged)
        {
            RaisePropertyChanged(nameof(SelectedDisplayTargetIdentity));
        }

        if ((identityChanged || selectedItemChanged)
            && queueRefreshWhenSelectionChanges
            && (previousSelected.UsesProjection || next.UsesProjection))
        {
            DisplayTargetRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
