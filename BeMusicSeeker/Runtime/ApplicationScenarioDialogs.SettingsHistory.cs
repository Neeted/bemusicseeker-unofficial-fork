#nullable enable
using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Runtime;

internal sealed partial class ApplicationScenarioDialogs
{
    private SchemaInput? environmentConfirmation;
    private SectionInput? activeSection;

    /// <summary>次の実設定Windowに節固有の一入力を渡し、その入力Task終端を返します。</summary>
    internal Task<ScenarioSettingsSectionObservation> ArmSettingsSection(ScenarioSettingsSectionCommand command)
    {
        var input = new SectionInput(command);
        if (command.Action == ScenarioSettingsSectionAction.DetailsRestart)
        {
            if (environmentConfirmation is not null) { throw new InvalidOperationException("Environment restart is already armed."); }
            environmentConfirmation = new();
        }
        Arm(input);
        return input.Completion.Task;
    }

    /// <summary>実環境確認の到達を待ち、操作成功は生成しません。</summary>
    internal Task WaitForEnvironmentConfirmationAsync(CancellationToken cancellation)
        => (environmentConfirmation ?? throw new InvalidOperationException("Environment restart is not armed.")).Reached.Task.WaitAsync(cancellation);

    /// <summary>実確認への回答をACK送信後まで留保します。</summary>
    internal Action PrepareEnvironmentAnswer()
    {
        SchemaInput input = environmentConfirmation ?? throw new InvalidOperationException("Environment restart is not armed.");
        if (!input.Reached.Task.IsCompletedSuccessfully || Interlocked.Exchange(ref input.AnswerReserved, 1) != 0)
        { throw new InvalidOperationException("Environment confirmation is not awaiting its first answer."); }
        return () => input.Answer.SetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
    }

    private async Task ApplySectionAsync(SettingsWindow window, SectionInput input)
    {
        try
        {
            activeSection = input;
            SettingsDialogViewModel model = window.DataContext as SettingsDialogViewModel ?? throw new InvalidOperationException("Settings model is unavailable.");
            if (!model.IsSettingsOperationAvailable) { throw new InvalidOperationException("Settings operation is busy."); }
            ScenarioSettingsSectionCommand command = input.Command;
            switch (command.Action)
            {
                case ScenarioSettingsSectionAction.AddRootsCancel:
                case ScenarioSettingsSectionAction.AddRootsCommit:
                    await model.AddStandaloneBmsRootPathsAsync([command.Path]);
                    break;
                case ScenarioSettingsSectionAction.OutputCancel:
                case ScenarioSettingsSectionAction.OutputCommit:
                    model.LR2CustomFolderOutputDir = command.Path;
                    break;
                case ScenarioSettingsSectionAction.DetailsCancel:
                case ScenarioSettingsSectionAction.DetailsRestart:
                    await model.SelectLr2DetailPathAsync(nameof(model.LR2SongDBPath), command.Path);
                    await model.SelectLr2DetailPathAsync(nameof(model.LR2ConfigXmlPath), command.ConfigPath);
                    break;
            }
            var candidateRoots = model.AvailableBMSDirectories.ToImmutableArray();
            string candidateOutput = model.LR2CustomFolderOutputDir;
            string candidateSong = model.LR2SongDBPath;
            string candidateConfig = model.LR2ConfigXmlPath;
            switch (command.Action)
            {
                case ScenarioSettingsSectionAction.InstallHistorySchema: await model.InstallOrRepairLr2PlayHistorySchemaAsync(); break;
                case ScenarioSettingsSectionAction.BeatorajaRootCommit: await model.CommitBeatorajaRootSettingsAsync(command.Path); break;
                case ScenarioSettingsSectionAction.BeatorajaScoreCommit:
                case ScenarioSettingsSectionAction.Lr2ScoreCommit:
                    await ApplyScoreSelectionAsync(model, command);
                    break;
                case ScenarioSettingsSectionAction.AddRootsCancel:
                    if (!model.CanCancelBmsRoots) { throw new InvalidOperationException("BMS cancellation is unavailable."); }
                    model.CancelBmsRoots();
                    break;
                case ScenarioSettingsSectionAction.AddRootsCommit: await model.CommitBmsRootsAsync(); break;
                case ScenarioSettingsSectionAction.OutputCommit: await model.CommitOutputSettingsAsync("normal"); break;
                case ScenarioSettingsSectionAction.DetailsCancel:
                    if (!model.CanCancelEnvironment) { throw new InvalidOperationException("Environment cancellation is unavailable."); }
                    model.CancelEnvironment();
                    break;
                case ScenarioSettingsSectionAction.DetailsRestart:
                    if (!model.CanConfirmEnvironment) { throw new InvalidOperationException("Environment confirmation is unavailable."); }
                    await model.CommitEnvironmentAsync();
                    break;
            }
            if (command.Action != ScenarioSettingsSectionAction.DetailsRestart) { model.CloseCommand.Execute(); }
            var fresh = new Settings();
            fresh.Reload();
            input.Completion.SetResult(new(candidateRoots, candidateOutput, candidateSong, candidateConfig,
                model.AvailableBMSDirectories.ToImmutableArray(), model.LR2CustomFolderOutputDir, model.LR2SongDBPath,
                model.LR2ConfigXmlPath, model.CustomTableFontSize, fresh.StandaloneBmsRootPaths,
                fresh.LR2CustomFolderOutputBaseDir, fresh.LR2SongDBPath, fresh.LR2ConfigXmlPath));
        }
        catch (Exception failure) { input.Completion.TrySetException(failure); throw; }
        finally { activeSection = null; }
    }

    /// <summary>実Windowが各候補setterで開始する保存・再読込の所有Taskを順に待ちます。受付中の次入力と二重commitを避け、同値や未受付を成功に補いません。</summary>
    private static async Task ApplyScoreSelectionAsync(SettingsDialogViewModel model, ScenarioSettingsSectionCommand command)
    {
        bool useBeatoraja = command.Action == ScenarioSettingsSectionAction.BeatorajaScoreCommit;
        if (!model.IsSettingsOperationAvailable || !model.IsScoreSelectionEnabled || model.UseBeatorajaScoreDb == useBeatoraja)
        { throw new InvalidOperationException("A changed score provider input must be available."); }
        Task previous = model.SettingsOperationCompletionTask;
        model.UseBeatorajaScoreDb = useBeatoraja;
        Task providerOperation = model.SettingsOperationCompletionTask;
        if (providerOperation is null || ReferenceEquals(previous, providerOperation))
        { throw new InvalidOperationException("The score provider input did not start its window-owned operation."); }
        await providerOperation;
        if (!useBeatoraja) { return; }

        if (!model.IsSettingsOperationAvailable || !model.IsScoreSelectionEnabled || !model.UseBeatorajaScoreDb
            || model.BeatorajaPlayerId == command.Path)
        { throw new InvalidOperationException("A changed score player input must be available."); }
        previous = model.SettingsOperationCompletionTask;
        model.BeatorajaPlayerId = command.Path;
        Task playerOperation = model.SettingsOperationCompletionTask;
        if (playerOperation is null || ReferenceEquals(previous, playerOperation))
        { throw new InvalidOperationException("The score player input did not start its window-owned operation."); }
        await playerOperation;
    }

    private sealed record SectionInput(ScenarioSettingsSectionCommand Command)
    {
        internal TaskCompletionSource<ScenarioSettingsSectionObservation> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
