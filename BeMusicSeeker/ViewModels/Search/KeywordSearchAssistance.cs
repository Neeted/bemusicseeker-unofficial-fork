using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Identifies a presentation section in the search assistance editor.
/// </summary>
public enum KeywordSearchPresentationSectionKind
{
    /// <summary>The pinned saved-query section.</summary>
    Favorites,
    /// <summary>The recent saved-query section.</summary>
    History,
    /// <summary>The field-name completion section.</summary>
    Fields,
    /// <summary>The finite or dynamic value completion section.</summary>
    Values,
}

/// <summary>
/// Identifies the semantic role of one immutable presentation item.
/// </summary>
public enum KeywordSearchPresentationItemKind
{
    /// <summary>A pinned saved query.</summary>
    Favorite,
    /// <summary>A recent saved query.</summary>
    History,
    /// <summary>A field-name candidate.</summary>
    Field,
    /// <summary>A field-value candidate.</summary>
    Value,
}

/// <summary>
/// Identifies actions exposed by a saved-query row.
/// </summary>
[Flags]
public enum KeywordSearchPresentationItemActions
{
    /// <summary>No row action.</summary>
    None = 0,
    /// <summary>Remove the represented query from Favorites.</summary>
    RemoveFavorite = 1,
    /// <summary>Pin the represented query in Favorites.</summary>
    AddFavorite = 2,
    /// <summary>Delete the represented query from History.</summary>
    DeleteHistory = 4,
}

/// <summary>
/// Captures the complete identity of a candidate calculation.
/// </summary>
public sealed class KeywordSearchCandidateIdentity : IEquatable<KeywordSearchCandidateIdentity>
{
    /// <summary>
    /// Creates an identity for one calculated candidate and its replacement span.
    /// </summary>
    /// <param name="text">The exact source text used for the calculation.</param>
    /// <param name="caretIndex">The caret used for the calculation.</param>
    /// <param name="context">The search context used for the calculation.</param>
    /// <param name="catalogRevision">The dynamic catalog revision used for the calculation.</param>
    /// <param name="replacementStart">The source offset covered by the candidate.</param>
    /// <param name="replacementLength">The number of source characters covered by the candidate.</param>
    internal KeywordSearchCandidateIdentity(
        string text,
        int caretIndex,
        GridKeywordSearchContext context,
        long catalogRevision,
        int replacementStart,
        int replacementLength)
    {
        Text = text ?? string.Empty;
        CaretIndex = caretIndex;
        Context = context;
        CatalogRevision = catalogRevision;
        ReplacementStart = replacementStart;
        ReplacementLength = replacementLength;
    }

    /// <summary>
    /// Gets the exact input text used to calculate the candidate.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets the exact caret position used to calculate the candidate.
    /// </summary>
    public int CaretIndex { get; }

    /// <summary>
    /// Gets the search context used to calculate the candidate.
    /// </summary>
    public GridKeywordSearchContext Context { get; }

    /// <summary>
    /// Gets the dynamic catalog revision used to calculate the candidate.
    /// </summary>
    public long CatalogRevision { get; }

    /// <summary>Gets the source offset covered by this candidate.</summary>
    internal int ReplacementStart { get; }

    /// <summary>Gets the number of source characters covered by this candidate.</summary>
    internal int ReplacementLength { get; }

    /// <summary>
    /// Compares two candidate identities by their complete editor snapshot.
    /// </summary>
    /// <param name="other">The identity to compare.</param>
    /// <returns><see langword="true"/> when both identities describe the same candidate snapshot.</returns>
    public bool Equals(KeywordSearchCandidateIdentity other)
    {
        return other != null
            && CaretIndex == other.CaretIndex
            && Context == other.Context
            && CatalogRevision == other.CatalogRevision
            && ReplacementStart == other.ReplacementStart
            && ReplacementLength == other.ReplacementLength
            && string.Equals(Text, other.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Compares this identity with another object.
    /// </summary>
    /// <param name="obj">The object to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is an equal identity.</returns>
    public override bool Equals(object obj)
        => Equals(obj as KeywordSearchCandidateIdentity);

    /// <summary>
    /// Gets a hash code for the complete candidate identity.
    /// </summary>
    public override int GetHashCode()
        => HashCode.Combine(Text, CaretIndex, Context, CatalogRevision, ReplacementStart, ReplacementLength);
}

/// <summary>
/// An immutable field, value, favorite, or history item shown by the editor.
/// </summary>
public sealed class KeywordSearchPresentationItem
{
    /// <summary>
    /// Creates one immutable presentation item.
    /// </summary>
    /// <param name="kind">The semantic item kind.</param>
    /// <param name="displayText">The display-facing text.</param>
    /// <param name="insertionText">The parser-facing insertion text.</param>
    /// <param name="query">The complete saved query, when this is a saved row.</param>
    /// <param name="identity">The immutable candidate identity.</param>
    /// <param name="actions">The row actions exposed by the item.</param>
    internal KeywordSearchPresentationItem(
        KeywordSearchPresentationItemKind kind,
        string displayText,
        string insertionText,
        string query,
        KeywordSearchCandidateIdentity identity,
        KeywordSearchPresentationItemActions actions)
    {
        Kind = kind;
        DisplayText = displayText ?? string.Empty;
        InsertionText = insertionText ?? string.Empty;
        Query = query ?? string.Empty;
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Actions = actions;
    }

    /// <summary>
    /// Gets the semantic kind of this item.
    /// </summary>
    public KeywordSearchPresentationItemKind Kind { get; }

    /// <summary>
    /// Gets the localized/display-facing candidate text.
    /// </summary>
    public string DisplayText { get; }

    /// <summary>
    /// Gets the parser-facing insertion text for field or value items.
    /// </summary>
    public string InsertionText { get; }

    /// <summary>
    /// Gets the complete saved query represented by a saved row.
    /// </summary>
    public string Query { get; }

    /// <summary>
    /// Gets the immutable candidate identity used for stale-edit detection.
    /// </summary>
    public KeywordSearchCandidateIdentity Identity { get; }

    /// <summary>
    /// Gets the row actions available without applying the row query.
    /// </summary>
    public KeywordSearchPresentationItemActions Actions { get; }

    /// <summary>
    /// Gets whether this item has a remove-favorite action.
    /// </summary>
    public bool CanRemoveFavorite => (Actions & KeywordSearchPresentationItemActions.RemoveFavorite) != 0;

    /// <summary>
    /// Gets whether this item has an add-favorite action.
    /// </summary>
    public bool CanAddFavorite => (Actions & KeywordSearchPresentationItemActions.AddFavorite) != 0;

    /// <summary>
    /// Gets whether this item has a delete-history action.
    /// </summary>
    public bool CanDeleteHistory => (Actions & KeywordSearchPresentationItemActions.DeleteHistory) != 0;

    internal bool IsSavedQuery => Kind is KeywordSearchPresentationItemKind.Favorite or KeywordSearchPresentationItemKind.History;

    internal int ReplacementStart => Identity.ReplacementStart;

    internal int ReplacementLength => Identity.ReplacementLength;
}

/// <summary>
/// An immutable section of the search assistance presentation.
/// </summary>
public sealed class KeywordSearchPresentationSection
{
    /// <summary>
    /// Creates one immutable presentation section.
    /// </summary>
    /// <param name="kind">The section kind.</param>
    /// <param name="headerText">The localized section header.</param>
    /// <param name="items">The immutable visible items.</param>
    internal KeywordSearchPresentationSection(
        KeywordSearchPresentationSectionKind kind,
        string headerText,
        IReadOnlyList<KeywordSearchPresentationItem> items)
    {
        Kind = kind;
        HeaderText = headerText ?? string.Empty;
        Items = items ?? [];
    }

    /// <summary>
    /// Gets the section kind.
    /// </summary>
    public KeywordSearchPresentationSectionKind Kind { get; }

    /// <summary>
    /// Gets the display header text, when the current resource set provides one.
    /// </summary>
    public string HeaderText { get; }

    /// <summary>
    /// Gets the immutable visible items in this section.
    /// </summary>
    public IReadOnlyList<KeywordSearchPresentationItem> Items { get; }

    /// <summary>
    /// Gets whether this section has rows and should be rendered.
    /// </summary>
    public bool IsVisible => Items.Count > 0;

    /// <summary>
    /// Gets whether the section may need its own scroll viewer.
    /// </summary>
    public bool IsScrollable => Items.Count > (Kind is KeywordSearchPresentationSectionKind.Favorites or KeywordSearchPresentationSectionKind.History ? 5 : 10);
}

/// <summary>
/// Immutable presentation state consumed by a reusable search editor.
/// </summary>
public sealed class KeywordSearchPresentationState
{
    /// <summary>
    /// Creates one immutable editor presentation snapshot.
    /// </summary>
    /// <param name="isOpen">Whether the assistance surface is visible.</param>
    /// <param name="text">The exact source text represented by the snapshot.</param>
    /// <param name="caretIndex">The caret represented by the snapshot.</param>
    /// <param name="context">The search context represented by the snapshot.</param>
    /// <param name="catalogRevision">The catalog revision represented by the snapshot.</param>
    /// <param name="sections">The visible sections in rendering order.</param>
    internal KeywordSearchPresentationState(
        bool isOpen,
        string text,
        int caretIndex,
        GridKeywordSearchContext context,
        long catalogRevision,
        IReadOnlyList<KeywordSearchPresentationSection> sections)
    {
        IsOpen = isOpen;
        Text = text ?? string.Empty;
        CaretIndex = caretIndex;
        Context = context;
        CatalogRevision = catalogRevision;
        Sections = sections ?? [];
        VisibleItems = new ReadOnlyCollection<KeywordSearchPresentationItem>(
            Sections.SelectMany(section => section.Items).ToArray());
    }

    /// <summary>
    /// Gets whether the editor should display its assistance surface.
    /// </summary>
    public bool IsOpen { get; }

    /// <summary>
    /// Gets the exact text represented by this state.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets the caret represented by this state.
    /// </summary>
    public int CaretIndex { get; }

    /// <summary>
    /// Gets the search context represented by this state.
    /// </summary>
    public GridKeywordSearchContext Context { get; }

    /// <summary>
    /// Gets the catalog revision represented by this state.
    /// </summary>
    public long CatalogRevision { get; }

    /// <summary>
    /// Gets visible sections in their rendering order.
    /// </summary>
    public IReadOnlyList<KeywordSearchPresentationSection> Sections { get; }

    /// <summary>
    /// Gets visible rows flattened in section order for keyboard navigation.
    /// </summary>
    public IReadOnlyList<KeywordSearchPresentationItem> VisibleItems { get; }
}

/// <summary>
/// Describes the result of a candidate application.
/// </summary>
public enum KeywordSearchApplyFailure
{
    /// <summary>The candidate was applied or no failure occurred.</summary>
    None,
    /// <summary>The owner is not focused.</summary>
    NotFocused,
    /// <summary>The candidate snapshot no longer matches the editor.</summary>
    StaleCandidate,
    /// <summary>The candidate kind or shape is not supported.</summary>
    UnsupportedCandidate,
    /// <summary>The saved-query adapter rejected persistence.</summary>
    PersistenceFailure,
}

/// <summary>
/// Immutable result returned when the editor tries to apply a candidate.
/// </summary>
public sealed class KeywordSearchApplyResult
{
    private KeywordSearchApplyResult(
        bool succeeded,
        string text,
        int caretIndex,
        KeywordSearchApplyFailure failure,
        Exception exception)
    {
        Succeeded = succeeded;
        Text = text ?? string.Empty;
        CaretIndex = caretIndex;
        Failure = failure;
        Exception = exception;
    }

    /// <summary>
    /// Gets whether the candidate was applied.
    /// </summary>
    public bool Succeeded { get; }

    /// <summary>
    /// Gets the resulting text when application succeeds.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets the resulting caret when application succeeds.
    /// </summary>
    public int CaretIndex { get; }

    /// <summary>
    /// Gets the explicit failure category when application is rejected.
    /// </summary>
    public KeywordSearchApplyFailure Failure { get; }

    /// <summary>
    /// Gets a persistence exception without changing the in-memory state.
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// Creates a successful application result.
    /// </summary>
    /// <param name="text">The resulting source text.</param>
    /// <param name="caretIndex">The resulting caret.</param>
    internal static KeywordSearchApplyResult Success(string text, int caretIndex)
        => new(true, text, caretIndex, KeywordSearchApplyFailure.None, null);

    /// <summary>Creates a rejected candidate result.</summary>
    /// <param name="failure">The rejection category.</param>
    /// <param name="text">The input text at rejection time.</param>
    /// <param name="caretIndex">The input caret at rejection time.</param>
    /// <param name="exception">The persistence failure, when applicable.</param>
    internal static KeywordSearchApplyResult FailureResult(
        KeywordSearchApplyFailure failure,
        string text,
        int caretIndex,
        Exception exception = null)
        => new(false, text, caretIndex, failure, exception);
}

/// <summary>
/// Owns focus-lifecycle-independent candidate calculation and immutable presentation state.
/// </summary>
internal sealed class KeywordSearchAssistanceOwner : INotifyPropertyChanged
{
    private readonly object syncRoot = new();

    private readonly KeywordSearchSavedQueryOwner savedQueryOwner;

    private GridKeywordSearchContext context;

    private KeywordSearchCatalogSnapshot catalogSnapshot;

    private string currentText = string.Empty;

    private int currentCaretIndex;

    private bool isFocused;

    private CandidateSnapshotKey? suppressedSnapshot;

    private KeywordSearchPresentationState presentation;

    /// <summary>
    /// Creates assistance state for one saved-query owner and search context.
    /// </summary>
    /// <param name="savedQueryOwner">The explicit persisted saved-query owner.</param>
    /// <param name="context">The initial search context.</param>
    /// <param name="catalogSnapshot">The initial immutable catalog snapshot.</param>
    internal KeywordSearchAssistanceOwner(
        KeywordSearchSavedQueryOwner savedQueryOwner,
        GridKeywordSearchContext context,
        KeywordSearchCatalogSnapshot catalogSnapshot = null)
    {
        this.savedQueryOwner = savedQueryOwner
            ?? throw new ArgumentNullException(nameof(savedQueryOwner));
        this.context = context;
        this.catalogSnapshot = catalogSnapshot ?? KeywordSearchCatalogSnapshot.Empty;
        presentation = ClosedPresentation();
        this.savedQueryOwner.StateChanged += SavedQueryOwnerStateChanged;
    }

    /// <summary>
    /// Raised only when immutable presentation state changes.
    /// </summary>
    internal event EventHandler PresentationChanged;

    /// <summary>
    /// Implements property notification for the reusable editor binding surface.
    /// </summary>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// Gets the persisted saved-query owner used by this assistance surface.
    /// </summary>
    internal KeywordSearchSavedQueryOwner SavedQueryOwner => savedQueryOwner;

    /// <summary>
    /// Gets the current immutable presentation.
    /// </summary>
    public KeywordSearchPresentationState Presentation
    {
        get
        {
            lock (syncRoot)
            {
                return presentation;
            }
        }
    }

    /// <summary>
    /// Gets whether the current presentation is open.
    /// </summary>
    public bool IsOpen => Presentation.IsOpen;

    /// <summary>
    /// Gets visible presentation sections.
    /// </summary>
    public IReadOnlyList<KeywordSearchPresentationSection> Sections => Presentation.Sections;

    /// <summary>
    /// Gets visible rows in keyboard-navigation order.
    /// </summary>
    public IReadOnlyList<KeywordSearchPresentationItem> VisibleItems => Presentation.VisibleItems;

    /// <summary>
    /// Gets the current context used by the owner.
    /// </summary>
    internal GridKeywordSearchContext Context
    {
        get
        {
            lock (syncRoot)
            {
                return context;
            }
        }
    }

    /// <summary>
    /// Gets the immutable catalog snapshot currently used by the owner.
    /// </summary>
    internal KeywordSearchCatalogSnapshot CatalogSnapshot
    {
        get
        {
            lock (syncRoot)
            {
                return catalogSnapshot;
            }
        }
    }

    /// <summary>
    /// Marks the editor focused and calculates assistance immediately.
    /// </summary>
    internal KeywordSearchPresentationState Focus(string text, int caretIndex)
    {
        lock (syncRoot)
        {
            isFocused = true;
            suppressedSnapshot = null;
        }
        return Refresh(text, caretIndex, ignoreSuppression: true);
    }

    /// <summary>
    /// Closes assistance on blur while retaining the text/caret snapshot for a later focus.
    /// </summary>
    internal KeywordSearchPresentationState Blur()
    {
        bool changed;
        KeywordSearchPresentationState next;
        lock (syncRoot)
        {
            isFocused = false;
            suppressedSnapshot = null;
            next = ClosedPresentation();
            changed = !PresentationEquivalent(presentation, next);
            presentation = next;
        }
        if (changed)
        {
            PublishPresentationChanged();
        }
        return next;
    }

    /// <summary>
    /// Refreshes assistance for the current focus, text, caret, context, and catalog revision.
    /// </summary>
    internal KeywordSearchPresentationState Refresh(string text, int caretIndex)
        => Refresh(text, caretIndex, ignoreSuppression: false);

    /// <summary>
    /// Updates the context and recalculates an active editor without rereading dynamic storage.
    /// </summary>
    internal KeywordSearchPresentationState UpdateContext(
        GridKeywordSearchContext nextContext,
        KeywordSearchCatalogSnapshot nextCatalogSnapshot)
    {
        lock (syncRoot)
        {
            context = nextContext;
            catalogSnapshot = nextCatalogSnapshot ?? KeywordSearchCatalogSnapshot.Empty;
            if (suppressedSnapshot.HasValue)
            {
                suppressedSnapshot = null;
            }
        }
        return RefreshCurrentSnapshot();
    }

    /// <summary>
    /// Updates only the immutable dynamic catalog revision and recalculates an active editor.
    /// </summary>
    internal KeywordSearchPresentationState UpdateCatalogSnapshot(KeywordSearchCatalogSnapshot nextCatalogSnapshot)
    {
        lock (syncRoot)
        {
            catalogSnapshot = nextCatalogSnapshot ?? KeywordSearchCatalogSnapshot.Empty;
            suppressedSnapshot = null;
        }
        return RefreshCurrentSnapshot();
    }

    /// <summary>
    /// Suppresses the current snapshot until text, caret, context, or catalog revision changes.
    /// </summary>
    internal KeywordSearchPresentationState SuppressCurrentSnapshot()
    {
        lock (syncRoot)
        {
            suppressedSnapshot = CreateCurrentKeyUnsafe();
        }
        return RefreshCurrentSnapshot();
    }

    /// <summary>
    /// Forces recalculation after an explicit Ctrl+Space-style reopen.
    /// </summary>
    internal KeywordSearchPresentationState ForceRefresh()
    {
        lock (syncRoot)
        {
            suppressedSnapshot = null;
        }
        return RefreshCurrentSnapshot(ignoreSuppression: true);
    }

    /// <summary>
    /// Applies an immutable item after validating text, caret, context, and catalog revision.
    /// </summary>
    internal KeywordSearchApplyResult TryApply(
        KeywordSearchPresentationItem item,
        string text,
        int caretIndex,
        GridKeywordSearchContext itemContext,
        long catalogRevision)
    {
        string safeText = text ?? string.Empty;
        int safeCaretIndex = Math.Max(0, Math.Min(caretIndex, safeText.Length));
        if (item == null)
        {
            return KeywordSearchApplyResult.FailureResult(
                KeywordSearchApplyFailure.UnsupportedCandidate,
                safeText,
                safeCaretIndex);
        }

        bool focused;
        GridKeywordSearchContext ownerContext;
        long ownerCatalogRevision;
        lock (syncRoot)
        {
            focused = isFocused;
            ownerContext = context;
            ownerCatalogRevision = catalogSnapshot.Revision;
        }
        if (!focused)
        {
            return KeywordSearchApplyResult.FailureResult(
                KeywordSearchApplyFailure.NotFocused,
                safeText,
                safeCaretIndex);
        }
        if (!string.Equals(item.Identity.Text, safeText, StringComparison.Ordinal)
            || item.Identity.CaretIndex != safeCaretIndex
            || item.Identity.Context != itemContext
            || item.Identity.Context != ownerContext
            || item.Identity.CatalogRevision != catalogRevision
            || item.Identity.CatalogRevision != ownerCatalogRevision)
        {
            return KeywordSearchApplyResult.FailureResult(
                KeywordSearchApplyFailure.StaleCandidate,
                safeText,
                safeCaretIndex);
        }

        if (item.IsSavedQuery)
        {
            KeywordSearchSavedQueryMutationResult historyResult = savedQueryOwner.TryCommitHistory(item.Query);
            if (!historyResult.Succeeded)
            {
                return KeywordSearchApplyResult.FailureResult(
                    KeywordSearchApplyFailure.PersistenceFailure,
                    safeText,
                    safeCaretIndex,
                    historyResult.Exception);
            }
            string savedText = item.Query;
            int savedCaret = savedText.Length;
            return KeywordSearchApplyResult.Success(savedText, savedCaret);
        }

        string appliedText = ApplyItem(item, safeText, out int appliedCaret);
        if (appliedText == null)
        {
            return KeywordSearchApplyResult.FailureResult(
                KeywordSearchApplyFailure.UnsupportedCandidate,
                safeText,
                safeCaretIndex);
        }
        return KeywordSearchApplyResult.Success(appliedText, appliedCaret);
    }

    /// <summary>
    /// Pins a saved query while keeping the text and caret untouched.
    /// </summary>
    internal KeywordSearchSavedQueryMutationResult TryAddFavorite(string query)
        => MutateSavedQuery(() => savedQueryOwner.TryAddFavorite(query));

    /// <summary>
    /// Removes a saved favorite while keeping the text and caret untouched.
    /// </summary>
    internal KeywordSearchSavedQueryMutationResult TryRemoveFavorite(string query)
        => MutateSavedQuery(() => savedQueryOwner.TryRemoveFavorite(query));

    /// <summary>
    /// Deletes a history entry while keeping an equal favorite and the input untouched.
    /// </summary>
    internal KeywordSearchSavedQueryMutationResult TryDeleteHistory(string query)
        => MutateSavedQuery(() => savedQueryOwner.TryDeleteHistory(query));

    private KeywordSearchSavedQueryMutationResult MutateSavedQuery(
        Func<KeywordSearchSavedQueryMutationResult> mutation)
    {
        KeywordSearchSavedQueryMutationResult result = mutation();
        if (result.Succeeded && result.Changed)
        {
            RefreshCurrentSnapshot();
        }
        return result;
    }

    private KeywordSearchPresentationState Refresh(
        string text,
        int caretIndex,
        bool ignoreSuppression)
    {
        lock (syncRoot)
        {
            currentText = text ?? string.Empty;
            currentCaretIndex = Math.Max(0, Math.Min(caretIndex, currentText.Length));
            if (!isFocused)
            {
                return presentation;
            }
        }
        return RefreshCurrentSnapshot(ignoreSuppression);
    }

    private KeywordSearchPresentationState RefreshCurrentSnapshot(bool ignoreSuppression = false)
    {
        KeywordSearchPresentationState next;
        bool changed;
        lock (syncRoot)
        {
            if (!isFocused)
            {
                next = ClosedPresentation();
                changed = !PresentationEquivalent(presentation, next);
                presentation = next;
            }
            else
            {
                CandidateSnapshotKey currentKey = CreateCurrentKeyUnsafe();
                if (!ignoreSuppression && suppressedSnapshot.HasValue && suppressedSnapshot.Value.Equals(currentKey))
                {
                    next = ClosedPresentation();
                }
                else
                {
                    if (suppressedSnapshot.HasValue && !suppressedSnapshot.Value.Equals(currentKey))
                    {
                        suppressedSnapshot = null;
                    }
                    next = BuildPresentationUnsafe();
                }
                changed = !PresentationEquivalent(presentation, next);
                presentation = next;
            }
        }
        if (changed)
        {
            PublishPresentationChanged();
        }
        return next;
    }

    private KeywordSearchPresentationState BuildPresentationUnsafe()
    {
        string text = currentText;
        int caretIndex = currentCaretIndex;
        GridKeywordSearchContext currentContext = context;
        long catalogRevision = catalogSnapshot.Revision;

        if (string.IsNullOrWhiteSpace(text))
        {
            List<KeywordSearchPresentationSection> emptySections = [];
            IReadOnlyList<string> favorites = savedQueryOwner.GetProjectedFavorites(currentContext);
            IReadOnlyList<string> history = savedQueryOwner.GetProjectedHistory(currentContext);
            KeywordSearchPresentationSection favoriteSection = CreateSavedSection(
                KeywordSearchPresentationSectionKind.Favorites,
                KeywordSearchPresentationItemKind.Favorite,
                favorites,
                KeywordSearchPresentationItemActions.RemoveFavorite,
                text,
                caretIndex,
                currentContext,
                catalogRevision);
            KeywordSearchPresentationSection historySection = CreateSavedSection(
                KeywordSearchPresentationSectionKind.History,
                KeywordSearchPresentationItemKind.History,
                history,
                KeywordSearchPresentationItemActions.AddFavorite | KeywordSearchPresentationItemActions.DeleteHistory,
                text,
                caretIndex,
                currentContext,
                catalogRevision);
            if (favoriteSection.Items.Count > 0)
            {
                emptySections.Add(favoriteSection);
            }
            if (historySection.Items.Count > 0)
            {
                emptySections.Add(historySection);
            }
            IReadOnlyList<KeywordSearchPresentationItem> fields = CreateFieldItems(
                text,
                caretIndex,
                currentContext,
                catalogRevision,
                fieldPrefixOnly: false);
            if (fields.Count > 0)
            {
                emptySections.Add(new KeywordSearchPresentationSection(
                    KeywordSearchPresentationSectionKind.Fields,
                    GetSectionHeaderText(KeywordSearchPresentationSectionKind.Fields),
                    fields));
            }
            return OpenOrClosedPresentation(text, caretIndex, currentContext, catalogRevision, emptySections);
        }

        if (HasUnquotedSeparatorBeforeCaret(text, caretIndex))
        {
            IReadOnlyList<KeywordSearchPresentationItem> fields = CreateFieldItems(
                text,
                caretIndex,
                currentContext,
                catalogRevision,
                fieldPrefixOnly: false);
            return OpenOrClosedPresentation(
                text,
                caretIndex,
                currentContext,
                catalogRevision,
                fields.Count == 0
                    ? []
                    : [new KeywordSearchPresentationSection(
                        KeywordSearchPresentationSectionKind.Fields,
                        GetSectionHeaderText(KeywordSearchPresentationSectionKind.Fields),
                        fields)]);
        }

        IReadOnlyList<KeywordSearchPresentationItem> fieldItems = CreateFieldItems(
            text,
            caretIndex,
            currentContext,
            catalogRevision,
            fieldPrefixOnly: true);
        if (fieldItems.Count > 0)
        {
            return OpenOrClosedPresentation(
                text,
                caretIndex,
                currentContext,
                catalogRevision,
                [new KeywordSearchPresentationSection(
                    KeywordSearchPresentationSectionKind.Fields,
                    GetSectionHeaderText(KeywordSearchPresentationSectionKind.Fields),
                    fieldItems)]);
        }

        IReadOnlyList<KeywordSearchPresentationItem> valueItems = CreateValueItems(
            text,
            caretIndex,
            currentContext,
            catalogRevision);
        if (valueItems.Count > 0)
        {
            return OpenOrClosedPresentation(
                text,
                caretIndex,
                currentContext,
                catalogRevision,
                [new KeywordSearchPresentationSection(
                    KeywordSearchPresentationSectionKind.Values,
                    GetSectionHeaderText(KeywordSearchPresentationSectionKind.Values),
                    valueItems)]);
        }

        return ClosedPresentation();
    }

    private IReadOnlyList<KeywordSearchPresentationItem> CreateFieldItems(
        string text,
        int caretIndex,
        GridKeywordSearchContext currentContext,
        long catalogRevision,
        bool fieldPrefixOnly)
    {
        TokenSpan span = FindCurrentToken(text, caretIndex);
        int safeCaretIndex = Math.Max(0, Math.Min(caretIndex, text.Length));
        if (span.Start > safeCaretIndex)
        {
            return [];
        }
        string tokenPrefix = text.Substring(span.Start, safeCaretIndex - span.Start);
        bool negated = tokenPrefix.Length > 1 && tokenPrefix[0] == '-';
        int fieldStart = span.Start + (negated ? 1 : 0);
        string fieldPrefix = text.Substring(fieldStart, safeCaretIndex - fieldStart);
        if (ContainsUnquotedBoundary(fieldPrefix)
            || (fieldPrefixOnly && fieldPrefix.Length == 0))
        {
            return [];
        }
        int colonIndex = FindUnquotedChar(fieldPrefix, ':');
        if (colonIndex >= 0)
        {
            return [];
        }
        IReadOnlyList<string> knownFields = GridKeywordSearchQuery.GetKnownFields(currentContext);
        List<KeywordSearchPresentationItem> items = [];
        foreach (string field in knownFields
            .Where(field => field.StartsWith(fieldPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(field => field, StringComparer.OrdinalIgnoreCase))
        {
            KeywordSearchCandidateIdentity identity = new(
                text,
                safeCaretIndex,
                currentContext,
                catalogRevision,
                fieldStart,
                safeCaretIndex - fieldStart);
            items.Add(new KeywordSearchPresentationItem(
                KeywordSearchPresentationItemKind.Field,
                (negated ? "-" : string.Empty) + field + ":",
                field + ":",
                string.Empty,
                identity,
                KeywordSearchPresentationItemActions.None));
        }
        return new ReadOnlyCollection<KeywordSearchPresentationItem>(items);
    }

    private IReadOnlyList<KeywordSearchPresentationItem> CreateValueItems(
        string text,
        int caretIndex,
        GridKeywordSearchContext currentContext,
        long catalogRevision)
    {
        if (currentContext == GridKeywordSearchContext.PlaylistSummary)
        {
            return [];
        }
        if (!TryGetValueFragment(text, caretIndex, currentContext, out ValueFragment fragment))
        {
            return [];
        }
        IReadOnlyList<string> values = catalogSnapshot.GetValues(currentContext, fragment.Field);
        if (values.Count == 0)
        {
            return [];
        }
        string normalizedPrefix = NormalizeValuePrefix(fragment.RawPrefix);
        List<KeywordSearchPresentationItem> items = [];
        foreach (string value in values
            .Where(value => normalizedPrefix.Length == 0
                || value.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            string insertionText = QuoteValueIfNeeded(value, fragment.IsQuoted);
            KeywordSearchCandidateIdentity identity = new(
                text,
                fragment.CaretIndex,
                currentContext,
                catalogRevision,
                fragment.ValueStart,
                fragment.ReplacementLength);
            items.Add(new KeywordSearchPresentationItem(
                KeywordSearchPresentationItemKind.Value,
                value,
                insertionText,
                string.Empty,
                identity,
                KeywordSearchPresentationItemActions.None));
        }
        return new ReadOnlyCollection<KeywordSearchPresentationItem>(items);
    }

    private KeywordSearchPresentationSection CreateSavedSection(
        KeywordSearchPresentationSectionKind sectionKind,
        KeywordSearchPresentationItemKind itemKind,
        IReadOnlyList<string> queries,
        KeywordSearchPresentationItemActions actions,
        string text,
        int caretIndex,
        GridKeywordSearchContext currentContext,
        long catalogRevision)
    {
        List<KeywordSearchPresentationItem> items = [];
        foreach (string query in queries ?? [])
        {
            KeywordSearchCandidateIdentity identity = new(
                text,
                caretIndex,
                currentContext,
                catalogRevision,
                0,
                text.Length);
            items.Add(new KeywordSearchPresentationItem(
                itemKind,
                query,
                query,
                query,
                identity,
                actions));
        }
        return new KeywordSearchPresentationSection(
            sectionKind,
            GetSectionHeaderText(sectionKind),
            new ReadOnlyCollection<KeywordSearchPresentationItem>(items));
    }

    private static string GetSectionHeaderText(KeywordSearchPresentationSectionKind sectionKind)
    {
        return sectionKind switch
        {
            KeywordSearchPresentationSectionKind.Favorites => BeMusicSeeker.Properties.Resources.Keyword_search_completion_favorites_header,
            KeywordSearchPresentationSectionKind.History => BeMusicSeeker.Properties.Resources.Keyword_search_completion_history_header,
            KeywordSearchPresentationSectionKind.Fields => BeMusicSeeker.Properties.Resources.Keyword_search_completion_fields_header,
            KeywordSearchPresentationSectionKind.Values => BeMusicSeeker.Properties.Resources.Keyword_search_completion_values_header,
            _ => string.Empty,
        } ?? string.Empty;
    }

    private KeywordSearchPresentationState OpenOrClosedPresentation(
        string text,
        int caretIndex,
        GridKeywordSearchContext currentContext,
        long catalogRevision,
        IReadOnlyList<KeywordSearchPresentationSection> sections)
    {
        IReadOnlyList<KeywordSearchPresentationSection> visibleSections = (sections ?? [])
            .Where(section => section != null && section.Items.Count > 0)
            .ToArray();
        return new KeywordSearchPresentationState(
            visibleSections.Count > 0,
            text,
            caretIndex,
            currentContext,
            catalogRevision,
            new ReadOnlyCollection<KeywordSearchPresentationSection>(visibleSections.ToArray()));
    }

    private KeywordSearchPresentationState ClosedPresentation()
    {
        return new KeywordSearchPresentationState(
            false,
            currentText,
            currentCaretIndex,
            context,
            catalogSnapshot.Revision,
            []);
    }

    private void SavedQueryOwnerStateChanged(object sender, EventArgs e)
    {
        RefreshCurrentSnapshot();
    }

    private void PublishPresentationChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Presentation)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sections)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisibleItems)));
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    private CandidateSnapshotKey CreateCurrentKeyUnsafe()
        => new(currentText, currentCaretIndex, context, catalogSnapshot.Revision);

    private static bool PresentationEquivalent(
        KeywordSearchPresentationState first,
        KeywordSearchPresentationState second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }
        if (first == null || second == null
            || first.IsOpen != second.IsOpen
            || first.CaretIndex != second.CaretIndex
            || first.Context != second.Context
            || first.CatalogRevision != second.CatalogRevision
            || !string.Equals(first.Text, second.Text, StringComparison.Ordinal)
            || first.Sections.Count != second.Sections.Count)
        {
            return false;
        }
        for (int sectionIndex = 0; sectionIndex < first.Sections.Count; sectionIndex++)
        {
            KeywordSearchPresentationSection leftSection = first.Sections[sectionIndex];
            KeywordSearchPresentationSection rightSection = second.Sections[sectionIndex];
            if (leftSection.Kind != rightSection.Kind
                || !string.Equals(leftSection.HeaderText, rightSection.HeaderText, StringComparison.Ordinal)
                || leftSection.Items.Count != rightSection.Items.Count)
            {
                return false;
            }
            for (int itemIndex = 0; itemIndex < leftSection.Items.Count; itemIndex++)
            {
                KeywordSearchPresentationItem left = leftSection.Items[itemIndex];
                KeywordSearchPresentationItem right = rightSection.Items[itemIndex];
                if (left.Kind != right.Kind
                    || left.Actions != right.Actions
                    || !string.Equals(left.DisplayText, right.DisplayText, StringComparison.Ordinal)
                    || !string.Equals(left.InsertionText, right.InsertionText, StringComparison.Ordinal)
                    || !string.Equals(left.Query, right.Query, StringComparison.Ordinal)
                    || !left.Identity.Equals(right.Identity))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool HasUnquotedSeparatorBeforeCaret(string text, int caretIndex)
    {
        text ??= string.Empty;
        int safeCaretIndex = Math.Max(0, Math.Min(caretIndex, text.Length));
        if (safeCaretIndex == 0 || !char.IsWhiteSpace(text[safeCaretIndex - 1]))
        {
            return false;
        }

        bool inQuote = false;
        bool escaping = false;
        for (int index = 0; index < safeCaretIndex - 1; index++)
        {
            char character = text[index];
            if (inQuote)
            {
                if (escaping)
                {
                    escaping = false;
                    continue;
                }
                if (character == '\\')
                {
                    escaping = true;
                    continue;
                }
                if (character == '"')
                {
                    inQuote = false;
                }
                continue;
            }
            if (character == '"')
            {
                inQuote = true;
            }
        }
        return !inQuote;
    }

    private static string ApplyItem(
        KeywordSearchPresentationItem item,
        string sourceText,
        out int caretIndex)
    {
        int start = Math.Max(0, Math.Min(item.ReplacementStart, sourceText.Length));
        int length = Math.Max(0, Math.Min(item.ReplacementLength, sourceText.Length - start));
        if (item.Kind == KeywordSearchPresentationItemKind.Value)
        {
            string prefix = sourceText.Remove(start, length).Insert(start, item.InsertionText);
            int separatorStart = start + item.InsertionText.Length;
            string suffix = prefix.Substring(separatorStart);
            int skip = 0;
            while (skip < suffix.Length && char.IsWhiteSpace(suffix[skip]))
            {
                skip++;
            }
            suffix = " " + suffix.Substring(skip);
            prefix = prefix.Substring(0, separatorStart) + suffix;
            caretIndex = separatorStart + 1;
            return prefix;
        }
        if (item.Kind != KeywordSearchPresentationItemKind.Field)
        {
            caretIndex = 0;
            return null;
        }
        string appliedText = sourceText.Remove(start, length).Insert(start, item.InsertionText);
        caretIndex = start + item.InsertionText.Length;
        return appliedText;
    }

    private static bool TryGetValueFragment(
        string text,
        int caretIndex,
        GridKeywordSearchContext currentContext,
        out ValueFragment fragment)
    {
        fragment = default;
        int safeCaretIndex = Math.Max(0, Math.Min(caretIndex, text.Length));
        TokenSpan span = FindCurrentToken(text, safeCaretIndex);
        if (span.Start >= safeCaretIndex)
        {
            return false;
        }
        string tokenPrefix = text.Substring(span.Start, safeCaretIndex - span.Start);
        bool negated = tokenPrefix.Length > 1 && tokenPrefix[0] == '-';
        int fieldOffset = negated ? 1 : 0;
        int colonIndex = FindUnquotedChar(tokenPrefix, ':');
        if (colonIndex <= fieldOffset)
        {
            return false;
        }
        string field = tokenPrefix.Substring(fieldOffset, colonIndex - fieldOffset).Trim().ToLowerInvariant();
        if (!KeywordSearchCatalog.IsValueField(currentContext, field))
        {
            return false;
        }
        int valueStart = span.Start + colonIndex + 1;
        string rawPrefix = text.Substring(valueStart, safeCaretIndex - valueStart);
        if (StartsWithRegexPrefix(rawPrefix) || ContainsUnquotedPipe(rawPrefix))
        {
            return false;
        }
        int replacementEnd = FindQuotedValueReplacementEnd(
            text,
            valueStart,
            safeCaretIndex,
            span.End);
        fragment = new ValueFragment(field, valueStart, safeCaretIndex, rawPrefix, replacementEnd - valueStart);
        return true;
    }

    private static int FindQuotedValueReplacementEnd(
        string text,
        int valueStart,
        int caretIndex,
        int tokenEnd)
    {
        int replacementEnd = caretIndex;
        if (valueStart >= text.Length || text[valueStart] != '"')
        {
            return replacementEnd;
        }

        bool escaping = false;
        int safeTokenEnd = Math.Max(valueStart + 1, Math.Min(tokenEnd, text.Length));
        for (int index = valueStart + 1; index < safeTokenEnd; index++)
        {
            char character = text[index];
            if (escaping)
            {
                escaping = false;
                continue;
            }
            if (character == '\\')
            {
                escaping = true;
                continue;
            }
            if (character == '"')
            {
                if (index >= caretIndex)
                {
                    replacementEnd = index + 1;
                }
                break;
            }
        }
        return replacementEnd;
    }

    private static string NormalizeValuePrefix(string rawPrefix)
    {
        string value = rawPrefix ?? string.Empty;
        if (value.Length == 0 || value[0] != '"')
        {
            return value.Trim();
        }
        StringBuilder builder = new(value.Length);
        bool escaping = false;
        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];
            if (escaping)
            {
                builder.Append(character is '"' or '\\' ? character : '\\');
                if (character is not ('"' or '\\'))
                {
                    builder.Append(character);
                }
                escaping = false;
                continue;
            }
            if (character == '\\')
            {
                escaping = true;
                continue;
            }
            if (character == '"')
            {
                break;
            }
            builder.Append(character);
        }
        if (escaping)
        {
            builder.Append('\\');
        }
        return builder.ToString();
    }

    private static string QuoteValueIfNeeded(string value, bool preserveQuotes = false)
    {
        string text = value ?? string.Empty;
        bool needsQuote = preserveQuotes
            || text.Length == 0
            || text.Any(character => char.IsWhiteSpace(character) || character is '"' or '\\' or '|');
        if (!needsQuote)
        {
            return text;
        }
        StringBuilder builder = new(text.Length + 2);
        builder.Append('"');
        foreach (char character in text)
        {
            if (character is '"' or '\\')
            {
                builder.Append('\\');
            }
            builder.Append(character);
        }
        builder.Append('"');
        return builder.ToString();
    }

    private static bool StartsWithRegexPrefix(string rawValue)
        => (rawValue ?? string.Empty).TrimStart().StartsWith("re:", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsUnquotedBoundary(string value)
        => ContainsUnquoted(value, ':', '"', '|', '\\');

    private static bool ContainsUnquotedPipe(string value)
        => ContainsUnquoted(value, '|');

    private static bool ContainsUnquoted(string value, params char[] targets)
    {
        bool inQuote = false;
        bool escaping = false;
        foreach (char character in value ?? string.Empty)
        {
            if (inQuote)
            {
                if (escaping)
                {
                    escaping = false;
                    continue;
                }
                if (character == '\\')
                {
                    escaping = true;
                    continue;
                }
                if (character == '"')
                {
                    inQuote = false;
                }
                continue;
            }
            if (character == '"')
            {
                inQuote = true;
                continue;
            }
            if (targets.Contains(character))
            {
                return true;
            }
        }
        return false;
    }

    private static int FindUnquotedChar(string value, char target)
    {
        bool inQuote = false;
        bool escaping = false;
        for (int index = 0; index < (value?.Length ?? 0); index++)
        {
            char character = value[index];
            if (inQuote)
            {
                if (escaping)
                {
                    escaping = false;
                    continue;
                }
                if (character == '\\')
                {
                    escaping = true;
                    continue;
                }
                if (character == '"')
                {
                    inQuote = false;
                }
                continue;
            }
            if (character == '"')
            {
                inQuote = true;
                continue;
            }
            if (character == target)
            {
                return index;
            }
        }
        return -1;
    }

    private static TokenSpan FindCurrentToken(string text, int caretIndex)
    {
        text ??= string.Empty;
        int safeCaretIndex = Math.Max(0, Math.Min(caretIndex, text.Length));
        int start = 0;
        bool inQuote = false;
        bool escaping = false;
        for (int index = 0; index < safeCaretIndex; index++)
        {
            char character = text[index];
            if (inQuote)
            {
                if (escaping)
                {
                    escaping = false;
                    continue;
                }
                if (character == '\\')
                {
                    escaping = true;
                    continue;
                }
                if (character == '"')
                {
                    inQuote = false;
                }
                continue;
            }
            if (character == '"')
            {
                inQuote = true;
                continue;
            }
            if (char.IsWhiteSpace(character) && !inQuote)
            {
                start = index + 1;
            }
        }
        int end = safeCaretIndex;
        for (; end < text.Length; end++)
        {
            char character = text[end];
            if (inQuote)
            {
                if (escaping)
                {
                    escaping = false;
                    continue;
                }
                if (character == '\\')
                {
                    escaping = true;
                    continue;
                }
                if (character == '"')
                {
                    inQuote = false;
                }
                continue;
            }
            if (character == '"')
            {
                inQuote = true;
                continue;
            }
            if (char.IsWhiteSpace(character) && !inQuote)
            {
                break;
            }
        }
        return new TokenSpan(start, end);
    }

    private readonly struct ValueFragment
    {
        internal ValueFragment(string field, int valueStart, int caretIndex, string rawPrefix, int replacementLength)
        {
            Field = field;
            ValueStart = valueStart;
            CaretIndex = caretIndex;
            RawPrefix = rawPrefix;
            ReplacementLength = replacementLength;
            IsQuoted = rawPrefix?.StartsWith("\"", StringComparison.Ordinal) == true;
        }

        internal string Field { get; }

        internal int ValueStart { get; }

        internal int CaretIndex { get; }

        internal string RawPrefix { get; }

        internal int ReplacementLength { get; }

        internal bool IsQuoted { get; }
    }

    private readonly struct TokenSpan
    {
        internal TokenSpan(int start, int end)
        {
            Start = start;
            End = end;
        }

        internal int Start { get; }

        internal int End { get; }
    }

    private readonly struct CandidateSnapshotKey : IEquatable<CandidateSnapshotKey>
    {
        internal CandidateSnapshotKey(
            string text,
            int caretIndex,
            GridKeywordSearchContext context,
            long catalogRevision)
        {
            Text = text ?? string.Empty;
            CaretIndex = caretIndex;
            Context = context;
            CatalogRevision = catalogRevision;
        }

        private string Text { get; }

        private int CaretIndex { get; }

        private GridKeywordSearchContext Context { get; }

        private long CatalogRevision { get; }

        public bool Equals(CandidateSnapshotKey other)
            => CaretIndex == other.CaretIndex
                && Context == other.Context
                && CatalogRevision == other.CatalogRevision
                && string.Equals(Text, other.Text, StringComparison.Ordinal);

        public override bool Equals(object obj)
            => obj is CandidateSnapshotKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(Text, CaretIndex, Context, CatalogRevision);
    }
}
