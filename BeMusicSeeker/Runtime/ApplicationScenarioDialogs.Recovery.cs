#nullable enable
using System;
using System.Text;
using System.Text.RegularExpressions;
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
    /// <remarks>
    /// 実modalの帰還と実handlerの終端は別の事象です。decoratorはmodalが閉じた時点で同期の呼出し元へ帰還し、
    /// 返すTaskだけが同じ実handlerの終端・入力の消費・元の例外を表します。固定入力はhandlerのfinallyまで保持します。
    /// modalの帰還を復元の成功として扱いません。
    /// </remarks>
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

    /// <summary>
    /// 複合書式の固定部分すべてに、先頭から末尾まで全体一致するかを返します。
    /// 書式が <c>{0}</c> で始まる場合でも、固定部分を無視した前方一致で無関係な確認を受理しないために使います。
    /// <c>{n}</c>（書式指定を含む）は改行を含む任意の文字列、<c>{{</c> と <c>}}</c> は波括弧そのものとして扱います。
    /// </summary>
    private static bool MatchesFormatExactly(string format, string text)
    {
        var pattern = new StringBuilder(@"\A");
        int last = 0;
        foreach (Match token in Regex.Matches(format, @"\{\{|\}\}|\{[^{}]*\}"))
        {
            pattern.Append(Regex.Escape(format.Substring(last, token.Index - last)));
            pattern.Append(token.Value switch
            {
                "{{" => Regex.Escape("{"),
                "}}" => Regex.Escape("}"),
                _ => @"[\s\S]*",
            });
            last = token.Index + token.Length;
        }
        pattern.Append(Regex.Escape(format.Substring(last))).Append(@"\z");
        return Regex.IsMatch(text, pattern.ToString(), RegexOptions.CultureInvariant);
    }

    private Task<UiDialogResult> ConfirmRecoveryAsync(UiConfirmationRequest request, CancellationToken cancellation)
    {
        if (activeSection?.Command.Action == ScenarioSettingsSectionAction.DetailsRestart
            && environmentConfirmation is { } environment
            && request.MessageBoxText == Resources.Confirm_RestartForOperationModeChange)
        {
            environment.Reached.TrySetResult();
            return environment.Answer.Task;
        }
        if (activeSection?.Command.Action == ScenarioSettingsSectionAction.OutputCommit
            && MatchesFormatExactly(Resources.Confirm_CustomFolderOutputChangesFormat, request.MessageBoxText))
        { return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)); }
        if (activeSection?.Command.Action == ScenarioSettingsSectionAction.InstallHistorySchema
            && request.MessageBoxText.StartsWith(Resources.Msg_confirm_lr2_play_history_schema_install_or_repair, StringComparison.Ordinal))
        { return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)); }
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
        if (activeSection?.Command.Action == ScenarioSettingsSectionAction.InstallHistorySchema
            && request.MessageBoxText == Resources.Msg_success_lr2_play_history_schema_install_or_repair)
        { return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)); }
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
