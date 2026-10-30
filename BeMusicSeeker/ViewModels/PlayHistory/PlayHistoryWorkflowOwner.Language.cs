using System.ComponentModel;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlayHistoryWorkflowOwner
{
    private MainChartListViewModel languageChartList;

    /// <summary>画面の購読寿命に従い、採用済み集計の言語変更だけを接続します。</summary>
    internal void AttachLanguagePresentation(MainChartListViewModel chartList)
    {
        DetachLanguagePresentation();
        languageChartList = chartList;
        ResourceService.Current.PropertyChanged += LanguageResourcesChanged;
        RefreshLanguagePresentation();
    }

    /// <summary>画面の退役後に表示担当を残さないよう購読を解除します。</summary>
    internal void DetachLanguagePresentation()
    {
        ResourceService.Current.PropertyChanged -= LanguageResourcesChanged;
        languageChartList = null;
    }

    private void LanguageResourcesChanged(object sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ResourceService.Resources)) { RefreshLanguagePresentation(); }
    }

    /// <summary>要求・日時境界・選択・検索集合を変更せず、採用した終端の小集計を現在言語で再整形します。</summary>
    internal void RefreshLanguagePresentation()
    {
        RefreshDisplayTargetLabels();
        bool cardsChanged = false;
        bool diagnosticChanged = false;
        string summaryText = null;
        lock (PresentationState.SyncRoot)
        {
            PlayHistoryAdoptedSummary adopted = PresentationState.AdoptedSummary;
            if (adopted != null)
            {
                string diagnostic = adopted.Failed ? BeMusicSeeker.Properties.Resources.Play_history_read_failed
                    : PlayHistoryPresentationState.FormatDiagnosticSummary(adopted.Diagnostics);
                cardsChanged = PresentationState.SetSummaryCards(adopted.Failed ? []
                    : PlayHistoryPresentationState.CreateSummaryCards(adopted.Summary, adopted.Provider,
                        SnapshotSummaryFilterKeys(adopted.Provider)));
                diagnosticChanged = PresentationState.SetDiagnosticText(diagnostic);
                summaryText = adopted.Failed ? diagnostic
                    : PlayHistoryPresentationState.FormatGridSummaryText(adopted.Period,
                        adopted.Summary, adopted.Diagnostics, diagnostic);
            }
        }
        if (cardsChanged) { RaisePropertyChanged(nameof(SummaryCards)); }
        if (diagnosticChanged) { RaisePropertyChanged(nameof(SummaryDiagnosticText)); }
        if (IsViewActive && summaryText != null && languageChartList != null)
        { languageChartList.SummaryText = summaryText; }
    }

    private void RefreshDisplayTargetLabels()
    {
        // 翻訳だけのオブジェクト交換では、待機中の投影を失効させる版更新を行いません。
        isRefreshingDisplayTargets = true;
        try
        {
            for (int index = 0; index < displayTargets.Count; index++)
            {
                PlayHistoryDisplayTargetItem current = displayTargets[index];
                PlayHistoryDisplayTargetItem next = current.Kind switch
                {
                    PlayHistoryDisplayTargetKind.All => PlayHistoryDisplayTargetItem.All,
                    PlayHistoryDisplayTargetKind.Playlist => PlayHistoryDisplayTargetItem.FromPlaylist(current.Table),
                    _ => current.Mode == PlayHistoryDisplayTargetMode.ProjectOnly
                        ? PlayHistoryDisplayTargetItem.FromTargetSetProjectionOnly(current.TargetSet)
                        : PlayHistoryDisplayTargetItem.FromTargetSet(current.TargetSet)
                };
                displayTargets[index] = next;
                if (ReferenceEquals(selectedDisplayTarget, current)) { selectedDisplayTarget = next; }
            }
            RaisePropertyChanged(nameof(DisplayTargets));
            RaisePropertyChanged(nameof(SelectedDisplayTarget));
        }
        finally { isRefreshingDisplayTargets = false; }
    }
}
