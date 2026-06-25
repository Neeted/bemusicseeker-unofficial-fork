using System;
using Parago.Windows;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// file picker route の結果を表します。詳細な選択値は Unit 5 の picker 統合で追加します。
/// </summary>
internal sealed class UiFilePickerResult
{
    /// <summary>
    /// file picker route の結果を初期化します。
    /// </summary>
    /// <param name="status">picker 表示と選択の結果。</param>
    internal UiFilePickerResult(UiDialogStatus status)
    {
        Status = status;
    }

    /// <summary>
    /// picker 表示と選択の結果です。
    /// </summary>
    internal UiDialogStatus Status { get; }
}

/// <summary>
/// folder picker route の結果を表します。詳細な選択値は Unit 5 の picker 統合で追加します。
/// </summary>
internal sealed class UiFolderPickerResult
{
    /// <summary>
    /// folder picker route の結果を初期化します。
    /// </summary>
    /// <param name="status">picker 表示と選択の結果。</param>
    internal UiFolderPickerResult(UiDialogStatus status)
    {
        Status = status;
    }

    /// <summary>
    /// picker 表示と選択の結果です。
    /// </summary>
    internal UiDialogStatus Status { get; }
}

/// <summary>
/// save file picker route の結果を表します。詳細な選択値は Unit 5 の picker 統合で追加します。
/// </summary>
internal sealed class UiSaveFilePickerResult
{
    /// <summary>
    /// save file picker route の結果を初期化します。
    /// </summary>
    /// <param name="status">picker 表示と選択の結果。</param>
    internal UiSaveFilePickerResult(UiDialogStatus status)
    {
        Status = status;
    }

    /// <summary>
    /// picker 表示と選択の結果です。
    /// </summary>
    internal UiDialogStatus Status { get; }
}

/// <summary>
/// progress operation route の結果を表します。詳細な進捗結果は Unit 4 の progress 統合で追加します。
/// </summary>
internal sealed class UiProgressResult
{
    /// <summary>
    /// progress operation route の結果を初期化します。
    /// </summary>
    /// <param name="status">progress 表示と operation 実行の結果。</param>
    internal UiProgressResult(UiDialogStatus status, object result = null, Exception error = null)
    {
        Status = status;
        Result = result;
        Error = error;
    }

    /// <summary>
    /// progress 表示と operation 実行の結果です。
    /// </summary>
    internal UiDialogStatus Status { get; }

    internal object Result { get; }

    internal Exception Error { get; }
}

/// <summary>
/// progress operation から UI へ進捗を報告する context です。Unit 4 で legacy progress static state の置換先にします。
/// </summary>
internal sealed class UiProgressContext
{
    private readonly ProgressDialogContext innerContext;

    internal UiProgressContext(ProgressDialogContext innerContext)
    {
        this.innerContext = innerContext ?? throw new ArgumentNullException(nameof(innerContext));
    }

    internal bool CheckCancellationPending()
    {
        return innerContext.CheckCancellationPending();
    }

    internal void ThrowIfCancellationPending()
    {
        innerContext.ThrowIfCancellationPending();
    }

    internal void ReportWithCancellationCheck(int percentProgress, string format, params object[] arg)
    {
        innerContext.ReportWithCancellationCheck(percentProgress, format, arg);
    }
}
