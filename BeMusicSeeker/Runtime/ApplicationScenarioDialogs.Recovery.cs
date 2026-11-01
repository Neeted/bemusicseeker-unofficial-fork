#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Runtime;

internal sealed partial class ApplicationScenarioDialogs
{
    private SchemaInput? schema;
    private SqlInput? activeSql;
    private bool removeTable;

    /// <summary>次の実設定画面のSQL操作だけを宣言します。実handler終端のTaskを返します。</summary>
    internal Task ArmPlaylistSql(string path, bool restore)
    {
        var input = new SqlInput(path, restore);
        Arm(input);
        return input.Completion.Task;
    }

    /// <summary>次の表削除固有の確認へ一回だけ肯定入力を用意します。</summary>
    internal void ArmTableRemoval()
    {
        if (removeTable) { throw new InvalidOperationException("Table removal is already armed."); }
        removeTable = true;
    }

    /// <summary>起動より先に実schema確認への固定入力を用意します。確認自体は呼びません。</summary>
    internal void ArmSchemaConfirmation()
    {
        if (schema is not null) { throw new InvalidOperationException("Schema input is already armed."); }
        schema = new();
    }

    /// <summary>実PrepareSchemaのConfirm到達だけを待ち、起動成功を補いません。</summary>
    internal Task WaitForSchemaConfirmationAsync(CancellationToken cancellation)
        => (schema ?? throw new InvalidOperationException("Schema input is not armed.")).Reached.Task.WaitAsync(cancellation);

    /// <summary>ACK前に到達と未回答を確認します。回答Taskの完了はACK送信後に分離します。</summary>
    internal Action PrepareSchemaAnswer(bool approve)
    {
        SchemaInput input = schema ?? throw new InvalidOperationException("Schema input is not armed.");
        if (!input.Reached.Task.IsCompletedSuccessfully || Interlocked.Exchange(ref input.AnswerReserved, 1) != 0)
        { throw new InvalidOperationException("Schema confirmation is not awaiting its first answer."); }
        return () => input.Answer.SetResult(UiDialogResult.FromMessageBoxResult(approve ? MessageBoxResult.OK : MessageBoxResult.Cancel));
    }

    private Task<UiDialogResult> ConfirmRecoveryAsync(UiConfirmationRequest request, CancellationToken cancellation)
    {
        if (removeTable && request.MessageBoxText == Resources.Msg_remove_playlist)
        { removeTable = false; return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)); }
        if (activeSql is { Restore: true, ConfirmationConsumed: false } sql
            && request.MessageBoxText.StartsWith(Resources.Confirm_RestorePlaylistBackup, StringComparison.Ordinal))
        { sql.ConfirmationConsumed = true; return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)); }
        if (schema is { } input && request.Caption == Resources.AppSchemaRepairWarningTitle
            && !input.Reached.Task.IsCompleted)
        {
            input.Reached.SetResult();
            return input.Answer.Task;
        }
        return inner.ConfirmAsync(request, cancellation);
    }

    private Task<UiDialogResult> ShowRecoveryMessageAsync(UiMessageRequest request, CancellationToken cancellation)
    {
        if (activeSql is { Restore: true, PathConsumed: true, RestoreSuccessConsumed: false } restored
            && request.MessageBoxText == Resources.Msg_success_playlist_restore && request.Caption == Resources.Success)
        { restored.RestoreSuccessConsumed = true; return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)); }
        if (activeSql is { Restore: true, PathConsumed: true, RestoreSuccessConsumed: true, ExitNoticeConsumed: false } restore
            && request.MessageBoxText == Resources.Msg_ApplicationWillExit)
        { restore.ExitNoticeConsumed = true; return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)); }
        if (activeSql is { Restore: false, PathConsumed: true } && request.MessageBoxText == Resources.Msg_success_playlist_backup)
        { return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)); }
        return inner.ShowMessageAsync(request, cancellation);
    }

    private sealed record SqlInput(string Path, bool Restore)
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ConfirmationConsumed { get; set; }
        internal bool PathConsumed { get; set; }
        internal bool RestoreSuccessConsumed { get; set; }
        internal bool ExitNoticeConsumed { get; set; }
    }

    private sealed class SchemaInput
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<UiDialogResult> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int AnswerReserved;
    }
}
