using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>
/// Provides the selected-chart feature terminals used by compiled table context-menu routes.
/// </summary>
internal sealed class MainWindowSelectedChartContextMenuTerminals
{
    internal MainWindowSelectedChartContextMenuTerminals(
        MainWindowSelectedChartMutationTerminal selectedChartMutation,
        MainWindowSelectedChartResourceHealthTerminal selectedChartResourceHealth,
        MainWindowSelectedChartAudioConversionTerminal selectedChartAudioConversion,
        MainWindowChartInfoParseFailureRemovalTerminal chartInfoParseFailureRemoval,
        MainWindowRankingCacheTerminal rankingCache,
        MainWindowScoreViewerTerminal scoreViewer,
        MainWindowSelectedChartExternalActionsTerminal selectedChartExternalActions)
    {
        SelectedChartMutation = selectedChartMutation ?? throw new ArgumentNullException(nameof(selectedChartMutation));
        SelectedChartResourceHealth = selectedChartResourceHealth ?? throw new ArgumentNullException(nameof(selectedChartResourceHealth));
        SelectedChartAudioConversion = selectedChartAudioConversion ?? throw new ArgumentNullException(nameof(selectedChartAudioConversion));
        ChartInfoParseFailureRemoval = chartInfoParseFailureRemoval ?? throw new ArgumentNullException(nameof(chartInfoParseFailureRemoval));
        RankingCache = rankingCache ?? throw new ArgumentNullException(nameof(rankingCache));
        ScoreViewer = scoreViewer ?? throw new ArgumentNullException(nameof(scoreViewer));
        SelectedChartExternalActions = selectedChartExternalActions ?? throw new ArgumentNullException(nameof(selectedChartExternalActions));
    }

    internal MainWindowSelectedChartMutationTerminal SelectedChartMutation { get; }

    internal MainWindowSelectedChartResourceHealthTerminal SelectedChartResourceHealth { get; }

    internal MainWindowSelectedChartAudioConversionTerminal SelectedChartAudioConversion { get; }

    internal MainWindowChartInfoParseFailureRemovalTerminal ChartInfoParseFailureRemoval { get; }

    internal MainWindowRankingCacheTerminal RankingCache { get; }

    internal MainWindowScoreViewerTerminal ScoreViewer { get; }

    internal MainWindowSelectedChartExternalActionsTerminal SelectedChartExternalActions { get; }

    internal static MainWindowSelectedChartContextMenuTerminals Create(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new MainWindowSelectedChartContextMenuTerminals(
            new MainWindowSelectedChartMutationTerminal(
                viewModel.SelectedChartMutations.DeleteAsync,
                viewModel.SelectedChartMutations.RenameInvalidExtensionsAsync,
                viewModel.SelectedChartMutations.MoveAsync,
                viewModel.SelectedChartMutations.ApplyEncoding),
            new MainWindowSelectedChartResourceHealthTerminal(
                viewModel.SelectedChartResourceHealth.RescanAsync,
                viewModel.SelectedChartResourceHealth.SetWarningsIgnored),
            new MainWindowSelectedChartAudioConversionTerminal(
                viewModel.SelectedChartAudioConversion.RunAsync),
            new MainWindowChartInfoParseFailureRemovalTerminal(
                viewModel.ChartInfoParseFailureRemoval.BeginRemove),
            new MainWindowRankingCacheTerminal(
                viewModel.RankingCacheDownloadWorkflow.HasRankingTarget,
                viewModel.RankingCacheDownloadWorkflow.CanRequestRanking,
                viewModel.RankingCacheDownloadWorkflow.Request),
            new MainWindowScoreViewerTerminal(
                viewModel.ScoreViewerRegistration.HasScoreViewerTarget,
                viewModel.ScoreViewerRegistration.CanRegisterScoreViewer,
                viewModel.ScoreViewerRegistration.RunAsync,
                (targets, openSingleViewerOnSuccess, logName) =>
                    viewModel.ScoreViewerRegistration.RunAsync(
                        targets,
                        openSingleViewerOnSuccess,
                        logName)),
            new MainWindowSelectedChartExternalActionsTerminal(
                viewModel.SelectedChartExternalActions.CanExecute,
                viewModel.SelectedChartExternalActions.Execute,
                viewModel.SelectedChartExternalActions.CanQueryRelatedDocuments,
                viewModel.SelectedChartExternalActions.QueryRelatedDocumentsAsync,
                viewModel.SelectedChartExternalActions.OpenRelatedDocument,
                viewModel.SelectedChartExternalActions.CreateResolutionInput,
                viewModel.SelectedChartExternalActions.ResolveConfiguredActions,
                viewModel.SelectedChartExternalActions.ExecuteConfiguredAction));
    }
}

/// <summary>
/// Narrows selected-chart mutation requests to the existing mutation workflow owner.
/// </summary>
internal sealed class MainWindowSelectedChartMutationTerminal
{
    private readonly Func<SelectedChartDeleteRequest, Task<SelectedChartMutationResult>> delete;
    private readonly Func<SelectedInvalidExtensionRenameRequest, Task<SelectedChartMutationResult>> renameInvalidExtensions;
    private readonly Func<SelectedChartMoveRequest, Task<SelectedChartMutationResult>> move;
    private readonly Func<SelectedChartEncodingRequest, SelectedChartMutationResult> applyEncoding;

    internal MainWindowSelectedChartMutationTerminal(
        Func<SelectedChartDeleteRequest, Task<SelectedChartMutationResult>> delete,
        Func<SelectedInvalidExtensionRenameRequest, Task<SelectedChartMutationResult>> renameInvalidExtensions,
        Func<SelectedChartMoveRequest, Task<SelectedChartMutationResult>> move,
        Func<SelectedChartEncodingRequest, SelectedChartMutationResult> applyEncoding)
    {
        this.delete = delete ?? throw new ArgumentNullException(nameof(delete));
        this.renameInvalidExtensions = renameInvalidExtensions ?? throw new ArgumentNullException(nameof(renameInvalidExtensions));
        this.move = move ?? throw new ArgumentNullException(nameof(move));
        this.applyEncoding = applyEncoding ?? throw new ArgumentNullException(nameof(applyEncoding));
    }

    internal Task<SelectedChartMutationResult> DeleteAsync(SelectedChartDeleteRequest request)
        => delete(request ?? throw new ArgumentNullException(nameof(request)));

    internal Task<SelectedChartMutationResult> RenameInvalidExtensionsAsync(SelectedInvalidExtensionRenameRequest request)
        => renameInvalidExtensions(request ?? throw new ArgumentNullException(nameof(request)));

    internal Task<SelectedChartMutationResult> MoveAsync(SelectedChartMoveRequest request)
        => move(request ?? throw new ArgumentNullException(nameof(request)));

    internal SelectedChartMutationResult ApplyEncoding(SelectedChartEncodingRequest request)
        => applyEncoding(request ?? throw new ArgumentNullException(nameof(request)));
}

/// <summary>
/// Narrows selected-chart resource-health requests to the existing workflow owner.
/// </summary>
internal sealed class MainWindowSelectedChartResourceHealthTerminal
{
    private readonly Func<ChartResourceHealthRequest, Task<SelectedChartResourceHealthWorkflowResult>> rescan;
    private readonly Func<ChartResourceHealthRequest, bool, SelectedChartResourceHealthWorkflowResult> setWarningsIgnored;

    internal MainWindowSelectedChartResourceHealthTerminal(
        Func<ChartResourceHealthRequest, Task<SelectedChartResourceHealthWorkflowResult>> rescan,
        Func<ChartResourceHealthRequest, bool, SelectedChartResourceHealthWorkflowResult> setWarningsIgnored)
    {
        this.rescan = rescan ?? throw new ArgumentNullException(nameof(rescan));
        this.setWarningsIgnored = setWarningsIgnored ?? throw new ArgumentNullException(nameof(setWarningsIgnored));
    }

    internal Task<SelectedChartResourceHealthWorkflowResult> RescanAsync(ChartResourceHealthRequest request)
        => rescan(request ?? throw new ArgumentNullException(nameof(request)));

    internal SelectedChartResourceHealthWorkflowResult SetWarningsIgnored(
        ChartResourceHealthRequest request,
        bool unset = false)
        => setWarningsIgnored(request ?? throw new ArgumentNullException(nameof(request)), unset);
}

/// <summary>
/// Narrows selected-chart audio conversion requests to the existing workflow owner.
/// </summary>
internal sealed class MainWindowSelectedChartAudioConversionTerminal
{
    private readonly Func<SelectedChartAudioConversionRequest, CancellationToken, Task<SelectedChartAudioConversionResult>> run;

    internal MainWindowSelectedChartAudioConversionTerminal(
        Func<SelectedChartAudioConversionRequest, CancellationToken, Task<SelectedChartAudioConversionResult>> run)
    {
        this.run = run ?? throw new ArgumentNullException(nameof(run));
    }

    internal Task<SelectedChartAudioConversionResult> RunAsync(
        SelectedChartAudioConversionRequest request,
        CancellationToken cancellationToken = default)
        => run(request ?? throw new ArgumentNullException(nameof(request)), cancellationToken);
}

/// <summary>
/// Narrows chart-info parse-failure removal requests to the existing workflow owner.
/// </summary>
internal sealed class MainWindowChartInfoParseFailureRemovalTerminal
{
    private readonly Func<ChartInfoParseFailureRemovalRequest, ChartInfoParseFailureRemovalOperation> beginRemove;

    internal MainWindowChartInfoParseFailureRemovalTerminal(
        Func<ChartInfoParseFailureRemovalRequest, ChartInfoParseFailureRemovalOperation> beginRemove)
    {
        this.beginRemove = beginRemove ?? throw new ArgumentNullException(nameof(beginRemove));
    }

    internal ChartInfoParseFailureRemovalOperation BeginRemove(ChartInfoParseFailureRemovalRequest request)
        => beginRemove(request ?? throw new ArgumentNullException(nameof(request)));
}

/// <summary>
/// Narrows ranking-cache context-menu state and request operations to the existing owner.
/// </summary>
internal sealed class MainWindowRankingCacheTerminal
{
    private readonly Func<IEnumerable<ChartOperationTarget>, bool> hasRankingTarget;
    private readonly Func<IEnumerable<ChartOperationTarget>, bool> canRequestRanking;
    private readonly Func<IEnumerable<ChartOperationTarget>, bool> request;

    internal MainWindowRankingCacheTerminal(
        Func<IEnumerable<ChartOperationTarget>, bool> hasRankingTarget,
        Func<IEnumerable<ChartOperationTarget>, bool> canRequestRanking,
        Func<IEnumerable<ChartOperationTarget>, bool> request)
    {
        this.hasRankingTarget = hasRankingTarget ?? throw new ArgumentNullException(nameof(hasRankingTarget));
        this.canRequestRanking = canRequestRanking ?? throw new ArgumentNullException(nameof(canRequestRanking));
        this.request = request ?? throw new ArgumentNullException(nameof(request));
    }

    internal bool HasRankingTarget(IEnumerable<ChartOperationTarget> targets)
        => hasRankingTarget(targets ?? throw new ArgumentNullException(nameof(targets)));

    internal bool CanRequestRanking(IEnumerable<ChartOperationTarget> targets)
        => canRequestRanking(targets ?? throw new ArgumentNullException(nameof(targets)));

    internal bool Request(IEnumerable<ChartOperationTarget> targets)
        => request(targets ?? throw new ArgumentNullException(nameof(targets)));
}

/// <summary>
/// Narrows score-viewer availability and registration operations to the existing owner.
/// </summary>
internal sealed class MainWindowScoreViewerTerminal
{
    private readonly Func<IEnumerable<ChartOperationTarget>, bool> hasScoreViewerTarget;
    private readonly Func<IEnumerable<ChartOperationTarget>, bool> canRegisterScoreViewer;
    private readonly Func<IReadOnlyList<ChartOperationTarget>, string, Task<ScoreViewerRegistrationResult>> runChartTargets;
    private readonly Func<IReadOnlyList<ScoreViewerTarget>, bool, string, Task<ScoreViewerRegistrationResult>> runScoreViewerTargets;

    internal MainWindowScoreViewerTerminal(
        Func<IEnumerable<ChartOperationTarget>, bool> hasScoreViewerTarget,
        Func<IEnumerable<ChartOperationTarget>, bool> canRegisterScoreViewer,
        Func<IReadOnlyList<ChartOperationTarget>, string, Task<ScoreViewerRegistrationResult>> runChartTargets,
        Func<IReadOnlyList<ScoreViewerTarget>, bool, string, Task<ScoreViewerRegistrationResult>> runScoreViewerTargets)
    {
        this.hasScoreViewerTarget = hasScoreViewerTarget ?? throw new ArgumentNullException(nameof(hasScoreViewerTarget));
        this.canRegisterScoreViewer = canRegisterScoreViewer ?? throw new ArgumentNullException(nameof(canRegisterScoreViewer));
        this.runChartTargets = runChartTargets ?? throw new ArgumentNullException(nameof(runChartTargets));
        this.runScoreViewerTargets = runScoreViewerTargets ?? throw new ArgumentNullException(nameof(runScoreViewerTargets));
    }

    internal bool HasScoreViewerTarget(IEnumerable<ChartOperationTarget> targets)
        => hasScoreViewerTarget(targets ?? throw new ArgumentNullException(nameof(targets)));

    internal bool CanRegisterScoreViewer(IEnumerable<ChartOperationTarget> targets)
        => canRegisterScoreViewer(targets ?? throw new ArgumentNullException(nameof(targets)));

    internal Task<ScoreViewerRegistrationResult> RunAsync(
        IReadOnlyList<ChartOperationTarget> targets,
        string logName)
        => runChartTargets(targets ?? throw new ArgumentNullException(nameof(targets)), logName);

    internal Task<ScoreViewerRegistrationResult> RunAsync(
        IReadOnlyList<ScoreViewerTarget> targets,
        bool openSingleViewerOnSuccess,
        string logName)
        => runScoreViewerTargets(
            targets ?? throw new ArgumentNullException(nameof(targets)),
            openSingleViewerOnSuccess,
            logName);
}

/// <summary>
/// Narrows selected-chart external actions and related-document queries to the existing owner.
/// </summary>
internal sealed class MainWindowSelectedChartExternalActionsTerminal
{
    private readonly Func<ChartOperationTarget, SelectedChartExternalActionKind, bool> canExecute;
    private readonly Action<ChartOperationTarget, SelectedChartExternalActionKind> execute;
    private readonly Func<ChartOperationTarget, bool> canQueryRelatedDocuments;
    private readonly Func<ChartOperationTarget, CancellationToken, Task<RelatedDocumentQueryReceipt>> queryRelatedDocuments;
    private readonly Action<string> openRelatedDocument;
    private readonly Func<ChartOperationTarget, RightClickActionResolutionInput> createResolutionInput;
    private readonly Func<RightClickActionResolutionInput, RightClickActionResolution> resolveConfiguredActions;
    private readonly Func<RightClickActionResolutionInput, ConfiguredExternalActionKind, string, ExternalConfiguredActionResult> executeConfiguredAction;

    internal MainWindowSelectedChartExternalActionsTerminal(
        Func<ChartOperationTarget, SelectedChartExternalActionKind, bool> canExecute,
        Action<ChartOperationTarget, SelectedChartExternalActionKind> execute,
        Func<ChartOperationTarget, bool> canQueryRelatedDocuments,
        Func<ChartOperationTarget, CancellationToken, Task<RelatedDocumentQueryReceipt>> queryRelatedDocuments,
        Action<string> openRelatedDocument,
        Func<ChartOperationTarget, RightClickActionResolutionInput> createResolutionInput,
        Func<RightClickActionResolutionInput, RightClickActionResolution> resolveConfiguredActions,
        Func<RightClickActionResolutionInput, ConfiguredExternalActionKind, string, ExternalConfiguredActionResult> executeConfiguredAction)
    {
        this.canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canQueryRelatedDocuments = canQueryRelatedDocuments ?? throw new ArgumentNullException(nameof(canQueryRelatedDocuments));
        this.queryRelatedDocuments = queryRelatedDocuments ?? throw new ArgumentNullException(nameof(queryRelatedDocuments));
        this.openRelatedDocument = openRelatedDocument ?? throw new ArgumentNullException(nameof(openRelatedDocument));
        this.createResolutionInput = createResolutionInput ?? throw new ArgumentNullException(nameof(createResolutionInput));
        this.resolveConfiguredActions = resolveConfiguredActions ?? throw new ArgumentNullException(nameof(resolveConfiguredActions));
        this.executeConfiguredAction = executeConfiguredAction ?? throw new ArgumentNullException(nameof(executeConfiguredAction));
    }

    internal bool CanExecute(ChartOperationTarget target, SelectedChartExternalActionKind action)
        => canExecute(target, action);

    internal void Execute(ChartOperationTarget target, SelectedChartExternalActionKind action)
        => execute(target, action);

    internal bool CanQueryRelatedDocuments(ChartOperationTarget target)
        => canQueryRelatedDocuments(target);

    internal Task<RelatedDocumentQueryReceipt> QueryRelatedDocumentsAsync(
        ChartOperationTarget target,
        CancellationToken cancellationToken)
        => queryRelatedDocuments(target, cancellationToken);

    internal void OpenRelatedDocument(string path)
        => openRelatedDocument(path);

    /// <summary>exact row target を immutable resolver input へ変換します。</summary>
    internal bool TryCreateResolutionInput(
        ChartOperationTarget target,
        out RightClickActionResolutionInput input)
    {
        input = createResolutionInput(target);
        return input != null;
    }

    /// <summary>menu-open 時点の settings と input から action 候補を取得します。</summary>
    internal RightClickActionResolution ResolveConfiguredActions(RightClickActionResolutionInput input)
        => resolveConfiguredActions(input);

    /// <summary>click 時点で action ID を再解決し、typed terminal result を返します。</summary>
    internal ExternalConfiguredActionResult ExecuteConfiguredAction(
        RightClickActionResolutionInput input,
        ConfiguredExternalActionKind actionKind,
        string actionId)
        => executeConfiguredAction(input, actionKind, actionId);
}
