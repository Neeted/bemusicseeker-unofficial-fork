using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>
/// コンパイル済みの譜面メニューから、譜面操作と閲覧の担当へ接続します。
/// </summary>
internal sealed class MainWindowSelectedChartContextMenuTerminals
{
    internal MainWindowSelectedChartContextMenuTerminals(
        MainWindowSelectedChartMutationTerminal selectedChartMutation,
        MainWindowSelectedChartResourceHealthTerminal selectedChartResourceHealth,
        MainWindowSelectedChartAudioConversionTerminal selectedChartAudioConversion,
        MainWindowChartInfoParseFailureRemovalTerminal chartInfoParseFailureRemoval,
        MainWindowScoreViewerTerminal scoreViewer,
        MainWindowSelectedChartExternalActionsTerminal selectedChartExternalActions)
    {
        SelectedChartMutation = selectedChartMutation ?? throw new ArgumentNullException(nameof(selectedChartMutation));
        SelectedChartResourceHealth = selectedChartResourceHealth ?? throw new ArgumentNullException(nameof(selectedChartResourceHealth));
        SelectedChartAudioConversion = selectedChartAudioConversion ?? throw new ArgumentNullException(nameof(selectedChartAudioConversion));
        ChartInfoParseFailureRemoval = chartInfoParseFailureRemoval ?? throw new ArgumentNullException(nameof(chartInfoParseFailureRemoval));
        ScoreViewer = scoreViewer ?? throw new ArgumentNullException(nameof(scoreViewer));
        SelectedChartExternalActions = selectedChartExternalActions ?? throw new ArgumentNullException(nameof(selectedChartExternalActions));
    }

    internal MainWindowSelectedChartMutationTerminal SelectedChartMutation { get; }

    internal MainWindowSelectedChartResourceHealthTerminal SelectedChartResourceHealth { get; }

    internal MainWindowSelectedChartAudioConversionTerminal SelectedChartAudioConversion { get; }

    internal MainWindowChartInfoParseFailureRemovalTerminal ChartInfoParseFailureRemoval { get; }

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
                viewModel.SelectedChartMutations.ApplyEncodingAsync),
            new MainWindowSelectedChartResourceHealthTerminal(
                viewModel.SelectedChartResourceHealth.RescanAsync,
                viewModel.SelectedChartResourceHealth.SetWarningsIgnored),
            new MainWindowSelectedChartAudioConversionTerminal(
                viewModel.SelectedChartAudioConversion.RunAsync),
            new MainWindowChartInfoParseFailureRemovalTerminal(
                viewModel.ChartInfoParseFailureRemoval.BeginRemove),
            new MainWindowScoreViewerTerminal(
                viewModel.ScoreViewerRegistration.RunAsync),
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
    private readonly Func<SelectedChartEncodingRequest, Task<SelectedChartMutationResult>> applyEncoding;

    internal MainWindowSelectedChartMutationTerminal(
        Func<SelectedChartDeleteRequest, Task<SelectedChartMutationResult>> delete,
        Func<SelectedInvalidExtensionRenameRequest, Task<SelectedChartMutationResult>> renameInvalidExtensions,
        Func<SelectedChartMoveRequest, Task<SelectedChartMutationResult>> move,
        Func<SelectedChartEncodingRequest, Task<SelectedChartMutationResult>> applyEncoding)
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

    /// <summary>文字コードの保存と反映通知の終端を呼出し元へ返します。</summary>
    internal Task<SelectedChartMutationResult> ApplyEncodingAsync(SelectedChartEncodingRequest request)
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
/// 譜面ビューアの項目の判定を本番の実装で持ち、判定後の処理だけを差し替え可能にして担当へ接続します。
/// 通常の譜面一覧、未所持のプレイリスト項目、プレイログの3つのメニューが、表示時とクリック時の両方で同じ判定を使います。
/// </summary>
internal sealed class MainWindowScoreViewerTerminal
{
    private readonly Func<ScoreViewerMenuDecision, string, Task<ScoreViewerRegistrationResult>> run;

    /// <summary>判定後の処理を接続して初期化します。</summary>
    /// <param name="run">非表示でない判定と入口名を受けて、開く・登録する処理を行う実行部分。</param>
    internal MainWindowScoreViewerTerminal(
        Func<ScoreViewerMenuDecision, string, Task<ScoreViewerRegistrationResult>> run)
    {
        this.run = run ?? throw new ArgumentNullException(nameof(run));
    }

    /// <summary>
    /// メニューを開くときの項目の扱いを返します。所持BMSを1件見つけた時点で打ち切るので、UIスレッドで全件のファイルを確認しません。
    /// 見出しは <see cref="ScoreViewerMenuMode.Open"/> が「譜面ビューアで開く」、<see cref="ScoreViewerMenuMode.Register"/> が「譜面ビューアに登録する」で、
    /// <see cref="ScoreViewerMenuMode.Hidden"/> は項目を表示しません。
    /// </summary>
    /// <param name="candidates">選択した行ごとの候補。</param>
    internal ScoreViewerMenuMode ResolveMenuMode(IReadOnlyList<ScoreViewerCandidate> candidates)
        => ScoreViewerMenuDecision.ResolveMode(candidates ?? throw new ArgumentNullException(nameof(candidates)));

    /// <summary>
    /// クリック時の候補から判定し直して処理します。判定が非表示なら通知もせず何もしません。
    /// 全件のファイルの実在確認はUIスレッドの外で行います。
    /// </summary>
    /// <param name="candidates">クリック時点の選択した行ごとの候補。</param>
    /// <param name="logName">失敗の記録へ添える入口名。</param>
    internal async Task<ScoreViewerRegistrationResult> RunAsync(
        IReadOnlyList<ScoreViewerCandidate> candidates,
        string logName)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ScoreViewerMenuDecision decision = await Task.Run(() => ScoreViewerMenuDecision.Resolve(candidates));
        return await RunAsync(decision, logName);
    }

    /// <summary>判定済みの結果を処理します。非表示の判定なら何もしません。</summary>
    internal Task<ScoreViewerRegistrationResult> RunAsync(ScoreViewerMenuDecision decision, string logName)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.Mode == ScoreViewerMenuMode.Hidden
            ? Task.FromResult<ScoreViewerRegistrationResult>(null)
            : run(decision, logName);
    }
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
